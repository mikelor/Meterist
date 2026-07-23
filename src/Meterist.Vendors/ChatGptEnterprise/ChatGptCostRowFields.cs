namespace Meterist.Vendors.ChatGptEnterprise;

/// <summary>
/// Well-known dictionary keys shared between <see cref="IChatGptCostLogRepository"/>
/// implementations (which flatten COSTS events into one row per billing line)
/// and <see cref="ChatGptEnterpriseSpendNormalizer"/> (which reads them).
/// User/agent identity fields are preserved even though the v1 normalizer
/// aggregates them away — raw storage keeps them for the future
/// per-employee-spend feature (see RawVendorSpendData's doc comment).
/// </summary>
public static class ChatGptCostRowFields
{
    public const string RecordDate = "RecordDate";
    public const string Sku = "Sku";
    public const string CostValue = "CostValue";
    public const string CostUnit = "CostUnit";
    public const string EstimatedCostUsdValue = "EstimatedCostUsdValue";
    public const string UserId = "UserId";
    public const string UserEmail = "UserEmail";
    public const string UserName = "UserName";
    public const string EventId = "EventId";
}
