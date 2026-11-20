using System.Net.Http;
using Meterist.Core.Vendors;
using Meterist.Vendors.ChatGptEnterprise;
using Microsoft.Extensions.Logging.Abstractions;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Meterist.Vendors.Tests.ChatGptEnterprise;

/// <summary>
/// Response bodies follow the real Daily Usage API shape captured 2026-11-19
/// (workspace rows have null user_id/actor; per-user rows carry both; credit-only
/// rows have cost_usd null), with every identifier and figure replaced.
/// </summary>
public class HttpChatGptDailyUsageRepositoryTests
{
    private const string WorkspaceId = "11111111-2222-3333-4444-555555555555";
    private const string AdminApiKey = "test-admin-key";
    private static readonly string UsagePath = $"/v1/analytics/workspaces/{WorkspaceId}/usage";

    // 2026-10-01 and 2026-10-02 at UTC midnight, and 2026-10-03 (the exclusive end).
    private const long Oct1 = 1790812800;
    private const long Oct2 = 1790899200;
    private const long Oct3 = 1790985600;

    private static ChatGptCredential Credential(string? workspaceId = WorkspaceId) => new()
    {
        OrganizationId = "org-test",
        AdminApiKey = AdminApiKey,
        WorkspaceId = workspaceId,
    };

