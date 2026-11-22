using System.Text.Json;
using Meterist.Core.Models;
using Meterist.Core.Vendors;
using Meterist.Vendors.GeminiEnterprise;
using Microsoft.Extensions.Logging.Abstractions;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Meterist.Vendors.Tests.GeminiEnterprise;

/// <summary>
/// The Discovery Engine user licence list and the roster source built on it. Bodies follow Google's published reference
/// (userLicenses[] with userPrincipal, userProfile, licenseAssignmentState, createTime, lastLoginTime; nextPageToken), with
/// invented people. Not yet read from a live tenant.
/// </summary>
public class GeminiUserLicenseTests
{
    private const string ListPath = "/v1/projects/proj-1/locations/global/userStores/default_user_store/userLicenses";

    private static GeminiCredential Credential(string? project = "proj-1", string location = "global", string userStore = "default_user_store") => new()
    {
        ServiceAccountJson = "{}",
        BillingProjectId = "billing",
        BillingDatasetId = "d",
        BillingTableId = "t",
        DiscoveryEngineProjectId = project,
        DiscoveryEngineLocation = location,
        UserStoreId = userStore,
    };

    [Fact]
    public async Task List_SendsTheBearerTokenAndPageSize_AndMapsEveryState()
    {
        using var server = WireMockServer.Start();
        Respond(server, """
            { "userLicenses": [
                { "userPrincipal": "a@example.test", "userProfile": "Ann Example", "licenseAssignmentState": "ASSIGNED",
                  "licenseConfig": "projects/1/locations/global/licenseConfigs/lc", "createTime": "2026-10-01T08:30:00Z",
                  "updateTime": "2026-11-01T00:00:00Z", "lastLoginTime": "2026-11-20T10:00:00Z" },
                { "userPrincipal": "b@example.test", "licenseAssignmentState": "NO_LICENSE_ATTEMPTED_LOGIN" },
                { "userPrincipal": "c@example.test", "userProfile": "  ", "licenseAssignmentState": "BLOCKED", "createTime": "not a time" },
                { "userPrincipal": "svc-account" }
              ] }
            """);

        var members = await Repository(server).ListUserLicensesAsync(Credential(), TestContext.Current.CancellationToken);

        Assert.Equal(["a@example.test", "b@example.test", "c@example.test", "svc-account"], members.Select(m => m.Id));
        var ann = members[0];
        Assert.Equal("a@example.test", ann.Email);
        Assert.Equal("Ann Example", ann.Name);
        Assert.Equal("ASSIGNED", ann.SeatType);
        Assert.Equal(new DateTimeOffset(2026, 10, 1, 8, 30, 0, TimeSpan.Zero), ann.AddedAt);
        Assert.Null(ann.Role);
        Assert.Equal("NO_LICENSE_ATTEMPTED_LOGIN", members[1].SeatType);
        Assert.Null(members[1].Name);
        Assert.Null(members[2].Name);       // a blank profile is no name
        Assert.Null(members[2].AddedAt);    // an unreadable time is no time, not a failure
        Assert.Null(members[3].Email);      // an identifier that is not an email address
        Assert.Equal("LICENSE_ASSIGNMENT_STATE_UNSPECIFIED", members[3].SeatType);   // protobuf JSON omits the zero enum

        var request = Assert.Single(server.LogEntries).RequestMessage!;
        Assert.Equal("Bearer fake-token", request.Headers!["Authorization"].Single());
        Assert.Contains("pageSize=1000", request.Url!);
        Assert.Equal(ListPath, request.Path);
    }

    [Fact]
    public async Task List_UsesTheLocationAndUserStoreFromTheCredential()
    {
        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath("/v1/projects/p-2/locations/us/userStores/store_9/userLicenses").UsingGet())
            .RespondWith(Json("""{ "userLicenses": [ { "userPrincipal": "a@example.test", "licenseAssignmentState": "ASSIGNED" } ] }"""));

        var members = await Repository(server).ListUserLicensesAsync(
            Credential("p-2", "us", "store_9"), TestContext.Current.CancellationToken);

