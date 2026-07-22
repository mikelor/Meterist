using Meterist.Core.Vendors;

namespace Meterist.Vendors.ClaudeApiPlatform;

/// <summary>
/// Claude API Platform Admin API cost_report/usage_report endpoints.
/// Not yet implemented — see docs/vendor-integration-reference.md for endpoint/auth detail.
/// </summary>
public sealed class ClaudeApiPlatformSpendExtractor : IVendorSpendExtractor
{
    public Guid VendorId => VendorCatalog.ClaudeApiPlatform.Id;

    // No seat/overage concept applies to this vendor at all — it's pure
    // usage-based pricing, so the entire cost_report figure is the spend.
    public bool SupportsOverage => false;

    // Granularity is by workspace_id/api_key_id, a developer-API construct,
    // not a named human user — there is no per-employee breakdown here.
    public bool SupportsPerUserBreakdown => false;

    public Task<RawVendorSpendData> ExtractAsync(
        string tenantId,
        DateRange period,
        CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException(
            "Claude API Platform Admin API integration not yet implemented.");
    }
}
