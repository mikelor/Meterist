using Meterist.Core.Extraction;
using Meterist.Core.Models;
using Meterist.Core.Persistence;
using Meterist.Core.Vendors;
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

        public Task<RawVendorSpendData> ExtractAsync(
            string tenantId, DateRange period, CancellationToken cancellationToken = default) =>
            Task.FromResult(dataToReturn);
    }

    private sealed class ThrowingExtractor(Guid vendorId, Exception exceptionToThrow) : IVendorSpendExtractor
    {
        public Guid VendorId => vendorId;

        public bool SupportsOverage => true;

        public bool SupportsPerUserBreakdown => true;

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
    }

    private sealed class FakeDailySpendRepository : IDailySpendRepository
    {
        public bool WasCalled { get; private set; }

        public Task UpsertAsync(IEnumerable<DailySpendRecord> records, CancellationToken cancellationToken = default)
        {
            WasCalled = true;
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<DailySpendRecord>> GetAsync(
            string tenantId, Guid vendorId, DateRange range, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<DailySpendRecord>>([]);
    }
}
