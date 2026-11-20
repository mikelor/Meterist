namespace Meterist.Core.Models;

/// <summary>
/// One person on a vendor's member list, as it stood on <see cref="SnapshotDate"/>. A roster is what a tenant
/// <i>has</i> (who holds a seat), as opposed to usage, which is what people <i>did</i>; joining the two is how idle
/// seats are found. Natural key is (TenantId, VendorId, SnapshotDate, MemberId).
/// <para>
/// Snapshots are dated and kept, one per (tenant, vendor, day): the member list changes over time and the vendors
/// expose no history, so the only way to answer "who held a seat in October" is to have taken a snapshot then.
/// Taking another on the same day replaces that day's snapshot.
/// </para>
/// <para>
/// Holds name and email, which are personal data: keep them out of logs at Information level and above and out
/// of anything committed. A class with the default <c>ToString</c>, not a record, so logging an instance by
/// accident prints a type name instead of a person.
/// </para>
/// </summary>
public sealed class MemberRosterRecord
{
    public required string TenantId { get; init; }

    public required Guid VendorId { get; init; }

    /// <summary>The UTC date the snapshot was taken.</summary>
    public required DateOnly SnapshotDate { get; init; }

    /// <summary>The vendor's stable identifier for the member. Part of the natural key, so not an email address.</summary>
    public required string MemberId { get; init; }

    public string? Email { get; init; }

    public string? Name { get; init; }

    /// <summary>The member's role in the vendor's own terms (Claude: user, managed, owner, membership_admin, primary_owner).</summary>
    public string? Role { get; init; }

    /// <summary>The seat or licence type, for vendors that report one. Null where the vendor does not.</summary>
    public string? SeatType { get; init; }

    /// <summary>When the member joined the organisation, as the vendor reports it. Null where the vendor does not.</summary>
    public DateTimeOffset? AddedAt { get; init; }
}
