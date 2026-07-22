using System.Text.Json;
using Meterist.Core.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Meterist.Data;

public sealed class EfRawExtractionRepository : IRawExtractionRepository
{
    private readonly MeteristDbContext _context;

    public EfRawExtractionRepository(MeteristDbContext context)
    {
        _context = context;
    }

    public async Task UpsertAsync(
        string tenantId,
        Guid vendorId,
        IReadOnlyDictionary<DateOnly, IReadOnlyList<IReadOnlyDictionary<string, object?>>> recordsByDate,
        DateTime extractedAtUtc,
        CancellationToken cancellationToken = default)
    {
        foreach (var (date, rows) in recordsByDate)
        {
            var updated = new RawDailyExtractionRecord
            {
                TenantId = tenantId,
                VendorId = vendorId,
                Date = date,
                ExtractedAtUtc = extractedAtUtc,
                RecordsJson = JsonSerializer.Serialize(rows),
            };

            var existing = await _context.RawDailyExtractionRecords.FirstOrDefaultAsync(
                r => r.TenantId == tenantId && r.VendorId == vendorId && r.Date == date,
                cancellationToken).ConfigureAwait(false);

            if (existing is null)
            {
                _context.RawDailyExtractionRecords.Add(updated);
            }
            else
            {
                _context.Entry(existing).CurrentValues.SetValues(updated);
            }
        }

        await _context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}
