using Meterist.Core.Models;
using Meterist.Core.Vendors;

namespace Meterist.Core.Reporting;

/// <summary>One vendor's weekly totals and current contracted rates, for one tenant.</summary>
public sealed class VendorReportData
{
    public required VendorIdentity Vendor { get; init; }

    public required IReadOnlyList<WeeklyTotal> Weeks { get; init; }

    /// <summary>Currently-active rate rows only (EffectiveTo is null) — contracted
    /// terms for the rate-card section, not day-by-day proration history.</summary>
    public required IReadOnlyList<VendorRateConfig> CurrentRates { get; init; }

    /// <summary>
    /// The seat rate in force on the report's last day (the "seat" rate row), or null when none is configured. The
    /// projection takes the seat part from this contract rather than from past weekly seat fees. Null for a vendor
    /// with no rate rows (Gemini Enterprise, whose seat fee arrives prorated from the billing export).
    /// </summary>
    public VendorRateConfig? SeatRateAtPeriodEnd { get; init; }

    /// <summary>
    /// How many stored days came from each data source (keyed by a
    /// <see cref="DailySpendSources"/> value), for vendors that record one.
    /// Empty for single-source vendors, where there is nothing to disclose.
    /// </summary>
    public IReadOnlyDictionary<string, int> SourceDayCounts { get; init; } = new Dictionary<string, int>();

    /// <summary>
    /// The per-kind split of this vendor's spend over the period (seat, overage, model usage...) and any
    /// non-usage charges kept out of the totals (tax, support). Empty for a vendor with no breakdown stored.
    /// Informational: the weekly totals and projections run on the daily records, not on this.
    /// </summary>
    public IReadOnlyList<BreakdownCategoryTotal> Breakdown { get; init; } = [];

    /// <summary>
    /// Named per-user usage for the period, with the check that it reconciles to this vendor's aggregate. Null when no
    /// per-user rows are stored for the vendor in the period (which is not the same as no usage).
    /// </summary>
    public VendorUserUsage? UserUsage { get; init; }

    /// <summary>Billed versus vendor-reported seats and the idle seats, as of the end of the period. Null for a vendor with no roster source.</summary>
    public Auditing.VendorSeatAudit? Seats { get; init; }

    /// <summary>The vendor's own active-user figures for the period. Null when none are stored.</summary>
    public ActivityOverview? Activity { get; init; }

    /// <summary>
    /// True when the breakdown says something the weekly figures do not: a usage kind other than the seat fee,
    /// or any non-usage charge, that is worth at least half a cent. A vendor that is only a seat fee, or a seat
    /// fee plus invoice rounding, has nothing to add.
    /// </summary>
    public bool HasNotableBreakdown =>
        Breakdown.Any(b => !b.IsNegligible && (!b.IsUsage || b.Category != Models.SpendCategories.Seat));

    public decimal TotalSeatFee => Weeks.Sum(w => w.SeatFee);

    public decimal TotalUsageOrOverage => Weeks.Sum(w => w.UsageOrOverage);

    public decimal TotalGrossSpend => Weeks.Sum(w => w.GrossSpend);
}
