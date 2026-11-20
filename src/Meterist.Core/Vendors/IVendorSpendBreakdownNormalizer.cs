using Meterist.Core.Models;

namespace Meterist.Core.Vendors;

/// <summary>
/// Turns one vendor's raw rows into the per-kind breakdown of each day's spend, alongside (and separate
/// from) the <see cref="IVendorSpendNormalizer"/> that produces the daily totals. Optional per vendor: a vendor
/// whose spend has no meaningful kinds simply has none registered, and extraction treats that as normal.
/// <para>
/// Rows flagged <see cref="DailySpendBreakdownRecord.IsUsage"/> must add up to the day's
/// <see cref="DailySpendRecord.GrossSpend"/>; the others are charges kept out of every total.
/// </para>
/// </summary>
public interface IVendorSpendBreakdownNormalizer
{
    Guid VendorId { get; }

    /// <summary>
    /// Breakdown rows for every day in <paramref name="rawData"/> that has any charge. A day with none
    /// contributes nothing, and the caller replaces that day's stored rows with the empty set.
    /// </summary>
    IReadOnlyList<DailySpendBreakdownRecord> Normalize(RawVendorSpendData rawData);
}
