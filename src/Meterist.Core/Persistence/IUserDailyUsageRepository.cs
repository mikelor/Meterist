using Meterist.Core.Models;
using Meterist.Core.Vendors;

namespace Meterist.Core.Persistence;

/// <summary>How many rows an upsert inserted, replaced, or found already identical.</summary>
public sealed record UserUsageUpsertResult(int Inserted, int Updated, int Unchanged)
{
    public int Total => Inserted + Updated + Unchanged;
}

/// <summary>
/// Per-user daily usage storage. Storing a user-day already stored and a brand new one are the same
/// code path: an upsert against the natural (TenantId, VendorId, Date, UserId) key.
/// <para>
/// The rows contain names and emails; implementations must not log them (see
/// <see cref="UserDailyUsageRecord"/>).
/// </para>
/// </summary>
public interface IUserDailyUsageRepository
{
    /// <summary>
    /// Inserts new user-days and replaces every field of existing ones. Rows that are not in the
    /// input are left alone: a re-pull that no longer lists a user does not delete the stored day,
    /// because the vendors only return users with usage and an absent row may be a window edge.
    /// </summary>
    Task<UserUsageUpsertResult> UpsertAsync(
        IEnumerable<UserDailyUsageRecord> records,
        CancellationToken cancellationToken = default);

    /// <summary>The same decisions as <see cref="UpsertAsync"/>, with nothing written. Backs a dry run.</summary>
    Task<UserUsageUpsertResult> PlanUpsertAsync(
        IEnumerable<UserDailyUsageRecord> records,
        CancellationToken cancellationToken = default);

    /// <summary>All stored user-days for the tenant and vendor in the period, ordered by date then user.</summary>
    Task<IReadOnlyList<UserDailyUsageRecord>> GetAsync(
        string tenantId,
        Guid vendorId,
        DateRange range,
        CancellationToken cancellationToken = default);
}
