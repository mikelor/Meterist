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
    /// The seat subscription and its per-request overage bill under "Vertex AI Search" with
    /// "Enterprise" in the SKU (verified against live exports 2026-07-22 and 2026-11-19).
    /// Agent Platform compute and memory, raw Gemini model usage ("Predictions" SKUs) and
    /// pay-as-you-go lines are counted too, as usage-priced spend: the client pays for them
    /// whichever product they belong to. The pay-as-you-go SKU names have not been seen in a
    /// live export yet, so those rules are best guesses and the watch terms still flag any
    /// pay-as-you-go line they miss. Only "Data Index" storage is ignored. To stop counting
    /// a category, move its rule from Include to Ignore (see the user guide).
    /// </summary>
    public static GeminiBillingFilter Default => new()
    {
        Include =
        [
            new GeminiBillingRule { Service = "Vertex AI Search", SkuLike = "%Enterprise%" },
            new GeminiBillingRule { Service = "Vertex AI", SkuLike = "%Agent Platform%" },
            new GeminiBillingRule { Service = "Vertex AI", SkuLike = "%Predictions%" },
            new GeminiBillingRule { Service = "Vertex AI Search", SkuLike = "%Pay as you go%" },
            new GeminiBillingRule { Service = "Vertex AI Search", SkuLike = "%Pay-as-you-go%" },
        ],
        Ignore = [new GeminiBillingRule { Service = "Vertex AI Search", SkuLike = "%Data Index%" }],
        WatchTerms =
        [
            "Gemini", "Agent Platform", "Discovery Engine", "Vertex AI Search", "Pay as you go", "Pay-as-you-go",
        ],
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
