using Meterist.Core.Models;

namespace Meterist.Core.Reporting;

/// <summary>What kind of limit a member has. A zero limit is a limit, not the absence of one.</summary>
public enum SpendLimitKind
{
    /// <summary>No limit is set (the vendor reports none): nothing to be near.</summary>
    NoLimit,

    /// <summary>A limit of zero: the member may spend nothing, so any spend is over it.</summary>
    Zero,

    /// <summary>A limit above zero.</summary>
    Positive,
}

/// <summary>
/// How much of a member's spend limit is used, worked out once so the display and its ordering agree. <b>Used</b> is a
/// share of the limit, so it exists only for a positive limit (a share of zero is undefined). <b>Left</b> is the limit
/// minus the spend and exists for any limit including zero (where it is the spend, negated), and goes negative when the
/// member is over. A member with no limit has neither.
/// </summary>
public sealed record SpendLimitHeadroom(SpendLimitKind Kind, decimal? UsedFraction, decimal? Left, decimal Pressure)
{
    /// <summary>
    /// Orders members by how constrained they are, most first: bigger means closer to or beyond the limit. A positive limit
    /// ranks by its share used; a zero limit ranks as fully used (1) with no spend and above every share once anything has
    /// been spent; no limit ranks below everything.
    /// </summary>
    public static SpendLimitHeadroom Of(SpendLimitRecord record)
    {
        var spent = record.PeriodToDateSpend;

        switch (record.LimitAmount)
        {
            case null:
                return new SpendLimitHeadroom(SpendLimitKind.NoLimit, null, null, decimal.MinValue);

            case 0m:
                return new SpendLimitHeadroom(
                    SpendLimitKind.Zero, null, spent is { } z ? 0m - z : null, spent is > 0m ? decimal.MaxValue : 1m);

            case { } limit:
                var used = spent is { } s ? s / limit : (decimal?)null;
                return new SpendLimitHeadroom(
                    SpendLimitKind.Positive, used, spent is { } s2 ? limit - s2 : null, used ?? 0m);
        }
    }
}
