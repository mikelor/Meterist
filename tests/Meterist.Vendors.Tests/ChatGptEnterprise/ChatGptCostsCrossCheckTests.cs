using System.Text.Json;
using Meterist.Core.Models;
using Meterist.Core.Vendors;
using Meterist.Vendors.ChatGptEnterprise;
using Meterist.Vendors.Tests.GeminiEnterprise;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Meterist.Vendors.Tests.ChatGptEnterprise;

/// <summary>
/// The settings that keep the COSTS cross-check affordable for a daily run (issue #70): bounded concurrent file downloads that
/// give exactly the rows the sequential loop gave, a last-N-days window, an off switch, and a run-output note for each.
/// None of it may change what the Daily Usage source of record stores.
/// </summary>
public class ChatGptCostsCrossCheckTests
{
    private const string OrganizationId = "org-test";
    private static readonly ChatGptCredential Credential = new() { OrganizationId = OrganizationId, AdminApiKey = "test-admin-key" };

    // ---- the repository: concurrent downloads ----

    [Fact]
    public async Task Download_FetchesFilesAFewAtATime_NeverMoreThanTheSetting()
    {
        using var server = WireMockServer.Start();
        const int files = 12;
        RegisterFiles(server, files);
        var factory = new CountingFactory();

        var repository = Repository(server, factory, parallelism: 4);
        var result = await repository.QueryCostRowsAsync(Credential, WholeJuly, TestContext.Current.CancellationToken);

        Assert.Equal(files, result.FileCount);
        Assert.Equal(files, result.Rows.Count);
        Assert.True(factory.MaxInFlight > 1, $"expected overlapping downloads, saw {factory.MaxInFlight} at a time");
        Assert.True(factory.MaxInFlight <= 4, $"expected at most 4 at a time, saw {factory.MaxInFlight}");
    }

    [Fact]
    public async Task Download_WithOneAtATime_IsTheOldSequentialBehaviour()
    {
        using var server = WireMockServer.Start();
        RegisterFiles(server, 6);
        var factory = new CountingFactory();

        await Repository(server, factory, parallelism: 1).QueryCostRowsAsync(Credential, WholeJuly, TestContext.Current.CancellationToken);

        Assert.Equal(1, factory.MaxInFlight);
    }

    [Fact]
    public async Task Download_GivesTheSameRowsInTheSameOrder_WhateverTheParallelism_IncludingWhichDuplicateWins()
    {
        using var server = WireMockServer.Start();
        RegisterFiles(server, 10);
        // The same event id appears in file 2 and file 7 with a different SKU: the earlier file in listing order must win.
        RegisterFile(server, 2, [Line("dup", "2026-07-10", "FIRST-WINS")]);
        RegisterFile(server, 7, [Line("dup", "2026-07-10", "LOSES")]);

        var sequential = await Repository(server, new CountingFactory(), 1).QueryCostRowsAsync(Credential, WholeJuly, TestContext.Current.CancellationToken);
        var parallel = await Repository(server, new CountingFactory(), 8).QueryCostRowsAsync(Credential, WholeJuly, TestContext.Current.CancellationToken);

        static string[] Key(ChatGptCostQueryResult r) =>
            r.Rows.Select(x => $"{x[ChatGptCostRowFields.EventId]}|{x[ChatGptCostRowFields.Sku]}").ToArray();

        Assert.Equal(Key(sequential), Key(parallel));
        Assert.Contains("dup|FIRST-WINS", Key(parallel));
        Assert.DoesNotContain("dup|LOSES", Key(parallel));
    }

    [Fact]
    public async Task Download_OneFailingFile_FailsTheQuery_NotAPartialResult()
    {
        using var server = WireMockServer.Start();
        RegisterFiles(server, 6);
        server.Given(Request.Create().WithPath("/signed/file-3").UsingGet()).RespondWith(Response.Create().WithStatusCode(500));

        await Assert.ThrowsAnyAsync<Exception>(() =>
            Repository(server, new CountingFactory(), 4).QueryCostRowsAsync(Credential, WholeJuly, TestContext.Current.CancellationToken));
    }

    // ---- the settings ----

    [Fact]
    public void TheWindow_IsAllOfThePeriodByDefault_OrItsLastNDays()
    {
        var period = new DateRange(new DateOnly(2026, 7, 1), new DateOnly(2026, 7, 31));

        Assert.Equal(period, new ChatGptCostsCrossCheckOptions().WindowFor(period));
        Assert.Equal(
            new DateRange(new DateOnly(2026, 7, 25), new DateOnly(2026, 7, 31)),
            new ChatGptCostsCrossCheckOptions { RecentDays = 7 }.WindowFor(period));
        // A period shorter than the window is covered whole, not extended before its start.
        var short3 = new DateRange(new DateOnly(2026, 7, 29), new DateOnly(2026, 7, 31));
        Assert.Equal(short3, new ChatGptCostsCrossCheckOptions { RecentDays = 7 }.WindowFor(short3));
        Assert.Equal(period.End, new ChatGptCostsCrossCheckOptions { RecentDays = 1 }.WindowFor(period).Start);
    }

