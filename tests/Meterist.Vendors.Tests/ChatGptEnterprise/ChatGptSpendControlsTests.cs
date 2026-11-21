using System.Text.Json;
using Meterist.Core.Vendors;
using Meterist.Vendors.ChatGptEnterprise;
using Meterist.Vendors.Tests.GeminiEnterprise;
using Microsoft.Extensions.Logging.Abstractions;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Meterist.Vendors.Tests.ChatGptEnterprise;

/// <summary>
/// Response bodies follow the real <c>usage_limits/users</c> shape captured 2026-11-20 (<c>data</c>, <c>has_more</c>,
/// <c>cursor</c>; a row has usage as a number and as <c>{amount, unit}</c>, a null <c>last_month_usage</c>, and an
/// <c>effective_monthly_usage_limit</c> that is null for no limit or <c>{limit: {limit, type}, source: {kind, ...}}</c>),
/// with every identifier and figure replaced.
/// </summary>
public class ChatGptSpendControlsTests
{
    private const string Workspace = "11111111-2222-3333-4444-555555555555";
    private const string UsersPath = "/v1/manage/workspaces/" + Workspace + "/usage_limits/users";

    private static readonly ChatGptCredential Credential =
        new() { OrganizationId = "org-x", AdminApiKey = "the-admin-key", WorkspaceId = Workspace };

    private const string GroupLimit =
        """{ "limit": { "limit": 2000, "type": "limited" }, "source": { "kind": "group_default", "group_id": "grp_1", "group_name": "Engineering" } }""";

    private static string Row(string id, string? effective, string usage = "12.5", string unit = "credit", string last = "null") =>
        $$"""
        { "object": "workspace.user.usage_limit", "id": "{{id}}", "account_user_id": "acct_{{id}}", "name": "Name {{id}}",
          "email": "{{id}}@example.test", "role": "standard-user", "seat_type": "default", "is_scim_managed": false,
          "last_month_usage": {{last}}, "current_month_usage": {{usage}}, "current_month_usage_unit": "{{unit}}",
          "current_month_usage_amount": { "amount": {{usage}}, "unit": "{{unit}}" },
          "override_monthly_usage_limit": null, "effective_monthly_usage_limit": {{effective ?? "null"}}, "inherited_monthly_usage_limit": null }
        """;

    private static string Page(string rows, bool hasMore = false, string? cursor = null) =>
        $$"""{ "object": "list", "data": [ {{rows}} ], "has_more": {{(hasMore ? "true" : "false")}}, "cursor": {{(cursor is null ? "null" : $"\"{cursor}\"")}}, "last_id": "x" }""";

    [Fact]
    public async Task List_SendsTheBearerKeyAndLimit_AndMapsEveryField()
    {
        using var server = WireMockServer.Start();
        Respond(server, Page(Row("u1", GroupLimit, usage: "298.016105", last: "40.5")));

        var limit = Assert.Single(await CreateRepository(server).ListUserLimitsAsync(Credential, TestContext.Current.CancellationToken));

        Assert.Equal("u1", limit.MemberId);
        Assert.Equal("u1@example.test", limit.Email);
        Assert.Equal("Name u1", limit.Name);
        Assert.Equal(2000m, limit.LimitAmount);
        Assert.Equal(298.016105m, limit.PeriodToDateSpend);
        Assert.Equal(40.5m, limit.PriorPeriodSpend);
        Assert.Equal("credit", limit.Currency);
        Assert.Equal("monthly", limit.Period);
        Assert.Equal("group_default", limit.SourceType);
        Assert.Equal("Engineering", limit.SourceDetail);   // the readable group name

        var request = Assert.Single(server.LogEntries).RequestMessage!;
        Assert.Equal("Bearer the-admin-key", request.Headers!["Authorization"].Single());
        Assert.Contains("limit=500", request.Url!);
        Assert.DoesNotContain("cursor", request.Url!);
    }

    [Fact]
    public async Task List_ANullEffectiveLimit_MeansNoLimit_NotZero_AndMissingPriorUsageStaysNull()
    {
        using var server = WireMockServer.Start();
        Respond(server, Page(Row("u1", null, usage: "0")));

        var limit = Assert.Single(await CreateRepository(server).ListUserLimitsAsync(Credential, TestContext.Current.CancellationToken));

        Assert.Null(limit.LimitAmount);
        Assert.Null(limit.SourceType);
        Assert.Equal(0m, limit.PeriodToDateSpend);
        Assert.Null(limit.PriorPeriodSpend);   // unavailable is not zero
    }

    [Fact]
    public async Task List_AnUnlimitedLimit_IsNoLimit()
    {
        using var server = WireMockServer.Start();
        Respond(server, Page(Row("u1", """{ "limit": { "type": "unlimited" }, "source": { "kind": "workspace_default" } }""")));

        var limit = Assert.Single(await CreateRepository(server).ListUserLimitsAsync(Credential, TestContext.Current.CancellationToken));

        Assert.Null(limit.LimitAmount);
        Assert.Equal("workspace_default", limit.SourceType);
    }

