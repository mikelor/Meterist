using System.Text.RegularExpressions;
using Meterist.Core.Models;
using Meterist.Core.Reporting;
using Meterist.Core.Vendors;

namespace Meterist.Core.Tests.Reporting;

/// <summary>
/// Issue #111: the report keeps the predictable part of the spend (seat licence charges, "committed") apart from the
/// variable part (usage and overage). The split moves figures between rows and columns, it never changes a total, so each
/// test also checks that committed plus variable is the gross figure.
/// </summary>
public class CommittedVariableReportTests
{
    private static readonly DateOnly Anchor = new(2026, 7, 1);   // a Wednesday: three full weeks to Jul 21
    private static readonly DateRange ThreeWeeks = new(Anchor, Anchor.AddDays(20));
    private static readonly IReadOnlyDictionary<string, int> NoSources = new Dictionary<string, int>();

    // ---- the period tables

    [Fact]
    public void TheWeeklyTable_VendorRowsAreVariable_TheCommittedRowHoldsTheSeats_AndTheTotalAddsThem()
    {
        var claude = Vendor(VendorCatalog.ClaudeEnterprise, Weeks([70m, 70m, 70m], [100m, 100m, 100m]));
        var api = Vendor(VendorCatalog.ClaudeApiPlatform, Weeks([0m, 0m, 0m], [50m, 50m, 50m]));
        var table = Section(Render(ThreeWeeks, Tenant(claude, api)), "Weekly spend by vendor");

        Assert.Equal(["$100.00", "$100.00", "$100.00", "$300.00"], RowFigures(table, "Claude Enterprise"));   // variable only
        Assert.Equal(["$50.00", "$50.00", "$50.00", "$150.00"], RowFigures(table, "Claude API Platform"));
        Assert.Equal(["$150.00", "$150.00", "$150.00", "$450.00"], RowFigures(table, "Total variable spend"));
        Assert.Equal(["$70.00", "$70.00", "$70.00", "$210.00"], RowFigures(table, "Committed (seat licences)"));
        Assert.Equal(["$220.00", "$220.00", "$220.00", "$660.00"], RowFigures(table, "Total weekly spend"));   // 150 + 70, per column
        Assert.DoesNotContain("seat $", table);   // no more "seat + usage" captions in the cells
    }

    [Fact]
    public void TheMonthlyTable_KeepsTheSameSplit()
    {
        var period = new DateRange(new DateOnly(2026, 7, 1), new DateOnly(2026, 8, 31));
        var claude = new VendorReportData
        {
            Vendor = VendorCatalog.ClaudeEnterprise,
            Weeks = Weeks([62m], [800m]),   // the same spend as the two months below, so the Total column agrees
            CurrentRates = [],
            SourceDayCounts = NoSources,
            DisplayPeriods =
            [
                new PeriodTotal(0, new DateOnly(2026, 7, 1), new DateOnly(2026, 7, 31), 31m, 500m, 531m, 31),
                new PeriodTotal(1, new DateOnly(2026, 8, 1), new DateOnly(2026, 8, 31), 31m, 300m, 331m, 31),
            ],
        };
        var html = ClientBenchmarkReportRenderer.Render(new ReportData
        {
            Period = period,
            WeekAnchor = Anchor,
            Aggregation = AggregationPeriod.Month,
            Tenants = [Tenant(claude)],
            GeneratedAt = new DateTime(2026, 11, 22, 12, 0, 0),
        });
        var table = Section(html, "Monthly spend by vendor");

        Assert.Equal(["$500.00", "$300.00", "$800.00"], RowFigures(table, "Claude Enterprise"));
        Assert.Equal(["$31.00", "$31.00", "$62.00"], RowFigures(table, "Committed (seat licences)"));
        Assert.Equal(["$531.00", "$331.00", "$862.00"], RowFigures(table, "Total monthly spend"));
    }

    // ---- the headline and the ledger

    [Fact]
    public void TheHeadlineAndTheLedger_NameTheCommittedAndVariableParts()
    {
        var claude = Vendor(VendorCatalog.ClaudeEnterprise, Weeks([70m, 70m, 70m], [100m, 100m, 100m]));
        var html = Render(ThreeWeeks, Tenant(claude));

        Assert.Contains("Committed $210.00 (seat licences) · variable $300.00 (59% variable)", html);
        Assert.Contains("committed $210.00 + variable $300.00 (59% variable)", Section(html, "The vendor ledger"));
    }

    // ---- the committed charges and contract terms

