using Meterist.Core.Models;
using Meterist.Core.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Meterist.Data;

/// <summary>
/// Never logs a member or any of their fields: they include names and emails.
/// </summary>
public sealed class EfMemberRosterRepository : IMemberRosterRepository
{
    private readonly MeteristDbContext _context;

    public EfMemberRosterRepository(MeteristDbContext context)
    {
        _context = context;
    }

    public async Task<RosterSnapshotResult> ReplaceSnapshotAsync(
        string tenantId,
        Guid vendorId,
        DateOnly snapshotDate,
        IEnumerable<MemberRosterRecord> members,
        CancellationToken cancellationToken = default)
    {
        var result = await ResolveAsync(tenantId, vendorId, snapshotDate, members, apply: true, cancellationToken)
            .ConfigureAwait(false);

        try
        {
            await _context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // A failed save leaves its additions, changes and removals tracked. Several vendors and tenants share one
            // context in a scheduled run, so the next snapshot's save would retry this one's rows (and fail the same
            // way, or worse, persist rows from a snapshot that was reported as failed). Drop them.
            DetachPendingChanges();
            throw;
        }

        return result;
    }

    private void DetachPendingChanges()
    {
        foreach (var entry in _context.ChangeTracker.Entries<MemberRosterRecord>().ToList())
        {
            if (entry.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)
            {
                entry.State = EntityState.Detached;
            }
        }
    }

    public Task<RosterSnapshotResult> PlanReplaceSnapshotAsync(
        string tenantId,
        Guid vendorId,
        DateOnly snapshotDate,
        IEnumerable<MemberRosterRecord> members,
        CancellationToken cancellationToken = default) =>
        ResolveAsync(tenantId, vendorId, snapshotDate, members, apply: false, cancellationToken);

    public async Task<RosterSnapshot?> GetLatestSnapshotAsync(
        string tenantId, Guid vendorId, DateOnly? onOrBefore = null, CancellationToken cancellationToken = default)
    {
        var dates = _context.MemberRosterRecords
            .AsNoTracking()
            .Where(r => r.TenantId == tenantId && r.VendorId == vendorId && (onOrBefore == null || r.SnapshotDate <= onOrBefore));

        var latest = await dates
            .OrderByDescending(r => r.SnapshotDate)
            .Select(r => (DateOnly?)r.SnapshotDate)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        if (latest is null)
        {
            return null;
        }

        var members = await _context.MemberRosterRecords
            .AsNoTracking()
            .Where(r => r.TenantId == tenantId && r.VendorId == vendorId && r.SnapshotDate == latest)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return new RosterSnapshot(
            latest.Value,
            members
                .OrderBy(m => m.Name ?? m.Email ?? m.MemberId, StringComparer.OrdinalIgnoreCase)
                .ThenBy(m => m.Email, StringComparer.OrdinalIgnoreCase)
                .ThenBy(m => m.MemberId, StringComparer.Ordinal)
                .ToList());
    }

    public async Task<IReadOnlyList<DateOnly>> GetSnapshotDatesAsync(
        string tenantId, Guid vendorId, CancellationToken cancellationToken = default) =>
        await _context.MemberRosterRecords
            .AsNoTracking()
            .Where(r => r.TenantId == tenantId && r.VendorId == vendorId)
            .Select(r => r.SnapshotDate)
            .Distinct()
            .OrderBy(d => d)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

    // One routine for the real replace and the dry-run plan, so a dry run cannot predict something the
    // real run then does not do.
    private async Task<RosterSnapshotResult> ResolveAsync(
        string tenantId,
        Guid vendorId,
        DateOnly snapshotDate,
        IEnumerable<MemberRosterRecord> members,
        bool apply,
        CancellationToken cancellationToken)
    {
        var batch = members.ToList();

        // Validate the whole batch before touching the shared context, so a rejected batch stages nothing for a
        // later SaveChanges on the same context to persist. Messages name no member: they are personal data.
        var incoming = new Dictionary<string, MemberRosterRecord>(StringComparer.Ordinal);
        foreach (var member in batch)
        {
            if (member.TenantId != tenantId || member.VendorId != vendorId || member.SnapshotDate != snapshotDate)
            {
                throw new InvalidOperationException(
                    "A roster batch holds a row for a different tenant, vendor or snapshot date than the one being stored.");
            }

            if (string.IsNullOrWhiteSpace(member.MemberId))
            {
                throw new InvalidOperationException("A roster batch holds a member with no id.");
            }

            if (!incoming.TryAdd(member.MemberId, member))
            {
                throw new InvalidOperationException(
                    $"A roster batch holds the same member id more than once ({snapshotDate:yyyy-MM-dd}); "
                    + "refusing to guess which is right.");
            }
        }

        var query = apply ? _context.MemberRosterRecords : _context.MemberRosterRecords.AsNoTracking();
        var existing = (await query
                .Where(r => r.TenantId == tenantId && r.VendorId == vendorId && r.SnapshotDate == snapshotDate)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false))
            .ToDictionary(r => r.MemberId, StringComparer.Ordinal);

        var inserted = 0;
        var updated = 0;
        var unchanged = 0;

        foreach (var (id, member) in incoming)
        {
            if (!existing.Remove(id, out var current))
            {
                inserted++;
                if (apply)
                {
                    _context.MemberRosterRecords.Add(member);
                }
            }
            else if (IsIdentical(current, member))
            {
                unchanged++;
            }
            else
            {
                updated++;
                if (apply)
                {
                    _context.Entry(current).CurrentValues.SetValues(member);
                }
            }
        }

        // Left over: on the stored snapshot for this day but not in the new one, so no longer a member.
        var removed = existing.Count;
        if (apply && removed > 0)
        {
            _context.MemberRosterRecords.RemoveRange(existing.Values);
        }

        return new RosterSnapshotResult(inserted, updated, unchanged, removed);
    }

    private static bool IsIdentical(MemberRosterRecord a, MemberRosterRecord b) =>
        a.Email == b.Email
        && a.Name == b.Name
        && a.Role == b.Role
        && a.SeatType == b.SeatType
        && a.IsScimManaged == b.IsScimManaged
        && a.AddedAt == b.AddedAt;
}
