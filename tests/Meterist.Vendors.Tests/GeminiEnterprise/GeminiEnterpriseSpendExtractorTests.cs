using System.Text.Json;
using Meterist.Core.Vendors;
using Meterist.Vendors.GeminiEnterprise;
using Meterist.Vendors.Tests.ChatGptEnterprise;
using Microsoft.Extensions.Logging.Abstractions;

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

        var extractor = new GeminiEnterpriseSpendExtractor(
            secretStore, repository, NullLogger<GeminiEnterpriseSpendExtractor>.Instance);
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
            new FakeSecretStore(), new FakeGeminiBillingQueryRepository([]),
            NullLogger<GeminiEnterpriseSpendExtractor>.Instance);

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
            secretStore, new FakeGeminiBillingQueryRepository([]),
            NullLogger<GeminiEnterpriseSpendExtractor>.Instance);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => extractor.ExtractAsync("ecosync", Period, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ExtractAsync_WarnsAboutExcludedGeminiLookingCharges_AndStillSucceeds()
    {
        var logger = new CapturingLogger<GeminiEnterpriseSpendExtractor>();
        var repository = new FakeGeminiBillingQueryRepository
        {
            ExcludedToReturn =
            [
                new ExcludedBillingLine(
                    "Vertex AI", "Agent Platform Compute", 12.34m, new DateOnly(2026, 7, 3), new DateOnly(2026, 7, 9)),
            ],
        };

        var result = await ExtractWithCredentialAsync(repository, logger);

        Assert.Equal(VendorCatalog.GeminiEnterprise.Id, result.VendorId);
        var warning = Assert.Single(logger.Warnings);
        Assert.Contains("Vertex AI | Agent Platform Compute: 12.34", warning);
        Assert.Contains("2026-07-03 to 2026-07-09", warning);
        Assert.Contains(GeminiBillingFilter.ConfigurationSection, warning);
    }

    [Fact]
    public async Task ExtractAsync_NoExcludedCharges_LogsNoWarning()
    {
        var logger = new CapturingLogger<GeminiEnterpriseSpendExtractor>();

        await ExtractWithCredentialAsync(new FakeGeminiBillingQueryRepository(), logger);

        Assert.Empty(logger.Warnings);
    }

    [Fact]
    public async Task ExtractAsync_ExcludedChargeCheckFailing_DoesNotFailTheExtraction()
    {
        var logger = new CapturingLogger<GeminiEnterpriseSpendExtractor>();
        var repository = new FakeGeminiBillingQueryRepository { ExcludedCheckThrows = new InvalidOperationException("bq down") };

        var result = await ExtractWithCredentialAsync(repository, logger);

        Assert.NotNull(result);
        Assert.Contains(logger.Warnings, w => w.Contains("Could not check"));
    }

    [Fact]
    public async Task ExtractAsync_ManyExcludedCharges_ListsSomeAndCountsTheRest()
    {
        var logger = new CapturingLogger<GeminiEnterpriseSpendExtractor>();
        var lines = Enumerable.Range(1, 11)
            .Select(i => new ExcludedBillingLine("Vertex AI", $"Agent Platform SKU {i}", i, Period.Start, Period.End))
            .ToList();

        await ExtractWithCredentialAsync(new FakeGeminiBillingQueryRepository { ExcludedToReturn = lines }, logger);

        var warning = Assert.Single(logger.Warnings);
        Assert.Contains("11 Gemini-related billing line(s)", warning);
        Assert.Contains("and 3 more", warning);
    }

    private static async Task<RawVendorSpendData> ExtractWithCredentialAsync(
        FakeGeminiBillingQueryRepository repository, CapturingLogger<GeminiEnterpriseSpendExtractor> logger)
    {
        var secretStore = new FakeSecretStore();
        await secretStore.SetCredentialAsync(
            "tenant-a", VendorCatalog.GeminiEnterprise.Id,
            JsonSerializer.Serialize(new GeminiCredential
            {
                ServiceAccountJson = """{"type":"service_account"}""",
                BillingProjectId = "acme-billing",
                BillingDatasetId = "billing_export",
                BillingTableId = "gcp_billing_export_v1_ABC123",
            }),
            TestContext.Current.CancellationToken);

        return await new GeminiEnterpriseSpendExtractor(secretStore, repository, logger)
            .ExtractAsync("tenant-a", Period, TestContext.Current.CancellationToken);
    }}
