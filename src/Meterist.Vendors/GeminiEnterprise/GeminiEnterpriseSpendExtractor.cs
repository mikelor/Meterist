using System.Text.Json;
using Meterist.Core.Secrets;
using Meterist.Core.Vendors;

namespace Meterist.Vendors.GeminiEnterprise;

/// <summary>
/// Gemini Enterprise via Cloud Billing export to BigQuery — see
/// docs/vendor-integration-reference.md for setup/schema detail.
/// </summary>
public sealed class GeminiEnterpriseSpendExtractor : IVendorSpendExtractor
{
    private readonly ISecretStore _secretStore;
    private readonly IGeminiBillingQueryRepository _billingRepository;

    public GeminiEnterpriseSpendExtractor(
        ISecretStore secretStore,
        IGeminiBillingQueryRepository billingRepository)
    {
        _secretStore = secretStore;
        _billingRepository = billingRepository;
    }

    public Guid VendorId => VendorCatalog.GeminiEnterprise.Id;

    public bool SupportsOverage => true;

    // True for per-user *activity* (a separate, permission-gated telemetry
    // export) — but real per-user dollar cost is never vendor-reported for
    // this vendor. Any per-user $ figure a normalizer produces here would be
    // a derived estimate, not vendor-verified data; callers must label it as
    // such (see the future UserSpendRecord.IsEstimated flag in architecture.md §5).
    public bool SupportsPerUserBreakdown => true;

    public async Task<RawVendorSpendData> ExtractAsync(
        string tenantId,
        DateRange period,
        CancellationToken cancellationToken = default)
    {
        var credential = await LoadCredentialAsync(tenantId, cancellationToken).ConfigureAwait(false);

        var rows = await _billingRepository
            .QueryBillingRowsAsync(credential, period, cancellationToken)
            .ConfigureAwait(false);

        var recordsByDate = rows
            .GroupBy(row => (DateOnly)row[GeminiBillingRowFields.RecordDate]!)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<IReadOnlyDictionary<string, object?>>)group.ToList());

        return new RawVendorSpendData
        {
            VendorId = VendorId,
            TenantId = tenantId,
            Period = period,
            RecordsByDate = recordsByDate,
        };
    }

    private async Task<GeminiCredential> LoadCredentialAsync(string tenantId, CancellationToken cancellationToken)
    {
        var json = await _secretStore
            .GetCredentialAsync(tenantId, VendorId, cancellationToken)
            .ConfigureAwait(false);

        // A missing/malformed credential is a configuration problem, not "no
        // spend this period" — fail loudly rather than let it look like an
        // empty-but-successful extraction (same principle as SupportsOverage
        // above: capability/config gaps must be visible, not silently absorbed).
        if (string.IsNullOrWhiteSpace(json))
        {
            throw new InvalidOperationException(
                $"No Gemini Enterprise credential configured for tenant '{tenantId}'.");
        }

        try
        {
            return JsonSerializer.Deserialize<GeminiCredential>(json)
                ?? throw new InvalidOperationException(
                    $"Gemini Enterprise credential for tenant '{tenantId}' deserialized to null.");
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                $"Gemini Enterprise credential for tenant '{tenantId}' is not valid JSON.", ex);
        }
    }
}
