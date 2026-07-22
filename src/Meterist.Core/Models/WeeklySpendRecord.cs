namespace Meterist.Core.Models;

/// <summary>
/// Normalized weekly spend for one tenant/vendor pair, regardless of that vendor's native API shape.
/// </summary>
public sealed class WeeklySpendRecord
{
    public required string TenantId { get; init; }

    public required string VendorName { get; init; }

    public required DateOnly WeekStart { get; init; }

    public required DateOnly WeekEnd { get; init; }

    public decimal SeatFee { get; init; }

    // Legitimately 0 for some vendor/tenant pairs (e.g. Claude API Platform always,
    // or Claude Enterprise when "usage credits" isn't enabled) — expected behavior,
    // not a missing-data signal. See IVendorSpendExtractor.SupportsOverage.
    public decimal UsageOrOverage { get; init; }

    public decimal GrossSpend { get; init; }

    public decimal CreditsApplied { get; init; }

    public decimal NetSpend { get; init; }
}
