using Meterist.Core.Secrets;
using Meterist.Core.Vendors;

namespace Meterist.Vendors.ChatGptEnterprise;

/// <summary>
/// Reads and validates a tenant's stored ChatGPT Enterprise credential. Shared by everything that talks to the ChatGPT
/// admin API (spend extraction, the member roster) so they fail with the same, operator-facing messages.
/// </summary>
public static class ChatGptCredentialLoader
{
    /// <exception cref="InvalidOperationException">
    /// No credential is stored, or it is not valid. A missing or malformed credential is a configuration problem, not
    /// "no spend" or "no members": it fails loudly rather than reading as an empty but successful run. A credential that
    /// exists but cannot be read surfaces as <see cref="UnreadableCredentialException"/> from the secret store.
    /// </exception>
    public static async Task<ChatGptCredential> LoadAsync(
        ISecretStore secretStore, string tenantId, CancellationToken cancellationToken = default)
    {
        var json = await secretStore
            .GetCredentialAsync(tenantId, VendorCatalog.ChatGptEnterprise.Id, cancellationToken)
            .ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(json))
        {
            throw new InvalidOperationException(
                $"No ChatGPT Enterprise credential configured for tenant '{tenantId}'.");
        }

        if (!ChatGptCredential.TryParse(json, out var credential, out var error))
        {
            throw new InvalidOperationException(
                $"ChatGPT Enterprise credential for tenant '{tenantId}' is invalid. {error}");
        }

        return credential;
    }
}
