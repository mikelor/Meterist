namespace Meterist.Vendors.ChatGptEnterprise;

/// <summary>
/// Stored as the JSON payload behind ISecretStore.GetCredentialAsync(tenantId,
/// VendorCatalog.ChatGptEnterprise.Id). OrganizationId is the API Platform
/// Organization ID (org-...), found in ChatGPT workspace Settings -> General
/// -> "Organization ID" — NOT the Workspace ID. The COSTS compliance log
/// export is org-scoped, not workspace-scoped (see
/// docs/vendor-integration-reference.md).
/// </summary>
public sealed record ChatGptCredential
{
    public required string OrganizationId { get; init; }

    public required string AdminApiKey { get; init; }
}
