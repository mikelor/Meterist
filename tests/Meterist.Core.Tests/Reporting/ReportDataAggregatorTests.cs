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
    public async Task AggregateAsync_ChatGptCompleteFirstWeek_IsNotExcluded()
    {
        // Jul 1 is a Wednesday, so Jul 1-7 is week 0 and Jul 8-14 is week 1. Both full.
        var dailyRepo = new FakeDailySpendRepository(
            recordsFor: (tenantId, vendorId) => DaysOf(tenantId, vendorId, Period.Start, 14));
        var aggregator = new ReportDataAggregator(dailyRepo, new FakeVendorRateConfigRepository());

        var data = await aggregator.AggregateAsync(
            ["zelleri"], Period, cancellationToken: TestContext.Current.CancellationToken);

        var chatGpt = data.Tenants[0].Vendors.Single(v => v.Vendor.Id == VendorCatalog.ChatGptEnterprise.Id);
        Assert.Equal(7, chatGpt.Weeks[0].DaysPresent);

        // The old rule excluded week 0 unconditionally. With the Daily Usage API as the source of
        // record it is real data, so a complete week 0 now feeds the projections.
        Assert.All(chatGpt.Weeks, w => Assert.False(w.ExcludeFromProjection));
    }

    [Fact]
    public async Task AggregateAsync_ChatGptWeekWithMissingDays_IsExcluded_OtherVendorsWithTheSameGapAreNot()
    {
        // Week 0 has only 3 of its 7 days recorded; week 1 is full.
        var dailyRepo = new FakeDailySpendRepository(
            recordsFor: (tenantId, vendorId) =>
                DaysOf(tenantId, vendorId, Period.Start, 14).Where(r => r.Date.Day is <= 3 or >= 8).ToList());
        var aggregator = new ReportDataAggregator(dailyRepo, new FakeVendorRateConfigRepository());

        var data = await aggregator.AggregateAsync(
            ["zelleri"], Period, cancellationToken: TestContext.Current.CancellationToken);

        var chatGpt = data.Tenants[0].Vendors.Single(v => v.Vendor.Id == VendorCatalog.ChatGptEnterprise.Id);
        Assert.Equal(3, chatGpt.Weeks[0].DaysPresent);
        Assert.True(chatGpt.Weeks[0].ExcludeFromProjection);
        Assert.False(chatGpt.Weeks[1].ExcludeFromProjection);

        // A usage-priced vendor writes no row for a quiet day, so a short week is not evidence of
        // missing data and must not be excluded.
        var gemini = data.Tenants[0].Vendors.Single(v => v.Vendor.Id == VendorCatalog.GeminiEnterprise.Id);
        Assert.Equal(3, gemini.Weeks[0].DaysPresent);
        Assert.False(gemini.Weeks[0].ExcludeFromProjection);
    }

    [Fact]
    public async Task AggregateAsync_CountsStoredDaysBySource_IgnoringDaysWithNoSource()
    {
        var dailyRepo = new FakeDailySpendRepository(
            recordsFor: (tenantId, vendorId) => vendorId == VendorCatalog.ChatGptEnterprise.Id
                ?
                [
                    Record(tenantId, vendorId, 1, DailySpendSources.DailyUsageApi),
                    Record(tenantId, vendorId, 2, DailySpendSources.DailyUsageApi),
                    Record(tenantId, vendorId, 3, DailySpendSources.CostsExport),
                ]
                : DaysOf(tenantId, vendorId, Period.Start, 3)); // no Source
        var aggregator = new ReportDataAggregator(dailyRepo, new FakeVendorRateConfigRepository());

        var data = await aggregator.AggregateAsync(
            ["zelleri"], Period, cancellationToken: TestContext.Current.CancellationToken);

        var chatGpt = data.Tenants[0].Vendors.Single(v => v.Vendor.Id == VendorCatalog.ChatGptEnterprise.Id);
        Assert.Equal(2, chatGpt.SourceDayCounts[DailySpendSources.DailyUsageApi]);
        Assert.Equal(1, chatGpt.SourceDayCounts[DailySpendSources.CostsExport]);

        var gemini = data.Tenants[0].Vendors.Single(v => v.Vendor.Id == VendorCatalog.GeminiEnterprise.Id);
        Assert.Empty(gemini.SourceDayCounts);
    }

    private static List<DailySpendRecord> DaysOf(string tenantId, Guid vendorId, DateOnly start, int count) =>
        Enumerable.Range(0, count)
            .Select(i => new DailySpendRecord { TenantId = tenantId, VendorId = vendorId, Date = start.AddDays(i) })
            .ToList();

    private static DailySpendRecord Record(string tenantId, Guid vendorId, int day, string source) => new()
    {
        TenantId = tenantId,
        VendorId = vendorId,
        Date = new DateOnly(2026, 7, day),
        Source = source,
    };

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
        public Task<IReadOnlyList<DailySpendChange>> UpsertAsync(
            IEnumerable<DailySpendRecord> records,
            bool requiresMonotonicUsage = false,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<DailySpendChange>>([]);

        public Task<IReadOnlyList<DailySpendChange>> PlanUpsertAsync(
            IEnumerable<DailySpendRecord> records,
            bool requiresMonotonicUsage = false,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<DailySpendChange>>([]);

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
