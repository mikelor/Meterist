using Meterist.Core.Reporting;
using Meterist.Core.Vendors;

namespace Meterist.Core.Tests.Reporting;

/// <summary>
/// Issue #104: a usage-priced vendor stores nothing on a day without usage, so a week with no record at all is a week of
/// no usage and must count as zero in the projections, not vanish from them. Weeks run Wednesday to Tuesday from Jul 1
/// 2026 (a Wednesday). Expected values are explicit dollars.
/// </summary>
public class EmptyWeekProjectionTests
{
    private static readonly DateOnly Anchor = new(2026, 7, 1);
    private static readonly IReadOnlyDictionary<string, int> NoSources = new Dictionary<string, int>();

    [Fact]
    public void UsageThatStops_ProjectsOverEverySixCompleteWeeks_AndTheLatestWeekIsZero()
    {
        // Weeks 0-2 hold 100 each; weeks 3-5 have no record. Flat = 300 / 6 x 52 = $2,600.00 (it was 100 x 52 = $5,200.00).
        var period = Period(weeks: 6);
        var html = Render(period, ClaudeApi(Week(0, 100), Week(1, 100), Week(2, 100)));

        var row = ApiRow(html);
        var cells = Cells(row);
        Assert.Equal("$2,600.00", cells[1]);                             // Flat
        Assert.Equal(["$0.00", "$0.00"], cells[2..4]);                   // the floored trend, and the latest week (week 5)
        Assert.DoesNotContain("$5,200.00", row);                         // the old Flat and Latest
    }

    [Fact]
    public void AQuietWeekInTheMiddle_CountsAsZero()
    {
        // Weeks 0 and 2 hold 100; week 1 has no record. Flat = 200 / 3 x 52 = $3,466.67, not 100 x 52 = $5,200.00.
        var html = Render(Period(weeks: 3), ClaudeApi(Week(0, 100), Week(2, 100)));

        var row = ApiRow(html);
        var cells = Cells(row);
        Assert.Equal(["$3,466.67", "$3,466.67"], cells[1..3]);   // Flat, and Trend (a flat fit through 100, 0, 100)
        Assert.Equal("$5,200.00", cells[3]);                     // Latest week is week 2 = 100
    }

    [Fact]
    public void ALeadingPartialWeek_IsLeftOut_EvenWhenItIsEmpty()
    {
        // Starts Fri Jul 3, so Jul 1-7 is a short first week with no record. Only weeks 1 and 2 count: 100 and 0.
        // Flat = 50 x 52 = $2,600.00. Counting the short week as zero would give 100 / 3 x 52 = $1,733.33.
        var period = new DateRange(new DateOnly(2026, 7, 3), new DateOnly(2026, 7, 21));
        var html = Render(period, ClaudeApi(Week(1, 100)));

        var row = ApiRow(html);
        Assert.Equal("$2,600.00", Cells(row)[1]);
        Assert.DoesNotContain("$1,733.33", row);
    }

    [Fact]
    public void ATrailingPartialWeek_IsLeftOut_EvenWhenItIsEmpty()
    {
        // Ends Fri Jul 17, so Jul 15-21 is short. Weeks 0 and 1 count: 100 and 0, Flat = $2,600.00, not 100 / 3 x 52.
        var period = new DateRange(Anchor, new DateOnly(2026, 7, 17));
        var html = Render(period, ClaudeApi(Week(0, 100)));

        var row = ApiRow(html);
        Assert.Equal("$2,600.00", Cells(row)[1]);
        Assert.DoesNotContain("$1,733.33", row);
    }

    [Fact]
    public void AVendorWithNoRecordAtAll_StillShowsDashes_NotAZeroProjection()
    {
        var html = Render(Period(weeks: 6), ClaudeApi());

        var row = ApiRow(html);
        Assert.Equal(4, CountOf(row, "<span class=\"muted-dash\">—</span>"));   // committed and the three variable scenarios
        Assert.Equal(["$0.00"], Cells(row));                                      // only the actual column is a figure
    }

    [Fact]
    public void ASeatPricedVendorWithAGapWeek_IsNotFilled()
    {
        // ChatGPT is seat-priced: a week with no record is a gap, not a week of no usage, so nothing is added.
        // Weeks 0 and 2 hold 100: Flat = 100 x 52 = $5,200.00, not 200 / 3 x 52.
        var html = Render(Period(weeks: 3), Vendor(VendorCatalog.ChatGptEnterprise, Week(0, 100), Week(2, 100)));

        var projections = Projections(html);
        Assert.Contains("$5,200.00", projections);
        Assert.DoesNotContain("$3,466.67", projections);
    }

    [Fact]
    public void TheFootnote_NamesTheVendorAndHowManyWeeksCountedAsZero()
    {
        var html = Render(Period(weeks: 6), ClaudeApi(Week(0, 100), Week(1, 100), Week(2, 100)));

        var note = html[html.IndexOf("Weeks with no usage count as zero in the projections", StringComparison.Ordinal)..];
        note = note[..note.IndexOf("</li>", StringComparison.Ordinal)];
        Assert.Contains("t1: Claude API Platform (3 weeks)", note);
    }

    [Fact]
    public void TheFootnote_IsOmitted_WhenNoWeekWasFilled()
    {
        var html = Render(Period(weeks: 3), ClaudeApi(Week(0, 100), Week(1, 100), Week(2, 100)));

        Assert.DoesNotContain("Weeks with no usage count as zero in the projections", html);
    }

