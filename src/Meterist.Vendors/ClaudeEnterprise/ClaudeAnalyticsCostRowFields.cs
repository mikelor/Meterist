namespace Meterist.Vendors.ClaudeEnterprise;

/// <summary>
/// Well-known dictionary keys shared between <see cref="IClaudeAnalyticsCostReportRepository"/>
/// implementations (which flatten user_cost_report results into one row per
/// actor per day) and <see cref="ClaudeEnterpriseSpendNormalizer"/> (which
/// reads them). User identity is preserved even though the v1 normalizer
/// aggregates it away — raw storage keeps it for the future
/// per-employee-spend feature, same rationale as ChatGPT's preserved
/// identity fields; real per-user dollar data is this vendor's standout
/// capability.
/// </summary>
public static class ClaudeAnalyticsCostRowFields
{
    public const string RecordDate = "RecordDate";
    public const string AmountUsd = "AmountUsd";
    public const string UserId = "UserId";
    public const string UserEmail = "UserEmail";
    public const string UserName = "UserName";
}
