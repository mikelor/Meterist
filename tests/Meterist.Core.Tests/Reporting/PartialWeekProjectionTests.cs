using Meterist.Core.Reporting;
using Meterist.Core.Vendors;

namespace Meterist.Core.Tests.Reporting;

/// <summary>
/// Issue #69: a week cut short by where the report starts or ends must not feed the projections, for any vendor. Weeks run
/// Wednesday to Tuesday, anchored to the Wednesday on or before the report's first day (Jul 1 2026 is a Wednesday).
/// </summary>
public class PartialWeekProjectionTests
{
    private static readonly DateOnly Anchor = new(2026, 7, 1);
    private static readonly IReadOnlyDictionary<string, int> NoSources = new Dictionary<string, int>();

    // ---- the rule itself

    [Theory]
    [InlineData(2026, 7, 1, 2026, 7, 21, 2026, 7, 1, false, false)]    // starts and ends on week edges: a full week
    [InlineData(2026, 7, 3, 2026, 7, 21, 2026, 7, 1, true, false)]     // report starts Friday: Jul 1-7 is a short first week
    [InlineData(2026, 7, 1, 2026, 7, 17, 2026, 7, 15, false, true)]    // report ends Friday: Jul 15-21 is a short last week
    [InlineData(2026, 7, 3, 2026, 7, 17, 2026, 7, 8, false, false)]    // a middle week of a report cut at both ends is full
    public void AWeekIsPartialOnlyWhereItsDaysSpillOutsideTheReportPeriod(
        int sy, int sm, int sd, int ey, int em, int ed, int wy, int wm, int wd, bool leading, bool trailing)
    {
        var period = new DateRange(new DateOnly(sy, sm, sd), new DateOnly(ey, em, ed));
        var weekStart = new DateOnly(wy, wm, wd);
        var week = new WeeklyTotal(0, weekStart, weekStart.AddDays(6), 0m, 1m, 1m, 7, false);

        Assert.Equal(leading, ProjectionEligibility.IsLeadingPartial(week, period));
        Assert.Equal(trailing, ProjectionEligibility.IsTrailingPartial(week, period));
        Assert.Equal(!leading && !trailing, ProjectionEligibility.FeedsProjection(week, period));
    }

    [Fact]
    public void AFullWeekFlaggedForMissingData_StillDoesNotFeedAProjection()
    {
        var period = new DateRange(Anchor, Anchor.AddDays(20));
        var flagged = new WeeklyTotal(1, Anchor.AddDays(7), Anchor.AddDays(13), 0m, 1m, 1m, 3, ExcludeFromProjection: true);

        Assert.False(ProjectionEligibility.FeedsProjection(flagged, period));
    }

    // ---- the projections, for every vendor

    [Fact]
    public void AMidWeekStart_LeavesTheShortFirstWeekOutOfTheProjection_ForAVendorOtherThanChatGpt()
    {
        // Starts Friday Jul 3. Week 0 (Jul 1-7) has 5 days and a low 10; weeks 1 and 2 are full at 100.
        // Without the rule flat = (10+100+100)/3 x 52 = $3,640.00; with it, flat = 100 x 52 = $5,200.00.
        var period = new DateRange(new DateOnly(2026, 7, 3), new DateOnly(2026, 7, 21));
        var weeks = new[] { Week(0, 10, days: 5), Week(1, 100), Week(2, 100) };

        var html = ClientBenchmarkReportRenderer.Render(Report(period, Tenant("t1", Claude(weeks))));

        var projections = Projections(html);
        Assert.Contains("$5,200.00", projections);
        Assert.DoesNotContain("$3,640.00", projections);
    }

    [Fact]
    public void AMidWeekStart_AlsoLeavesOutAChatGptFirstWeekThatHasEveryDayInsideThePeriod()
    {
        // All five days of Jul 3-7 are stored, so the missing-data rule does not flag it; the partial-week rule still does.
        var period = new DateRange(new DateOnly(2026, 7, 3), new DateOnly(2026, 7, 21));
        var weeks = new[] { Week(0, 999, days: 5), Week(1, 100), Week(2, 100) };

        var html = ClientBenchmarkReportRenderer.Render(Report(period, Tenant("t1", ChatGpt(weeks))));

        var projections = Projections(html);
        Assert.Contains("$5,200.00", projections);
        Assert.DoesNotContain("$20,782.67", projections);   // (999+100+100)/3 x 52
    }

