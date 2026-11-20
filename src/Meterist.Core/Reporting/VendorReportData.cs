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
    /// True when the breakdown says something the weekly figures do not: a usage kind other than the seat fee,
    /// or any non-usage charge. A vendor that is only a seat fee has nothing to add.
    /// </summary>
    public bool HasNotableBreakdown =>
        Breakdown.Any(b => !b.IsUsage || b.Category != Models.SpendCategories.Seat);

    public decimal TotalSeatFee => Weeks.Sum(w => w.SeatFee);

    public decimal TotalUsageOrOverage => Weeks.Sum(w => w.UsageOrOverage);

    public decimal TotalGrossSpend => Weeks.Sum(w => w.GrossSpend);
}
