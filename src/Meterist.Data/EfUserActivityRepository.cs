using Meterist.Core.Models;
using Meterist.Core.Persistence;
using Meterist.Core.Vendors;
using Microsoft.EntityFrameworkCore;

namespace Meterist.Data;

/// <summary>Never logs a row or any of its fields: they include emails.</summary>
public sealed class EfUserActivityRepository : IUserActivityRepository
{
    private readonly MeteristDbContext _context;

    public EfUserActivityRepository(MeteristDbContext context)
    {
        _context = context;
    }

    public async Task<UserActivityReplaceResult> ReplaceDaysAsync(
        string tenantId,
        Guid vendorId,
        IReadOnlyCollection<DateOnly> fetchedDays,
        IEnumerable<UserActivityRecord> records,
        CancellationToken cancellationToken = default)
    {
        var result = await ResolveAsync(tenantId, vendorId, fetchedDays, records, apply: true, cancellationToken)
            .ConfigureAwait(false);

        try
        {
            await _context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // Several vendors and tenants share one context in a run: a failed save must not leave its rows tracked for
            // the next save to retry.
            foreach (var entry in _context.ChangeTracker.Entries<UserActivityRecord>().ToList())
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

    public Task<UserActivityReplaceResult> PlanReplaceDaysAsync(
        string tenantId,
        Guid vendorId,
        IReadOnlyCollection<DateOnly> fetchedDays,
        IEnumerable<UserActivityRecord> records,
        CancellationToken cancellationToken = default) =>
        ResolveAsync(tenantId, vendorId, fetchedDays, records, apply: false, cancellationToken);

    public async Task<IReadOnlyList<UserActivityRecord>> GetAsync(
        string tenantId, Guid vendorId, DateRange range, CancellationToken cancellationToken = default) =>
        await _context.UserActivityRecords
            .AsNoTracking()
            .Where(r => r.TenantId == tenantId && r.VendorId == vendorId && r.Date >= range.Start && r.Date <= range.End)
            .OrderBy(r => r.Date)
            .ThenBy(r => r.UserId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

    // One routine for the real replace and the dry-run plan, so a dry run cannot predict something the real run then does not do.
    private async Task<UserActivityReplaceResult> ResolveAsync(
        string tenantId,
        Guid vendorId,
        IReadOnlyCollection<DateOnly> fetchedDays,
        IEnumerable<UserActivityRecord> records,
        bool apply,
        CancellationToken cancellationToken)
    {
        var batch = records.ToList();
        var covered = fetchedDays.ToHashSet();

        // Validate the whole batch before touching the shared context, so a rejected batch stages nothing. Messages name
        // no user: emails are personal data.
        var incoming = new Dictionary<(DateOnly, string), UserActivityRecord>();
        foreach (var record in batch)
        {
            if (record.TenantId != tenantId || record.VendorId != vendorId)
            {
                throw new InvalidOperationException(
                    "A user-activity batch holds a row for a different tenant or vendor than the one being replaced.");
            }

            if (!covered.Contains(record.Date))
            {
                throw new InvalidOperationException(
                    $"A user-activity row is dated {record.Date:yyyy-MM-dd}, which the batch does not declare it fetched.");
            }

            if (string.IsNullOrWhiteSpace(record.UserId))
            {
                throw new InvalidOperationException($"A user-activity row on {record.Date:yyyy-MM-dd} has no user id.");
            }

            if (!incoming.TryAdd((record.Date, record.UserId), record))
            {
                throw new InvalidOperationException(
                    $"A user-activity batch holds more than one row for {record.Date:yyyy-MM-dd} and the same user; "
                    + "refusing to guess which is right.");
            }
        }

        if (covered.Count == 0)
        {
            return new UserActivityReplaceResult(0, 0, 0, 0);
        }

        var first = covered.Min();
        var last = covered.Max();

        var query = apply ? _context.UserActivityRecords : _context.UserActivityRecords.AsNoTracking();
        var existing = (await query
                .Where(r => r.TenantId == tenantId && r.VendorId == vendorId && r.Date >= first && r.Date <= last)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false))
            .Where(r => covered.Contains(r.Date))
            .ToDictionary(r => (r.Date, r.UserId));

        var inserted = 0;
        var updated = 0;
        var unchanged = 0;

        foreach (var (key, record) in incoming)
        {
            if (!existing.Remove(key, out var current))
            {
                inserted++;
                if (apply)
                {
                    _context.UserActivityRecords.Add(record);
                }
            }
            else if (IsIdentical(current, record))
            {
                unchanged++;
            }
            else
            {
                updated++;
                if (apply)
                {
                    _context.Entry(current).CurrentValues.SetValues(record);
                }
            }
        }

        // What is left was stored for a fetched day and is not in the batch: the vendor no longer reports it.
        var removed = existing.Count;
        if (apply && removed > 0)
        {
            _context.UserActivityRecords.RemoveRange(existing.Values);
        }

        return new UserActivityReplaceResult(inserted, updated, unchanged, removed);
    }

    private static bool IsIdentical(UserActivityRecord a, UserActivityRecord b) =>
        a.UserEmail == b.UserEmail
        && a.ChatMessages == b.ChatMessages
        && a.ChatConversations == b.ChatConversations
        && a.ClaudeCodeSessions == b.ClaudeCodeSessions
        && a.ClaudeCodeCommits == b.ClaudeCodeCommits
        && a.ClaudeCodePullRequests == b.ClaudeCodePullRequests
        && a.ClaudeCodeLinesAdded == b.ClaudeCodeLinesAdded
        && a.ClaudeCodeLinesRemoved == b.ClaudeCodeLinesRemoved
        && a.CoworkSessions == b.CoworkSessions
        && a.CoworkActions == b.CoworkActions
        && a.CoworkMessages == b.CoworkMessages
        && a.OfficeSessions == b.OfficeSessions
        && a.OfficeMessages == b.OfficeMessages
        && a.DesignMessages == b.DesignMessages
        && a.ScienceMessages == b.ScienceMessages
        && a.WebSearches == b.WebSearches
        && string.Equals(a.DetailJson, b.DetailJson, StringComparison.Ordinal);
}
