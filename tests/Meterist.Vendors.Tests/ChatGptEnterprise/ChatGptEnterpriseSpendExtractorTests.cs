using System.Text.Json;
using Meterist.Core.Vendors;
using Meterist.Vendors.ChatGptEnterprise;
using Meterist.Vendors.Tests.GeminiEnterprise;
using Microsoft.Extensions.Logging.Abstractions;

namespace Meterist.Vendors.Tests.ChatGptEnterprise;

public class ChatGptEnterpriseSpendExtractorTests
{
    private static readonly DateRange Period = new(new DateOnly(2026, 7, 19), new DateOnly(2026, 7, 25));

    [Fact]
    public async Task ExtractAsync_WithConfiguredCredential_GroupsRowsByRecordDate()
    {
        var credential = new ChatGptCredential
        {
            OrganizationId = "org-abc123",
            AdminApiKey = "admin-key-xyz",
        };

        var secretStore = new FakeSecretStore();
        await secretStore.SetCredentialAsync(
            "ecosync", VendorCatalog.ChatGptEnterprise.Id, JsonSerializer.Serialize(credential),
            TestContext.Current.CancellationToken);

        var day1 = new DateOnly(2026, 7, 20);
        var day2 = new DateOnly(2026, 7, 21);
        var rows = new List<IReadOnlyDictionary<string, object?>>
        {
            new Dictionary<string, object?>
            {
                [ChatGptCostRowFields.RecordDate] = day1,
                [ChatGptCostRowFields.CostValue] = 5.00m,
            },
            new Dictionary<string, object?>
            {
                [ChatGptCostRowFields.RecordDate] = day1,
                [ChatGptCostRowFields.CostValue] = 2.00m,
            },
            new Dictionary<string, object?>
            {
                [ChatGptCostRowFields.RecordDate] = day2,
                [ChatGptCostRowFields.CostValue] = 9.00m,
            },
        };
        var repository = new FakeChatGptCostLogRepository(rows);

        var extractor = new ChatGptEnterpriseSpendExtractor(
            secretStore, repository, NullLogger<ChatGptEnterpriseSpendExtractor>.Instance);
        var result = await extractor.ExtractAsync("ecosync", Period, TestContext.Current.CancellationToken);

        Assert.Equal(VendorCatalog.ChatGptEnterprise.Id, result.VendorId);
        Assert.Equal("ecosync", result.TenantId);
        Assert.Equal(Period, result.Period);

        // Every day in the 7-day period (Jul 19-25) must be present, not just
        // the 2 days with actual activity — a zero-activity day must still
        // reach the normalizer so its SeatFee accrues.
        Assert.Equal(7, result.RecordsByDate.Count);
        Assert.Equal(2, result.RecordsByDate[day1].Count);
        Assert.Single(result.RecordsByDate[day2]);
        Assert.Empty(result.RecordsByDate[new DateOnly(2026, 7, 19)]);
        Assert.Empty(result.RecordsByDate[new DateOnly(2026, 7, 22)]);

        Assert.Equal(credential, repository.ReceivedCredential);
        Assert.Equal(Period, repository.ReceivedPeriod);
    }

    [Fact]
    public async Task ExtractAsync_DoesNotGapFillDaysBeforeEffectiveStart_WhenVendorClampedThePeriod()
    {
        var credential = new ChatGptCredential
        {
            OrganizationId = "org-abc123",
            AdminApiKey = "admin-key-xyz",
        };

        var secretStore = new FakeSecretStore();
        await secretStore.SetCredentialAsync(
            "ecosync", VendorCatalog.ChatGptEnterprise.Id, JsonSerializer.Serialize(credential),
            TestContext.Current.CancellationToken);

        // Period is Jul 19-25 (7 days); simulate the vendor's retention window
        // clamping the effective start to Jul 22 -- as if Jul 19-21 aged out.
        var clampedEffectiveStart = new DateOnly(2026, 7, 22);
        var repository = new FakeChatGptCostLogRepository([], clampedEffectiveStart);

        var extractor = new ChatGptEnterpriseSpendExtractor(
            secretStore, repository, NullLogger<ChatGptEnterpriseSpendExtractor>.Instance);
        var result = await extractor.ExtractAsync("ecosync", Period, TestContext.Current.CancellationToken);

        // The pre-clamp days must be entirely absent -- not present with an
        // empty row set -- so an existing DailySpendRecord for them is never
        // touched by the upsert that follows normalization.
        Assert.DoesNotContain(new DateOnly(2026, 7, 19), result.RecordsByDate.Keys);
        Assert.DoesNotContain(new DateOnly(2026, 7, 20), result.RecordsByDate.Keys);
        Assert.DoesNotContain(new DateOnly(2026, 7, 21), result.RecordsByDate.Keys);

        // Post-clamp days still get gap-filled as before.
        Assert.Equal(4, result.RecordsByDate.Count);
        Assert.Empty(result.RecordsByDate[new DateOnly(2026, 7, 22)]);
        Assert.Empty(result.RecordsByDate[new DateOnly(2026, 7, 25)]);
    }

    [Fact]
    public async Task ExtractAsync_WithNoCredentialConfigured_ThrowsInvalidOperation()
    {
        var extractor = new ChatGptEnterpriseSpendExtractor(
            new FakeSecretStore(), new FakeChatGptCostLogRepository([]),
            NullLogger<ChatGptEnterpriseSpendExtractor>.Instance);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => extractor.ExtractAsync("ecosync", Period, TestContext.Current.CancellationToken));
        Assert.Contains("ecosync", ex.Message);
    }

    [Fact]
    public async Task ExtractAsync_WithMalformedCredentialJson_ThrowsInvalidOperation()
    {
        var secretStore = new FakeSecretStore();
        await secretStore.SetCredentialAsync(
            "ecosync", VendorCatalog.ChatGptEnterprise.Id, "not valid json", TestContext.Current.CancellationToken);

        var extractor = new ChatGptEnterpriseSpendExtractor(
            secretStore, new FakeChatGptCostLogRepository([]),
            NullLogger<ChatGptEnterpriseSpendExtractor>.Instance);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => extractor.ExtractAsync("ecosync", Period, TestContext.Current.CancellationToken));
    }
}
