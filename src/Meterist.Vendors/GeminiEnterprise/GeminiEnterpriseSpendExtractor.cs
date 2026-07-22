using Meterist.Core.Vendors;

namespace Meterist.Vendors.GeminiEnterprise;

/// <summary>
/// Gemini Enterprise via Cloud Billing export to BigQuery.
/// Not yet implemented — see docs/vendor-integration-reference.md for setup/schema detail.
/// </summary>
public sealed class GeminiEnterpriseSpendExtractor : IVendorSpendExtractor
{
    public string VendorName => "GeminiEnterprise";

    public bool SupportsOverage => true;

    // True for per-user *activity* (a separate, permission-gated telemetry
    // export) — but real per-user dollar cost is never vendor-reported for
    // this vendor. Any per-user $ figure a normalizer produces here would be
    // a derived estimate, not vendor-verified data; callers must label it as
    // such (see the future UserSpendRecord.IsEstimated flag in architecture.md §5).
    public bool SupportsPerUserBreakdown => true;

    public Task<RawVendorSpendData> ExtractAsync(
        string tenantId,
        DateRange period,
        CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException(
            "Gemini Enterprise BigQuery integration not yet implemented.");
    }
}
