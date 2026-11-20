using System.Text.Json;

namespace Meterist.Vendors.GeminiEnterprise;

/// <summary>
/// Which kind of Gemini billing line a SKU is. The stored <c>DailySpendRecord</c> only separates
/// the seat fee from "everything else"; this finer split is derived from the SKU text in the raw
/// extraction rows, for reporting only (a stored column is tracked as a later issue).
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

/// <summary>Total for one category over a period.</summary>
public sealed record GeminiCategoryTotal(GeminiCostCategory Category, decimal Cost, int Days, IReadOnlyList<string> Skus);

/// <summary>Splits stored raw Gemini rows into <see cref="GeminiCostCategory"/> totals.</summary>
public static class GeminiCostBreakdown
{
    public static GeminiCostCategory Classify(string? skuDescription)
    {
        var sku = skuDescription ?? string.Empty;

        // Subscription is checked first because the normalizer decides SeatFee the same way.
        if (Has(sku, "Subscription")) return GeminiCostCategory.Seat;
        if (Has(sku, "Agent Platform")) return GeminiCostCategory.AgentPlatform;
        if (Has(sku, "Predictions")) return GeminiCostCategory.ModelUsage;
        if (Has(sku, "Pay as you go") || Has(sku, "Pay-as-you-go")) return GeminiCostCategory.PayAsYouGo;
        if (Has(sku, "Enterprise")) return GeminiCostCategory.EnterpriseOverage;
        return GeminiCostCategory.Other;
    }

    /// <summary>
    /// Totals per category from each day's stored raw-rows JSON. Only categories with a
    /// non-zero total are returned, in enum order. Cost is the gross line cost, before credits.
    /// </summary>
    public static IReadOnlyList<GeminiCategoryTotal> Summarize(IEnumerable<string> recordsJsonPerDay)
    {
        var cost = new Dictionary<GeminiCostCategory, decimal>();
        var days = new Dictionary<GeminiCostCategory, int>();
        var skus = new Dictionary<GeminiCostCategory, SortedSet<string>>();

        foreach (var json in recordsJsonPerDay)
        {
            var seenToday = new HashSet<GeminiCostCategory>();
            using var document = JsonDocument.Parse(json);

            foreach (var row in document.RootElement.EnumerateArray())
            {
                var sku = row.TryGetProperty(GeminiBillingRowFields.SkuDescription, out var s) && s.ValueKind == JsonValueKind.String
                    ? s.GetString() ?? string.Empty
                    : string.Empty;
                var amount = row.TryGetProperty(GeminiBillingRowFields.Cost, out var c) && c.ValueKind == JsonValueKind.Number
                    ? c.GetDecimal()
                    : 0m;

                var category = Classify(sku);
                cost[category] = cost.GetValueOrDefault(category) + amount;

                if (!skus.TryGetValue(category, out var set))
                {
                    skus[category] = set = [];
                }

                set.Add(sku);

                if (amount != 0m && seenToday.Add(category))
                {
                    days[category] = days.GetValueOrDefault(category) + 1;
                }
            }
        }

        return Enum.GetValues<GeminiCostCategory>()
            .Where(cost.ContainsKey)
            .Where(category => cost[category] != 0m)
            .Select(category => new GeminiCategoryTotal(
                category, cost[category], days.GetValueOrDefault(category), [.. skus[category]]))
            .ToList();
    }

    private static bool Has(string sku, string term) => sku.Contains(term, StringComparison.OrdinalIgnoreCase);
}
