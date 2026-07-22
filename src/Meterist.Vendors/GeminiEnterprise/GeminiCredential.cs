namespace Meterist.Vendors.GeminiEnterprise;

/// <summary>
/// Stored as the JSON payload behind ISecretStore.GetCredentialAsync(tenantId,
/// "GeminiEnterprise") — bundles the service account key with the billing
/// export location it should query, since nothing else in the system tracks
/// per-tenant vendor *connection* config (only credentials). See
/// docs/architecture.md §6 and the plan context for why this lives here
/// rather than as a new general-purpose config subsystem.
/// </summary>
public sealed record GeminiCredential
{
    public required string ServiceAccountJson { get; init; }

    public required string BillingProjectId { get; init; }

    public required string BillingDatasetId { get; init; }

    public required string BillingTableId { get; init; }
}
