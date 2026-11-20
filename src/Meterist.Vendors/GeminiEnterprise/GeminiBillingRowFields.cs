namespace Meterist.Vendors.GeminiEnterprise;

/// <summary>
/// Well-known dictionary keys shared between <see cref="IGeminiBillingQueryRepository"/>
/// implementations (which build the rows) and the Gemini normalizers
/// (which read them) — avoids stringly-typed key duplication/typos between them.
/// Rows are already aggregated to (day, service, SKU) grain by the query itself.
/// </summary>
public static class GeminiBillingRowFields
{
    public const string RecordDate = "RecordDate";
    public const string SkuDescription = "SkuDescription";
    public const string Cost = "Cost";
    public const string CreditsAmount = "CreditsAmount";

    /// <summary>The billing service the line belongs to (for example "Vertex AI Search"). Absent in raw rows stored before categories existed.</summary>
    public const string ServiceDescription = "ServiceDescription";

    /// <summary>
    /// The kind of charge the first matching rule named (for example "Seat" or "Tax"), or null when that rule
    /// has no category. Absent in raw rows stored before categories existed; a missing or null value is classified
    /// from the SKU text instead (<see cref="GeminiCostBreakdown.Classify"/>).
    /// </summary>
    public const string Category = "Category";

    /// <summary>True when the line is usage the totals count; false for a non-usage charge (tax, support, rounding). Absent means usage, as every row stored before this key existed was.</summary>
    public const string IsUsage = "IsUsage";
}
