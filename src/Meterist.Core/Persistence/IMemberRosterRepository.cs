using Meterist.Core.Models;

namespace Meterist.Core.Persistence;

/// <summary>What storing a roster snapshot inserted, changed, left alone, or removed.</summary>
public sealed record RosterSnapshotResult(int Inserted, int Updated, int Unchanged, int Removed)
{
    public int Total => Inserted + Updated + Unchanged + Removed;
}

/// <summary>A stored snapshot: the day it was taken and the members on it.</summary>
public sealed record RosterSnapshot(DateOnly Date, IReadOnlyList<MemberRosterRecord> Members);

/// <summary>
/// Storage for dated member-roster snapshots. A snapshot is stored as a whole: the stored rows for
/// (tenant, vendor, date) become exactly the batch, so a member who has since left does not linger in a
/// snapshot retaken the same day. Snapshots of other days are never touched.
/// <para>
/// The rows contain names and emails; implementations must not log them (see <see cref="MemberRosterRecord"/>).
/// </para>
/// </summary>
public interface IMemberRosterRepository
{
    /// <summary>
    /// Makes the stored snapshot for the batch's tenant, vendor and date exactly <paramref name="members"/>. Every
    /// row must be for that tenant, vendor and date, and no two may share a member id; a malformed batch is
    /// rejected before anything is staged. An empty batch clears that day's snapshot, so callers must not pass one
    /// by accident.
    /// </summary>
    Task<RosterSnapshotResult> ReplaceSnapshotAsync(
        string tenantId,
        Guid vendorId,
        DateOnly snapshotDate,
        IEnumerable<MemberRosterRecord> members,
        CancellationToken cancellationToken = default);

    /// <summary>The same decisions as <see cref="ReplaceSnapshotAsync"/>, with nothing written. Backs a dry run.</summary>
    Task<RosterSnapshotResult> PlanReplaceSnapshotAsync(
        string tenantId,
        Guid vendorId,
        DateOnly snapshotDate,
        IEnumerable<MemberRosterRecord> members,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The most recent snapshot taken on or before <paramref name="onOrBefore"/> (or the latest of all when null),
    /// members ordered by name then email then id; null when none has been taken.
    /// </summary>
    Task<RosterSnapshot?> GetLatestSnapshotAsync(
        string tenantId,
        Guid vendorId,
        DateOnly? onOrBefore = null,
        CancellationToken cancellationToken = default);

    /// <summary>The dates a tenant has a snapshot for this vendor, oldest first.</summary>
    Task<IReadOnlyList<DateOnly>> GetSnapshotDatesAsync(
        string tenantId, Guid vendorId, CancellationToken cancellationToken = default);
}