    [Fact]
    public async Task List_ALimitAmountInUsd_IsUsedInPreferenceToTheOlderIntegerForm_WithItsUnit()
    {
        using var server = WireMockServer.Start();
        Respond(server, Page("""
            { "id": "u1", "current_month_usage": 3, "effective_monthly_usage_limit":
              { "limit": { "limit": 99, "type": "limited", "limit_amount": { "amount": 250.5, "unit": "usd" } },
                "source": { "kind": "individual_override" } } }
            """));

        var limit = Assert.Single(await CreateRepository(server).ListUserLimitsAsync(Credential, TestContext.Current.CancellationToken));

        Assert.Equal(250.5m, limit.LimitAmount);
        Assert.Equal("usd", limit.Currency);   // taken from the limit when the usage carries no unit
        Assert.Equal(3m, limit.PeriodToDateSpend);
        Assert.Null(limit.SourceDetail);   // an individual override has no group or seat
    }

    [Theory]
    [InlineData("""{ "kind": "role_based_personal_budget", "role_id": "role_1", "role_name": "Analysts" }""", "Analysts")]
    [InlineData("""{ "kind": "role_based_personal_budget", "seat_type": "codex" }""", "codex")]
    [InlineData("""{ "kind": "group_default", "group_id": "grp_9" }""", "grp_9")]
    public async Task List_TheSourceDetailIsTheMostReadableOriginAvailable(string source, string expected)
    {
        using var server = WireMockServer.Start();
        Respond(server, Page(Row("u1", $$"""{ "limit": { "limit": 10, "type": "limited" }, "source": {{source}} }""")));

        var limit = Assert.Single(await CreateRepository(server).ListUserLimitsAsync(Credential, TestContext.Current.CancellationToken));

        Assert.Equal(expected, limit.SourceDetail);
    }

    [Fact]
    public async Task List_FollowsTheCursorToTheEnd_SendingItBackAsCursor()
    {
        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath(UsersPath).UsingGet().WithParam("cursor", WireMock.Matchers.MatchBehaviour.RejectOnMatch))
            .RespondWith(Json(Page(Row("u1", null), hasMore: true, cursor: "cur/sor+1=")));
        server.Given(Request.Create().WithPath(UsersPath).UsingGet().WithParam("cursor", "cur/sor+1="))
            .RespondWith(Json(Page(Row("u2", null))));

        var limits = await CreateRepository(server).ListUserLimitsAsync(Credential, TestContext.Current.CancellationToken);

