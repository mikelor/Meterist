using System.Net.Http;
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
/// Response bodies follow the real workspace user list shape captured 2026-11-20 (<c>object</c>, <c>data</c>,
/// <c>first_id</c>, <c>last_id</c>, <c>has_more</c>; a member is object, id, email, name, role, seat_type and
/// is_scim_managed), with every identifier replaced.
/// </summary>
public class HttpChatGptWorkspaceUserRepositoryTests
{
    private const string WorkspaceId = "11111111-2222-3333-4444-555555555555";
    private const string AdminKey = "test-admin-key";
    private static readonly string UsersPath = $"/v1/manage/workspaces/{WorkspaceId}/users";

    private static ChatGptCredential Credential(string? workspaceId = WorkspaceId) => new()
    {
        OrganizationId = "org-test",
        AdminApiKey = AdminKey,
        WorkspaceId = workspaceId,
    };

    [Fact]
    public async Task ListUsers_SendsTheBearerKeyAndLimit_AndMapsEveryField()
    {
        using var server = WireMockServer.Start();
        Respond(server, """
            {
              "object": "list",
              "data": [
                { "object": "workspace.user", "id": "user-aaa", "email": "a@example.test", "name": "Alex Example",
                  "role": "admin", "seat_type": "chatgpt", "is_scim_managed": true }
              ],
              "first_id": "user-aaa", "last_id": "user-aaa", "has_more": false
            }
            """);

        var members = await CreateRepository(server).ListUsersAsync(Credential(), TestContext.Current.CancellationToken);

        var member = Assert.Single(members);
        Assert.Equal("user-aaa", member.Id);
        Assert.Equal("a@example.test", member.Email);
        Assert.Equal("Alex Example", member.Name);
        Assert.Equal("admin", member.Role);
        Assert.Equal("chatgpt", member.SeatType);
        Assert.True(member.IsScimManaged);
        Assert.Null(member.AddedAt);   // the API gives no join date

        var request = Assert.Single(server.LogEntries).RequestMessage!;
        Assert.Equal($"Bearer {AdminKey}", request.Headers!["Authorization"].Single());
        Assert.Contains("limit=1000", request.Url!);
        Assert.DoesNotContain("after=", request.Url!);
    }

    [Fact]
    public async Task ListUsers_FollowsLastIdAsAfter_ToTheEnd()
    {
        using var server = WireMockServer.Start();
        server
            .Given(Request.Create().WithPath(UsersPath).UsingGet().WithParam("after", WireMock.Matchers.MatchBehaviour.RejectOnMatch))
            .RespondWith(Json("""
                { "data": [ { "id": "u1", "role": "member", "seat_type": "chatgpt" } ], "has_more": true, "last_id": "cursor/odd+chars=" }
                """));
        server
            .Given(Request.Create().WithPath(UsersPath).UsingGet().WithParam("after", "cursor/odd+chars="))
            .RespondWith(Json("""
                { "data": [ { "id": "u2", "role": "owner", "seat_type": "codex" } ], "has_more": false, "last_id": "u2" }
                """));

        var members = await CreateRepository(server).ListUsersAsync(Credential(), TestContext.Current.CancellationToken);

        Assert.Equal(["u1", "u2"], members.Select(m => m.Id));
        Assert.Equal(["chatgpt", "codex"], members.Select(m => m.SeatType));
        Assert.Contains("limit=1000", server.LogEntries.Last().RequestMessage!.Url!);   // every other parameter is repeated
    }

    [Fact]
    public async Task ListUsers_AFieldTheApiOmits_IsNull_NotAnError()
    {
        using var server = WireMockServer.Start();
        Respond(server, """{ "data": [ { "id": "u1" } ], "has_more": false }""");

        var member = Assert.Single(await CreateRepository(server).ListUsersAsync(Credential(), TestContext.Current.CancellationToken));

        Assert.Null(member.Email);
        Assert.Null(member.Role);
        Assert.Null(member.SeatType);
        Assert.Null(member.IsScimManaged);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("\"  \"")]
    public async Task ListUsers_HasMoreWithNoUsableCursor_FailsInsteadOfReturningAPartialRoster(string cursor)
    {
        using var server = WireMockServer.Start();
        Respond(server, $$"""{ "data": [ { "id": "u1" } ], "has_more": true, "last_id": {{cursor}} }""");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CreateRepository(server).ListUsersAsync(Credential(), TestContext.Current.CancellationToken));

