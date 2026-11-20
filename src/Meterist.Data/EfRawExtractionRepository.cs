using System.Text.Json;
using Meterist.Core.Persistence;
using Meterist.Core.Vendors;
using Microsoft.EntityFrameworkCore;

namespace Meterist.Data;

public sealed class EfRawExtractionRepository : IRawExtractionRepository
{
    private readonly MeteristDbContext _context;

    public EfRawExtractionRepository(MeteristDbContext context)
    {
        _context = context;
    }

    public async Task<IReadOnlyDictionary<DateOnly, string>> GetRecordsJsonAsync(
        string tenantId,
        Guid vendorId,
        DateRange period,
        CancellationToken cancellationToken = default)
    {
        var rows = await _context.RawDailyExtractionRecords
            .AsNoTracking()
            .Where(r => r.TenantId == tenantId && r.VendorId == vendorId && r.Date >= period.Start && r.Date <= period.End)
            .Select(r => new { r.Date, r.RecordsJson })
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        return rows.ToDictionary(r => r.Date, r => r.RecordsJson);
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
