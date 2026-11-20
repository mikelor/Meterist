namespace Meterist.Vendors.GeminiEnterprise;

/// <summary>
/// One billing-export line the adapter counts as Gemini Enterprise spend (or, in
/// <see cref="GeminiBillingFilter.Ignore"/>, one it knowingly leaves out): a service
/// name matched exactly and a SKU description matched with a SQL <c>LIKE</c> pattern.
/// </summary>
public sealed class GeminiBillingRule
{
    /// <summary>Exact <c>service.description</c>, e.g. "Vertex AI Search".</summary>
    public string Service { get; set; } = string.Empty;

    /// <summary>SQL <c>LIKE</c> pattern for <c>sku.description</c>, e.g. "%Enterprise%".</summary>
    public string SkuLike { get; set; } = "%";
}

/// <summary>
/// Which billing-export lines count as Gemini Enterprise spend, and which excluded lines
/// should be flagged. Configuration rather than code because Google keeps changing how
/// this product bills (a new quota and pay-as-you-go model from 2026-08-17, new Agent
/// Platform SKUs from July and September): a new SKU should be a settings change, and a
/// SKU we do not yet count should be noticed rather than silently dropped.
/// <para>
/// Bound from the <c>Meterist:Gemini:BillingFilter</c> configuration section; when that
/// section is absent, <see cref="Default"/> applies.
/// </para>
/// </summary>
public sealed class GeminiBillingFilter
{
    public const string ConfigurationSection = "Meterist:Gemini:BillingFilter";

    /// <summary>Lines counted as Gemini Enterprise spend. A line matching any rule is included.</summary>
    public List<GeminiBillingRule> Include { get; set; } = [];

    /// <summary>
    /// Lines deliberately left out that would otherwise trigger the warning, so a known
    /// sibling SKU does not cry wolf on every run.
    /// </summary>
    public List<GeminiBillingRule> Ignore { get; set; } = [];

    /// <summary>
    /// Case-insensitive terms that make an excluded line look Gemini-related when found in
    /// its service or SKU description. A non-zero excluded line matching one of these (and
    /// not in <see cref="Ignore"/>) is reported as a warning.
    /// </summary>
    public List<string> WatchTerms { get; set; } = [];

    /// <summary>
    /// What was verified against live billing exports on 2026-07-22 and 2026-11-19: the seat
    /// subscription and its per-request overage bill under "Vertex AI Search" with
    /// "Enterprise" in the SKU. Agent Platform compute and memory (Vertex AI) are pay-as-you-go
    /// consumption, structurally closer to the Claude API Platform than to a seat product,
    /// so they are not counted here; the watch terms make sure that is a visible decision
    /// rather than an accident. To count them, add a rule with service "Vertex AI" and SKU
    /// pattern "%Agent Platform%".
    /// </summary>
    public static GeminiBillingFilter Default => new()
    {
        Include = [new GeminiBillingRule { Service = "Vertex AI Search", SkuLike = "%Enterprise%" }],
        Ignore =
        [
            new GeminiBillingRule { Service = "Vertex AI Search", SkuLike = "%Data Index%" },
            // Raw Gemini model token usage ("... Text Input - Predictions"): pay-as-you-go API
            // consumption, a different product from the seat-licensed Gemini Enterprise.
            new GeminiBillingRule { Service = "Vertex AI", SkuLike = "%Predictions%" },
        ],
        WatchTerms = ["Gemini", "Agent Platform", "Discovery Engine", "Vertex AI Search"],
    };

    /// <summary>Throws if the filter could not produce a meaningful query.</summary>
    public void Validate()
    {
        if (Include.Count == 0)
        {
            throw new InvalidOperationException(
                $"{ConfigurationSection}:Include must contain at least one rule, otherwise no Gemini Enterprise spend would be counted.");
        }

        foreach (var rule in Include.Concat(Ignore))
        {
            if (string.IsNullOrWhiteSpace(rule.Service) || string.IsNullOrWhiteSpace(rule.SkuLike))
            {
                throw new InvalidOperationException(
                    $"Every {ConfigurationSection} rule needs a non-empty Service and SkuLike.");
            }
        }

        if (WatchTerms.Any(string.IsNullOrWhiteSpace))
        {
            throw new InvalidOperationException($"{ConfigurationSection}:WatchTerms may not contain blank entries.");
        }
    }
}

/// <summary>
/// A billing line with non-zero cost that the filter left out but that looks Gemini-related.
/// </summary>
public sealed record ExcludedBillingLine(
    string Service, string Sku, decimal Cost, DateOnly FirstDate, DateOnly LastDate);
