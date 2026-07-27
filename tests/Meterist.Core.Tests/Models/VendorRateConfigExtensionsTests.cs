using Meterist.Core.Models;

namespace Meterist.Core.Tests.Models;

public class VendorRateConfigExtensionsTests
{
    private static readonly Guid VendorId = Guid.NewGuid();

    [Fact]
    public void FindRateForDay_MatchesByModelOrSkuAndDateWindow()
    {
        var rates = new List<VendorRateConfig>
        {
            Rate("seat", 30m, new DateOnly(2026, 1, 1), new DateOnly(2026, 6, 30)),
            Rate("seat", 35m, new DateOnly(2026, 7, 1)),
            Rate("credit-usd", 0.07m, new DateOnly(2026, 1, 1)),
        };

        var julyRate = rates.FindRateForDay("seat", new DateOnly(2026, 7, 15));
        Assert.Equal(35m, julyRate?.Rate);

        var marchRate = rates.FindRateForDay("seat", new DateOnly(2026, 3, 15));
        Assert.Equal(30m, marchRate?.Rate);
    }

    [Fact]
    public void FindRateForDay_NoMatch_ReturnsNull()
    {
        var rates = new List<VendorRateConfig> { Rate("seat", 30m, new DateOnly(2026, 1, 1)) };

        Assert.Null(rates.FindRateForDay("credit-usd", new DateOnly(2026, 3, 15)));
        Assert.Null(rates.FindRateForDay("seat", new DateOnly(2025, 12, 31)));
    }

    [Theory]
    [InlineData(BillingCadence.Monthly, 2026, 2, 28)] // Feb 2026 has 28 days
    [InlineData(BillingCadence.Monthly, 2026, 7, 31)]
    [InlineData(BillingCadence.Annual, 2026, 1, 365)]
    [InlineData(BillingCadence.Annual, 2028, 1, 366)] // leap year
    public void ProrateSeatFee_DividesBySeatCadencePeriod(
        BillingCadence cadence, int year, int month, int expectedDaysInPeriod)
    {
        var rate = new VendorRateConfig
        {
            VendorId = VendorId,
            RateType = "per-seat",
            ModelOrSku = "seat",
            Rate = 30m,
            SeatCount = 50,
            BillingCadence = cadence,
            EffectiveFrom = new DateOnly(2026, 1, 1),
        };

        var prorated = rate.ProrateSeatFee(new DateOnly(year, month, 1));

        Assert.Equal(50m * 30m / expectedDaysInPeriod, prorated);
    }

    [Fact]
    public void ProrateSeatFee_OneTimeCadence_IsNotProrated()
    {
        var rate = new VendorRateConfig
        {
            VendorId = VendorId,
            RateType = "per-seat",
            ModelOrSku = "seat",
            Rate = 100m,
            SeatCount = 10,
            BillingCadence = BillingCadence.OneTime,
            EffectiveFrom = new DateOnly(2026, 1, 1),
        };

        Assert.Equal(1000m, rate.ProrateSeatFee(new DateOnly(2026, 6, 15)));
    }

    [Fact]
    public void ProrateSeatFee_NoSeatCountConfigured_DefaultsToOne()
    {
        var rate = new VendorRateConfig
        {
            VendorId = VendorId,
            RateType = "per-seat",
            ModelOrSku = "seat",
            Rate = 30m,
            BillingCadence = BillingCadence.Monthly,
            EffectiveFrom = new DateOnly(2026, 1, 1),
        };

        Assert.Equal(30m / 31, rate.ProrateSeatFee(new DateOnly(2026, 7, 1)));
    }

    private static VendorRateConfig Rate(
        string modelOrSku, decimal rate, DateOnly effectiveFrom, DateOnly? effectiveTo = null) => new()
    {
        VendorId = VendorId,
        RateType = modelOrSku == "seat" ? "per-seat" : "credit-to-usd",
        ModelOrSku = modelOrSku,
        Rate = rate,
        EffectiveFrom = effectiveFrom,
        EffectiveTo = effectiveTo,
    };
}
