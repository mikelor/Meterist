using Meterist.Core.Models;
using Meterist.Core.Persistence;
using Meterist.Core.Vendors;
using Microsoft.EntityFrameworkCore;

namespace Meterist.Data;

/// <summary>
/// Never logs a record or any of its fields: they include names and emails.
/// </summary>
public sealed class EfUserDailyUsageRepository : IUserDailyUsageRepository
{
    private readonly MeteristDbContext _context;

    public EfUserDailyUsageRepository(MeteristDbContext context)
    {
        _context = context;
    }

    public async Task<UserUsageUpsertResult> UpsertAsync(
        IEnumerable<UserDailyUsageRecord> records, CancellationToken cancellationToken = default)
    {
        var result = await ResolveAsync(records, apply: true, cancellationToken).ConfigureAwait(false);
        await _context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }

    public Task<UserUsageUpsertResult> PlanUpsertAsync(
        IEnumerable<UserDailyUsageRecord> records, CancellationToken cancellationToken = default) =>
        ResolveAsync(records, apply: false, cancellationToken);

    public async Task<IReadOnlyList<UserDailyUsageRecord>> GetAsync(
        string tenantId, Guid vendorId, DateRange range, CancellationToken cancellationToken = default) =>
        await _context.UserDailyUsageRecords
            .AsNoTracking()
            .Where(r => r.TenantId == tenantId && r.VendorId == vendorId && r.Date >= range.Start && r.Date <= range.End)
            .OrderBy(r => r.Date)
            .ThenBy(r => r.UserId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

    // One routine for the real upsert and the dry-run plan, as in EfDailySpendRepository, so a
    // dry run cannot predict something the real run then does not do.
    private async Task<UserUsageUpsertResult> ResolveAsync(
        IEnumerable<UserDailyUsageRecord> records, bool apply, CancellationToken cancellationToken)
    {
        var batch = records.ToList();

        // Validate the whole batch before touching the shared context. Two rows for one key would
        // make the outcome depend on order (and, on a first insert, violate the unique index), so
        // refuse rather than pick one silently. Doing it up front means a rejected batch stages
        // nothing for a later SaveChanges on the same context to persist. The message names the
        // date only: the user is personal data.
        var seen = new HashSet<(string, Guid, DateOnly, string)>();
        foreach (var record in batch)
        {
            if (!seen.Add((record.TenantId, record.VendorId, record.Date, record.UserId)))
            {
                throw new InvalidOperationException(
                    "Per-user usage batch holds more than one row for the same tenant, vendor, date and user "
                    + $"({record.Date:yyyy-MM-dd}); refusing to guess which is right.");
            }
        }

        var inserted = 0;
        var updated = 0;
        var unchanged = 0;
        var staged = new List<object>();
        var source = apply ? _context.UserDailyUsageRecords : _context.UserDailyUsageRecords.AsNoTracking();

        try
        {
            foreach (var record in batch)
            {
                var existing = await source.FirstOrDefaultAsync(
                    r => r.TenantId == record.TenantId
                        && r.VendorId == record.VendorId
                        && r.Date == record.Date
                        && r.UserId == record.UserId,
                    cancellationToken).ConfigureAwait(false);

                if (existing is null)
                {
                    inserted++;
                    if (apply)
                    {
                        _context.UserDailyUsageRecords.Add(record);
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
            // A failure part-way (a cancelled or failed lookup) must not leave this batch's earlier
            // rows tracked for the next SaveChanges. Only this batch's entries are detached, not
            // whatever else the shared context is tracking.
            foreach (var entity in staged)
            {
                _context.Entry(entity).State = EntityState.Detached;
            }

            throw;
        }

        return new UserUsageUpsertResult(inserted, updated, unchanged);
    }
    private static bool IsIdentical(UserDailyUsageRecord a, UserDailyUsageRecord b) =>
        a.UserEmail == b.UserEmail
        && a.UserName == b.UserName
        && a.Amount == b.Amount
        && a.Credits == b.Credits
        && a.Requests == b.Requests
        && a.IsEstimated == b.IsEstimated
        && a.Attribution == b.Attribution;
}
