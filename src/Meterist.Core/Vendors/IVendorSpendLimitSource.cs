namespace Meterist.Core.Vendors;

/// <summary>One member's spend limit as a vendor reports it. Personal data: never logged.</summary>
public sealed class VendorSpendLimit
{
    public required string MemberId { get; init; }

    public string? Email { get; init; }

    public string? Name { get; init; }

    /// <summary>The limit in dollars (or the vendor's currency unit); null when the member has none.</summary>
    public decimal? LimitAmount { get; init; }

    public decimal? PeriodToDateSpend { get; init; }

    public string? Currency { get; init; }

    public string? Period { get; init; }

    public string? SourceType { get; init; }

    public string? SourceDetail { get; init; }

    public string? SpendLimitId { get; init; }
}

/// <summary>
/// Fetches one vendor's per-member spend limits for a tenant. Optional per vendor: a vendor with no such API simply
/// has none registered. Implementations resolve their own credentials and must return the <i>complete</i> list or
/// throw: a partial list stored as a snapshot would read as members having no limit.
/// </summary>
public interface IVendorSpendLimitSource
{
    Guid VendorId { get; }

    Task<IReadOnlyList<VendorSpendLimit>> FetchLimitsAsync(string tenantId, CancellationToken cancellationToken = default);
}
