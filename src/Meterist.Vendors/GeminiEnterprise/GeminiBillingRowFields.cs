namespace Meterist.Vendors.GeminiEnterprise;

/// <summary>
/// Well-known dictionary keys shared between <see cref="IGeminiBillingQueryRepository"/>
/// implementations (which build the rows) and <see cref="GeminiEnterpriseSpendNormalizer"/>
/// (which reads them) — avoids stringly-typed key duplication/typos between the two.
/// Rows are already aggregated to (day, SKU) grain by the query itself.
/// </summary>
public static class GeminiBillingRowFields
{
    public const string RecordDate = "RecordDate";
    public const string SkuDescription = "SkuDescription";
    public const string Cost = "Cost";
    public const string CreditsAmount = "CreditsAmount";
}
