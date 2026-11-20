using Meterist.Core.Vendors;
using Meterist.Vendors.ClaudeEnterprise;
using Meterist.Vendors.Tests.GeminiEnterprise;
using Microsoft.Extensions.Logging.Abstractions;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Meterist.Vendors.Tests.ClaudeEnterprise;

/// <summary>
/// Response bodies follow the real member list shape captured 2026-11-20
/// (<c>data</c>, <c>has_more</c>, <c>first_id</c>, <c>last_id</c>; a member is id, type, email, name, role and
/// added_at), with every identifier replaced.
/// </summary>
public class HttpClaudeMemberRosterRepositoryTests
{
    private const string Key = "sk-ant-test-key";
    private const string UsersPath = "/v1/organizations/users";

    [Fact]
    public async Task ListMembers_SendsTheKeyAndLimit_AndMapsEveryField()
    {
        using var server = WireMockServer.Start();
        Respond(server, """
            {
              "data": [
                { "id": "user_01", "type": "user", "email": "a@example.test", "name": "Alex Example",
                  "role": "primary_owner", "added_at": "2026-03-30T00:04:14.123456Z" }
              ],
              "has_more": false, "first_id": "user_01", "last_id": "user_01"
            }
            """);

        var members = await CreateRepository(server).ListMembersAsync(Key, TestContext.Current.CancellationToken);

        var member = Assert.Single(members);
        Assert.Equal("user_01", member.Id);
        Assert.Equal("a@example.test", member.Email);
        Assert.Equal("Alex Example", member.Name);
        Assert.Equal("primary_owner", member.Role);
        Assert.Null(member.SeatType);
        Assert.Equal(new DateTimeOffset(2026, 3, 30, 0, 4, 14, TimeSpan.Zero).AddTicks(1234560), member.AddedAt);

        var request = Assert.Single(server.LogEntries).RequestMessage!;
        Assert.Equal("2023-06-01", request.Headers!["anthropic-version"].Single());
        Assert.Equal(Key, request.Headers["x-api-key"].Single());
        Assert.Contains("limit=1000", request.Url!);
        Assert.DoesNotContain("after_id", request.Url!);
    }

    [Fact]
    public async Task ListMembers_FollowsTheCursorToTheEnd_PassingLastIdAsAfterId()
    {
        using var server = WireMockServer.Start();
        server
            .Given(Request.Create().WithPath(UsersPath).UsingGet().WithParam("after_id", WireMock.Matchers.MatchBehaviour.RejectOnMatch))
            .RespondWith(Json("""
                { "data": [ { "id": "u1", "role": "user" } ], "has_more": true, "first_id": "u1", "last_id": "cursor/odd+chars=" }
                """));
        server
            .Given(Request.Create().WithPath(UsersPath).UsingGet().WithParam("after_id", "cursor/odd+chars="))
            .RespondWith(Json("""
                { "data": [ { "id": "u2", "role": "owner" } ], "has_more": false, "first_id": "u2", "last_id": "u2" }
                """));

        var members = await CreateRepository(server).ListMembersAsync(Key, TestContext.Current.CancellationToken);

        Assert.Equal(["u1", "u2"], members.Select(m => m.Id));
        Assert.Contains("limit=1000", server.LogEntries.Last().RequestMessage!.Url!);   // every other parameter is repeated
    }

    [Fact]
    public async Task ListMembers_AMemberWithNoAddedAtOrAnUnparseableOne_GetsNull()
    {
        using var server = WireMockServer.Start();
        Respond(server, """
            { "data": [ { "id": "u1" }, { "id": "u2", "added_at": "not a date" } ], "has_more": false, "last_id": "u2" }
            """);

        var members = await CreateRepository(server).ListMembersAsync(Key, TestContext.Current.CancellationToken);

        Assert.All(members, m => Assert.Null(m.AddedAt));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("\"  \"")]
    public async Task ListMembers_HasMoreWithNoUsableCursor_FailsInsteadOfReturningAPartialRoster(string cursor)
    {
        using var server = WireMockServer.Start();
        Respond(server, $$"""
            { "data": [ { "id": "u1" } ], "has_more": true, "last_id": {{cursor}} }
            """);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CreateRepository(server).ListMembersAsync(Key, TestContext.Current.CancellationToken));