    [Fact]
    public void AMidWeekEnd_LeavesOutTheShortLastWeek_AsItAlwaysDid()
    {
        var period = new DateRange(Anchor, new DateOnly(2026, 7, 17));
        var weeks = new[] { Week(0, 100), Week(1, 100), Week(2, 5, days: 3) };

        var html = ClientBenchmarkReportRenderer.Render(Report(period, Tenant("t1", Claude(weeks))));

        Assert.Contains("$5,200.00", Projections(html));
    }

    [Fact]
    public void WhenBothEndsAreCut_OnlyTheFullMiddleWeeksFeedTheProjection()
    {
        // Starts Fri Jul 3, ends Fri Jul 24: week 0 and week 3 are short, weeks 1 and 2 (100, 120) are full.
        // flat = (100+120)/2 x 52 = $5,720.00.
        var period = new DateRange(new DateOnly(2026, 7, 3), new DateOnly(2026, 7, 24));
        var weeks = new[] { Week(0, 7, days: 5), Week(1, 100), Week(2, 120), Week(3, 9, days: 3) };

        var html = ClientBenchmarkReportRenderer.Render(Report(period, Tenant("t1", Claude(weeks))));

        Assert.Contains("$5,720.00", Projections(html));
    }

    [Fact]
    public void AQuietWeekInsideThePeriod_ForAUsagePricedVendor_IsStillUsed()
    {
        // Claude API Platform has no record on a day with no usage, so a week can hold fewer than seven days without
        // being cut short by the report's edges; it is real data and must still feed the projection.
        var period = new DateRange(Anchor, Anchor.AddDays(20));
        var weeks = new[] { Week(0, 100), Week(1, 40, days: 3), Week(2, 100) };

        var html = ClientBenchmarkReportRenderer.Render(Report(period, Tenant("t1", ClaudeApi(weeks))));

        Assert.Contains("$4,160.00", Projections(html));   // (100+40+100)/3 x 52
    }

    [Fact]
    public void AWednesdayStart_LeavesNothingOutAtTheStart()
    {
        var period = new DateRange(Anchor, Anchor.AddDays(20));

        var html = ClientBenchmarkReportRenderer.Render(Report(period, Tenant("t1", Claude(FullWeeks(100, 100, 100)))));

        Assert.Contains("which opens a full week, so no first week was left out", html);
        Assert.DoesNotContain("Left out at the start", html);
        Assert.DoesNotContain("*</th>", html);   // no week is marked partial
    }

    [Fact]
    public void ShorterThanAWeek_HasNoProjectionAndDoesNotThrow()
    {
        var period = new DateRange(new DateOnly(2026, 7, 3), new DateOnly(2026, 7, 5));
        var weeks = new[] { Week(0, 30, days: 3) };

        var html = ClientBenchmarkReportRenderer.Render(Report(period, Tenant("t1", Claude(weeks))));

        Assert.DoesNotContain("$1,560.00", Projections(html));   // 30 x 52 must not appear as a projection
        Assert.Contains("<span class=\"muted-dash\">—</span>", Projections(html));
    }

    // ---- what the report says about it

    [Fact]
    public void TheFootnote_NamesTheLeadingWeekAndTheVendorsItAffected_PerTenant()
    {
        var period = new DateRange(new DateOnly(2026, 7, 3), new DateOnly(2026, 7, 21));
        var weeks = new[] { Week(0, 10, days: 5), Week(1, 100), Week(2, 100) };

        var html = ClientBenchmarkReportRenderer.Render(Report(
            period,
            Tenant("tenant-a", Claude(weeks), ChatGpt(weeks)),
            Tenant("tenant-b", Claude([Week(1, 100), Week(2, 100)]))));   // a tenant with nothing stored in the first week

        var footnote = html[html.IndexOf("Partial weeks are left out", StringComparison.Ordinal)..];
        footnote = footnote[..footnote.IndexOf("</li>", StringComparison.Ordinal)];

        Assert.Contains("every vendor", footnote);
        Assert.Contains("Friday, Jul 3, 2026", footnote);
        Assert.Contains("Jul 1", footnote);   // the week that was left out
        Assert.Contains("tenant-a: Claude Enterprise, ChatGPT Enterprise", footnote);
        Assert.DoesNotContain("tenant-b: Claude", footnote);   // nothing of tenant-b's was cut
        Assert.Contains("Start a report on a Wednesday", footnote);
    }

