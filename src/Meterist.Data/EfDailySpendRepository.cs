using Meterist.Core.Models;
using Meterist.Core.Persistence;
using Meterist.Core.Vendors;
using Microsoft.EntityFrameworkCore;

namespace Meterist.Data;

public sealed class EfDailySpendRepository : IDailySpendRepository
{
    private readonly MeteristDbContext _context;

    public EfDailySpendRepository(MeteristDbContext context)
    {
        _context = context;
    }

    public async Task<IReadOnlyList<DailySpendChange>> UpsertAsync(
        IEnumerable<DailySpendRecord> records,
        bool requiresMonotonicUsage = false,
        CancellationToken cancellationToken = default)
    {
        var changes = await ResolveAsync(records, requiresMonotonicUsage, apply: true, cancellationToken)
            .ConfigureAwait(false);

        await _context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return changes;
    }

    public Task<IReadOnlyList<DailySpendChange>> PlanUpsertAsync(
        IEnumerable<DailySpendRecord> records,
        bool requiresMonotonicUsage = false,
        CancellationToken cancellationToken = default) =>
        ResolveAsync(records, requiresMonotonicUsage, apply: false, cancellationToken);

    // One routine decides what happens to each day and, when apply is true,
    // stages it. The real upsert and the dry-run plan both go through here so
    // they cannot drift apart: a dry run that predicted something the real
    // run then didn't do would be worse than no dry run.
    private async Task<IReadOnlyList<DailySpendChange>> ResolveAsync(
        IEnumerable<DailySpendRecord> records,
        bool requiresMonotonicUsage,
        bool apply,
        CancellationToken cancellationToken)
    {
        var changes = new List<DailySpendChange>();
        var source = apply ? _context.DailySpendRecords : _context.DailySpendRecords.AsNoTracking();

        foreach (var record in records)
        {
            var existing = await source.FirstOrDefaultAsync(
                r => r.TenantId == record.TenantId && r.VendorId == record.VendorId && r.Date == record.Date,
                cancellationToken).ConfigureAwait(false);

            var effective = record;
            var kind = DailySpendChangeKind.Updated;

            if (requiresMonotonicUsage && existing is not null && existing.UsageOrOverage > record.UsageOrOverage)
            {
                // Pin the higher, earlier-observed usage figure; still let SeatFee refresh from
                // the new pull (proration can legitimately change it). The figure kept is the
                // earlier source's, so Source stays that source's too.
                var usage = existing.UsageOrOverage;
                var gross = record.SeatFee + usage;
                effective = new DailySpendRecord
                {
                    TenantId = record.TenantId,
                    VendorId = record.VendorId,
                    Date = record.Date,
                    SeatFee = record.SeatFee,
                    UsageOrOverage = usage,
                    GrossSpend = gross,
                    CreditsApplied = record.CreditsApplied,
                    NetSpend = gross - record.CreditsApplied,
                    Source = existing.Source,
                };
                kind = DailySpendChangeKind.KeptHigherUsage;
            }
            else if (existing is null)
            {
                kind = DailySpendChangeKind.Inserted;
            }

            if (existing is not null && kind == DailySpendChangeKind.Updated && SameFigures(existing, effective))
            {
                kind = DailySpendChangeKind.Unchanged;
            }

            // A copy, not the entity itself: when applying, `existing` is the
            // tracked entity and is overwritten in place just below, which would
            // make "previous" read as the new figures.
            changes.Add(new DailySpendChange(record.Date, kind, existing is null ? null : Snapshot(existing), effective, record));

            if (!apply)
            {
                continue;
            }

            if (existing is null)
            {
                _context.DailySpendRecords.Add(effective);
            }
            else
            {
                // Works with init-only properties: EF Core sets values via its own
                // metadata-driven property access, not the C# `init` accessor rules.
                _context.Entry(existing).CurrentValues.SetValues(effective);
            }
        }

        return changes;
    }

    private static DailySpendRecord Snapshot(DailySpendRecord r) => new()
    {
        TenantId = r.TenantId,
        VendorId = r.VendorId,
        Date = r.Date,
        SeatFee = r.SeatFee,
        UsageOrOverage = r.UsageOrOverage,
        GrossSpend = r.GrossSpend,
        CreditsApplied = r.CreditsApplied,
        NetSpend = r.NetSpend,
        Source = r.Source,
    };

    // Rounded to the column's scale so a figure computed to more places than
    // the column keeps doesn't read as a change on every run.
    private static bool SameFigures(DailySpendRecord a, DailySpendRecord b) =>
        Math.Round(a.SeatFee, 4) == Math.Round(b.SeatFee, 4)
        && Math.Round(a.UsageOrOverage, 4) == Math.Round(b.UsageOrOverage, 4)
        && Math.Round(a.GrossSpend, 4) == Math.Round(b.GrossSpend, 4)
        && Math.Round(a.CreditsApplied, 4) == Math.Round(b.CreditsApplied, 4)
        && Math.Round(a.NetSpend, 4) == Math.Round(b.NetSpend, 4)
        && string.Equals(a.Source, b.Source, StringComparison.Ordinal);

    public async Task<IReadOnlyList<DailySpendRecord>> GetAsync(
        string tenantId,
        Guid vendorId,
        DateRange range,
        CancellationToken cancellationToken = default)
    {
        return await _context.DailySpendRecords
            .Where(r => r.TenantId == tenantId && r.VendorId == vendorId && r.Date >= range.Start && r.Date <= range.End)
            .OrderBy(r => r.Date)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
    }
}
