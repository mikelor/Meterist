using System.Text.Json;
using Meterist.Core.Models;
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
            secretStore, repository, new FakeChatGptDailyUsageRepository(), NullLogger<ChatGptEnterpriseSpendExtractor>.Instance);
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
    public async Task ExtractAsync_WithWorkspaceIdInCredential_PassesItToTheRepository()
    {
        var credential = new ChatGptCredential
        {
            OrganizationId = "org-abc123",
            AdminApiKey = "admin-key-xyz",
            WorkspaceId = "11111111-2222-3333-4444-555555555555",
        };

        var secretStore = new FakeSecretStore();
        await secretStore.SetCredentialAsync(
            "ecosync", VendorCatalog.ChatGptEnterprise.Id, JsonSerializer.Serialize(credential),
            TestContext.Current.CancellationToken);

        var repository = new FakeChatGptCostLogRepository([]);
        var extractor = new ChatGptEnterpriseSpendExtractor(
            secretStore, repository, new FakeChatGptDailyUsageRepository(), NullLogger<ChatGptEnterpriseSpendExtractor>.Instance);

        await extractor.ExtractAsync("ecosync", Period, TestContext.Current.CancellationToken);

        Assert.Equal(credential, repository.ReceivedCredential);
        Assert.Equal("11111111-2222-3333-4444-555555555555", repository.ReceivedCredential!.WorkspaceId);
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
            secretStore, repository, new FakeChatGptDailyUsageRepository(), NullLogger<ChatGptEnterpriseSpendExtractor>.Instance);
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
    public async Task ExtractAsync_CostsOnlyFallback_DoesNotKeyADayBeforeEffectiveStartEvenWhenARowCameBackForIt()
    {
        var credential = new ChatGptCredential { OrganizationId = "org-abc123", AdminApiKey = "admin-key-xyz" };
        var secretStore = new FakeSecretStore();
        await secretStore.SetCredentialAsync(
            "ecosync", VendorCatalog.ChatGptEnterprise.Id, JsonSerializer.Serialize(credential),
            TestContext.Current.CancellationToken);

        // The export clamped to Jul 22, but it lists files by write time and filters events
        // against the *requested* start, so a sliver of Jul 21 (a day that mostly aged out)
        // can still come back. It must not create a key for that day: a partial figure would
        // be written as if it were the whole day.
        var edgeDay = new DateOnly(2026, 7, 21);
        var firstFullDay = new DateOnly(2026, 7, 22);
        var repository = new FakeChatGptCostLogRepository(
            [
                new Dictionary<string, object?>
                {
                    [ChatGptCostRowFields.RecordDate] = edgeDay,
                    [ChatGptCostRowFields.CostValue] = 3m,
                },
                new Dictionary<string, object?>
                {
                    [ChatGptCostRowFields.RecordDate] = firstFullDay,
                    [ChatGptCostRowFields.CostValue] = 9m,
                },
            ],
            effectiveStartOverride: firstFullDay);

        var extractor = new ChatGptEnterpriseSpendExtractor(
            secretStore, repository, new FakeChatGptDailyUsageRepository(),
            NullLogger<ChatGptEnterpriseSpendExtractor>.Instance);

        var result = await extractor.ExtractAsync("ecosync", Period, TestContext.Current.CancellationToken);

        Assert.DoesNotContain(edgeDay, result.RecordsByDate.Keys);
        Assert.Single(result.RecordsByDate[firstFullDay]);
    }

    [Fact]
    public async Task ExtractAsync_WithNoCredentialConfigured_ThrowsInvalidOperation()
    {
        var extractor = new ChatGptEnterpriseSpendExtractor(
            new FakeSecretStore(), new FakeChatGptCostLogRepository([]), new FakeChatGptDailyUsageRepository(),
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
            secretStore, new FakeChatGptCostLogRepository([]), new FakeChatGptDailyUsageRepository(),
            NullLogger<ChatGptEnterpriseSpendExtractor>.Instance);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => extractor.ExtractAsync("ecosync", Period, TestContext.Current.CancellationToken));
    }

    // ---- Daily Usage API as the source of record -------------------------------------------

    private const string WorkspaceId = "11111111-2222-3333-4444-555555555555";

    private static ChatGptDailyUsageRow UsageRow(DateOnly day, decimal credits, decimal? estimated = null) =>
        new(day, null, null, 1, 2, credits, null, estimated ?? credits * 0.04m, 100, 200, 300, 600);

    private static async Task<FakeSecretStore> StoreCredentialAsync(string? workspaceId)
    {
        var store = new FakeSecretStore();
        await store.SetCredentialAsync(
            "ecosync", VendorCatalog.ChatGptEnterprise.Id,
            JsonSerializer.Serialize(new ChatGptCredential
            {
                OrganizationId = "org-abc123",
                AdminApiKey = "admin-key-xyz",
                WorkspaceId = workspaceId,
            }),
            TestContext.Current.CancellationToken);
        return store;
    }

    private static Dictionary<string, object?> CostsRow(DateOnly day, decimal credits) => new()
    {
        [ChatGptCostRowFields.RecordDate] = day,
        [ChatGptCostRowFields.CostValue] = credits,
        [ChatGptCostRowFields.CostUnit] = "CREDITS",
    };

    [Fact]
    public async Task ExtractAsync_WithWorkspaceId_UsesDailyUsageAsSourceOfRecord()
    {
        var day1 = new DateOnly(2026, 7, 20);
        var day3 = new DateOnly(2026, 7, 22);
        var dailyUsage = new FakeChatGptDailyUsageRepository([UsageRow(day1, 1000m), UsageRow(day3, 500m)]);
        var costs = new FakeChatGptCostLogRepository([]);

        var extractor = new ChatGptEnterpriseSpendExtractor(
            await StoreCredentialAsync(WorkspaceId), costs, dailyUsage,
            NullLogger<ChatGptEnterpriseSpendExtractor>.Instance);

        var result = await extractor.ExtractAsync("ecosync", Period, TestContext.Current.CancellationToken);

        Assert.Equal(DailySpendSources.DailyUsageApi, result.PrimarySource);

        // Workspace-wide totals, for the whole requested period.
        Assert.Equal(ChatGptUsageGrouping.Workspace, dailyUsage.ReceivedGrouping);
        Assert.Equal(Period, dailyUsage.ReceivedPeriod);
        Assert.Equal(WorkspaceId, dailyUsage.ReceivedCredential!.WorkspaceId);

        // Every day is present so a no-usage day still accrues its seat fee.
        Assert.Equal(7, result.RecordsByDate.Count);

        var row1 = Assert.Single(result.RecordsByDate[day1]);
        Assert.Equal(ChatGptRowSources.DailyUsage, row1[ChatGptDailyUsageRowFields.Source]);
        Assert.Equal(1000m, row1[ChatGptDailyUsageRowFields.Credits]);
        Assert.Equal(40m, row1[ChatGptDailyUsageRowFields.EstimatedCostUsd]);
        Assert.Equal(2, row1[ChatGptDailyUsageRowFields.Messages]);

        Assert.Empty(result.RecordsByDate[new DateOnly(2026, 7, 21)]);
        Assert.Single(result.RecordsByDate[day3]);
    }

    [Fact]
    public async Task ExtractAsync_WithWorkspaceId_ReturnsDaysTheCostsExportHasAlreadyLost()
    {
        // The reason for the switch. The COSTS export clamped to Jul 22, so Jul 19-21
        // aged out of its retention window; the Daily Usage API still has them.
        var lostDays = new[] { new DateOnly(2026, 7, 19), new DateOnly(2026, 7, 20), new DateOnly(2026, 7, 21) };
        var dailyUsage = new FakeChatGptDailyUsageRepository(lostDays.Select(d => UsageRow(d, 800m)).ToList());
        var costs = new FakeChatGptCostLogRepository([], effectiveStartOverride: new DateOnly(2026, 7, 22));

        var extractor = new ChatGptEnterpriseSpendExtractor(
            await StoreCredentialAsync(WorkspaceId), costs, dailyUsage,
            NullLogger<ChatGptEnterpriseSpendExtractor>.Instance);

        var result = await extractor.ExtractAsync("ecosync", Period, TestContext.Current.CancellationToken);

        // Unlike the COSTS-only path, which must not touch days before its clamp,
        // these days are present and carry real usage.
        foreach (var day in lostDays)
        {
            var row = Assert.Single(result.RecordsByDate[day]);
            Assert.Equal(800m, row[ChatGptDailyUsageRowFields.Credits]);
        }
    }

    [Fact]
    public async Task ExtractAsync_WithWorkspaceId_KeepsCostsRowsAlongsideTaggedAsCostsExport()
    {
        var day = new DateOnly(2026, 7, 20);
        var dailyUsage = new FakeChatGptDailyUsageRepository([UsageRow(day, 1000m)]);
        var costs = new FakeChatGptCostLogRepository([CostsRow(day, 990m), CostsRow(day, 5m)]);

        var extractor = new ChatGptEnterpriseSpendExtractor(
            await StoreCredentialAsync(WorkspaceId), costs, dailyUsage,
            NullLogger<ChatGptEnterpriseSpendExtractor>.Instance);

        var result = await extractor.ExtractAsync("ecosync", Period, TestContext.Current.CancellationToken);

        var rows = result.RecordsByDate[day];
        Assert.Equal(3, rows.Count);
        Assert.Single(rows, r => (string?)r[ChatGptDailyUsageRowFields.Source] == ChatGptRowSources.DailyUsage);
        Assert.Equal(2, rows.Count(r => (string?)r[ChatGptDailyUsageRowFields.Source] == ChatGptRowSources.CostsExport));
    }

    [Fact]
    public async Task ExtractAsync_WithWorkspaceId_IgnoresCostsRowsFromBeforeTheExportsEffectiveStart()
    {
        // Real case: the export's retention cutoff falls mid-history, and the day just
        // before it comes back as a sliver (273 credits against 5,670 for the whole day).
        var edgeDay = new DateOnly(2026, 7, 21);
        var firstFullDay = new DateOnly(2026, 7, 22);
        var dailyUsage = new FakeChatGptDailyUsageRepository([UsageRow(edgeDay, 5670m), UsageRow(firstFullDay, 100m)]);
        var costs = new FakeChatGptCostLogRepository(
            [CostsRow(edgeDay, 273m), CostsRow(firstFullDay, 99m)], effectiveStartOverride: firstFullDay);

        var extractor = new ChatGptEnterpriseSpendExtractor(
            await StoreCredentialAsync(WorkspaceId), costs, dailyUsage,
            NullLogger<ChatGptEnterpriseSpendExtractor>.Instance);

        var result = await extractor.ExtractAsync("ecosync", Period, TestContext.Current.CancellationToken);

        // The sliver is not merged: the edge day carries only the Daily Usage row.
        Assert.Single(result.RecordsByDate[edgeDay]);
        // From the effective start on, COSTS rows are merged as usual.
        Assert.Equal(2, result.RecordsByDate[firstFullDay].Count);
    }

    [Fact]
    public async Task ExtractAsync_WhenTheCostsCrossCheckFails_StillSucceedsOnDailyUsageAndSaysSo()
    {
        var day = new DateOnly(2026, 7, 20);
        var dailyUsage = new FakeChatGptDailyUsageRepository([UsageRow(day, 1000m)]);
        var costs = new FakeChatGptCostLogRepository([], exceptionToThrow: new HttpRequestException("timed out"));
        var logger = new CapturingLogger<ChatGptEnterpriseSpendExtractor>();

        var extractor = new ChatGptEnterpriseSpendExtractor(
            await StoreCredentialAsync(WorkspaceId), costs, dailyUsage, logger);

        var result = await extractor.ExtractAsync("ecosync", Period, TestContext.Current.CancellationToken);

        Assert.Equal(1, costs.CallCount);
        Assert.Single(result.RecordsByDate[day]);
        var warning = Assert.Single(logger.Warnings);
        Assert.Contains("cross-check", warning);
        Assert.Contains("timed out", warning);
    }

    [Fact]
    public async Task ExtractAsync_WhenDailyUsageFails_FailsLoudlyInsteadOfFallingBackToCosts()
    {
        var dailyUsage = new FakeChatGptDailyUsageRepository(
            exceptionToThrow: new InvalidOperationException("Daily Usage returned 403. Check the key permission."));
        var costs = new FakeChatGptCostLogRepository([CostsRow(new DateOnly(2026, 7, 20), 100m)]);

        var extractor = new ChatGptEnterpriseSpendExtractor(
            await StoreCredentialAsync(WorkspaceId), costs, dailyUsage,
            NullLogger<ChatGptEnterpriseSpendExtractor>.Instance);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => extractor.ExtractAsync("ecosync", Period, TestContext.Current.CancellationToken));

        // The operator sees the actionable message, and a source known to lose
        // data is not quietly substituted.
        Assert.Contains("403", ex.Message);
        Assert.Equal(0, costs.CallCount);
    }

    [Fact]
    public async Task ExtractAsync_WithNoWorkspaceId_FallsBackToCostsAloneWithAWarning()
    {
        var day = new DateOnly(2026, 7, 20);
        var dailyUsage = new FakeChatGptDailyUsageRepository([UsageRow(day, 1000m)]);
        var costs = new FakeChatGptCostLogRepository([CostsRow(day, 50m)]);
        var logger = new CapturingLogger<ChatGptEnterpriseSpendExtractor>();

        var extractor = new ChatGptEnterpriseSpendExtractor(
            await StoreCredentialAsync(workspaceId: null), costs, dailyUsage, logger);

        var result = await extractor.ExtractAsync("ecosync", Period, TestContext.Current.CancellationToken);

        Assert.Equal(0, dailyUsage.CallCount);
        Assert.Equal(1, costs.CallCount);
        Assert.Equal(DailySpendSources.CostsExport, result.PrimarySource);

        // The COSTS rows come through in their original, untagged shape.
        var row = Assert.Single(result.RecordsByDate[day]);
        Assert.False(row.ContainsKey(ChatGptDailyUsageRowFields.Source));

        var warning = Assert.Single(logger.Warnings);
        Assert.Contains("WorkspaceId", warning);
        Assert.Contains("COSTS", warning);
    }

    [Fact]
    public async Task ExtractAsync_WhenDailyUsageReturnsTwoRowsForOneDay_Throws()
    {
        var day = new DateOnly(2026, 7, 20);
        var dailyUsage = new FakeChatGptDailyUsageRepository([UsageRow(day, 1000m), UsageRow(day, 900m)]);

        var extractor = new ChatGptEnterpriseSpendExtractor(
            await StoreCredentialAsync(WorkspaceId), new FakeChatGptCostLogRepository([]), dailyUsage,
            NullLogger<ChatGptEnterpriseSpendExtractor>.Instance);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => extractor.ExtractAsync("ecosync", Period, TestContext.Current.CancellationToken));

        Assert.Contains("2026-07-20", ex.Message);
    }
}
