using Meterist.Core.Vendors;
using Meterist.Vendors.ClaudeApiPlatform;
using Microsoft.Extensions.Logging.Abstractions;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Meterist.Vendors.Tests.ClaudeApiPlatform;

public class HttpClaudeCostReportRepositoryTests
{
    private const string AdminApiKey = "sk-ant-admin01-test-key";
    private const string CostReportPath = "/v1/organizations/cost_report";

    [Fact]
    public async Task QueryCostRowsAsync_ParsesCentsToDollars_AndSendsRequiredHeaders()
    {
        using var server = WireMockServer.Start();

        server
            .Given(Request.Create().WithPath(CostReportPath).UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody("""
                    {
                      "data": [
                        {
                          "starting_at": "2026-07-20T00:00:00Z",
                          "ending_at": "2026-07-21T00:00:00Z",
                          "results": [
                            {
                              "amount": "12345",
                              "context_window": "0-200k",
                              "cost_type": "tokens",
                              "currency": "USD",
                              "description": "Claude Sonnet 5 Usage - Input Tokens",
                              "inference_geo": "global",
                              "model": "claude-sonnet-5",
                              "service_tier": "standard",
                              "token_type": "uncached_input_tokens",
                              "workspace_id": "wrkspc_01JwQvzr7rXLA5AGx3HKfFUJ"
                            }
                          ]
                        }
                      ],
                      "has_more": false,
                      "next_page": null
                    }
                    """));

        var period = new DateRange(new DateOnly(2026, 7, 20), new DateOnly(2026, 7, 20));
        var repository = CreateRepository(server);
        var rows = await repository.QueryCostRowsAsync(AdminApiKey, period, TestContext.Current.CancellationToken);

        var row = Assert.Single(rows);
        Assert.Equal(new DateOnly(2026, 7, 20), row[ClaudeApiCostRowFields.RecordDate]);
        // "12345" cents == $123.45 — the load-bearing conversion this vendor's schema needs.
        Assert.Equal(123.45m, row[ClaudeApiCostRowFields.AmountUsd]);
        Assert.Equal("claude-sonnet-5", row[ClaudeApiCostRowFields.Model]);
        Assert.Equal("wrkspc_01JwQvzr7rXLA5AGx3HKfFUJ", row[ClaudeApiCostRowFields.WorkspaceId]);

        var entry = server.LogEntries.First(e => e.RequestMessage?.Path == CostReportPath);
        var headers = entry.RequestMessage?.Headers;
        Assert.NotNull(headers);
        Assert.Equal("2023-06-01", headers!["anthropic-version"].FirstOrDefault());
        Assert.Equal(AdminApiKey, headers["x-api-key"].FirstOrDefault());
    }

    [Fact]
    public async Task QueryCostRowsAsync_PaginatesUntilHasMoreFalse()
    {
        using var server = WireMockServer.Start();

        server
            .Given(Request.Create().WithPath(CostReportPath).UsingGet())
            .InScenario("cost-report-paging")
            .WillSetStateTo("page-2")
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody("""
                    {
                      "data": [
                        {
                          "starting_at": "2026-07-20T00:00:00Z",
                          "ending_at": "2026-07-21T00:00:00Z",
                          "results": [{"amount": "100", "description": "a", "model": "m", "token_type": "t", "cost_type": "tokens", "service_tier": "standard", "workspace_id": "w1"}]
                        }
                      ],
                      "has_more": true,
                      "next_page": "page-2-token"
                    }
                    """));

        server
            .Given(Request.Create().WithPath(CostReportPath).UsingGet())
            .InScenario("cost-report-paging")
            .WhenStateIs("page-2")
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody("""
                    {
                      "data": [
                        {
                          "starting_at": "2026-07-21T00:00:00Z",
                          "ending_at": "2026-07-22T00:00:00Z",
                          "results": [{"amount": "200", "description": "b", "model": "m", "token_type": "t", "cost_type": "tokens", "service_tier": "standard", "workspace_id": "w1"}]
                        }
                      ],
                      "has_more": false,
                      "next_page": null
                    }
                    """));

        var period = new DateRange(new DateOnly(2026, 7, 20), new DateOnly(2026, 7, 21));
        var repository = CreateRepository(server);
        var rows = await repository.QueryCostRowsAsync(AdminApiKey, period, TestContext.Current.CancellationToken);

        Assert.Equal(2, rows.Count);
        Assert.Contains(rows, r => (decimal)r[ClaudeApiCostRowFields.AmountUsd]! == 1.00m);
        Assert.Contains(rows, r => (decimal)r[ClaudeApiCostRowFields.AmountUsd]! == 2.00m);
    }

    private static HttpClaudeCostReportRepository CreateRepository(WireMockServer server)
    {
        var httpClient = new HttpClient { BaseAddress = new Uri(server.Url + "/") };
        return new HttpClaudeCostReportRepository(httpClient, NullLogger<HttpClaudeCostReportRepository>.Instance);
    }
}
