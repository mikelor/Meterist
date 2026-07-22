using Meterist.Core.Vendors;

namespace Meterist.Vendors.ChatGptEnterprise;

/// <summary>
/// ChatGPT Enterprise unified Cost API (shipped Jun 18, 2026).
/// Not yet implemented — schema needs a hands-on spike with a real Admin key
/// before this can be built out; see docs/vendor-integration-reference.md.
/// </summary>
public sealed class ChatGptEnterpriseSpendExtractor : IVendorSpendExtractor
{
    public string VendorName => "ChatGptEnterprise";

    // True for raw credit consumption, which the Cost API does expose. The
    // credit-pool size and contracted overage rate are NOT API-derivable
    // though (console/contract-only) — those must come from tenant-level
    // config, not this extractor.
    public bool SupportsOverage => true;

    public bool SupportsPerUserBreakdown => true;

    public Task<RawVendorSpendData> ExtractAsync(
        string tenantId,
        DateRange period,
        CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException(
            "ChatGPT Enterprise Cost API integration not yet implemented — schema unspiked.");
    }
}
