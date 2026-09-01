using Meterist.Core.Reporting;

namespace Meterist.Core.Tests.Reporting;

public class ProjectionCalculatorTests
{
    [Fact]
    public void Compute_EmptySeries_ReturnsAllZero()
    {
        var projection = ProjectionCalculator.Compute([]);

        Assert.Equal(0m, projection.Flat);
        Assert.Equal(0m, projection.TrendAdjusted);
        Assert.Equal(0m, projection.LatestWeek);
    }

    [Fact]
    public void Compute_SingleWeek_FlatTrendAndLatestAllEqualThatWeekTimes52()
    {
        var projection = ProjectionCalculator.Compute([100m]);

        Assert.Equal(5200m, projection.Flat);
        Assert.Equal(5200m, projection.TrendAdjusted);
        Assert.Equal(5200m, projection.LatestWeek);
    }

    [Fact]
    public void Compute_FlatWeeklySeries_AllThreeScenariosAgree()
    {
        var projection = ProjectionCalculator.Compute([50m, 50m, 50m, 50m]);

        Assert.Equal(2600m, projection.Flat);
        Assert.Equal(2600m, projection.TrendAdjusted);
        Assert.Equal(2600m, projection.LatestWeek);
    }

    // Regression test against a real figure independently verified via a
    // standalone awk computation during the 2026-08-29 reconciliation pull —
    // see the session's own published report for the source numbers
    // (zelleri ChatGPT Enterprise usage, weeks Jul 8–Aug 25).
    [Fact]
    public void Compute_RealSevenWeekUsageSeries_MatchesIndependentlyVerifiedFigures()
    {
        decimal[] weeklyUsage = [72.03m, 190.41m, 337.43m, 82.93m, 351.55m, 145.55m, 159.54m];

        var projection = ProjectionCalculator.Compute(weeklyUsage);

        Assert.Equal(9950.13m, Math.Round(projection.Flat, 2));
        Assert.Equal(11338.75m, Math.Round(projection.TrendAdjusted, 2));
        Assert.Equal(8296.08m, Math.Round(projection.LatestWeek, 2));
    }
}