    [Theory]
    [InlineData(null, 4, false)]
    [InlineData(7, 4, false)]
    [InlineData(29, 16, false)]
    [InlineData(0, 4, true)]
    [InlineData(30, 4, true)]
    [InlineData(-1, 4, true)]
    [InlineData(7, 0, true)]
    [InlineData(7, 17, true)]
    public void BadSettings_AreAnError_NotQuietlyAdjusted(int? recentDays, int parallelism, bool isProblem)
    {
        var options = new ChatGptCostsCrossCheckOptions { RecentDays = recentDays, MaxParallelDownloads = parallelism };

        Assert.Equal(isProblem, options.Problem is not null);
        if (isProblem)
        {
            Assert.Contains("Meterist:ChatGpt:CostsCrossCheck", options.Problem);
        }
    }

    // ---- the extractor ----

    [Fact]
    public async Task ByDefault_TheWholePeriodIsCrossChecked_AndTheRunOutputSaysHowLongItTook()
    {
        var (extractor, costs, _) = await Build(new ChatGptCostsCrossCheckOptions());

        var raw = await extractor.ExtractAsync("t1", Period, TestContext.Current.CancellationToken);

        Assert.Equal(Period, costs.ReceivedPeriod);
        var note = Assert.Single(raw.Notes);
        Assert.Contains("COSTS cross-check: 3 file(s) in 2s", note);
        Assert.Contains("up to 4 at a time", note);
    }

    [Fact]
    public async Task WithARecentWindow_OnlyThoseDaysAreRequested_AndTheNoteSaysSo_AndDailyUsageStillCoversEveryDay()
    {
        var (extractor, costs, usage) = await Build(new ChatGptCostsCrossCheckOptions { RecentDays = 3 });

        var raw = await extractor.ExtractAsync("t1", Period, TestContext.Current.CancellationToken);

        Assert.Equal(new DateRange(new DateOnly(2026, 7, 23), new DateOnly(2026, 7, 25)), costs.ReceivedPeriod);
        Assert.Contains(raw.Notes, n => n.Contains("limited to 2026-07-23 to 2026-07-25"));
        Assert.Equal(Period, usage.ReceivedPeriod);        // the source of record is asked for the whole period, as before
        Assert.Equal(7, raw.RecordsByDate.Count);           // every day still reaches the normalizer
        Assert.Equal(DailySpendSources.DailyUsageApi, raw.PrimarySource);
    }

    [Fact]
    public async Task TurnedOff_TheExportIsNeverRead_TheRunOutputSaysSkipped_AndDailyUsageIsUnaffected()
    {
        var (extractor, costs, usage) = await Build(new ChatGptCostsCrossCheckOptions { Enabled = false });

        var raw = await extractor.ExtractAsync("t1", Period, TestContext.Current.CancellationToken);

        Assert.Equal(0, costs.CallCount);
        var note = Assert.Single(raw.Notes);
        Assert.Contains("skipped", note);
        Assert.Contains("Enabled is false", note);
        Assert.Equal(Period, usage.ReceivedPeriod);
        Assert.DoesNotContain(
            raw.RecordsByDate.Values.SelectMany(rows => rows),
            row => (string?)row.GetValueOrDefault(ChatGptDailyUsageRowFields.Source) == ChatGptRowSources.CostsExport);
        Assert.Equal(DailySpendSources.DailyUsageApi, raw.PrimarySource);
    }

    [Fact]
    public async Task ACrossCheckThatFails_IsNotedInTheRunOutput_AndDoesNotFailTheExtraction()
    {
        var (extractor, _, _) = await Build(new ChatGptCostsCrossCheckOptions(), costsFailure: new HttpRequestException("boom"));

        var raw = await extractor.ExtractAsync("t1", Period, TestContext.Current.CancellationToken);

        Assert.Contains(raw.Notes, n => n.Contains("COSTS cross-check failed"));
        Assert.Equal(7, raw.RecordsByDate.Count);
    }

    [Fact]
    public async Task ABadSetting_FailsBeforeAnyCallIsMade()
    {
        var (extractor, costs, usage) = await Build(new ChatGptCostsCrossCheckOptions { RecentDays = 45 });

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            extractor.ExtractAsync("t1", Period, TestContext.Current.CancellationToken));

