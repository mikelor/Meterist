using System.Net.Http;
using System.Text.Json;
using Meterist.Core.Vendors;
using Meterist.Vendors.ChatGptEnterprise;
using Meterist.Vendors.Tests.GeminiEnterprise;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Meterist.Vendors.Tests.ChatGptEnterprise;

/// <summary>
/// The organisation ID and workspace ID identify a client. They are not credentials, but the repository is public and the
/// debug log is routinely pasted into chats and issues, so no ChatGPT log line may carry either one, in a URL or on its own.
/// Each test uses distinctive values so a leak cannot be mistaken for anything else, and checks that something was logged
/// so a silent logger cannot pass.
/// </summary>
public class ChatGptLogRedactionTests
{
    private const string OrganizationId = "org-SECRETorgID0123456789";
    private const string WorkspaceId = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee";
    private const string AdminApiKey = "secret-admin-key";

    private static readonly ChatGptCredential Credential =
        new() { OrganizationId = OrganizationId, AdminApiKey = AdminApiKey, WorkspaceId = WorkspaceId };

    private static readonly DateRange Period = new(new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 2));

    private static void AssertNoIdentifiers(IEnumerable<string> messages)
    {
        var all = messages.ToList();
        Assert.NotEmpty(all);
        foreach (var message in all)
        {
            Assert.DoesNotContain(OrganizationId, message);
            Assert.DoesNotContain("org-SECRET", message);
            Assert.DoesNotContain(WorkspaceId, message);
            Assert.DoesNotContain(AdminApiKey, message);
        }
    }

    [Fact]
    public async Task TheExtractor_SaysWhetherEachIdIsSet_NeverTheValue()
    {
        var store = new FakeSecretStore();
        await store.SetCredentialAsync(
            "t1", VendorCatalog.ChatGptEnterprise.Id, JsonSerializer.Serialize(Credential), TestContext.Current.CancellationToken);
        var logger = new CapturingLogger<ChatGptEnterpriseSpendExtractor>();
        var extractor = new ChatGptEnterpriseSpendExtractor(
            store, new FakeChatGptCostLogRepository([]), new FakeChatGptDailyUsageRepository(), logger);

        await extractor.ExtractAsync("t1", Period, TestContext.Current.CancellationToken);

        AssertNoIdentifiers(logger.Entries.Select(e => e.Message));
        Assert.Contains(logger.Entries, e => e.Message.Contains("organization ID set") && e.Message.Contains("workspace ID set"));
    }

    [Fact]
    public async Task TheExtractor_SaysWhenTheWorkspaceIdIsNotSet()
    {
        var store = new FakeSecretStore();
        await store.SetCredentialAsync(
            "t1", VendorCatalog.ChatGptEnterprise.Id,
            JsonSerializer.Serialize(Credential with { WorkspaceId = null }), TestContext.Current.CancellationToken);
        var logger = new CapturingLogger<ChatGptEnterpriseSpendExtractor>();
        var extractor = new ChatGptEnterpriseSpendExtractor(
            store, new FakeChatGptCostLogRepository([]), new FakeChatGptDailyUsageRepository(), logger);

        await extractor.ExtractAsync("t1", Period, TestContext.Current.CancellationToken);

        AssertNoIdentifiers(logger.Entries.Select(e => e.Message));
        Assert.Contains(logger.Entries, e => e.Message.Contains("workspace ID not set"));
    }

    [Fact]
    public async Task TheDailyUsageClient_LogsNoWorkspaceIdInAnyLine()
    {
        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath($"/v1/analytics/workspaces/{WorkspaceId}/usage").UsingGet()).RespondWith(
            Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/json")
                .WithBody("""{ "object": "page", "data": [], "has_more": false, "next_page": null }"""));
        var logger = new CapturingLogger<HttpChatGptDailyUsageRepository>();
        var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { BaseAddress = new Uri(server.Url + "/") };

        await new HttpChatGptDailyUsageRepository(client, logger)
            .QueryDailyUsageAsync(Credential, Period, ChatGptUsageGrouping.PerUser, TestContext.Current.CancellationToken);

        AssertNoIdentifiers(logger.Entries.Select(e => e.Message));
        Assert.Contains(logger.Entries, e => e.Message.Contains("2026-10-01") || e.Message.Contains("10/01/2026"));   // the window is still logged
    }

    [Fact]
    public async Task TheCostLogClient_LogsNoOrganizationIdInAnyLine()
    {
        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath($"/v1/compliance/organizations/{OrganizationId}/logs").UsingGet()).RespondWith(
            Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/json")
                .WithBody("""{ "data": [], "has_more": false, "last_end_time": null }"""));
        var logger = new CapturingLogger<HttpChatGptCostLogRepository>();
        var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { BaseAddress = new Uri(server.Url + "/") };

        await new HttpChatGptCostLogRepository(client, new NoOpFactory(), logger)
            .QueryCostRowsAsync(Credential, Period, TestContext.Current.CancellationToken);

        AssertNoIdentifiers(logger.Entries.Select(e => e.Message));
    }

    private sealed class NoOpFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }
}