    private static readonly DateRange TwoDays = new(new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 2));

    [Fact]
    public async Task QueryDailyUsageAsync_Workspace_SendsUtcBoundsAndGroupAndMapsTotals()
    {
        using var server = WireMockServer.Start();
        Respond(server, $$"""
            {
              "object": "page",
              "data": [
                {
                  "object": "workspace.usage.result", "start_time": {{Oct1}}, "end_time": {{Oct2}},
                  "user_id": null, "actor": null,
                  "totals": {
                    "chats": 125, "messages": 217, "credits": 4193.087416, "cost_usd": null,
                    "estimated_cost_usd": 167.72,
                    "uncached_text_input_tokens": 20213547, "cached_text_input_tokens": 224591104,
                    "text_output_tokens": 1431426, "text_total_tokens": 246236077
                  },
                  "clients": []
                }
              ],
              "has_more": false, "next_page": null
            }
            """);

        var rows = await CreateRepository(server)
            .QueryDailyUsageAsync(Credential(), TwoDays, ChatGptUsageGrouping.Workspace, TestContext.Current.CancellationToken);

        var row = Assert.Single(rows);
        Assert.Equal(new DateOnly(2026, 10, 1), row.Day);
        Assert.Null(row.UserId);
        Assert.Null(row.Email);
        Assert.Equal(125, row.Chats);
        Assert.Equal(217, row.Messages);
        Assert.Equal(4193.087416m, row.Credits);
        Assert.Null(row.CostUsd);
        Assert.Equal(167.72m, row.EstimatedCostUsd);
        Assert.Equal(20213547L, row.UncachedTextInputTokens);
        Assert.Equal(224591104L, row.CachedTextInputTokens);
        Assert.Equal(1431426L, row.TextOutputTokens);
        Assert.Equal(246236077L, row.TextTotalTokens);

        var request = Assert.Single(server.LogEntries).RequestMessage!;
        Assert.Equal(UsagePath, request.Path);
        Assert.Equal(Oct1.ToString(), Query(request.Query, "start_time"));
        // The inclusive period end (Oct 2) becomes the exclusive next midnight (Oct 3).
        Assert.Equal(Oct3.ToString(), Query(request.Query, "end_time"));
        Assert.Equal("workspace", Query(request.Query, "group"));
        Assert.Equal("1000", Query(request.Query, "limit"));
        Assert.Equal($"Bearer {AdminApiKey}", request.Headers!["Authorization"].Single());
    }

    [Fact]
    public async Task QueryDailyUsageAsync_PerUser_OmitsGroupAndMapsIdentity()
    {
        using var server = WireMockServer.Start();
        Respond(server, $$"""
            {
              "object": "page",
              "data": [
                {
                  "object": "workspace.usage.result", "start_time": {{Oct1}}, "end_time": {{Oct2}},
                  "user_id": "user-aaa",
                  "actor": { "type": "user", "user_id": "user-aaa", "email": "person@example.com" },
                  "totals": { "chats": 0, "messages": 1, "credits": 109.3115, "cost_usd": null, "estimated_cost_usd": 4.37 },
                  "clients": [], "models": []
                }
              ],
              "has_more": false, "next_page": null
            }
            """);

        var rows = await CreateRepository(server)
            .QueryDailyUsageAsync(Credential(), TwoDays, ChatGptUsageGrouping.PerUser, TestContext.Current.CancellationToken);

        var row = Assert.Single(rows);
        Assert.Equal("user-aaa", row.UserId);
        Assert.Equal("person@example.com", row.Email);
        Assert.Equal(109.3115m, row.Credits);

        var request = Assert.Single(server.LogEntries).RequestMessage!;
        Assert.Null(Query(request.Query, "group"));
    }

    [Fact]
    public async Task QueryDailyUsageAsync_PerUser_KeepsActorTypeAndTheClientAndModelBreakdowns()
    {
        using var server = WireMockServer.Start();
        Respond(server, $$"""
            {
              "object": "page",
              "data": [
                {
                  "start_time": {{Oct1}}, "end_time": {{Oct2}},
                  "user_id": "user-aaa",
                  "actor": { "type": "ACCOUNT_USER", "user_id": "user-aaa", "email": "person@example.com" },
                  "totals": { "chats": 1, "messages": 2, "credits": 10 },
                  "clients": [ { "client_id": "CHAT_WEB", "credits": 6 }, { "client_id": "CODEX_CLI", "credits": 4 } ],
                  "models": [ { "model": "gpt-x", "speed": "default", "credits": 10 } ]
                },
                {
                  "start_time": {{Oct1}}, "end_time": {{Oct2}}, "user_id": "user-bbb",
                  "actor": { "type": "ACCOUNT_USER", "email": "b@example.com" },
                  "totals": { "chats": 1, "messages": 1, "credits": 1 }
                }
              ],
              "has_more": false, "next_page": null
            }
            """);

        var rows = await CreateRepository(server)
            .QueryDailyUsageAsync(Credential(), TwoDays, ChatGptUsageGrouping.PerUser, TestContext.Current.CancellationToken);

        var first = rows[0];
        Assert.Equal("ACCOUNT_USER", first.ActorType);
        Assert.Equal(2, first.Clients!.Value.GetArrayLength());
        Assert.Equal("CODEX_CLI", first.Clients.Value[1].GetProperty("client_id").GetString());
        Assert.Equal("gpt-x", first.Models!.Value[0].GetProperty("model").GetString());

        // A row that omits the breakdowns gets null, not an exception or an empty placeholder.
        Assert.Null(rows[1].Clients);
        Assert.Null(rows[1].Models);
    }

    [Fact]
    public async Task QueryDailyUsageAsync_FollowsNextPageCursorAndPreservesOtherParameters()
    {
        using var server = WireMockServer.Start();

        server
            .Given(Request.Create().WithPath(UsagePath).UsingGet().WithParam("page", WireMock.Matchers.MatchBehaviour.RejectOnMatch))
            .RespondWith(Json($$"""
                {
                  "object": "page",
                  "data": [ { "start_time": {{Oct1}}, "user_id": null, "actor": null,
                              "totals": { "chats": 1, "messages": 1, "credits": 1.5 } } ],
                  "has_more": true, "next_page": "cursor/with+odd=chars"
                }
                """));

        server
            .Given(Request.Create().WithPath(UsagePath).UsingGet().WithParam("page", "cursor/with+odd=chars"))
            .RespondWith(Json($$"""
                {
                  "object": "page",
                  "data": [ { "start_time": {{Oct2}}, "user_id": null, "actor": null,
                              "totals": { "chats": 2, "messages": 2, "credits": 2.5 } } ],
                  "has_more": false, "next_page": null
                }
                """));

        var rows = await CreateRepository(server)
            .QueryDailyUsageAsync(Credential(), TwoDays, ChatGptUsageGrouping.Workspace, TestContext.Current.CancellationToken);

        Assert.Equal(2, rows.Count);
        Assert.Equal(new DateOnly(2026, 10, 1), rows[0].Day);
        Assert.Equal(new DateOnly(2026, 10, 2), rows[1].Day);

        Assert.Equal(2, server.LogEntries.Count());
        var second = server.LogEntries.Last().RequestMessage!;
        Assert.Equal("cursor/with+odd=chars", Query(second.Query, "page"));
        Assert.Equal(Oct1.ToString(), Query(second.Query, "start_time"));
        Assert.Equal(Oct3.ToString(), Query(second.Query, "end_time"));
        Assert.Equal("workspace", Query(second.Query, "group"));
    }

    [Fact]
    public async Task QueryDailyUsageAsync_KeepsNullsAndZerosDistinct()
    {
        using var server = WireMockServer.Start();
        // A USD-only day: credits null, native USD present, token fields omitted
        // entirely. A reported zero must stay zero, not become null.
        Respond(server, $$"""
            {
              "object": "page",
              "data": [
                { "start_time": {{Oct1}}, "user_id": null, "actor": null,
                  "totals": { "chats": 0, "messages": 0, "credits": null, "cost_usd": 12.5 } },
                { "start_time": {{Oct2}}, "user_id": null, "actor": null,
                  "totals": { "chats": 3, "messages": 4, "credits": 0, "cost_usd": null,
                              "estimated_cost_usd": 0, "text_output_tokens": 0 } }
              ],
              "has_more": false, "next_page": null
            }
            """);

        var rows = await CreateRepository(server)
            .QueryDailyUsageAsync(Credential(), TwoDays, ChatGptUsageGrouping.Workspace, TestContext.Current.CancellationToken);

        Assert.Null(rows[0].Credits);
        Assert.Equal(12.5m, rows[0].CostUsd);
        Assert.Null(rows[0].EstimatedCostUsd);
        Assert.Null(rows[0].TextTotalTokens);

        Assert.Equal(0m, rows[1].Credits);
        Assert.Null(rows[1].CostUsd);
        Assert.Equal(0m, rows[1].EstimatedCostUsd);
        Assert.Equal(0L, rows[1].TextOutputTokens);
        Assert.Null(rows[1].UncachedTextInputTokens);
    }

    [Fact]
    public async Task QueryDailyUsageAsync_EmptyPage_ReturnsNoRows()
    {
        using var server = WireMockServer.Start();
        Respond(server, """{ "object": "page", "data": [], "has_more": false, "next_page": null }""");

        var rows = await CreateRepository(server)
            .QueryDailyUsageAsync(Credential(), TwoDays, ChatGptUsageGrouping.Workspace, TestContext.Current.CancellationToken);

        Assert.Empty(rows);
    }

    [Fact]
    public async Task QueryDailyUsageAsync_HasMoreWithoutCursor_ThrowsRatherThanReturningAPartialResult()
    {
        using var server = WireMockServer.Start();
        Respond(server, $$"""
            { "object": "page",
              "data": [ { "start_time": {{Oct1}}, "totals": { "chats": 1, "messages": 1, "credits": 1 } } ],
              "has_more": true, "next_page": null }
            """);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => CreateRepository(server)
            .QueryDailyUsageAsync(Credential(), TwoDays, ChatGptUsageGrouping.Workspace, TestContext.Current.CancellationToken));

        Assert.Contains("next_page", ex.Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t")]
    public async Task QueryDailyUsageAsync_HasMoreWithBlankCursor_ThrowsWithoutSendingAnotherRequest(string cursor)
    {
        using var server = WireMockServer.Start();
        RespondInSequence(server, PageBody(Oct1, hasMore: true, nextPage: cursor));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => CreateRepository(server)
            .QueryDailyUsageAsync(Credential(), TwoDays, ChatGptUsageGrouping.Workspace, TestContext.Current.CancellationToken));

        Assert.Contains("blank", ex.Message);
        // The blank cursor must be rejected, not sent as an empty page parameter.
        Assert.Single(server.LogEntries);
    }

    [Fact]
    public async Task QueryDailyUsageAsync_RepeatedCursor_ThrowsOnTheRepeatNotAfterThePageCap()
    {
        using var server = WireMockServer.Start();
        // Page 1 hands out cursor "A"; the page fetched with "A" hands out "A" again.
        RespondInSequence(server,
            PageBody(Oct1, hasMore: true, nextPage: "A"),
            PageBody(Oct2, hasMore: true, nextPage: "A"));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => CreateRepository(server)
            .QueryDailyUsageAsync(Credential(), TwoDays, ChatGptUsageGrouping.Workspace, TestContext.Current.CancellationToken));

        Assert.Contains("already returned", ex.Message);
        Assert.Equal(2, server.LogEntries.Count());
    }

    [Fact]
    public async Task QueryDailyUsageAsync_CursorCycle_ThrowsWhenAnEarlierCursorComesBack()
    {
        using var server = WireMockServer.Start();
        // A -> B -> A: no two consecutive cursors are equal, so only remembering
        // every cursor seen (not just the last one) catches this.
        RespondInSequence(server,
            PageBody(Oct1, hasMore: true, nextPage: "A"),
            PageBody(Oct1, hasMore: true, nextPage: "B"),
            PageBody(Oct2, hasMore: true, nextPage: "A"));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => CreateRepository(server)
            .QueryDailyUsageAsync(Credential(), TwoDays, ChatGptUsageGrouping.Workspace, TestContext.Current.CancellationToken));

        Assert.Contains("already returned", ex.Message);
        Assert.Equal(3, server.LogEntries.Count());
    }

    [Fact]
    public async Task QueryDailyUsageAsync_DistinctCursorsAcrossManyPages_StillSucceeds()
    {
        using var server = WireMockServer.Start();
        RespondInSequence(server,
            PageBody(Oct1, hasMore: true, nextPage: "A"),
            PageBody(Oct1, hasMore: true, nextPage: "B"),
            PageBody(Oct2, hasMore: false, nextPage: null));

        var rows = await CreateRepository(server)
            .QueryDailyUsageAsync(Credential(), TwoDays, ChatGptUsageGrouping.Workspace, TestContext.Current.CancellationToken);

        Assert.Equal(3, rows.Count);
        Assert.Equal(3, server.LogEntries.Count());
    }

    // Serves the given bodies one per request, in order, via a WireMock scenario.
    private static void RespondInSequence(WireMockServer server, params string[] bodies)
    {
        for (var i = 0; i < bodies.Length; i++)
        {
            var mapping = server.Given(Request.Create().WithPath(UsagePath).UsingGet()).InScenario("pages");
            if (i > 0)
            {
                mapping = mapping.WhenStateIs($"page-{i}");
            }

            mapping.WillSetStateTo($"page-{i + 1}").RespondWith(Json(bodies[i]));
        }
    }

    private static string PageBody(long startTime, bool hasMore, string? nextPage) =>
        System.Text.Json.JsonSerializer.Serialize(new
        {
            @object = "page",
            data = new[]
            {
                new
                {
                    start_time = startTime,
                    user_id = (string?)null,
                    actor = (object?)null,
                    totals = new { chats = 1, messages = 1, credits = 1.5 },
                },
            },
            has_more = hasMore,
            next_page = nextPage,
        });

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task QueryDailyUsageAsync_WithoutWorkspaceId_ThrowsAndSendsNothing(string? workspaceId)
    {
        using var server = WireMockServer.Start();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => CreateRepository(server)
            .QueryDailyUsageAsync(Credential(workspaceId), TwoDays, ChatGptUsageGrouping.Workspace, TestContext.Current.CancellationToken));

        Assert.Contains("WorkspaceId", ex.Message);
        Assert.Empty(server.LogEntries);
    }

    [Theory]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(404)]
    public async Task QueryDailyUsageAsync_AuthOrNotFoundErrors_ExplainPermissionAndWorkspaceAndNeverEchoTheKey(int status)
    {
        using var server = WireMockServer.Start();
        server
            .Given(Request.Create().WithPath(UsagePath).UsingGet())
            .RespondWith(Response.Create().WithStatusCode(status).WithBody("""{"detail":"Forbidden"}"""));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => CreateRepository(server)
            .QueryDailyUsageAsync(Credential(), TwoDays, ChatGptUsageGrouping.Workspace, TestContext.Current.CancellationToken));

        Assert.Contains(status.ToString(), ex.Message);
        Assert.Contains("Enterprise analytics API", ex.Message);
        Assert.Contains("WorkspaceId", ex.Message);
        Assert.DoesNotContain(AdminApiKey, ex.Message);
    }

    [Fact]
    public async Task QueryDailyUsageAsync_OtherServerErrors_SurfaceAsHttpRequestException()
    {
        using var server = WireMockServer.Start();
        server
            .Given(Request.Create().WithPath(UsagePath).UsingGet())
            .RespondWith(Response.Create().WithStatusCode(500));

        await Assert.ThrowsAsync<HttpRequestException>(() => CreateRepository(server)
            .QueryDailyUsageAsync(Credential(), TwoDays, ChatGptUsageGrouping.Workspace, TestContext.Current.CancellationToken));
    }

    private static void Respond(WireMockServer server, string body) =>
        server.Given(Request.Create().WithPath(UsagePath).UsingGet()).RespondWith(Json(body));

    private static IResponseBuilder Json(string body) =>
        Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/json").WithBody(body);

    private static string? Query(IDictionary<string, WireMock.Types.WireMockList<string>>? query, string name) =>
        query is not null && query.TryGetValue(name, out var values) ? values.FirstOrDefault() : null;

    private static HttpChatGptDailyUsageRepository CreateRepository(WireMockServer server)
    {
        var handler = new HttpClientHandler { AllowAutoRedirect = false };
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri(server.Url + "/") };
        return new HttpChatGptDailyUsageRepository(httpClient, NullLogger<HttpChatGptDailyUsageRepository>.Instance);
    }
}