    [Fact]
    public void TheFootnote_NamesTheTrailingWeekToo()
    {
        var period = new DateRange(Anchor, new DateOnly(2026, 7, 17));
        var weeks = new[] { Week(0, 100), Week(1, 100), Week(2, 5, days: 3) };

        var html = ClientBenchmarkReportRenderer.Render(Report(period, Tenant("t1", Claude(weeks))));

        var footnote = html[html.IndexOf("Partial weeks are left out", StringComparison.Ordinal)..];
        footnote = footnote[..footnote.IndexOf("</li>", StringComparison.Ordinal)];
        Assert.Contains("ends on Friday, Jul 17, 2026", footnote);
        Assert.Contains("Left out at the end — t1: Claude Enterprise", footnote);
    }

    [Fact]
    public void TheWeeklyTable_MarksBothAPartialFirstAndAPartialLastWeek_AndExplainsTheMarker()
    {
        var period = new DateRange(new DateOnly(2026, 7, 3), new DateOnly(2026, 7, 17));
        var weeks = new[] { Week(0, 10, days: 5), Week(1, 100), Week(2, 5, days: 3) };

        var html = ClientBenchmarkReportRenderer.Render(Report(period, Tenant("t1", Claude(weeks))));

        var weekly = html[html.IndexOf("<h2>Weekly spend by vendor</h2>", StringComparison.Ordinal)..];
        weekly = weekly[..weekly.IndexOf("</section>", StringComparison.Ordinal)];
        Assert.Equal(2, CountOf(weekly, "*</th>"));   // Jul 1-7* and Jul 15-21*, not Jul 8-14
        Assert.Contains("only part of it is inside the report period", weekly);
        Assert.Contains("left out of the projections", weekly);
    }

    [Fact]
    public void ThePartialWeekStaysInTheWeeklyTableAndTheActualTotal()
    {
        var period = new DateRange(new DateOnly(2026, 7, 3), new DateOnly(2026, 7, 21));
        var weeks = new[] { Week(0, 10, days: 5), Week(1, 100), Week(2, 100) };

        var html = ClientBenchmarkReportRenderer.Render(Report(period, Tenant("t1", Claude(weeks))));

        // The actual column is the vendor's whole spend ($210), partial week included.
        Assert.Contains("$210.00", Projections(html));
    }

    // ---- fixtures

    private static string Projections(string html)
    {
        var start = html.IndexOf("<h2>Annualized cost projection</h2>", StringComparison.Ordinal);
        Assert.True(start >= 0);
        return html[start..html.IndexOf("</section>", start, StringComparison.Ordinal)];
    }

    private static int CountOf(string text, string value)
    {
        var count = 0;
        for (var i = text.IndexOf(value, StringComparison.Ordinal); i >= 0; i = text.IndexOf(value, i + 1, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }

    private static ReportData Report(DateRange period, params TenantReportData[] tenants) => new()
    {
        Period = period,
        WeekAnchor = Anchor,
        Tenants = tenants,
        GeneratedAt = new DateTime(2026, 11, 19, 12, 0, 0),
    };

    // The renderer looks up every catalog vendor for every tenant, so each tenant carries all four.
    private static TenantReportData Tenant(string tenantId, params VendorReportData[] vendors) => new()
    {
        TenantId = tenantId,
        Vendors = VendorCatalog.All
            .Select(v => vendors.FirstOrDefault(x => x.Vendor.Id == v.Id) ?? Vendor(v, []))
            .ToList(),
    };

    private static VendorReportData Claude(IReadOnlyList<WeeklyTotal> weeks) => Vendor(VendorCatalog.ClaudeEnterprise, weeks);

    private static VendorReportData ClaudeApi(IReadOnlyList<WeeklyTotal> weeks) => Vendor(VendorCatalog.ClaudeApiPlatform, weeks);

    private static VendorReportData ChatGpt(IReadOnlyList<WeeklyTotal> weeks) => Vendor(VendorCatalog.ChatGptEnterprise, weeks);

    private static VendorReportData Vendor(VendorIdentity vendor, IReadOnlyList<WeeklyTotal> weeks) => new()
    {
        Vendor = vendor,
        Weeks = weeks,
        CurrentRates = [],
        SourceDayCounts = NoSources,
    };

    private static IReadOnlyList<WeeklyTotal> FullWeeks(params decimal[] gross) =>
        gross.Select((g, i) => Week(i, g)).ToList();

    private static WeeklyTotal Week(int index, decimal gross, int days = 7, bool exclude = false)
    {
        var start = Anchor.AddDays(index * 7);
        return new WeeklyTotal(index, start, start.AddDays(6), 0m, gross, gross, days, exclude);
    }
}
