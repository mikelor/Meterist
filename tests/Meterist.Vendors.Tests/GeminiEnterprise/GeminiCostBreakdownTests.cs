using Meterist.Vendors.GeminiEnterprise;

namespace Meterist.Vendors.Tests.GeminiEnterprise;

public class GeminiCostBreakdownTests
{
    [Theory]
    [InlineData("Gemini Enterprise Plus: Subscription - one month term", GeminiCostCategory.Seat)]
    [InlineData("Gemini Enterprise Plus Overage", GeminiCostCategory.EnterpriseOverage)]
    [InlineData("Agent Platform Compute", GeminiCostCategory.AgentPlatform)]
    [InlineData("Agent Platform Memory", GeminiCostCategory.AgentPlatform)]
    [InlineData("Gemini 2.5 Flash Text Input - Predictions", GeminiCostCategory.ModelUsage)]
    [InlineData("Usage above quota - Pay as you go", GeminiCostCategory.PayAsYouGo)]
    [InlineData("Usage above quota - pay-as-you-go", GeminiCostCategory.PayAsYouGo)]
    [InlineData("Something new", GeminiCostCategory.Other)]
    [InlineData(null, GeminiCostCategory.Other)]
    public void Classify_MapsSkuTextToCategory(string? sku, GeminiCostCategory expected) =>
        Assert.Equal(expected, GeminiCostBreakdown.Classify(sku));

    [Fact]
    public void Summarize_TotalsPerCategory_CountsDaysWithCharge_AndListsSkus()
    {
        var day1 = """
            [{"SkuDescription":"Gemini Enterprise Plus: Subscription","Cost":100.5},
             {"SkuDescription":"Agent Platform Compute","Cost":0.02},
             {"SkuDescription":"Agent Platform Memory","Cost":0.01}]
            """;
        var day2 = """
            [{"SkuDescription":"Gemini Enterprise Plus: Subscription","Cost":100.5},
             {"SkuDescription":"Gemini Flash Text Input - Predictions","Cost":3.25},
             {"SkuDescription":"Agent Platform Compute","Cost":0}]
            """;

        var totals = GeminiCostBreakdown.Summarize([day1, day2]);

        Assert.Equal(
            [GeminiCostCategory.Seat, GeminiCostCategory.AgentPlatform, GeminiCostCategory.ModelUsage],
            totals.Select(t => t.Category));

        var seat = totals[0];
        Assert.Equal(201.0m, seat.Cost);
        Assert.Equal(2, seat.Days);

        var agent = totals[1];
        Assert.Equal(0.03m, agent.Cost);
        Assert.Equal(1, agent.Days);
        Assert.Equal(["Agent Platform Compute", "Agent Platform Memory"], agent.Skus);

        Assert.Equal(1, totals[2].Days);
    }

    [Fact]
    public void Summarize_NoRowsOrAllZero_ReturnsNothing()
    {
        Assert.Empty(GeminiCostBreakdown.Summarize([]));
        Assert.Empty(GeminiCostBreakdown.Summarize(["""[{"SkuDescription":"Agent Platform Compute","Cost":0}]"""]));
    }

    [Fact]
    public void Summarize_ReadsTheKeysTheRawRepositoryActuallyWrites()
    {
        // Pins the stored raw-row key names so a rename in GeminiBillingRowFields cannot silently
        // turn every category into Other/zero.
        var row = new Dictionary<string, object?>
        {
            [GeminiBillingRowFields.SkuDescription] = "Agent Platform Compute",
            [GeminiBillingRowFields.Cost] = 1.25m,
        };
        var json = System.Text.Json.JsonSerializer.Serialize(new[] { row });

        var total = Assert.Single(GeminiCostBreakdown.Summarize([json]));

        Assert.Equal(GeminiCostCategory.AgentPlatform, total.Category);
        Assert.Equal(1.25m, total.Cost);
    }
}
