using Meterist.Core.Models;
using Meterist.Core.Vendors;

namespace Meterist.Core.Persistence;

/// <summary>What replacing a set of days' activity rows inserted, changed, left alone, or removed.</summary>
public sealed record UserActivityReplaceResult(int Inserted, int Updated, int Unchanged, int Removed)
{
    public int Total => Inserted + Updated + Unchanged + Removed;
}

/// <summary>
/// Storage for per-person daily activity. Writing is a replace of the days the caller fetched: a vendor can revise a recent
/// day (a person's count goes up, or a row appears or disappears), so every stored row on a fetched day that is not in the
/// batch is removed, and days that were not fetched are never touched. The rows hold emails; implementations must not log them.
/// </summary>
public interface IUserActivityRepository
{
    /// <summary>
    /// Makes the stored rows for <paramref name="fetchedDays"/> exactly <paramref name="records"/>. A batch with two rows for one
    /// key, or a row for a tenant, vendor or day it does not declare, is rejected before anything is staged.
    /// </summary>
    Task<UserActivityReplaceResult> ReplaceDaysAsync(
        string tenantId,
        Guid vendorId,
        IReadOnlyCollection<DateOnly> fetchedDays,
        IEnumerable<UserActivityRecord> records,
        CancellationToken cancellationToken = default);

    /// <summary>The same decisions as <see cref="ReplaceDaysAsync"/>, with nothing written. Backs a dry run.</summary>
    Task<UserActivityReplaceResult> PlanReplaceDaysAsync(
        string tenantId,
        Guid vendorId,
        IReadOnlyCollection<DateOnly> fetchedDays,
        IEnumerable<UserActivityRecord> records,
        CancellationToken cancellationToken = default);

    /// <summary>All stored rows for the tenant and vendor in the period, ordered by date then user id.</summary>
    Task<IReadOnlyList<UserActivityRecord>> GetAsync(
        string tenantId, Guid vendorId, DateRange range, CancellationToken cancellationToken = default);
}
