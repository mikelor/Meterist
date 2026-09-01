namespace Meterist.Core.Reporting;

/// <summary>
/// One Wednesday–Tuesday week's totals for a single (tenant, vendor) pair.
/// <see cref="DaysPresent"/> is how many days in the week have a stored
/// record — NOT a reliable completeness signal on its own, since a vendor
/// like Claude API Platform only writes a row for days with actual usage
/// (a fully-elapsed, genuinely-quiet week can show DaysPresent well below 7).
/// Completeness is a calendar question instead: a week is complete once
/// <see cref="WeekEnd"/> falls on or before the report period's end date —
/// see the caller in ReportDataAggregator. <see cref="ExcludeFromProjection"/>
/// is set by the caller (see <see cref="WeeklyBucketer.Bucket"/>'s
/// excludeWeekIndices parameter) for weeks known to be unreliable for trend
/// purposes — e.g. ChatGPT Enterprise's first week in any report period,
/// permanently lost to the vendor's retention window.
/// </summary>
public readonly record struct WeeklyTotal(
    int WeekIndex,
    DateOnly WeekStart,
    DateOnly WeekEnd,
    decimal SeatFee,
    decimal UsageOrOverage,
    decimal GrossSpend,
    int DaysPresent,
    bool ExcludeFromProjection);
