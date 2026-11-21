using Meterist.Core.Extraction;
using Meterist.Core.Models;
using Meterist.Core.Persistence;
using Meterist.Core.Vendors;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Meterist.Core.Tests.Extraction;

public class SpendExtractionServiceTests
{
    private static readonly DateRange Period = new(new DateOnly(2026, 7, 19), new DateOnly(2026, 7, 25));

    [Fact]
    public async Task ExtractAsync_SucceedingVendor_PersistsRawThenCanonicalAndReportsSucceeded()
    {
        var vendorId = Guid.NewGuid();
        var extractor = new FakeExtractor(vendorId, EmptyRawData(vendorId));
        var normalizedRecords = new List<DailySpendRecord>
        {
            new() { TenantId = "ecosync", VendorId = vendorId, Date = Period.Start },
        };
        var normalizer = new FakeNormalizer(vendorId, normalizedRecords);
        var rawRepo = new FakeRawExtractionRepository();
        var dailyRepo = new FakeDailySpendRepository();

        var service = new SpendExtractionService(
            [extractor], [normalizer], rawRepo, dailyRepo,
            new FakeVendorRateConfigRepository(), NullLogger<SpendExtractionService>.Instance);
        var results = await service.ExtractAsync(
            "ecosync", Period, cancellationToken: TestContext.Current.CancellationToken);

        var result = Assert.Single(results);
        Assert.Equal(VendorExtractionStatus.Succeeded, result.Status);
        Assert.Equal(1, result.RecordCount);
        Assert.True(rawRepo.WasCalled);
        Assert.True(dailyRepo.WasCalled);
    }

    [Fact]
    public async Task ExtractAsync_SucceedingVendor_DetailSummarizesWhatActuallyChanged()
    {
        var vendorId = Guid.NewGuid();
        var records = new List<DailySpendRecord>
        {
            new() { TenantId = "ecosync", VendorId = vendorId, Date = Period.Start },
            new() { TenantId = "ecosync", VendorId = vendorId, Date = Period.Start.AddDays(1) },
            new() { TenantId = "ecosync", VendorId = vendorId, Date = Period.Start.AddDays(2) },
        };
        var dailyRepo = new FakeDailySpendRepository(list =>
        [
            new DailySpendChange(list[0].Date, DailySpendChangeKind.Inserted, null, list[0], list[0]),
            new DailySpendChange(list[1].Date, DailySpendChangeKind.Unchanged, list[1], list[1], list[1]),
            new DailySpendChange(list[2].Date, DailySpendChangeKind.Unchanged, list[2], list[2], list[2]),
        ]);

        var service = new SpendExtractionService(
            [new FakeExtractor(vendorId, EmptyRawData(vendorId))], [new FakeNormalizer(vendorId, records)],
            new FakeRawExtractionRepository(), dailyRepo,
            new FakeVendorRateConfigRepository(), NullLogger<SpendExtractionService>.Instance);

        var result = Assert.Single(await service.ExtractAsync(
            "ecosync", Period, cancellationToken: TestContext.Current.CancellationToken));

        // The status line can now be checked against what really changed,
        // instead of "Succeeded, 3 records" being taken on trust.
        Assert.Equal("1 inserted, 2 unchanged", result.Detail);
        Assert.Equal(3, result.Changes!.Count);
    }

    [Fact]
    public async Task ExtractAsync_PositionalCallWithTheOriginalFourArguments_StillBindsAndWrites()
    {
        // Guards the signature: (tenantId, period, vendorFilter, cancellationToken) was the
        // shape before dryRun existed, and a positional caller must keep compiling and
        // behaving the same (a real run, not a dry one).
        var vendorId = Guid.NewGuid();
        var rawRepo = new FakeRawExtractionRepository();
        var dailyRepo = new FakeDailySpendRepository();
        var service = new SpendExtractionService(
            [new FakeExtractor(vendorId, EmptyRawData(vendorId))],
            [new FakeNormalizer(vendorId, [new() { TenantId = "ecosync", VendorId = vendorId, Date = Period.Start }])],
            rawRepo, dailyRepo, new FakeVendorRateConfigRepository(), NullLogger<SpendExtractionService>.Instance);

        var results = await service.ExtractAsync("ecosync", Period, null, TestContext.Current.CancellationToken);

        Assert.Equal(VendorExtractionStatus.Succeeded, Assert.Single(results).Status);
        Assert.True(rawRepo.WasCalled);
        Assert.True(dailyRepo.WasCalled);
        Assert.False(dailyRepo.PlanWasCalled);
    }

