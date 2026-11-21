using Meterist.Core.Models;
using Meterist.Core.Vendors;
using Meterist.Vendors.ClaudeEnterprise;
using Meterist.Vendors.Tests.GeminiEnterprise;
using Microsoft.Extensions.Logging.Abstractions;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Meterist.Vendors.Tests.ClaudeEnterprise;

/// <summary>
/// Response bodies follow the real <c>/analytics/summaries</c> shape captured 2026-11-20 (a <c>data</c> array of days
/// with <c>starting_at</c>, flat counts and rates, a null for a product the organisation lacks, and <c>next_page</c>),
/// with every figure replaced. The API's error for a date past its latest day is reproduced from the live 400.
/// </summary>
public class ClaudeActivitySummaryTests
{
    private const string Key = "sk-ant-test-key";
    private const string Path = "/v1/organizations/analytics/summaries";

    private static readonly DateOnly Yesterday = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-1);

    [Fact]
    public async Task Query_SendsTheKeyAndAnInclusiveStartAndExclusiveEnd_WithNoBetaHeader()
    {
        using var server = WireMockServer.Start();
        Respond(server, """{ "data": [ { "starting_at": "2026-11-18T00:00:00Z", "daily_active_user_count": 3 } ], "next_page": null }""");

        var days = await CreateRepository(server).QueryAsync(
            Key, new DateRange(new DateOnly(2026, 11, 18), new DateOnly(2026, 11, 18)), TestContext.Current.CancellationToken);

        var day = Assert.Single(days);
        Assert.Equal(new DateOnly(2026, 11, 18), day.Date);
        Assert.Equal(3m, day.Values["daily_active_user_count"]);

        var request = Assert.Single(server.LogEntries).RequestMessage!;
        Assert.Equal(Key, request.Headers!["x-api-key"].Single());
        Assert.Equal("2023-06-01", request.Headers["anthropic-version"].Single());
        Assert.False(request.Headers.ContainsKey("anthropic-beta"));
        Assert.Contains("starting_date=2026-11-18", request.Url!);
        Assert.Contains("ending_date=2026-11-19", request.Url!);   // exclusive, so the last wanted day plus one
    }

    [Fact]
    public async Task Query_KeepsEveryNumericFieldIncludingNullsAndOnesItDoesNotKnow()
    {
        using var server = WireMockServer.Start();
        Respond(server, """
            { "data": [ { "starting_at": "2026-11-18T00:00:00Z", "ending_at": "2026-11-19T00:00:00Z",
                          "assigned_seat_count": 10, "daily_adoption_rate": 30.5,
                          "claude_tag_daily_active_user_count": null, "brand_new_product_daily_active_user_count": 4,
                          "note": "text is not a number" } ], "next_page": null }
            """);

        var day = Assert.Single(await CreateRepository(server).QueryAsync(
            Key, new DateRange(new DateOnly(2026, 11, 18), new DateOnly(2026, 11, 18)), TestContext.Current.CancellationToken));

        Assert.Equal(10m, day.Values["assigned_seat_count"]);
        Assert.Equal(30.5m, day.Values["daily_adoption_rate"]);
        Assert.Null(day.Values["claude_tag_daily_active_user_count"]);
        Assert.Equal(4m, day.Values["brand_new_product_daily_active_user_count"]);
        Assert.False(day.Values.ContainsKey("note"));
        Assert.False(day.Values.ContainsKey("ending_at"));
    }

    [Fact]
    public async Task Query_StartingBeforeTheFirstAvailableDay_AsksFromJanuaryFirst()
    {
        using var server = WireMockServer.Start();
        Respond(server, """{ "data": [], "next_page": null }""");

        await CreateRepository(server).QueryAsync(
            Key, new DateRange(new DateOnly(2025, 6, 1), new DateOnly(2026, 1, 10)), TestContext.Current.CancellationToken);

        Assert.Contains("starting_date=2026-01-01", Assert.Single(server.LogEntries).RequestMessage!.Url!);
    }

    [Fact]
    public async Task Query_EndingToday_AsksOnlyUpToYesterday_BecauseTheNewestDayIsPublishedTheNextDay()
    {
        using var server = WireMockServer.Start();
        Respond(server, """{ "data": [], "next_page": null }""");

        await CreateRepository(server).QueryAsync(
            Key, new DateRange(Yesterday.AddDays(-3), Yesterday.AddDays(1)), TestContext.Current.CancellationToken);

        Assert.Contains($"ending_date={Yesterday.AddDays(1):yyyy-MM-dd}", Assert.Single(server.LogEntries).RequestMessage!.Url!);
    }

    [Fact]
    public async Task Query_APeriodWithNothingAvailableYet_MakesNoRequest()
    {
        using var server = WireMockServer.Start();
        Respond(server, """{ "data": [], "next_page": null }""");

        var days = await CreateRepository(server).QueryAsync(
            Key, new DateRange(Yesterday.AddDays(1), Yesterday.AddDays(1)), TestContext.Current.CancellationToken);

        Assert.Empty(days);
        Assert.Empty(server.LogEntries);
    }

    [Fact]
    public async Task Query_WhenTheApiSaysItsLatestDayIsEarlier_AsksAgainForJustThat()
    {
        // Before the day's snapshot is published (about 13:00 UTC) yesterday does not exist yet.
        using var server = WireMockServer.Start();
        var latest = Yesterday.AddDays(-1);
        server.Given(Request.Create().WithPath(Path).UsingGet().WithParam("ending_date", $"{Yesterday.AddDays(1):yyyy-MM-dd}"))
            .RespondWith(Response.Create().WithStatusCode(400).WithHeader("Content-Type", "application/json").WithBody(
                "{\"type\":\"error\",\"error\":{\"type\":\"invalid_request_error\",\"message\":\"Latest available data for this query is " + latest.ToString("yyyy-MM-dd") + ".\"}}"));
        server.Given(Request.Create().WithPath(Path).UsingGet().WithParam("ending_date", $"{latest.AddDays(1):yyyy-MM-dd}"))
            .RespondWith(Json("{ \"data\": [ { \"starting_at\": \"" + latest.ToString("yyyy-MM-dd") + "T00:00:00Z\", \"daily_active_user_count\": 2 } ], \"next_page\": null }"));

        var days = await CreateRepository(server).QueryAsync(
            Key, new DateRange(latest.AddDays(-2), Yesterday), TestContext.Current.CancellationToken);

        Assert.Equal(latest, Assert.Single(days).Date);
        Assert.Equal(2, server.LogEntries.Count);
    }

    [Fact]
    public async Task Query_AnotherBadRequest_Throws()
    {
        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath(Path).UsingGet()).RespondWith(
            Response.Create().WithStatusCode(400).WithBody("""{"error":{"message":"something else"}}"""));

        await Assert.ThrowsAsync<HttpRequestException>(() => CreateRepository(server).QueryAsync(
            Key, new DateRange(new DateOnly(2026, 11, 1), new DateOnly(2026, 11, 2)), TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(401)]
    [InlineData(403)]
    public async Task Query_AnAuthFailure_SaysToCheckTheKeyAndScope(int status)
    {
        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath(Path).UsingGet()).RespondWith(Response.Create().WithStatusCode(status).WithBody("{}"));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => CreateRepository(server).QueryAsync(
            Key, new DateRange(new DateOnly(2026, 11, 1), new DateOnly(2026, 11, 2)), TestContext.Current.CancellationToken));

        Assert.Contains("read:analytics", ex.Message);
        Assert.DoesNotContain(Key, ex.Message);
    }

    [Fact]
    public async Task Query_FollowsNextPageToTheEnd_RepeatingTheOtherParameters()
    {
        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath(Path).UsingGet().WithParam("page", WireMock.Matchers.MatchBehaviour.RejectOnMatch))
            .RespondWith(Json("""{ "data": [ { "starting_at": "2026-11-01T00:00:00Z", "daily_active_user_count": 1 } ], "next_page": "cur/sor+1=" }"""));
        server.Given(Request.Create().WithPath(Path).UsingGet().WithParam("page", "cur/sor+1="))
            .RespondWith(Json("""{ "data": [ { "starting_at": "2026-11-02T00:00:00Z", "daily_active_user_count": 2 } ], "next_page": null }"""));

        var days = await CreateRepository(server).QueryAsync(
            Key, new DateRange(new DateOnly(2026, 11, 1), new DateOnly(2026, 11, 2)), TestContext.Current.CancellationToken);

        Assert.Equal([new DateOnly(2026, 11, 1), new DateOnly(2026, 11, 2)], days.Select(d => d.Date));
        Assert.Contains("starting_date=2026-11-01", server.LogEntries.Last().RequestMessage!.Url!);
    }

    [Fact]
    public async Task Query_ARepeatedCursor_FailsInsteadOfLooping()
    {
        using var server = WireMockServer.Start();
        Respond(server, """{ "data": [ { "starting_at": "2026-11-01T00:00:00Z", "daily_active_user_count": 1 } ], "next_page": "same" }""");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => CreateRepository(server).QueryAsync(
            Key, new DateRange(new DateOnly(2026, 11, 1), new DateOnly(2026, 11, 2)), TestContext.Current.CancellationToken));

        Assert.Contains("already returned", ex.Message);
    }

    [Fact]
    public async Task Query_ARowWithNoUsableDate_FailsInsteadOfBeingSkipped()
    {
        using var server = WireMockServer.Start();
        Respond(server, """{ "data": [ { "daily_active_user_count": 3 } ], "next_page": null }""");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => CreateRepository(server).QueryAsync(
            Key, new DateRange(new DateOnly(2026, 11, 1), new DateOnly(2026, 11, 2)), TestContext.Current.CancellationToken));

        Assert.Contains("partial", ex.Message);
    }

    [Theory]
    [InlineData("{ \"starting_at\": \"2026-11-18T00:00:00Z\" }")]
    [InlineData("{ \"starting_at\": \"2026-11-18T00:00:00Z\", \"ending_at\": \"2026-11-19T00:00:00Z\", \"note\": \"x\" }")]
    [InlineData("{ \"starting_at\": \"2026-11-18T00:00:00Z\", \"daily_active_user_count\": null, \"assigned_seat_count\": null }")]
    public async Task Query_ARowWithADateButNoNumbers_IsRejected_SoItCannotBlankStoredFigures(string row)
    {
        using var server = WireMockServer.Start();
        Respond(server, "{ \"data\": [ " + row + " ], \"next_page\": null }");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => CreateRepository(server).QueryAsync(
            Key, new DateRange(new DateOnly(2026, 11, 18), new DateOnly(2026, 11, 18)), TestContext.Current.CancellationToken));

        Assert.Contains("no numeric fields", ex.Message);
    }

    [Theory]
    [InlineData("2.5")]
    [InlineData("-1")]
    [InlineData("3000000000")]
    public void Map_ACountThatIsNotAWholeIntInRange_IsRejected_NotTruncated(string value)
    {
        var day = new ClaudeActivityDay(new DateOnly(2026, 11, 18), new Dictionary<string, decimal?>
        {
            ["daily_active_user_count"] = decimal.Parse(value, System.Globalization.CultureInfo.InvariantCulture),
        });

        var ex = Assert.Throws<InvalidOperationException>(
            () => ClaudeEnterpriseActivitySummarySource.Map("t1", VendorCatalog.ClaudeEnterprise.Id, [day]));

        Assert.Contains("daily_active_user_count", ex.Message);
    }

    [Fact]
    public void Map_AWholeNumberThatArrivedWithADecimalPoint_IsAccepted()
    {
        var day = new ClaudeActivityDay(new DateOnly(2026, 11, 18), new Dictionary<string, decimal?>
        {
            ["daily_active_user_count"] = 3.0m,
            ["daily_adoption_rate"] = 33.3m,   // a rate is a percentage and may be fractional
        });

        var row = Assert.Single(ClaudeEnterpriseActivitySummarySource.Map("t1", VendorCatalog.ClaudeEnterprise.Id, [day]));

        Assert.Equal(3, row.DailyActiveUsers);
        Assert.Equal(33.3m, row.DailyAdoptionRate);
    }

    [Fact]
    public async Task Query_APeriodLongerThanTheSpanCap_IsSplitIntoWindows()
    {
        using var server = WireMockServer.Start();
        Respond(server, """{ "data": [], "next_page": null }""");

        await CreateRepository(server).QueryAsync(
            Key, new DateRange(new DateOnly(2026, 1, 1), new DateOnly(2026, 11, 1)), TestContext.Current.CancellationToken);

        // 2026-01-01 .. 2026-11-01 is 305 days, inside one 366-day window.
        Assert.Single(server.LogEntries);
    }

    [Fact]
    public void Map_ProducesAnAllRow_AndOneRowPerProductFoundByThePattern()
    {
        var day = new ClaudeActivityDay(new DateOnly(2026, 11, 18), new Dictionary<string, decimal?>
        {
            ["daily_active_user_count"] = 3,
            ["weekly_active_user_count"] = 6,
            ["monthly_active_user_count"] = 9,
            ["assigned_seat_count"] = 10,
            ["pending_invite_count"] = 1,
            ["daily_adoption_rate"] = 30,
            ["weekly_adoption_rate"] = 60,
            ["monthly_adoption_rate"] = 90,
            ["claude_code_daily_active_user_count"] = 2,
            ["claude_code_weekly_active_user_count"] = 2,
            ["claude_code_monthly_active_user_count"] = 2,
            ["chat_cowork_unified_daily_active_user_count"] = 1,
            ["claude_tag_daily_active_user_count"] = null,
            ["claude_tag_weekly_active_user_count"] = null,
            ["claude_tag_monthly_active_user_count"] = null,
            ["science_entitled_user_count"] = 6,
        });

        var rows = ClaudeEnterpriseActivitySummarySource.Map("t1", VendorCatalog.ClaudeEnterprise.Id, [day]);

        Assert.Equal(["all", "chat_cowork_unified", "claude_code"], rows.Select(r => r.Product));

        var all = rows[0];
        Assert.Equal((3, 6, 9), (all.DailyActiveUsers, all.WeeklyActiveUsers, all.MonthlyActiveUsers));
        Assert.Equal((10, 1), (all.AssignedSeatCount, all.PendingInviteCount));
        Assert.Equal((30m, 60m, 90m), (all.DailyAdoptionRate, all.WeeklyAdoptionRate, all.MonthlyAdoptionRate));

        var code = rows[2];
        Assert.Equal((2, 2, 2), (code.DailyActiveUsers, code.WeeklyActiveUsers, code.MonthlyActiveUsers));
        Assert.Null(code.AssignedSeatCount);

        var unified = rows[1];
        Assert.Equal(1, unified.DailyActiveUsers);
        Assert.Null(unified.WeeklyActiveUsers);   // only what the API gave is stored
        Assert.All(rows, r => Assert.Equal("t1", r.TenantId));
    }

    [Fact]
    public async Task Source_ReadsTheStoredKey_AndKeepsOnlyDaysInsideThePeriod()
    {
        var store = new FakeSecretStore();
        await store.SetCredentialAsync("t1", VendorCatalog.ClaudeEnterprise.Id, Key, TestContext.Current.CancellationToken);
        var repository = new RecordingRepository(
        [
            new ClaudeActivityDay(new DateOnly(2026, 11, 1), new Dictionary<string, decimal?> { ["daily_active_user_count"] = 1 }),
            new ClaudeActivityDay(new DateOnly(2026, 11, 5), new Dictionary<string, decimal?> { ["daily_active_user_count"] = 2 }),
        ]);
        var source = new ClaudeEnterpriseActivitySummarySource(store, repository);

        var rows = await source.FetchAsync(
            "t1", new DateRange(new DateOnly(2026, 11, 4), new DateOnly(2026, 11, 6)), TestContext.Current.CancellationToken);

        Assert.Equal(Key, repository.ReceivedKey);
        Assert.Equal(new DateOnly(2026, 11, 5), Assert.Single(rows).Date);
        Assert.Equal(VendorCatalog.ClaudeEnterprise.Id, source.VendorId);
    }

    [Fact]
    public async Task Source_WithNoStoredCredential_FailsLoudly_AndNeverCallsTheApi()
    {
        var repository = new RecordingRepository([]);
        var source = new ClaudeEnterpriseActivitySummarySource(new FakeSecretStore(), repository);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => source.FetchAsync(
            "t1", new DateRange(new DateOnly(2026, 11, 4), new DateOnly(2026, 11, 6)), TestContext.Current.CancellationToken));

        Assert.Contains("No Claude Enterprise credential", ex.Message);
        Assert.Null(repository.ReceivedKey);
    }

    private static void Respond(WireMockServer server, string body) =>
        server.Given(Request.Create().WithPath(Path).UsingGet()).RespondWith(Json(body));

    private static IResponseBuilder Json(string body) =>
        Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/json").WithBody(body);

    private static HttpClaudeActivitySummaryRepository CreateRepository(WireMockServer server) =>
        new(new HttpClient { BaseAddress = new Uri(server.Url + "/") }, NullLogger<HttpClaudeActivitySummaryRepository>.Instance);

    private sealed class RecordingRepository(IReadOnlyList<ClaudeActivityDay> days) : IClaudeActivitySummaryRepository
    {
        public string? ReceivedKey { get; private set; }

        public Task<IReadOnlyList<ClaudeActivityDay>> QueryAsync(
            string analyticsApiKey, DateRange period, CancellationToken cancellationToken = default)
        {
            ReceivedKey = analyticsApiKey;
            return Task.FromResult(days);
        }
    }
}
