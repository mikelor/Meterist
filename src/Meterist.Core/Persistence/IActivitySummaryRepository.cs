using Meterist.Core.Models;
using Meterist.Core.Vendors;

namespace Meterist.Core.Persistence;

/// <summary>How many rows an upsert inserted, replaced, or found already identical.</summary>
public sealed record ActivitySummaryUpsertResult(int Inserted, int Updated, int Unchanged)
{
    public int Total => Inserted + Updated + Unchanged;
}

/// <summary>
/// Storage for the vendor's daily active-user and seat counts. An upsert against the natural
/// (TenantId, VendorId, Date, Product) key: storing a day already stored and a brand new one are the same path.
/// Rows that are not in the input are left alone, because the vendor publishes the newest day a day late and an
/// absent row is not a statement that the day had no activity.
/// </summary>
public interface IActivitySummaryRepository
{
    Task<ActivitySummaryUpsertResult> UpsertAsync(
        IEnumerable<ActivitySummaryRecord> records, CancellationToken cancellationToken = default);

    /// <summary>The same decisions as <see cref="UpsertAsync"/>, with nothing written. Backs a dry run.</summary>
    Task<ActivitySummaryUpsertResult> PlanUpsertAsync(
        IEnumerable<ActivitySummaryRecord> records, CancellationToken cancellationToken = default);

    /// <summary>All stored rows for the tenant and vendor in the period, ordered by date then product.</summary>
    Task<IReadOnlyList<ActivitySummaryRecord>> GetAsync(
        string tenantId, Guid vendorId, DateRange range, CancellationToken cancellationToken = default);
}
