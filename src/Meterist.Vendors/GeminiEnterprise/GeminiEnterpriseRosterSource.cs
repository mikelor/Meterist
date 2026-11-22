using Meterist.Core.Secrets;
using Meterist.Core.Vendors;

namespace Meterist.Vendors.GeminiEnterprise;

/// <summary>
/// Gemini Enterprise's per-user licence list (<c>userStores.userLicenses.list</c>), read with the tenant's service account.
/// It needs settings beyond the billing ones (the app's project, location and user store); a credential without them
/// is reported as not set up, not as a failure, so a scheduled run is not noisy for a tenant that has not opted in.
/// <para>
/// The list includes people with no licence, among them users who tried to sign in and were refused. Each record's licence
/// state is kept as the member's seat type; <see cref="Meterist.Core.Models.RosterSeatTypes"/> says which states hold a seat.
/// </para>
/// </summary>
public sealed class GeminiEnterpriseRosterSource : IVendorRosterSource
{
    private readonly ISecretStore _secretStore;
    private readonly IGeminiUserLicenseRepository _repository;

    public GeminiEnterpriseRosterSource(ISecretStore secretStore, IGeminiUserLicenseRepository repository)
    {
        _secretStore = secretStore;
        _repository = repository;
    }

    public Guid VendorId => VendorCatalog.GeminiEnterprise.Id;

    public async Task<string?> GetSkipReasonAsync(string tenantId, CancellationToken cancellationToken = default)
    {
        var credential = await GeminiCredentialLoader.LoadAsync(_secretStore, tenantId, cancellationToken).ConfigureAwait(false);
        return credential.HasLicenseSettings
            ? null
            : "the Gemini credential has no DiscoveryEngineProjectId, so the licence roster is not set up (see the user guide)";
    }

    public async Task<IReadOnlyList<VendorMember>> FetchMembersAsync(string tenantId, CancellationToken cancellationToken = default)
    {
        var credential = await GeminiCredentialLoader.LoadAsync(_secretStore, tenantId, cancellationToken).ConfigureAwait(false);
        if (!credential.HasLicenseSettings)
        {
            throw new InvalidOperationException(
                $"The Gemini Enterprise credential for tenant '{tenantId}' has no DiscoveryEngineProjectId, so there is no licence list to read. "
                + "Add DiscoveryEngineProjectId (and DiscoveryEngineLocation and UserStoreId if they are not global and default_user_store) "
                + "to the credential JSON and store it again; see the user guide.");
        }

        return await _repository.ListUserLicensesAsync(credential, cancellationToken).ConfigureAwait(false);
    }
}
