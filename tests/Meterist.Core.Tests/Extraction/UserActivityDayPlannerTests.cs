using Meterist.Core.Extraction;
using Meterist.Core.Vendors;

namespace Meterist.Core.Tests.Extraction;

public class UserActivityDayPlannerTests
{
    // Today is Sat 2026-11-21, so the newest day that can be fetched is Fri 2026-11-20.
    private static readonly DateOnly Today = new(2026, 11, 21);
    private static readonly IReadOnlySet<DateOnly> NoneHeld = new HashSet<DateOnly>();
    private static readonly IReadOnlyDictionary<DateOnly, int> NoSummary = new Dictionary<DateOnly, int>();

    private static DateRange Range(string from, string to) => new(DateOnly.Parse(from), DateOnly.Parse(to));

    private static DateOnly D(string date) => DateOnly.Parse(date);

    [Fact]
    public void WithNothingStoredAndNoSummary_EveryDayUpToYesterdayIsFetched_NeverTodayOrLater()
    {
        var days = UserActivityDayPlanner.DaysToFetch(Range("2026-11-17", "2026-11-25"), Today, NoneHeld, NoSummary);

        Assert.Equal([D("2026-11-17"), D("2026-11-18"), D("2026-11-19"), D("2026-11-20")], days);
    }

    [Fact]
    public void ADayThatIsAlreadyStored_IsNotFetchedAgain_UnlessItIsRecent()
    {
        var held = new HashSet<DateOnly> { D("2026-11-10"), D("2026-11-11"), D("2026-11-18"), D("2026-11-19"), D("2026-11-20") };

        var days = UserActivityDayPlanner.DaysToFetch(Range("2026-11-10", "2026-11-20"), Today, held, NoSummary);

        // The 10th and 11th are held and old, so skipped. The last three days (18th to 20th) are re-read although held.
        Assert.DoesNotContain(D("2026-11-10"), days);
        Assert.DoesNotContain(D("2026-11-11"), days);
        Assert.Contains(D("2026-11-18"), days);
        Assert.Contains(D("2026-11-19"), days);
        Assert.Contains(D("2026-11-20"), days);
        // The days in between were never stored and the summary says nothing about them, so they are fetched.
        Assert.Contains(D("2026-11-12"), days);
        Assert.Contains(D("2026-11-17"), days);
    }

    [Fact]
    public void ADayWhereTheOrganisationHadNoActiveUsers_IsNotFetched_BecauseThereIsNothingToFind()
    {
        var summary = new Dictionary<DateOnly, int> { [D("2026-11-14")] = 0, [D("2026-11-15")] = 0, [D("2026-11-16")] = 3 };

        var days = UserActivityDayPlanner.DaysToFetch(Range("2026-11-13", "2026-11-17"), Today, NoneHeld, summary);

        Assert.Equal([D("2026-11-13"), D("2026-11-16"), D("2026-11-17")], days);   // 14th and 15th known empty; 16th had users
    }

    [Fact]
    public void ADayWithNoStoredRowsButActiveUsers_IsFetched_ItWasMissed()
    {
        var summary = new Dictionary<DateOnly, int> { [D("2026-11-05")] = 7 };

        var days = UserActivityDayPlanner.DaysToFetch(Range("2026-11-05", "2026-11-05"), Today, NoneHeld, summary);

        Assert.Equal([D("2026-11-05")], days);
    }

    [Fact]
    public void TheRecentDaysAreAlwaysRead_EvenWhenTheSummaryOnceSaidNobodyWasActive()
    {
        // The vendor revises recent days, so a zero from yesterday's pull is not final.
        var summary = new Dictionary<DateOnly, int> { [D("2026-11-20")] = 0, [D("2026-11-19")] = 0 };
        var held = new HashSet<DateOnly> { D("2026-11-18") };

        var days = UserActivityDayPlanner.DaysToFetch(Range("2026-11-18", "2026-11-20"), Today, held, summary);

        Assert.Equal([D("2026-11-18"), D("2026-11-19"), D("2026-11-20")], days);
    }

    [Fact]
    public void ADailyRunOverALongWindow_AsksForAHandfulOfDays_NotOneRequestPerDay()
    {
        // The scheduled run re-reads from a fixed start every day. With the whole window stored it should ask for the recent days only.
        var period = Range("2026-07-01", "2026-11-20");
        var held = Enumerable.Range(0, 142).Select(i => D("2026-07-01").AddDays(i)).ToHashSet();

        var days = UserActivityDayPlanner.DaysToFetch(period, Today, held, NoSummary);

        Assert.Equal(UserActivityDayPlanner.DefaultRevisionDays, days.Count);
        Assert.Equal(D("2026-11-20"), days[^1]);
    }

    [Fact]
    public void ZeroRevisionDays_FetchesOnlyWhatIsMissing()
    {
        var held = new HashSet<DateOnly> { D("2026-11-19"), D("2026-11-20") };

        var days = UserActivityDayPlanner.DaysToFetch(Range("2026-11-18", "2026-11-20"), Today, held, NoSummary, revisionDays: 0);

        Assert.Equal([D("2026-11-18")], days);
    }

    [Fact]
    public void APeriodEntirelyInTheFuture_FetchesNothing()
    {
        Assert.Empty(UserActivityDayPlanner.DaysToFetch(Range("2026-11-21", "2026-11-30"), Today, NoneHeld, NoSummary));
    }
}
