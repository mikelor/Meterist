using Meterist.Core.Models;
using Meterist.Core.Vendors;
using Meterist.Vendors.ClaudeEnterprise;
using Microsoft.Extensions.Logging.Abstractions;

namespace Meterist.Vendors.Tests.ClaudeEnterprise;

public class ClaudeEnterpriseSpendNormalizerTests
{
    private static readonly DateOnly Day = new(2026, 7, 15); // July has 31 days

    [Fact]
    public void Normalize_SumsPerUserRowsIntoDayTotal_AndAddsProratedSeatFee()
    {
        var rawData = new RawVendorSpendData
        {
            TenantId = "zelleri",
            VendorId = VendorCatalog.ClaudeEnterprise.Id,
            Period = new DateRange(Day, Day),
            RecordsByDate = new Dictionary<DateOnly, IReadOnlyList<IReadOnlyDictionary<string, object?>>>
            {
                [Day] = [Row(10.50m, "user-1"), Row(4.25m, "user-2")],
            },
        };

        var seatRate = new VendorRateConfig
        {
            VendorId = VendorCatalog.ClaudeEnterprise.Id,
            RateType = ClaudeEnterpriseRateKeys.PerSeatRateType,
            ModelOrSku = ClaudeEnterpriseRateKeys.SeatModelOrSku,
            Rate = 60m,
            SeatCount = 50,
            BillingCadence = BillingCadence.Monthly,
            EffectiveFrom = new DateOnly(2026, 1, 1),
        };

        var normalizer = new ClaudeEnterpriseSpendNormalizer(NullLogger<ClaudeEnterpriseSpendNormalizer>.Instance);
        var result = normalizer.Normalize(rawData, [seatRate]);

        var record = Assert.Single(result);
        var expectedSeatFee = 50m * 60m / 31m;
        Assert.Equal(expectedSeatFee, record.SeatFee);
        Assert.Equal(14.75m, record.UsageOrOverage);
        Assert.Equal(0m, record.CreditsApplied);
        Assert.Equal(expectedSeatFee + 14.75m, record.GrossSpend);
        Assert.Equal(record.GrossSpend, record.NetSpend);
    }

    [Fact]
    public void Normalize_NoUsageCreditsActivity_UsageOrOverageIsZero_ButSeatFeeStillApplies()
    {
        var rawData = new RawVendorSpendData
        {
            TenantId = "zelleri",
            VendorId = VendorCatalog.ClaudeEnterprise.Id,
            Period = new DateRange(Day, Day),
            RecordsByDate = new Dictionary<DateOnly, IReadOnlyList<IReadOnlyDictionary<string, object?>>>
            {
                [Day] = [],
            },
        };

        var seatRate = new VendorRateConfig
        {
            VendorId = VendorCatalog.ClaudeEnterprise.Id,
            RateType = ClaudeEnterpriseRateKeys.PerSeatRateType,
            ModelOrSku = ClaudeEnterpriseRateKeys.SeatModelOrSku,
            Rate = 60m,
            SeatCount = 50,
            BillingCadence = BillingCadence.Monthly,
            EffectiveFrom = new DateOnly(2026, 1, 1),
        };

        var normalizer = new ClaudeEnterpriseSpendNormalizer(NullLogger<ClaudeEnterpriseSpendNormalizer>.Instance);
        var result = normalizer.Normalize(rawData, [seatRate]);

        var record = Assert.Single(result);
        Assert.Equal(0m, record.UsageOrOverage);
        Assert.True(record.SeatFee > 0m); // seat-based plan without usage credits — still pays the seat fee
    }

    [Fact]
    public void Normalize_NoSeatRateConfigured_SeatFeeIsZero()
    {
        var rawData = new RawVendorSpendData
        {
            TenantId = "zelleri",
            VendorId = VendorCatalog.ClaudeEnterprise.Id,
            Period = new DateRange(Day, Day),
            RecordsByDate = new Dictionary<DateOnly, IReadOnlyList<IReadOnlyDictionary<string, object?>>>
            {
                [Day] = [Row(3.00m, "user-1")],
            },
        };

        var normalizer = new ClaudeEnterpriseSpendNormalizer(NullLogger<ClaudeEnterpriseSpendNormalizer>.Instance);
        var result = normalizer.Normalize(rawData, applicableRates: []);

        var record = Assert.Single(result);
        Assert.Equal(0m, record.SeatFee);
        Assert.Equal(3.00m, record.UsageOrOverage);
    }

    private static Dictionary<string, object?> Row(decimal amountUsd, string userId) => new()
    {
        [ClaudeAnalyticsCostRowFields.AmountUsd] = amountUsd,
        [ClaudeAnalyticsCostRowFields.UserId] = userId,
    };
}
