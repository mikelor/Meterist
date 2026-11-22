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

    // Present only on rows from the newer /analytics/cost endpoint (HttpClaudeApiAnalyticsCostRepository); the legacy
    // cost_report does not carry them. UserId, UserEmail and UserName are personal data when the actor is a person.
    public const string ApiKeyId = "ApiKeyId";
    public const string Product = "Product";
    public const string Requests = "Requests";

    /// <summary>The actor's own id: user_id, service_account_id or api_key_id by type. Null when the row has no actor.</summary>
    public const string UserId = "UserId";
    public const string UserEmail = "UserEmail";
    public const string UserName = "UserName";

    /// <summary>user_actor, service_account_actor, api_actor or federated_actor. The key is absent on legacy rows and null when the row has no actor.</summary>
    public const string ActorType = "ActorType";
    public const string ActorDeleted = "ActorDeleted";
}
