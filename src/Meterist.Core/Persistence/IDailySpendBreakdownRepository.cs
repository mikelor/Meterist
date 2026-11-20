using Meterist.Core.Models;
using Meterist.Core.Vendors;

namespace Meterist.Core.Persistence;

/// <summary>What replacing a set of days' breakdown rows inserted, changed, left alone, or removed.</summary>
public sealed record BreakdownReplaceResult(int Inserted, int Updated, int Unchanged, int Removed)
{
    public int Total => Inserted + Updated + Unchanged + Removed;
}

/// <summary>
/// Storage for the per-kind breakdown of daily spend. Writing is a replace, not an upsert: a vendor's
/// billing for a day can lose or revise a line, and a stale row left behind would make the breakdown
/// disagree with the day's total. So the caller states which days the batch covers, and every stored row
/// on those days that is not in the batch is removed.
/// </summary>
public interface IDailySpendBreakdownRepository
{
    /// <summary>
    /// Makes the stored rows for <paramref name="coveredDays"/> exactly <paramref name="records"/>: new keys are
    /// inserted, changed amounts updated, identical rows left, and rows on a covered day that are absent from the
    /// batch removed. Days outside <paramref name="coveredDays"/> are never touched. A batch that holds two rows
    /// for one key, or a row for a tenant, vendor or day it does not cover, is rejected before anything is staged.
    /// </summary>
    Task<BreakdownReplaceResult> ReplaceAsync(
        string tenantId,
        Guid vendorId,
        IReadOnlyCollection<DateOnly> coveredDays,
        IEnumerable<DailySpendBreakdownRecord> records,
        CancellationToken cancellationToken = default);

    /// <summary>The same decisions as <see cref="ReplaceAsync"/>, with nothing written. Backs a dry run.</summary>
    Task<BreakdownReplaceResult> PlanReplaceAsync(
        string tenantId,
        Guid vendorId,
        IReadOnlyCollection<DateOnly> coveredDays,
        IEnumerable<DailySpendBreakdownRecord> records,
        CancellationToken cancellationToken = default);

    /// <summary>All stored rows for the tenant and vendor in the period, ordered by date, category, then detail.</summary>
    Task<IReadOnlyList<DailySpendBreakdownRecord>> GetAsync(
        string tenantId,
        Guid vendorId,
        DateRange range,
        CancellationToken cancellationToken = default);
}
