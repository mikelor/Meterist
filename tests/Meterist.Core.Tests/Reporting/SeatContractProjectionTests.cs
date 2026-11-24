using Meterist.Core.Models;
using Meterist.Core.Reporting;
using Meterist.Core.Vendors;

namespace Meterist.Core.Tests.Reporting;

/// <summary>
/// Issue #101: the seat part of the projections comes from the seat rate in force on the report's last day, not from past
/// weekly seat fees, which lag every seat change. Expected values are explicit dollars, not "same as the weekly method":
/// the weekly method annualises with 52 weeks (364 days), so it projects a constant $1,200 a year as $1,196.71.
/// </summary>
public class SeatContractProjectionTests
{
    private static readonly DateOnly Anchor = new(2026, 7, 1);   // a Wednesday: three full weeks to Jul 21
    private static readonly IReadOnlyDictionary<string, int> NoSources = new Dictionary<string, int>();

    // ---- the contract arithmetic

    [Theory]
    [InlineData(BillingCadence.Annual, 14, 365, 5110)]
    [InlineData(BillingCadence.Monthly, 3, 20, 720)]
    public void TheAnnualSeatCost_IsSeatCountTimesRate_AndTimesTwelveForAMonthlyRate(
        BillingCadence cadence, int seats, int rate, int expected)
    {
        var annual = ProjectionCalculator.AnnualSeatCostFromContract(Rate(seats, rate, cadence));

        Assert.Equal((decimal)expected, annual);
    }

    [Fact]
    public void AMissingRate_ASeatlessRate_OrAOneTimeRate_CannotBeAnnualised()
    {
        Assert.Null(ProjectionCalculator.AnnualSeatCostFromContract(null));
        Assert.Null(ProjectionCalculator.AnnualSeatCostFromContract(Rate(null, 100, BillingCadence.Annual)));
        Assert.Null(ProjectionCalculator.AnnualSeatCostFromContract(Rate(5, 100, BillingCadence.OneTime)));
        Assert.Null(ProjectionCalculator.AnnualSeatCostFromContract(Rate(5, 100, null)));
    }

    // ---- the report

    [Theory]
    [InlineData(2026, 7, 1, 365)]
    [InlineData(2028, 7, 5, 366)]   // a leap year: Jul 5 2028 is a Wednesday
    public void AConstantAnnualSeatCharge_ProjectsToTheContractPrice_InFlatTrendAndLatest(int y, int m, int d, int daysInYear)
    {
        var anchor = new DateOnly(y, m, d);
        var weekly = 1200m / daysInYear * 7;   // what the accrual stores each week
        var html = Render(anchor, Claude(anchor, [weekly, weekly, weekly], [0m, 0m, 0m], Rate(1, 1200, BillingCadence.Annual)));

        var cells = Cells(VendorRow(html));
        Assert.Equal("$1,200.00", cells[0]);                                   // committed
        Assert.Equal(["$0.00", "$0.00", "$0.00"], cells[1..4]);                // variable: Flat, Trend-adjusted, Latest week
        Assert.DoesNotContain("$1,196.71", html);   // the weekly method's answer in a normal year
        Assert.DoesNotContain("$1,193.44", html);   // and in a leap year
    }

    [Fact]
    public void ASeatStep_ProjectsTheNewSeatCount_NotTheLaggingWeeklyAverage()
    {
        // 14 seats at $365 a year = $5,110. The weeks still reflect 10, 11 and 13 seats (70, 77, 91 seat-days at $1).
        var html = Render(Anchor, Claude(Anchor, [70m, 77m, 91m], [100m, 100m, 100m], Rate(14, 365, BillingCadence.Annual)));

        var row = VendorRow(html);
        var cells = Cells(row);
        Assert.Equal("$5,110.00", cells[0]);
        Assert.Equal(["$5,200.00", "$5,200.00", "$5,200.00"], cells[1..4]);
        Assert.DoesNotContain("$12,376.00", row);   // the weekly seat average, (70+77+91)/3 x 52, is not used
    }

    [Fact]
    public void ARenewalRow_ProjectsTheRenewedTerms()
    {
        // The rate the aggregator hands over is the one in force on the last day, so a renewal is just the new row.
        var html = Render(Anchor, Claude(Anchor, [50m, 50m, 50m], [0m, 0m, 0m], Rate(20, 300, BillingCadence.Annual)));

        Assert.Equal("$6,000.00", Cells(VendorRow(html))[0]);
    }

    [Fact]
    public void AMonthlyRate_ProjectsSeatsTimesRateTimesTwelve()
    {
        var html = Render(Anchor, Claude(Anchor, [10m, 10m, 10m], [0m, 0m, 0m], Rate(3, 20, BillingCadence.Monthly)));

        Assert.Equal("$720.00", Cells(VendorRow(html))[0]);
    }

    [Fact]
    public void WithNoRate_TheSeatPartStaysTheWeeklyProjection() => AssertWeeklySeatProjection(null);

    [Fact]
    public void WithARateThatHasNoSeatCount_TheSeatPartStaysTheWeeklyProjection() =>
        AssertWeeklySeatProjection(Rate(null, 100, BillingCadence.Annual));

    [Fact]
    public void WithAOneTimeRate_TheSeatPartStaysTheWeeklyProjection() =>
        AssertWeeklySeatProjection(Rate(5, 100, BillingCadence.OneTime));

