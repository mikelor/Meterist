using Meterist.Core.Models;
using Meterist.Core.Vendors;
using Meterist.Vendors.ChatGptEnterprise;
using Microsoft.Extensions.Logging.Abstractions;

namespace Meterist.Vendors.Tests.ChatGptEnterprise;

public class ChatGptEnterpriseSpendNormalizerTests
{
    private static readonly DateOnly Day = new(2026, 7, 15); // July has 31 days

    [Fact]
    public void Normalize_ProratesSeatFeeAndConvertsCredits_PreferringVendorEstimateWhenPresent()
    {
        var rawData = new RawVendorSpendData
        {
            TenantId = "zelleri",
            VendorId = VendorCatalog.ChatGptEnterprise.Id,
            Period = new DateRange(Day, Day),
            RecordsByDate = new Dictionary<DateOnly, IReadOnlyList<IReadOnlyDictionary<string, object?>>>
            {
                [Day] =
                [
                    // No estimate — converted via the configured credit-to-usd rate.
                    Row(costValue: 10m, costUnit: "CREDITS"),
                    // Has a vendor-supplied estimate — used directly, ignoring the configured rate.
                    Row(costValue: 5m, costUnit: "CREDITS", estimatedCostUsd: 0.50m),
                    // Already a direct currency unit — passed through unconverted.
                    Row(costValue: 2.00m, costUnit: "USD"),
                ],
            },
        };

        var applicableRates = new List<VendorRateConfig>
        {
            new()
            {
                VendorId = VendorCatalog.ChatGptEnterprise.Id,
                RateType = ChatGptRateKeys.PerSeatRateType,
                ModelOrSku = ChatGptRateKeys.SeatModelOrSku,
                Rate = 30m,
                SeatCount = 50,
                BillingCadence = BillingCadence.Monthly,
                EffectiveFrom = new DateOnly(2026, 1, 1),
            },
            new()
            {
                VendorId = VendorCatalog.ChatGptEnterprise.Id,
                RateType = ChatGptRateKeys.CreditToUsdRateType,
                ModelOrSku = ChatGptRateKeys.CreditUsdModelOrSku,
                Rate = 0.07m,
                EffectiveFrom = new DateOnly(2026, 1, 1),
            },
        };

        var normalizer = new ChatGptEnterpriseSpendNormalizer(NullLogger<ChatGptEnterpriseSpendNormalizer>.Instance);
        var result = normalizer.Normalize(rawData, applicableRates);

        var record = Assert.Single(result);
        Assert.Equal("zelleri", record.TenantId);
        Assert.Equal(VendorCatalog.ChatGptEnterprise.Id, record.VendorId);
        Assert.Equal(Day, record.Date);

        var expectedSeatFee = 50m * 30m / 31m; // prorated across July's 31 days
        Assert.Equal(expectedSeatFee, record.SeatFee);

        var expectedUsageOrOverage = (10m * 0.07m) + 0.50m + 2.00m;
        Assert.Equal(expectedUsageOrOverage, record.UsageOrOverage);

        Assert.Equal(0m, record.CreditsApplied);
        Assert.Equal(expectedSeatFee + expectedUsageOrOverage, record.GrossSpend);
        Assert.Equal(record.GrossSpend, record.NetSpend);
    }

    [Fact]
    public void Normalize_NoApplicableRates_SeatFeeIsZeroAndUnconvertibleCreditsAreExcluded()
    {
        var rawData = new RawVendorSpendData
        {
            TenantId = "zelleri",
            VendorId = VendorCatalog.ChatGptEnterprise.Id,
            Period = new DateRange(Day, Day),
            RecordsByDate = new Dictionary<DateOnly, IReadOnlyList<IReadOnlyDictionary<string, object?>>>
            {
                [Day] = [Row(costValue: 10m, costUnit: "CREDITS")],
            },
        };

        var normalizer = new ChatGptEnterpriseSpendNormalizer(NullLogger<ChatGptEnterpriseSpendNormalizer>.Instance);
        var result = normalizer.Normalize(rawData, applicableRates: []);

        var record = Assert.Single(result);
        Assert.Equal(0m, record.SeatFee);
        Assert.Equal(0m, record.UsageOrOverage);
        Assert.Equal(0m, record.GrossSpend);
        Assert.Equal(0m, record.NetSpend);
    }

    [Fact]
    public void Normalize_MultipleDays_ProducesOneRecordPerDayWithLinesAggregated()
    {
        var day2 = Day.AddDays(1);

        var rawData = new RawVendorSpendData
        {
            TenantId = "zelleri",
            VendorId = VendorCatalog.ChatGptEnterprise.Id,
            Period = new DateRange(Day, day2),
            RecordsByDate = new Dictionary<DateOnly, IReadOnlyList<IReadOnlyDictionary<string, object?>>>
            {
                [Day] = [Row(costValue: 1m, costUnit: "USD"), Row(costValue: 2m, costUnit: "USD")],
                [day2] = [Row(costValue: 3m, costUnit: "USD")],
            },
        };

        var normalizer = new ChatGptEnterpriseSpendNormalizer(NullLogger<ChatGptEnterpriseSpendNormalizer>.Instance);
        var result = normalizer.Normalize(rawData, applicableRates: []);

        Assert.Equal(2, result.Count);
        Assert.Equal(3m, Assert.Single(result, r => r.Date == Day).UsageOrOverage);
        Assert.Equal(3m, Assert.Single(result, r => r.Date == day2).UsageOrOverage);
    }

    private static Dictionary<string, object?> Row(decimal costValue, string costUnit, decimal? estimatedCostUsd = null) =>
        new()
        {
            [ChatGptCostRowFields.Sku] = "test-sku",
            [ChatGptCostRowFields.CostValue] = costValue,
            [ChatGptCostRowFields.CostUnit] = costUnit,
            [ChatGptCostRowFields.EstimatedCostUsdValue] = estimatedCostUsd,
        };
}
