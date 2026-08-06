using Meterist.Core.Models;
using Meterist.Core.Persistence;
using Meterist.Core.Vendors;
using Microsoft.EntityFrameworkCore;

namespace Meterist.Data;

public sealed class EfVendorRateConfigRepository : IVendorRateConfigRepository
{
    private readonly MeteristDbContext _context;

    public EfVendorRateConfigRepository(MeteristDbContext context)
    {
        _context = context;
    }

    public async Task AddAsync(VendorRateConfig rate, CancellationToken cancellationToken = default)
    {
        _context.VendorRateConfigs.Add(rate);
        await _context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<int> CloseOpenEndedRateAsync(
        string? tenantId,
        Guid vendorId,
        string? modelOrSku,
        DateOnly newEffectiveFrom,
        CancellationToken cancellationToken = default)
    {
        var openEnded = await _context.VendorRateConfigs
            .Where(r => r.TenantId == tenantId
                && r.VendorId == vendorId
                && r.ModelOrSku == modelOrSku
                && r.EffectiveTo == null
                && r.EffectiveFrom < newEffectiveFrom)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        foreach (var rate in openEnded)
        {
            // EffectiveTo is init-only in the C# model, but EF Core's
            // metadata-based property access can still write it after the
            // fact — same pattern EfDailySpendRepository's upsert relies on.
            _context.Entry(rate).Property(r => r.EffectiveTo).CurrentValue = newEffectiveFrom.AddDays(-1);
        }

        if (openEnded.Count > 0)
        {
            await _context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        return openEnded.Count;
    }

    public async Task<IReadOnlyList<VendorRateConfig>> GetApplicableRatesAsync(
        string tenantId,
        Guid vendorId,
        DateRange period,
        CancellationToken cancellationToken = default)
    {
        var candidates = await _context.VendorRateConfigs
            .Where(r => r.VendorId == vendorId
                && (r.TenantId == tenantId || r.TenantId == null)
                && r.EffectiveFrom <= period.End
                && (r.EffectiveTo == null || r.EffectiveTo >= period.Start))
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        // Tenant-specific overrides fully replace the public default for the
        // same ModelOrSku bucket — see IVendorRateConfigRepository's doc comment.
        var tenantModelOrSkus = candidates
            .Where(r => r.TenantId == tenantId)
            .Select(r => r.ModelOrSku)
            .ToHashSet();

        return candidates
            .Where(r => r.TenantId == tenantId || !tenantModelOrSkus.Contains(r.ModelOrSku))
            .ToList();
    }

    public async Task<DateOnly?> FindNextEffectiveFromAsync(
        string? tenantId,
        Guid vendorId,
        string? modelOrSku,
        DateOnly afterEffectiveFrom,
        CancellationToken cancellationToken = default)
    {
        var next = await _context.VendorRateConfigs
            .Where(r => r.TenantId == tenantId
                && r.VendorId == vendorId
                && r.ModelOrSku == modelOrSku
                && r.EffectiveFrom > afterEffectiveFrom)
            .OrderBy(r => r.EffectiveFrom)
            .Select(r => (DateOnly?)r.EffectiveFrom)
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);

        return next;
    }

    public async Task<IReadOnlyList<VendorRateConfig>> FindOverlappingRatesAsync(
        string? tenantId,
        Guid vendorId,
        string? modelOrSku,
        DateOnly effectiveFrom,
        DateOnly? effectiveTo,
        CancellationToken cancellationToken = default)
    {
        var effectiveToOrMax = effectiveTo ?? DateOnly.MaxValue;

        return await _context.VendorRateConfigs
            .Where(r => r.TenantId == tenantId
                && r.VendorId == vendorId
                && r.ModelOrSku == modelOrSku
                && r.EffectiveFrom <= effectiveToOrMax
                && (r.EffectiveTo == null || r.EffectiveTo >= effectiveFrom))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
    }
}
