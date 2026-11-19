using Meterist.Core.Models;
using Meterist.Core.Vendors;

namespace Meterist.Core.Persistence;

public enum DailySpendChangeKind
{
    /// <summary>No record existed for this day.</summary>
    Inserted,

    /// <summary>A record existed and the incoming figures replaced it.</summary>
    Updated,

    /// <summary>A record existed and already held exactly the incoming figures.</summary>
    Unchanged,

    /// <summary>
    /// A record existed with higher usage than the incoming figure and the
    /// monotonic rule kept it (the seat fee may still have been refreshed).
    /// </summary>
    KeptHigherUsage,
}

/// <summary>
/// What an upsert did, or would do, to one day. <see cref="Stored"/> is the
/// record as it is (or would be) after the upsert; <see cref="Incoming"/> is
/// what the extraction produced, which differs from it only for
/// <see cref="DailySpendChangeKind.KeptHigherUsage"/>. <see cref="Previous"/>
/// is null when the day is new.
/// </summary>
public sealed record DailySpendChange(
    DateOnly Date,
    DailySpendChangeKind Kind,
    DailySpendRecord? Previous,
    DailySpendRecord Stored,
    DailySpendRecord Incoming);

/// <summary>
/// Canonical daily spend storage. Upserting a day already stored (the
/// mechanism behind handling overlapping extraction timespans) and a brand
/// new day are the same code path — both are just an upsert against the
/// natural (TenantId, VendorId, Date) key.
/// </summary>
public interface IDailySpendRepository
{
    // requiresMonotonicUsage: when true, an existing record's UsageOrOverage is
    // never lowered by this upsert (see IVendorSpendExtractor.RequiresMonotonicUsage) --
    // GrossSpend/NetSpend are recomputed consistently off whichever UsageOrOverage wins.
    // Returns what was done to each day, so callers can log it instead of the
    // rule operating silently.
    Task<IReadOnlyList<DailySpendChange>> UpsertAsync(
        IEnumerable<DailySpendRecord> records,
        bool requiresMonotonicUsage = false,
        CancellationToken cancellationToken = default);

    // The same decisions as UpsertAsync, computed against what is stored, with
    // nothing written. Backs a dry run.
    Task<IReadOnlyList<DailySpendChange>> PlanUpsertAsync(
        IEnumerable<DailySpendRecord> records,
        bool requiresMonotonicUsage = false,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<DailySpendRecord>> GetAsync(
        string tenantId,
        Guid vendorId,
        DateRange range,
        CancellationToken cancellationToken = default);
}
