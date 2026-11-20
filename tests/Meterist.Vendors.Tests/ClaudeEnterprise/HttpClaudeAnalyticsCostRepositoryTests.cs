using Meterist.Core.Vendors;
using Meterist.Vendors.ClaudeEnterprise;
using Microsoft.Extensions.Logging.Abstractions;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Meterist.Vendors.Tests.ClaudeEnterprise;

/// <summary>
/// Response bodies follow the real /analytics/cost shape captured 2026-11-20 (a <c>group</c> object per
/// row holding the requested dimensions, a <c>cost</c> object with cents-denominated string amounts),
/// with every identifier and figure replaced.
/// </summary>
public class HttpClaudeAnalyticsCostRepositoryTests
{
    private const string Key = "sk-ant-analytics-test-key";
    private const string CostPath = "/v1/organizations/analytics/cost";

    private static readonly DateRange OneDay = new(new DateOnly(2026, 7, 20), new DateOnly(2026, 7, 20));

    [Fact]
    public async Task Query_SendsTheBetaHeaderAndRequiredParameters_AndMapsEveryField()
    {
        using var server = WireMockServer.Start();
        Respond(server, """
            {
              "data": [
                {
                  "starting_at": "2026-07-20T05:30:00Z", "ending_at": "2026-07-21T05:30:00Z",
                  "group": {
                    "actor": { "type": "user_actor", "user_id": "user_01Abc", "email_address": "jane@example.test",
                               "name": "Jane Example", "deleted": false },
                    "product": "claude_code", "model": null, "workspace_id": null
                  },
                  "cost": { "currency": "USD", "amount": "12345.000000", "list_amount": "15000.000000", "requests": 77 }
                }
              ],
              "has_more": false, "next_page": null, "data_refreshed_at": "2026-11-20T16:47:03Z"
            }
            """);

        var rows = await CreateRepository(server).QueryUserCostRowsAsync(Key, OneDay, TestContext.Current.CancellationToken);

        var row = Assert.Single(rows);
        // A non-midnight timestamp still resolves to its calendar date.
        Assert.Equal(new DateOnly(2026, 7, 20), row[ClaudeAnalyticsCostRowFields.RecordDate]);
        Assert.Equal(123.45m, row[ClaudeAnalyticsCostRowFields.AmountUsd]);   // 12345 cents
        Assert.Equal("user_01Abc", row[ClaudeAnalyticsCostRowFields.UserId]);
        Assert.Equal("jane@example.test", row[ClaudeAnalyticsCostRowFields.UserEmail]);   // email_address, not email
        Assert.Equal("Jane Example", row[ClaudeAnalyticsCostRowFields.UserName]);
        Assert.Equal("claude_code", row[ClaudeAnalyticsCostRowFields.Product]);
        Assert.Equal(77L, row[ClaudeAnalyticsCostRowFields.Requests]);
        Assert.Equal("user_actor", row[ClaudeAnalyticsCostRowFields.ActorType]);
        Assert.Equal(false, row[ClaudeAnalyticsCostRowFields.ActorDeleted]);

        var request = Assert.Single(server.LogEntries).RequestMessage!;
        Assert.Equal("2023-06-01", request.Headers!["anthropic-version"].Single());
        Assert.Equal("analytics-usage-cost-2026-08-24", request.Headers["anthropic-beta"].Single());
        Assert.Equal(Key, request.Headers["x-api-key"].Single());

        var url = request.Url!;
        Assert.Contains("starting_at=2026-07-20T00%3A00%3A00Z", url);
        Assert.Contains("ending_at=2026-07-21T00%3A00%3A00Z", url);   // explicit, exclusive end
        Assert.Contains("time_bucket_width=1d", url);
        Assert.Contains("group_by%5B%5D=actor", url);
        Assert.Contains("group_by%5B%5D=product", url);
        // Every actor is requested so the daily total counts all cost, not only people's.
        Assert.DoesNotContain("actor_types", url);
    }

    [Fact]
    public async Task Query_KeepsRowsWithNoActorOrRequests()
    {
        using var server = WireMockServer.Start();
        Respond(server, """
            {
              "data": [
                { "starting_at": "2026-07-20T00:00:00Z", "group": { "actor": null, "product": "chat" },
                  "cost": { "amount": "500", "requests": null } }
              ],
              "has_more": false, "next_page": null
            }
            """);

        var row = Assert.Single(
            await CreateRepository(server).QueryUserCostRowsAsync(Key, OneDay, TestContext.Current.CancellationToken));

        Assert.Equal(5m, row[ClaudeAnalyticsCostRowFields.AmountUsd]);
        Assert.Null(row[ClaudeAnalyticsCostRowFields.UserId]);
        Assert.Null(row[ClaudeAnalyticsCostRowFields.ActorType]);
        Assert.Null(row[ClaudeAnalyticsCostRowFields.Requests]);
    }

