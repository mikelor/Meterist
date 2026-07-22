using Meterist.Core.Models;

namespace Meterist.Core.Tests.Models;

public class WeeklySpendRecordTests
{
    [Fact]
    public void UsageOrOverage_CanLegitimatelyBeZero()
    {
        // Not a data gap: e.g. Claude API Platform always, or Claude Enterprise
        // when "usage credits" isn't enabled for that tenant. See architecture.md §4.
        var record = new WeeklySpendRecord
        {
            TenantId = "ecosync",
            VendorName = "ClaudeApiPlatform",
            WeekStart = new DateOnly(2026, 7, 19),
            WeekEnd = new DateOnly(2026, 7, 25),
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
