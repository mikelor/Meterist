using System.Text.Json;
using Meterist.Core.Secrets;
using Meterist.Core.Vendors;

namespace Meterist.Vendors.GeminiEnterprise;

/// <summary>Reads a tenant's stored Gemini credential, failing with a message that says how to fix it (and never echoing the JSON).</summary>
public static class GeminiCredentialLoader
{
    public static async Task<GeminiCredential> LoadAsync(ISecretStore secretStore, string tenantId, CancellationToken cancellationToken)
    {
        var json = await secretStore
            .GetCredentialAsync(tenantId, VendorCatalog.GeminiEnterprise.Id, cancellationToken)
            .ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(json))
        {
            throw new InvalidOperationException($"No Gemini Enterprise credential configured for tenant '{tenantId}'.");
        }

        try
        {
            return JsonSerializer.Deserialize<GeminiCredential>(json)
                ?? throw new InvalidOperationException($"Gemini Enterprise credential for tenant '{tenantId}' deserialized to null.");
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"Gemini Enterprise credential for tenant '{tenantId}' is not valid JSON.", ex);
        }
    }
}
