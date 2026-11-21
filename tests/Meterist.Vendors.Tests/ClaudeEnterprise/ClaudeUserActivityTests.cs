using Meterist.Core.Vendors;
using Meterist.Vendors.ClaudeEnterprise;
using Meterist.Vendors.Tests.ChatGptEnterprise;
using Meterist.Vendors.Tests.GeminiEnterprise;
using Microsoft.Extensions.Logging.Abstractions;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Meterist.Vendors.Tests.ClaudeEnterprise;

/// <summary>
/// Response bodies follow the real <c>/analytics/users</c> single-day shape captured 2026-11-21: <c>data</c> and
/// <c>next_page</c>, a row being <c>user</c> (<c>type</c>, <c>id</c>, <c>email_address</c>) beside nested per-product
/// metrics (<c>chat_metrics</c>, <c>claude_code_metrics.core_metrics</c>, <c>office_metrics.excel</c>, <c>cowork_metrics</c>
/// and so on) and <c>last_activity_date</c>, with every identifier and figure replaced.
/// </summary>
public class ClaudeUserActivityTests
{
    private const string Key = "sk-ant-test-key";
    private const string Path = "/v1/organizations/analytics/users";
    private static readonly DateOnly Day = new(2026, 11, 18);

    private static string Row(string id, int messages = 5, int sessions = 0, string? extraMetrics = null) =>
        $$"""
        { "user": { "type": "user", "id": "{{id}}", "email_address": "{{id}}@example.test" },
          "distinct_user_count": null,
          "chat_metrics": { "distinct_conversation_count": 2, "message_count": {{messages}}, "thinking_message_count": 1 },
          "claude_code_metrics": { "core_metrics": { "distinct_session_count": {{sessions}}, "commit_count": 3, "pull_request_count": 1,
              "lines_of_code": { "added_count": 120, "removed_count": 30 } } },
          "office_metrics": { "excel": { "distinct_session_count": 1, "message_count": 4 }, "word": { "distinct_session_count": 2, "message_count": 6 } },
          "cowork_metrics": { "distinct_session_count": 2, "action_count": 40, "message_count": 8 },
          "design_metrics": { "message_count": 3 }, "science_metrics": null,
          "web_search_count": 7, "last_activity_date": "2026-11-18"{{extraMetrics}} }
        """;

    // ---- the HTTP client

    [Fact]
    public async Task QueryDay_SendsTheKeyAndTheDate_NoBetaHeader_AndKeepsIdentityApartFromTheDetail()
    {
        using var server = WireMockServer.Start();
        Respond(server, $$"""{ "data": [ {{Row("user_1")}} ], "next_page": null }""");

        var rows = await CreateRepository(server).QueryDayAsync(Key, Day, TestContext.Current.CancellationToken);

        var row = Assert.Single(rows!);
        Assert.Equal("user_1", row.UserId);
        Assert.Equal("user_1@example.test", row.Email);
        Assert.Contains("\"message_count\":5", row.MetricsJson);
        Assert.DoesNotContain("email_address", row.MetricsJson);   // identity is not duplicated into the detail
        Assert.DoesNotContain("example.test", row.MetricsJson);

        var request = Assert.Single(server.LogEntries).RequestMessage!;
        Assert.Equal(Key, request.Headers!["x-api-key"].Single());
        Assert.Equal("2023-06-01", request.Headers["anthropic-version"].Single());
        Assert.False(request.Headers.ContainsKey("anthropic-beta"));
        Assert.Contains("date=2026-11-18", request.Url!);
        Assert.Contains("limit=1000", request.Url!);
    }

    [Fact]
    public async Task QueryDay_ADayThePublisherDoesNotHaveYet_IsNullNotAnError()
    {
        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath(Path).UsingGet()).RespondWith(Response.Create().WithStatusCode(400).WithBody(
            """{"type":"error","error":{"type":"invalid_request_error","message":"Latest available data for this query is 2026-11-20."}}"""));

