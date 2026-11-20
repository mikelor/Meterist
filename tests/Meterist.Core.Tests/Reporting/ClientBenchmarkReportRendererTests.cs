using Meterist.Core.Models;
using Meterist.Core.Reporting;
using Meterist.Core.Vendors;

namespace Meterist.Core.Tests.Reporting;

public class ClientBenchmarkReportRendererTests
{
    // Jul 1 2026 is a Wednesday: weeks are Jul 1-7, Jul 8-14, Jul 15-21, all complete by Jul 21.
    private static readonly DateOnly Anchor = new(2026, 7, 1);
    private static readonly DateRange Period = new(Anchor, new DateOnly(2026, 7, 21));

    private static readonly IReadOnlyDictionary<string, int> NoSources = new Dictionary<string, int>();

    [Fact]
    public void Footnote_ShowsEachTenantsChatGptDaysBySource()
    {
        var html = ClientBenchmarkReportRenderer.Render(Report(
            Tenant("tenant-a", ChatGpt(FullWeeks(100, 100, 100), new Dictionary<string, int>
            {
                [DailySpendSources.CostsExport] = 3,
                [DailySpendSources.DailyUsageApi] = 12,
            })),
            Tenant("tenant-b", ChatGpt(FullWeeks(100, 100, 100), new Dictionary<string, int>
            {
                [DailySpendSources.DailyUsageApi] = 20,
            }))));

        // The source of record is listed first whatever order the counts arrive in.
        Assert.Contains("tenant-a: 12 from the Daily Usage API, 3 from the COSTS export", html);
        Assert.Contains("tenant-b: 20 from the Daily Usage API", html);
        Assert.Contains("a stored usage figure is never lowered", html);
    }

    [Fact]
    public void Footnote_OmitsTheSourceNote_WhenNoDayCarriesASource()
    {
        var html = ClientBenchmarkReportRenderer.Render(Report(
            Tenant("tenant-a", ChatGpt(FullWeeks(100, 100, 100), NoSources))));

        Assert.DoesNotContain("ChatGPT Enterprise data sources", html);
    }

    [Fact]
    public void Footnote_SaysNoWeekWasExcluded_AndDropsTheOldRetentionClaim()
    {
        var html = ClientBenchmarkReportRenderer.Render(Report(
            Tenant("tenant-a", ChatGpt(FullWeeks(100, 100, 100), NoSources))));

        Assert.Contains("No ChatGPT Enterprise week was excluded from the trend projections", html);

        // These statements were true only while the COSTS export was the sole source; leaving
        // them in would tell a client that real data was discarded when it wasn't.
        Assert.DoesNotContain("first week is always excluded", html);
        Assert.DoesNotContain("first week is excluded from trend projections", html);
        Assert.DoesNotContain("permanently gone", html);
        Assert.DoesNotContain("retention-clamp data loss", html);
    }

    [Fact]
    public void Footnote_ListsTheWeeksThatWereExcluded_PerTenant()
    {
        var incompleteFirstWeek = new[]
        {
            Week(0, 100, days: 3, exclude: true),
            Week(1, 100),
            Week(2, 100),
        };

        var html = ClientBenchmarkReportRenderer.Render(Report(
            Tenant("tenant-a", ChatGpt(FullWeeks(100, 100, 100), NoSources)),
            Tenant("tenant-b", ChatGpt(incompleteFirstWeek, NoSources))));

        Assert.Contains("weeks with missing days are excluded from the trend projections", html);
        Assert.Contains("Excluded: tenant-b: Jul 1–7", html);
        Assert.DoesNotContain("No ChatGPT Enterprise week was excluded", html);
        // tenant-a had nothing excluded, so it isn't named in the excluded list.
        Assert.DoesNotContain("Excluded: tenant-a", html);
        Assert.DoesNotContain("; tenant-a:", html);
    }

