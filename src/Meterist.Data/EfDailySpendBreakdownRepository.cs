using Meterist.Core.Models;
using Meterist.Core.Persistence;
using Meterist.Core.Vendors;
using Microsoft.EntityFrameworkCore;

namespace Meterist.Data;

public sealed class EfDailySpendBreakdownRepository : IDailySpendBreakdownRepository
{
    private readonly MeteristDbContext _context;

    public EfDailySpendBreakdownRepository(MeteristDbContext context)
    {
        _context = context;
    }

    public async Task<BreakdownReplaceResult> ReplaceAsync(
        string tenantId,
        Guid vendorId,
        IReadOnlyCollection<DateOnly> coveredDays,
        IEnumerable<DailySpendBreakdownRecord> records,
        CancellationToken cancellationToken = default)
    {
        var result = await ResolveAsync(tenantId, vendorId, coveredDays, records, apply: true, cancellationToken)
            .ConfigureAwait(false);
        await _context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }

    public Task<BreakdownReplaceResult> PlanReplaceAsync(
        string tenantId,
        Guid vendorId,
        IReadOnlyCollection<DateOnly> coveredDays,
        IEnumerable<DailySpendBreakdownRecord> records,
        CancellationToken cancellationToken = default) =>
        ResolveAsync(tenantId, vendorId, coveredDays, records, apply: false, cancellationToken);

    public async Task<IReadOnlyList<DailySpendBreakdownRecord>> GetAsync(
        string tenantId, Guid vendorId, DateRange range, CancellationToken cancellationToken = default) =>
        await _context.DailySpendBreakdownRecords
            .AsNoTracking()
            .Where(r => r.TenantId == tenantId && r.VendorId == vendorId && r.Date >= range.Start && r.Date <= range.End)
            .OrderBy(r => r.Date)
            .ThenBy(r => r.Category)
            .ThenBy(r => r.Detail)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

    // One routine for the real replace and the dry-run plan, so a dry run cannot predict something the
    // real run then does not do.
    private async Task<BreakdownReplaceResult> ResolveAsync(
        string tenantId,
        Guid vendorId,
        IReadOnlyCollection<DateOnly> coveredDays,
        IEnumerable<DailySpendBreakdownRecord> records,
        bool apply,
        CancellationToken cancellationToken)
    {
        var batch = records.ToList();
        var covered = coveredDays.ToHashSet();

        // Validate the whole batch before touching the shared context, so a rejected batch stages
        // nothing for a later SaveChanges on the same context to persist.
        var incoming = new Dictionary<(DateOnly, string, string), DailySpendBreakdownRecord>();
        foreach (var record in batch)
        {
            if (record.TenantId != tenantId || record.VendorId != vendorId)
            {
                throw new InvalidOperationException(
                    "A spend breakdown batch holds a row for a different tenant or vendor than the one being replaced.");
            }

            if (!covered.Contains(record.Date))
            {
                throw new InvalidOperationException(
                    $"A spend breakdown row is dated {record.Date:yyyy-MM-dd}, which the batch does not declare it covers.");
            }

            if (string.IsNullOrWhiteSpace(record.Category))
            {
                throw new InvalidOperationException(
                    $"A spend breakdown row on {record.Date:yyyy-MM-dd} has no category.");
            }

            if (!incoming.TryAdd((record.Date, record.Category, record.Detail), record))
            {
                throw new InvalidOperationException(
                    $"A spend breakdown batch holds more than one row for {record.Date:yyyy-MM-dd}, category "
                    + $"'{record.Category}' and the same detail; refusing to guess which is right.");
            }
        }

        if (covered.Count == 0)
        {
            return new BreakdownReplaceResult(0, 0, 0, 0);
        }

        var first = covered.Min();
        var last = covered.Max();

        var query = apply ? _context.DailySpendBreakdownRecords : _context.DailySpendBreakdownRecords.AsNoTracking();
        var existing = (await query
                .Where(r => r.TenantId == tenantId && r.VendorId == vendorId && r.Date >= first && r.Date <= last)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false))
            .Where(r => covered.Contains(r.Date))
            .ToDictionary(r => (r.Date, r.Category, r.Detail));

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
                    _context.DailySpendBreakdownRecords.Add(record);
                }
            }
            else if (current.Amount == record.Amount && current.IsUsage == record.IsUsage)
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

        // What is left was stored for a covered day and is not in the batch: the vendor no longer reports it.
        var removed = existing.Count;
        if (apply && removed > 0)
        {
            _context.DailySpendBreakdownRecords.RemoveRange(existing.Values);
        }

        return new BreakdownReplaceResult(inserted, updated, unchanged, removed);
    }
}
