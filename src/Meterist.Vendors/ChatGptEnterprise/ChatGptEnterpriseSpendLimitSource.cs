using Meterist.Core.Secrets;
using Meterist.Core.Vendors;

namespace Meterist.Vendors.ChatGptEnterprise;

/// <summary>
/// ChatGPT Enterprise's per-member spend limits and usage, read with the admin key and workspace the tenant's
/// extraction already uses. A missing or invalid credential fails loudly (the shared loader) rather than reading as no limits.
/// </summary>
public sealed class ChatGptEnterpriseSpendLimitSource : IVendorSpendLimitSource
{
    private readonly ISecretStore _secretStore;
    private readonly IChatGptSpendControlsRepository _repository;

    public ChatGptEnterpriseSpendLimitSource(ISecretStore secretStore, IChatGptSpendControlsRepository repository)
    {
        _secretStore = secretStore;
        _repository = repository;
    }

    public Guid VendorId => VendorCatalog.ChatGptEnterprise.Id;

    public async Task<IReadOnlyList<VendorSpendLimit>> FetchLimitsAsync(
        string tenantId, CancellationToken cancellationToken = default)
    {
        var credential = await ChatGptCredentialLoader.LoadAsync(_secretStore, tenantId, cancellationToken).ConfigureAwait(false);
        return await _repository.ListUserLimitsAsync(credential, cancellationToken).ConfigureAwait(false);
    }
}
