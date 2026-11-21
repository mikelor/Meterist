using Meterist.Core.Models;

namespace Meterist.Core.Persistence;

/// <summary>What storing a spend-limit snapshot inserted, changed, left alone, or removed.</summary>
public sealed record SpendLimitSnapshotResult(int Inserted, int Updated, int Unchanged, int Removed)
{
    public int Total => Inserted + Updated + Unchanged + Removed;
}

/// <summary>A stored snapshot: the day it was taken and the rows on it.</summary>
public sealed record SpendLimitSnapshot(DateOnly Date, IReadOnlyList<SpendLimitRecord> Limits);

/// <summary>
/// Storage for dated per-member spend-limit snapshots. A snapshot is stored as a whole: the stored rows for
/// (tenant, vendor, date) become exactly the batch, so a member who has since left does not linger in a snapshot
/// retaken the same day. Snapshots of other days are never touched.
/// <para>
/// The rows contain names and emails; implementations must not log them (see <see cref="SpendLimitRecord"/>).
/// </para>
/// </summary>
public interface ISpendLimitRepository
{
    /// <summary>
    /// Makes the stored snapshot for the batch's tenant, vendor and date exactly <paramref name="limits"/>. Every row
    /// must be for that tenant, vendor and date, and no two may share a member id; a malformed batch is rejected
    /// before anything is staged. An empty batch clears that day's snapshot, so callers must not pass one by accident.
    /// </summary>
    Task<SpendLimitSnapshotResult> ReplaceSnapshotAsync(
        string tenantId,
        Guid vendorId,
        DateOnly snapshotDate,
        IEnumerable<SpendLimitRecord> limits,
        CancellationToken cancellationToken = default);

    /// <summary>The same decisions as <see cref="ReplaceSnapshotAsync"/>, with nothing written. Backs a dry run.</summary>
    Task<SpendLimitSnapshotResult> PlanReplaceSnapshotAsync(
        string tenantId,
        Guid vendorId,
        DateOnly snapshotDate,
        IEnumerable<SpendLimitRecord> limits,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The most recent snapshot taken on or before <paramref name="onOrBefore"/> (or the latest of all when null);
    /// null when none has been taken. Rows are ordered by member id: presentation order is the caller's business.
    /// </summary>
    Task<SpendLimitSnapshot?> GetLatestSnapshotAsync(
        string tenantId,
        Guid vendorId,
        DateOnly? onOrBefore = null,
        CancellationToken cancellationToken = default);
}
