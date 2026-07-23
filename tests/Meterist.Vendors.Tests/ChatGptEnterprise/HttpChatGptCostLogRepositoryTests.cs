using System.Net.Http;
using System.Text.Json;
using Meterist.Core.Vendors;
using Meterist.Vendors.ChatGptEnterprise;
using Microsoft.Extensions.Logging.Abstractions;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Meterist.Vendors.Tests.ChatGptEnterprise;

/// <summary>
/// WireMock.Net-backed — this is the HTTP-vendor-shape case docs/architecture.md
/// §11 reserved that package for, unlike Gemini's BigQuery client which isn't
/// sensible to mock directly.
/// </summary>
public class HttpChatGptCostLogRepositoryTests
{
    private const string OrganizationId = "org-test";
    private const string AdminApiKey = "test-admin-key";

    [Fact]
    public async Task QueryCostRowsAsync_PaginatesAndDedupesEventsAcrossFiles()
    {
        using var server = WireMockServer.Start();

        var listPath = $"/v1/compliance/organizations/{OrganizationId}/logs";

        server
            .Given(Request.Create().WithPath(listPath).UsingGet())
            .InScenario("list-files")
            .WillSetStateTo("page-2")
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody("""
                    {
                      "data": [{"id":"file-1","event_type":"COSTS","end_time":"2026-07-20T00:00:00Z","file_name":"a.jsonl","file_size":10,"file_sha256":"x"}],
                      "has_more": true,
                      "last_end_time": "2026-07-20T00:00:00Z"
                    }
                    """));