        Assert.Contains("RecentDays", ex.Message);
        Assert.Equal(0, costs.CallCount);
        Assert.Null(usage.ReceivedPeriod);
    }

    [Fact]
    public async Task TheSettings_DoNotApplyWhenThereIsNoWorkspaceId_BecauseTheExportIsThenTheOnlySource()
    {
        var secretStore = new FakeSecretStore();
        await secretStore.SetCredentialAsync(
            "t1", VendorCatalog.ChatGptEnterprise.Id,
            JsonSerializer.Serialize(new ChatGptCredential { OrganizationId = "org", AdminApiKey = "key" }),
            TestContext.Current.CancellationToken);
        var costs = new FakeChatGptCostLogRepository();
        var extractor = new ChatGptEnterpriseSpendExtractor(
            secretStore, costs, new FakeChatGptDailyUsageRepository(), NullLogger<ChatGptEnterpriseSpendExtractor>.Instance,
            Options.Create(new ChatGptCostsCrossCheckOptions { Enabled = false, RecentDays = 2 }));

        await extractor.ExtractAsync("t1", Period, TestContext.Current.CancellationToken);

        Assert.Equal(1, costs.CallCount);
        Assert.Equal(Period, costs.ReceivedPeriod);
    }

    // ---- helpers ----

    private static readonly DateRange Period = new(new DateOnly(2026, 7, 19), new DateOnly(2026, 7, 25));
    private static readonly DateRange WholeJuly = new(new DateOnly(2026, 7, 1), new DateOnly(2026, 7, 31));

    private static async Task<(ChatGptEnterpriseSpendExtractor Extractor, TimedCosts Costs, FakeChatGptDailyUsageRepository Usage)> Build(
        ChatGptCostsCrossCheckOptions options, Exception? costsFailure = null)
    {
        var secretStore = new FakeSecretStore();
        await secretStore.SetCredentialAsync(
            "t1", VendorCatalog.ChatGptEnterprise.Id,
            JsonSerializer.Serialize(new ChatGptCredential
            {
                OrganizationId = "org", AdminApiKey = "key", WorkspaceId = "11111111-2222-3333-4444-555555555555",
            }),
            TestContext.Current.CancellationToken);
        var costs = new TimedCosts(costsFailure);
        var usage = new FakeChatGptDailyUsageRepository();
        return (
            new ChatGptEnterpriseSpendExtractor(
                secretStore, costs, usage, NullLogger<ChatGptEnterpriseSpendExtractor>.Instance, Options.Create(options)),
            costs, usage);
    }

    private static ChatGptCostsCrossCheckOptions Opt(int parallelism) => new() { MaxParallelDownloads = parallelism };

    private static HttpChatGptCostLogRepository Repository(WireMockServer server, IHttpClientFactory factory, int parallelism) =>
        new(
            new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { BaseAddress = new Uri(server.Url + "/") },
            factory, NullLogger<HttpChatGptCostLogRepository>.Instance, Options.Create(Opt(parallelism)));

    private static void RegisterFiles(WireMockServer server, int count)
    {
        var data = string.Join(",", Enumerable.Range(1, count).Select(i =>
            $$"""{"id":"file-{{i}}","event_type":"COSTS","end_time":"2026-07-10T00:00:00Z","file_name":"f{{i}}.jsonl","file_size":10,"file_sha256":"x"}"""));
        server.Given(Request.Create().WithPath($"/v1/compliance/organizations/{OrganizationId}/logs").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/json")
                .WithBody($$"""{"data":[{{data}}],"has_more":false,"last_end_time":null}"""));

        for (var i = 1; i <= count; i++)
        {
            RegisterFile(server, i, [Line($"evt-{i}", "2026-07-10", $"Sku{i}")]);
        }
    }

    private static void RegisterFile(WireMockServer server, int index, IEnumerable<string> lines)
    {
        server.Given(Request.Create().WithPath($"/v1/compliance/organizations/{OrganizationId}/logs/file-{index}").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(307).WithHeader("Location", $"{server.Url}/signed/file-{index}"));
        server.Given(Request.Create().WithPath($"/signed/file-{index}").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithDelay(40).WithBody(string.Join("\n", lines)));
    }

    private static string Line(string eventId, string day, string sku) => JsonSerializer.Serialize(new
    {
        event_id = eventId,
        type = "COSTS",
        payload = new
        {
            day,
            identity = new { user_id = "user-1", email = "a@example.com", name = "A" },
            measures = new { billing = new object[] { new { sku, cost = new { value = 1m, unit = "CREDITS" } } } },
        },
    });

    private sealed class CountingFactory : IHttpClientFactory
    {
        private int _inFlight;
        private int _max;

        public int MaxInFlight => _max;

        public HttpClient CreateClient(string name) => new(new CountingHandler(this, new HttpClientHandler()));

        private sealed class CountingHandler(CountingFactory owner, HttpMessageHandler inner) : DelegatingHandler(inner)
        {
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var now = Interlocked.Increment(ref owner._inFlight);
                int seen;
                while (now > (seen = Volatile.Read(ref owner._max)) && Interlocked.CompareExchange(ref owner._max, now, seen) != seen)
                {
                }

                try
                {
                    return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    Interlocked.Decrement(ref owner._inFlight);
                }
            }
        }
    }

    /// <summary>A cost-log repository that reports three files taking two seconds, or fails.</summary>
    private sealed class TimedCosts(Exception? failure) : IChatGptCostLogRepository
    {
        public int CallCount { get; private set; }

        public DateRange? ReceivedPeriod { get; private set; }

        public Task<ChatGptCostQueryResult> QueryCostRowsAsync(
            ChatGptCredential credential, DateRange period, CancellationToken cancellationToken = default)
        {
            CallCount++;
            ReceivedPeriod = period;
            return failure is not null
                ? throw failure
                : Task.FromResult(new ChatGptCostQueryResult(period.Start, [], 3, TimeSpan.FromSeconds(2)));
        }
    }
}
