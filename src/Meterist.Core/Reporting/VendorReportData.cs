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

    public decimal TotalSeatFee => Weeks.Sum(w => w.SeatFee);

    public decimal TotalUsageOrOverage => Weeks.Sum(w => w.UsageOrOverage);

    public decimal TotalGrossSpend => Weeks.Sum(w => w.GrossSpend);
}
