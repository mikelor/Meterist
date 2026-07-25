using Meterist.Core.Vendors;
using Meterist.Vendors.ClaudeApiPlatform;
using Microsoft.Extensions.Logging.Abstractions;

namespace Meterist.Vendors.Tests.ClaudeApiPlatform;

public class ClaudeApiPlatformSpendNormalizerTests
{
    [Fact]
    public void Normalize_SumsMultipleRowsIntoOneDayTotal_WithNoSeatFeeOrCredits()
    {
        var day1 = new DateOnly(2026, 7, 20);
        var day2 = new DateOnly(2026, 7, 21);

        var rawData = new RawVendorSpendData
        {
            TenantId = "ecosync",
            VendorId = VendorCatalog.ClaudeApiPlatform.Id,
            Period = new DateRange(day1, day2),
            RecordsByDate = new Dictionary<DateOnly, IReadOnlyList<IReadOnlyDictionary<string, object?>>>
            {
                [day1] = [Row(10.50m), Row(4.25m)],
                [day2] = [Row(7.00m)],
            },
        };

        var normalizer = new ClaudeApiPlatformSpendNormalizer(NullLogger<ClaudeApiPlatformSpendNormalizer>.Instance);
        var result = normalizer.Normalize(rawData, applicableRates: []);

        Assert.Equal(2, result.Count);

        var record1 = Assert.Single(result, r => r.Date == day1);
        Assert.Equal(0m, record1.SeatFee);
        Assert.Equal(14.75m, record1.UsageOrOverage);
        Assert.Equal(0m, record1.CreditsApplied);
        Assert.Equal(14.75m, record1.GrossSpend);
        Assert.Equal(record1.GrossSpend, record1.NetSpend);
        Assert.Equal("ecosync", record1.TenantId);
        Assert.Equal(VendorCatalog.ClaudeApiPlatform.Id, record1.VendorId);

        var record2 = Assert.Single(result, r => r.Date == day2);
        Assert.Equal(7.00m, record2.UsageOrOverage);
        Assert.Equal(record2.GrossSpend, record2.NetSpend);
    }

    [Fact]
    public void Normalize_EmptyDay_ProducesZeroSpendRecord()
    {
        var day = new DateOnly(2026, 7, 20);
        var rawData = new RawVendorSpendData
        {
            TenantId = "ecosync",
            VendorId = VendorCatalog.ClaudeApiPlatform.Id,
            Period = new DateRange(day, day),
            RecordsByDate = new Dictionary<DateOnly, IReadOnlyList<IReadOnlyDictionary<string, object?>>>
            {
                [day] = [],
            },
        };

        var normalizer = new ClaudeApiPlatformSpendNormalizer(NullLogger<ClaudeApiPlatformSpendNormalizer>.Instance);
        var result = normalizer.Normalize(rawData, applicableRates: []);

        var record = Assert.Single(result);
        Assert.Equal(0m, record.GrossSpend);
        Assert.Equal(0m, record.NetSpend);
    }

    private static Dictionary<string, object?> Row(decimal amountUsd) => new()
    {
        [ClaudeApiCostRowFields.AmountUsd] = amountUsd,
    };
}
