using System.Security.Cryptography;
using System.Text.Json;
using Meterist.Core.Secrets;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging;

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
    private readonly ILogger<DataProtectionSecretStore> _logger;

    public DataProtectionSecretStore(
        IDataProtectionProvider dataProtectionProvider,
        ILogger<DataProtectionSecretStore> logger,
        string? secretsDirectory = null)
    {
        _dataProtectionProvider = dataProtectionProvider;
        _logger = logger;
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
        // A read never recovers: an unreadable file is an error here, not an empty store. See LoadTenantCredentialsAsync.
        var credentials = await LoadTenantCredentialsAsync(tenantId, recoverFromUnreadable: false, cancellationToken)
            .ConfigureAwait(false);
        return credentials.TryGetValue(vendorId.ToString(), out var value) ? value : null;
    }

    public async Task SetCredentialAsync(
        string tenantId,
        Guid vendorId,
        string credential,
        CancellationToken cancellationToken = default)
    {
        var credentials = await LoadTenantCredentialsAsync(tenantId, recoverFromUnreadable: true, cancellationToken)
            .ConfigureAwait(false);
        credentials[vendorId.ToString()] = credential;
        await SaveTenantCredentialsAsync(tenantId, credentials, cancellationToken).ConfigureAwait(false);
    }

    /// <param name="recoverFromUnreadable">
    /// True only for the read that precedes a write. A file that will not decrypt is then treated as empty, with a
    /// warning, so the operator can always reset a tenant. False for every read that feeds an extraction or a
    /// snapshot, where an unreadable file must surface as an error rather than look like "no credential".
    /// </param>
    private async Task<Dictionary<string, string>> LoadTenantCredentialsAsync(
        string tenantId,
        bool recoverFromUnreadable,
        CancellationToken cancellationToken)
    {
        var path = GetTenantFilePath(tenantId);
        if (!File.Exists(path))
        {
            return new Dictionary<string, string>();
        }

        var protectedBytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);

        try
        {
            var plaintextJson = CreateProtectorForTenant(tenantId).Unprotect(protectedBytes);
            return JsonSerializer.Deserialize<Dictionary<string, string>>(plaintextJson)
                ?? new Dictionary<string, string>();
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException)
        {
            if (!recoverFromUnreadable)
            {
                // Reported as unreadable, never as empty. Returning an empty dictionary here made every reader see
                // "no credential" for the tenant: ConfiguredVendorExtraction and ConfiguredRosterSnapshots would
                // mark each vendor Skipped and a scheduled run (for example under the wrong Windows account, which
                // DPAPI cannot decrypt for) would exit 0 having collected nothing.
                throw new UnreadableCredentialException(tenantId, ex);
            }

            // An undecryptable file must not become a permanent dead end: SetCredentialAsync
            // reads-before-write (to merge in other vendors' credentials for this same
            // tenant), so without this recovery, a corrupted/stale file — e.g. from before
            // the DataProtection application name was pinned, see ServiceCollectionExtensions
            // — would permanently block the operator from ever resetting that tenant, not
            // just from reading it. So a WRITE treats it as "nothing usable stored" and moves on, loudly.
            _logger.LogWarning(ex,
                "Stored credential file for tenant '{TenantId}' could not be decrypted — treating it "
                + "as empty rather than blocking. Any other vendor credentials previously stored for "
                + "this tenant will need to be re-set with 'credentials set'.",
                tenantId);
            return new Dictionary<string, string>();
        }
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
