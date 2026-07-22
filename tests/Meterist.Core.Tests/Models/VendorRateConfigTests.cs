using Meterist.Core.Models;
using Meterist.Core.Vendors;

namespace Meterist.Core.Tests.Models;

public class VendorRateConfigTests
{
    [Fact]
    public void NullTenantId_RepresentsThePublicDefaultRate()
    {
        var publicRate = new VendorRateConfig
        {
            TenantId = null,
            VendorId = VendorCatalog.ClaudeApiPlatform.Id,
            RateType = "per-million-tokens-input",
            ModelOrSku = "sonnet-5",
            Rate = 2.00m,
            EffectiveFrom = new DateOnly(2026, 1, 1),
            EffectiveTo = new DateOnly(2026, 8, 31),
        };

        var tenantOverride = new VendorRateConfig
        {
            TenantId = "big-client",
            VendorId = publicRate.VendorId,
            RateType = publicRate.RateType,
            ModelOrSku = publicRate.ModelOrSku,
            Rate = 1.50m,
            EffectiveFrom = publicRate.EffectiveFrom,
            EffectiveTo = publicRate.EffectiveTo,
        };

        Assert.Null(publicRate.TenantId);
        Assert.Equal("big-client", tenantOverride.TenantId);
    }

    [Fact]
    public void SeatCountAndBillingCadence_AreOptionalAndUnconsumedForNow()
    {
        // Defined ahead of the first vendor (Claude Enterprise / ChatGPT Enterprise)
        // that will need to derive a seat fee from contract terms rather than a
        // vendor API — see VendorRateConfig's doc comment.
        var seatTerm = new VendorRateConfig
        {
            TenantId = "ecosync",
            VendorId = VendorCatalog.ClaudeEnterprise.Id,
            RateType = "per-seat",
            Rate = 30.00m,
            SeatCount = 25,
            BillingCadence = BillingCadence.Monthly,
            EffectiveFrom = new DateOnly(2026, 1, 1),
        };

        Assert.Equal(25, seatTerm.SeatCount);
        Assert.Equal(BillingCadence.Monthly, seatTerm.BillingCadence);
    }
}
