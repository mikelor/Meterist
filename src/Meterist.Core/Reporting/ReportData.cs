using Meterist.Core.Vendors;

namespace Meterist.Core.Reporting;

/// <summary>Everything a report renderer needs, already aggregated and bucketed.</summary>
public sealed class ReportData
{
    public required DateRange Period { get; init; }

    public required DateOnly WeekAnchor { get; init; }

    /// <summary>
    /// What the spend table is grouped by. The projections and the manifest stay weekly whatever this is, so the figures
    /// in them do not change with the choice.
    /// </summary>
    public AggregationPeriod Aggregation { get; init; } = AggregationPeriod.Week;

    public required IReadOnlyList<TenantReportData> Tenants { get; init; }

    public required DateTime GeneratedAt { get; init; }
}
