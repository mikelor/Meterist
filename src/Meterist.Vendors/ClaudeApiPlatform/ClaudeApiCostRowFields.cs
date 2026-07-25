namespace Meterist.Vendors.ClaudeApiPlatform;

/// <summary>
/// Well-known dictionary keys shared between <see cref="IClaudeCostReportRepository"/>
/// implementations (which flatten cost_report results into one row per line
/// item) and <see cref="ClaudeApiPlatformSpendNormalizer"/> (which reads
/// them). Model/token-type/cost-type detail is preserved even though the v1
/// normalizer aggregates it away — raw storage keeps it for a possible
/// future "cost by model" feature (see RawVendorSpendData's doc comment).
/// </summary>
public static class ClaudeApiCostRowFields
{
    public const string RecordDate = "RecordDate";
    public const string AmountUsd = "AmountUsd";
    public const string Description = "Description";
    public const string Model = "Model";
    public const string TokenType = "TokenType";
    public const string CostType = "CostType";
    public const string ServiceTier = "ServiceTier";
    public const string WorkspaceId = "WorkspaceId";
}
