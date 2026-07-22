namespace Meterist.Core.Secrets;

/// <summary>
/// Per-tenant, per-vendor credential access, keyed by the vendor's stable
/// <see cref="Meterist.Core.Vendors.VendorIdentity.Id"/> rather than its
/// display name, so a rebrand can't orphan a stored credential. The four v1
/// vendors use four distinct credential shapes (bare API key, Admin API key,
/// workspace Admin key, GCP service account JSON) — all represented here as
/// an opaque string payload, since the backing store (DPAPI-encrypted files
/// for v1, see Meterist.Secrets) doesn't need to know or care which shape
/// it's holding.
/// </summary>
public interface ISecretStore
{
    Task<string?> GetCredentialAsync(
        string tenantId,
        Guid vendorId,
        CancellationToken cancellationToken = default);

    Task SetCredentialAsync(
        string tenantId,
        Guid vendorId,
        string credential,
        CancellationToken cancellationToken = default);
}
