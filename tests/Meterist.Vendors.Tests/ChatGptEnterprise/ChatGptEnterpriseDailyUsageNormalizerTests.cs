using Meterist.Core.Models;
using Meterist.Core.Vendors;
using Meterist.Vendors.ChatGptEnterprise;

namespace Meterist.Vendors.Tests.ChatGptEnterprise;

/// <summary>
/// The Daily Usage API half of the ChatGPT normalizer. The COSTS-only half is
/// covered by <see cref="ChatGptEnterpriseSpendNormalizerTests"/>, which still passes unchanged.
/// </summary>
public class ChatGptEnterpriseDailyUsageNormalizerTests
{
    // October has 31 days, so a $30/seat/month rate for 50 seats prorates to 50 * 30 / 31 a day.
    private static readonly DateOnly Day = new(2026, 10, 15);
    private static readonly decimal ExpectedSeatFee = 50m * 30m / 31m;

    // A moment safely after every test day, so "today" never interferes unless a test says so.
    private static readonly TimeProvider LongAfter = new FixedTimeProvider(new DateTimeOffset(2026, 12, 1, 0, 0, 0, TimeSpan.Zero));

    [Fact]
    public void Normalize_UsesCreditsTimesTheConfiguredRatePlusNativeUsd_AndLabelsTheSource()
    {
        var logger = new CapturingLogger<ChatGptEnterpriseSpendNormalizer>();

        var record = Single(Normalize(
            RawData(Day, [UsageRow(credits: 1000m, costUsd: 5m, estimated: 40m)]),
            [SeatRate(), CreditRate(0.04m)], logger));

        Assert.Equal(ExpectedSeatFee, record.SeatFee);
        Assert.Equal(1000m * 0.04m + 5m, record.UsageOrOverage);
        Assert.Equal(record.SeatFee + record.UsageOrOverage, record.GrossSpend);
        Assert.Equal(DailySpendSources.DailyUsageApi, record.Source);
        Assert.Empty(logger.Warnings);
    }

    [Fact]
    public void Normalize_NativeUsdUsage_IsDollarsAndIsNeverRunThroughTheCreditRate()
    {
        // A USD-only day: credits null. 12.50 must stay 12.50, not become 12.50 * 0.07.
        var record = Single(Normalize(
            RawData(Day, [UsageRow(credits: null, costUsd: 12.5m, estimated: null)]),
            [SeatRate(), CreditRate(0.07m)]));

        Assert.Equal(12.5m, record.UsageOrOverage);
    }

    [Fact]
    public void Normalize_ConfiguredRateIsTheFigureOfRecord_AndADisagreeingEstimateIsFlaggedNotUsed()
    {
        var logger = new CapturingLogger<ChatGptEnterpriseSpendNormalizer>();

        // The vendor estimates at today's overage rate (0.04); this day was priced under 0.07.
        var record = Single(Normalize(
            RawData(Day, [UsageRow(credits: 1000m, costUsd: null, estimated: 40m)]),
            [SeatRate(), CreditRate(0.07m)], logger));

        Assert.Equal(70m, record.UsageOrOverage);
        var warning = Assert.Single(logger.Warnings);
        Assert.Contains("rate may be stale", warning);
        Assert.Contains("70.00", warning);
        Assert.Contains("40.00", warning);
    }

    [Fact]
    public void Normalize_EstimateWithinToleranceOfTheRate_RaisesNoWarning()
    {
        var logger = new CapturingLogger<ChatGptEnterpriseSpendNormalizer>();

        // 4193.087416 credits at 0.04 is 167.7235; the vendor reports 167.72.
        Normalize(
            RawData(Day, [UsageRow(credits: 4193.087416m, costUsd: null, estimated: 167.72m)]),
            [SeatRate(), CreditRate(0.04m)], logger);

        Assert.Empty(logger.Warnings);
    }

    [Fact]
    public void Normalize_NoCreditRate_FallsBackToTheVendorEstimateWithAWarning()
    {
        var logger = new CapturingLogger<ChatGptEnterpriseSpendNormalizer>();

        var record = Single(Normalize(
            RawData(Day, [UsageRow(credits: 1000m, costUsd: null, estimated: 40m)]),
            [SeatRate()], logger));

        Assert.Equal(40m, record.UsageOrOverage);
        var warning = Assert.Single(logger.Warnings);
        Assert.Contains("credit-usd", warning);
        Assert.Contains("estimated USD", warning);
    }

