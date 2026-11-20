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

    /// <summary>
    /// What kind of charge a matching line is, for the per-kind breakdown (e.g. "Seat", "ModelUsage", "Tax").
    /// Optional on an <see cref="GeminiBillingFilter.Include"/> rule: when it is left out, the kind is worked out
    /// from the SKU text instead, as it was before categories existed, so an older configuration keeps working.
    /// Required on a <see cref="GeminiBillingFilter.NonUsage"/> rule. Matching is first rule wins, so order rules
    /// from most to least specific. Not used on <see cref="GeminiBillingFilter.Ignore"/> rules.
    /// </summary>
    public string? Category { get; set; }
}

/// <summary>
/// Which billing-export lines count as Gemini Enterprise spend, which are recorded but kept out of the totals,
/// and which excluded lines should be flagged. Configuration rather than code because Google keeps changing how
/// this product bills (a new quota and pay-as-you-go model from 2026-08-17, new Agent Platform SKUs from July and
/// September): a new SKU should be a settings change, and a SKU we do not yet count should be noticed rather than
/// silently dropped.
/// <para>
/// Bound from the <c>Meterist:Gemini:BillingFilter</c> configuration section; when that
/// section is absent, <see cref="Default"/> applies.
/// </para>
/// </summary>
public sealed class GeminiBillingFilter
{
    public const string ConfigurationSection = "Meterist:Gemini:BillingFilter";

    /// <summary>
    /// Lines counted as Gemini Enterprise spend. A line matching any rule is included; the first match decides
    /// its <see cref="GeminiBillingRule.Category"/>.
    /// </summary>
    public List<GeminiBillingRule> Include { get; set; } = [];

    /// <summary>
    /// Lines that are real charges on the billing account but not usage: tax, support fees, rounding. They are
    /// stored in the per-kind breakdown, flagged as not usage, so the gap to the Google invoice can be explained,
    /// but they are never part of any total (see docs/architecture.md section 5.2). Every rule needs a Category.
    /// </summary>
    public List<GeminiBillingRule> NonUsage { get; set; } = [];

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
    /// The seat subscription and its per-request overage bill under "Vertex AI Search" (verified against live
    /// exports 2026-07-22 and 2026-11-19), split into Seat (the Subscription SKU, which must come first because
    /// it also contains "Enterprise") and EnterpriseOverage. Agent Platform compute and memory, raw Gemini model
    /// usage ("Predictions" SKUs) and pay-as-you-go lines are counted too, as usage-priced spend. The
    /// pay-as-you-go SKU names have not been seen in a live export yet, so those rules are best guesses and the
    /// watch terms still flag any pay-as-you-go line they miss. Tax, support fees and rounding are recorded as
    /// non-usage (verified present 2026-11-19). Only "Data Index" storage is ignored.
    /// </summary>
    public static GeminiBillingFilter Default => new()
    {
        Include =
        [
            new GeminiBillingRule { Service = "Vertex AI Search", SkuLike = "%Subscription%", Category = "Seat" },
            new GeminiBillingRule { Service = "Vertex AI Search", SkuLike = "%Enterprise%", Category = "EnterpriseOverage" },
            new GeminiBillingRule { Service = "Vertex AI", SkuLike = "%Agent Platform%", Category = "AgentPlatform" },
            new GeminiBillingRule { Service = "Vertex AI", SkuLike = "%Predictions%", Category = "ModelUsage" },
            new GeminiBillingRule { Service = "Vertex AI Search", SkuLike = "%Pay as you go%", Category = "PayAsYouGo" },
            new GeminiBillingRule { Service = "Vertex AI Search", SkuLike = "%Pay-as-you-go%", Category = "PayAsYouGo" },
        ],
        NonUsage =
        [
            new GeminiBillingRule { Service = "Invoice", SkuLike = "%Tax%", Category = "Tax" },
            new GeminiBillingRule { Service = "Invoice", SkuLike = "%Rounding%", Category = "Rounding" },
            new GeminiBillingRule { Service = "Support", SkuLike = "%", Category = "Support" },
        ],
        Ignore = [new GeminiBillingRule { Service = "Vertex AI Search", SkuLike = "%Data Index%" }],
        WatchTerms =
        [
            "Gemini", "Agent Platform", "Discovery Engine", "Vertex AI Search", "Pay as you go", "Pay-as-you-go",
        ],
    };

    /// <summary>Longest category label the breakdown table stores.</summary>
    public const int MaxCategoryLength = 64;

    /// <summary>Throws if the filter could not produce a meaningful query.</summary>
    public void Validate()
    {
        if (Include.Count == 0)
        {
            throw new InvalidOperationException(
                $"{ConfigurationSection}:Include must contain at least one rule, otherwise no Gemini Enterprise spend would be counted.");
        }

        foreach (var rule in Include.Concat(NonUsage).Concat(Ignore))
        {
            if (string.IsNullOrWhiteSpace(rule.Service) || string.IsNullOrWhiteSpace(rule.SkuLike))
            {
                throw new InvalidOperationException(
                    $"Every {ConfigurationSection} rule needs a non-empty Service and SkuLike.");
            }
        }

        foreach (var rule in NonUsage)
        {
            if (string.IsNullOrWhiteSpace(rule.Category))
            {
                throw new InvalidOperationException(
                    $"Every {ConfigurationSection}:NonUsage rule needs a Category (for example Tax or Support), "
                    + "because the breakdown cannot be labelled otherwise.");
            }
        }

        foreach (var rule in Include.Concat(NonUsage).Where(r => r.Category is not null))
        {
            if (string.IsNullOrWhiteSpace(rule.Category) || rule.Category.Length > MaxCategoryLength
                || rule.Category != rule.Category.Trim())
            {
                throw new InvalidOperationException(
                    $"A {ConfigurationSection} Category must be 1 to {MaxCategoryLength} characters with no leading or "
                    + "trailing spaces (or left out on an Include rule).");
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