    [Fact]
    public void Footnote_DoesNotListTheTrailingPartialWeekAsAnExclusion()
    {
        // The report ends Jul 17, so week 2 (Jul 15-21) is still in progress. It is flagged because
        // it has few days, but that is the normal trailing-partial-week rule, not a data problem.
        var shortPeriod = new DateRange(Anchor, new DateOnly(2026, 7, 17));
        var weeks = new[] { Week(0, 100), Week(1, 100), Week(2, 100, days: 3, exclude: true) };

        var html = ClientBenchmarkReportRenderer.Render(Report(
            shortPeriod, Tenant("tenant-a", ChatGpt(weeks, NoSources))));

        Assert.Contains("No ChatGPT Enterprise week was excluded", html);
    }

    [Fact]
    public void WeeklyAndProjectionIntros_NoLongerClaimTheFirstWeekIsAlwaysExcluded()
    {
        var html = ClientBenchmarkReportRenderer.Render(Report(
            Tenant("tenant-a", ChatGpt(FullWeeks(100, 100, 100), NoSources))));

        Assert.Contains("A ChatGPT Enterprise week with missing days is excluded from the trend projections below", html);
        Assert.Contains("any ChatGPT Enterprise week with missing days", html);
        Assert.DoesNotContain("first week is always excluded", html);
    }

    [Fact]
    public void Projection_UsesAChatGptFirstWeekThatIsNotExcluded()
    {
        // Weeks of 999, 100, 100. Including the first: flat = (999+100+100)/3 x 52 = $20,782.67.
        var html = ClientBenchmarkReportRenderer.Render(Report(
            Tenant("tenant-a", ChatGpt(FullWeeks(999, 100, 100), NoSources))));

        Assert.Contains("$20,782.67", html);
    }

    [Fact]
    public void Projection_StillLeavesOutAChatGptFirstWeekThatIsFlaggedIncomplete()
    {
        // Same values, but week 0 is flagged incomplete: flat = (100+100)/2 x 52 = $5,200.00,
        // and the 999 must not leak into any projection.
        var weeks = new[] { Week(0, 999, days: 3, exclude: true), Week(1, 100), Week(2, 100) };

        var html = ClientBenchmarkReportRenderer.Render(Report(
            Tenant("tenant-a", ChatGpt(weeks, NoSources))));

        Assert.Contains("$5,200.00", html);
        Assert.DoesNotContain("$20,782.67", html);
    }

    // ---- fixtures -------------------------------------------------------------------------

    private static ReportData Report(params TenantReportData[] tenants) => Report(Period, tenants);

    private static ReportData Report(DateRange period, params TenantReportData[] tenants) => new()
    {
        Period = period,
        WeekAnchor = Anchor,
        Tenants = tenants,
        GeneratedAt = new DateTime(2026, 11, 19, 12, 0, 0),
    };

    // The renderer looks up every catalog vendor for every tenant, so each tenant needs all four.
    private static TenantReportData Tenant(string tenantId, VendorReportData chatGpt) => new()
    {
        TenantId = tenantId,
        Vendors = VendorCatalog.All
            .Select(v => v.Id == VendorCatalog.ChatGptEnterprise.Id ? chatGpt : Vendor(v, [], NoSources))
            .ToList(),
    };

    private static VendorReportData ChatGpt(
        IReadOnlyList<WeeklyTotal> weeks, IReadOnlyDictionary<string, int> sources) =>
        Vendor(VendorCatalog.ChatGptEnterprise, weeks, sources);

    private static VendorReportData Vendor(
        VendorIdentity vendor, IReadOnlyList<WeeklyTotal> weeks, IReadOnlyDictionary<string, int> sources) => new()
    {
        Vendor = vendor,
        Weeks = weeks,
        CurrentRates = [],
        SourceDayCounts = sources,
    };

    private static IReadOnlyList<WeeklyTotal> FullWeeks(params decimal[] gross) =>
        gross.Select((g, i) => Week(i, g)).ToList();

    private static WeeklyTotal Week(int index, decimal gross, int days = 7, bool exclude = false)
    {
        var start = Anchor.AddDays(index * 7);
        return new WeeklyTotal(index, start, start.AddDays(6), 0m, gross, gross, days, exclude);
    }
}
