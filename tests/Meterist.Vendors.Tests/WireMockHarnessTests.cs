using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Meterist.Vendors.Tests;

/// <summary>
/// Proves the WireMock.Net harness itself works end-to-end (spin up a fake
/// server, stub a response, call it with a real HttpClient) before any real
/// vendor adapter is implemented against it. See docs/architecture.md §11.
/// </summary>
public class WireMockHarnessTests
{
    [Fact]
    public async Task StubbedEndpoint_ReturnsConfiguredJsonResponse()
    {
        using var server = WireMockServer.Start();

        server
            .Given(Request.Create().WithPath("/v1/organizations/cost_report").UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody("""{"data":[{"amount":"4250"}]}"""));

        using var client = new HttpClient();
        var response = await client.GetAsync(
            $"{server.Urls[0]}/v1/organizations/cost_report", TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("4250", body);
    }
}
