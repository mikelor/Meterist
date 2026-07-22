using System.Text.Json;
using Meterist.Core.Vendors;
using Meterist.Vendors.GeminiEnterprise;

namespace Meterist.Vendors.Tests.GeminiEnterprise;

public class GeminiEnterpriseSpendExtractorTests
{
    private static readonly DateRange Period = new(new DateOnly(2026, 7, 19), new DateOnly(2026, 7, 25));

    [Fact]
    public async Task ExtractAsync_WithConfiguredCredential_GroupsRowsByRecordDate()
    {
        var credential = new GeminiCredential
        {
            ServiceAccountJson = """{"type":"service_account"}""",
            BillingProjectId = "acme-billing",
            BillingDatasetId = "billing_export",
            BillingTableId = "gcp_billing_export_v1_ABC123",
        };

        var secretStore = new FakeSecretStore();
        await secretStore.SetCredentialAsync(
            "ecosync", VendorCatalog.GeminiEnterprise.Id, JsonSerializer.Serialize(credential),
            TestContext.Current.CancellationToken);

        var day1 = new DateOnly(2026, 7, 20);
        var day2 = new DateOnly(2026, 7, 21);
        var rows = new List<IReadOnlyDictionary<string, object?>>
        {
            new Dictionary<string, object?>
            {
                [GeminiBillingRowFields.RecordDate] = day1,
                [GeminiBillingRowFields.Cost] = 42.00m,
            },
            new Dictionary<string, object?>
            {
                [GeminiBillingRowFields.RecordDate] = day1,
                [GeminiBillingRowFields.Cost] = 8.00m,
            },
            new Dictionary<string, object?>
            {
                [GeminiBillingRowFields.RecordDate] = day2,
                [GeminiBillingRowFields.Cost] = 15.00m,
            },
        };
        var repository = new FakeGeminiBillingQueryRepository(rows);

        var extractor = new GeminiEnterpriseSpendExtractor(secretStore, repository);
        var result = await extractor.ExtractAsync("ecosync", Period, TestContext.Current.CancellationToken);

        Assert.Equal(VendorCatalog.GeminiEnterprise.Id, result.VendorId);
        Assert.Equal("ecosync", result.TenantId);
        Assert.Equal(Period, result.Period);

        Assert.Equal(2, result.RecordsByDate.Count);
        Assert.Equal(2, result.RecordsByDate[day1].Count);
        Assert.Single(result.RecordsByDate[day2]);

        Assert.Equal(credential, repository.ReceivedCredential);
        Assert.Equal(Period, repository.ReceivedPeriod);
    }

    [Fact]
    public async Task ExtractAsync_WithNoCredentialConfigured_ThrowsInvalidOperation()
    {
        var extractor = new GeminiEnterpriseSpendExtractor(
            new FakeSecretStore(), new FakeGeminiBillingQueryRepository([]));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => extractor.ExtractAsync("ecosync", Period, TestContext.Current.CancellationToken));
        Assert.Contains("ecosync", ex.Message);
    }

    [Fact]
    public async Task ExtractAsync_WithMalformedCredentialJson_ThrowsInvalidOperation()
    {
        var secretStore = new FakeSecretStore();
        await secretStore.SetCredentialAsync(
            "ecosync", VendorCatalog.GeminiEnterprise.Id, "not valid json", TestContext.Current.CancellationToken);

        var extractor = new GeminiEnterpriseSpendExtractor(
            secretStore, new FakeGeminiBillingQueryRepository([]));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => extractor.ExtractAsync("ecosync", Period, TestContext.Current.CancellationToken));
    }
}
