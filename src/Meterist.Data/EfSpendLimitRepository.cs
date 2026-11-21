using Meterist.Core.Models;
using Meterist.Core.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Meterist.Data;

/// <summary>
/// Never logs a limit row or any of its fields: they include names and emails.
/// </summary>
public sealed class EfSpendLimitRepository : ISpendLimitRepository
{
    private readonly MeteristDbContext _context;

    public EfSpendLimitRepository(MeteristDbContext context)
    {
        _context = context;
    }

    public async Task<SpendLimitSnapshotResult> ReplaceSnapshotAsync(
        string tenantId,
        Guid vendorId,
        DateOnly snapshotDate,
        IEnumerable<SpendLimitRecord> limits,
        CancellationToken cancellationToken = default)
    {
        var result = await ResolveAsync(tenantId, vendorId, snapshotDate, limits, apply: true, cancellationToken)
            .ConfigureAwait(false);

        try
        {
            await _context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // Several vendors and tenants share one context in a run: a failed save must not leave its rows tracked
            // for the next save to retry.
            foreach (var entry in _context.ChangeTracker.Entries<SpendLimitRecord>().ToList())
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

    public Task<SpendLimitSnapshotResult> PlanReplaceSnapshotAsync(
        string tenantId,
        Guid vendorId,
        DateOnly snapshotDate,
        IEnumerable<SpendLimitRecord> limits,
        CancellationToken cancellationToken = default) =>
        ResolveAsync(tenantId, vendorId, snapshotDate, limits, apply: false, cancellationToken);

    public async Task<SpendLimitSnapshot?> GetLatestSnapshotAsync(
        string tenantId, Guid vendorId, DateOnly? onOrBefore = null, CancellationToken cancellationToken = default)
    {
        var latest = await _context.SpendLimitRecords
            .AsNoTracking()
            .Where(r => r.TenantId == tenantId && r.VendorId == vendorId && (onOrBefore == null || r.SnapshotDate <= onOrBefore))
            .OrderByDescending(r => r.SnapshotDate)
            .Select(r => (DateOnly?)r.SnapshotDate)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        if (latest is null)
        {
            return null;
        }

        var rows = await _context.SpendLimitRecords
            .AsNoTracking()
            .Where(r => r.TenantId == tenantId && r.VendorId == vendorId && r.SnapshotDate == latest)
            .OrderBy(r => r.MemberId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return new SpendLimitSnapshot(latest.Value, rows);
    }

    // One routine for the real replace and the dry-run plan, so a dry run cannot predict something the real run
    // then does not do.
    private async Task<SpendLimitSnapshotResult> ResolveAsync(
        string tenantId,
        Guid vendorId,
        DateOnly snapshotDate,
        IEnumerable<SpendLimitRecord> limits,
        bool apply,
        CancellationToken cancellationToken)
    {
        var batch = limits.ToList();

        // Validate the whole batch before touching the shared context, so a rejected batch stages nothing. Messages
        // name no member: they are personal data.
        var incoming = new Dictionary<string, SpendLimitRecord>(StringComparer.Ordinal);
        foreach (var row in batch)
        {
            if (row.TenantId != tenantId || row.VendorId != vendorId || row.SnapshotDate != snapshotDate)
            {
                throw new InvalidOperationException(
                    "A spend-limit batch holds a row for a different tenant, vendor or snapshot date than the one being stored.");
            }

            if (string.IsNullOrWhiteSpace(row.MemberId))
            {
                throw new InvalidOperationException("A spend-limit batch holds a row with no member id.");
            }

            if (!incoming.TryAdd(row.MemberId, row))
            {
                throw new InvalidOperationException(
                    $"A spend-limit batch holds the same member id more than once ({snapshotDate:yyyy-MM-dd}); "
                    + "refusing to guess which is right.");
            }
        }

        var query = apply ? _context.SpendLimitRecords : _context.SpendLimitRecords.AsNoTracking();
        var existing = (await query
                .Where(r => r.TenantId == tenantId && r.VendorId == vendorId && r.SnapshotDate == snapshotDate)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false))
            .ToDictionary(r => r.MemberId, StringComparer.Ordinal);

        var inserted = 0;
        var updated = 0;
        var unchanged = 0;

        foreach (var (id, row) in incoming)
        {
            if (!existing.Remove(id, out var current))
            {
                inserted++;
                if (apply)
                {
                    _context.SpendLimitRecords.Add(row);
                }
            }
            else if (IsIdentical(current, row))
            {
                unchanged++;
            }
            else
            {
                updated++;
                if (apply)
                {
                    _context.Entry(current).CurrentValues.SetValues(row);
                }
            }
        }

        // Left over: on the stored snapshot for this day but not in the new one, so no longer a member.
        var removed = existing.Count;
        if (apply && removed > 0)
        {
            _context.SpendLimitRecords.RemoveRange(existing.Values);
        }

        return new SpendLimitSnapshotResult(inserted, updated, unchanged, removed);
    }

    private static bool IsIdentical(SpendLimitRecord a, SpendLimitRecord b) =>
        a.Email == b.Email
        && a.Name == b.Name
        && a.LimitAmount == b.LimitAmount
        && a.PeriodToDateSpend == b.PeriodToDateSpend
        && a.PriorPeriodSpend == b.PriorPeriodSpend
        && a.Currency == b.Currency
        && a.Period == b.Period
        && a.SourceType == b.SourceType
        && a.SourceDetail == b.SourceDetail
        && a.SpendLimitId == b.SpendLimitId;
}
