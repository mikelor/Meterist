namespace Meterist.Core.Vendors;

/// <summary>
/// Pulls raw spend data from one vendor's API for one tenant and date range.
/// Implemented once per vendor in Meterist.Vendors; the aggregation engine and
/// everything above it depends only on this interface, never a concrete vendor type.
/// </summary>
public interface IVendorSpendExtractor
{
    Guid VendorId { get; }

    // Capability flags genuinely vary per vendor/plan — not a gap to paper over.
    // The aggregation engine must treat "false" (or, for Claude Enterprise, a
    // runtime-conditional "no overage this tenant") as a valid, expected
    // outcome, not an extraction failure. See vendor-integration-reference.md's
    // cross-vendor overage/per-user matrix for the full picture per vendor.
    bool SupportsOverage { get; }

    bool SupportsPerUserBreakdown { get; }

    Task<RawVendorSpendData> ExtractAsync(
        string tenantId,
        DateRange period,
        CancellationToken cancellationToken = default);
}
