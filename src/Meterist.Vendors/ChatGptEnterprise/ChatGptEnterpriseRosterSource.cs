using Meterist.Core.Secrets;
using Meterist.Core.Vendors;

namespace Meterist.Vendors.ChatGptEnterprise;

/// <summary>
/// ChatGPT Enterprise's workspace member list, read with the admin key and workspace ID the tenant's spend extraction
/// already stores (the live test found the key accepted by the member endpoint). A missing, invalid or unreadable
/// credential, or one with no WorkspaceId, fails loudly with a message saying how to fix it rather than reading as an
/// empty roster.
/// <para>
/// The API lists active members only, so a member who has been removed disappears from the next snapshot, which is
/// the intended meaning of a snapshot. There is no last-active field: idle seats are roster members with no Daily
/// Usage rows in the period, which is a later join, not this source's job.
/// </para>
/// </summary>
public sealed class ChatGptEnterpriseRosterSource : IVendorRosterSource
{
    private readonly ISecretStore _secretStore;
    private readonly IChatGptWorkspaceUserRepository _repository;

    public ChatGptEnterpriseRosterSource(ISecretStore secretStore, IChatGptWorkspaceUserRepository repository)
    {
        _secretStore = secretStore;
        _repository = repository;
    }

    public Guid VendorId => VendorCatalog.ChatGptEnterprise.Id;

    public async Task<IReadOnlyList<VendorMember>> FetchMembersAsync(
        string tenantId, CancellationToken cancellationToken = default)
    {
        var credential = await ChatGptCredentialLoader.LoadAsync(_secretStore, tenantId, cancellationToken).ConfigureAwait(false);
        return await _repository.ListUsersAsync(credential, cancellationToken).ConfigureAwait(false);
    }
}
