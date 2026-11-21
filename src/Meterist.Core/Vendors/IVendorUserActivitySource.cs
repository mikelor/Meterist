using Meterist.Core.Models;

namespace Meterist.Core.Vendors;

/// <summary>
/// What a source fetched: the activity rows, and the days it actually covered. A covered day with no rows means nobody was
/// active that day; a day not covered (not published yet, or before the vendor's data begins) was not looked at, so
/// whatever is stored for it must be left alone.
/// </summary>
public sealed record UserActivityFetch(IReadOnlyList<UserActivityRecord> Records, IReadOnlyCollection<DateOnly> CoveredDays);

/// <summary>
/// A vendor's per-person activity by day, for vendors that report it (Claude Enterprise). Optional per vendor, like the
/// other seams. It takes the exact days to fetch rather than a range, because the vendor serves one day per request and the
/// caller knows which days it already holds. Implementations resolve their own credentials and must return every row for
/// every covered day or throw: a partial day stored as complete would read as people not having been active.
/// </summary>
public interface IVendorUserActivitySource
{
    Guid VendorId { get; }

    Task<UserActivityFetch> FetchAsync(
        string tenantId, IReadOnlyCollection<DateOnly> days, CancellationToken cancellationToken = default);
}
