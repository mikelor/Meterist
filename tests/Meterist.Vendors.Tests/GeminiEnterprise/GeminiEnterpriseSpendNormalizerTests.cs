using Meterist.Core.Models;
using Meterist.Core.Vendors;
using Meterist.Vendors.GeminiEnterprise;
using Microsoft.Extensions.Logging.Abstractions;

namespace Meterist.Vendors.Tests.GeminiEnterprise;

public class GeminiEnterpriseSpendNormalizerTests
{
    [Fact]
    public void Normalize_ProducesOneRecordPerDay_SplittingSubscriptionFromOverageAndApplyingCredits()
    {
        var day1 = new DateOnly(2026, 7, 20);
        var day2 = new DateOnly(2026, 7, 21);

        var rawData = new RawVendorSpendData
        {
            TenantId = "ecosync",
            VendorId = VendorCatalog.GeminiEnterprise.Id,
            Period = new DateRange(day1, day2),
            RecordsByDate = new Dictionary<DateOnly, IReadOnlyList<IReadOnlyDictionary<string, object?>>>
            {
                [day1] =
                [
                    Row("Gemini Enterprise Plus: Subscription", 100.00m, creditsAmount: 0m),
                    Row("Gemini Enterprise Overage", 25.50m, creditsAmount: -5.00m),
                ],
                [day2] =
                [
                    Row("Gemini Enterprise Plus: Subscription", 100.00m, creditsAmount: 0m),
                ],
            },
        };

        var normalizer = new GeminiEnterpriseSpendNormalizer(NullLogger<GeminiEnterpriseSpendNormalizer>.Instance);
        var result = normalizer.Normalize(rawData, applicableRates: []);

        Assert.Equal(2, result.Count);

        var record1 = Assert.Single(result, r => r.Date == day1);
        Assert.Equal(100.00m, record1.SeatFee);
        Assert.Equal(25.50m, record1.UsageOrOverage);
        Assert.Equal(5.00m, record1.CreditsApplied);
        Assert.Equal(125.50m, record1.GrossSpend);
        Assert.Equal(120.50m, record1.NetSpend);
        Assert.Equal("ecosync", record1.TenantId);
        Assert.Equal(VendorCatalog.GeminiEnterprise.Id, record1.VendorId);

        var record2 = Assert.Single(result, r => r.Date == day2);
        Assert.Equal(100.00m, record2.SeatFee);
        Assert.Equal(0m, record2.UsageOrOverage);
        Assert.Equal(0m, record2.CreditsApplied);
        Assert.Equal(100.00m, record2.GrossSpend);
        Assert.Equal(100.00m, record2.NetSpend);
    }

    private static Dictionary<string, object?> Row(string skuDescription, decimal cost, decimal creditsAmount) => new()
    {
        [GeminiBillingRowFields.SkuDescription] = skuDescription,
        [GeminiBillingRowFields.Cost] = cost,
        [GeminiBillingRowFields.CreditsAmount] = creditsAmount,
    };
}
