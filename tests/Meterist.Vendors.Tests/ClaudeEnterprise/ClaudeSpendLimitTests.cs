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
/// Response bodies follow the real <c>/spend_limits/effective</c> shape captured 2026-11-20 (<c>data</c> and
/// <c>next_page</c>; a row is scope, actor, amount, currency, period, source, spend_limit_id and
/// period_to_date_spend, with amounts as decimal strings in cents and a null amount for no limit), with every
/// identifier and figure replaced.
/// </summary>
public class ClaudeSpendLimitTests
{
    private const string Key = "sk-ant-test-key";
    private const string Path = "/v1/organizations/spend_limits/effective";

    private static string Row(
        string userId, string? amount, string spend, string source = """{ "type": "seat_tier", "seat_tier": "enterprise_usage_based" }""") =>
        $$"""
        { "scope": { "type": "user", "user_id": "{{userId}}" },
          "actor": { "type": "user_actor", "user_id": "{{userId}}", "name": "Name {{userId}}", "email_address": "{{userId}}@example.test", "deleted": false },
          "amount": {{(amount is null ? "null" : $"\"{amount}\"")}}, "currency": "USD", "period": "monthly",
          "source": {{source}}, "spend_limit_id": "spl_{{userId}}", "period_to_date_spend": "{{spend}}" }
        """;

    [Fact]
    public async Task List_SendsTheKey_NoBetaHeader_AndMapsEveryField_ConvertingCentsToDollars()
    {
        using var server = WireMockServer.Start();
        Respond(server, $$"""{ "data": [ {{Row("u1", "50000", "12345.67")}} ], "next_page": null }""");

        var limits = await CreateRepository(server).ListEffectiveLimitsAsync(Key, TestContext.Current.CancellationToken);

        var limit = Assert.Single(limits);
        Assert.Equal("u1", limit.MemberId);
        Assert.Equal("u1@example.test", limit.Email);
        Assert.Equal("Name u1", limit.Name);
        Assert.Equal(500m, limit.LimitAmount);              // 50000 cents
        Assert.Equal(123.4567m, limit.PeriodToDateSpend);   // cents too, not dollars
        Assert.Equal("USD", limit.Currency);
        Assert.Equal("monthly", limit.Period);
        Assert.Equal("seat_tier", limit.SourceType);
        Assert.Equal("enterprise_usage_based", limit.SourceDetail);
        Assert.Equal("spl_u1", limit.SpendLimitId);

        var request = Assert.Single(server.LogEntries).RequestMessage!;
        Assert.Equal(Key, request.Headers!["x-api-key"].Single());
        Assert.Equal("2023-06-01", request.Headers["anthropic-version"].Single());
        Assert.False(request.Headers.ContainsKey("anthropic-beta"));
        Assert.Contains("limit=1000", request.Url!);
    }

    [Fact]
    public async Task List_ANullAmount_MeansNoLimit_NotZero()
    {
        using var server = WireMockServer.Start();
        Respond(server, $$"""{ "data": [ {{Row("u1", null, "0")}} ], "next_page": null }""");

        var limit = Assert.Single(await CreateRepository(server).ListEffectiveLimitsAsync(Key, TestContext.Current.CancellationToken));

        Assert.Null(limit.LimitAmount);
        Assert.Equal(0m, limit.PeriodToDateSpend);
    }

    [Fact]
    public async Task List_TheSourceDetailIsTheGroupIdForAGroupLimit_AndNullForAPerMemberLimit()
    {
        using var server = WireMockServer.Start();
        Respond(server, $$"""
            { "data": [ {{Row("u1", "1000", "0", """{ "type": "rbac_group", "rbac_group_id": "grp_1" }""")}},
                        {{Row("u2", "1000", "0", """{ "type": "user", "user_id": "u2" }""")}} ], "next_page": null }
            """);

        var limits = await CreateRepository(server).ListEffectiveLimitsAsync(Key, TestContext.Current.CancellationToken);

        Assert.Equal(("rbac_group", "grp_1"), (limits[0].SourceType, limits[0].SourceDetail));
        Assert.Equal("user", limits[1].SourceType);
        Assert.Null(limits[1].SourceDetail);
    }

