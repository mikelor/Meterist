using Meterist.Core.Secrets;
using Meterist.Core.Vendors;

namespace Meterist.Vendors.ClaudeEnterprise;

/// <summary>
/// Claude Enterprise's per-member spend limits, read with the same key the tenant's spend extraction already uses
/// (live test: it carries the <c>read:spend_limits</c> scope). A missing credential is a configuration problem, so it
/// fails loudly rather than reading as no limits.
/// </summary>
public sealed class ClaudeEnterpriseSpendLimitSource : IVendorSpendLimitSource
{
    private readonly ISecretStore _secretStore;
    private readonly IClaudeSpendLimitRepository _repository;

    public ClaudeEnterpriseSpendLimitSource(ISecretStore secretStore, IClaudeSpendLimitRepository repository)
    {
        _secretStore = secretStore;
        _repository = repository;
    }

    public Guid VendorId => VendorCatalog.ClaudeEnterprise.Id;

    public async Task<IReadOnlyList<VendorSpendLimit>> FetchLimitsAsync(
        string tenantId, CancellationToken cancellationToken = default)
    {
        var apiKey = await _secretStore.GetCredentialAsync(tenantId, VendorId, cancellationToken).ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException(
                $"No Claude Enterprise credential configured for tenant '{tenantId}'.");
        }

        return await _repository.ListEffectiveLimitsAsync(apiKey, cancellationToken).ConfigureAwait(false);
    }
}