        Assert.Contains("partial", ex.Message);
        Assert.Single(server.LogEntries);   // no second request with an empty cursor
    }

    [Fact]
    public async Task ListUsers_ARepeatedCursor_FailsInsteadOfLooping()
    {
        using var server = WireMockServer.Start();
        Respond(server, """{ "data": [ { "id": "u1" } ], "has_more": true, "last_id": "same" }""");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CreateRepository(server).ListUsersAsync(Credential(), TestContext.Current.CancellationToken));

        Assert.Contains("already returned", ex.Message);
        Assert.Equal(2, server.LogEntries.Count());
    }

    [Fact]
    public async Task ListUsers_AMemberWithNoId_FailsInsteadOfBeingSkipped()
    {
        using var server = WireMockServer.Start();
        Respond(server, """{ "data": [ { "id": "u1" }, { "email": "noid@example.test" } ], "has_more": false }""");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CreateRepository(server).ListUsersAsync(Credential(), TestContext.Current.CancellationToken));

        Assert.Contains("no id", ex.Message);
        Assert.DoesNotContain("noid@example.test", ex.Message);
    }

    [Fact]
    public async Task ListUsers_WithNoWorkspaceId_SaysHowToFixIt_WithoutCallingTheApi()
    {
        using var server = WireMockServer.Start();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CreateRepository(server).ListUsersAsync(Credential(workspaceId: null), TestContext.Current.CancellationToken));

        Assert.Contains("WorkspaceId", ex.Message);
        Assert.Contains("credentials set", ex.Message);
        Assert.Empty(server.LogEntries);
    }

    [Theory]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(404)]
    public async Task ListUsers_AnAuthOrScopeFailure_NamesTheScopeAndWorkspace_WithoutLeakingTheKey(int status)
    {
        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath(UsersPath).UsingGet()).RespondWith(Response.Create().WithStatusCode(status));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CreateRepository(server).ListUsersAsync(Credential(), TestContext.Current.CancellationToken));

        Assert.Contains(status.ToString(), ex.Message);
        Assert.Contains("chatgpt.enterprise.user.read", ex.Message);
        Assert.Contains("WorkspaceId", ex.Message);
        Assert.DoesNotContain(AdminKey, ex.Message);
    }

    [Fact]
    public async Task ListUsers_NeverLogsACursorAMemberIdOrAnyMemberField()
    {
        using var server = WireMockServer.Start();
        server
            .Given(Request.Create().WithPath(UsersPath).UsingGet().WithParam("after", WireMock.Matchers.MatchBehaviour.RejectOnMatch))
            .RespondWith(Json("""
                { "data": [ { "id": "user_FIRST", "email": "first@example.test", "name": "First Person", "role": "member" } ],
                  "has_more": true, "last_id": "cursor_LAST_ID_SECRET" }
                """));
        server
            .Given(Request.Create().WithPath(UsersPath).UsingGet().WithParam("after", "cursor_LAST_ID_SECRET"))
            .RespondWith(Json("""
                { "data": [ { "id": "user_SECOND", "email": "second@example.test", "name": "Second Person", "role": "owner" } ],
                  "has_more": false, "last_id": "user_SECOND" }
                """));
        var logger = new CapturingLogger<HttpChatGptWorkspaceUserRepository>();
        var repository = new HttpChatGptWorkspaceUserRepository(
            new HttpClient { BaseAddress = new Uri(server.Url + "/") }, logger);

        await repository.ListUsersAsync(Credential(), TestContext.Current.CancellationToken);

        Assert.NotEmpty(logger.Entries);   // it does log, so this is not vacuous
        var everything = string.Join("\n", logger.Entries.Select(e => e.Message));
        foreach (var secret in new[] { "cursor_LAST_ID_SECRET", "user_FIRST", "user_SECOND", "example.test", "First Person", "Second Person", AdminKey, "after=" })
        {
            Assert.DoesNotContain(secret, everything);
        }

        Assert.Contains("page 2", everything);
        Assert.Contains("2 member(s)", everything);
    }

    [Fact]
    public async Task RosterSource_ReadsTheStoredCredential_AndPassesItToTheApi()
    {
        var store = new FakeSecretStore();
        await store.SetCredentialAsync("t1", VendorCatalog.ChatGptEnterprise.Id, JsonSerializer.Serialize(Credential()), TestContext.Current.CancellationToken);
        var repository = new RecordingRepository([new VendorMember { Id = "u1", SeatType = "chatgpt" }]);
        var source = new ChatGptEnterpriseRosterSource(store, repository);

        var members = await source.FetchMembersAsync("t1", TestContext.Current.CancellationToken);

        Assert.Equal("u1", Assert.Single(members).Id);
        Assert.Equal(AdminKey, repository.Received!.AdminApiKey);
        Assert.Equal(WorkspaceId, repository.Received.WorkspaceId);
        Assert.Equal(VendorCatalog.ChatGptEnterprise.Id, source.VendorId);
    }

    [Fact]
    public async Task RosterSource_WithNoStoredCredential_FailsLoudly_AndNeverCallsTheApi()
    {
        var repository = new RecordingRepository([]);
        var source = new ChatGptEnterpriseRosterSource(new FakeSecretStore(), repository);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => source.FetchMembersAsync("t1", TestContext.Current.CancellationToken));

        Assert.Contains("No ChatGPT Enterprise credential", ex.Message);
        Assert.Null(repository.Received);
    }

    [Fact]
    public async Task RosterSource_WithAnInvalidStoredCredential_SaysSo_WithoutEchoingTheKey()
    {
        var store = new FakeSecretStore();
        await store.SetCredentialAsync("t1", VendorCatalog.ChatGptEnterprise.Id, """{ "OrganizationId": "", "AdminApiKey": "sk-secret-value" }""", TestContext.Current.CancellationToken);
        var repository = new RecordingRepository([]);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => new ChatGptEnterpriseRosterSource(store, repository).FetchMembersAsync("t1", TestContext.Current.CancellationToken));

        Assert.Contains("invalid", ex.Message);
        Assert.DoesNotContain("sk-secret-value", ex.Message);
        Assert.Null(repository.Received);
    }

    private static void Respond(WireMockServer server, string body) =>
        server.Given(Request.Create().WithPath(UsersPath).UsingGet()).RespondWith(Json(body));

    private static IResponseBuilder Json(string body) =>
        Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/json").WithBody(body);

    private static HttpChatGptWorkspaceUserRepository CreateRepository(WireMockServer server) =>
        new(new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { BaseAddress = new Uri(server.Url + "/") },
            NullLogger<HttpChatGptWorkspaceUserRepository>.Instance);

    private sealed class RecordingRepository(IReadOnlyList<VendorMember> members) : IChatGptWorkspaceUserRepository
    {
        public ChatGptCredential? Received { get; private set; }

        public Task<IReadOnlyList<VendorMember>> ListUsersAsync(ChatGptCredential credential, CancellationToken cancellationToken = default)
        {
            Received = credential;
            return Task.FromResult(members);
        }
    }
}
