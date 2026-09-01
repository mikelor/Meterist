namespace Meterist.Core.Reporting;

/// <summary>
/// Flat / Trend-adjusted / Latest-week annualized projections from a series
/// of complete-week totals, in that series's own natural (chronological)
/// order. This is the exact math this project's reconciliation reports have
/// computed by hand every pull — a subtle bug here already produced one real
/// mistake (see docs/product-design-document.md §7's "Automated report
/// generation" entry), so this is deliberately a pure, independently-tested
/// function rather than inline arithmetic in a report renderer.
/// </summary>
public static class ProjectionCalculator
{
    public readonly record struct Projection(decimal Flat, decimal TrendAdjusted, decimal LatestWeek);

    private const int WeeksPerYear = 52;

    /// <summary>
    /// Flat = average(values) × 52. Trend-adjusted = an OLS linear regression
    /// over the series (x = 0..n-1), projected one step past the last point,
    /// × 52. Latest week = values[^1] × 52. Returns all-zero for an empty series.
    /// </summary>
    public static Projection Compute(IReadOnlyList<decimal> completeWeekValues)
    {
        if (completeWeekValues.Count == 0)
        {
            return new Projection(0m, 0m, 0m);
        }

        var flat = completeWeekValues.Average() * WeeksPerYear;
        var latestWeek = completeWeekValues[^1] * WeeksPerYear;
        var trendAdjusted = ProjectNextViaOlsRegression(completeWeekValues) * WeeksPerYear;

        return new Projection(flat, trendAdjusted, latestWeek);
    }

    private static decimal ProjectNextViaOlsRegression(IReadOnlyList<decimal> values)
    {
        var n = values.Count;
        if (n == 1)
        {
            return values[0];
        }

        double sumX = 0, sumY = 0;
        for (var i = 0; i < n; i++)
        {
            sumX += i;
            sumY += (double)values[i];
        }

        var meanX = sumX / n;
        var meanY = sumY / n;

        double sumXy = 0, sumXx = 0;
        for (var i = 0; i < n; i++)
        {
            sumXy += (i - meanX) * ((double)values[i] - meanY);
            sumXx += (i - meanX) * (i - meanX);
        }

        var slope = sumXx == 0 ? 0 : sumXy / sumXx;
        var intercept = meanY - (slope * meanX);
        var projected = intercept + (slope * n); // one step past the last index (n-1 + 1)

        return (decimal)projected;
    }
}
