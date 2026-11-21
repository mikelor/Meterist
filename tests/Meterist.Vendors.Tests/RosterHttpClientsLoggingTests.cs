using System.Net;
using System.Text;
using Meterist.Core.Http;
using Meterist.Core.Vendors;
using Meterist.Vendors.ChatGptEnterprise;
using Meterist.Vendors.ClaudeEnterprise;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Logging;

namespace Meterist.Vendors.Tests;

/// <summary>
/// The member-list clients paginate with a cursor that is a member id, so their URLs must stay out of logs. These
/// build the real registrations (<see cref="RosterHttpClients"/>) with logging at Trace for every category and a fake
/// primary handler (no network), and assert on every line written. An ordinary client in the same setup is the
/// control: it proves request logging is switched on, so a silent roster client means its loggers were removed rather
/// than that nothing was being logged.
/// </summary>
public class RosterHttpClientsLoggingTests
{
    private const string Cursor = "user_CURSOR_ID_SECRET";
    private static readonly string[] Secrets =
        [Cursor, "user_FIRST", "user_SECOND", "first@example.test", "second@example.test", "First Person", "Second Person", "the-admin-key", "after="];

    [Fact]
    public async Task TheChatGptRosterClient_WritesNoRequestLogAtAll_AndNothingPersonal()
    {
        var logs = new CapturingProvider();
        var handler = new PagedHandler(ChatGptPage1, ChatGptPage2);
        using var provider = Build(logs, services => services.AddChatGptRosterHttpClient(), handler);

        var members = await provider.GetRequiredService<IChatGptWorkspaceUserRepository>().ListUsersAsync(
            new ChatGptCredential { OrganizationId = "org-x", AdminApiKey = "the-admin-key", WorkspaceId = "11111111-2222-3333-4444-555555555555" },
            TestContext.Current.CancellationToken);

        Assert.Equal([true, true], handler.Marked);   // every request, the cursor one included, is marked sensitive
        Assert.Equal(2, members.Count);   // both pages were really fetched, so the second request (the cursor one) happened
        AssertNothingLeaked(logs);
        Assert.DoesNotContain(logs.Lines, l => l.Category.StartsWith("System.Net.Http.HttpClient", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheClaudeRosterClient_WritesNoRequestLogAtAll_AndNothingPersonal()
    {
        var logs = new CapturingProvider();
        var handler = new PagedHandler(ClaudePage1, ClaudePage2);
        using var provider = Build(logs, services => services.AddClaudeRosterHttpClient(), handler);

        var members = await provider.GetRequiredService<IClaudeMemberRosterRepository>()
            .ListMembersAsync("the-admin-key", TestContext.Current.CancellationToken);

        Assert.Equal([true, true], handler.Marked);
        Assert.Equal(2, members.Count);
        AssertNothingLeaked(logs);
        Assert.DoesNotContain(logs.Lines, l => l.Category.StartsWith("System.Net.Http.HttpClient", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheChatGptSpendControlsClient_WritesNoRequestLogAtAll_AndNothingPersonal()
    {
        var logs = new CapturingProvider();
        var handler = new PagedHandler(SpendPage1, SpendPage2);
        using var provider = Build(logs, services => services.AddChatGptSpendControlsHttpClient(), handler);

        var limits = await provider.GetRequiredService<IChatGptSpendControlsRepository>().ListUserLimitsAsync(
            new ChatGptCredential { OrganizationId = "org-x", AdminApiKey = "the-admin-key", WorkspaceId = "11111111-2222-3333-4444-555555555555" },
            TestContext.Current.CancellationToken);

        Assert.Equal([true, true], handler.Marked);
        Assert.Equal(2, limits.Count);
        AssertNothingLeaked(logs);
        Assert.DoesNotContain(logs.Lines, l => l.Category.StartsWith("System.Net.Http.HttpClient", StringComparison.Ordinal));
    }
    [Fact]
    public async Task TheClaudeUserActivityClient_WritesNoRequestLogAtAll_AndNothingPersonal()
    {
        // Its cursor is base64 of a JSON object holding a member's email, so the URL after page one is personal data.
        var logs = new CapturingProvider();
        var handler = new PagedHandler(ActivityPage1, ActivityPage2);
        using var provider = Build(logs, services => services.AddClaudeUserActivityHttpClient(), handler);

        var rows = await provider.GetRequiredService<IClaudeUserActivityRepository>()
            .QueryDayAsync("the-admin-key", new DateOnly(2026, 11, 18), TestContext.Current.CancellationToken);

        Assert.Equal(2, rows!.Count);
        Assert.Equal([true, true], handler.Marked);
        AssertNothingLeaked(logs);
        Assert.DoesNotContain(logs.Lines, l => l.Category.StartsWith("System.Net.Http.HttpClient", StringComparison.Ordinal));
    }
    [Fact]
    public async Task Control_AnOrdinaryClientInTheSameSetup_DoesLogItsRequests()
    {
        var logs = new CapturingProvider();
        using var provider = Build(
            logs,
            services => services.AddHttpClient<IChatGptWorkspaceUserRepository, HttpChatGptWorkspaceUserRepository>(
                c => c.BaseAddress = new Uri("https://api.chatgpt.com/")),
            new PagedHandler(ChatGptPage1, ChatGptPage2));

        await provider.GetRequiredService<IChatGptWorkspaceUserRepository>().ListUsersAsync(
            new ChatGptCredential { OrganizationId = "org-x", AdminApiKey = "the-admin-key", WorkspaceId = "11111111-2222-3333-4444-555555555555" },
            TestContext.Current.CancellationToken);

        // Without RemoveAllLoggers the factory writes a line per request, naming the URL. (Its default redaction of the
        // query string may hide the cursor on this runtime, but that is a framework default that a switch can turn
        // off; the point of the roster registration is not to depend on it.)
        var httpLines = logs.Lines.Where(l => l.Category.StartsWith("System.Net.Http.HttpClient", StringComparison.Ordinal)).ToList();
        Assert.NotEmpty(httpLines);
        Assert.Contains(httpLines, l => l.Message.Contains("/users", StringComparison.Ordinal));
    }

    private static void AssertNothingLeaked(CapturingProvider logs)
    {
        Assert.NotEmpty(logs.Lines);   // the repository does log (page numbers, counts): the check is not vacuous
        foreach (var line in logs.Lines)
        {
            foreach (var secret in Secrets)
            {
                Assert.False(
                    line.Message.Contains(secret, StringComparison.Ordinal),
                    $"A log line from '{line.Category}' contains '{secret}': {line.Message}");
            }

            Assert.DoesNotContain("/users", line.Message, StringComparison.Ordinal);
        }
    }

    private static ServiceProvider Build(CapturingProvider logs, Action<IServiceCollection> register, HttpMessageHandler primary)
    {
        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Trace).AddProvider(logs));
        register(services);
        // No network: every client's primary handler is the fake.
        services.ConfigureAll<HttpClientFactoryOptions>(o => o.HttpMessageHandlerBuilderActions.Add(b => b.PrimaryHandler = primary));
        return services.BuildServiceProvider();
    }

    private const string ChatGptPage1 =
        $$"""{ "data": [ { "id": "user_FIRST", "email": "first@example.test", "name": "First Person", "role": "member" } ], "has_more": true, "last_id": "{{Cursor}}" }""";

    private const string ChatGptPage2 =
        """{ "data": [ { "id": "user_SECOND", "email": "second@example.test", "name": "Second Person", "role": "owner" } ], "has_more": false, "last_id": "user_SECOND" }""";

    private const string SpendPage1 =
        $$"""{ "data": [ { "id": "user_FIRST", "email": "first@example.test", "name": "First Person", "current_month_usage": 1 } ], "has_more": true, "cursor": "{{Cursor}}" }""";

    private const string SpendPage2 =
        """{ "data": [ { "id": "user_SECOND", "email": "second@example.test", "name": "Second Person", "current_month_usage": 2 } ], "has_more": false, "cursor": null }""";
    private const string ActivityPage1 =
        $$"""{ "data": [ { "user": { "id": "user_FIRST", "email_address": "first@example.test" }, "chat_metrics": { "message_count": 1 } } ], "next_page": "{{Cursor}}" }""";

    private const string ActivityPage2 =
        """{ "data": [ { "user": { "id": "user_SECOND", "email_address": "second@example.test" }, "chat_metrics": { "message_count": 2 } } ], "next_page": null }""";
    private const string ClaudePage1 = ChatGptPage1;

    private const string ClaudePage2 = ChatGptPage2;

    /// <summary>First request gets page 1, any later one page 2 (the request that carries the cursor).</summary>
    private sealed class PagedHandler(string first, string second) : HttpMessageHandler
    {
        private int _calls;

        public List<bool> Marked { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Marked.Add(SensitiveRequest.IsMarked(request));
            var body = Interlocked.Increment(ref _calls) == 1 ? first : second;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }

    private sealed class CapturingProvider : ILoggerProvider
    {
        private readonly List<(string Category, string Message)> _lines = [];

        public IReadOnlyList<(string Category, string Message)> Lines { get { lock (_lines) { return [.. _lines]; } } }

        public ILogger CreateLogger(string categoryName) => new Logger(categoryName, this);

        public void Dispose()
        {
        }

        private sealed class Logger(string category, CapturingProvider owner) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                lock (owner._lines)
                {
                    owner._lines.Add((category, formatter(state, exception)));
                }
            }
        }
    }
}
