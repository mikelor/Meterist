using Meterist.Core.Models;
using Meterist.Core.Vendors;
using Meterist.Vendors.ClaudeApiPlatform;
using Microsoft.Extensions.Logging.Abstractions;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Meterist.Vendors.Tests.ClaudeApiPlatform;

/// <summary>
/// The new /analytics/cost endpoint with an Admin key, and the per-actor rows built from it. Response bodies follow the
/// documented shape (a <c>group</c> object holding the requested dimensions; a <c>cost</c> object with cents-denominated
/// string amounts); every identifier and figure is invented.
/// </summary>
public class ClaudeApiAnalyticsTests
{
    private const string Key = "sk-ant-admin-test-key";
    private const string CostPath = "/v1/organizations/analytics/cost";
    private static readonly DateRange OneDay = new(new DateOnly(2026, 11, 3), new DateOnly(2026, 11, 3));
    private static readonly DateOnly Day = new(2026, 11, 3);

    [Fact]
    public async Task Query_SendsTheAdminKeyBetaHeaderAndGroupings_AndMapsEveryActorKind()
    {
        using var server = WireMockServer.Start();
        Respond(server, """
            {
              "data": [
                { "starting_at": "2026-11-03T00:00:00Z", "group": {
                    "actor": { "type": "user_actor", "user_id": "user_1", "email_address": "jane@example.test", "name": "Jane", "deleted": false },
                    "api_key_id": "apikey_a", "workspace_id": "wrkspc_1", "product": "messages_api" },
                  "cost": { "amount": "12345.000000", "requests": 7 } },
                { "starting_at": "2026-11-03T00:00:00Z", "group": {
                    "actor": { "type": "service_account_actor", "service_account_id": "svac_1" },
                    "api_key_id": "apikey_b", "workspace_id": "wrkspc_1", "product": "messages_api" },
                  "cost": { "amount": "500", "requests": 2 } },
                { "starting_at": "2026-11-03T00:00:00Z", "group": {
                    "actor": { "type": "api_actor", "api_key_id": "apikey_c" },
                    "api_key_id": "apikey_c", "workspace_id": "wrkspc_2", "product": "claude_console" },
                  "cost": { "amount": "100", "requests": null } },
                { "starting_at": "2026-11-03T00:00:00Z", "group": { "actor": null, "product": "other" },
                  "cost": { "amount": "50" } }
              ],
              "has_more": false, "next_page": null
            }
            """);

        var rows = await CreateRepository(server).QueryCostRowsAsync(Key, OneDay, TestContext.Current.CancellationToken);

        Assert.Equal(4, rows.Count);
        var person = rows[0];
        Assert.Equal(Day, person[ClaudeApiCostRowFields.RecordDate]);
        Assert.Equal(123.45m, person[ClaudeApiCostRowFields.AmountUsd]);   // 12345 cents
        Assert.Equal("user_1", person[ClaudeApiCostRowFields.UserId]);
        Assert.Equal("jane@example.test", person[ClaudeApiCostRowFields.UserEmail]);
        Assert.Equal("user_actor", person[ClaudeApiCostRowFields.ActorType]);
        Assert.Equal("apikey_a", person[ClaudeApiCostRowFields.ApiKeyId]);
        Assert.Equal("wrkspc_1", person[ClaudeApiCostRowFields.WorkspaceId]);
        Assert.Equal(7L, person[ClaudeApiCostRowFields.Requests]);
        Assert.Equal("svac_1", rows[1][ClaudeApiCostRowFields.UserId]);
        Assert.Equal("apikey_c", rows[2][ClaudeApiCostRowFields.UserId]);   // a workspace key is its own actor
        Assert.Null(rows[3][ClaudeApiCostRowFields.UserId]);
        Assert.Null(rows[3][ClaudeApiCostRowFields.ActorType]);

        var request = Assert.Single(server.LogEntries).RequestMessage!;
        Assert.Equal("2023-06-01", request.Headers!["anthropic-version"].Single());
        Assert.Equal("analytics-usage-cost-2026-08-24", request.Headers["anthropic-beta"].Single());
        Assert.Equal(Key, request.Headers["x-api-key"].Single());
        var url = request.Url!;
        Assert.Contains("ending_at=2026-11-04T00%3A00%3A00Z", url);
        Assert.Contains("time_bucket_width=1d", url);
        foreach (var dimension in new[] { "actor", "api_key_id", "workspace_id", "product" })
        {
            Assert.Contains($"group_by%5B%5D={dimension}", url);
        }

        Assert.DoesNotContain("actor_types", url);   // every actor, so the total counts all cost
    }