    [Fact]
    public async Task Query_FollowsTheCursor_RepeatingEveryOtherParameter()
    {
        using var server = WireMockServer.Start();
        server
            .Given(Request.Create().WithPath(CostPath).UsingGet().WithParam("page", WireMock.Matchers.MatchBehaviour.RejectOnMatch))
            .RespondWith(Json("""
                { "data": [ { "starting_at": "2026-07-20T00:00:00Z", "group": { "product": "chat" }, "cost": { "amount": "100" } } ],
                  "has_more": true, "next_page": "cursor/with+odd=chars" }
                """));
        server
            .Given(Request.Create().WithPath(CostPath).UsingGet().WithParam("page", "cursor/with+odd=chars"))
            .RespondWith(Json("""
                { "data": [ { "starting_at": "2026-07-20T00:00:00Z", "group": { "product": "cowork" }, "cost": { "amount": "200" } } ],
                  "has_more": false, "next_page": null }
                """));

        var rows = await CreateRepository(server).QueryUserCostRowsAsync(Key, OneDay, TestContext.Current.CancellationToken);

        Assert.Equal(["chat", "cowork"], rows.Select(r => (string?)r[ClaudeAnalyticsCostRowFields.Product]));
        var second = server.LogEntries.Last().RequestMessage!.Url!;
        Assert.Contains("group_by%5B%5D=actor", second);   // a cursor is bound to its query
        Assert.Contains("ending_at=2026-07-21", second);
    }

    [Fact]
    public async Task Query_ChunksALongPeriodIntoWindowsOfAtMost31Days_EachWithAnExplicitEnd()
    {
        using var server = WireMockServer.Start();
        Respond(server, """{ "data": [], "has_more": false, "next_page": null }""");

        // 35 days: 2026-06-01 to 2026-07-05 inclusive.
        await CreateRepository(server).QueryUserCostRowsAsync(
            Key, new DateRange(new DateOnly(2026, 6, 1), new DateOnly(2026, 7, 5)), TestContext.Current.CancellationToken);

        var urls = server.LogEntries.Select(e => e.RequestMessage!.Url!).ToList();
        Assert.Equal(2, urls.Count);
        Assert.Contains("starting_at=2026-06-01T00%3A00%3A00Z", urls[0]);
        Assert.Contains("ending_at=2026-07-02T00%3A00%3A00Z", urls[0]);   // 31 days later
        Assert.Contains("starting_at=2026-07-02T00%3A00%3A00Z", urls[1]);
        Assert.Contains("ending_at=2026-07-06T00%3A00%3A00Z", urls[1]);   // the day after the period end
    }

    [Fact]
    public async Task Query_ClampsAStartBeforeTheEarliestAllowedDate()
    {
        using var server = WireMockServer.Start();
        Respond(server, """{ "data": [], "has_more": false, "next_page": null }""");

        await CreateRepository(server).QueryUserCostRowsAsync(
            Key, new DateRange(new DateOnly(2025, 12, 20), new DateOnly(2026, 1, 3)), TestContext.Current.CancellationToken);

        Assert.Contains("starting_at=2026-01-01T00%3A00%3A00Z", server.LogEntries.First().RequestMessage!.Url!);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("\"   \"")]
    public async Task Query_HasMoreWithNoUsableCursor_FailsInsteadOfReturningAPartialMonth(string cursor)
    {
        using var server = WireMockServer.Start();
        Respond(server, $$"""
            { "data": [ { "starting_at": "2026-07-20T00:00:00Z", "group": {}, "cost": { "amount": "100" } } ],
              "has_more": true, "next_page": {{cursor}} }
            """);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CreateRepository(server).QueryUserCostRowsAsync(Key, OneDay, TestContext.Current.CancellationToken));

        Assert.Contains("partial", ex.Message);
        Assert.Single(server.LogEntries);   // no second request with an empty page parameter
    }

    [Fact]
    public async Task Query_ARepeatedCursor_FailsInsteadOfLooping()
    {
        using var server = WireMockServer.Start();
        Respond(server, """
            { "data": [ { "starting_at": "2026-07-20T00:00:00Z", "group": {}, "cost": { "amount": "100" } } ],
              "has_more": true, "next_page": "same" }
            """);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CreateRepository(server).QueryUserCostRowsAsync(Key, OneDay, TestContext.Current.CancellationToken));

        Assert.Contains("already returned", ex.Message);
        Assert.Equal(2, server.LogEntries.Count());
    }

    [Fact]
    public async Task Query_A404_ExplainsTheBetaAndNamesTheFallbackSetting()
    {
        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath(CostPath).UsingGet()).RespondWith(Response.Create().WithStatusCode(404));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CreateRepository(server).QueryUserCostRowsAsync(Key, OneDay, TestContext.Current.CancellationToken));

        Assert.Contains("beta", ex.Message);
        Assert.Contains("analytics-usage-cost-2026-08-24", ex.Message);
        Assert.Contains("Meterist:ClaudeEnterprise:UseLegacyCostReport", ex.Message);
        Assert.DoesNotContain(Key, ex.Message);
    }

    [Theory]
    [InlineData(401)]
    [InlineData(403)]
    public async Task Query_AnAuthFailure_NamesTheScope_WithoutLeakingTheKey(int status)
    {
        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath(CostPath).UsingGet()).RespondWith(Response.Create().WithStatusCode(status));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CreateRepository(server).QueryUserCostRowsAsync(Key, OneDay, TestContext.Current.CancellationToken));

        Assert.Contains(status.ToString(), ex.Message);
        Assert.Contains("read:analytics", ex.Message);
        Assert.DoesNotContain(Key, ex.Message);
    }

    private static void Respond(WireMockServer server, string body) =>
        server.Given(Request.Create().WithPath(CostPath).UsingGet()).RespondWith(Json(body));

    private static IResponseBuilder Json(string body) =>
        Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/json").WithBody(body);

    private static HttpClaudeAnalyticsCostRepository CreateRepository(WireMockServer server) =>
        new(new HttpClient { BaseAddress = new Uri(server.Url + "/") }, NullLogger<HttpClaudeAnalyticsCostRepository>.Instance);
}
