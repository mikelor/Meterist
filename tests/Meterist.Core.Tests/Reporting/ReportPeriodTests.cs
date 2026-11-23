using System.Text.Json;
using Meterist.Core.Models;
using Meterist.Core.Reporting;
using Meterist.Core.Vendors;

namespace Meterist.Core.Tests.Reporting;

/// <summary>
/// Issue #108: the report's spend table can be grouped by week, month, quarter or year. The projections and the manifest stay
/// weekly whatever is chosen. Expected values are explicit.
/// </summary>
public class ReportPeriodTests
{
    // Jul 1 2026 is a Wednesday: weeks run Jul 1-7, 8-14, 15-21 and so on.
    private static readonly DateOnly Anchor = new(2026, 7, 1);
    private static readonly DateRange Period = new(Anchor, new DateOnly(2026, 11, 21));
    private static readonly IReadOnlyDictionary<string, int> NoSources = new Dictionary<string, int>();

    // ---- parsing

    [Theory]
    [InlineData("week", AggregationPeriod.Week)]
    [InlineData("Month", AggregationPeriod.Month)]
    [InlineData("QUARTER", AggregationPeriod.Quarter)]
    [InlineData("year", AggregationPeriod.Year)]
    public void ParsesTheFourPeriods_IgnoringCase(string text, AggregationPeriod expected)
    {
        Assert.True(PeriodBuckets.TryParse(text, out var period));
        Assert.Equal(expected, period);
    }

