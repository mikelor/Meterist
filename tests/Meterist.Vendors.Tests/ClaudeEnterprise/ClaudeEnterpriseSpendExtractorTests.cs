using Meterist.Core.Vendors;
using Meterist.Vendors.ClaudeEnterprise;
using Meterist.Vendors.Tests.GeminiEnterprise;
using Microsoft.Extensions.Logging.Abstractions;

namespace Meterist.Vendors.Tests.ClaudeEnterprise;

public class ClaudeEnterpriseSpendExtractorTests
{
    private static readonly DateRange Period = new(new DateOnly(2026, 7, 19), new DateOnly(2026, 7, 25));

    [Fact]
    public async Task ExtractAsync_WithConfiguredCredential_GroupsRowsByRecordDate()
    {
        const string analyticsApiKey = "sk-ant-fake-analytics-key";

        var secretStore = new FakeSecretStore();
        await secretStore.SetCredentialAsync(
            "ecosync", VendorCatalog.ClaudeEnterprise.Id, analyticsApiKey, TestContext.Current.CancellationToken);

        var day1 = new DateOnly(2026, 7, 20);
        var day2 = new DateOnly(2026, 7, 21);
        var rows = new List<IReadOnlyDictionary<string, object?>>
        {
            new Dictionary<string, object?>
            {
                [ClaudeAnalyticsCostRowFields.RecordDate] = day1,
                [ClaudeAnalyticsCostRowFields.AmountUsd] = 5.00m,
            },
            new Dictionary<string, object?>
            {
                [ClaudeAnalyticsCostRowFields.RecordDate] = day1,
                [ClaudeAnalyticsCostRowFields.AmountUsd] = 2.00m,
            },
            new Dictionary<string, object?>
            {
                [ClaudeAnalyticsCostRowFields.RecordDate] = day2,
                [ClaudeAnalyticsCostRowFields.AmountUsd] = 9.00m,
            },
        };
        var repository = new FakeClaudeAnalyticsCostReportRepository(rows);

        var extractor = new ClaudeEnterpriseSpendExtractor(
            secretStore, repository, NullLogger<ClaudeEnterpriseSpendExtractor>.Instance);
        var result = await extractor.ExtractAsync("ecosync", Period, TestContext.Current.CancellationToken);

        Assert.Equal(VendorCatalog.ClaudeEnterprise.Id, result.VendorId);
        Assert.Equal("ecosync", result.TenantId);
        Assert.Equal(Period, result.Period);

        Assert.Equal(2, result.RecordsByDate.Count);
        Assert.Equal(2, result.RecordsByDate[day1].Count);
        Assert.Single(result.RecordsByDate[day2]);

        Assert.Equal(analyticsApiKey, repository.ReceivedAnalyticsApiKey);
        Assert.Equal(Period, repository.ReceivedPeriod);
    }

    [Fact]
    public async Task ExtractAsync_WithNoCredentialConfigured_ThrowsInvalidOperation()
    {
        var extractor = new ClaudeEnterpriseSpendExtractor(
            new FakeSecretStore(), new FakeClaudeAnalyticsCostReportRepository([]),
            NullLogger<ClaudeEnterpriseSpendExtractor>.Instance);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => extractor.ExtractAsync("ecosync", Period, TestContext.Current.CancellationToken));
        Assert.Contains("ecosync", ex.Message);
    }
}
