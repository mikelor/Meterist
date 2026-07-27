using Meterist.Core.Vendors;
using Meterist.Vendors.ClaudeEnterprise;
using Microsoft.Extensions.Logging.Abstractions;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Meterist.Vendors.Tests.ClaudeEnterprise;

public class HttpClaudeAnalyticsCostReportRepositoryTests
{
    private const string AnalyticsApiKey = "sk-ant-analytics-test-key";
    private const string UserCostReportPath = "/v1/organizations/analytics/user_cost_report";

    [Fact]
    public async Task QueryUserCostRowsAsync_ParsesCentsToDollarsAndNonMidnightTimestamps_AndSendsRequiredHeaders()
    {
        using var server = WireMockServer.Start();

        server
            .Given(Request.Create().WithPath(UserCostReportPath).UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody("""
                    {
                      "data": [
                        {
                          "actor": {"user_id": "user_01Abc", "email": "jane@example.com", "name": "Jane Smith", "deleted": false, "type": "user_actor"},
                          "amount": "12345",
                          "list_amount": "15000",
                          "currency": "USD",
                          "model": "claude-sonnet-5",
                          "product": "chat",
                          "starting_at": "2026-07-20T05:30:00Z",
                          "ending_at": "2026-07-21T05:30:00Z"
                        }
                      ],
                      "has_more": false,
                      "next_page": null,
                      "organization_id": "org_test"
                    }
                    """));

        var period = new DateRange(new DateOnly(2026, 7, 20), new DateOnly(2026, 7, 20));
        var repository = CreateRepository(server);
        var rows = await repository.QueryUserCostRowsAsync(
            AnalyticsApiKey, period, TestContext.Current.CancellationToken);

        var row = Assert.Single(rows);
        // Non-midnight time component ("T05:30:00Z") must still resolve to the
        // correct calendar date — the exact DateOnly.TryParse pitfall found
        // while building the Claude API Platform adapter.
        Assert.Equal(new DateOnly(2026, 7, 20), row[ClaudeAnalyticsCostRowFields.RecordDate]);
        // "12345" cents == $123.45
        Assert.Equal(123.45m, row[ClaudeAnalyticsCostRowFields.AmountUsd]);
        Assert.Equal("user_01Abc", row[ClaudeAnalyticsCostRowFields.UserId]);
        Assert.Equal("jane@example.com", row[ClaudeAnalyticsCostRowFields.UserEmail]);
        Assert.Equal("Jane Smith", row[ClaudeAnalyticsCostRowFields.UserName]);

        var entry = server.LogEntries.First(e => e.RequestMessage?.Path == UserCostReportPath);
        var headers = entry.RequestMessage?.Headers;
        Assert.NotNull(headers);
        Assert.Equal("2023-06-01", headers!["anthropic-version"].FirstOrDefault());
        Assert.Equal(AnalyticsApiKey, headers["x-api-key"].FirstOrDefault());
    }

    [Fact]
    public async Task QueryUserCostRowsAsync_PaginatesWithinASingleWindow()
    {
        using var server = WireMockServer.Start();

        server
            .Given(Request.Create().WithPath(UserCostReportPath).UsingGet())
            .InScenario("paging")
            .WillSetStateTo("page-2")
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody("""
                    {
                      "data": [{"actor": {"user_id": "u1", "email": null, "name": "A", "deleted": false, "type": "user_actor"}, "amount": "100", "starting_at": "2026-07-20T00:00:00Z"}],
                      "has_more": true,
                      "next_page": "page-2-token"
                    }
                    """));

        server
            .Given(Request.Create().WithPath(UserCostReportPath).UsingGet())
            .InScenario("paging")
            .WhenStateIs("page-2")
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody("""
                    {
                      "data": [{"actor": {"user_id": "u2", "email": null, "name": "B", "deleted": false, "type": "user_actor"}, "amount": "200", "starting_at": "2026-07-20T00:00:00Z"}],
                      "has_more": false,
                      "next_page": null
                    }
                    """));

        var period = new DateRange(new DateOnly(2026, 7, 20), new DateOnly(2026, 7, 20));
        var repository = CreateRepository(server);
        var rows = await repository.QueryUserCostRowsAsync(
            AnalyticsApiKey, period, TestContext.Current.CancellationToken);

        Assert.Equal(2, rows.Count);
        Assert.Contains(rows, r => (string?)r[ClaudeAnalyticsCostRowFields.UserId] == "u1");
        Assert.Contains(rows, r => (string?)r[ClaudeAnalyticsCostRowFields.UserId] == "u2");
    }

    [Fact]
    public async Task QueryUserCostRowsAsync_ChunksPeriodLongerThan31DaysIntoMultipleWindows()
    {
        using var server = WireMockServer.Start();

        server
            .Given(Request.Create().WithPath(UserCostReportPath).UsingGet())
            .InScenario("windows")
            .WillSetStateTo("window-2")
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody("""
                    {
                      "data": [{"actor": {"user_id": "u1", "email": null, "name": "A", "deleted": false, "type": "user_actor"}, "amount": "100", "starting_at": "2026-06-01T00:00:00Z"}],
                      "has_more": false,
                      "next_page": null
                    }
                    """));

        server
            .Given(Request.Create().WithPath(UserCostReportPath).UsingGet())
            .InScenario("windows")
            .WhenStateIs("window-2")
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody("""
                    {
                      "data": [{"actor": {"user_id": "u2", "email": null, "name": "B", "deleted": false, "type": "user_actor"}, "amount": "200", "starting_at": "2026-07-02T00:00:00Z"}],
                      "has_more": false,
                      "next_page": null
                    }
                    """));

        // 35 days: 2026-06-01 to 2026-07-05 inclusive — exceeds the 31-day
        // single-query cap, forcing exactly two outer-loop windows.
        var period = new DateRange(new DateOnly(2026, 6, 1), new DateOnly(2026, 7, 5));
        var repository = CreateRepository(server);
        var rows = await repository.QueryUserCostRowsAsync(
            AnalyticsApiKey, period, TestContext.Current.CancellationToken);

        // Both windows' rows must be present — if chunking didn't happen, only
        // the first window's single row would come back.
        Assert.Equal(2, rows.Count);
        Assert.Contains(rows, r => (string?)r[ClaudeAnalyticsCostRowFields.UserId] == "u1");
        Assert.Contains(rows, r => (string?)r[ClaudeAnalyticsCostRowFields.UserId] == "u2");

        var requestUrls = server.LogEntries
            .Where(e => e.RequestMessage?.Path == UserCostReportPath)
            .Select(e => e.RequestMessage!.Url)
            .ToList();

        Assert.Equal(2, requestUrls.Count);
        Assert.Contains(requestUrls, url => url!.Contains("starting_at=2026-06-01"));
        Assert.Contains(requestUrls, url => url!.Contains("starting_at=2026-07-02"));
    }

    private static HttpClaudeAnalyticsCostReportRepository CreateRepository(WireMockServer server)
    {
        var httpClient = new HttpClient { BaseAddress = new Uri(server.Url + "/") };
        return new HttpClaudeAnalyticsCostReportRepository(
            httpClient, NullLogger<HttpClaudeAnalyticsCostReportRepository>.Instance);
    }
}
