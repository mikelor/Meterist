using Meterist.Core.Secrets;
using Meterist.Core.Vendors;

namespace Meterist.Vendors.ClaudeEnterprise;

/// <summary>
/// Claude Enterprise's member list, read with the same key the tenant's spend extraction already uses (the
/// live test found it accepted by the member endpoint). A missing credential is a configuration problem, so it
/// fails loudly rather than reading as an empty roster.
/// </summary>
public sealed class ClaudeEnterpriseRosterSource : IVendorRosterSource
{
    private readonly ISecretStore _secretStore;
    private readonly IClaudeMemberRosterRepository _repository;

    public ClaudeEnterpriseRosterSource(ISecretStore secretStore, IClaudeMemberRosterRepository repository)
    {
        _secretStore = secretStore;
        _repository = repository;
    }

    public Guid VendorId => VendorCatalog.ClaudeEnterprise.Id;

    public async Task<IReadOnlyList<VendorMember>> FetchMembersAsync(
        string tenantId, CancellationToken cancellationToken = default)
    {
        var apiKey = await _secretStore.GetCredentialAsync(tenantId, VendorId, cancellationToken).ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException(
                $"No Claude Enterprise credential configured for tenant '{tenantId}'.");
        }

        return await _repository.ListMembersAsync(apiKey, cancellationToken).ConfigureAwait(false);
    }
}
