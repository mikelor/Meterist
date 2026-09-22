using Meterist.Core.Persistence;
using Meterist.Core.Vendors;

namespace Meterist.Core.Reporting;

/// <summary>
/// Queries the existing single-tenant/single-vendor repository surface once
/// per (tenant, vendor) pair — no new bulk-query repository method needed for
/// a once-per-report call — and assembles it into <see cref="ReportData"/>.
/// </summary>
public sealed class ReportDataAggregator
{
    // The one hardcoded exclusion this session decided on: ChatGPT Enterprise's
    // first week in any report period is permanently lost to the vendor's
    // 29-day retention window and must never feed Flat/Trend/Latest-week
    // projections. See docs/vendor-integration-reference.md.
    private static readonly IReadOnlySet<int> ChatGptExcludedWeekIndices = new HashSet<int> { 0 };

    private readonly IDailySpendRepository _dailySpendRepository;
    private readonly IVendorRateConfigRepository _vendorRateConfigRepository;

    public ReportDataAggregator(
        IDailySpendRepository dailySpendRepository,
        IVendorRateConfigRepository vendorRateConfigRepository)
    {
        _dailySpendRepository = dailySpendRepository;
        _vendorRateConfigRepository = vendorRateConfigRepository;
    }

    public async Task<ReportData> AggregateAsync(
        IReadOnlyList<string> tenantIds,
        DateRange period,
        DateTime? generatedAt = null,
        CancellationToken cancellationToken = default)
    {
        var anchor = WeeklyBucketer.AnchorWednesdayOnOrBefore(period.Start);
        var tenantReports = new List<TenantReportData>(tenantIds.Count);

        foreach (var tenantId in tenantIds)
        {
            var vendorReports = new List<VendorReportData>(VendorCatalog.All.Count);

            foreach (var vendor in VendorCatalog.All)
            {
                var records = await _dailySpendRepository
                    .GetAsync(tenantId, vendor.Id, period, cancellationToken)
                    .ConfigureAwait(false);

                var excludeIndices = vendor.Id == VendorCatalog.ChatGptEnterprise.Id
                    ? ChatGptExcludedWeekIndices
                    : null;
                var weeks = WeeklyBucketer.Bucket(records, anchor, excludeIndices);

                var rates = await _vendorRateConfigRepository
                    .GetApplicableRatesAsync(tenantId, vendor.Id, period, cancellationToken)
                    .ConfigureAwait(false);
                var currentRates = rates.Where(r => r.EffectiveTo is null).ToList();

                vendorReports.Add(new VendorReportData
                {
                    Vendor = vendor,
                    Weeks = weeks,
                    CurrentRates = currentRates,
                });
            }

            tenantReports.Add(new TenantReportData { TenantId = tenantId, Vendors = vendorReports });
        }

        return new ReportData
        {
            Period = period,
            WeekAnchor = anchor,
            Tenants = tenantReports,
            GeneratedAt = generatedAt ?? DateTime.Now,
        };
    }
}