    [Fact]
    public async Task Query_FollowsPagesAndWindows_AndRefusesIncompleteResponses()
    {
        using var server = WireMockServer.Start();
        server
            .Given(Request.Create().WithPath(CostPath).UsingGet().WithParam("page", WireMock.Matchers.MatchBehaviour.RejectOnMatch))
            .RespondWith(Json("""
                { "data": [ { "starting_at": "2026-11-03T00:00:00Z", "group": { "product": "a" }, "cost": { "amount": "100" } } ],
                  "has_more": true, "next_page": "c1" }
                """));
        server
            .Given(Request.Create().WithPath(CostPath).UsingGet().WithParam("page", "c1"))
            .RespondWith(Json("""
                { "data": [ { "starting_at": "2026-11-03T00:00:00Z", "group": { "product": "b" }, "cost": { "amount": "200" } } ],
                  "has_more": false, "next_page": null }
                """));

        var rows = await CreateRepository(server).QueryCostRowsAsync(Key, OneDay, TestContext.Current.CancellationToken);
        Assert.Equal(["a", "b"], rows.Select(r => (string?)r[ClaudeApiCostRowFields.Product]));
        Assert.Contains("group_by%5B%5D=actor", server.LogEntries.Last().RequestMessage!.Url!);

        using var longServer = WireMockServer.Start();
        Respond(longServer, """{ "data": [], "has_more": false, "next_page": null }""");
        await CreateRepository(longServer).QueryCostRowsAsync(
            Key, new DateRange(new DateOnly(2026, 6, 1), new DateOnly(2026, 7, 5)), TestContext.Current.CancellationToken);
        Assert.Equal(2, longServer.LogEntries.Count());   // 31-day windows

        using var noData = WireMockServer.Start();
        Respond(noData, """{ "has_more": false }""");
        var missing = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CreateRepository(noData).QueryCostRowsAsync(Key, OneDay, TestContext.Current.CancellationToken));
        Assert.Contains("no data array", missing.Message);

        using var blank = WireMockServer.Start();
        Respond(blank, """{ "data": [], "has_more": true, "next_page": " " }""");
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CreateRepository(blank).QueryCostRowsAsync(Key, OneDay, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Query_A404NamesTheFallbackSetting_AndNeverTheKey()
    {
        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath(CostPath).UsingGet()).RespondWith(Response.Create().WithStatusCode(404));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CreateRepository(server).QueryCostRowsAsync(Key, OneDay, TestContext.Current.CancellationToken));

