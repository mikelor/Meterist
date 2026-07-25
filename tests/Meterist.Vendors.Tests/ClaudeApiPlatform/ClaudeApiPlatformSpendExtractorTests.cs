using Meterist.Core.Vendors;
using Meterist.Vendors.ClaudeApiPlatform;
using Meterist.Vendors.Tests.GeminiEnterprise;
using Microsoft.Extensions.Logging.Abstractions;

namespace Meterist.Vendors.Tests.ClaudeApiPlatform;

public class ClaudeApiPlatformSpendExtractorTests
{
    private static readonly DateRange Period = new(new DateOnly(2026, 7, 19), new DateOnly(2026, 7, 25));

    [Fact]
    public async Task ExtractAsync_WithConfiguredCredential_GroupsRowsByRecordDate()
    {
        const string adminApiKey = "sk-ant-admin01-fake-key";

        var secretStore = new FakeSecretStore();
        await secretStore.SetCredentialAsync(
            "ecosync", VendorCatalog.ClaudeApiPlatform.Id, adminApiKey, TestContext.Current.CancellationToken);

        var day1 = new DateOnly(2026, 7, 20);
        var day2 = new DateOnly(2026, 7, 21);
        var rows = new List<IReadOnlyDictionary<string, object?>>
        {
            new Dictionary<string, object?>
            {
                [ClaudeApiCostRowFields.RecordDate] = day1,
                [ClaudeApiCostRowFields.AmountUsd] = 5.00m,
            },
            new Dictionary<string, object?>
            {
                [ClaudeApiCostRowFields.RecordDate] = day1,
                [ClaudeApiCostRowFields.AmountUsd] = 2.00m,
            },
            new Dictionary<string, object?>
            {
                [ClaudeApiCostRowFields.RecordDate] = day2,
                [ClaudeApiCostRowFields.AmountUsd] = 9.00m,
            },
        };
        var repository = new FakeClaudeCostReportRepository(rows);

        var extractor = new ClaudeApiPlatformSpendExtractor(
            secretStore, repository, NullLogger<ClaudeApiPlatformSpendExtractor>.Instance);
        var result = await extractor.ExtractAsync("ecosync", Period, TestContext.Current.CancellationToken);

        Assert.Equal(VendorCatalog.ClaudeApiPlatform.Id, result.VendorId);
        Assert.Equal("ecosync", result.TenantId);
        Assert.Equal(Period, result.Period);

        Assert.Equal(2, result.RecordsByDate.Count);
        Assert.Equal(2, result.RecordsByDate[day1].Count);
        Assert.Single(result.RecordsByDate[day2]);

        Assert.Equal(adminApiKey, repository.ReceivedAdminApiKey);
        Assert.Equal(Period, repository.ReceivedPeriod);
    }

    [Fact]
    public async Task ExtractAsync_WithNoCredentialConfigured_ThrowsInvalidOperation()
    {
        var extractor = new ClaudeApiPlatformSpendExtractor(
            new FakeSecretStore(), new FakeClaudeCostReportRepository([]),
            NullLogger<ClaudeApiPlatformSpendExtractor>.Instance);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => extractor.ExtractAsync("ecosync", Period, TestContext.Current.CancellationToken));
        Assert.Contains("ecosync", ex.Message);
    }
}