    [Fact]
    public void TheWeeklyTable_StillShowsAnEmptyWeekAsADash_NotAsAZero()
    {
        var html = Render(Period(weeks: 6), ClaudeApi(Week(0, 100), Week(1, 100), Week(2, 100)));

        var weekly = html[html.IndexOf("<h2>Weekly spend by vendor</h2>", StringComparison.Ordinal)..];
        weekly = weekly[..weekly.IndexOf("</section>", StringComparison.Ordinal)];
        var row = weekly[weekly.IndexOf("<th scope=\"row\">Claude API Platform</th>", StringComparison.Ordinal)..];
        row = row[..row.IndexOf("</tr>", StringComparison.Ordinal)];
        Assert.Equal(3, CountOf(row, "<span class=\"muted-dash\">—</span>"));   // weeks 3 to 5
        Assert.DoesNotContain("$0.00", row);
    }

    // ---- the trend floor

    [Fact]
    public void AFallingSeries_HasItsTrendFlooredAtZero_NotNegative()
    {
        // 100, 100, 100, 0, 0, 0 fits a line that reaches -40 one week on: the unfloored trend is -$2,080.00.
        var projection = ProjectionCalculator.Compute([100m, 100m, 100m, 0m, 0m, 0m]);

        Assert.Equal(2600m, projection.Flat);
        Assert.Equal(0m, projection.TrendAdjusted);
        Assert.Equal(0m, projection.LatestWeek);
    }

    // ---- the rule itself

    [Fact]
    public void ProjectionWeeks_AddsOnlyEmptyCompleteWeeks_ForAUsagePricedVendor()
    {
        var period = Period(weeks: 4);
        var weeks = new[] { Week(1, 10) };

        var series = ProjectionEligibility.ProjectionWeeks(weeks, VendorCatalog.ClaudeApiPlatform, Anchor, period);

        Assert.Equal([0, 1, 2, 3], series.Select(w => w.WeekIndex));
        Assert.Equal([0m, 10m, 0m, 0m], series.Select(w => w.UsageOrOverage));
        Assert.Equal([true, false, true, true], series.Select(ProjectionEligibility.IsFilledEmptyWeek));
    }

    [Fact]
    public void ProjectionWeeks_DoesNotTurnAFlaggedWeekThatHasRecordsIntoAZero()
    {
        // A week flagged as unreliable stays out of the series; it is not "no usage", so it must not be replaced by $0.
        var flagged = Week(1, 999) with { ExcludeFromProjection = true };

        var series = ProjectionEligibility.ProjectionWeeks(
            [Week(0, 10), flagged, Week(2, 10)], VendorCatalog.ClaudeApiPlatform, Anchor, Period(weeks: 3));

        Assert.Equal([0, 2], series.Select(w => w.WeekIndex));
    }

    // ---- fixtures

    private static DateRange Period(int weeks) => new(Anchor, Anchor.AddDays((weeks * 7) - 1));

    private static string Render(DateRange period, VendorReportData vendor) =>
        ClientBenchmarkReportRenderer.Render(new ReportData
        {
            Period = period,
            WeekAnchor = Anchor,
            Tenants = [Tenant("t1", vendor)],
            GeneratedAt = new DateTime(2026, 11, 22, 12, 0, 0),
        });

    private static VendorReportData ClaudeApi(params WeeklyTotal[] weeks) => Vendor(VendorCatalog.ClaudeApiPlatform, weeks);

    private static VendorReportData Vendor(VendorIdentity vendor, params WeeklyTotal[] weeks) => new()
    {
        Vendor = vendor,
        Weeks = weeks,
        CurrentRates = [],
        SourceDayCounts = NoSources,
    };

    private static TenantReportData Tenant(string tenantId, VendorReportData vendor) => new()
    {
        TenantId = tenantId,
        Vendors = VendorCatalog.All
            .Select(v => v.Id == vendor.Vendor.Id
                ? vendor
                : new VendorReportData { Vendor = v, Weeks = [], CurrentRates = [], SourceDayCounts = NoSources })
            .ToList(),
    };

    private static WeeklyTotal Week(int index, decimal usage)
    {
        var start = Anchor.AddDays(index * 7);
        return new WeeklyTotal(index, start, start.AddDays(6), 0m, usage, usage, 3, false);
    }

    private static string Projections(string html)
    {
        var start = html.IndexOf("<h2>Annualized cost projection</h2>", StringComparison.Ordinal);
        Assert.True(start >= 0);
        return html[start..html.IndexOf("</section>", start, StringComparison.Ordinal)];
    }

    private static string ApiRow(string html)
    {
        var section = Projections(html);
        var row = section.IndexOf("<th scope=\"row\">Claude API Platform</th>", StringComparison.Ordinal);
        Assert.True(row >= 0);
        return section[row..section.IndexOf("</tr>", row, StringComparison.Ordinal)];
    }

    // The figures in a projection row: committed per year, the variable Flat, Trend-adjusted and Latest week, then the actual.
    private static string[] Cells(string row) =>
        System.Text.RegularExpressions.Regex.Matches(row, "<td class=\"figure tabular\">([^<]*)</td>")
            .Select(m => m.Groups[1].Value.Trim())
            .ToArray();

    private static int CountOf(string text, string value)
    {
        var count = 0;
        for (var i = text.IndexOf(value, StringComparison.Ordinal); i >= 0; i = text.IndexOf(value, i + value.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }
}
