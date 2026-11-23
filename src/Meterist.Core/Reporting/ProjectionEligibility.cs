using Meterist.Core.Vendors;

namespace Meterist.Core.Reporting;

/// <summary>
/// The one rule for which weeks feed the Flat, Trend-adjusted and Latest-week projections, the same for every vendor.
/// <para>
/// A week feeds a projection only if it lies <b>fully inside the report period</b>. Weeks run Wednesday to Tuesday and are
/// anchored to the Wednesday on or before the period's first day, so a report that starts or ends mid-week has a short
/// first or last week: only some of its days are in the report, and a short week averaged in with full ones drags the
/// average and the trend down even when every requested day is stored. Such a week is still shown in the weekly table and
/// counted in the actual totals; it is only kept out of the projections.
/// </para>
/// <para>
/// This is separate from <see cref="WeeklyTotal.ExcludeFromProjection"/>, which is about <b>missing data</b>: a day inside
/// the period that has no stored record (ChatGPT Enterprise, which is seat-priced so every real day has one). A week is
/// left out if either applies.
/// </para>
/// </summary>
public static class ProjectionEligibility
{
    /// <summary>The week starts before the report does, so part of it is not in the report.</summary>
    public static bool IsLeadingPartial(WeeklyTotal week, DateRange period) => week.WeekStart < period.Start;

    /// <summary>The week ends after the report does, so part of it has not happened yet or is not in the report.</summary>
    public static bool IsTrailingPartial(WeeklyTotal week, DateRange period) => week.WeekEnd > period.End;

    /// <summary>The week is cut short at either end of the report.</summary>
    public static bool IsPartial(WeeklyTotal week, DateRange period) =>
        IsLeadingPartial(week, period) || IsTrailingPartial(week, period);

    /// <summary>
    /// The weeks that feed a vendor's projections, in order. A vendor that expects a record every day (the seat-priced
    /// ones) just contributes its eligible weeks: a missing day is a gap that <c>audit coverage</c> flags. A usage-priced
    /// vendor stores nothing on a day without usage, so a week with no record at all is real: zero usage. Each such week
    /// that lies fully inside the period is added as a $0 week, so Flat, Trend-adjusted and Latest week see the quiet
    /// stretch instead of averaging over only the active weeks. A vendor with no record in the period gets no weeks at
    /// all, so it keeps showing no projection rather than a $0 one. The weekly table and the manifest are not touched.
    /// </summary>
    public static IReadOnlyList<WeeklyTotal> ProjectionWeeks(
        IReadOnlyList<WeeklyTotal> weeks, VendorIdentity vendor, DateOnly anchor, DateRange period)
    {
        var eligible = weeks.Where(w => FeedsProjection(w, period)).OrderBy(w => w.WeekIndex).ToList();
        if (vendor.ExpectsRecordEveryDay || weeks.Count == 0)
        {
            return eligible;
        }

        var byIndex = eligible.ToDictionary(w => w.WeekIndex);
        var recorded = weeks.Select(w => w.WeekIndex).ToHashSet();
        var result = new List<WeeklyTotal>();
        for (var index = 0; ; index++)
        {
            var start = anchor.AddDays(index * 7);
            var end = start.AddDays(6);
            if (end > period.End)
            {
                break;
            }

            if (start < period.Start)
            {
                continue;   // a leading partial week is left out, empty or not
            }

            if (byIndex.TryGetValue(index, out var week))
            {
                result.Add(week);
            }
            else if (!recorded.Contains(index))
            {
                result.Add(new WeeklyTotal(index, start, end, 0m, 0m, 0m, 0, false));
            }
        }

        return result;
    }

    /// <summary>True for a week that <see cref="ProjectionWeeks"/> added because the vendor stored nothing in it.</summary>
    public static bool IsFilledEmptyWeek(WeeklyTotal week) => week.DaysPresent == 0;

    /// <summary>True when the week may feed a projection: not flagged for missing data, and not cut short by the report's edges.</summary>
    public static bool FeedsProjection(WeeklyTotal week, DateRange period) =>
        !week.ExcludeFromProjection && !IsPartial(week, period);
}
