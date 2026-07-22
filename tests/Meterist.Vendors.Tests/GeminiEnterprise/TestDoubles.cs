using Meterist.Core.Secrets;
using Meterist.Core.Vendors;
using Meterist.Vendors.GeminiEnterprise;

namespace Meterist.Vendors.Tests.GeminiEnterprise;

internal sealed class FakeSecretStore : ISecretStore
{
    private readonly Dictionary<(string TenantId, Guid VendorId), string> _store = new();

    public Task<string?> GetCredentialAsync(
        string tenantId, Guid vendorId, CancellationToken cancellationToken = default) =>
        Task.FromResult(_store.TryGetValue((tenantId, vendorId), out var value) ? value : null);

    public Task SetCredentialAsync(
        string tenantId, Guid vendorId, string credential, CancellationToken cancellationToken = default)
    {
        _store[(tenantId, vendorId)] = credential;
        return Task.CompletedTask;
    }
}

internal sealed class FakeGeminiBillingQueryRepository(IReadOnlyList<IReadOnlyDictionary<string, object?>> rowsToReturn)
    : IGeminiBillingQueryRepository
{
    public FakeGeminiBillingQueryRepository() : this([])
    {
    }

    public GeminiCredential? ReceivedCredential { get; private set; }

    public DateRange? ReceivedPeriod { get; private set; }

    public Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> QueryBillingRowsAsync(
        GeminiCredential credential, DateRange period, CancellationToken cancellationToken = default)
    {
        ReceivedCredential = credential;
        ReceivedPeriod = period;
        return Task.FromResult(rowsToReturn);
    }
}
