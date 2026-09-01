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

    // True when a later pull of the same day can legitimately return LESS
    // UsageOrOverage than an earlier pull already captured (ChatGPT Enterprise's
    // COSTS export can drop rows for a day still nominally inside its 29-day
    // retention window — see vendor-integration-reference.md). When true, the
    // daily-spend upsert must never let UsageOrOverage decrease on re-extraction.
    // False (the normal case) is last-write-wins — Claude Enterprise/Claude API
    // Platform's own revisions to recent data should always win.
    bool RequiresMonotonicUsage { get; }

    Task<RawVendorSpendData> ExtractAsync(
        string tenantId,
        DateRange period,
        CancellationToken cancellationToken = default);
}
