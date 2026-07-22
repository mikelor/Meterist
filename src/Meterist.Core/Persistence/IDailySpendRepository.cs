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
    Task UpsertAsync(IEnumerable<DailySpendRecord> records, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<DailySpendRecord>> GetAsync(
        string tenantId,
        Guid vendorId,
        DateRange range,
        CancellationToken cancellationToken = default);
}
