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
    public async Task AggregateAsync_ChatGptWeekShortOnlyBecauseTheReportStartsMidweek_IsNotCalledIncomplete()
    {
        // The report starts on a Friday (Jul 3). Weeks are anchored to the Wednesday on or before
        // the start (Jul 1), so week 0 is Jul 1-7 but only Jul 3-7 is inside the report: five days.
        // Every one of those five is stored, so nothing is missing; the week is short because of
        // where the report starts, not because of the vendor's data.
        var midweekStart = new DateRange(new DateOnly(2026, 7, 3), new DateOnly(2026, 7, 21));
        var dailyRepo = new FakeDailySpendRepository(
            recordsFor: (tenantId, vendorId) => DaysOf(tenantId, vendorId, midweekStart.Start, 19));
        var aggregator = new ReportDataAggregator(dailyRepo, new FakeVendorRateConfigRepository());

        var data = await aggregator.AggregateAsync(
            ["zelleri"], midweekStart, cancellationToken: TestContext.Current.CancellationToken);

        var chatGpt = data.Tenants[0].Vendors.Single(v => v.Vendor.Id == VendorCatalog.ChatGptEnterprise.Id);
        Assert.Equal(5, chatGpt.Weeks[0].DaysPresent);
        Assert.False(chatGpt.Weeks[0].ExcludeFromProjection);
    }

    [Fact]
    public async Task AggregateAsync_ChatGptWeekMissingDaysInsideAMidweekStartedReport_IsStillExcluded()
    {
        // Same Friday start, but only Jul 3-4 of the five in-report days are stored.
        var midweekStart = new DateRange(new DateOnly(2026, 7, 3), new DateOnly(2026, 7, 21));
        var dailyRepo = new FakeDailySpendRepository(
            recordsFor: (tenantId, vendorId) =>
                DaysOf(tenantId, vendorId, midweekStart.Start, 19).Where(r => r.Date.Day is <= 4 or >= 8).ToList());
        var aggregator = new ReportDataAggregator(dailyRepo, new FakeVendorRateConfigRepository());

        var data = await aggregator.AggregateAsync(
            ["zelleri"], midweekStart, cancellationToken: TestContext.Current.CancellationToken);

        var chatGpt = data.Tenants[0].Vendors.Single(v => v.Vendor.Id == VendorCatalog.ChatGptEnterprise.Id);
        Assert.Equal(2, chatGpt.Weeks[0].DaysPresent);
        Assert.True(chatGpt.Weeks[0].ExcludeFromProjection);
    }

    [Fact]
    public async Task AggregateAsync_ChatGptTrailingPartialWeekWithAllItsDays_IsNotFlaggedAsMissingData()
    {
        // The report ends on a Friday (Jul 17); week 2 is Jul 15-21 with Jul 15-17 inside the report
        // and all three stored. Projections leave it out as an incomplete week anyway (its end is
        // past the report's), so it must not also be reported as missing data.
        var endsMidweek = new DateRange(new DateOnly(2026, 7, 1), new DateOnly(2026, 7, 17));
        var dailyRepo = new FakeDailySpendRepository(
            recordsFor: (tenantId, vendorId) => DaysOf(tenantId, vendorId, endsMidweek.Start, 17));
        var aggregator = new ReportDataAggregator(dailyRepo, new FakeVendorRateConfigRepository());

        var data = await aggregator.AggregateAsync(
            ["zelleri"], endsMidweek, cancellationToken: TestContext.Current.CancellationToken);

        var chatGpt = data.Tenants[0].Vendors.Single(v => v.Vendor.Id == VendorCatalog.ChatGptEnterprise.Id);
        Assert.Equal(3, chatGpt.Weeks[2].DaysPresent);
        Assert.False(chatGpt.Weeks[2].ExcludeFromProjection);
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

    [Fact]
    public async Task AggregateAsync_AttachesTheStoredBreakdown_SummarisedPerVendor()
    {
        var gemini = VendorCatalog.GeminiEnterprise.Id;
        var breakdown = new FakeBreakdownRepository(
        [
            Breakdown("ecosync", gemini, 1, "Seat", 10m),
            Breakdown("ecosync", gemini, 2, "Seat", 10m),
            Breakdown("ecosync", gemini, 1, "Tax", 3m, isUsage: false),
            Breakdown("zelleri", gemini, 1, "Seat", 7m),
        ]);
        var aggregator = new ReportDataAggregator(new FakeDailySpendRepository(), new FakeVendorRateConfigRepository(), breakdown);

        var data = await aggregator.AggregateAsync(
            ["ecosync", "zelleri"], Period, cancellationToken: TestContext.Current.CancellationToken);

        var ecosync = data.Tenants.Single(t => t.TenantId == "ecosync").Vendors.Single(v => v.Vendor.Id == gemini);
        Assert.Equal(["Seat", "Tax"], ecosync.Breakdown.Select(b => b.Category));
        Assert.Equal(20m, ecosync.Breakdown[0].Amount);
        Assert.False(ecosync.Breakdown[1].IsUsage);
        Assert.True(ecosync.HasNotableBreakdown);   // a non-usage charge is worth showing

        var zelleri = data.Tenants.Single(t => t.TenantId == "zelleri").Vendors.Single(v => v.Vendor.Id == gemini);
        Assert.Equal(["Seat"], zelleri.Breakdown.Select(b => b.Category));
        Assert.False(zelleri.HasNotableBreakdown);  // nothing but a seat fee: no table needed

        // A vendor with no stored breakdown is simply empty.
        var other = data.Tenants[0].Vendors.First(v => v.Vendor.Id != gemini);
        Assert.Empty(other.Breakdown);
    }

    [Fact]
    public async Task AggregateAsync_WithoutABreakdownRepository_LeavesTheBreakdownEmpty()
    {
        var aggregator = new ReportDataAggregator(new FakeDailySpendRepository(), new FakeVendorRateConfigRepository());

        var data = await aggregator.AggregateAsync(["ecosync"], Period, cancellationToken: TestContext.Current.CancellationToken);

        Assert.All(data.Tenants[0].Vendors, v => Assert.Empty(v.Breakdown));
    }

    private static DailySpendBreakdownRecord Breakdown(
        string tenantId, Guid vendorId, int day, string category, decimal amount, bool isUsage = true) => new()
    {
        TenantId = tenantId,
        VendorId = vendorId,
        Date = Period.Start.AddDays(day - 1),
        Category = category,
        Amount = amount,
        IsUsage = isUsage,
    };

    private sealed class FakeBreakdownRepository(IReadOnlyList<DailySpendBreakdownRecord> rows) : IDailySpendBreakdownRepository
    {
        public Task<IReadOnlyList<DailySpendBreakdownRecord>> GetAsync(
            string tenantId, Guid vendorId, DateRange range, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<DailySpendBreakdownRecord>>(rows
                .Where(r => r.TenantId == tenantId && r.VendorId == vendorId && r.Date >= range.Start && r.Date <= range.End)
                .ToList());

        public Task<BreakdownReplaceResult> ReplaceAsync(
            string tenantId, Guid vendorId, IReadOnlyCollection<DateOnly> coveredDays,
            IEnumerable<DailySpendBreakdownRecord> records, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<BreakdownReplaceResult> PlanReplaceAsync(
            string tenantId, Guid vendorId, IReadOnlyCollection<DateOnly> coveredDays,
            IEnumerable<DailySpendBreakdownRecord> records, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
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
    public async Task AggregateAsync_ByMonth_GroupsTheDailyRecordsIntoCalendarMonths_AndLeavesTheWeeksAlone()
    {
        // Jul 20 to Aug 10, $10 a day: 12 days in July ($120) and 10 in August ($100).
        var period = new DateRange(new DateOnly(2026, 7, 20), new DateOnly(2026, 8, 10));
        var dailyRepo = new FakeDailySpendRepository(
            recordsFor: (tenantId, vendorId) => Enumerable.Range(0, 22)
                .Select(i => new DailySpendRecord
                {
                    TenantId = tenantId, VendorId = vendorId, Date = period.Start.AddDays(i),
                    SeatFee = 4m, UsageOrOverage = 6m, GrossSpend = 10m,
                })
                .ToList());
        var aggregator = new ReportDataAggregator(dailyRepo, new FakeVendorRateConfigRepository());

        var byMonth = await aggregator.AggregateAsync(
            ["zelleri"], period, aggregation: AggregationPeriod.Month, cancellationToken: TestContext.Current.CancellationToken);
        var byWeek = await aggregator.AggregateAsync(
            ["zelleri"], period, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(AggregationPeriod.Month, byMonth.Aggregation);
        var claude = byMonth.Tenants[0].Vendors.Single(v => v.Vendor.Id == VendorCatalog.ClaudeEnterprise.Id);
        Assert.Equal([120m, 100m], claude.DisplayPeriods.Select(p => p.GrossSpend));
        Assert.Equal([12, 10], claude.DisplayPeriods.Select(p => p.DaysPresent));
        Assert.Equal([48m, 40m], claude.DisplayPeriods.Select(p => p.SeatFee));
        Assert.Equal(claude.TotalGrossSpend, claude.DisplayPeriods.Sum(p => p.GrossSpend));

        // The weekly buckets, which feed the projections and the manifest, are the same whichever period was asked for.
        var weeksByMonth = claude.Weeks;
        var weeksByWeek = byWeek.Tenants[0].Vendors.Single(v => v.Vendor.Id == VendorCatalog.ClaudeEnterprise.Id).Weeks;
        Assert.Equal(weeksByWeek, weeksByMonth);
        Assert.Empty(byWeek.Tenants[0].Vendors[0].DisplayPeriods);
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
    public async Task AggregateAsync_SeatRateAtPeriodEnd_IsTheSeatRowInForceOnTheLastDay()
    {
        // Period ends Jul 14. A renewal on Jul 8 replaced the 10-seat row; a non-seat row must be ignored.
        var vendorId = VendorCatalog.ClaudeEnterprise.Id;
        var rateConfigRepo = new FakeVendorRateConfigRepository(
        [
            new VendorRateConfig
            {
                VendorId = vendorId, RateType = "per-seat", ModelOrSku = "seat", Rate = 300m, SeatCount = 10,
                EffectiveFrom = new DateOnly(2026, 1, 1), EffectiveTo = new DateOnly(2026, 7, 7),
            },
            new VendorRateConfig
            {
                VendorId = vendorId, RateType = "per-seat", ModelOrSku = "seat", Rate = 320m, SeatCount = 12,
                EffectiveFrom = new DateOnly(2026, 7, 8),
            },
            new VendorRateConfig
            {
                VendorId = vendorId, RateType = "credit", ModelOrSku = "credit-usd", Rate = 0.04m,
                EffectiveFrom = new DateOnly(2026, 1, 1),
            },
        ]);
        var aggregator = new ReportDataAggregator(new FakeDailySpendRepository(), rateConfigRepo);

        var data = await aggregator.AggregateAsync(
            ["zelleri"], Period, cancellationToken: TestContext.Current.CancellationToken);

        var claude = data.Tenants[0].Vendors.Single(v => v.Vendor.Id == vendorId);
        Assert.Equal(12, claude.SeatRateAtPeriodEnd?.SeatCount);
        Assert.Equal(320m, claude.SeatRateAtPeriodEnd?.Rate);
    }

    [Fact]
    public async Task AggregateAsync_SeatRateAtPeriodEnd_IsNullWhenTheSeatRateEndedBeforeTheLastDay()
    {
        var vendorId = VendorCatalog.ClaudeEnterprise.Id;
        var rateConfigRepo = new FakeVendorRateConfigRepository(
        [
            new VendorRateConfig
            {
                VendorId = vendorId, RateType = "per-seat", ModelOrSku = "seat", Rate = 300m, SeatCount = 10,
                EffectiveFrom = new DateOnly(2026, 1, 1), EffectiveTo = new DateOnly(2026, 7, 7),
            },
        ]);
        var aggregator = new ReportDataAggregator(new FakeDailySpendRepository(), rateConfigRepo);

        var data = await aggregator.AggregateAsync(
            ["zelleri"], Period, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Null(data.Tenants[0].Vendors.Single(v => v.Vendor.Id == vendorId).SeatRateAtPeriodEnd);
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