    [Fact]
    public async Task List_FollowsNextPageToTheEnd_RepeatingTheLimit()
    {
        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath(Path).UsingGet().WithParam("page", WireMock.Matchers.MatchBehaviour.RejectOnMatch))
            .RespondWith(Json($$"""{ "data": [ {{Row("u1", "1000", "0")}} ], "next_page": "cur/sor+1=" }"""));
        server.Given(Request.Create().WithPath(Path).UsingGet().WithParam("page", "cur/sor+1="))
            .RespondWith(Json($$"""{ "data": [ {{Row("u2", "1000", "0")}} ], "next_page": null }"""));

        var limits = await CreateRepository(server).ListEffectiveLimitsAsync(Key, TestContext.Current.CancellationToken);

        Assert.Equal(["u1", "u2"], limits.Select(l => l.MemberId));
        Assert.Contains("limit=1000", server.LogEntries.Last().RequestMessage!.Url!);
    }

    [Fact]
    public async Task List_ARepeatedCursor_FailsInsteadOfLooping()
    {
        using var server = WireMockServer.Start();
        Respond(server, $$"""{ "data": [ {{Row("u1", "1000", "0")}} ], "next_page": "same" }""");

        // The same cursor again returns the same member, which is rejected as a duplicate before the cursor check.
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CreateRepository(server).ListEffectiveLimitsAsync(Key, TestContext.Current.CancellationToken));

        Assert.Contains("more than once", ex.Message);
    }

    [Fact]
    public async Task List_ARepeatedCursorWithNewMembers_FailsInsteadOfLooping()
    {
        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath(Path).UsingGet().WithParam("page", WireMock.Matchers.MatchBehaviour.RejectOnMatch))
            .RespondWith(Json($$"""{ "data": [ {{Row("u1", "1", "0")}} ], "next_page": "same" }"""));
        server.Given(Request.Create().WithPath(Path).UsingGet().WithParam("page", "same"))
            .RespondWith(Json($$"""{ "data": [ {{Row("u2", "1", "0")}} ], "next_page": "same" }"""));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CreateRepository(server).ListEffectiveLimitsAsync(Key, TestContext.Current.CancellationToken));

        Assert.Contains("already returned", ex.Message);
    }

    [Theory]
    [InlineData("\" \"")]
    [InlineData("\"   \"")]
    [InlineData("\"\\u0009\"")]
    public async Task List_AWhitespaceOnlyCursor_IsRejectedAtOnce_WithoutAnotherRequest(string cursor)
    {
        using var server = WireMockServer.Start();
        Respond(server, "{ \"data\": [ " + Row("u1", "1", "0") + " ], \"next_page\": " + cursor + " }");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CreateRepository(server).ListEffectiveLimitsAsync(Key, TestContext.Current.CancellationToken));

        Assert.Contains("blank next_page", ex.Message);
        Assert.Single(server.LogEntries);
    }

    [Fact]
    public async Task List_AnEmptyStringCursor_IsTheEndOfTheList()
    {
        using var server = WireMockServer.Start();
        Respond(server, "{ \"data\": [ " + Row("u1", "1", "0") + " ], \"next_page\": \"\" }");

        Assert.Single(await CreateRepository(server).ListEffectiveLimitsAsync(Key, TestContext.Current.CancellationToken));
        Assert.Single(server.LogEntries);
    }

    [Fact]
    public async Task List_ARowWithNoMemberId_FailsInsteadOfBeingSkipped()
    {
        using var server = WireMockServer.Start();
        Respond(server, """{ "data": [ { "amount": "100", "currency": "USD" } ], "next_page": null }""");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CreateRepository(server).ListEffectiveLimitsAsync(Key, TestContext.Current.CancellationToken));

        Assert.Contains("no member id", ex.Message);
    }

    [Theory]
    [InlineData("""{ "data": [ { "actor": { "user_id": "u1" }, "amount": "abc" } ], "next_page": null }""", "amount")]
    [InlineData("""{ "data": [ { "actor": { "user_id": "u1" }, "period_to_date_spend": "1,5" } ], "next_page": null }""", "period_to_date_spend")]
    public async Task List_AFigureThatIsNotANumber_FailsNamingTheField(string body, string field)
    {
        using var server = WireMockServer.Start();
        Respond(server, body);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CreateRepository(server).ListEffectiveLimitsAsync(Key, TestContext.Current.CancellationToken));

        Assert.Contains(field, ex.Message);
    }

    [Theory]
    [InlineData(401)]
    [InlineData(403)]
    public async Task List_AnAuthFailure_SaysToCheckTheScopeAndUsageCredits(int status)
    {
        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath(Path).UsingGet()).RespondWith(Response.Create().WithStatusCode(status).WithBody("{}"));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CreateRepository(server).ListEffectiveLimitsAsync(Key, TestContext.Current.CancellationToken));

        Assert.Contains("read:spend_limits", ex.Message);
        Assert.Contains("usage credits", ex.Message);
        Assert.DoesNotContain(Key, ex.Message);
    }

    [Fact]
    public async Task List_NeverLogsAMemberOrAFigureOrTheCursor()
    {
        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath(Path).UsingGet().WithParam("page", WireMock.Matchers.MatchBehaviour.RejectOnMatch))
            .RespondWith(Json($$"""{ "data": [ {{Row("user_FIRST", "777700", "123400")}} ], "next_page": "cursor_SECRET" }"""));
        server.Given(Request.Create().WithPath(Path).UsingGet().WithParam("page", "cursor_SECRET"))
            .RespondWith(Json($$"""{ "data": [ {{Row("user_SECOND", "1", "0")}} ], "next_page": null }"""));
        var logger = new CapturingLogger<HttpClaudeSpendLimitRepository>();
        var repository = new HttpClaudeSpendLimitRepository(new HttpClient { BaseAddress = new Uri(server.Url + "/") }, logger);

        await repository.ListEffectiveLimitsAsync(Key, TestContext.Current.CancellationToken);

        Assert.NotEmpty(logger.Entries);   // it does log, so this is not vacuous
        var everything = string.Join("\n", logger.Entries.Select(e => e.Message));
        foreach (var secret in new[] { "cursor_SECRET", "user_FIRST", "user_SECOND", "example.test", "Name user", "7777", "1234", Key, "page=" })
        {
            Assert.DoesNotContain(secret, everything);
        }
    }

    [Fact]
    public async Task Source_ReadsTheStoredKey()
    {
        var store = new FakeSecretStore();
        await store.SetCredentialAsync("t1", VendorCatalog.ClaudeEnterprise.Id, Key, TestContext.Current.CancellationToken);
        var repository = new RecordingRepository([new VendorSpendLimit { MemberId = "u1" }]);
        var source = new ClaudeEnterpriseSpendLimitSource(store, repository);

        var limits = await source.FetchLimitsAsync("t1", TestContext.Current.CancellationToken);

        Assert.Equal("u1", Assert.Single(limits).MemberId);
        Assert.Equal(Key, repository.ReceivedKey);
        Assert.Equal(VendorCatalog.ClaudeEnterprise.Id, source.VendorId);
    }

    [Fact]
    public async Task Source_WithNoStoredCredential_FailsLoudly_AndNeverCallsTheApi()
    {
        var repository = new RecordingRepository([]);
        var source = new ClaudeEnterpriseSpendLimitSource(new FakeSecretStore(), repository);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => source.FetchLimitsAsync("t1", TestContext.Current.CancellationToken));

        Assert.Contains("No Claude Enterprise credential", ex.Message);
        Assert.Null(repository.ReceivedKey);
    }

    private static void Respond(WireMockServer server, string body) =>
        server.Given(Request.Create().WithPath(Path).UsingGet()).RespondWith(Json(body));

    private static IResponseBuilder Json(string body) =>
        Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/json").WithBody(body);

    private static HttpClaudeSpendLimitRepository CreateRepository(WireMockServer server) =>
        new(new HttpClient { BaseAddress = new Uri(server.Url + "/") }, NullLogger<HttpClaudeSpendLimitRepository>.Instance);

    private sealed class RecordingRepository(IReadOnlyList<VendorSpendLimit> limits) : IClaudeSpendLimitRepository
    {
        public string? ReceivedKey { get; private set; }

        public Task<IReadOnlyList<VendorSpendLimit>> ListEffectiveLimitsAsync(string apiKey, CancellationToken cancellationToken = default)
        {
            ReceivedKey = apiKey;
            return Task.FromResult(limits);
        }
    }
}
