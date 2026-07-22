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

    public async Task UpsertAsync(IEnumerable<DailySpendRecord> records, CancellationToken cancellationToken = default)
    {
        foreach (var record in records)
        {
            var existing = await _context.DailySpendRecords.FirstOrDefaultAsync(
                r => r.TenantId == record.TenantId && r.VendorId == record.VendorId && r.Date == record.Date,
                cancellationToken).ConfigureAwait(false);

            if (existing is null)
            {
                _context.DailySpendRecords.Add(record);
            }
            else
            {
                // Works with init-only properties: EF Core sets values via its own
                // metadata-driven property access, not the C# `init` accessor rules.
                _context.Entry(existing).CurrentValues.SetValues(record);
            }
        }

        await _context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

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
