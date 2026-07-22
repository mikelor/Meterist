using Meterist.Core.Vendors;

namespace Meterist.Vendors.ClaudeEnterprise;

/// <summary>
/// Claude Enterprise Analytics API (docs.claude.com/en/manage-claude/analytics-api).
/// Not yet implemented — see docs/vendor-integration-reference.md for endpoint/auth detail.
/// </summary>
public sealed class ClaudeEnterpriseSpendExtractor : IVendorSpendExtractor
{
    public Guid VendorId => VendorCatalog.ClaudeEnterprise.Id;

    // True because the API *can* return overage — but only for tenants that have
    // "usage credits" enabled. A seat-based tenant without that toggle has no
    // overage concept at all (hard usage cap), so callers must additionally check
    // that tenant-level flag before treating a zero result as an extraction problem.
    public bool SupportsOverage => true;

    public bool SupportsPerUserBreakdown => true;

    public Task<RawVendorSpendData> ExtractAsync(
        string tenantId,
        DateRange period,
        CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException(
            "Claude Enterprise Analytics API integration not yet implemented.");
    }
}
