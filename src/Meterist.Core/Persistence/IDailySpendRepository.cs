using Meterist.Core.Models;
using Meterist.Core.Vendors;

namespace Meterist.Core.Persistence;

/// <summary>
/// Canonical daily spend storage. Upserting a day already stored (the
/// mechanism behind handling overlapping extraction timespans) and a brand
/// new day are the same code path — both are just an upsert against the
/// natural (TenantId, VendorId, Date) key.
/// </summary>
public interface IDailySpendRepository
{
    // requiresMonotonicUsage: when true, an existing record's UsageOrOverage is
    // never lowered by this upsert (see IVendorSpendExtractor.RequiresMonotonicUsage) --
    // GrossSpend/NetSpend are recomputed consistently off whichever UsageOrOverage wins.
    Task UpsertAsync(
        IEnumerable<DailySpendRecord> records,
        bool requiresMonotonicUsage = false,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<DailySpendRecord>> GetAsync(
        string tenantId,
        Guid vendorId,
        DateRange range,
        CancellationToken cancellationToken = default);
}