    [Fact]
    public void Normalize_NoCreditRateAndNoEstimate_ExcludesTheCreditsWithAWarning()
    {
        var logger = new CapturingLogger<ChatGptEnterpriseSpendNormalizer>();

        var record = Single(Normalize(
            RawData(Day, [UsageRow(credits: 1000m, costUsd: null, estimated: null)]),
            [SeatRate()], logger));

        Assert.Equal(0m, record.UsageOrOverage);
        Assert.Contains("cannot be converted to dollars", Assert.Single(logger.Warnings));
    }

    [Fact]
    public void Normalize_DayWithNoRows_StillGetsTheSeatFeeAndTheSourceOfRecord()
    {
        // A no-usage day comes back from the API as no row at all.
        var record = Single(Normalize(RawData(Day, []), [SeatRate(), CreditRate(0.04m)]));

        Assert.Equal(ExpectedSeatFee, record.SeatFee);
        Assert.Equal(0m, record.UsageOrOverage);
        Assert.Equal(DailySpendSources.DailyUsageApi, record.Source);
    }

    [Fact]
    public void Normalize_DailyUsageWins_AndCostsDisagreementIsLoggedNotApplied()
    {
        var logger = new CapturingLogger<ChatGptEnterpriseSpendNormalizer>();

        // COSTS has eroded to 600 credits where Daily Usage reports 1000.
        var record = Single(Normalize(
            RawData(Day, [UsageRow(1000m, null, 40m), CostsRow(600m)]),
            [SeatRate(), CreditRate(0.04m)], logger));

        Assert.Equal(1000m * 0.04m, record.UsageOrOverage);
        Assert.Equal(DailySpendSources.DailyUsageApi, record.Source);

        var warning = Assert.Single(logger.Warnings);
        Assert.Contains("disagree on credits", warning);
        Assert.Contains("1000.0", warning);
        Assert.Contains("600.0", warning);
    }

    [Fact]
    public void Normalize_CostsAndDailyUsageAgreeWithinTolerance_RaiseNoWarning()
    {
        var logger = new CapturingLogger<ChatGptEnterpriseSpendNormalizer>();

        // 1000 vs 1005: inside max(5 credits, 2%) = 20.
        Normalize(
            RawData(Day, [UsageRow(1000m, null, 40m), CostsRow(1005m)]),
            [SeatRate(), CreditRate(0.04m)], logger);

        Assert.Empty(logger.Warnings);
    }

    [Fact]
    public void Normalize_TheDayStillInProgress_IsExcludedFromTheCostsCrossCheck()
    {
        var logger = new CapturingLogger<ChatGptEnterpriseSpendNormalizer>();
        var duringTheDay = new FixedTimeProvider(new DateTimeOffset(Day.ToDateTime(new TimeOnly(15, 0)), TimeSpan.Zero));

        // A huge disagreement, but today is incomplete in both sources.
        Normalize(
            RawData(Day, [UsageRow(1000m, null, 40m), CostsRow(10m)]),
            [SeatRate(), CreditRate(0.04m)], logger, duringTheDay);

        Assert.Empty(logger.Warnings);
    }

    [Fact]
    public void Normalize_DayWithOnlyCostsActivity_UsesCostsAndSaysTheSourcesDisagree()
    {
        var logger = new CapturingLogger<ChatGptEnterpriseSpendNormalizer>();

        // The Daily Usage API reported nothing, but COSTS shows 100 credits.
        var record = Single(Normalize(
            RawData(Day, [CostsRow(100m)]),
            [SeatRate(), CreditRate(0.04m)], logger));

        Assert.Equal(100m * 0.04m, record.UsageOrOverage);
        Assert.Equal(DailySpendSources.CostsExport, record.Source);
        Assert.Contains("computed from COSTS", Assert.Single(logger.Warnings));
    }

