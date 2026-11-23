using System.Globalization;
using Meterist.Core.Vendors;

namespace Meterist.Core.Reporting;

/// <summary>
/// The unit the report's spend table is grouped by. Storage is always daily; this only chooses how the table is shown.
/// The projections and the report manifest stay weekly whatever is chosen here (see <see cref="PeriodBuckets"/>).
/// </summary>
public enum AggregationPeriod
{
    /// <summary>Wednesday to Tuesday, anchored to the Wednesday on or before the report's first day. The default.</summary>
    Week,

    /// <summary>Calendar months.</summary>
    Month,

    /// <summary>Calendar quarters (Jan-Mar, Apr-Jun, Jul-Sep, Oct-Dec).</summary>
    Quarter,

    /// <summary>Calendar years.</summary>
    Year,
}

/// <summary>One column of the spend table: a period of the report's date range, which may stick out of it at either end.</summary>
public readonly record struct PeriodColumn(int Index, DateOnly Start, DateOnly End);

/// <summary>One vendor's stored spend inside one <see cref="PeriodColumn"/>.</summary>
public readonly record struct PeriodTotal(
    int Index,
    DateOnly Start,
    DateOnly End,
    decimal SeatFee,
    decimal UsageOrOverage,
    decimal GrossSpend,
    int DaysPresent);

/// <summary>Works out the columns of the spend table for an <see cref="AggregationPeriod"/>.</summary>
public static class PeriodBuckets
{
    private static readonly CultureInfo Culture = CultureInfo.InvariantCulture;

    /// <summary>Parses a command-line value (case-insensitive); false when it is not one of the four periods.</summary>
    public static bool TryParse(string? text, out AggregationPeriod period)
    {
        period = AggregationPeriod.Week;
        return !string.IsNullOrWhiteSpace(text)
            && text.All(char.IsLetter)
            && Enum.TryParse(text, ignoreCase: true, out period)
            && Enum.IsDefined(period);
    }

    /// <summary>The columns that cover <paramref name="period"/>, from the one holding its first day to the one holding its last.</summary>
    public static IReadOnlyList<PeriodColumn> Columns(AggregationPeriod aggregation, DateRange period, DateOnly weekAnchor)
    {
        var columns = new List<PeriodColumn>();
        var start = ColumnStart(aggregation, period.Start, weekAnchor);
        for (var index = 0; start <= period.End; index++)
        {
            var next = NextStart(aggregation, start);
            columns.Add(new PeriodColumn(index, start, next.AddDays(-1)));
            start = next;
        }

        return columns;
    }

    /// <summary>The first day of the column that holds <paramref name="date"/>.</summary>
    public static DateOnly ColumnStart(AggregationPeriod aggregation, DateOnly date, DateOnly weekAnchor) => aggregation switch
    {
        AggregationPeriod.Month => new DateOnly(date.Year, date.Month, 1),
        AggregationPeriod.Quarter => new DateOnly(date.Year, (((date.Month - 1) / 3) * 3) + 1, 1),
        AggregationPeriod.Year => new DateOnly(date.Year, 1, 1),
        _ => weekAnchor.AddDays(Math.DivRem(date.DayNumber - weekAnchor.DayNumber, 7, out _) * 7),
    };

    private static DateOnly NextStart(AggregationPeriod aggregation, DateOnly start) => aggregation switch
    {
        AggregationPeriod.Month => start.AddMonths(1),
        AggregationPeriod.Quarter => start.AddMonths(3),
        AggregationPeriod.Year => start.AddYears(1),
        _ => start.AddDays(7),
    };

    /// <summary>A column heading: "Jul 1–7" for a week, "Jul 2026", "Q3 2026" or "2026".</summary>
    public static string Label(AggregationPeriod aggregation, DateOnly start, DateOnly end) => aggregation switch
    {
        AggregationPeriod.Month => start.ToString("MMM yyyy", Culture),
        AggregationPeriod.Quarter => $"Q{((start.Month - 1) / 3) + 1} {start.Year.ToString(Culture)}",
        AggregationPeriod.Year => start.Year.ToString(Culture),
        _ => WeekLabel(start, end),
    };

    private static string WeekLabel(DateOnly start, DateOnly end)
    {
        var startStr = start.ToString("MMM d", Culture);
        var endStr = start.Month == end.Month ? end.Day.ToString(Culture) : end.ToString("MMM d", Culture);
        return $"{startStr}–{endStr}";
    }

    /// <summary>The adjective used in the section heading: "Weekly", "Monthly", "Quarterly" or "Yearly".</summary>
    public static string Adjective(AggregationPeriod aggregation) => aggregation switch
    {
        AggregationPeriod.Month => "Monthly",
        AggregationPeriod.Quarter => "Quarterly",
        AggregationPeriod.Year => "Yearly",
        _ => "Weekly",
    };

    /// <summary>The singular noun: "week", "month", "quarter" or "year".</summary>
    public static string Noun(AggregationPeriod aggregation) => aggregation.ToString().ToLowerInvariant();

    /// <summary>
    /// Groups a vendor's daily records into the columns of <paramref name="aggregation"/>. A column with no record is not
    /// returned, so the table shows a dash for it, as it does for an empty week.
    /// </summary>
    public static IReadOnlyList<PeriodTotal> Bucket(
        IReadOnlyList<Models.DailySpendRecord> records, AggregationPeriod aggregation, DateRange period, DateOnly weekAnchor)
    {
        var columns = Columns(aggregation, period, weekAnchor).ToDictionary(c => c.Start);

        return records
            .Where(r => r.Date >= period.Start && r.Date <= period.End)
            .GroupBy(r => ColumnStart(aggregation, r.Date, weekAnchor))
            .OrderBy(g => g.Key)
            .Select(g =>
            {
                var column = columns[g.Key];
                return new PeriodTotal(
                    column.Index,
                    column.Start,
                    column.End,
                    g.Sum(r => r.SeatFee),
                    g.Sum(r => r.UsageOrOverage),
                    g.Sum(r => r.GrossSpend),
                    g.Count());
            })
            .ToList();
    }
}