        server
            .Given(Request.Create().WithPath(listPath).UsingGet())
            .InScenario("list-files")
            .WhenStateIs("page-2")
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody("""
                    {
                      "data": [{"id":"file-2","event_type":"COSTS","end_time":"2026-07-21T00:00:00Z","file_name":"b.jsonl","file_size":10,"file_sha256":"y"}],
                      "has_more": false,
                      "last_end_time": null
                    }
                    """));

        RegisterRedirectingDownload(server, "file-1");
        RegisterRedirectingDownload(server, "file-2");

        // evt-A appears in BOTH files (duplicate under the "at least once"
        // delivery contract) — must be counted once. evt-B and evt-C are each
        // seen only once.
        server
            .Given(Request.Create().WithPath("/signed/file-1").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody(
                EventLine("evt-A", "2026-07-20", "SkuA", 10m, "CREDITS")
                + "\n" + EventLine("evt-B", "2026-07-20", "SkuB", 5m, "USD")));

        server
            .Given(Request.Create().WithPath("/signed/file-2").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody(
                EventLine("evt-A", "2026-07-20", "SkuA", 10m, "CREDITS") // duplicate
                + "\n" + EventLine("evt-C", "2026-07-21", "SkuC", 3m, "CREDITS", estimatedCostUsd: 0.3m)));

        var period = new DateRange(new DateOnly(2026, 7, 20), new DateOnly(2026, 7, 21));
        var credential = new ChatGptCredential { OrganizationId = OrganizationId, AdminApiKey = AdminApiKey };

        var repository = CreateRepository(server);
        var rows = await repository.QueryCostRowsAsync(credential, period, TestContext.Current.CancellationToken);

        Assert.Equal(3, rows.Count); // evt-A once, evt-B, evt-C — not 4
        Assert.Contains(rows, r => (string?)r[ChatGptCostRowFields.EventId] == "evt-B"
            && (string?)r[ChatGptCostRowFields.CostUnit] == "USD"
            && (decimal)r[ChatGptCostRowFields.CostValue]! == 5m);
        Assert.Contains(rows, r => (string?)r[ChatGptCostRowFields.EventId] == "evt-C"
            && (decimal?)r[ChatGptCostRowFields.EstimatedCostUsdValue] == 0.3m);

        // The admin bearer token must reach the authenticated API calls...
        var listEntry = server.LogEntries.First(
            e => string.Equals(e.RequestMessage?.Path, listPath, StringComparison.Ordinal));
        var listAuthHeader = listEntry.RequestMessage?.Headers is { } listHeaders
            && listHeaders.TryGetValue("Authorization", out var listValues)
            ? listValues.FirstOrDefault()
            : null;
        Assert.Equal($"Bearer {AdminApiKey}", listAuthHeader);

        // ...but never the signed-URL host.
        var signedEntries = server.LogEntries
            .Where(e => (e.RequestMessage?.Path ?? string.Empty).StartsWith("/signed/", StringComparison.Ordinal))
            .ToList();
        Assert.NotEmpty(signedEntries);
        Assert.All(signedEntries, e =>
            Assert.False(e.RequestMessage?.Headers is { } headers && headers.ContainsKey("Authorization")));
    }

    [Fact]
    public async Task QueryCostRowsAsync_FiltersEventsOutsideRequestedPeriod()
    {
        using var server = WireMockServer.Start();

        var listPath = $"/v1/compliance/organizations/{OrganizationId}/logs";

        server
            .Given(Request.Create().WithPath(listPath).UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody("""
                    {
                      "data": [{"id":"file-1","event_type":"COSTS","end_time":"2026-07-20T00:00:00Z","file_name":"a.jsonl","file_size":10,"file_sha256":"x"}],
                      "has_more": false,
                      "last_end_time": null
                    }
                    """));

        RegisterRedirectingDownload(server, "file-1");

        server
            .Given(Request.Create().WithPath("/signed/file-1").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody(
                EventLine("evt-in", "2026-07-20", "Sku", 1m, "USD")
                + "\n" + EventLine("evt-out-of-range", "2026-06-01", "Sku", 1m, "USD")));

        var period = new DateRange(new DateOnly(2026, 7, 20), new DateOnly(2026, 7, 20));
        var credential = new ChatGptCredential { OrganizationId = OrganizationId, AdminApiKey = AdminApiKey };

        var repository = CreateRepository(server);
        var rows = await repository.QueryCostRowsAsync(credential, period, TestContext.Current.CancellationToken);

        var row = Assert.Single(rows);
        Assert.Equal("evt-in", row[ChatGptCostRowFields.EventId]);
    }

    private static void RegisterRedirectingDownload(WireMockServer server, string fileId)
    {
        server
            .Given(Request.Create().WithPath($"/v1/compliance/organizations/{OrganizationId}/logs/{fileId}").UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(307)
                .WithHeader("Location", $"{server.Url}/signed/{fileId}"));
    }

    private static string EventLine(
        string eventId, string day, string sku, decimal costValue, string costUnit, decimal? estimatedCostUsd = null)
    {
        // Built via JsonSerializer rather than a raw string literal — the real
        // JSON has enough nested closing braces that hand-written interpolated
        // raw strings become an unreadable brace-counting exercise.
        var evt = new
        {
            event_id = eventId,
            type = "COSTS",
            payload = new
            {
                day,
                identity = new { user_id = "user-1", email = "a@example.com", name = "A" },
                measures = new
                {
                    billing = new object[]
                    {
                        new
                        {
                            sku,
                            cost = new { value = costValue, unit = costUnit },
                            estimated_cost_usd = estimatedCostUsd is null
                                ? null
                                : (object)new { value = estimatedCostUsd.Value, unit = "USD" },
                        },
                    },
                },
            },
        };

        return JsonSerializer.Serialize(evt);
    }

    private static HttpChatGptCostLogRepository CreateRepository(WireMockServer server)
    {
        var handler = new HttpClientHandler { AllowAutoRedirect = false };
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri(server.Url + "/") };
        return new HttpChatGptCostLogRepository(
            httpClient, new FakeHttpClientFactory(), NullLogger<HttpChatGptCostLogRepository>.Instance);
    }

    private sealed class FakeHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }
}
