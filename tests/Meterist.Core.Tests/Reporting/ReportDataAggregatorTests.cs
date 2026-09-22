using Meterist.Core.Models;
using Meterist.Core.Persistence;
using Meterist.Core.Reporting;
using Meterist.Core.Vendors;

namespace Meterist.Core.Tests.Reporting;

public class ReportDataAggregatorTests
{
    private static readonly DateRange Period = new(new DateOnly(2026, 7, 1), new DateOnly(2026, 7, 14));

    [Fact]
    public async Task AggregateAsync_CoversEveryTenantAcrossEveryCatalogVendor()
    {
        var aggregator = new ReportDataAggregator(new FakeDailySpendRepository(), new FakeVendorRateConfigRepository());

        var data = await aggregator.AggregateAsync(
            ["zelleri", "ecosync"], Period, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(2, data.Tenants.Count);
        Assert.All(data.Tenants, t => Assert.Equal(VendorCatalog.All.Count, t.Vendors.Count));
        Assert.Equal(["zelleri", "ecosync"], data.Tenants.Select(t => t.TenantId));
    }

    [Fact]
    public async Task AggregateAsync_ChatGptWeekZero_IsExcludedFromProjection_OtherVendorsAreNot()
    {
        var dailyRepo = new FakeDailySpendRepository(
            recordsFor: (tenantId, vendorId) =>
            [
                new DailySpendRecord { TenantId = tenantId, VendorId = vendorId, Date = Period.Start },
            ]);
        var aggregator = new ReportDataAggregator(dailyRepo, new FakeVendorRateConfigRepository());

        var data = await aggregator.AggregateAsync(
            ["zelleri"], Period, cancellationToken: TestContext.Current.CancellationToken);

        var chatGpt = data.Tenants[0].Vendors.Single(v => v.Vendor.Id == VendorCatalog.ChatGptEnterprise.Id);
        Assert.True(chatGpt.Weeks[0].ExcludeFromProjection);

        var gemini = data.Tenants[0].Vendors.Single(v => v.Vendor.Id == VendorCatalog.GeminiEnterprise.Id);
        Assert.False(gemini.Weeks[0].ExcludeFromProjection);
    }

    [Fact]
    public async Task AggregateAsync_OnlyKeepsCurrentlyActiveRates()
    {
        var vendorId = VendorCatalog.GeminiEnterprise.Id;
        var rateConfigRepo = new FakeVendorRateConfigRepository(
        [
            new VendorRateConfig
            {
                VendorId = vendorId, RateType = "per-seat", Rate = 10m,
                EffectiveFrom = new DateOnly(2026, 1, 1), EffectiveTo = new DateOnly(2026, 6, 30),
            },
            new VendorRateConfig
            {
                VendorId = vendorId, RateType = "per-seat", Rate = 20m,
                EffectiveFrom = new DateOnly(2026, 7, 1), EffectiveTo = null,
            },
        ]);
        var aggregator = new ReportDataAggregator(new FakeDailySpendRepository(), rateConfigRepo);

        var data = await aggregator.AggregateAsync(
            ["zelleri"], Period, cancellationToken: TestContext.Current.CancellationToken);

        var gemini = data.Tenants[0].Vendors.Single(v => v.Vendor.Id == vendorId);
        var rate = Assert.Single(gemini.CurrentRates);
        Assert.Equal(20m, rate.Rate);
    }

    [Fact]
    public async Task AggregateAsync_UsesTheSuppliedGeneratedAt_RatherThanTheWallClock()
    {
        var aggregator = new ReportDataAggregator(new FakeDailySpendRepository(), new FakeVendorRateConfigRepository());
        var fixedTimestamp = new DateTime(2026, 8, 29, 14, 30, 0);

        var data = await aggregator.AggregateAsync(
            ["zelleri"], Period, generatedAt: fixedTimestamp, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(fixedTimestamp, data.GeneratedAt);
    }

    private sealed class FakeDailySpendRepository(
        Func<string, Guid, IReadOnlyList<DailySpendRecord>>? recordsFor = null) : IDailySpendRepository
    {
        public Task UpsertAsync(
            IEnumerable<DailySpendRecord> records,
            bool requiresMonotonicUsage = false,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<IReadOnlyList<DailySpendRecord>> GetAsync(
            string tenantId, Guid vendorId, DateRange range, CancellationToken cancellationToken = default) =>
            Task.FromResult(recordsFor?.Invoke(tenantId, vendorId) ?? []);
    }

    private sealed class FakeVendorRateConfigRepository(IReadOnlyList<VendorRateConfig>? ratesToReturn = null)
        : IVendorRateConfigRepository
    {
        private readonly IReadOnlyList<VendorRateConfig> _ratesToReturn = ratesToReturn ?? [];

        public Task AddAsync(VendorRateConfig rate, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<int> CloseOpenEndedRateAsync(
            string? tenantId, Guid vendorId, string? modelOrSku, DateOnly newEffectiveFrom,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(0);

        public Task<IReadOnlyList<VendorRateConfig>> GetApplicableRatesAsync(
            string tenantId, Guid vendorId, DateRange period, CancellationToken cancellationToken = default) =>
            Task.FromResult(_ratesToReturn);

        public Task<DateOnly?> FindNextEffectiveFromAsync(
            string? tenantId, Guid vendorId, string? modelOrSku, DateOnly afterEffectiveFrom,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<DateOnly?>(null);

        public Task<IReadOnlyList<VendorRateConfig>> FindOverlappingRatesAsync(
            string? tenantId, Guid vendorId, string? modelOrSku, DateOnly effectiveFrom, DateOnly? effectiveTo,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<VendorRateConfig>>([]);
    }
}
