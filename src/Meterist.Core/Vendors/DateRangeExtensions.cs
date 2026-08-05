namespace Meterist.Core.Vendors;

public static class DateRangeExtensions
{
    public static IEnumerable<DateOnly> EnumerateDays(this DateRange period)
    {
        for (var date = period.Start; date <= period.End; date = date.AddDays(1))
        {
            yield return date;
        }
    }
}
