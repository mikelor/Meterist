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
    private const int DaysInAWeek = 7;

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

                var weeks = WeeklyBucketer.Bucket(records, anchor);

                // ChatGPT Enterprise is seat-priced, so every real day has a record (the seat
                // accrues whether or not anyone used it), which makes a week with fewer than
                // seven recorded days genuinely incomplete: days the vendor's data no longer
                // reaches. Such a week must not feed Flat/Trend/Latest-week projections.
                // This replaces a blanket "week 0 is always excluded", which was written when
                // the COSTS export's 29-day retention made that week unrecoverable; with the
                // Daily Usage API as the source of record, a complete first week is real data.
                // Not applied to usage-priced vendors, where a quiet day has no row at all and
                // a short week is not evidence of missing data.
                if (vendor.Id == VendorCatalog.ChatGptEnterprise.Id)
                {
                    weeks = weeks
                        .Select(w => w with { ExcludeFromProjection = w.ExcludeFromProjection || w.DaysPresent < DaysInAWeek })
                        .ToList();
                }

                var sourceDayCounts = records
                    .Where(r => r.Source is not null)
                    .GroupBy(r => r.Source!)
                    .ToDictionary(g => g.Key, g => g.Count());

                var rates = await _vendorRateConfigRepository
                    .GetApplicableRatesAsync(tenantId, vendor.Id, period, cancellationToken)
                    .ConfigureAwait(false);
                var currentRates = rates.Where(r => r.EffectiveTo is null).ToList();

                vendorReports.Add(new VendorReportData
                {
                    Vendor = vendor,
                    Weeks = weeks,
                    CurrentRates = currentRates,
                    SourceDayCounts = sourceDayCounts,
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
