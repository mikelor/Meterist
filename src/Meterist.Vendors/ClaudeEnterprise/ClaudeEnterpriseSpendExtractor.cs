using Meterist.Core.Secrets;
using Meterist.Core.Vendors;
using Microsoft.Extensions.Logging;

namespace Meterist.Vendors.ClaudeEnterprise;

/// <summary>
/// Claude Enterprise via the Claude Enterprise Analytics API's
/// user_cost_report endpoint — see docs/vendor-integration-reference.md for
/// setup/schema detail.
/// </summary>
public sealed class ClaudeEnterpriseSpendExtractor : IVendorSpendExtractor
{
    private readonly ISecretStore _secretStore;
    private readonly IClaudeAnalyticsCostReportRepository _costReportRepository;
    private readonly ILogger<ClaudeEnterpriseSpendExtractor> _logger;

    public ClaudeEnterpriseSpendExtractor(
        ISecretStore secretStore,
        IClaudeAnalyticsCostReportRepository costReportRepository,
        ILogger<ClaudeEnterpriseSpendExtractor> logger)
    {
        _secretStore = secretStore;
        _costReportRepository = costReportRepository;
        _logger = logger;
    }

    public Guid VendorId => VendorCatalog.ClaudeEnterprise.Id;

    // True because the API *can* return overage — but only for tenants that have
    // "usage credits" enabled. A seat-based tenant without that toggle has no
    // overage concept at all (hard usage cap), so a $0 result is expected, not
    // a broken integration — there's no API field indicating which kind of
    // plan a tenant is on, so this can't be checked/branched on in code.
    public bool SupportsOverage => true;

    // Real vendor-reported per-user dollar cost (actor.user_id/email/name).
    public bool SupportsPerUserBreakdown => true;

    public async Task<RawVendorSpendData> ExtractAsync(
        string tenantId,
        DateRange period,
        CancellationToken cancellationToken = default)
    {
        _logger.LogDebug(
            "Starting Claude Enterprise extraction for tenant '{TenantId}', {PeriodStart} to {PeriodEnd}.",
            tenantId, period.Start, period.End);

        var analyticsApiKey = await LoadAnalyticsApiKeyAsync(tenantId, cancellationToken).ConfigureAwait(false);

        var rows = await _costReportRepository
            .QueryUserCostRowsAsync(analyticsApiKey, period, cancellationToken)
            .ConfigureAwait(false);

        var recordsByDate = rows
            .GroupBy(row => (DateOnly)row[ClaudeAnalyticsCostRowFields.RecordDate]!)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<IReadOnlyDictionary<string, object?>>)group.ToList());

        _logger.LogDebug(
            "Claude Enterprise extraction for tenant '{TenantId}' grouped {RowCount} row(s) into {DayCount} day(s).",
            tenantId, rows.Count, recordsByDate.Count);

        return new RawVendorSpendData
        {
            VendorId = VendorId,
            TenantId = tenantId,
            Period = period,
            RecordsByDate = recordsByDate,
        };
    }

    private async Task<string> LoadAnalyticsApiKeyAsync(string tenantId, CancellationToken cancellationToken)
    {
        var analyticsApiKey = await _secretStore
            .GetCredentialAsync(tenantId, VendorId, cancellationToken)
            .ConfigureAwait(false);

        // A missing credential is a configuration problem, not "no spend this
        // period" — fail loudly rather than let it look like an
        // empty-but-successful extraction.
        if (string.IsNullOrWhiteSpace(analyticsApiKey))
        {
            throw new InvalidOperationException(
                $"No Claude Enterprise credential configured for tenant '{tenantId}'.");
        }

        return analyticsApiKey;
    }
}