    [Theory]
    [InlineData("decade")]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("1")]
    [InlineData("0")]
    [InlineData("weeks")]
    public void RejectsAnythingElse_IncludingANumberThatWouldMatchAnEnumValue(string text)
    {
        Assert.False(PeriodBuckets.TryParse(text, out _));
    }

    [Fact]
    public void ANullValueIsRejected() => Assert.False(PeriodBuckets.TryParse(null, out _));

    // ---- the columns

    [Fact]
    public void Months_JulyToNovember_AreFiveColumns_AndNovemberRunsToTheEndOfTheMonth()
    {
        var columns = PeriodBuckets.Columns(AggregationPeriod.Month, Period, Anchor);

        Assert.Equal(
            ["Jul 2026", "Aug 2026", "Sep 2026", "Oct 2026", "Nov 2026"],
            columns.Select(c => PeriodBuckets.Label(AggregationPeriod.Month, c.Start, c.End)));
        Assert.Equal(new DateOnly(2026, 11, 1), columns[^1].Start);
        Assert.Equal(new DateOnly(2026, 11, 30), columns[^1].End);
        Assert.Equal([0, 1, 2, 3, 4], columns.Select(c => c.Index));
    }

    [Fact]
    public void Quarters_JulyToNovember_AreQ3AndQ4()
    {
        var columns = PeriodBuckets.Columns(AggregationPeriod.Quarter, Period, Anchor);

        Assert.Equal(["Q3 2026", "Q4 2026"], columns.Select(c => PeriodBuckets.Label(AggregationPeriod.Quarter, c.Start, c.End)));
        Assert.Equal(new DateOnly(2026, 9, 30), columns[0].End);
        Assert.Equal(new DateOnly(2026, 12, 31), columns[1].End);
    }

    [Fact]
    public void AQuarterThatStartsMidQuarter_IsStillAFullCalendarColumn_AndGroupsItsRecords()
    {
        var period = new DateRange(new DateOnly(2026, 8, 15), new DateOnly(2026, 11, 5));

        var columns = PeriodBuckets.Columns(AggregationPeriod.Quarter, period, Anchor);
        Assert.Equal(new DateOnly(2026, 7, 1), columns[0].Start);
        Assert.Equal(new DateOnly(2026, 9, 30), columns[0].End);
        Assert.Equal(new DateOnly(2026, 10, 1), columns[1].Start);

        var records = new[]
        {
            new DailySpendRecord { TenantId = "t1", VendorId = VendorCatalog.ClaudeEnterprise.Id, Date = new DateOnly(2026, 8, 20), GrossSpend = 10m },
            new DailySpendRecord { TenantId = "t1", VendorId = VendorCatalog.ClaudeEnterprise.Id, Date = new DateOnly(2026, 9, 30), GrossSpend = 5m },
            new DailySpendRecord { TenantId = "t1", VendorId = VendorCatalog.ClaudeEnterprise.Id, Date = new DateOnly(2026, 10, 1), GrossSpend = 7m },
        };
        var buckets = PeriodBuckets.Bucket(records, AggregationPeriod.Quarter, period, Anchor);
        Assert.Equal([15m, 7m], buckets.Select(b => b.GrossSpend));
    }

    [Fact]
    public void Years_AcrossAYearBoundary_AreOneColumnPerCalendarYear()
    {
        var columns = PeriodBuckets.Columns(AggregationPeriod.Year, new DateRange(new DateOnly(2026, 11, 1), new DateOnly(2027, 2, 1)), Anchor);

        Assert.Equal(["2026", "2027"], columns.Select(c => PeriodBuckets.Label(AggregationPeriod.Year, c.Start, c.End)));
    }

    [Fact]
    public void Weeks_StillStartOnTheAnchorWednesday_AndCoverTheLastDay()
    {
        var columns = PeriodBuckets.Columns(AggregationPeriod.Week, Period, Anchor);

        Assert.Equal(21, columns.Count);   // Jul 1 to the week of Nov 18-24
        Assert.Equal(Anchor, columns[0].Start);
        Assert.Equal(new DateOnly(2026, 11, 24), columns[^1].End);
    }

    [Fact]
    public void AMonthThatStartsMidMonth_IsStillAFullCalendarColumn()
    {
        var columns = PeriodBuckets.Columns(AggregationPeriod.Month, new DateRange(new DateOnly(2026, 7, 20), new DateOnly(2026, 8, 10)), Anchor);

        Assert.Equal(new DateOnly(2026, 7, 1), columns[0].Start);
        Assert.Equal(new DateOnly(2026, 7, 31), columns[0].End);
    }

    // ---- bucketing the daily records

    [Fact]
    public void Bucket_SumsTheStoredDaysOfEachMonth_AndCountsThem()
    {
        var period = new DateRange(new DateOnly(2026, 7, 20), new DateOnly(2026, 8, 10));
        var records = Enumerable.Range(0, 22)
            .Select(i => new DailySpendRecord
            {
                TenantId = "t1", VendorId = VendorCatalog.ClaudeEnterprise.Id, Date = period.Start.AddDays(i),
                SeatFee = 4m, UsageOrOverage = 6m, GrossSpend = 10m,
            })
            .ToList();

        var buckets = PeriodBuckets.Bucket(records, AggregationPeriod.Month, period, Anchor);

        Assert.Equal([120m, 100m], buckets.Select(b => b.GrossSpend));
        Assert.Equal([12, 10], buckets.Select(b => b.DaysPresent));
        Assert.Equal([0, 1], buckets.Select(b => b.Index));
    }

    [Fact]
    public void Bucket_LeavesOutAMonthWithNoRecord_SoItShowsADash()
    {
        var period = new DateRange(new DateOnly(2026, 7, 1), new DateOnly(2026, 9, 30));
        var records = new[]
        {
            new DailySpendRecord { TenantId = "t1", VendorId = VendorCatalog.ClaudeApiPlatform.Id, Date = new DateOnly(2026, 7, 5), UsageOrOverage = 5m, GrossSpend = 5m },
            new DailySpendRecord { TenantId = "t1", VendorId = VendorCatalog.ClaudeApiPlatform.Id, Date = new DateOnly(2026, 9, 5), UsageOrOverage = 7m, GrossSpend = 7m },
        };

        var buckets = PeriodBuckets.Bucket(records, AggregationPeriod.Month, period, Anchor);

        Assert.Equal([0, 2], buckets.Select(b => b.Index));   // August (1) has no record
    }

    // ---- the report

    [Fact]
    public void ByMonth_TheTableHasFiveColumns_NovemberIsMarkedPartial_AndTheRowsAddUp()
    {
        var html = Render(AggregationPeriod.Month, Claude(
            Month(0, new DateOnly(2026, 7, 1), new DateOnly(2026, 7, 31), 31, 100m),
            Month(1, new DateOnly(2026, 8, 1), new DateOnly(2026, 8, 31), 31, 200m),
            Month(2, new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 30), 30, 300m),
            Month(3, new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 31), 31, 400m),
            Month(4, new DateOnly(2026, 11, 1), new DateOnly(2026, 11, 21), 21, 500m)));

        var table = SpendTable(html);
        Assert.Contains("<h2>Monthly spend by vendor</h2>", html);
        foreach (var label in new[] { "Jul 2026", "Aug 2026", "Sep 2026", "Oct 2026", "Nov 2026*" })
        {
            Assert.Contains($">{label}</th>", table);
        }

        Assert.Equal(1, CountOf(table, "*</th>"));                  // only November is partial
        Assert.Contains("Total monthly spend", table);
        Assert.Contains("$1,500.00", table);                        // the vendor's and the tenant's total
        Assert.Contains("*Partial month", table);
        Assert.DoesNotContain("Wednesday", table);
    }

    [Theory]
    [InlineData(AggregationPeriod.Month, "a month with fewer days")]
    [InlineData(AggregationPeriod.Quarter, "a quarter with fewer days")]
    [InlineData(AggregationPeriod.Year, "a year with fewer days")]
    public void ByAMonthQuarterOrYear_ANoteExplainsWhyAShorterPeriodShowsALowerSeatFigure(AggregationPeriod aggregation, string phrase)
    {
        var html = Render(aggregation, Claude());

        var table = SpendTable(html);
        Assert.Contains("Seat charges are accrued daily", table);
        Assert.Contains("divided by the days in the year", table);
        Assert.Contains(phrase, table);
        Assert.Contains("each full month shows the same figure", table);
    }

    [Fact]
    public void ByWeek_ThereIsNoSeatAccrualNote()
    {
        var html = Render(AggregationPeriod.Week, Claude());

        Assert.DoesNotContain("Seat charges are accrued daily", SpendTable(html));
    }

    [Fact]
    public void ByMonth_AMonthWithNoRecord_IsADash_NotZero()
    {
        var html = Render(AggregationPeriod.Month, ClaudeApi(
            Month(0, new DateOnly(2026, 7, 1), new DateOnly(2026, 7, 31), 5, 50m),
            Month(4, new DateOnly(2026, 11, 1), new DateOnly(2026, 11, 21), 5, 70m)));

        var row = ApiRow(SpendTable(html));
        Assert.Equal(3, CountOf(row, "<span class=\"muted-dash\">—</span>"));   // Aug, Sep and Oct
    }

    [Fact]
    public void ByQuarter_TheTableHasQ3AndQ4()
    {
        var html = Render(AggregationPeriod.Quarter, Claude(
            Month(0, new DateOnly(2026, 7, 1), new DateOnly(2026, 9, 30), 92, 900m),
            Month(1, new DateOnly(2026, 10, 1), new DateOnly(2026, 12, 31), 52, 600m)));

        var table = SpendTable(html);
        Assert.Contains("<h2>Quarterly spend by vendor</h2>", html);
        Assert.Contains(">Q3 2026</th>", table);
        Assert.Contains(">Q4 2026*</th>", table);
        Assert.Contains("*Partial quarter", table);
    }

    [Fact]
    public void ByYear_AReportInsideOneYear_IsOnePartialColumnEqualToThePeriodTotal()
    {
        var html = Render(AggregationPeriod.Year, Claude(
            Month(0, new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31), 144, 1500m)));

        var table = SpendTable(html);
        Assert.Contains("<h2>Yearly spend by vendor</h2>", html);
        Assert.Contains(">2026*</th>", table);
        Assert.Equal(1, CountOf(table, "<th class=\"figure\" style=\"width:") - 1);   // one period column plus the Total column
    }

    [Fact]
    public void ByWeek_IsTheDefault_AndKeepsTheWeeklyWording()
    {
        var html = Render(AggregationPeriod.Week, Claude());

        Assert.Contains("<h2>Weekly spend by vendor</h2>", html);
        Assert.Contains("Total weekly spend", html);
        Assert.DoesNotContain("the projections stay weekly", html);
        Assert.Equal(AggregationPeriod.Week, new ReportData
        {
            Period = Period, WeekAnchor = Anchor, Tenants = [], GeneratedAt = DateTime.Now,
        }.Aggregation);
    }

    [Fact]
    public void TheProjections_AreIdentical_WhateverThePeriodThatTheTableUses()
    {
        var weeks = Enumerable.Range(0, 20)
            .Select(i => new WeeklyTotal(i, Anchor.AddDays(i * 7), Anchor.AddDays((i * 7) + 6), 10m, 100m + i, 110m + i, 7, false))
            .ToList();

        string Rows(AggregationPeriod period)
        {
            var html = ClientBenchmarkReportRenderer.Render(Data(period, Tenant("t1", new VendorReportData
            {
                Vendor = VendorCatalog.ClaudeEnterprise, Weeks = weeks, CurrentRates = [], SourceDayCounts = NoSources,
                DisplayPeriods = [Month(0, new DateOnly(2026, 7, 1), new DateOnly(2026, 7, 31), 31, 1m)],
            })));
            var start = html.IndexOf("<h2>Annualized cost projection</h2>", StringComparison.Ordinal);
            var section = html[start..html.IndexOf("</section>", start, StringComparison.Ordinal)];
            return section[section.IndexOf("<tbody>", StringComparison.Ordinal)..];
        }

        var byWeek = Rows(AggregationPeriod.Week);
        Assert.Equal(byWeek, Rows(AggregationPeriod.Month));
        Assert.Equal(byWeek, Rows(AggregationPeriod.Quarter));
        Assert.Equal(byWeek, Rows(AggregationPeriod.Year));
    }

    [Fact]
    public void TheManifest_IsTheSame_WhateverThePeriod()
    {
        var weeks = new[] { new WeeklyTotal(0, Anchor, Anchor.AddDays(6), 10m, 100m, 110m, 7, false) };
        ReportManifest ManifestFor(AggregationPeriod period) => ReportManifest.From(Data(period, Tenant("t1", new VendorReportData
        {
            Vendor = VendorCatalog.ClaudeEnterprise, Weeks = weeks, CurrentRates = [], SourceDayCounts = NoSources,
            DisplayPeriods = [Month(0, new DateOnly(2026, 7, 1), new DateOnly(2026, 7, 31), 31, 999m)],
        })));

        var week = JsonSerializer.Serialize(ManifestFor(AggregationPeriod.Week));
        Assert.Equal(week, JsonSerializer.Serialize(ManifestFor(AggregationPeriod.Month)));
        Assert.Equal(week, JsonSerializer.Serialize(ManifestFor(AggregationPeriod.Quarter)));
        Assert.Equal(week, JsonSerializer.Serialize(ManifestFor(AggregationPeriod.Year)));
    }

    [Fact]
    public void ByMonth_TheFootnoteSaysTheProjectionsStayWeekly()
    {
        var html = Render(AggregationPeriod.Month, Claude());

        var note = html[html.IndexOf("The spend table is grouped by month; the projections stay weekly", StringComparison.Ordinal)..];
        note = note[..note.IndexOf("</li>", StringComparison.Ordinal)];
        Assert.Contains("report-to-report comparison still use Wednesday-to-Tuesday weeks", note);
    }

    [Fact]
    public void ByMonth_ThePartialWeekFootnote_NoLongerPointsAtAWeeklyTable()
    {
        var html = Render(AggregationPeriod.Month, Claude());

        Assert.DoesNotContain("Partial weeks stay in the weekly table", html);
        Assert.Contains("Partial weeks stay in the actual totals.", html);
    }

    // ---- fixtures

    private static string Render(AggregationPeriod aggregation, VendorReportData vendor) =>
        ClientBenchmarkReportRenderer.Render(Data(aggregation, Tenant("t1", vendor)));

    private static ReportData Data(AggregationPeriod aggregation, TenantReportData tenant) => new()
    {
        Period = Period,
        WeekAnchor = Anchor,
        Aggregation = aggregation,
        Tenants = [tenant],
        GeneratedAt = new DateTime(2026, 11, 22, 12, 0, 0),
    };

    private static VendorReportData Claude(params PeriodTotal[] periods) => Vendor(VendorCatalog.ClaudeEnterprise, periods);

    private static VendorReportData ClaudeApi(params PeriodTotal[] periods) => Vendor(VendorCatalog.ClaudeApiPlatform, periods);

    private static VendorReportData Vendor(VendorIdentity vendor, PeriodTotal[] periods) => new()
    {
        Vendor = vendor,
        // The weeks are not what the table shows for a month, quarter or year; the totals still come from them.
        Weeks = periods.Length == 0
            ? []
            : [new WeeklyTotal(0, Anchor, Anchor.AddDays(6), 0m, periods.Sum(p => p.GrossSpend), periods.Sum(p => p.GrossSpend), 7, false)],
        CurrentRates = [],
        SourceDayCounts = NoSources,
        DisplayPeriods = periods,
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

    private static PeriodTotal Month(int index, DateOnly start, DateOnly end, int days, decimal gross) =>
        new(index, start, end, 0m, gross, gross, days);

    private static string SpendTable(string html)
    {
        var start = html.IndexOf("spend by vendor</h2>", StringComparison.Ordinal);
        Assert.True(start >= 0);
        return html[start..html.IndexOf("</section>", start, StringComparison.Ordinal)];
    }

    private static string ApiRow(string table)
    {
        var row = table.IndexOf("<th scope=\"row\">Claude API Platform</th>", StringComparison.Ordinal);
        Assert.True(row >= 0);
        return table[row..table.IndexOf("</tr>", row, StringComparison.Ordinal)];
    }

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