    [Fact]
    public void Normalize_CostsOnlyDayThatIsStillInProgress_StillUsesCostsButRaisesNoWarning()
    {
        var logger = new CapturingLogger<ChatGptEnterpriseSpendNormalizer>();
        var duringTheDay = new FixedTimeProvider(new DateTimeOffset(Day.ToDateTime(new TimeOnly(15, 0)), TimeSpan.Zero));

        // Today: COSTS already shows activity, and the Daily Usage row may simply not
        // have posted yet. That is lag, not the sources disagreeing.
        var record = Single(Normalize(
            RawData(Day, [CostsRow(100m)]),
            [SeatRate(), CreditRate(0.04m)], logger, duringTheDay));

        // The known usage is still recorded; only the alarm is withheld.
        Assert.Equal(100m * 0.04m, record.UsageOrOverage);
        Assert.Equal(DailySpendSources.CostsExport, record.Source);
        Assert.Empty(logger.Warnings);
    }

    [Fact]
    public void Normalize_CorrectRateOnAPartialInProgressDay_RaisesNoRateWarning()
    {
        var logger = new CapturingLogger<ChatGptEnterpriseSpendNormalizer>();
        var duringTheDay = new FixedTimeProvider(new DateTimeOffset(Day.ToDateTime(new TimeOnly(15, 0)), TimeSpan.Zero));

        // Real shape from the live API for a day in progress: the vendor computes the
        // estimate from the same partial credits, so credits * rate matches it to within
        // rounding (783.8162375 * 0.04 = 31.3526 against an estimate of 31.35). An incomplete
        // day therefore does not, by itself, make the rate look wrong.
        Normalize(
            RawData(Day, [UsageRow(credits: 783.8162375m, costUsd: null, estimated: 31.35m)]),
            [SeatRate(), CreditRate(0.04m)], logger, duringTheDay);

        Assert.Empty(logger.Warnings);
    }

    [Fact]
    public void Normalize_StaleRateOnTheInProgressDay_IsStillFlagged()
    {
        var logger = new CapturingLogger<ChatGptEnterpriseSpendNormalizer>();
        var duringTheDay = new FixedTimeProvider(new DateTimeOffset(Day.ToDateTime(new TimeOnly(15, 0)), TimeSpan.Zero));

        // Deliberately not guarded by "date < today": the two figures come from the same
        // row, so a disagreement here is a real signal about the rate, and the current day
        // is the earliest chance to see it.
        Normalize(
            RawData(Day, [UsageRow(credits: 1000m, costUsd: null, estimated: 40m)]),
            [SeatRate(), CreditRate(0.07m)], logger, duringTheDay);

        Assert.Contains("rate may be stale", Assert.Single(logger.Warnings));
    }

    [Fact]
    public void Normalize_LegacyUntaggedCostsRows_StillNormalizeExactlyAsBefore()
    {
        // Raw records stored before the Daily Usage API existed have no Source tag.
        var legacy = new Dictionary<string, object?>
        {
            [ChatGptCostRowFields.RecordDate] = Day,
            [ChatGptCostRowFields.CostValue] = 10m,
            [ChatGptCostRowFields.CostUnit] = "CREDITS",
            [ChatGptCostRowFields.EstimatedCostUsdValue] = null, // real rows always carry every key
        };
        var raw = new RawVendorSpendData
        {
            TenantId = "zelleri",
            VendorId = VendorCatalog.ChatGptEnterprise.Id,
            Period = new DateRange(Day, Day),
            RecordsByDate = new Dictionary<DateOnly, IReadOnlyList<IReadOnlyDictionary<string, object?>>> { [Day] = [legacy] },
            PrimarySource = DailySpendSources.CostsExport,
        };

        var logger = new CapturingLogger<ChatGptEnterpriseSpendNormalizer>();
        var record = Single(Normalize(raw, [SeatRate(), CreditRate(0.07m)], logger));

        Assert.Equal(10m * 0.07m, record.UsageOrOverage);
        Assert.Equal(DailySpendSources.CostsExport, record.Source);
        Assert.Empty(logger.Warnings);
    }