        Assert.Contains("Meterist:ClaudeApiPlatform:UseLegacyCostReport", ex.Message);
        Assert.DoesNotContain(Key, ex.Message);
    }

    [Fact]
    public void SpendNormalizer_StillSumsTheDay_FromTheNewRows()
    {
        var normalizer = new ClaudeApiPlatformSpendNormalizer(NullLogger<ClaudeApiPlatformSpendNormalizer>.Instance);

        var record = Assert.Single(normalizer.Normalize(
            Raw(Row("u1", "user_actor", 2m), Row("svac_1", "service_account_actor", 3m), Row(null, null, 0.5m)), []));

        Assert.Equal(5.5m, record.UsageOrOverage);
        Assert.Equal(0m, record.SeatFee);
    }

    [Fact]
    public void UserNormalizer_AttributesByActorType_AndKeepsAnUnattributedBucketSoTheTotalAddsUp()
    {
        var records = Normalizer().Normalize(
            Raw(
                Row("u1", "user_actor", 1m, requests: 3, email: "a@example.test", name: "Alex"),
                Row("u1", "user_actor", 2m, requests: 4),
                Row("svac_1", "service_account_actor", 4m),
                Row("apikey_c", "api_actor", 8m),
                Row("fed", "federated_actor", 16m),
                Row(null, null, 32m)),
            []);

        var byId = records.ToDictionary(r => r.UserId);
        Assert.Equal(3m, byId["u1"].Amount);
        Assert.Equal(7L, byId["u1"].Requests);
        Assert.Equal("a@example.test", byId["u1"].UserEmail);
        Assert.Equal(UsageAttribution.Person, byId["u1"].Attribution);
        Assert.Equal(UsageAttribution.ApiKey, byId["svac_1"].Attribution);
        Assert.Equal(UsageAttribution.ApiKey, byId["apikey_c"].Attribution);
        Assert.Equal(UsageAttribution.Unrecognized, byId["fed"].Attribution);
        Assert.Equal(UsageAttribution.Unrecognized, byId[ClaudeApiPlatformUserUsageNormalizer.UnattributedUserId].Attribution);
        Assert.DoesNotContain(records, r => r.IsEstimated);
        Assert.Equal(63m, records.Sum(r => r.Amount));   // per-actor figures add up to the daily total
    }

    [Fact]
    public void UserNormalizer_IgnoresLegacyRowsThatCarryNoActor()
    {
        var legacy = new Dictionary<string, object?>
        {
            [ClaudeApiCostRowFields.RecordDate] = Day,
            [ClaudeApiCostRowFields.AmountUsd] = 9m,
            [ClaudeApiCostRowFields.WorkspaceId] = "wrkspc_1",
        };

        Assert.Empty(Normalizer().Normalize(Raw(legacy), []));
    }

    private static ClaudeApiPlatformUserUsageNormalizer Normalizer() =>
        new(NullLogger<ClaudeApiPlatformUserUsageNormalizer>.Instance);

    private static RawVendorSpendData Raw(params IReadOnlyDictionary<string, object?>[] rows) => new()
    {
        VendorId = VendorCatalog.ClaudeApiPlatform.Id,
        TenantId = "t1",
        Period = new DateRange(Day, Day),
        RecordsByDate = new Dictionary<DateOnly, IReadOnlyList<IReadOnlyDictionary<string, object?>>> { [Day] = rows },
    };

    private static Dictionary<string, object?> Row(
        string? userId, string? actorType, decimal amount, long? requests = null, string? email = null, string? name = null) => new()
    {
        [ClaudeApiCostRowFields.RecordDate] = Day,
        [ClaudeApiCostRowFields.AmountUsd] = amount,
        [ClaudeApiCostRowFields.UserId] = userId,
        [ClaudeApiCostRowFields.UserEmail] = email,
        [ClaudeApiCostRowFields.UserName] = name,
        [ClaudeApiCostRowFields.Requests] = requests,
        [ClaudeApiCostRowFields.ActorType] = actorType,
    };

    private static void Respond(WireMockServer server, string body) =>
        server.Given(Request.Create().WithPath(CostPath).UsingGet()).RespondWith(Json(body));

    private static IResponseBuilder Json(string body) =>
        Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/json").WithBody(body);

    private static HttpClaudeApiAnalyticsCostRepository CreateRepository(WireMockServer server) =>
        new(new HttpClient { BaseAddress = new Uri(server.Url + "/") }, NullLogger<HttpClaudeApiAnalyticsCostRepository>.Instance);
}
