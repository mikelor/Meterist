using Meterist.Core.Models;
using Meterist.Core.Vendors;

namespace Meterist.Core.Reporting;

/// <summary>
/// One user's usage for a vendor over a period, summed from their per-user daily rows.
/// <see cref="Credits"/> and <see cref="Requests"/> are null when the vendor reports none.
/// </summary>
public sealed record UserUsageSummary(
    string UserId,
    string? Email,
    string? Name,
    UsageAttribution Attribution,
    decimal Amount,
    decimal? Credits,
    long? Requests,
    int DaysActive,
    bool IsEstimated)
{
    /// <summary>The name if the vendor gives one, else the email, else the vendor's id.</summary>
    public string DisplayName =>
        !string.IsNullOrWhiteSpace(Name) ? Name
        : !string.IsNullOrWhiteSpace(Email) ? Email
        : UserId;
}

/// <summary>
/// How a vendor's per-user dollars compare with its aggregate daily usage. The comparison is
/// like for like: only days that have per-user rows are compared, because per-user history can start
/// later than the daily totals (or be backfilled partway). <see cref="UsageDaysWithoutUserRows"/> says
/// how many days the aggregate has usage on that the per-user rows do not cover at all.
/// </summary>
public sealed record UserUsageReconciliation(
    int DaysCompared,
    decimal PerUserAmount,
    decimal AggregateUsage,
    int UsageDaysWithoutUserRows,
    bool Reconciles)
{
    public decimal Difference => PerUserAmount - AggregateUsage;
}

/// <summary>Per-user usage for one vendor, with the aggregate it should add up to.</summary>
public sealed record VendorUserUsage(
    VendorIdentity Vendor,
    IReadOnlyList<UserUsageSummary> Users,
    decimal AggregateSeatFee,
    decimal AggregateUsage,
    UserUsageReconciliation Reconciliation)
{
    public decimal TotalAmount => Users.Sum(u => u.Amount);

    public decimal? TotalCredits => Users.Any(u => u.Credits is not null) ? Users.Sum(u => u.Credits ?? 0m) : null;

    public long? TotalRequests => Users.Any(u => u.Requests is not null) ? Users.Sum(u => u.Requests ?? 0L) : null;
}

public sealed record UserUsageReport(string TenantId, DateRange Period, IReadOnlyList<VendorUserUsage> Vendors);
