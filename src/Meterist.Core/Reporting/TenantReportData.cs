namespace Meterist.Core.Reporting;

/// <summary>One tenant's report data across every vendor in <see cref="Vendors.VendorCatalog.All"/>.</summary>
public sealed class TenantReportData
{
    public required string TenantId { get; init; }

    public required IReadOnlyList<VendorReportData> Vendors { get; init; }

    public decimal TotalGrossSpend => Vendors.Sum(v => v.TotalGrossSpend);
}
