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
    private readonly IDailySpendRepository _dailySpendRepository;
    private readonly IVendorRateConfigRepository _vendorRateConfigRepository;
    private readonly IDailySpendBreakdownRepository? _breakdownRepository;

    public ReportDataAggregator(
        IDailySpendRepository dailySpendRepository,
        IVendorRateConfigRepository vendorRateConfigRepository,
        IDailySpendBreakdownRepository? breakdownRepository = null)
    {
        _dailySpendRepository = dailySpendRepository;
        _vendorRateConfigRepository = vendorRateConfigRepository;
        _breakdownRepository = breakdownRepository;
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
                // accrues whether or not anyone used it). A week is therefore missing data when
                // it has fewer stored days than it has days *inside the report period*, and such
                // a week must not feed Flat/Trend/Latest-week projections. Comparing with the
                // days inside the period, not a flat seven, matters: weeks are anchored to a
                // Wednesday, so a report that starts or ends midweek has a short boundary week
                // even when every requested day is stored, and that is where the report starts,
                // not a gap.
                // This replaces a blanket "week 0 is always excluded", which was written when
                // the COSTS export's 29-day retention made that week unrecoverable; with the
                // Daily Usage API as the source of record, a complete first week is real data.
                // Not applied to usage-priced vendors, where a quiet day has no row at all and
                // a short week is not evidence of missing data.
                if (vendor.Id == VendorCatalog.ChatGptEnterprise.Id)
                {
                    weeks = weeks
                        .Select(w => w with
                        {
                            ExcludeFromProjection = w.ExcludeFromProjection || w.DaysPresent < DaysInsidePeriod(w, period),
                        })
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

                var breakdown = _breakdownRepository is null
                    ? []
                    : SpendBreakdownSummary.Summarize(await _breakdownRepository
                        .GetAsync(tenantId, vendor.Id, period, cancellationToken)
                        .ConfigureAwait(false));

                vendorReports.Add(new VendorReportData
                {
                    Vendor = vendor,
                    Weeks = weeks,
                    CurrentRates = currentRates,
                    SourceDayCounts = sourceDayCounts,
                    Breakdown = breakdown,
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

    // How many of a week's seven days fall inside the report period: seven for a week wholly
    // inside it, fewer for the boundary week a mid-week start or end cuts short.
    private static int DaysInsidePeriod(WeeklyTotal week, DateRange period)
    {
        var from = week.WeekStart > period.Start ? week.WeekStart : period.Start;
        var to = week.WeekEnd < period.End ? week.WeekEnd : period.End;
        return Math.Max(0, to.DayNumber - from.DayNumber + 1);
    }
}
