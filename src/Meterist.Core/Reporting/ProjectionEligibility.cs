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

    /// <summary>True when the week may feed a projection: not flagged for missing data, and not cut short by the report's edges.</summary>
    public static bool FeedsProjection(WeeklyTotal week, DateRange period) =>
        !week.ExcludeFromProjection && !IsPartial(week, period);
}
