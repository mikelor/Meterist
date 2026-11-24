namespace Meterist.Core.Reporting;

/// <summary>One tenant's report data across every vendor in <see cref="Vendors.VendorCatalog.All"/>.</summary>
public sealed class TenantReportData
{
    public required string TenantId { get; init; }

    public required IReadOnlyList<VendorReportData> Vendors { get; init; }

    public decimal TotalGrossSpend => Vendors.Sum(v => v.TotalGrossSpend);

    /// <summary>The committed part of the tenant's spend: seat licence charges across every vendor.</summary>
    public decimal TotalSeatFee => Vendors.Sum(v => v.TotalSeatFee);

    /// <summary>The variable part of the tenant's spend: usage and overage across every vendor.</summary>
    public decimal TotalUsageOrOverage => Vendors.Sum(v => v.TotalUsageOrOverage);
}
