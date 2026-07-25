using Meterist.Core.Secrets;
using Meterist.Core.Vendors;
using Microsoft.Extensions.Logging;

namespace Meterist.Vendors.ClaudeApiPlatform;

/// <summary>
/// Claude API Platform via the Anthropic Admin API's Cost Report endpoint —
/// see docs/vendor-integration-reference.md for setup/schema detail.
/// </summary>
public sealed class ClaudeApiPlatformSpendExtractor : IVendorSpendExtractor
{
    private readonly ISecretStore _secretStore;
    private readonly IClaudeCostReportRepository _costReportRepository;
    private readonly ILogger<ClaudeApiPlatformSpendExtractor> _logger;

    public ClaudeApiPlatformSpendExtractor(
        ISecretStore secretStore,
        IClaudeCostReportRepository costReportRepository,
        ILogger<ClaudeApiPlatformSpendExtractor> logger)
    {
        _secretStore = secretStore;
        _costReportRepository = costReportRepository;
        _logger = logger;
    }

    public Guid VendorId => VendorCatalog.ClaudeApiPlatform.Id;

    // No seat/overage concept applies to this vendor at all — it's pure
    // usage-based pricing, so the entire cost_report figure is the spend.
    public bool SupportsOverage => false;

    // Granularity is by workspace_id/api_key_id, a developer-API construct,
    // not a named human user — there is no per-employee breakdown here.
    public bool SupportsPerUserBreakdown => false;

    public async Task<RawVendorSpendData> ExtractAsync(
        string tenantId,
        DateRange period,
        CancellationToken cancellationToken = default)
    {
        _logger.LogDebug(
            "Starting Claude API Platform extraction for tenant '{TenantId}', {PeriodStart} to {PeriodEnd}.",
            tenantId, period.Start, period.End);

        var adminApiKey = await LoadAdminApiKeyAsync(tenantId, cancellationToken).ConfigureAwait(false);

        var rows = await _costReportRepository
            .QueryCostRowsAsync(adminApiKey, period, cancellationToken)
            .ConfigureAwait(false);

        var recordsByDate = rows
            .GroupBy(row => (DateOnly)row[ClaudeApiCostRowFields.RecordDate]!)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<IReadOnlyDictionary<string, object?>>)group.ToList());

        _logger.LogDebug(
            "Claude API Platform extraction for tenant '{TenantId}' grouped {RowCount} row(s) into {DayCount} day(s).",
            tenantId, rows.Count, recordsByDate.Count);

        return new RawVendorSpendData
        {
            VendorId = VendorId,
            TenantId = tenantId,
            Period = period,
            RecordsByDate = recordsByDate,
        };
    }

    private async Task<string> LoadAdminApiKeyAsync(string tenantId, CancellationToken cancellationToken)
    {
        var adminApiKey = await _secretStore
            .GetCredentialAsync(tenantId, VendorId, cancellationToken)
            .ConfigureAwait(false);

        // A missing credential is a configuration problem, not "no spend this
        // period" — fail loudly rather than let it look like an
        // empty-but-successful extraction.
        if (string.IsNullOrWhiteSpace(adminApiKey))
        {
            throw new InvalidOperationException(
                $"No Claude API Platform credential configured for tenant '{tenantId}'.");
        }

        return adminApiKey;
    }
}
