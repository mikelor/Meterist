namespace Meterist.Core.Models;

/// <summary>
/// One member's spend limit and how much of it they had used, as the vendor reported it on
/// <see cref="SnapshotDate"/>. Natural key is (TenantId, VendorId, SnapshotDate, MemberId).
/// <para>
/// The figures are a <b>headroom signal, not a ledger</b>: the vendor calls the spend to date informational, and it is
/// the vendor's running figure at the moment of the snapshot, which can differ from the cost reports (live-tested:
/// equal to the cost report for one tenant, higher by several percent for another). Snapshots are dated and kept,
/// because the vendor exposes no history: one per day shows how fast a member is using their limit.
/// </para>
/// <para>
/// Holds name and email, which are personal data: keep them out of logs at Information level and above and out of
/// anything committed. A class with the default <c>ToString</c>, not a record, so logging an instance by accident
/// prints a type name instead of a person.
/// </para>
/// </summary>
public sealed class SpendLimitRecord
{
    public required string TenantId { get; init; }

    public required Guid VendorId { get; init; }

    /// <summary>The UTC date the snapshot was taken.</summary>
    public required DateOnly SnapshotDate { get; init; }

    /// <summary>The vendor's stable identifier for the member. Part of the natural key, so not an email address.</summary>
    public required string MemberId { get; init; }

    public string? Email { get; init; }

    public string? Name { get; init; }

    /// <summary>The limit for the period, in <see cref="Currency"/> units. Null when the member has no limit.</summary>
    public decimal? LimitAmount { get; init; }

    /// <summary>What the member had spent against the limit in the current period. Informational, see the class remarks.</summary>
    public decimal? PeriodToDateSpend { get; init; }

    /// <summary>The currency, as the vendor states it (USD for Claude). Null when it does not say.</summary>
    public string? Currency { get; init; }

    /// <summary>The period the limit applies to (Claude: monthly).</summary>
    public string? Period { get; init; }

    /// <summary>
    /// Where the limit comes from in the vendor's hierarchy: <c>user</c> (set for this member), <c>rbac_group</c>,
    /// <c>seat_tier</c> or <c>organization</c> for Claude. Null when the vendor does not say.
    /// </summary>
    public string? SourceType { get; init; }

    /// <summary>The group id or seat tier name that the limit is inherited from. Null for a per-member limit.</summary>
    public string? SourceDetail { get; init; }

    /// <summary>The vendor's id for the limit, for tracing a figure back to its setting.</summary>
    public string? SpendLimitId { get; init; }
}
