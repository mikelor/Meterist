using Meterist.Core.Models;
using Meterist.Core.Persistence;
using Meterist.Core.Vendors;

namespace Meterist.Core.Reporting;

/// <summary>
/// Builds the per-user view of a tenant's usage from the stored per-user rows, and compares each
/// vendor's per-user dollars with its aggregate daily usage so the two can be read side by side.
/// Vendors with no per-user rows in the period are left out of the report.
/// </summary>
public sealed class UserUsageReporter
{
    // Per-user dollars are priced the way the daily total is, so they should agree to the cent. The
    // allowance covers rounding, users the vendor has since deleted (ChatGPT omits them from the
    // per-user grouping), and days whose stored aggregate kept a higher earlier figure.
    private const decimal ToleranceAbsolute = 0.05m;
    private const decimal ToleranceRelative = 0.02m;

    private readonly IUserDailyUsageRepository _userUsage;
    private readonly IDailySpendRepository _dailySpend;

    public UserUsageReporter(IUserDailyUsageRepository userUsage, IDailySpendRepository dailySpend)
    {
        _userUsage = userUsage;
        _dailySpend = dailySpend;
    }

    public async Task<UserUsageReport> BuildAsync(
        string tenantId, DateRange period, Guid? vendorFilter = null, CancellationToken cancellationToken = default)
    {
        var vendors = new List<VendorUserUsage>();

        foreach (var vendor in VendorCatalog.All.Where(v => vendorFilter is null || v.Id == vendorFilter))
        {
            var rows = await _userUsage.GetAsync(tenantId, vendor.Id, period, cancellationToken).ConfigureAwait(false);
            if (rows.Count == 0)
            {
                continue;
            }

            var daily = await _dailySpend.GetAsync(tenantId, vendor.Id, period, cancellationToken).ConfigureAwait(false);
            vendors.Add(BuildVendor(vendor, rows, daily));
        }

        return new UserUsageReport(tenantId, period, vendors);
    }

    private static VendorUserUsage BuildVendor(
        VendorIdentity vendor, IReadOnlyList<UserDailyUsageRecord> rows, IReadOnlyList<DailySpendRecord> daily)
    {
        var users = rows
            .GroupBy(r => r.UserId, StringComparer.Ordinal)
            .Select(g =>
            {
                // Identity and attribution come from the most recent row, so a renamed mailbox or a
                // reclassified actor shows its current state.
                var latest = g.OrderByDescending(r => r.Date).First();
                return new UserUsageSummary(
                    g.Key,
                    g.Select(r => r.UserEmail).FirstOrDefault(e => !string.IsNullOrWhiteSpace(e)) ?? latest.UserEmail,
                    g.OrderByDescending(r => r.Date).Select(r => r.UserName).FirstOrDefault(n => !string.IsNullOrWhiteSpace(n)),
                    latest.Attribution,
                    g.Sum(r => r.Amount),
                    g.Any(r => r.Credits is not null) ? g.Sum(r => r.Credits ?? 0m) : null,
                    g.Any(r => r.Requests is not null) ? g.Sum(r => r.Requests ?? 0L) : null,
                    g.Select(r => r.Date).Distinct().Count(),
                    g.Any(r => r.IsEstimated));
            })
            .OrderByDescending(u => u.Amount)
            .ThenByDescending(u => u.Requests ?? 0L)
            .ThenBy(u => u.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var daysWithUserRows = rows.Select(r => r.Date).ToHashSet();
        var comparedDaily = daily.Where(d => daysWithUserRows.Contains(d.Date)).ToList();

        var perUserAmount = rows.Sum(r => r.Amount);
        var aggregateOnComparedDays = comparedDaily.Sum(d => d.UsageOrOverage);

        var tolerance = Math.Max(ToleranceAbsolute, ToleranceRelative * Math.Max(perUserAmount, aggregateOnComparedDays));
        var reconciliation = new UserUsageReconciliation(
            daysWithUserRows.Count,
            perUserAmount,
            aggregateOnComparedDays,
            daily.Count(d => d.UsageOrOverage > 0m && !daysWithUserRows.Contains(d.Date)),
            Math.Abs(perUserAmount - aggregateOnComparedDays) <= tolerance);

        return new VendorUserUsage(
            vendor,
            users,
            daily.Sum(d => d.SeatFee),
            daily.Sum(d => d.UsageOrOverage),
            reconciliation);
    }
}