    private static void AssertWeeklySeatProjection(VendorRateConfig? rate)
    {
        // Seat fee 70 a week x 52 = $3,640.00 committed, exactly as before.
        var html = Render(Anchor, Claude(Anchor, [70m, 70m, 70m], [100m, 100m, 100m], rate));

        var cells = Cells(VendorRow(html));
        Assert.Equal("$3,640.00", cells[0]);
        Assert.Equal(["$5,200.00", "$5,200.00", "$5,200.00"], cells[1..4]);
        Assert.DoesNotContain("The committed part of the projections comes from the contract", html);
    }

    [Fact]
    public void TheUsagePart_IsStillProjectedFromTheWeeklySeries_WhenTheContractSuppliesTheSeatPart()
    {
        // Usage 100, 120, 140: flat 120 x 52 = $6,240.00; trend (next point 160) x 52 = $8,320.00; latest 140 x 52 = $7,280.00.
        var html = Render(Anchor, Claude(Anchor, [1m, 1m, 1m], [100m, 120m, 140m], Rate(1, 365, BillingCadence.Annual)));

        var cells = Cells(VendorRow(html));
        Assert.Equal("$365.00", cells[0]);
        Assert.Equal(["$6,240.00", "$8,320.00", "$7,280.00"], cells[1..4]);
    }

    [Fact]
    public void TheFootnote_SaysTheCommittedPartIsFromTheContract_AndNamesTheDateAndTheCorrection()
    {
        var html = Render(Anchor, Claude(Anchor, [70m, 70m, 70m], [0m, 0m, 0m], Rate(1, 365, BillingCadence.Annual)));

        var note = html[html.IndexOf("The committed part of the projections comes from the contract", StringComparison.Ordinal)..];
        note = note[..note.IndexOf("</li>", StringComparison.Ordinal)];
        Assert.Contains("rate in force on Jul 21, 2026", note);
        Assert.Contains("364/365", note);
        Assert.Contains("t1: Claude Enterprise", note);
    }

    [Fact]
    public void AVendorWithNoWeekThatFeedsTheProjection_IsNotNamedInTheFootnote()
    {
        // A one-day report has no complete week, so there is no projection for the contract to feed.
        var period = new DateRange(Anchor, Anchor);
        var week = new WeeklyTotal(0, Anchor, Anchor.AddDays(6), 1m, 0m, 1m, 1, false);
        var vendor = new VendorReportData
        {
            Vendor = VendorCatalog.ClaudeEnterprise,
            Weeks = [week],
            CurrentRates = [],
            SourceDayCounts = NoSources,
            SeatRateAtPeriodEnd = Rate(1, 365, BillingCadence.Annual),
        };

        var html = ClientBenchmarkReportRenderer.Render(Report(period, Tenant("t1", vendor)));

        Assert.DoesNotContain("The committed part of the projections comes from the contract", html);
    }

    // ---- fixtures

    private static string Render(DateOnly anchor, VendorReportData claude) =>
        ClientBenchmarkReportRenderer.Render(Report(new DateRange(anchor, anchor.AddDays(20)), Tenant("t1", claude), anchor));

    private static VendorReportData Claude(
        DateOnly anchor, decimal[] seat, decimal[] usage, VendorRateConfig? rate) => new()
    {
        Vendor = VendorCatalog.ClaudeEnterprise,
        Weeks = seat.Select((s, i) =>
        {
            var start = anchor.AddDays(i * 7);
            return new WeeklyTotal(i, start, start.AddDays(6), s, usage[i], s + usage[i], 7, false);
        }).ToList(),
        CurrentRates = [],
        SourceDayCounts = NoSources,
        SeatRateAtPeriodEnd = rate,
    };

    private static VendorRateConfig Rate(int? seats, int rate, BillingCadence? cadence) => new()
    {
        VendorId = VendorCatalog.ClaudeEnterprise.Id,
        RateType = "per-seat",
        ModelOrSku = ProjectionCalculator.SeatModelOrSku,
        Rate = rate,
        SeatCount = seats,
        BillingCadence = cadence,
        EffectiveFrom = new DateOnly(2026, 1, 1),
    };

    private static ReportData Report(DateRange period, TenantReportData tenant, DateOnly? anchor = null) => new()
    {
        Period = period,
        WeekAnchor = anchor ?? Anchor,
        Tenants = [tenant],
        GeneratedAt = new DateTime(2026, 11, 22, 12, 0, 0),
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

    private static string VendorRow(string html)
    {
        var start = html.IndexOf("<h2>Annualized cost projection</h2>", StringComparison.Ordinal);
        Assert.True(start >= 0);
        var section = html[start..html.IndexOf("</section>", start, StringComparison.Ordinal)];
        var row = section.IndexOf("<th scope=\"row\">Claude Enterprise</th>", StringComparison.Ordinal);
        Assert.True(row >= 0);
        return section[row..section.IndexOf("</tr>", row, StringComparison.Ordinal)];
    }

    // The figures in a projection row: committed per year, then the variable Flat, Trend-adjusted and Latest week, then the actual.
    private static string[] Cells(string row) =>
        System.Text.RegularExpressions.Regex.Matches(row, "<td class=\"figure tabular\">([^<]*)</td>")
            .Select(m => m.Groups[1].Value.Trim())
            .ToArray();
}
