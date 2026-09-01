using Meterist.Core.Models;

namespace Meterist.Core.Reporting;

/// <summary>
/// Ports scripts/QueryWeeklyTotals.cmd's julianday-based Wednesday–Tuesday
/// bucketing to C#. The anchor is always the Wednesday on or before the
/// report period's own start date — not a hardcoded calendar date — so this
/// generalizes past any one report's date range.
/// </summary>
public static class WeeklyBucketer
{
    public static DateOnly AnchorWednesdayOnOrBefore(DateOnly date)
    {
        var offsetFromWednesday = ((int)date.DayOfWeek - (int)DayOfWeek.Wednesday + 7) % 7;
        return date.AddDays(-offsetFromWednesday);
    }

    /// <summary>
    /// Groups records into Wednesday–Tuesday weeks relative to <paramref name="anchor"/>.
    /// Every record's date must be on or after <paramref name="anchor"/> — callers
    /// derive the anchor from the report period's own start, which is always true
    /// by construction. <paramref name="excludeWeekIndices"/> marks specific
    /// week indices (0-based, relative to the anchor) as excluded from trend
    /// projections — e.g. week 0 for ChatGPT Enterprise.
    /// </summary>
    public static IReadOnlyList<WeeklyTotal> Bucket(
        IReadOnlyList<DailySpendRecord> records,
        DateOnly anchor,
        IReadOnlySet<int>? excludeWeekIndices = null)
    {
        excludeWeekIndices ??= new HashSet<int>();

        return records
            .GroupBy(r => (r.Date.DayNumber - anchor.DayNumber) / 7)
            .OrderBy(g => g.Key)
            .Select(g =>
            {
                var weekStart = anchor.AddDays(g.Key * 7);
                return new WeeklyTotal(
                    WeekIndex: g.Key,
                    WeekStart: weekStart,
                    WeekEnd: weekStart.AddDays(6),
                    SeatFee: g.Sum(r => r.SeatFee),
                    UsageOrOverage: g.Sum(r => r.UsageOrOverage),
                    GrossSpend: g.Sum(r => r.GrossSpend),
                    DaysPresent: g.Count(),
                    ExcludeFromProjection: excludeWeekIndices.Contains(g.Key));
            })
            .ToList();
    }
}
