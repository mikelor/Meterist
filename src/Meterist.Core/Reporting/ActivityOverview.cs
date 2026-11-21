using Meterist.Core.Models;

namespace Meterist.Core.Reporting;

/// <summary>
/// The vendor's own active-user figures for the report period, from the stored activity summaries (Claude Enterprise
/// today). Aggregate counts only. <see cref="AsOf"/> is the newest day in the period that has a summary; the daily,
/// weekly and monthly counts and the assigned seats are that day's, and <see cref="AverageDailyActive"/> is the mean of
/// the daily count over the <see cref="DaysCovered"/> days that had one.
/// </summary>
public sealed record ActivityOverview(
    DateOnly AsOf,
    int? DailyActive,
    int? WeeklyActive,
    int? MonthlyActive,
    int? AssignedSeats,
    decimal? MonthlyAdoptionRate,
    decimal? AverageDailyActive,
    int DaysCovered)
{
    /// <summary>Builds the overview from the organisation-wide rows of a period, or null when there are none with any figure.</summary>
    public static ActivityOverview? From(IEnumerable<ActivitySummaryRecord> rows)
    {
        var all = rows
            .Where(r => r.Product == ActivitySummaryRecord.AllProducts
                && (r.DailyActiveUsers is not null || r.WeeklyActiveUsers is not null || r.MonthlyActiveUsers is not null || r.AssignedSeatCount is not null))
            .OrderBy(r => r.Date)
            .ToList();

        if (all.Count == 0)
        {
            return null;
        }

        var latest = all[^1];
        var daily = all.Where(r => r.DailyActiveUsers is not null).ToList();

        return new ActivityOverview(
            latest.Date,
            latest.DailyActiveUsers,
            latest.WeeklyActiveUsers,
            latest.MonthlyActiveUsers,
            latest.AssignedSeatCount,
            latest.MonthlyAdoptionRate,
            daily.Count == 0 ? null : (decimal)daily.Average(r => r.DailyActiveUsers!.Value),
            daily.Count);
    }
}