    [Fact]
    public void Normalize_ManyDisagreements_AreSummarizedInOneWarningNotOnePerDay()
    {
        var logger = new CapturingLogger<ChatGptEnterpriseSpendNormalizer>();
        var days = Enumerable.Range(0, 8).Select(i => Day.AddDays(-i)).ToList();

        var raw = new RawVendorSpendData
        {
            TenantId = "zelleri",
            VendorId = VendorCatalog.ChatGptEnterprise.Id,
            Period = new DateRange(days.Min(), days.Max()),
            RecordsByDate = days.ToDictionary(
                d => d,
                d => (IReadOnlyList<IReadOnlyDictionary<string, object?>>)
                    [UsageRow(1000m, null, 40m), CostsRow(100m, d)]),
            PrimarySource = DailySpendSources.DailyUsageApi,
        };

        Normalize(raw, [SeatRate(), CreditRate(0.04m)], logger);

        var warning = Assert.Single(logger.Warnings);
        Assert.Contains("8 day(s)", warning);
        Assert.Contains("and 3 more", warning);
    }

    // ---- helpers --------------------------------------------------------------------------

    private static DailySpendRecord Single(IReadOnlyList<DailySpendRecord> records) => Assert.Single(records);

    private static IReadOnlyList<DailySpendRecord> Normalize(
        RawVendorSpendData raw,
        IReadOnlyList<VendorRateConfig> rates,
        CapturingLogger<ChatGptEnterpriseSpendNormalizer>? logger = null,
        TimeProvider? timeProvider = null) =>
        new ChatGptEnterpriseSpendNormalizer(
            logger ?? new CapturingLogger<ChatGptEnterpriseSpendNormalizer>(), timeProvider ?? LongAfter)
            .Normalize(raw, rates);

    private static RawVendorSpendData RawData(DateOnly day, IReadOnlyList<IReadOnlyDictionary<string, object?>> rows) => new()
    {
        TenantId = "zelleri",
        VendorId = VendorCatalog.ChatGptEnterprise.Id,
        Period = new DateRange(day, day),
        RecordsByDate = new Dictionary<DateOnly, IReadOnlyList<IReadOnlyDictionary<string, object?>>> { [day] = rows },
        PrimarySource = DailySpendSources.DailyUsageApi,
    };

    private static IReadOnlyDictionary<string, object?> UsageRow(decimal? credits, decimal? costUsd, decimal? estimated) =>
        new Dictionary<string, object?>
        {
            [ChatGptDailyUsageRowFields.Source] = ChatGptRowSources.DailyUsage,
            [ChatGptDailyUsageRowFields.Chats] = 1,
            [ChatGptDailyUsageRowFields.Messages] = 2,
            [ChatGptDailyUsageRowFields.Credits] = credits,
            [ChatGptDailyUsageRowFields.CostUsd] = costUsd,
            [ChatGptDailyUsageRowFields.EstimatedCostUsd] = estimated,
        };

    private static IReadOnlyDictionary<string, object?> CostsRow(decimal credits, DateOnly? day = null) =>
        new Dictionary<string, object?>
        {
            [ChatGptDailyUsageRowFields.Source] = ChatGptRowSources.CostsExport,
            [ChatGptCostRowFields.RecordDate] = day ?? Day,
            [ChatGptCostRowFields.CostValue] = credits,
            [ChatGptCostRowFields.CostUnit] = "CREDITS",
            [ChatGptCostRowFields.EstimatedCostUsdValue] = null, // real rows always carry every key
        };

    private static VendorRateConfig SeatRate() => new()
    {
        VendorId = VendorCatalog.ChatGptEnterprise.Id,
        RateType = ChatGptRateKeys.PerSeatRateType,
        ModelOrSku = ChatGptRateKeys.SeatModelOrSku,
        Rate = 30m,
        SeatCount = 50,
        BillingCadence = BillingCadence.Monthly,
        EffectiveFrom = new DateOnly(2026, 1, 1),
    };

    private static VendorRateConfig CreditRate(decimal rate) => new()
    {
        VendorId = VendorCatalog.ChatGptEnterprise.Id,
        RateType = ChatGptRateKeys.CreditToUsdRateType,
        ModelOrSku = ChatGptRateKeys.CreditUsdModelOrSku,
        Rate = rate,
        EffectiveFrom = new DateOnly(2026, 1, 1),
    };

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
