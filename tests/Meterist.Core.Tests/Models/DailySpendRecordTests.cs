using Meterist.Core.Models;
using Meterist.Core.Vendors;

namespace Meterist.Core.Tests.Models;

public class DailySpendRecordTests
{
    [Fact]
    public void UsageOrOverage_CanLegitimatelyBeZero()
    {
        // Not a data gap: e.g. Claude API Platform always, or Claude Enterprise
        // when "usage credits" isn't enabled for that tenant. See architecture.md §4.
        var record = new DailySpendRecord
        {
            TenantId = "ecosync",
            VendorId = VendorCatalog.ClaudeApiPlatform.Id,
            Date = new DateOnly(2026, 7, 20),
            SeatFee = 0m,
            UsageOrOverage = 0m,
            GrossSpend = 42.50m,
            CreditsApplied = 0m,
            NetSpend = 42.50m,
        };

        Assert.Equal(0m, record.UsageOrOverage);
        Assert.Equal(record.GrossSpend, record.NetSpend);
    }
}
