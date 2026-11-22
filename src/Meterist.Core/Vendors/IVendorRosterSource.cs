namespace Meterist.Core.Vendors;

/// <summary>One member as a vendor's member list reports them. Personal data: never logged.</summary>
public sealed class VendorMember
{
    public required string Id { get; init; }

    public string? Email { get; init; }

    public string? Name { get; init; }

    public string? Role { get; init; }

    public string? SeatType { get; init; }

    /// <summary>
    /// True when the member is provisioned and managed by the organisation's identity provider (SCIM), which the
    /// vendor then will not let an API change. Null where the vendor does not say.
    /// </summary>
    public bool? IsScimManaged { get; init; }

    public DateTimeOffset? AddedAt { get; init; }
}

/// <summary>
/// Fetches one vendor's current member list for a tenant. Optional per vendor: a vendor with no roster API simply
/// has none registered. Implementations resolve their own credentials and must return the <i>complete</i> list or
/// throw: a partial list stored as a snapshot would read as people having left.
/// </summary>
public interface IVendorRosterSource
{
    Guid VendorId { get; }

    Task<IReadOnlyList<VendorMember>> FetchMembersAsync(string tenantId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Why a snapshot cannot be attempted for this tenant even though a credential is stored, or null when it can. A vendor
    /// whose roster needs settings beyond the credential (Gemini's Discovery Engine project) says so here, so an unattended
    /// run reports the tenant as not set up rather than as a failure every day. Default: always attemptable.
    /// </summary>
    Task<string?> GetSkipReasonAsync(string tenantId, CancellationToken cancellationToken = default) =>
        Task.FromResult<string?>(null);
}