    [Fact]
    public void TheRateCard_ShowsSeatHistory_AnnualCommittedCost_AndTheCommittedInThePeriod()
    {
        var first = SeatRate(VendorCatalog.ClaudeEnterprise, 20, 240m, new DateOnly(2026, 3, 29));
        var second = SeatRate(VendorCatalog.ClaudeEnterprise, 24, 240m, new DateOnly(2026, 7, 5));
        var claude = new VendorReportData
        {
            Vendor = VendorCatalog.ClaudeEnterprise,
            Weeks = Weeks([70m, 70m, 70m], [100m, 100m, 100m]),
            CurrentRates = [second],
            SeatRateAtPeriodEnd = second,
            SeatRateHistory = [first, second],
            SourceDayCounts = NoSources,
        };
        var card = Section(Render(ThreeWeeks, Tenant(claude)), "Committed charges and contract terms");

        Assert.Contains("per-seat · seat · $240.00 · Annual · 24 seats · since Jul 5, 2026", card);
        Assert.Contains("Seat history: 20 seats from Mar 29, 2026, 24 seats from Jul 5, 2026", card);
        var cells = RowFigures(card, "Claude Enterprise (t1)");
        Assert.Equal(["$5,760.00", "$210.00"], cells);   // 24 x $240 a year; the seat charges in the period
    }

    [Fact]
    public void TheRateCard_ShowsAGeminiSeatChargeAsObserved_NotAsAContractRate()
    {
        var gemini = new VendorReportData
        {
            Vendor = VendorCatalog.GeminiEnterprise,
            Weeks = Weeks([140m, 140m, 140m], [20m, 20m, 20m]),
            CurrentRates = [],
            SourceDayCounts = NoSources,
            Breakdown = [new BreakdownCategoryTotal(SpendCategories.Seat, true, 420m, 21, [])],
        };
        var card = Section(Render(ThreeWeeks, Tenant(gemini)), "Committed charges and contract terms");

        Assert.Contains("observed in the vendor's billing export, not a contract rate: $420.00 over 21 day(s), about $20.00 a day", card);
        Assert.Equal(["$420.00"], RowFigures(card, "Gemini Enterprise (t1)"));   // no annual figure without a contract; the period total
    }

    [Fact]
    public void TheRateCard_SaysAUsagePricedVendorHasNoSeatCharge_AndASeatPricedOneHasNoRateYet()
    {
        var api = Vendor(VendorCatalog.ClaudeApiPlatform, Weeks([0m, 0m, 0m], [50m, 50m, 50m]));
        var chatGpt = Vendor(VendorCatalog.ChatGptEnterprise, Weeks([0m, 0m, 0m], [50m, 50m, 50m]));
        var card = Section(Render(ThreeWeeks, Tenant(api, chatGpt)), "Committed charges and contract terms");

        Assert.Contains("No seat charge — usage only.", card);
        Assert.Contains("No seat rate configured", card);
    }

    // ---- fixtures

    private static string Render(DateRange period, TenantReportData tenant) =>
        ClientBenchmarkReportRenderer.Render(new ReportData
        {
            Period = period,
            WeekAnchor = Anchor,
            Tenants = [tenant],
            GeneratedAt = new DateTime(2026, 11, 22, 12, 0, 0),
        });

    private static List<WeeklyTotal> Weeks(decimal[] seat, decimal[] usage) =>
        seat.Select((s, i) =>
        {
            var start = Anchor.AddDays(i * 7);
            return new WeeklyTotal(i, start, start.AddDays(6), s, usage[i], s + usage[i], 7, false);
        }).ToList();

    private static VendorReportData Vendor(VendorIdentity vendor, IReadOnlyList<WeeklyTotal> weeks) => new()
    {
        Vendor = vendor,
        Weeks = weeks,
        CurrentRates = [],
        SourceDayCounts = NoSources,
    };

    private static VendorRateConfig SeatRate(VendorIdentity vendor, int seats, decimal rate, DateOnly from) => new()
    {
        VendorId = vendor.Id,
        RateType = "per-seat",
        ModelOrSku = ProjectionCalculator.SeatModelOrSku,
        Rate = rate,
        SeatCount = seats,
        BillingCadence = BillingCadence.Annual,
        EffectiveFrom = from,
    };

    // Every catalog vendor for the tenant (the renderer looks each up), with the given ones carrying data.
    private static TenantReportData Tenant(params VendorReportData[] vendors) => new()
    {
        TenantId = "t1",
        Vendors = VendorCatalog.All
            .Select(v => vendors.FirstOrDefault(x => x.Vendor.Id == v.Id)
                ?? new VendorReportData { Vendor = v, Weeks = [], CurrentRates = [], SourceDayCounts = NoSources })
            .ToList(),
    };

    private static string Section(string html, string heading)
    {
        var h = html.IndexOf($"<h2>{heading}</h2>", StringComparison.Ordinal);
        Assert.True(h >= 0, $"The report has no '{heading}' section.");
        return html[h..html.IndexOf("</section>", h, StringComparison.Ordinal)];
    }

    // The plain figures in one table row, found by its row header: the cells that hold text and no markup.
    private static string[] RowFigures(string section, string rowHeader)
    {
        var start = section.IndexOf($"<th scope=\"row\">{rowHeader}</th>", StringComparison.Ordinal);
        Assert.True(start >= 0, $"No row '{rowHeader}'.");
        var row = section[start..section.IndexOf("</tr>", start, StringComparison.Ordinal)];
        return Regex.Matches(row, "<td class=\"figure tabular\">([^<]*)</td>")
            .Select(m => m.Groups[1].Value.Trim())
            .ToArray();
    }
}