        Assert.Equal(["u1", "u2"], limits.Select(l => l.MemberId));
        Assert.Contains("limit=500", server.LogEntries.Last().RequestMessage!.Url!);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("\"  \"")]
    [InlineData("\"\"")]
    public async Task List_HasMoreWithNoUsableCursor_FailsWithoutAnotherRequest(string cursor)
    {
        using var server = WireMockServer.Start();
        Respond(server, "{ \"data\": [ " + Row("u1", null) + " ], \"has_more\": true, \"cursor\": " + cursor + " }");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CreateRepository(server).ListUserLimitsAsync(Credential, TestContext.Current.CancellationToken));

        Assert.Contains("partial", ex.Message);
        Assert.Single(server.LogEntries);
    }

    [Fact]
    public async Task List_ARepeatedCursor_FailsInsteadOfLooping()
    {
        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath(UsersPath).UsingGet().WithParam("cursor", WireMock.Matchers.MatchBehaviour.RejectOnMatch))
            .RespondWith(Json(Page(Row("u1", null), hasMore: true, cursor: "same")));
        server.Given(Request.Create().WithPath(UsersPath).UsingGet().WithParam("cursor", "same"))
            .RespondWith(Json(Page(Row("u2", null), hasMore: true, cursor: "same")));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CreateRepository(server).ListUserLimitsAsync(Credential, TestContext.Current.CancellationToken));

        Assert.Contains("already returned", ex.Message);
    }

    [Fact]
    public async Task List_ARowWithNoIdOrADuplicateMember_Fails()
    {
        using var server = WireMockServer.Start();
        Respond(server, Page("{ \"current_month_usage\": 1 }"));
        var noId = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CreateRepository(server).ListUserLimitsAsync(Credential, TestContext.Current.CancellationToken));
        Assert.Contains("no member id", noId.Message);

        using var second = WireMockServer.Start();
        Respond(second, Page(Row("u1", null) + "," + Row("u1", null)));
        var dup = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CreateRepository(second).ListUserLimitsAsync(Credential, TestContext.Current.CancellationToken));
        Assert.Contains("more than once", dup.Message);
    }

    [Theory]
    [InlineData("""{ "limit": { "type": "limited" }, "source": { "kind": "group_default" } }""", "no amount")]
    [InlineData("""{ "limit": { "type": "mystery", "limit": 5 }, "source": { "kind": "group_default" } }""", "unknown type")]
    [InlineData("""{ "source": { "kind": "group_default" } }""", "unknown type")]
    public async Task List_ALimitThatCannotBeRead_FailsInsteadOfBeingStoredAsNoLimit(string effective, string expected)
    {
        using var server = WireMockServer.Start();
        Respond(server, Page(Row("u1", effective)));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CreateRepository(server).ListUserLimitsAsync(Credential, TestContext.Current.CancellationToken));

        Assert.Contains(expected, ex.Message);
    }

    [Fact]
    public async Task List_ACredentialWithNoWorkspaceId_FailsBeforeAnyRequest()
    {
        using var server = WireMockServer.Start();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => CreateRepository(server).ListUserLimitsAsync(
            Credential with { WorkspaceId = null }, TestContext.Current.CancellationToken));

        Assert.Contains("WorkspaceId", ex.Message);
        Assert.Empty(server.LogEntries);
    }

    [Theory]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(404)]
    public async Task List_AnAuthFailure_SaysToCheckTheScopeAndWorkspace_WithoutTheKey(int status)
    {
        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath(UsersPath).UsingGet()).RespondWith(Response.Create().WithStatusCode(status).WithBody("{}"));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CreateRepository(server).ListUserLimitsAsync(Credential, TestContext.Current.CancellationToken));

        Assert.Contains("usage_limit.read", ex.Message);
        Assert.DoesNotContain("the-admin-key", ex.Message);
    }

    [Fact]
    public async Task List_EveryRequestIsMarkedSensitive_AndNothingPersonalIsLogged()
    {
        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath(UsersPath).UsingGet().WithParam("cursor", WireMock.Matchers.MatchBehaviour.RejectOnMatch))
            .RespondWith(Json(Page(Row("user_FIRST", GroupLimit, usage: "1234.5"), hasMore: true, cursor: "cursor_SECRET")));
        server.Given(Request.Create().WithPath(UsersPath).UsingGet().WithParam("cursor", "cursor_SECRET"))
            .RespondWith(Json(Page(Row("user_SECOND", null))));
        var logger = new CapturingLogger<HttpChatGptSpendControlsRepository>();
        var marks = new List<bool>();
        var client = new HttpClient(new MarkRecordingHandler(marks)) { BaseAddress = new Uri(server.Url + "/") };

        await new HttpChatGptSpendControlsRepository(client, logger).ListUserLimitsAsync(Credential, TestContext.Current.CancellationToken);

        Assert.Equal([true, true], marks);   // the cursor request included
        Assert.NotEmpty(logger.Entries);
        var everything = string.Join("\n", logger.Entries.Select(e => e.Message));
        foreach (var secret in new[] { "cursor_SECRET", "user_FIRST", "user_SECOND", "example.test", "Name user", "Engineering", "1234", "the-admin-key", "cursor=" })
        {
            Assert.DoesNotContain(secret, everything);
        }
    }

    [Fact]
    public async Task Source_UsesTheStoredCredential()
    {
        var store = new FakeSecretStore();
        await store.SetCredentialAsync("t1", VendorCatalog.ChatGptEnterprise.Id, JsonSerializer.Serialize(Credential), TestContext.Current.CancellationToken);
        var repository = new RecordingRepository([new VendorSpendLimit { MemberId = "u1" }]);
        var source = new ChatGptEnterpriseSpendLimitSource(store, repository);

        var limits = await source.FetchLimitsAsync("t1", TestContext.Current.CancellationToken);

        Assert.Equal("u1", Assert.Single(limits).MemberId);
        Assert.Equal(Workspace, repository.Received!.WorkspaceId);
        Assert.Equal(VendorCatalog.ChatGptEnterprise.Id, source.VendorId);
    }

    [Fact]
    public async Task Source_WithNoStoredCredential_FailsLoudly_AndNeverCallsTheApi()
    {
        var repository = new RecordingRepository([]);
        var source = new ChatGptEnterpriseSpendLimitSource(new FakeSecretStore(), repository);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => source.FetchLimitsAsync("t1", TestContext.Current.CancellationToken));

        Assert.Contains("No ChatGPT Enterprise credential", ex.Message);
        Assert.Null(repository.Received);
    }

    private static void Respond(WireMockServer server, string body) =>
        server.Given(Request.Create().WithPath(UsersPath).UsingGet()).RespondWith(Json(body));

    private static IResponseBuilder Json(string body) =>
        Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/json").WithBody(body);

    private static HttpChatGptSpendControlsRepository CreateRepository(WireMockServer server) =>
        new(new HttpClient { BaseAddress = new Uri(server.Url + "/") }, NullLogger<HttpChatGptSpendControlsRepository>.Instance);

    private sealed class MarkRecordingHandler(List<bool> marks) : DelegatingHandler(new HttpClientHandler())
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            marks.Add(Meterist.Core.Http.SensitiveRequest.IsMarked(request));
            return base.SendAsync(request, cancellationToken);
        }
    }

    private sealed class RecordingRepository(IReadOnlyList<VendorSpendLimit> limits) : IChatGptSpendControlsRepository
    {
        public ChatGptCredential? Received { get; private set; }

        public Task<IReadOnlyList<VendorSpendLimit>> ListUserLimitsAsync(ChatGptCredential credential, CancellationToken cancellationToken = default)
        {
            Received = credential;
            return Task.FromResult(limits);
        }
    }
}
