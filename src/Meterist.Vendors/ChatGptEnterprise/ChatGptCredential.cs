using System.Diagnostics.CodeAnalysis;
using System.Text.Json;

namespace Meterist.Vendors.ChatGptEnterprise;

/// <summary>
/// Stored as the JSON payload behind ISecretStore.GetCredentialAsync(tenantId,
/// VendorCatalog.ChatGptEnterprise.Id). OrganizationId is the API Platform
/// Organization ID (org-...), found in ChatGPT workspace Settings -> General
/// -> "Organization ID" — NOT the Workspace ID. The COSTS compliance log
/// export is org-scoped, not workspace-scoped (see
/// docs/vendor-integration-reference.md).
///
/// WorkspaceId is the opposite: the workspace UUID shown just below the
/// Organization ID on the same page. The Daily Usage API
/// (/v1/analytics/workspaces/{workspace_id}/usage) is addressed by it. It is
/// optional so credentials stored before it existed keep working for COSTS
/// extraction; anything that needs it must check for null.
/// </summary>
public sealed record ChatGptCredential
{
    public required string OrganizationId { get; init; }

    public required string AdminApiKey { get; init; }

    public string? WorkspaceId { get; init; }

    /// <summary>
    /// Parses and validates a stored or about-to-be-stored credential payload.
    /// Returns false with a message that is safe to show an operator: it never
    /// echoes the admin key.
    /// </summary>
    public static bool TryParse(
        string json,
        [NotNullWhen(true)] out ChatGptCredential? credential,
        [NotNullWhen(false)] out string? error)
    {
        credential = null;

        try
        {
            credential = JsonSerializer.Deserialize<ChatGptCredential>(json);
        }
        catch (JsonException ex)
        {
            error = "The ChatGPT Enterprise credential is not valid JSON with the expected fields "
                + $"(OrganizationId, AdminApiKey, optional WorkspaceId): {ex.Message}";
            return false;
        }

        if (credential is null)
        {
            error = "The ChatGPT Enterprise credential is empty.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(credential.OrganizationId) || string.IsNullOrWhiteSpace(credential.AdminApiKey))
        {
            credential = null;
            error = "The ChatGPT Enterprise credential needs a non-empty OrganizationId and AdminApiKey.";
            return false;
        }

        // The Organization ID (org-...) and the Workspace ID sit next to each
        // other in the admin console and are easy to swap. A workspace ID is a
        // UUID; anything else here is almost certainly the wrong value, and
        // catching it now beats a 403 or 404 from the Daily Usage API later.
        if (credential.WorkspaceId is not null && !Guid.TryParse(credential.WorkspaceId, out _))
        {
            credential = null;
            error = "WorkspaceId must be the workspace's UUID (Settings -> General -> Workspace ID, "
                + "e.g. 11111111-2222-3333-4444-555555555555), not the org-... Organization ID. "
                + "Omit the field if you don't have it.";
            return false;
        }

        error = null;
        return true;
    }
}
