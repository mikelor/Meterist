using Meterist.Core.Extraction;
using Meterist.Core.Models;
using Meterist.Core.Persistence;
using Meterist.Core.Secrets;
using Meterist.Core.Vendors;
using Microsoft.Extensions.Logging.Abstractions;

namespace Meterist.Core.Tests.Extraction;

public class ConfiguredVendorExtractionTests
{
    private static readonly DateRange Period = new(new DateOnly(2026, 11, 1), new DateOnly(2026, 11, 2));

    private static readonly Guid Configured = VendorCatalog.GeminiEnterprise.Id;
    private static readonly Guid Unconfigured = VendorCatalog.ClaudeApiPlatform.Id;

    // The service gets an extractor for every catalog vendor, so "skipped" can only come
    // from the credential check, never from there being nothing registered to run.
    private static ConfiguredVendorExtraction Build(
        IEnumerable<Guid> vendorsWithCredential,
        HashSet<Guid>? vendorsThatFail = null,
        string? blankValue = null)
    {
        vendorsThatFail ??= [];
        var service = new SpendExtractionService(
            VendorCatalog.All.Select(v => (IVendorSpendExtractor)new ScriptedExtractor(v.Id, vendorsThatFail.Contains(v.Id))).ToList(),
            VendorCatalog.All.Select(v => (IVendorSpendNormalizer)new EmptyNormalizer(v.Id)).ToList(),
            new NoOpRawRepository(), new NoOpDailyRepository(), new NoOpRateRepository(),
            NullLogger<SpendExtractionService>.Instance);

        return new ConfiguredVendorExtraction(service, new FakeSecretStore(vendorsWithCredential, blankValue));
    }

