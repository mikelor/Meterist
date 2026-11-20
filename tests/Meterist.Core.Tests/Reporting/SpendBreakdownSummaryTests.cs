using Meterist.Core.Models;
using Meterist.Core.Reporting;

namespace Meterist.Core.Tests.Reporting;

public class SpendBreakdownSummaryTests
{
    private static readonly DateOnly D1 = new(2026, 11, 1);
    private static readonly DateOnly D2 = new(2026, 11, 2);

    [Fact]
    public void Summarize_TotalsEachCategory_CountsDaysWithAChargeAndListsTheLines()
    {
        var totals = SpendBreakdownSummary.Summarize(
        [
            Row(D1, "Seat", 10m, "Plus: Subscription"),
            Row(D2, "Seat", 10m, "Plus: Subscription"),
            Row(D1, "AgentPlatform", 0.02m, "Compute"),
            Row(D1, "AgentPlatform", 0.01m, "Memory"),
            Row(D2, "AgentPlatform", 0.03m, "Compute"),
        ]);

        var seat = totals.Single(t => t.Category == "Seat");
        Assert.Equal(20m, seat.Amount);
        Assert.Equal(2, seat.Days);
        Assert.Equal(["Plus: Subscription"], seat.Details);

        var agent = totals.Single(t => t.Category == "AgentPlatform");
        Assert.Equal(0.06m, agent.Amount);
        Assert.Equal(2, agent.Days);
        Assert.Equal(["Compute", "Memory"], agent.Details);
    }

    [Fact]
    public void Summarize_OrdersUsageFirstWithTheSeatOnTop_ThenNonUsageByAmount()
    {
        var totals = SpendBreakdownSummary.Summarize(
        [
            Row(D1, "Tax", 5m, isUsage: false),
            Row(D1, "ModelUsage", 3m),
            Row(D1, "Support", 9m, isUsage: false),
            Row(D1, "Seat", 1m),
            Row(D1, "AgentPlatform", 4m),
        ]);

        Assert.Equal(["Seat", "AgentPlatform", "ModelUsage", "Support", "Tax"], totals.Select(t => t.Category));
        Assert.Equal([true, true, true, false, false], totals.Select(t => t.IsUsage));
    }

    [Fact]
    public void Summarize_KeepsAUsageAndANonUsageCategoryOfTheSameNameApart()
    {
        var totals = SpendBreakdownSummary.Summarize([Row(D1, "Other", 1m), Row(D1, "Other", 2m, isUsage: false)]);

        Assert.Equal(2, totals.Count);
        Assert.Equal(1m, totals.Single(t => t.IsUsage).Amount);
        Assert.Equal(2m, totals.Single(t => !t.IsUsage).Amount);
    }

    [Fact]
    public void Summarize_LeavesOutACategoryThatNetsToZero_AndHandlesNoRows()
    {
        Assert.Empty(SpendBreakdownSummary.Summarize([Row(D1, "Seat", 1m), Row(D2, "Seat", -1m)]));
        Assert.Empty(SpendBreakdownSummary.Summarize([]));
    }

    private static DailySpendBreakdownRecord Row(
        DateOnly date, string category, decimal amount, string detail = "", bool isUsage = true) => new()
    {
        TenantId = "t1",
        VendorId = Guid.NewGuid(),
        Date = date,
        Category = category,
        Detail = detail,
        Amount = amount,
        IsUsage = isUsage,
    };
}