        Assert.Contains("partial", ex.Message);
        Assert.Single(server.LogEntries);   // no second request with an empty cursor
    }

    [Fact]
    public async Task ListMembers_ARepeatedCursor_FailsInsteadOfLooping()
    {
        using var server = WireMockServer.Start();
        Respond(server, """
            { "data": [ { "id": "u1" } ], "has_more": true, "last_id": "same" }
            """);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CreateRepository(server).ListMembersAsync(Key, TestContext.Current.CancellationToken));

        Assert.Contains("already returned", ex.Message);
        Assert.Equal(2, server.LogEntries.Count());
    }

    [Fact]
    public async Task ListMembers_AMemberWithNoId_FailsInsteadOfBeingSkipped()
    {
        using var server = WireMockServer.Start();
        Respond(server, """
            { "data": [ { "id": "u1" }, { "email": "noid@example.test" } ], "has_more": false }
            """);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CreateRepository(server).ListMembersAsync(Key, TestContext.Current.CancellationToken));

        Assert.Contains("no id", ex.Message);
        Assert.DoesNotContain("noid@example.test", ex.Message);
    }

    [Theory]
    [InlineData(401)]
    [InlineData(403)]
    public async Task ListMembers_AnAuthFailure_NamesTheScope_WithoutLeakingTheKey(int status)
    {
        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath(UsersPath).UsingGet()).RespondWith(Response.Create().WithStatusCode(status));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CreateRepository(server).ListMembersAsync(Key, TestContext.Current.CancellationToken));

        Assert.Contains(status.ToString(), ex.Message);
        Assert.Contains("read:members", ex.Message);
        Assert.DoesNotContain(Key, ex.Message);
    }

    [Fact]
    public async Task ListMembers_A404_SaysUserManagementIsAnEnterpriseFeature()
    {
        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath(UsersPath).UsingGet()).RespondWith(Response.Create().WithStatusCode(404));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CreateRepository(server).ListMembersAsync(Key, TestContext.Current.CancellationToken));

        Assert.Contains("Enterprise", ex.Message);
    }

    [Fact]
    public async Task RosterSource_ReadsTheTenantsStoredKey_AndPassesItOn()
    {
        var store = new FakeSecretStore();
        await store.SetCredentialAsync("t1", VendorCatalog.ClaudeEnterprise.Id, Key, TestContext.Current.CancellationToken);
        var repository = new RecordingRosterRepository([new VendorMember { Id = "u1" }]);
        var source = new ClaudeEnterpriseRosterSource(store, repository);

        var members = await source.FetchMembersAsync("t1", TestContext.Current.CancellationToken);

        Assert.Equal("u1", Assert.Single(members).Id);
        Assert.Equal(Key, repository.ReceivedKey);
        Assert.Equal(VendorCatalog.ClaudeEnterprise.Id, source.VendorId);
    }

    [Fact]
    public async Task RosterSource_WithNoStoredCredential_FailsLoudly_AndNeverCallsTheApi()
    {
        var repository = new RecordingRosterRepository([]);
        var source = new ClaudeEnterpriseRosterSource(new FakeSecretStore(), repository);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => source.FetchMembersAsync("t1", TestContext.Current.CancellationToken));

        Assert.Contains("No Claude Enterprise credential", ex.Message);
        Assert.Null(repository.ReceivedKey);
    }

    private static void Respond(WireMockServer server, string body) =>
        server.Given(Request.Create().WithPath(UsersPath).UsingGet()).RespondWith(Json(body));

    private static IResponseBuilder Json(string body) =>
        Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/json").WithBody(body);

    private static HttpClaudeMemberRosterRepository CreateRepository(WireMockServer server) =>
        new(new HttpClient { BaseAddress = new Uri(server.Url + "/") }, NullLogger<HttpClaudeMemberRosterRepository>.Instance);

    private sealed class RecordingRosterRepository(IReadOnlyList<VendorMember> members) : IClaudeMemberRosterRepository
    {
        public string? ReceivedKey { get; private set; }

        public Task<IReadOnlyList<VendorMember>> ListMembersAsync(string apiKey, CancellationToken cancellationToken = default)
        {
            ReceivedKey = apiKey;
            return Task.FromResult(members);
        }
    }
}
