using System.Text.Json;
using Meterist.Core.Secrets;
using Microsoft.AspNetCore.DataProtection;

namespace Meterist.Secrets;

/// <summary>
/// v1 ISecretStore backed by Microsoft.AspNetCore.DataProtection, with the key
/// ring protected at rest via Windows DPAPI (wired up in
/// ServiceCollectionExtensions.AddMeteristSecretStore). One encrypted file per
/// tenant, holding a JSON object of whichever vendor credentials that tenant
/// actually uses — see docs/architecture.md §6.
///
/// DPAPI ties decryption to the current Windows user profile on the current
/// machine by design: this is a forcing function, not a bug. The moment
/// Meterist runs somewhere other than the operator's own machine, this store
/// stops working loudly, forcing a deliberate migration to a real cloud
/// secrets manager instead of silently carrying a local-only security model
/// into a hosted environment.
/// </summary>
public sealed class DataProtectionSecretStore : ISecretStore
{
    private readonly IDataProtectionProvider _dataProtectionProvider;
    private readonly string _secretsDirectory;

    public DataProtectionSecretStore(
        IDataProtectionProvider dataProtectionProvider,
        string? secretsDirectory = null)
    {
        _dataProtectionProvider = dataProtectionProvider;
        _secretsDirectory = secretsDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Meterist",
            "secrets");
    }

    public async Task<string?> GetCredentialAsync(
        string tenantId,
        Guid vendorId,
        CancellationToken cancellationToken = default)
    {
        var credentials = await LoadTenantCredentialsAsync(tenantId, cancellationToken).ConfigureAwait(false);
        return credentials.TryGetValue(vendorId.ToString(), out var value) ? value : null;
    }

    public async Task SetCredentialAsync(
        string tenantId,
        Guid vendorId,
        string credential,
        CancellationToken cancellationToken = default)
    {
        var credentials = await LoadTenantCredentialsAsync(tenantId, cancellationToken).ConfigureAwait(false);
        credentials[vendorId.ToString()] = credential;
        await SaveTenantCredentialsAsync(tenantId, credentials, cancellationToken).ConfigureAwait(false);
    }

    private async Task<Dictionary<string, string>> LoadTenantCredentialsAsync(
        string tenantId,
        CancellationToken cancellationToken)
    {
        var path = GetTenantFilePath(tenantId);
        if (!File.Exists(path))
        {
            return new Dictionary<string, string>();
        }

        var protectedBytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        var plaintextJson = CreateProtectorForTenant(tenantId).Unprotect(protectedBytes);
        return JsonSerializer.Deserialize<Dictionary<string, string>>(plaintextJson)
            ?? new Dictionary<string, string>();
    }

    private async Task SaveTenantCredentialsAsync(
        string tenantId,
        Dictionary<string, string> credentials,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_secretsDirectory);
        var plaintextJson = JsonSerializer.SerializeToUtf8Bytes(credentials);
        var protectedBytes = CreateProtectorForTenant(tenantId).Protect(plaintextJson);
        await File.WriteAllBytesAsync(GetTenantFilePath(tenantId), protectedBytes, cancellationToken).ConfigureAwait(false);
    }

    private string GetTenantFilePath(string tenantId) => Path.Combine(_secretsDirectory, $"{tenantId}.bin");

    // Purpose-string isolation per tenant: even if the key ring were ever
    // shared or misconfigured, one tenant's protector cannot decrypt another
    // tenant's blob.
    private IDataProtector CreateProtectorForTenant(string tenantId) =>
        _dataProtectionProvider.CreateProtector($"Meterist.Tenant.{tenantId}");
}