    [Fact]
    public async Task ExtractAsync_DryRun_WritesNothingAndReportsThePlannedChanges()
    {
        var vendorId = Guid.NewGuid();
        var records = new List<DailySpendRecord>
        {
            new() { TenantId = "ecosync", VendorId = vendorId, Date = Period.Start },
        };
        var rawRepo = new FakeRawExtractionRepository();
        var dailyRepo = new FakeDailySpendRepository();

        var service = new SpendExtractionService(
            [new FakeExtractor(vendorId, EmptyRawData(vendorId))], [new FakeNormalizer(vendorId, records)],
            rawRepo, dailyRepo,
            new FakeVendorRateConfigRepository(), NullLogger<SpendExtractionService>.Instance);

        var result = Assert.Single(await service.ExtractAsync(
            "ecosync", Period, dryRun: true, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(VendorExtractionStatus.Succeeded, result.Status);
        Assert.False(rawRepo.WasCalled, "a dry run must not persist raw data");
        Assert.False(dailyRepo.WasCalled, "a dry run must not upsert daily records");
        Assert.True(dailyRepo.PlanWasCalled);
        Assert.Equal("dry run: 1 inserted", result.Detail);
        Assert.Equal(DailySpendChangeKind.Inserted, Assert.Single(result.Changes!).Kind);
    }

    [Fact]
    public async Task ExtractAsync_WhenTheNeverLowerRuleKeepsAHigherFigure_LogsAWarningWithTheNumbers()
    {
        var vendorId = Guid.NewGuid();
        var date = Period.Start;
        var incoming = new DailySpendRecord { TenantId = "ecosync", VendorId = vendorId, Date = date, UsageOrOverage = 319.97m };
        var stored = new DailySpendRecord { TenantId = "ecosync", VendorId = vendorId, Date = date, UsageOrOverage = 337.43m };
        var dailyRepo = new FakeDailySpendRepository(_ =>
            [new DailySpendChange(date, DailySpendChangeKind.KeptHigherUsage, stored, stored, incoming)]);
        var logger = new CapturingLogger<SpendExtractionService>();

        var service = new SpendExtractionService(
            [new FakeExtractor(vendorId, EmptyRawData(vendorId))], [new FakeNormalizer(vendorId, [incoming])],
            new FakeRawExtractionRepository(), dailyRepo, new FakeVendorRateConfigRepository(), logger);

        var result = Assert.Single(await service.ExtractAsync(
            "ecosync", Period, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal("1 kept higher usage", result.Detail);
        var warning = Assert.Single(logger.Entries, e => e.Level == LogLevel.Warning);
        Assert.Contains("337.43", warning.Message);
        Assert.Contains("319.97", warning.Message);
        Assert.Contains(date.ToString("yyyy-MM-dd"), warning.Message);
    }

    [Fact]
    public void Summarize_OmitsZeroCountsAndHandlesNoDays()
    {
        var day = new DailySpendRecord { TenantId = "t", VendorId = Guid.NewGuid(), Date = Period.Start };

        Assert.Equal("no days", Array.Empty<DailySpendChange>().ToList().AsReadOnly().Summarize());
        Assert.Equal(
            "2 updated",
            new List<DailySpendChange>
            {
                new(day.Date, DailySpendChangeKind.Updated, day, day, day),
                new(day.Date, DailySpendChangeKind.Updated, day, day, day),
            }.AsReadOnly().Summarize());
    }

    [Fact]
    public async Task ExtractAsync_ExtractorThrowsNotImplemented_ReportsNotImplementedNotFailed()
    {
        var vendorId = Guid.NewGuid();
        var extractor = new ThrowingExtractor(vendorId, new NotImplementedException("nope"));
        var normalizer = new FakeNormalizer(vendorId, []);

        var service = new SpendExtractionService(
            [extractor], [normalizer], new FakeRawExtractionRepository(), new FakeDailySpendRepository(),
            new FakeVendorRateConfigRepository(), NullLogger<SpendExtractionService>.Instance);
        var results = await service.ExtractAsync(
            "ecosync", Period, cancellationToken: TestContext.Current.CancellationToken);

        var result = Assert.Single(results);
        Assert.Equal(VendorExtractionStatus.NotImplemented, result.Status);
    }

    [Fact]
    public async Task ExtractAsync_ExtractorThrowsOtherException_ReportsFailed()
    {
        var vendorId = Guid.NewGuid();
        var extractor = new ThrowingExtractor(vendorId, new InvalidOperationException("boom"));
        var normalizer = new FakeNormalizer(vendorId, []);

        var service = new SpendExtractionService(
            [extractor], [normalizer], new FakeRawExtractionRepository(), new FakeDailySpendRepository(),
            new FakeVendorRateConfigRepository(), NullLogger<SpendExtractionService>.Instance);
        var results = await service.ExtractAsync(
            "ecosync", Period, cancellationToken: TestContext.Current.CancellationToken);

        var result = Assert.Single(results);
        Assert.Equal(VendorExtractionStatus.Failed, result.Status);
        Assert.Equal("boom", result.Detail);
    }

    [Fact]
    public async Task ExtractAsync_NoNormalizerRegisteredForVendor_ReportsFailed()
    {
        var vendorId = Guid.NewGuid();
        var extractor = new FakeExtractor(vendorId, EmptyRawData(vendorId));

        var service = new SpendExtractionService(
            [extractor], [], new FakeRawExtractionRepository(), new FakeDailySpendRepository(),
            new FakeVendorRateConfigRepository(), NullLogger<SpendExtractionService>.Instance);
        var results = await service.ExtractAsync(
            "ecosync", Period, cancellationToken: TestContext.Current.CancellationToken);

        var result = Assert.Single(results);
        Assert.Equal(VendorExtractionStatus.Failed, result.Status);
    }

    [Fact]
    public async Task ExtractAsync_NotImplementedExtractor_ReportsNotImplemented_EvenWithNoNormalizerRegistered()
    {
        // Regression test: the normalizer lookup must not run before extraction is
        // attempted — otherwise every not-yet-built vendor (no normalizer exists
        // for it either) would misreport as "Failed: no normalizer" instead of
        // NotImplemented, masking the extractor's own, more specific signal.
        var vendorId = Guid.NewGuid();
        var extractor = new ThrowingExtractor(vendorId, new NotImplementedException("nope"));

        var service = new SpendExtractionService(
            [extractor], [], new FakeRawExtractionRepository(), new FakeDailySpendRepository(),
            new FakeVendorRateConfigRepository(), NullLogger<SpendExtractionService>.Instance);
        var results = await service.ExtractAsync(
            "ecosync", Period, cancellationToken: TestContext.Current.CancellationToken);

        var result = Assert.Single(results);
        Assert.Equal(VendorExtractionStatus.NotImplemented, result.Status);
    }

    [Fact]
    public async Task ExtractAsync_WithVendorFilter_OnlyRunsThatVendor()
    {
        var vendorA = Guid.NewGuid();
        var vendorB = Guid.NewGuid();

        var extractorA = new FakeExtractor(vendorA, EmptyRawData(vendorA));
        var extractorB = new FakeExtractor(vendorB, EmptyRawData(vendorB));
        var normalizerA = new FakeNormalizer(vendorA, []);
        var normalizerB = new FakeNormalizer(vendorB, []);

        var service = new SpendExtractionService(
            [extractorA, extractorB], [normalizerA, normalizerB],
            new FakeRawExtractionRepository(), new FakeDailySpendRepository(),
            new FakeVendorRateConfigRepository(), NullLogger<SpendExtractionService>.Instance);

        var results = await service.ExtractAsync(
            "ecosync", Period, vendorFilter: vendorA, cancellationToken: TestContext.Current.CancellationToken);

        var result = Assert.Single(results);
        Assert.Equal(vendorA, result.VendorId);
    }

    [Fact]
    public async Task ExtractAsync_ResolvesApplicableRates_AndPassesThemToNormalizer()
    {
        var vendorId = Guid.NewGuid();
        var extractor = new FakeExtractor(vendorId, EmptyRawData(vendorId));
        var normalizer = new FakeNormalizer(vendorId, []);
        var expectedRates = new List<VendorRateConfig>
        {
            new() { VendorId = vendorId, RateType = "per-seat", Rate = 30m, EffectiveFrom = Period.Start },
        };
        var rateConfigRepo = new FakeVendorRateConfigRepository(expectedRates);

        var service = new SpendExtractionService(
            [extractor], [normalizer], new FakeRawExtractionRepository(), new FakeDailySpendRepository(),
            rateConfigRepo, NullLogger<SpendExtractionService>.Instance);

        await service.ExtractAsync("ecosync", Period, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(Period, rateConfigRepo.ReceivedPeriod);
        Assert.Same(expectedRates, normalizer.ReceivedApplicableRates);
    }

    [Fact]
    public async Task ExtractAsync_PassesExtractorsRequiresMonotonicUsage_ToDailySpendRepository()
    {
        var vendorId = Guid.NewGuid();
        var extractor = new FakeExtractor(vendorId, EmptyRawData(vendorId)) { RequiresMonotonicUsage = true };
        var normalizer = new FakeNormalizer(vendorId, []);
        var dailyRepo = new FakeDailySpendRepository();

        var service = new SpendExtractionService(
            [extractor], [normalizer], new FakeRawExtractionRepository(), dailyRepo,
            new FakeVendorRateConfigRepository(), NullLogger<SpendExtractionService>.Instance);
        await service.ExtractAsync("ecosync", Period, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(true, dailyRepo.ReceivedRequiresMonotonicUsage);
    }

    private static RawVendorSpendData EmptyRawData(Guid vendorId) => new()
    {
        VendorId = vendorId,
        TenantId = "ecosync",
        Period = Period,
        RecordsByDate = new Dictionary<DateOnly, IReadOnlyList<IReadOnlyDictionary<string, object?>>>(),
    };

    private sealed class FakeExtractor(Guid vendorId, RawVendorSpendData dataToReturn) : IVendorSpendExtractor
    {
        public Guid VendorId => vendorId;

        public bool SupportsOverage => true;

        public bool SupportsPerUserBreakdown => true;

        public bool RequiresMonotonicUsage { get; init; }

        public Task<RawVendorSpendData> ExtractAsync(
            string tenantId, DateRange period, CancellationToken cancellationToken = default) =>
            Task.FromResult(dataToReturn);
    }

    private sealed class ThrowingExtractor(Guid vendorId, Exception exceptionToThrow) : IVendorSpendExtractor
    {
        public Guid VendorId => vendorId;

        public bool SupportsOverage => true;

        public bool SupportsPerUserBreakdown => true;

        public bool RequiresMonotonicUsage => false;

        public Task<RawVendorSpendData> ExtractAsync(
            string tenantId, DateRange period, CancellationToken cancellationToken = default) =>
            throw exceptionToThrow;
    }

    private sealed class FakeNormalizer(Guid vendorId, IReadOnlyList<DailySpendRecord> recordsToReturn)
        : IVendorSpendNormalizer
    {
        public Guid VendorId => vendorId;

        public IReadOnlyList<VendorRateConfig>? ReceivedApplicableRates { get; private set; }

        public IReadOnlyList<DailySpendRecord> Normalize(
            RawVendorSpendData rawData, IReadOnlyList<VendorRateConfig> applicableRates)
        {
            ReceivedApplicableRates = applicableRates;
            return recordsToReturn;
        }
    }

    private sealed class FakeVendorRateConfigRepository(IReadOnlyList<VendorRateConfig>? ratesToReturn = null)
        : IVendorRateConfigRepository
    {
        private readonly IReadOnlyList<VendorRateConfig> _ratesToReturn = ratesToReturn ?? [];

        public DateRange? ReceivedPeriod { get; private set; }

        public Task AddAsync(VendorRateConfig rate, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<int> CloseOpenEndedRateAsync(
            string? tenantId, Guid vendorId, string? modelOrSku, DateOnly newEffectiveFrom,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(0);

        public Task<IReadOnlyList<VendorRateConfig>> GetApplicableRatesAsync(
            string tenantId, Guid vendorId, DateRange period, CancellationToken cancellationToken = default)
        {
            ReceivedPeriod = period;
            return Task.FromResult(_ratesToReturn);
        }

        public Task<DateOnly?> FindNextEffectiveFromAsync(
            string? tenantId, Guid vendorId, string? modelOrSku, DateOnly afterEffectiveFrom,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<DateOnly?>(null);

        public Task<IReadOnlyList<VendorRateConfig>> FindOverlappingRatesAsync(
            string? tenantId, Guid vendorId, string? modelOrSku, DateOnly effectiveFrom, DateOnly? effectiveTo,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<VendorRateConfig>>([]);
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));
    }

    [Fact]
    public async Task ExtractAsync_VendorWithAUserNormalizer_StoresPerUserRowsAfterTheDailyTotals_AndSaysSoInTheDetail()
    {
        var vendorId = Guid.NewGuid();
        var userRepo = new FakeUserRepository();
        var userRows = new List<UserDailyUsageRecord>
        {
            new() { TenantId = "ecosync", VendorId = vendorId, Date = Period.Start, UserId = "u1" },
            new() { TenantId = "ecosync", VendorId = vendorId, Date = Period.Start, UserId = "u2" },
        };

        var service = new SpendExtractionService(
            [new FakeExtractor(vendorId, EmptyRawData(vendorId))],
            [new FakeNormalizer(vendorId, [new() { TenantId = "ecosync", VendorId = vendorId, Date = Period.Start }])],
            new FakeRawExtractionRepository(), new FakeDailySpendRepository(),
            new FakeVendorRateConfigRepository(), NullLogger<SpendExtractionService>.Instance,
            [new FakeUserNormalizer(vendorId, userRows)], userRepo);

        var result = Assert.Single(await service.ExtractAsync(
            "ecosync", Period, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(VendorExtractionStatus.Succeeded, result.Status);
        Assert.Equal(2, userRepo.Upserted.Count);
        Assert.Equal(0, userRepo.Planned);
        Assert.Equal(new UserUsageUpsertResult(2, 0, 0), result.UserUsage);
        Assert.Contains("per-user rows: 2 inserted", result.Detail);
    }

    [Fact]
    public async Task ExtractAsync_DryRun_PlansPerUserRowsAndWritesNone()
    {
        var vendorId = Guid.NewGuid();
        var userRepo = new FakeUserRepository();

        var service = new SpendExtractionService(
            [new FakeExtractor(vendorId, EmptyRawData(vendorId))],
            [new FakeNormalizer(vendorId, [new() { TenantId = "ecosync", VendorId = vendorId, Date = Period.Start }])],
            new FakeRawExtractionRepository(), new FakeDailySpendRepository(),
            new FakeVendorRateConfigRepository(), NullLogger<SpendExtractionService>.Instance,
            [new FakeUserNormalizer(vendorId, [new() { TenantId = "ecosync", VendorId = vendorId, Date = Period.Start, UserId = "u1" }])],
            userRepo);

        var result = Assert.Single(await service.ExtractAsync(
            "ecosync", Period, cancellationToken: TestContext.Current.CancellationToken, dryRun: true));

        Assert.Empty(userRepo.Upserted);
        Assert.Equal(1, userRepo.Planned);
        Assert.Contains("dry run:", result.Detail);
        Assert.Contains("per-user rows: 1 inserted", result.Detail);
    }

    [Fact]
    public async Task ExtractAsync_VendorWithNoUserNormalizer_LeavesPerUserStorageUntouched()
    {
        var vendorId = Guid.NewGuid();
        var userRepo = new FakeUserRepository();

        var service = new SpendExtractionService(
            [new FakeExtractor(vendorId, EmptyRawData(vendorId))],
            [new FakeNormalizer(vendorId, [new() { TenantId = "ecosync", VendorId = vendorId, Date = Period.Start }])],
            new FakeRawExtractionRepository(), new FakeDailySpendRepository(),
            new FakeVendorRateConfigRepository(), NullLogger<SpendExtractionService>.Instance,
            [new FakeUserNormalizer(Guid.NewGuid(), [])], userRepo);

        var result = Assert.Single(await service.ExtractAsync(
            "ecosync", Period, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(0, userRepo.Planned);
        Assert.Empty(userRepo.Upserted);
        Assert.Null(result.UserUsage);
        Assert.DoesNotContain("per-user", result.Detail);
    }

    [Fact]
    public async Task ExtractAsync_VendorWithABreakdownNormalizer_ReplacesTheBreakdownForEveryDayThePullCovered()
    {
        var vendorId = Guid.NewGuid();
        var breakdownRepo = new FakeBreakdownRepository();
        var row = new DailySpendBreakdownRecord
        {
            TenantId = "ecosync", VendorId = vendorId, Date = Period.Start, Category = "Seat", Amount = 5m,
        };

        var service = new SpendExtractionService(
            [new FakeExtractor(vendorId, EmptyRawData(vendorId))],
            [new FakeNormalizer(vendorId, [new() { TenantId = "ecosync", VendorId = vendorId, Date = Period.Start }])],
            new FakeRawExtractionRepository(), new FakeDailySpendRepository(),
            new FakeVendorRateConfigRepository(), NullLogger<SpendExtractionService>.Instance,
            breakdownNormalizers: [new FakeBreakdownNormalizer(vendorId, [row])], breakdownRepository: breakdownRepo);

        var result = Assert.Single(await service.ExtractAsync(
            "ecosync", Period, cancellationToken: TestContext.Current.CancellationToken));

        // Every day of the period is declared covered, not only the days that have a row: a day the vendor
        // now reports nothing for must have its old rows cleared.
        Assert.Equal(Period.EnumerateDays().ToList(), breakdownRepo.CoveredDays);
        Assert.Equal([row], breakdownRepo.Written);
        Assert.Equal(0, breakdownRepo.Planned);
        Assert.Equal(new BreakdownReplaceResult(1, 0, 0, 0), result.Breakdown);
        Assert.Contains("breakdown rows: 1 inserted", result.Detail);
    }

    [Fact]
    public async Task ExtractAsync_DryRun_PlansTheBreakdownAndWritesNone()
    {
        var vendorId = Guid.NewGuid();
        var breakdownRepo = new FakeBreakdownRepository();
        var row = new DailySpendBreakdownRecord
        {
            TenantId = "ecosync", VendorId = vendorId, Date = Period.Start, Category = "Tax", Amount = 1m, IsUsage = false,
        };

        var service = new SpendExtractionService(
            [new FakeExtractor(vendorId, EmptyRawData(vendorId))],
            [new FakeNormalizer(vendorId, [new() { TenantId = "ecosync", VendorId = vendorId, Date = Period.Start }])],
            new FakeRawExtractionRepository(), new FakeDailySpendRepository(),
            new FakeVendorRateConfigRepository(), NullLogger<SpendExtractionService>.Instance,
            breakdownNormalizers: [new FakeBreakdownNormalizer(vendorId, [row])], breakdownRepository: breakdownRepo);

        var result = Assert.Single(await service.ExtractAsync(
            "ecosync", Period, cancellationToken: TestContext.Current.CancellationToken, dryRun: true));

        Assert.Empty(breakdownRepo.Written);
        Assert.Equal(1, breakdownRepo.Planned);
        Assert.Contains("dry run:", result.Detail);
        Assert.Contains("breakdown rows: 1 inserted", result.Detail);
    }

    [Fact]
    public async Task ExtractAsync_VendorWithNoBreakdownNormalizer_LeavesTheBreakdownUntouched()
    {
        var vendorId = Guid.NewGuid();
        var breakdownRepo = new FakeBreakdownRepository();

        var service = new SpendExtractionService(
            [new FakeExtractor(vendorId, EmptyRawData(vendorId))],
            [new FakeNormalizer(vendorId, [new() { TenantId = "ecosync", VendorId = vendorId, Date = Period.Start }])],
            new FakeRawExtractionRepository(), new FakeDailySpendRepository(),
            new FakeVendorRateConfigRepository(), NullLogger<SpendExtractionService>.Instance,
            breakdownNormalizers: [new FakeBreakdownNormalizer(Guid.NewGuid(), [])], breakdownRepository: breakdownRepo);

        var result = Assert.Single(await service.ExtractAsync(
            "ecosync", Period, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Empty(breakdownRepo.Written);
        Assert.Equal(0, breakdownRepo.Planned);
        Assert.Null(result.Breakdown);
        Assert.DoesNotContain("breakdown", result.Detail);
    }

    private sealed class FakeBreakdownNormalizer(Guid vendorId, IReadOnlyList<DailySpendBreakdownRecord> rows) : IVendorSpendBreakdownNormalizer
    {
        public Guid VendorId => vendorId;

        public IReadOnlyList<DailySpendBreakdownRecord> Normalize(RawVendorSpendData rawData) => rows;
    }

    private sealed class FakeBreakdownRepository : IDailySpendBreakdownRepository
    {
        public List<DailySpendBreakdownRecord> Written { get; } = [];

        public List<DateOnly> CoveredDays { get; private set; } = [];

        public int Planned { get; private set; }

        public Task<BreakdownReplaceResult> ReplaceAsync(
            string tenantId, Guid vendorId, IReadOnlyCollection<DateOnly> coveredDays,
            IEnumerable<DailySpendBreakdownRecord> records, CancellationToken cancellationToken = default)
        {
            var list = records.ToList();
            Written.AddRange(list);
            CoveredDays = coveredDays.ToList();
            return Task.FromResult(new BreakdownReplaceResult(list.Count, 0, 0, 0));
        }

        public Task<BreakdownReplaceResult> PlanReplaceAsync(
            string tenantId, Guid vendorId, IReadOnlyCollection<DateOnly> coveredDays,
            IEnumerable<DailySpendBreakdownRecord> records, CancellationToken cancellationToken = default)
        {
            var count = records.Count();
            Planned += count;
            CoveredDays = coveredDays.ToList();
            return Task.FromResult(new BreakdownReplaceResult(count, 0, 0, 0));
        }

        public Task<IReadOnlyList<DailySpendBreakdownRecord>> GetAsync(
            string tenantId, Guid vendorId, DateRange range, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<DailySpendBreakdownRecord>>([]);
    }

    [Fact]
    public async Task ExtractAsync_WhenTheLastDayOfTheDailyWriteIsNotThereOnReadBack_TheVendorFailsNamingTheDate()
    {
        // The August symptom: success reported, the newest day absent. It must now be a failure with the date.
        var vendorId = Guid.NewGuid();
        var records = Enumerable.Range(0, 3)
            .Select(i => new DailySpendRecord { TenantId = "ecosync", VendorId = vendorId, Date = Period.Start.AddDays(i) })
            .ToList();
        var dailyRepo = new FakeDailySpendRepository();
        dailyRepo.LoseDays.Add(Period.Start.AddDays(2));

        var service = new SpendExtractionService(
            [new FakeExtractor(vendorId, EmptyRawData(vendorId))], [new FakeNormalizer(vendorId, records)],
            new FakeRawExtractionRepository(), dailyRepo,
            new FakeVendorRateConfigRepository(), NullLogger<SpendExtractionService>.Instance);

        var result = Assert.Single(await service.ExtractAsync(
            "ecosync", Period, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(VendorExtractionStatus.Failed, result.Status);
        Assert.Contains("daily spend records", result.Detail);
        Assert.Contains(Period.Start.AddDays(2).ToString("yyyy-MM-dd"), result.Detail);
        Assert.DoesNotContain(Period.Start.ToString("yyyy-MM-dd"), result.Detail);   // only the missing day is named
        Assert.Contains("1 day(s)", result.Detail);
    }

    [Fact]
    public async Task ExtractAsync_WhenARawDayIsNotThereOnReadBack_TheVendorFailsNamingTheDate()
    {
        var vendorId = Guid.NewGuid();
        var raw = new RawVendorSpendData
        {
            VendorId = vendorId,
            TenantId = "ecosync",
            Period = Period,
            RecordsByDate = new Dictionary<DateOnly, IReadOnlyList<IReadOnlyDictionary<string, object?>>>
            {
                [Period.Start] = [],
                [Period.Start.AddDays(1)] = [],
            },
        };
        var rawRepo = new FakeRawExtractionRepository();
        rawRepo.LoseDays.Add(Period.Start.AddDays(1));

        var service = new SpendExtractionService(
            [new FakeExtractor(vendorId, raw)], [new FakeNormalizer(vendorId, [])],
            rawRepo, new FakeDailySpendRepository(),
            new FakeVendorRateConfigRepository(), NullLogger<SpendExtractionService>.Instance);

        var result = Assert.Single(await service.ExtractAsync(
            "ecosync", Period, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(VendorExtractionStatus.Failed, result.Status);
        Assert.Contains("raw extraction records", result.Detail);
        Assert.Contains(Period.Start.AddDays(1).ToString("yyyy-MM-dd"), result.Detail);
    }

    [Fact]
    public async Task ExtractAsync_AFailedPostWriteCheck_LogsAnError_AndNamesNoFigure()
    {
        var vendorId = Guid.NewGuid();
        var dailyRepo = new FakeDailySpendRepository();
        dailyRepo.LoseDays.Add(Period.Start);
        var logger = new CapturingLogger<SpendExtractionService>();

        var service = new SpendExtractionService(
            [new FakeExtractor(vendorId, EmptyRawData(vendorId))],
            [new FakeNormalizer(vendorId, [new() { TenantId = "ecosync", VendorId = vendorId, Date = Period.Start, UsageOrOverage = 98765.43m }])],
            new FakeRawExtractionRepository(), dailyRepo,
            new FakeVendorRateConfigRepository(), logger);

        var result = Assert.Single(await service.ExtractAsync(
            "ecosync", Period, cancellationToken: TestContext.Current.CancellationToken));

        Assert.DoesNotContain("98765", result.Detail);
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Error && e.Message.Contains("Post-write check failed"));
        Assert.DoesNotContain(logger.Entries, e => e.Message.Contains("98765"));
    }

    [Fact]
    public async Task ExtractAsync_DryRun_DoesNotRunThePostWriteCheck()
    {
        var vendorId = Guid.NewGuid();
        var dailyRepo = new FakeDailySpendRepository();
        dailyRepo.LoseDays.Add(Period.Start);   // would fail the check if it ran: a dry run writes nothing to read back

        var service = new SpendExtractionService(
            [new FakeExtractor(vendorId, EmptyRawData(vendorId))],
            [new FakeNormalizer(vendorId, [new() { TenantId = "ecosync", VendorId = vendorId, Date = Period.Start }])],
            new FakeRawExtractionRepository(), dailyRepo,
            new FakeVendorRateConfigRepository(), NullLogger<SpendExtractionService>.Instance);

        var result = Assert.Single(await service.ExtractAsync(
            "ecosync", Period, dryRun: true, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(VendorExtractionStatus.Succeeded, result.Status);
    }

    [Fact]
    public async Task ExtractAsync_VendorWithAnActivitySource_StoresItsSummaries_AndSaysSoInTheDetail()
    {
        var vendorId = Guid.NewGuid();
        var rows = new List<ActivitySummaryRecord>
        {
            new() { TenantId = "ecosync", VendorId = vendorId, Date = Period.Start, DailyActiveUsers = 3, AssignedSeatCount = 10 },
            new() { TenantId = "ecosync", VendorId = vendorId, Date = Period.Start, Product = "chat", DailyActiveUsers = 1 },
        };
        var source = new FakeActivitySource(vendorId, rows);
        var repository = new FakeActivityRepository();

        var service = new SpendExtractionService(
            [new FakeExtractor(vendorId, EmptyRawData(vendorId))],
            [new FakeNormalizer(vendorId, [new() { TenantId = "ecosync", VendorId = vendorId, Date = Period.Start }])],
            new FakeRawExtractionRepository(), new FakeDailySpendRepository(),
            new FakeVendorRateConfigRepository(), NullLogger<SpendExtractionService>.Instance,
            activitySources: [source], activityRepository: repository);

        var result = Assert.Single(await service.ExtractAsync(
            "ecosync", Period, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(VendorExtractionStatus.Succeeded, result.Status);
        Assert.Equal(rows, repository.Upserted);
        Assert.Equal(Period, source.RequestedPeriod);
        Assert.Equal("ecosync", source.RequestedTenant);
        Assert.Equal(new ActivitySummaryUpsertResult(2, 0, 0), result.ActivitySummaries);
        Assert.Contains("activity summaries: 2 inserted", result.Detail);
    }

    [Fact]
    public async Task ExtractAsync_DryRun_PlansTheActivitySummariesWithoutWritingThem()
    {
        var vendorId = Guid.NewGuid();
        var repository = new FakeActivityRepository();

        var service = new SpendExtractionService(
            [new FakeExtractor(vendorId, EmptyRawData(vendorId))],
            [new FakeNormalizer(vendorId, [])],
            new FakeRawExtractionRepository(), new FakeDailySpendRepository(),
            new FakeVendorRateConfigRepository(), NullLogger<SpendExtractionService>.Instance,
            activitySources: [new FakeActivitySource(vendorId, [new() { TenantId = "ecosync", VendorId = vendorId, Date = Period.Start }])],
            activityRepository: repository);

        var result = Assert.Single(await service.ExtractAsync(
            "ecosync", Period, dryRun: true, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(VendorExtractionStatus.Succeeded, result.Status);
        Assert.Empty(repository.Upserted);
        Assert.Equal(1, repository.Planned);
    }

    [Fact]
    public async Task ExtractAsync_VendorWithNoActivitySource_IsUnaffected()
    {
        var vendorId = Guid.NewGuid();
        var repository = new FakeActivityRepository();

        var service = new SpendExtractionService(
            [new FakeExtractor(vendorId, EmptyRawData(vendorId))],
            [new FakeNormalizer(vendorId, [])],
            new FakeRawExtractionRepository(), new FakeDailySpendRepository(),
            new FakeVendorRateConfigRepository(), NullLogger<SpendExtractionService>.Instance,
            activitySources: [new FakeActivitySource(Guid.NewGuid(), [])], activityRepository: repository);

        var result = Assert.Single(await service.ExtractAsync(
            "ecosync", Period, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(VendorExtractionStatus.Succeeded, result.Status);
        Assert.Null(result.ActivitySummaries);
        Assert.DoesNotContain("activity", result.Detail);
        Assert.Empty(repository.Upserted);
    }

    [Fact]
    public async Task ExtractAsync_WhenTheActivitySourceFails_TheVendorIsFailed_ButTheDailyTotalsAreAlreadyWritten()
    {
        var vendorId = Guid.NewGuid();
        var dailyRepo = new FakeDailySpendRepository();

        var service = new SpendExtractionService(
            [new FakeExtractor(vendorId, EmptyRawData(vendorId))],
            [new FakeNormalizer(vendorId, [new() { TenantId = "ecosync", VendorId = vendorId, Date = Period.Start }])],
            new FakeRawExtractionRepository(), dailyRepo,
            new FakeVendorRateConfigRepository(), NullLogger<SpendExtractionService>.Instance,
            activitySources: [new FakeActivitySource(vendorId, [], throws: new InvalidOperationException("summaries are down"))],
            activityRepository: new FakeActivityRepository());

        var result = Assert.Single(await service.ExtractAsync(
            "ecosync", Period, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(VendorExtractionStatus.Failed, result.Status);
        Assert.Contains("summaries are down", result.Detail);
        Assert.True(dailyRepo.WasCalled);   // a failure here must not lose the spend, which is the source of record
    }

    private sealed class FakeActivitySource(
        Guid vendorId, IReadOnlyList<ActivitySummaryRecord> rows, Exception? throws = null) : IVendorActivitySummarySource
    {
        public Guid VendorId => vendorId;

        public DateRange? RequestedPeriod { get; private set; }

        public string? RequestedTenant { get; private set; }

        public Task<IReadOnlyList<ActivitySummaryRecord>> FetchAsync(
            string tenantId, DateRange period, CancellationToken cancellationToken = default)
        {
            RequestedTenant = tenantId;
            RequestedPeriod = period;
            return throws is null ? Task.FromResult(rows) : Task.FromException<IReadOnlyList<ActivitySummaryRecord>>(throws);
        }
    }

    private sealed class FakeActivityRepository : IActivitySummaryRepository
    {
        public List<ActivitySummaryRecord> Upserted { get; } = [];

        public int Planned { get; private set; }

        public Task<ActivitySummaryUpsertResult> UpsertAsync(
            IEnumerable<ActivitySummaryRecord> records, CancellationToken cancellationToken = default)
        {
            var list = records.ToList();
            Upserted.AddRange(list);
            return Task.FromResult(new ActivitySummaryUpsertResult(list.Count, 0, 0));
        }

        public Task<ActivitySummaryUpsertResult> PlanUpsertAsync(
            IEnumerable<ActivitySummaryRecord> records, CancellationToken cancellationToken = default)
        {
            Planned += records.Count();
            return Task.FromResult(new ActivitySummaryUpsertResult(records.Count(), 0, 0));
        }

        public Task<IReadOnlyList<ActivitySummaryRecord>> GetAsync(
            string tenantId, Guid vendorId, DateRange range, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ActivitySummaryRecord>>([]);
    }

    private sealed class FakeUserNormalizer(Guid vendorId, IReadOnlyList<UserDailyUsageRecord> rows) : IVendorUserUsageNormalizer
    {
        public Guid VendorId => vendorId;

        public IReadOnlyList<UserDailyUsageRecord> Normalize(
            RawVendorSpendData rawData, IReadOnlyList<VendorRateConfig> applicableRates) => rows;
    }

    private sealed class FakeUserRepository : IUserDailyUsageRepository
    {
        public List<UserDailyUsageRecord> Upserted { get; } = [];

        public int Planned { get; private set; }

        public Task<UserUsageUpsertResult> UpsertAsync(
            IEnumerable<UserDailyUsageRecord> records, CancellationToken cancellationToken = default)
        {
            var list = records.ToList();
            Upserted.AddRange(list);
            return Task.FromResult(new UserUsageUpsertResult(list.Count, 0, 0));
        }

        public Task<UserUsageUpsertResult> PlanUpsertAsync(
            IEnumerable<UserDailyUsageRecord> records, CancellationToken cancellationToken = default)
        {
            Planned += records.Count();
            return Task.FromResult(new UserUsageUpsertResult(records.Count(), 0, 0));
        }

        public Task<IReadOnlyList<UserDailyUsageRecord>> GetAsync(
            string tenantId, Guid vendorId, DateRange range, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<UserDailyUsageRecord>>([]);
    }

    private sealed class FakeRawExtractionRepository : IRawExtractionRepository
    {
        private readonly HashSet<DateOnly> _stored = [];

        public bool WasCalled { get; private set; }

        /// <summary>Days the fake accepts but does not keep, as a write that reports success without persisting.</summary>
        public HashSet<DateOnly> LoseDays { get; } = [];

        public Task UpsertAsync(
            string tenantId,
            Guid vendorId,
            IReadOnlyDictionary<DateOnly, IReadOnlyList<IReadOnlyDictionary<string, object?>>> recordsByDate,
            DateTime extractedAtUtc,
            CancellationToken cancellationToken = default)
        {
            WasCalled = true;
            foreach (var date in recordsByDate.Keys.Where(d => !LoseDays.Contains(d)))
            {
                _stored.Add(date);
            }

            return Task.CompletedTask;
        }

        public Task<IReadOnlyDictionary<DateOnly, string>> GetRecordsJsonAsync(
            string tenantId, Guid vendorId, DateRange period, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyDictionary<DateOnly, string>>(
                _stored.Where(d => d >= period.Start && d <= period.End).ToDictionary(d => d, _ => "[]"));
    }

    private sealed class FakeDailySpendRepository(
        Func<IReadOnlyList<DailySpendRecord>, IReadOnlyList<DailySpendChange>>? changesFor = null) : IDailySpendRepository
    {
        public bool WasCalled { get; private set; }

        public bool PlanWasCalled { get; private set; }

        public bool? ReceivedRequiresMonotonicUsage { get; private set; }

        private readonly List<DailySpendRecord> _stored = [];

        /// <summary>Days the fake accepts but does not keep, as a write that reports success without persisting.</summary>
        public HashSet<DateOnly> LoseDays { get; } = [];

        public Task<IReadOnlyList<DailySpendChange>> UpsertAsync(
            IEnumerable<DailySpendRecord> records,
            bool requiresMonotonicUsage = false,
            CancellationToken cancellationToken = default)
        {
            WasCalled = true;
            ReceivedRequiresMonotonicUsage = requiresMonotonicUsage;
            var list = records.ToList();
            _stored.AddRange(list.Where(r => !LoseDays.Contains(r.Date)));
            return Task.FromResult(ChangesFor(list));
        }

        public Task<IReadOnlyList<DailySpendChange>> PlanUpsertAsync(
            IEnumerable<DailySpendRecord> records,
            bool requiresMonotonicUsage = false,
            CancellationToken cancellationToken = default)
        {
            PlanWasCalled = true;
            ReceivedRequiresMonotonicUsage = requiresMonotonicUsage;
            return Task.FromResult(ChangesFor(records));
        }

        // Default: every record is a brand-new day.
        private IReadOnlyList<DailySpendChange> ChangesFor(IEnumerable<DailySpendRecord> records)
        {
            var list = records.ToList();
            return changesFor?.Invoke(list)
                ?? list.Select(r => new DailySpendChange(r.Date, DailySpendChangeKind.Inserted, null, r, r)).ToList();
        }

        public Task<IReadOnlyList<DailySpendRecord>> GetAsync(
            string tenantId, Guid vendorId, DateRange range, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<DailySpendRecord>>(
                _stored.Where(r => r.TenantId == tenantId && r.VendorId == vendorId && r.Date >= range.Start && r.Date <= range.End).ToList());
    }
}
