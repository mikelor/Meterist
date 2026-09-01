using Meterist.Core.Vendors;

namespace Meterist.Core.Reporting;

/// <summary>Everything a report renderer needs, already aggregated and bucketed.</summary>
public sealed class ReportData
{
    public required DateRange Period { get; init; }

    public required DateOnly WeekAnchor { get; init; }

    public required IReadOnlyList<TenantReportData> Tenants { get; init; }
}
