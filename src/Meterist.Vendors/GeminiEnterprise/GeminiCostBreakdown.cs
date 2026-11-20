namespace Meterist.Vendors.GeminiEnterprise;

/// <summary>
/// Which kind of Gemini billing line a SKU is, worked out from its text. This is only the fallback for a
/// billing rule that names no category (and for raw rows stored before categories existed): the real
/// classification is the configured <see cref="GeminiBillingRule.Category"/>.
/// </summary>
public enum GeminiCostCategory
{
    /// <summary>"... Subscription ..." SKUs: the seat licence (stored as SeatFee).</summary>
    Seat,

    /// <summary>Enterprise SKUs that are not a subscription, i.e. per-request overage.</summary>
    EnterpriseOverage,

    /// <summary>Agent Platform compute and memory.</summary>
    AgentPlatform,

    /// <summary>Raw Gemini model token usage ("Predictions" SKUs).</summary>
    ModelUsage,

    /// <summary>Pay-as-you-go usage above the edition quota.</summary>
    PayAsYouGo,

    /// <summary>Matched an Include rule but none of the above; look at the SKU.</summary>
    Other,
}

public static class GeminiCostBreakdown
{
    public static GeminiCostCategory Classify(string? skuDescription)
    {
        var sku = skuDescription ?? string.Empty;

        // Subscription is checked first because it also contains "Enterprise", and a seat fee must not be
        // mistaken for overage.
        if (Has(sku, "Subscription")) return GeminiCostCategory.Seat;
        if (Has(sku, "Agent Platform")) return GeminiCostCategory.AgentPlatform;
        if (Has(sku, "Predictions")) return GeminiCostCategory.ModelUsage;
        if (Has(sku, "Pay as you go") || Has(sku, "Pay-as-you-go")) return GeminiCostCategory.PayAsYouGo;
        if (Has(sku, "Enterprise")) return GeminiCostCategory.EnterpriseOverage;
        return GeminiCostCategory.Other;
    }

    /// <summary>
    /// The category label for a raw row: the configured one when the row carries it, otherwise the SKU-text
    /// classification. The labels match the default configuration's, so a row classified either way is
    /// stored under the same name.
    /// </summary>
    public static string CategoryOf(IReadOnlyDictionary<string, object?> row)
    {
        if (row.TryGetValue(GeminiBillingRowFields.Category, out var category)
            && category is string configured
            && !string.IsNullOrWhiteSpace(configured))
        {
            return configured;
        }

        row.TryGetValue(GeminiBillingRowFields.SkuDescription, out var sku);
        return Classify(sku as string).ToString();
    }

    /// <summary>False only for a row explicitly marked as non-usage; a row with no marker is usage.</summary>
    public static bool IsUsage(IReadOnlyDictionary<string, object?> row) =>
        !row.TryGetValue(GeminiBillingRowFields.IsUsage, out var isUsage) || isUsage is not false;

    private static bool Has(string sku, string term) => sku.Contains(term, StringComparison.OrdinalIgnoreCase);
}