        Assert.Null(await CreateRepository(server).QueryDayAsync(Key, new DateOnly(2026, 11, 21), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task QueryDay_ADayBeforeTheDataBegins_IsNull()
    {
        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath(Path).UsingGet()).RespondWith(Response.Create().WithStatusCode(400).WithBody(
            """{"type":"error","error":{"type":"invalid_request_error","message":"Data is only available from 2026-01-01 onwards."}}"""));

        Assert.Null(await CreateRepository(server).QueryDayAsync(Key, new DateOnly(2025, 12, 31), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task QueryDay_AnyOtherBadRequest_Throws()
    {
        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath(Path).UsingGet()).RespondWith(
            Response.Create().WithStatusCode(400).WithBody("""{"error":{"message":"something else"}}"""));

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            CreateRepository(server).QueryDayAsync(Key, Day, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(401)]
    [InlineData(403)]
    public async Task QueryDay_AnAuthFailure_SaysToCheckTheKeyAndScope_WithoutTheKey(int status)
    {
        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath(Path).UsingGet()).RespondWith(Response.Create().WithStatusCode(status).WithBody("{}"));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CreateRepository(server).QueryDayAsync(Key, Day, TestContext.Current.CancellationToken));

        Assert.Contains("read:analytics", ex.Message);
        Assert.DoesNotContain(Key, ex.Message);
    }

    [Fact]
    public async Task QueryDay_FollowsNextPageToTheEnd_RepeatingTheDate()
    {
        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath(Path).UsingGet().WithParam("page", WireMock.Matchers.MatchBehaviour.RejectOnMatch))
            .RespondWith(Json($$"""{ "data": [ {{Row("user_1")}} ], "next_page": "cur/sor+1=" }"""));
        server.Given(Request.Create().WithPath(Path).UsingGet().WithParam("page", "cur/sor+1="))
            .RespondWith(Json($$"""{ "data": [ {{Row("user_2")}} ], "next_page": null }"""));

        var rows = await CreateRepository(server).QueryDayAsync(Key, Day, TestContext.Current.CancellationToken);

        Assert.Equal(["user_1", "user_2"], rows!.Select(r => r.UserId));
        Assert.Contains("date=2026-11-18", server.LogEntries.Last().RequestMessage!.Url!);
    }

    [Theory]
    [InlineData("\" \"")]
    [InlineData("\"   \"")]
    public async Task QueryDay_AWhitespaceOnlyCursor_IsRejectedAtOnce_WithoutAnotherRequest(string cursor)
    {
        using var server = WireMockServer.Start();
        Respond(server, "{ \"data\": [ " + Row("user_1") + " ], \"next_page\": " + cursor + " }");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CreateRepository(server).QueryDayAsync(Key, Day, TestContext.Current.CancellationToken));

        Assert.Contains("blank next_page", ex.Message);
        Assert.Single(server.LogEntries);
    }

