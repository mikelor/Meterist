using Meterist.Core.Models;
using Meterist.Core.Persistence;
using Meterist.Core.Vendors;
using Microsoft.EntityFrameworkCore;

namespace Meterist.Data;

public sealed class EfActivitySummaryRepository : IActivitySummaryRepository
{
    private readonly MeteristDbContext _context;

    public EfActivitySummaryRepository(MeteristDbContext context)
    {
        _context = context;
    }

    public async Task<ActivitySummaryUpsertResult> UpsertAsync(
        IEnumerable<ActivitySummaryRecord> records, CancellationToken cancellationToken = default)
    {
        var result = await ResolveAsync(records, apply: true, cancellationToken).ConfigureAwait(false);

        try
        {
            await _context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // Several vendors and tenants share one context in a run: a failed save must not leave its rows tracked
            // for the next save to retry.
            foreach (var entry in _context.ChangeTracker.Entries<ActivitySummaryRecord>().ToList())
            {
                if (entry.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)
                {
                    entry.State = EntityState.Detached;
                }
            }

            throw;
        }

        return result;
    }

    public Task<ActivitySummaryUpsertResult> PlanUpsertAsync(
        IEnumerable<ActivitySummaryRecord> records, CancellationToken cancellationToken = default) =>
        ResolveAsync(records, apply: false, cancellationToken);

    public async Task<IReadOnlyList<ActivitySummaryRecord>> GetAsync(
        string tenantId, Guid vendorId, DateRange range, CancellationToken cancellationToken = default) =>
        await _context.ActivitySummaryRecords
            .AsNoTracking()
            .Where(r => r.TenantId == tenantId && r.VendorId == vendorId && r.Date >= range.Start && r.Date <= range.End)
            .OrderBy(r => r.Date)
            .ThenBy(r => r.Product)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

    // One routine for the real upsert and the dry-run plan, so a dry run cannot predict something the real run
    // then does not do.
    private async Task<ActivitySummaryUpsertResult> ResolveAsync(
        IEnumerable<ActivitySummaryRecord> records, bool apply, CancellationToken cancellationToken)
    {
        var batch = records.ToList();

        // The whole batch is validated before the shared context is touched, so a rejected batch stages nothing.
        var seen = new HashSet<(string, Guid, DateOnly, string)>();
        foreach (var record in batch)
        {
            if (string.IsNullOrWhiteSpace(record.Product))
            {
                throw new InvalidOperationException(
                    $"An activity summary for {record.Date:yyyy-MM-dd} has no product; use "
                    + $"\"{ActivitySummaryRecord.AllProducts}\" for the whole organisation.");
            }

            if (!seen.Add((record.TenantId, record.VendorId, record.Date, record.Product)))
            {
                throw new InvalidOperationException(
                    "Activity summary batch holds more than one row for the same tenant, vendor, date and product "
                    + $"({record.Date:yyyy-MM-dd}, {record.Product}); refusing to guess which is right.");
            }
        }

        var inserted = 0;
        var updated = 0;
        var unchanged = 0;
        var staged = new List<object>();
        var source = apply ? _context.ActivitySummaryRecords : _context.ActivitySummaryRecords.AsNoTracking();

        try
        {
            foreach (var record in batch)
            {
                var existing = await source.FirstOrDefaultAsync(
                    r => r.TenantId == record.TenantId
                        && r.VendorId == record.VendorId
                        && r.Date == record.Date
                        && r.Product == record.Product,
                    cancellationToken).ConfigureAwait(false);

                if (existing is null)
                {
                    inserted++;
                    if (apply)
                    {
                        _context.ActivitySummaryRecords.Add(record);
                        staged.Add(record);
                    }
                }
                else if (IsIdentical(existing, record))
                {
                    unchanged++;
                }
                else
                {
                    updated++;
                    if (apply)
                    {
                        _context.Entry(existing).CurrentValues.SetValues(record);
                        staged.Add(existing);
                    }
                }
            }
        }
        catch
        {
            foreach (var entity in staged)
            {
                _context.Entry(entity).State = EntityState.Detached;
            }

            throw;
        }

        return new ActivitySummaryUpsertResult(inserted, updated, unchanged);
    }

    private static bool IsIdentical(ActivitySummaryRecord a, ActivitySummaryRecord b) =>
        a.DailyActiveUsers == b.DailyActiveUsers
        && a.WeeklyActiveUsers == b.WeeklyActiveUsers
        && a.MonthlyActiveUsers == b.MonthlyActiveUsers
        && a.AssignedSeatCount == b.AssignedSeatCount
        && a.PendingInviteCount == b.PendingInviteCount
        && a.DailyAdoptionRate == b.DailyAdoptionRate
        && a.WeeklyAdoptionRate == b.WeeklyAdoptionRate
        && a.MonthlyAdoptionRate == b.MonthlyAdoptionRate;
}
