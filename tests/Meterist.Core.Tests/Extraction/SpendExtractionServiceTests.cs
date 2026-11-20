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

    private sealed class FakeRawExtractionRepository : IRawExtractionRepository
    {
        public bool WasCalled { get; private set; }

        public Task UpsertAsync(
            string tenantId,
            Guid vendorId,
            IReadOnlyDictionary<DateOnly, IReadOnlyList<IReadOnlyDictionary<string, object?>>> recordsByDate,
            DateTime extractedAtUtc,
            CancellationToken cancellationToken = default)
        {
            WasCalled = true;
            return Task.CompletedTask;
        }

        public Task<IReadOnlyDictionary<DateOnly, string>> GetRecordsJsonAsync(
            string tenantId, Guid vendorId, DateRange period, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyDictionary<DateOnly, string>>(new Dictionary<DateOnly, string>());
    }

    private sealed class FakeDailySpendRepository(
        Func<IReadOnlyList<DailySpendRecord>, IReadOnlyList<DailySpendChange>>? changesFor = null) : IDailySpendRepository
    {
        public bool WasCalled { get; private set; }

        public bool PlanWasCalled { get; private set; }

        public bool? ReceivedRequiresMonotonicUsage { get; private set; }

        public Task<IReadOnlyList<DailySpendChange>> UpsertAsync(
            IEnumerable<DailySpendRecord> records,
            bool requiresMonotonicUsage = false,
            CancellationToken cancellationToken = default)
        {
            WasCalled = true;
            ReceivedRequiresMonotonicUsage = requiresMonotonicUsage;
            return Task.FromResult(ChangesFor(records));
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
            Task.FromResult<IReadOnlyList<DailySpendRecord>>([]);
    }
}
