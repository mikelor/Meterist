using Meterist.Core.Models;

namespace Meterist.Core.Vendors;

/// <summary>
/// A vendor's own daily active-user and seat counts, for vendors that report them. Optional per vendor, like the
/// per-user and breakdown seams: a vendor with none registered simply has no activity summary, and extraction
/// treats that as normal. Separate from the spend normalizers because these figures come from their own API call
/// rather than from the cost rows.
/// </summary>
public interface IVendorActivitySummarySource
{
    Guid VendorId { get; }

    /// <summary>
    /// The summaries the vendor has for the days in <paramref name="period"/>. A day the vendor has no summary for
    /// yet (the newest day is published the next day) is simply absent. Returns everything or throws: a partial
    /// list would read as days with fewer users.
    /// </summary>
    Task<IReadOnlyList<ActivitySummaryRecord>> FetchAsync(
        string tenantId, DateRange period, CancellationToken cancellationToken = default);
}
