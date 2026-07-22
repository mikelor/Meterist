namespace Meterist.Core.Models;

/// <summary>
/// Normalized daily spend for one tenant/vendor pair, regardless of that
/// vendor's native API shape. Daily is the canonical stored grain — weekly,
/// monthly, and annual views are query-time aggregations over this, not
/// separately stored. Natural key is (TenantId, VendorId, Date): re-running
/// extraction for a day already stored is an upsert against this same key,
/// which is also how overlapping extraction timespans are handled — no
/// special-case "overlap" logic needed.
/// </summary>
public sealed class DailySpendRecord
{
    public required string TenantId { get; init; }

    public required Guid VendorId { get; init; }

    public required DateOnly Date { get; init; }

    public decimal SeatFee { get; init; }

    // Legitimately 0 for some vendor/tenant pairs (e.g. Claude API Platform always,
    // or Claude Enterprise when "usage credits" isn't enabled) — expected behavior,
    // not a missing-data signal. See IVendorSpendExtractor.SupportsOverage.
    public decimal UsageOrOverage { get; init; }

    public decimal GrossSpend { get; init; }

    public decimal CreditsApplied { get; init; }

    public decimal NetSpend { get; init; }
}