    [Fact]
    public async Task QueryDay_ARepeatedCursor_FailsInsteadOfLooping()
    {
        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath(Path).UsingGet().WithParam("page", WireMock.Matchers.MatchBehaviour.RejectOnMatch))
            .RespondWith(Json($$"""{ "data": [ {{Row("user_1")}} ], "next_page": "same" }"""));
        server.Given(Request.Create().WithPath(Path).UsingGet().WithParam("page", "same"))
            .RespondWith(Json($$"""{ "data": [ {{Row("user_2")}} ], "next_page": "same" }"""));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CreateRepository(server).QueryDayAsync(Key, Day, TestContext.Current.CancellationToken));

        Assert.Contains("already returned", ex.Message);
    }

    [Fact]
    public async Task QueryDay_APersonListedTwice_OrARowWithNoId_FailsInsteadOfReturningAnUnreliableDay()
    {
        using var server = WireMockServer.Start();
        Respond(server, $$"""{ "data": [ {{Row("user_1")}}, {{Row("user_1")}} ], "next_page": null }""");
        var twice = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CreateRepository(server).QueryDayAsync(Key, Day, TestContext.Current.CancellationToken));
        Assert.Contains("more than once", twice.Message);

        using var second = WireMockServer.Start();
        Respond(second, """{ "data": [ { "chat_metrics": { "message_count": 1 } } ], "next_page": null }""");
        var noId = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CreateRepository(second).QueryDayAsync(Key, Day, TestContext.Current.CancellationToken));
        Assert.Contains("no user id", noId.Message);
    }

    [Fact]
    public async Task QueryDay_NeverLogsAnEmailACursorOrAMemberId_AndMarksEveryRequestSensitive()
    {
        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath(Path).UsingGet().WithParam("page", WireMock.Matchers.MatchBehaviour.RejectOnMatch))
            .RespondWith(Json($$"""{ "data": [ {{Row("user_FIRST")}} ], "next_page": "eyJsYXN0X2VtYWlsIjoiY3Vyc29yLXNlY3JldEBleGFtcGxlLnRlc3QifQ" }"""));
        server.Given(Request.Create().WithPath(Path).UsingGet().WithParam("page", "eyJsYXN0X2VtYWlsIjoiY3Vyc29yLXNlY3JldEBleGFtcGxlLnRlc3QifQ"))
            .RespondWith(Json($$"""{ "data": [ {{Row("user_SECOND")}} ], "next_page": null }"""));
        var logger = new CapturingLogger<HttpClaudeUserActivityRepository>();
        var marks = new List<bool>();
        var client = new HttpClient(new MarkRecordingHandler(marks)) { BaseAddress = new Uri(server.Url + "/") };

        await new HttpClaudeUserActivityRepository(client, logger).QueryDayAsync(Key, Day, TestContext.Current.CancellationToken);

        Assert.Equal([true, true], marks);   // the cursor request included
        Assert.NotEmpty(logger.Entries);
        var everything = string.Join("\n", logger.Entries.Select(e => e.Message));
        foreach (var secret in new[] { "user_FIRST", "user_SECOND", "example.test", "eyJsYXN0", "page=", Key })
        {
            Assert.DoesNotContain(secret, everything);
        }
    }

    // ---- the source and its mapping

    [Fact]
    public void Map_TurnsEveryHeadlineCountIntoAColumn_SummingTheOfficeApps_AndKeepsTheDetail()
    {
        var row = new ClaudeUserActivityRow { UserId = "user_1", Email = "user_1@example.test", MetricsJson = MetricsOf(Row("user_1", messages: 5, sessions: 4)) };

        var r = ClaudeEnterpriseUserActivitySource.Map("t1", VendorCatalog.ClaudeEnterprise.Id, Day, row);

        Assert.Equal(("t1", Day, "user_1", "user_1@example.test"), (r.TenantId, r.Date, r.UserId, r.UserEmail));
        Assert.Equal((5L, 2L), (r.ChatMessages, r.ChatConversations));
        Assert.Equal((4L, 3L, 1L, 120L, 30L), (r.ClaudeCodeSessions, r.ClaudeCodeCommits, r.ClaudeCodePullRequests, r.ClaudeCodeLinesAdded, r.ClaudeCodeLinesRemoved));
        Assert.Equal((2L, 40L, 8L), (r.CoworkSessions, r.CoworkActions, r.CoworkMessages));
        Assert.Equal((3L, 10L), (r.OfficeSessions, r.OfficeMessages));   // excel 1+0+0, word 2 ; messages 4+6
        Assert.Equal((3L, 0L, 7L), (r.DesignMessages, r.ScienceMessages, r.WebSearches));   // science_metrics is null: zero, not an error
        Assert.Equal(row.MetricsJson, r.DetailJson);
        Assert.True(r.HasHeadlineActivity);
    }

    [Fact]
    public void Map_AProductTheCallerNeverTouched_IsZeroNotMissing()
    {
        var row = new ClaudeUserActivityRow { UserId = "u", MetricsJson = """{ "chat_metrics": { "message_count": 2 }, "last_activity_date": "2026-11-18" }""" };

        var r = ClaudeEnterpriseUserActivitySource.Map("t1", VendorCatalog.ClaudeEnterprise.Id, Day, row);

        Assert.Equal(2L, r.ChatMessages);
        Assert.Equal((0L, 0L, 0L, 0L), (r.ClaudeCodeSessions, r.CoworkSessions, r.OfficeMessages, r.WebSearches));
    }

    [Theory]
    [InlineData("{ \"chat_metrics\": { \"message_count\": 2.5 } }")]
    [InlineData("{ \"chat_metrics\": { \"message_count\": -1 } }")]
    [InlineData("{ \"chat_metrics\": { \"message_count\": \"many\" } }")]
    public void Map_ACountThatIsNotAWholeNonNegativeNumber_IsRefused_NotTruncated(string metrics)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => ClaudeEnterpriseUserActivitySource.Map(
            "t1", VendorCatalog.ClaudeEnterprise.Id, Day, new ClaudeUserActivityRow { UserId = "u", MetricsJson = metrics }));

        Assert.Contains("chat_metrics.message_count", ex.Message);
    }

    [Fact]
    public void Map_ARowThatIsOnlyANonHeadlineProduct_StillStoresItsDetail_WithoutHeadlineActivity()
    {
        var r = ClaudeEnterpriseUserActivitySource.Map("t1", VendorCatalog.ClaudeEnterprise.Id, Day,
            new ClaudeUserActivityRow { UserId = "u", MetricsJson = """{ "claude_tag_metrics": { "active_message_count": 4 } }""" });

        Assert.False(r.HasHeadlineActivity);
        Assert.Contains("claude_tag_metrics", r.DetailJson);
    }

    [Fact]
    public async Task Source_FetchesEachRequestedDay_SkippingDaysBeforeTheDataAndAfterYesterday_AndReportsWhatItCovered()
    {
        var store = new FakeSecretStore();
        await store.SetCredentialAsync("t1", VendorCatalog.ClaudeEnterprise.Id, Key, TestContext.Current.CancellationToken);
        var repository = new ScriptedRepository(new Dictionary<DateOnly, IReadOnlyList<ClaudeUserActivityRow>?>
        {
            [new DateOnly(2026, 11, 18)] = [new ClaudeUserActivityRow { UserId = "a", MetricsJson = """{ "chat_metrics": { "message_count": 1 } }""" }],
            [new DateOnly(2026, 11, 19)] = [],   // a covered day with nobody active
            [new DateOnly(2026, 11, 20)] = null, // the API does not have it yet
        });
        var source = new ClaudeEnterpriseUserActivitySource(store, repository, new FixedClock(new DateTimeOffset(2026, 11, 21, 6, 0, 0, TimeSpan.Zero)));

        var fetch = await source.FetchAsync("t1",
            [new DateOnly(2025, 12, 31), new DateOnly(2026, 11, 18), new DateOnly(2026, 11, 19), new DateOnly(2026, 11, 20), new DateOnly(2026, 11, 21), new DateOnly(2026, 11, 22)],
            TestContext.Current.CancellationToken);

        Assert.Equal([new DateOnly(2026, 11, 18), new DateOnly(2026, 11, 19), new DateOnly(2026, 11, 20)], repository.Asked);   // never 2025, today or later
        Assert.Equal([new DateOnly(2026, 11, 18), new DateOnly(2026, 11, 19)], fetch.CoveredDays);                              // the 20th was not looked at
        Assert.Equal("a", Assert.Single(fetch.Records).UserId);
        Assert.Equal(Key, repository.ReceivedKey);
        Assert.Equal(VendorCatalog.ClaudeEnterprise.Id, source.VendorId);
    }

    [Fact]
    public async Task Source_WithNoStoredCredential_FailsLoudly_AndNeverCallsTheApi()
    {
        var repository = new ScriptedRepository();
        var source = new ClaudeEnterpriseUserActivitySource(new FakeSecretStore(), repository);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => source.FetchAsync("t1", [Day], TestContext.Current.CancellationToken));

        Assert.Contains("No Claude Enterprise credential", ex.Message);
        Assert.Empty(repository.Asked);
    }

    // ---- fixtures

    private static string MetricsOf(string rowJson)
    {
        var node = System.Text.Json.Nodes.JsonNode.Parse(rowJson)!.AsObject();
        node.Remove("user");
        return node.ToJsonString();
    }

    private static void Respond(WireMockServer server, string body) =>
        server.Given(Request.Create().WithPath(Path).UsingGet()).RespondWith(Json(body));

    private static IResponseBuilder Json(string body) =>
        Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/json").WithBody(body);

    private static HttpClaudeUserActivityRepository CreateRepository(WireMockServer server) =>
        new(new HttpClient { BaseAddress = new Uri(server.Url + "/") }, NullLogger<HttpClaudeUserActivityRepository>.Instance);

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class MarkRecordingHandler(List<bool> marks) : DelegatingHandler(new HttpClientHandler())
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            marks.Add(Meterist.Core.Http.SensitiveRequest.IsMarked(request));
            return base.SendAsync(request, cancellationToken);
        }
    }

    private sealed class ScriptedRepository(params (DateOnly Day, IReadOnlyList<ClaudeUserActivityRow>? Rows)[] script) : IClaudeUserActivityRepository
    {
        public ScriptedRepository(Dictionary<DateOnly, IReadOnlyList<ClaudeUserActivityRow>?> days)
            : this(days.Select(kv => (kv.Key, kv.Value)).ToArray())
        {
        }

        public List<DateOnly> Asked { get; } = [];

        public string? ReceivedKey { get; private set; }

        public Task<IReadOnlyList<ClaudeUserActivityRow>?> QueryDayAsync(string apiKey, DateOnly day, CancellationToken cancellationToken = default)
        {
            ReceivedKey = apiKey;
            Asked.Add(day);
            return Task.FromResult(script.FirstOrDefault(s => s.Day == day).Rows);
        }
    }
}
