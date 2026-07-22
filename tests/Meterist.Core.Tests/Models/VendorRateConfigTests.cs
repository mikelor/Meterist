using Meterist.Core.Models;

namespace Meterist.Core.Tests.Models;

public class VendorRateConfigTests
{
    [Fact]
    public void NullTenantId_RepresentsThePublicDefaultRate()
    {
        var publicRate = new VendorRateConfig
        {
            TenantId = null,
            VendorName = "ClaudeApiPlatform",
            RateType = "per-million-tokens-input",
            ModelOrSku = "sonnet-5",
            Rate = 2.00m,
            EffectiveFrom = new DateOnly(2026, 1, 1),
            EffectiveTo = new DateOnly(2026, 8, 31),
        };

        var tenantOverride = new VendorRateConfig
        {
            TenantId = "big-client",
            VendorName = publicRate.VendorName,
            RateType = publicRate.RateType,
            ModelOrSku = publicRate.ModelOrSku,
            Rate = 1.50m,
            EffectiveFrom = publicRate.EffectiveFrom,
            EffectiveTo = publicRate.EffectiveTo,
        };

        Assert.Null(publicRate.TenantId);
        Assert.Equal("big-client", tenantOverride.TenantId);
    }
}
