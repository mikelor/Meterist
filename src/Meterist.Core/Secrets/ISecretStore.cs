namespace Meterist.Core.Secrets;

/// <summary>
/// Per-tenant, per-vendor credential access. The four v1 vendors use four
/// distinct credential shapes (bare API key, Admin API key, workspace Admin
/// key, GCP service account JSON) — all represented here as an opaque string
/// payload, since the backing store (DPAPI-encrypted files for v1, see
/// Meterist.Secrets) doesn't need to know or care which shape it's holding.
/// </summary>
public interface ISecretStore
{
    Task<string?> GetCredentialAsync(
        string tenantId,
        string vendorName,
        CancellationToken cancellationToken = default);

    Task SetCredentialAsync(
        string tenantId,
        string vendorName,
        string credential,
        CancellationToken cancellationToken = default);
}
