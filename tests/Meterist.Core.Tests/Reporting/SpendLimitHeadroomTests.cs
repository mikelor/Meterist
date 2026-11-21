using Meterist.Core.Models;
using Meterist.Core.Reporting;
using Meterist.Core.Vendors;

namespace Meterist.Core.Tests.Reporting;

public class SpendLimitHeadroomTests
{
    private static SpendLimitRecord Record(decimal? limit, decimal? spent) => new()
    {
        TenantId = "t1",
        VendorId = VendorCatalog.ClaudeEnterprise.Id,
        SnapshotDate = new DateOnly(2026, 11, 21),
        MemberId = "m1",
        LimitAmount = limit,
        PeriodToDateSpend = spent,
    };

    [Fact]
    public void NullLimit_IsNoLimit_WithNeitherUsedNorLeft()
    {
        var h = SpendLimitHeadroom.Of(Record(null, 50m));

        Assert.Equal(SpendLimitKind.NoLimit, h.Kind);
        Assert.Null(h.UsedFraction);
        Assert.Null(h.Left);
    }

    [Fact]
    public void PositiveLimit_HasAShareUsedAndAmountLeft()
    {
        var h = SpendLimitHeadroom.Of(Record(200m, 50m));

        Assert.Equal(SpendLimitKind.Positive, h.Kind);
        Assert.Equal(0.25m, h.UsedFraction);
        Assert.Equal(150m, h.Left);
    }

    [Fact]
    public void PositiveLimit_WhenOver_HasANegativeLeft()
    {
        var h = SpendLimitHeadroom.Of(Record(100m, 130m));

        Assert.Equal(1.3m, h.UsedFraction);
        Assert.Equal(-30m, h.Left);
    }

    [Fact]
    public void ZeroLimit_IsALimit_ShowsExplicitZeroHeadroom_AndNoShareBecauseAShareOfZeroIsUndefined()
    {
        var unspent = SpendLimitHeadroom.Of(Record(0m, 0m));
        var overspent = SpendLimitHeadroom.Of(Record(0m, 12.5m));

        Assert.Equal(SpendLimitKind.Zero, unspent.Kind);
        Assert.Null(unspent.UsedFraction);
        Assert.Equal(0m, unspent.Left);        // explicit zero, not hidden
        Assert.Equal(-12.5m, overspent.Left);  // over by what was spent
    }

    [Fact]
    public void ASpendFigureThatIsMissing_LeavesUsedAndLeftUnknown_NotZero()
    {
        Assert.Null(SpendLimitHeadroom.Of(Record(100m, null)).Left);
        Assert.Null(SpendLimitHeadroom.Of(Record(100m, null)).UsedFraction);
        Assert.Null(SpendLimitHeadroom.Of(Record(0m, null)).Left);
    }

    [Fact]
    public void Ranking_ZeroLimitWithSpend_ThenPositiveByShareUsed_ThenZeroLimitUnspent_ThenNoLimit()
    {
        var ordered = new[]
        {
            ("none", Record(null, 999m)),
            ("low", Record(100m, 10m)),
            ("zeroSpent", Record(0m, 0m)),
            ("high", Record(100m, 90m)),
            ("zeroOver", Record(0m, 5m)),
            ("over", Record(100m, 120m)),
        }
        .OrderByDescending(x => SpendLimitHeadroom.Of(x.Item2).Pressure)
        .Select(x => x.Item1)
        .ToList();

        // zeroOver > over (120%) > zeroSpent (blocked, ranks as 100%) ties with nothing above high (90%) > low > no limit
        Assert.Equal(["zeroOver", "over", "zeroSpent", "high", "low", "none"], ordered);
    }

    [Fact]
    public void ANoLimitMember_NeverOutranksAMemberWithALimit_EvenWithHugeSpend()
    {
        var none = SpendLimitHeadroom.Of(Record(null, 1_000_000m)).Pressure;
        var low = SpendLimitHeadroom.Of(Record(1000m, 0m)).Pressure;

        Assert.True(low > none);
    }
}
