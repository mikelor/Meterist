using System.Diagnostics;
using Meterist.Core.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenTelemetry;
using OpenTelemetry.Trace;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Meterist.ServiceDefaults.Tests;

/// <summary>
/// Uses the application's real OpenTelemetry configuration (<c>ConfigureOpenTelemetry</c>) with a capturing
/// processor, so what is asserted is what the exporters would be handed.
/// <para>
/// Found 2026-11-20 on .NET 11: the runtime already redacts the query string of an outgoing request's span
/// (<c>url.full</c> is <c>http://host/users?*</c>), so a pagination cursor is not recorded by default. That is a
/// framework default (switched off by <c>System.Net.Http.DisableUriRedaction</c>), so the roster clients do not rely
/// on it: their requests are marked sensitive and the HTTP instrumentation leaves them out of traces altogether.
/// </para>
/// </summary>
public sealed class SensitiveRequestTracingTests : IAsyncLifetime
{
    private const string Cursor = "user_CURSOR_THAT_IS_A_MEMBER_ID";

    private WireMockServer _server = null!;
    private IHost _host = null!;
    private readonly CapturingProcessor _captured = new();

    public async ValueTask InitializeAsync()
    {
        _server = WireMockServer.Start();
        _server.Given(Request.Create().WithPath("/users").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("{}"));

        var builder = Host.CreateApplicationBuilder();
        builder.ConfigureOpenTelemetry();
        builder.Services.AddOpenTelemetry().WithTracing(tracing => tracing.AddProcessor(_captured));
        _host = builder.Build();
        await _host.StartAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
        _server.Dispose();
    }

    [Fact]
    public async Task AnOrdinaryRequest_IsTraced_AndTheRuntimeRedactsItsQueryString()
    {
        // The baseline the roster protection sits on, and a canary: if the runtime default ever changes (or redaction
        // is switched off for the process) this fails and says the cursor would be exposed on unmarked requests.
        using var client = new HttpClient();

        await client.GetAsync($"{_server.Url}/users?limit=1000&after={Cursor}", TestContext.Current.CancellationToken);
        Flush();

        var url = Assert.Single(_captured.OutgoingUrls);
        Assert.EndsWith("/users?*", url);
        Assert.DoesNotContain(Cursor, url);
    }

    [Fact]
    public async Task ARequestMarkedSensitive_IsLeftOutOfTracesEntirely_NotMerelyRedacted()
    {
        using var client = new HttpClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{_server.Url}/users?limit=1000&after={Cursor}");
        request.MarkSensitive();

        await client.SendAsync(request, TestContext.Current.CancellationToken);
        Flush();

        Assert.Empty(_captured.OutgoingUrls);
        Assert.DoesNotContain(_captured.AllTagValues, value => value.Contains(Cursor));
    }

    [Fact]
    public async Task OnlyTheMarkedRequestIsDropped_AnUnmarkedOneInTheSameRunIsStillTraced()
    {
        using var client = new HttpClient();
        using var marked = new HttpRequestMessage(HttpMethod.Get, $"{_server.Url}/users?after={Cursor}");
        marked.MarkSensitive();

        await client.SendAsync(marked, TestContext.Current.CancellationToken);
        await client.GetAsync($"{_server.Url}/users?limit=5", TestContext.Current.CancellationToken);
        Flush();

        Assert.Single(_captured.OutgoingUrls);
    }

    private void Flush() => _host.Services.GetRequiredService<TracerProvider>().ForceFlush();

    private sealed class CapturingProcessor : BaseProcessor<Activity>
    {
        private readonly object _gate = new();
        private readonly List<string> _outgoing = [];
        private readonly List<string> _tagValues = [];

        /// <summary>The <c>url.full</c> of each outgoing HttpClient span (WireMock's own incoming requests are not HttpClient spans).</summary>
        public IReadOnlyList<string> OutgoingUrls { get { lock (_gate) { return [.. _outgoing]; } } }

        public IReadOnlyList<string> AllTagValues { get { lock (_gate) { return [.. _tagValues]; } } }

        public override void OnEnd(Activity data)
        {
            lock (_gate)
            {
                _tagValues.AddRange(data.TagObjects.Select(t => t.Value?.ToString() ?? string.Empty));

                if (data.Source.Name == "System.Net.Http" && data.GetTagItem("url.full") is string url)
                {
                    _outgoing.Add(url);
                }
            }
        }
    }
}
