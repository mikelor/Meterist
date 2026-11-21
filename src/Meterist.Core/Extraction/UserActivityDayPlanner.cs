using Meterist.Core.Vendors;

namespace Meterist.Core.Extraction;

/// <summary>
/// Decides which days of per-person activity are worth asking the vendor for. The vendor serves one day per request and the
/// scheduled run re-reads a long window every day, so asking for every day every time would be hundreds of requests a run for
/// data that has not changed. A day is fetched when:
/// <list type="bullet">
/// <item>it is one of the last <c>revisionDays</c> days (the vendor revises recent days, as it did with seat counts), or</item>
/// <item>nothing is stored for it and the organisation's own daily active-user count says someone was active (or does not say).</item>
/// </list>
/// A day with no stored rows where the organisation's active-user count is zero is not fetched: there is nothing to find. Today
/// and later are never fetched, since the vendor publishes a day late.
/// </summary>
public static class UserActivityDayPlanner
{
    /// <summary>How many of the newest days are always re-read, because the vendor revises them for a day or two.</summary>
    public const int DefaultRevisionDays = 3;

    /// <param name="period">The days the caller asked for.</param>
    /// <param name="today">Today in UTC. The newest day that can be fetched is the day before.</param>
    /// <param name="daysWithStoredRows">Days that already have at least one stored activity row.</param>
    /// <param name="dailyActiveByDay">The organisation's active-user count for a day where the activity summary stored one; absent when unknown.</param>
    public static IReadOnlyList<DateOnly> DaysToFetch(
        DateRange period,
        DateOnly today,
        IReadOnlySet<DateOnly> daysWithStoredRows,
        IReadOnlyDictionary<DateOnly, int> dailyActiveByDay,
        int revisionDays = DefaultRevisionDays)
    {
        var newest = today.AddDays(-1);
        var recentFrom = newest.AddDays(1 - Math.Max(0, revisionDays));
        var days = new List<DateOnly>();

        for (var day = period.Start; day <= period.End && day <= newest; day = day.AddDays(1))
        {
            var recent = revisionDays > 0 && day >= recentFrom;
            var knownEmpty = dailyActiveByDay.TryGetValue(day, out var active) && active == 0;
            var held = daysWithStoredRows.Contains(day);

            if (recent || (!held && !knownEmpty))
            {
                days.Add(day);
            }
        }

        return days;
    }
}