        Assert.Single(members);
    }

    [Theory]
    [InlineData("global", "https://discoveryengine.googleapis.com/")]
    [InlineData("us", "https://us-discoveryengine.googleapis.com/")]
    [InlineData("EU", "https://eu-discoveryengine.googleapis.com/")]
    public void TheHost_DependsOnTheLocation(string location, string expected) =>
        Assert.Equal(expected, HttpGeminiUserLicenseRepository.HostFor(location));

    [Fact]
    public async Task List_FollowsThePageTokenToTheEnd_RepeatingTheSameQuery()
    {
        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath(ListPath).UsingGet().WithParam("pageToken", WireMock.Matchers.MatchBehaviour.RejectOnMatch))
            .RespondWith(Json("""{ "userLicenses": [ { "userPrincipal": "a@example.test", "licenseAssignmentState": "ASSIGNED" } ], "nextPageToken": "tok/with+odd=chars" }"""));
        server.Given(Request.Create().WithPath(ListPath).UsingGet().WithParam("pageToken", "tok/with+odd=chars"))
            .RespondWith(Json("""{ "userLicenses": [ { "userPrincipal": "b@example.test", "licenseAssignmentState": "NO_LICENSE" } ] }"""));

        var members = await Repository(server).ListUserLicensesAsync(Credential(), TestContext.Current.CancellationToken);

        Assert.Equal(["a@example.test", "b@example.test"], members.Select(m => m.Id));
        Assert.Contains("pageSize=1000", server.LogEntries.Last().RequestMessage!.Url!);
    }

    [Fact]
    public async Task List_AnEmptyListLeavesOutTheArray_AndIsNoRecordsNotAnError()
    {
        using var server = WireMockServer.Start();
        Respond(server, "{}");

        Assert.Empty(await Repository(server).ListUserLicensesAsync(Credential(), TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("""{ "userLicenses": [ { "userProfile": "No Principal" } ] }""")]
    [InlineData("""{ "userLicenses": [ { "userPrincipal": "  " } ] }""")]
    [InlineData("""{ "userLicenses": [ null ] }""")]
    [InlineData("[]")]
    [InlineData("not json")]
    public async Task List_AMalformedResponse_FailsInsteadOfStoringAPartialRoster(string body)
    {
        using var server = WireMockServer.Start();
        Respond(server, body);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Repository(server).ListUserLicensesAsync(Credential(), TestContext.Current.CancellationToken));

        Assert.DoesNotContain("No Principal", ex.Message);
    }

    [Fact]
    public async Task List_ABlankOrRepeatedPageToken_FailsInsteadOfLooping()
    {
        using var blank = WireMockServer.Start();
        Respond(blank, """{ "userLicenses": [ { "userPrincipal": "a@example.test" } ], "nextPageToken": "  " }""");
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Repository(blank).ListUserLicensesAsync(Credential(), TestContext.Current.CancellationToken));
        Assert.Single(blank.LogEntries);

        using var loop = WireMockServer.Start();
        Respond(loop, """{ "userLicenses": [ { "userPrincipal": "a@example.test" } ], "nextPageToken": "same" }""");
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Repository(loop).ListUserLicensesAsync(Credential(), TestContext.Current.CancellationToken));
        Assert.Contains("repeated", ex.Message);
        Assert.Equal(2, loop.LogEntries.Count());
    }

    [Theory]
    [InlineData(401)]
    [InlineData(403)]
    public async Task List_AnAuthFailure_NamesThePermission_AndNeverTheToken(int status)
    {
        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath(ListPath).UsingGet()).RespondWith(Response.Create().WithStatusCode(status));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Repository(server).ListUserLicensesAsync(Credential(), TestContext.Current.CancellationToken));

        Assert.Contains("listUserLicenses", ex.Message);
        Assert.DoesNotContain("fake-token", ex.Message);
    }

    [Fact]
    public async Task List_A404_NamesTheSettingsToCheck()
    {
        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath(ListPath).UsingGet()).RespondWith(Response.Create().WithStatusCode(404));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Repository(server).ListUserLicensesAsync(Credential(), TestContext.Current.CancellationToken));

        Assert.Contains("UserStoreId", ex.Message);
        Assert.Contains("default_user_store", ex.Message);
    }

    [Theory]
    [InlineData(null, "global", "default_user_store")]
    [InlineData("", "global", "default_user_store")]
    [InlineData("proj/../x", "global", "default_user_store")]
    [InlineData("proj-1", "global?x=1", "default_user_store")]
    [InlineData("proj-1", "global", "a/b")]
    [InlineData("proj-1", "global", " ")]
    public async Task List_AnIdThatIsMissingOrCouldBreakOutOfTheUrlPath_IsRejectedBeforeAnyRequest(string? project, string location, string store)
    {
        using var server = WireMockServer.Start();
        Respond(server, "{}");

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Repository(server).ListUserLicensesAsync(Credential(project, location, store), TestContext.Current.CancellationToken));

        Assert.Empty(server.LogEntries);
    }

    // ---- the source ----

    [Fact]
    public async Task TheSource_WithNoDiscoveryEngineProject_IsNotSetUp_NotAFailure()
    {
        var store = new FakeSecretStore();
        await store.SetCredentialAsync("t1", VendorCatalog.GeminiEnterprise.Id, BillingOnlyJson, TestContext.Current.CancellationToken);
        var source = new GeminiEnterpriseRosterSource(store, new FakeLicenses());

        var reason = await source.GetSkipReasonAsync("t1", TestContext.Current.CancellationToken);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => source.FetchMembersAsync("t1", TestContext.Current.CancellationToken));

        Assert.Contains("DiscoveryEngineProjectId", reason);
        Assert.Contains("DiscoveryEngineProjectId", ex.Message);
        Assert.DoesNotContain("PRIVATE", ex.Message + reason);   // never the key
    }

    [Fact]
    public async Task TheSource_WithTheSettings_ReadsTheListWithThoseSettings_AndDefaultsTheRest()
    {
        var store = new FakeSecretStore();
        await store.SetCredentialAsync("t1", VendorCatalog.GeminiEnterprise.Id, WithProjectJson, TestContext.Current.CancellationToken);
        var licenses = new FakeLicenses();
        var source = new GeminiEnterpriseRosterSource(store, licenses);

        Assert.Null(await source.GetSkipReasonAsync("t1", TestContext.Current.CancellationToken));
        var members = await source.FetchMembersAsync("t1", TestContext.Current.CancellationToken);

        Assert.Single(members);
        Assert.Equal("proj-1", licenses.Received!.DiscoveryEngineProjectId);
        Assert.Equal("global", licenses.Received.DiscoveryEngineLocation);          // the default
        Assert.Equal("default_user_store", licenses.Received.UserStoreId);          // the default
        Assert.Equal(VendorCatalog.GeminiEnterprise.Id, source.VendorId);
    }

    [Fact]
    public void TheSource_SaysAnEmptyLicenceListIsARealAnswer() =>
        Assert.True(new GeminiEnterpriseRosterSource(new FakeSecretStore(), new FakeLicenses()).CanBeEmpty);

    [Fact]
    public async Task TheSource_AMissingCredential_FailsWithoutEchoingAnything()
    {
        var source = new GeminiEnterpriseRosterSource(new FakeSecretStore(), new FakeLicenses());

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => source.FetchMembersAsync("t1", TestContext.Current.CancellationToken));

        Assert.Contains("No Gemini Enterprise credential", ex.Message);
    }

    [Fact]
    public void ACredentialStoredBeforeTheRoster_StillReadsForBilling_WithDefaultsForTheNewSettings()
    {
        var credential = JsonSerializer.Deserialize<GeminiCredential>(BillingOnlyJson)!;

        Assert.False(credential.HasLicenseSettings);
        Assert.Equal("billing", credential.BillingProjectId);
        Assert.Equal("global", credential.DiscoveryEngineLocation);
    }

    // ---- what the roster means for seats ----

    [Theory]
    [InlineData("ASSIGNED", true)]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("NO_LICENSE", false)]
    [InlineData("no_license", false)]
    [InlineData("NO_LICENSE_ATTEMPTED_LOGIN", false)]
    [InlineData("BLOCKED", false)]
    [InlineData("UNASSIGNED", false)]
    [InlineData("LICENSE_ASSIGNMENT_STATE_UNSPECIFIED", false)]
    [InlineData("standard-user", true)]   // another vendor's seat type is a seat
    public void OnlyAStateThatSaysNoLicenceIsNotASeat(string? seatType, bool holdsSeat) =>
        Assert.Equal(holdsSeat, RosterSeatTypes.HoldsSeat(seatType));

    private const string BillingOnlyJson =
        """{ "ServiceAccountJson": "PRIVATE-KEY-MATERIAL", "BillingProjectId": "billing", "BillingDatasetId": "d", "BillingTableId": "t" }""";

    private const string WithProjectJson =
        """{ "ServiceAccountJson": "PRIVATE-KEY-MATERIAL", "BillingProjectId": "billing", "BillingDatasetId": "d", "BillingTableId": "t", "DiscoveryEngineProjectId": "proj-1" }""";

    private static HttpGeminiUserLicenseRepository Repository(WireMockServer server) =>
        new(new HttpClient { BaseAddress = new Uri(server.Url + "/") }, new FakeToken(), NullLogger<HttpGeminiUserLicenseRepository>.Instance);

    private static void Respond(WireMockServer server, string body) =>
        server.Given(Request.Create().WithPath(ListPath).UsingGet()).RespondWith(Json(body));

    private static IResponseBuilder Json(string body) =>
        Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/json").WithBody(body);

    private sealed class FakeToken : IGeminiAccessTokenProvider
    {
        public Task<string> GetAccessTokenAsync(GeminiCredential credential, CancellationToken cancellationToken = default) =>
            Task.FromResult("fake-token");
    }

    private sealed class FakeLicenses : IGeminiUserLicenseRepository
    {
        public GeminiCredential? Received { get; private set; }

        public Task<IReadOnlyList<VendorMember>> ListUserLicensesAsync(GeminiCredential credential, CancellationToken cancellationToken = default)
        {
            Received = credential;
            return Task.FromResult<IReadOnlyList<VendorMember>>([new VendorMember { Id = "a@example.test", SeatType = "ASSIGNED" }]);
        }
    }
}
