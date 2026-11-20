using System.Text.Json;
using Meterist.Core.Secrets;
using Meterist.Core.Vendors;
using Microsoft.Extensions.Logging;

namespace Meterist.Vendors.GeminiEnterprise;

/// <summary>
/// Gemini Enterprise via Cloud Billing export to BigQuery — see
/// docs/vendor-integration-reference.md for setup/schema detail.
/// </summary>
public sealed class GeminiEnterpriseSpendExtractor : IVendorSpendExtractor
{
    private readonly ISecretStore _secretStore;
    private readonly IGeminiBillingQueryRepository _billingRepository;
    private readonly ILogger<GeminiEnterpriseSpendExtractor> _logger;

    public GeminiEnterpriseSpendExtractor(
        ISecretStore secretStore,
        IGeminiBillingQueryRepository billingRepository,
        ILogger<GeminiEnterpriseSpendExtractor> logger)
    {
        _secretStore = secretStore;
        _billingRepository = billingRepository;
        _logger = logger;
    }

    public Guid VendorId => VendorCatalog.GeminiEnterprise.Id;

    public bool SupportsOverage => true;

    // True for per-user *activity* (a separate, permission-gated telemetry
    // export) — but real per-user dollar cost is never vendor-reported for
    // this vendor. Any per-user $ figure a normalizer produces here would be
    // a derived estimate, not vendor-verified data; callers must label it as
    // such (see the future UserSpendRecord.IsEstimated flag in architecture.md §5).
    public bool SupportsPerUserBreakdown => true;

    // BigQuery billing export has no retention/eviction quirk -- re-extraction
    // should always overwrite with the latest pull.
    public bool RequiresMonotonicUsage => false;

    public async Task<RawVendorSpendData> ExtractAsync(
        string tenantId,
        DateRange period,
        CancellationToken cancellationToken = default)
    {
        _logger.LogDebug(
            "Starting Gemini Enterprise extraction for tenant '{TenantId}', {PeriodStart} to {PeriodEnd}.",
            tenantId, period.Start, period.End);

        var credential = await LoadCredentialAsync(tenantId, cancellationToken).ConfigureAwait(false);

        _logger.LogDebug(
            "Resolved Gemini Enterprise credential for tenant '{TenantId}': project '{Project}', dataset '{Dataset}', table '{Table}'.",
            tenantId, credential.BillingProjectId, credential.BillingDatasetId, credential.BillingTableId);

        var rows = await _billingRepository
            .QueryBillingRowsAsync(credential, period, cancellationToken)
            .ConfigureAwait(false);

        await WarnAboutExcludedChargesAsync(tenantId, credential, period, cancellationToken).ConfigureAwait(false);

        var recordsByDate = rows
            .GroupBy(row => (DateOnly)row[GeminiBillingRowFields.RecordDate]!)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<IReadOnlyDictionary<string, object?>>)group.ToList());

        _logger.LogDebug(
            "Gemini Enterprise extraction for tenant '{TenantId}' grouped {RowCount} row(s) into {DayCount} day(s).",
            tenantId, rows.Count, recordsByDate.Count);

        return new RawVendorSpendData
        {
            VendorId = VendorId,
            TenantId = tenantId,
            Period = period,
            RecordsByDate = recordsByDate,
        };
    }

    // A Gemini-looking SKU the filter does not count is either correctly out of scope or a
    // charge we are silently missing (Google keeps adding SKUs), and only a person can tell
    // which. So it is reported on every run, but never fails the extraction: this is a
    // diagnostic, and the spend figures themselves do not depend on it.
    private async Task WarnAboutExcludedChargesAsync(
        string tenantId, GeminiCredential credential, DateRange period, CancellationToken cancellationToken)
    {
        IReadOnlyList<ExcludedBillingLine> excluded;
        try
        {
            excluded = await _billingRepository
                .FindExcludedChargesAsync(credential, period, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(
                ex, "Could not check tenant '{TenantId}' for Gemini-related billing lines outside the filter.", tenantId);
            return;
        }

        if (excluded.Count == 0)
        {
            return;
        }

        const int Shown = 8;
        var lines = string.Join(
            "; ",
            excluded.Take(Shown).Select(l =>
                $"{l.Service} | {l.Sku}: {l.Cost:F2} ({l.FirstDate:yyyy-MM-dd} to {l.LastDate:yyyy-MM-dd})"));

        _logger.LogWarning(
            "Tenant '{TenantId}' has {Count} Gemini-related billing line(s) with non-zero cost that the Gemini Enterprise "
            + "filter does not count: {Lines}{More}. If they belong in Gemini Enterprise spend, add an Include rule under "
            + "{Section}; if not, add an Ignore rule to silence this.",
            tenantId, excluded.Count, lines,
            excluded.Count > Shown ? $"; and {excluded.Count - Shown} more" : string.Empty,
            GeminiBillingFilter.ConfigurationSection);
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
