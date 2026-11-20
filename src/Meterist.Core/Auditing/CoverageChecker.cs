using Meterist.Core.Persistence;
using Meterist.Core.Vendors;

namespace Meterist.Core.Auditing;

/// <summary>
/// What the stored data covers for one tenant and vendor over a period.
/// <para>
/// Missing days fall into two kinds, because they mean different things.
/// <see cref="Gaps"/> are missing days at or after the vendor's first stored
/// record for this tenant: data we should have and don't (a missed run, a failed
/// extraction, a vendor that stopped being pulled). <see cref="NotYetBackfilled"/>
/// is the stretch of the period *before* that first record: history that was
/// never extracted, which is normal for a newly onboarded tenant and is reported
/// but never counted as a problem.
/// </para>
/// </summary>
public sealed record VendorCoverage(
    VendorIdentity Vendor,
    bool HasAnyRecord,
    int DaysInPeriod,
    int DaysStored,
    IReadOnlyList<DateRange> Gaps,
    DateRange? NotYetBackfilled)
{
    public int GapDays => Gaps.Sum(g => g.End.DayNumber - g.Start.DayNumber + 1);

    /// <summary>
    /// A problem is a gap on a vendor that has a record for every real day. A usage-priced
    /// vendor's missing days are normal (no usage, no record), and a vendor with no records at
    /// all is probably unused or never extracted for this tenant, so neither counts.
    /// </summary>
    public bool HasProblem => Vendor.ExpectsRecordEveryDay && Gaps.Count > 0;
}

public sealed record TenantCoverage(string TenantId, IReadOnlyList<VendorCoverage> Vendors)
{
    public bool HasProblems => Vendors.Any(v => v.HasProblem);
}

public sealed record CoverageReport(DateRange Period, IReadOnlyList<TenantCoverage> Tenants)
{
    public bool HasProblems => Tenants.Any(t => t.HasProblems);
}

/// <summary>
/// Reports which days in a period have no stored <c>DailySpendRecord</c>, per tenant and
/// vendor. Exists because a stretch of days that were never extracted looks exactly like a
/// quiet stretch in the data: nothing errors, the totals are simply low, and in some vendors
/// the missed days can no longer be recovered once their retention window passes.
/// A library service rather than CLI logic so a dashboard or scheduled run can use it too.
/// </summary>
public sealed class CoverageChecker
{
    private readonly IDailySpendRepository _dailySpendRepository;

    public CoverageChecker(IDailySpendRepository dailySpendRepository)
    {
        _dailySpendRepository = dailySpendRepository;
    }

    public async Task<CoverageReport> CheckAsync(
        IReadOnlyList<string> tenantIds,
        DateRange period,
        Guid? vendorFilter = null,
        CancellationToken cancellationToken = default)
    {
        var vendors = vendorFilter is null
            ? VendorCatalog.All
            : VendorCatalog.All.Where(v => v.Id == vendorFilter.Value).ToList();

        var tenants = new List<TenantCoverage>(tenantIds.Count);

        foreach (var tenantId in tenantIds)
        {
            var coverage = new List<VendorCoverage>(vendors.Count);

            foreach (var vendor in vendors)
            {
                // All of the tenant's records for this vendor, not just the period's: whether a
                // missing day is a gap or just unbackfilled history depends on where the data
                // first begins, which can lie before the period starts. A handful of rows per
                // tenant and vendor per year, so reading them all is cheap.
                var all = await _dailySpendRepository
                    .GetAsync(tenantId, vendor.Id, new DateRange(DateOnly.MinValue, DateOnly.MaxValue), cancellationToken)
                    .ConfigureAwait(false);

                coverage.Add(Analyze(vendor, all.Select(r => r.Date).ToHashSet(), period));
            }

            tenants.Add(new TenantCoverage(tenantId, coverage));
        }

        return new CoverageReport(period, tenants);
    }

    private static VendorCoverage Analyze(VendorIdentity vendor, HashSet<DateOnly> storedDates, DateRange period)
    {
        var daysInPeriod = period.End.DayNumber - period.Start.DayNumber + 1;

        if (storedDates.Count == 0)
        {
            return new VendorCoverage(vendor, HasAnyRecord: false, daysInPeriod, 0, [], null);
        }

        var firstRecord = storedDates.Min();
        var gapDays = new List<DateOnly>();
        var leadingDays = new List<DateOnly>();
        var stored = 0;

        foreach (var day in period.EnumerateDays())
        {
            if (storedDates.Contains(day))
            {
                stored++;
            }
            else if (day < firstRecord && vendor.ExpectsRecordEveryDay)
            {
                // Only meaningful where a record exists every day: the vendor must have been
                // pulled by then, so days before its first record are history never extracted.
                // A usage-priced vendor's first record is just its first day with usage, so the
                // days before it are ordinary quiet days and fall through to the case below.
                leadingDays.Add(day);
            }
            else
            {
                gapDays.Add(day);
            }
        }

        DateRange? notYetBackfilled = leadingDays.Count == 0
            ? null
            : new DateRange(leadingDays[0], leadingDays[^1]);

        return new VendorCoverage(vendor, HasAnyRecord: true, daysInPeriod, stored, ToRanges(gapDays), notYetBackfilled);
    }

    // Consecutive days collapse into one range, so eight missing days in a row read as one gap.
    private static List<DateRange> ToRanges(List<DateOnly> orderedDays)
    {
        var ranges = new List<DateRange>();
        if (orderedDays.Count == 0)
        {
            return ranges;
        }

        var start = orderedDays[0];
        var previous = start;

        foreach (var day in orderedDays.Skip(1))
        {
            if (day.DayNumber != previous.DayNumber + 1)
            {
                ranges.Add(new DateRange(start, previous));
                start = day;
            }

            previous = day;
        }

        ranges.Add(new DateRange(start, previous));
        return ranges;
    }
}
