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
    public void CategoryOf_PrefersTheConfiguredCategory()
    {
        // The SKU text says Subscription, but the rule that matched said Tax: the rule wins.
        var row = new Dictionary<string, object?>
        {
            [GeminiBillingRowFields.SkuDescription] = "Gemini Enterprise Plus: Subscription",
            [GeminiBillingRowFields.Category] = "Tax",
        };

        Assert.Equal("Tax", GeminiCostBreakdown.CategoryOf(row));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void CategoryOf_FallsBackToTheSkuText_WhenTheRuleNamedNone(string? category)
    {
        var row = new Dictionary<string, object?>
        {
            [GeminiBillingRowFields.SkuDescription] = "Agent Platform Compute",
            [GeminiBillingRowFields.Category] = category,
        };

        Assert.Equal("AgentPlatform", GeminiCostBreakdown.CategoryOf(row));
    }

    [Fact]
    public void CategoryOf_ARowStoredBeforeCategoriesExisted_IsClassifiedFromItsSku()
    {
        // No Category key at all, as in every raw row stored before this change.
        var row = new Dictionary<string, object?>
        {
            [GeminiBillingRowFields.SkuDescription] = "Gemini Enterprise Plus: Subscription - one month term",
        };

        Assert.Equal("Seat", GeminiCostBreakdown.CategoryOf(row));
    }

    [Fact]
    public void DefaultConfigurationLabels_AreTheSameStringsTheFallbackProduces()
    {
        // A row classified from its SKU and one classified by the default rules must land under one name.
        var fromConfig = GeminiBillingFilter.Default.Include.Select(r => r.Category!).Distinct();
        var fromFallback = Enum.GetNames<GeminiCostCategory>();

        Assert.All(fromConfig, label => Assert.Contains(label, fromFallback));
    }

    [Fact]
    public void IsUsage_IsFalseOnlyForARowExplicitlyMarkedNonUsage()
    {
        Assert.True(GeminiCostBreakdown.IsUsage(new Dictionary<string, object?>()));
        Assert.True(GeminiCostBreakdown.IsUsage(new Dictionary<string, object?> { [GeminiBillingRowFields.IsUsage] = true }));
        Assert.True(GeminiCostBreakdown.IsUsage(new Dictionary<string, object?> { [GeminiBillingRowFields.IsUsage] = null }));
        Assert.False(GeminiCostBreakdown.IsUsage(new Dictionary<string, object?> { [GeminiBillingRowFields.IsUsage] = false }));
    }
}