    [Fact]
    public async Task VendorsWithoutACredential_AreSkipped_NotFailed()
    {
        var results = await Build([Configured]).ExtractAsync(
            "t", Period, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(VendorCatalog.All.Count, results.Count);
        Assert.Equal(VendorExtractionStatus.Succeeded, results.Single(r => r.VendorId == Configured).Status);
        Assert.All(
            results.Where(r => r.VendorId != Configured),
            r => Assert.Equal(VendorExtractionStatus.Skipped, r.Status));
        Assert.Equal(0, ExtractionExitCode.For(results));
    }

    [Fact]
    public async Task TenantWithNoCredentialsAtAll_SkipsEverything_AndExitsZero()
    {
        var results = await Build([]).ExtractAsync(
            "t", Period, cancellationToken: TestContext.Current.CancellationToken);

        Assert.All(results, r => Assert.Equal(VendorExtractionStatus.Skipped, r.Status));
        Assert.Equal(0, ExtractionExitCode.For(results));
    }

    [Fact]
    public async Task ConfiguredVendorThatFails_StillFails_AndExitsOne()
    {
        var results = await Build([Configured], vendorsThatFail: [Configured]).ExtractAsync(
            "t", Period, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(VendorExtractionStatus.Failed, results.Single(r => r.VendorId == Configured).Status);
        Assert.Equal(1, ExtractionExitCode.For(results));
    }

    [Fact]
    public async Task VendorFilter_RestrictsToThatVendor_AndSkipsIfUnconfigured()
    {
        var results = await Build([Configured]).ExtractAsync(
            "t", Period, Unconfigured, TestContext.Current.CancellationToken);

        var only = Assert.Single(results);
        Assert.Equal(Unconfigured, only.VendorId);
        Assert.Equal(VendorExtractionStatus.Skipped, only.Status);
    }

    [Fact]
    public async Task BlankCredential_CountsAsNotConfigured()
    {
        var results = await Build([], blankValue: "   ").ExtractAsync(
            "t", Period, cancellationToken: TestContext.Current.CancellationToken);

        Assert.All(results, r => Assert.Equal(VendorExtractionStatus.Skipped, r.Status));
    }

    [Fact]
    public void ExitCode_OnlyFailedCountsAsFailure()
    {
        static VendorExtractionResult R(VendorExtractionStatus s) => new(Guid.NewGuid(), "v", s, 0, null);

        Assert.Equal(0, ExtractionExitCode.For([]));
        Assert.Equal(0, ExtractionExitCode.For(
            [R(VendorExtractionStatus.Succeeded), R(VendorExtractionStatus.Skipped), R(VendorExtractionStatus.NotImplemented)]));
        Assert.Equal(1, ExtractionExitCode.For([R(VendorExtractionStatus.Succeeded), R(VendorExtractionStatus.Failed)]));
        Assert.Equal(1, ExtractionExitCode.For([R(VendorExtractionStatus.Skipped), R(VendorExtractionStatus.Failed)]));
    }

    private sealed class FakeSecretStore(IEnumerable<Guid> withCredential, string? blankValue) : ISecretStore
    {
        private readonly HashSet<Guid> _with = [.. withCredential];

        public Task<string?> GetCredentialAsync(string tenantId, Guid vendorId, CancellationToken cancellationToken = default) =>
            Task.FromResult(_with.Contains(vendorId) ? "secret" : blankValue);

        public Task SetCredentialAsync(string tenantId, Guid vendorId, string credential, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class ScriptedExtractor(Guid vendorId, bool fail) : IVendorSpendExtractor
    {
        public Guid VendorId => vendorId;

        public bool SupportsOverage => true;

        public bool SupportsPerUserBreakdown => true;

        public bool RequiresMonotonicUsage => false;

        public Task<RawVendorSpendData> ExtractAsync(
            string tenantId, DateRange period, CancellationToken cancellationToken = default) =>
            fail
                ? throw new InvalidOperationException("boom")
                : Task.FromResult(new RawVendorSpendData
                {
                    VendorId = vendorId,
                    TenantId = tenantId,
                    Period = period,
                    RecordsByDate = new Dictionary<DateOnly, IReadOnlyList<IReadOnlyDictionary<string, object?>>>(),
                });
    }

    private sealed class EmptyNormalizer(Guid vendorId) : IVendorSpendNormalizer
    {
        public Guid VendorId => vendorId;

        public IReadOnlyList<DailySpendRecord> Normalize(
            RawVendorSpendData rawData, IReadOnlyList<VendorRateConfig> applicableRates) => [];
    }

    private sealed class NoOpRawRepository : IRawExtractionRepository
    {
        public Task UpsertAsync(
            string tenantId, Guid vendorId,
            IReadOnlyDictionary<DateOnly, IReadOnlyList<IReadOnlyDictionary<string, object?>>> recordsByDate,
            DateTime extractedAtUtc, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<IReadOnlyDictionary<DateOnly, string>> GetRecordsJsonAsync(
            string tenantId, Guid vendorId, DateRange period, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyDictionary<DateOnly, string>>(new Dictionary<DateOnly, string>());
    }

    private sealed class NoOpDailyRepository : IDailySpendRepository
    {
        public Task<IReadOnlyList<DailySpendChange>> UpsertAsync(
            IEnumerable<DailySpendRecord> records, bool requiresMonotonicUsage = false,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<DailySpendChange>>([]);

        public Task<IReadOnlyList<DailySpendChange>> PlanUpsertAsync(
            IEnumerable<DailySpendRecord> records, bool requiresMonotonicUsage = false,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<DailySpendChange>>([]);

        public Task<IReadOnlyList<DailySpendRecord>> GetAsync(
            string tenantId, Guid vendorId, DateRange range, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<DailySpendRecord>>([]);
    }

    private sealed class NoOpRateRepository : IVendorRateConfigRepository
    {
        public Task AddAsync(VendorRateConfig rate, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<int> CloseOpenEndedRateAsync(
            string? tenantId, Guid vendorId, string? modelOrSku, DateOnly newEffectiveFrom,
            CancellationToken cancellationToken = default) => Task.FromResult(0);

        public Task<IReadOnlyList<VendorRateConfig>> GetApplicableRatesAsync(
            string tenantId, Guid vendorId, DateRange period, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<VendorRateConfig>>([]);

        public Task<DateOnly?> FindNextEffectiveFromAsync(
            string? tenantId, Guid vendorId, string? modelOrSku, DateOnly afterEffectiveFrom,
            CancellationToken cancellationToken = default) => Task.FromResult<DateOnly?>(null);

        public Task<IReadOnlyList<VendorRateConfig>> FindOverlappingRatesAsync(
            string? tenantId, Guid vendorId, string? modelOrSku, DateOnly effectiveFrom, DateOnly? effectiveTo,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<VendorRateConfig>>([]);
    }
}
