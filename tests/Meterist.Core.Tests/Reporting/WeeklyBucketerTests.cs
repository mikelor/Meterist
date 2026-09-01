using Meterist.Core.Models;
using Meterist.Core.Reporting;

namespace Meterist.Core.Tests.Reporting;

public class WeeklyBucketerTests
{
    private static readonly Guid VendorId = Guid.NewGuid();

    [Theory]
    [InlineData(2026, 7, 1, 2026, 7, 1)] // Wednesday -> itself
    [InlineData(2026, 7, 2, 2026, 7, 1)] // Thursday -> preceding Wednesday
    [InlineData(2026, 7, 7, 2026, 7, 1)] // Tuesday -> preceding Wednesday
    [InlineData(2026, 7, 8, 2026, 7, 8)] // next Wednesday -> itself
    public void AnchorWednesdayOnOrBefore_ReturnsThePrecedingOrSameWednesday(
        int y, int m, int d, int expectedY, int expectedM, int expectedD)
    {
        var anchor = WeeklyBucketer.AnchorWednesdayOnOrBefore(new DateOnly(y, m, d));

        Assert.Equal(new DateOnly(expectedY, expectedM, expectedD), anchor);
        Assert.Equal(DayOfWeek.Wednesday, anchor.DayOfWeek);
    }

    [Fact]
    public void Bucket_GroupsRecordsIntoWednesdayToTuesdayWeeks()
    {
        var anchor = new DateOnly(2026, 7, 1); // a Wednesday
        var records = new List<DailySpendRecord>
        {
            Record(new DateOnly(2026, 7, 1), seat: 10m, usage: 1m),  // week 0
            Record(new DateOnly(2026, 7, 7), seat: 10m, usage: 2m),  // week 0 (Tuesday, last day)
            Record(new DateOnly(2026, 7, 8), seat: 10m, usage: 3m),  // week 1 (Wednesday, first day)
        };

        var weeks = WeeklyBucketer.Bucket(records, anchor);

        Assert.Equal(2, weeks.Count);
        Assert.Equal(0, weeks[0].WeekIndex);
        Assert.Equal(new DateOnly(2026, 7, 1), weeks[0].WeekStart);
        Assert.Equal(new DateOnly(2026, 7, 7), weeks[0].WeekEnd);
        Assert.Equal(20m, weeks[0].SeatFee);
        Assert.Equal(3m, weeks[0].UsageOrOverage);
        Assert.Equal(2, weeks[0].DaysPresent);

        Assert.Equal(1, weeks[1].WeekIndex);
        Assert.Equal(new DateOnly(2026, 7, 8), weeks[1].WeekStart);
        Assert.Equal(new DateOnly(2026, 7, 14), weeks[1].WeekEnd);
        Assert.Equal(1, weeks[1].DaysPresent);
    }

    [Fact]
    public void Bucket_MarksOnlyTheSpecifiedWeekIndicesAsExcludedFromProjection()
    {
        var anchor = new DateOnly(2026, 7, 1);
        var records = new List<DailySpendRecord>
        {
            Record(new DateOnly(2026, 7, 1)),
            Record(new DateOnly(2026, 7, 8)),
        };

        var weeks = WeeklyBucketer.Bucket(records, anchor, excludeWeekIndices: new HashSet<int> { 0 });

        Assert.True(weeks[0].ExcludeFromProjection);
        Assert.False(weeks[1].ExcludeFromProjection);
    }

    private static DailySpendRecord Record(DateOnly date, decimal seat = 0m, decimal usage = 0m) => new()
    {
        TenantId = "ecosync",
        VendorId = VendorId,
        Date = date,
        SeatFee = seat,
        UsageOrOverage = usage,
        GrossSpend = seat + usage,
        NetSpend = seat + usage,
    };
}
