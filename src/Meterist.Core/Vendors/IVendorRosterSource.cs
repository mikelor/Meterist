namespace Meterist.Core.Vendors;

/// <summary>One member as a vendor's member list reports them. Personal data: never logged.</summary>
public sealed class VendorMember
{
    public required string Id { get; init; }

    public string? Email { get; init; }

    public string? Name { get; init; }

    public string? Role { get; init; }

    public string? SeatType { get; init; }

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
}
