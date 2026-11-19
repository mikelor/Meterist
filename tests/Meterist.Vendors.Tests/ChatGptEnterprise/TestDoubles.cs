using Meterist.Core.Vendors;
using Meterist.Vendors.ChatGptEnterprise;

namespace Meterist.Vendors.Tests.ChatGptEnterprise;

internal sealed class FakeChatGptCostLogRepository(
    IReadOnlyList<IReadOnlyDictionary<string, object?>> rowsToReturn,
    DateOnly? effectiveStartOverride = null,
    Exception? exceptionToThrow = null)
    : IChatGptCostLogRepository
{
    public FakeChatGptCostLogRepository() : this([])
    {
    }

    public ChatGptCredential? ReceivedCredential { get; private set; }

    public DateRange? ReceivedPeriod { get; private set; }

    public int CallCount { get; private set; }

    public Task<ChatGptCostQueryResult> QueryCostRowsAsync(
        ChatGptCredential credential, DateRange period, CancellationToken cancellationToken = default)
    {
        CallCount++;
        ReceivedCredential = credential;
        ReceivedPeriod = period;

        if (exceptionToThrow is not null)
        {
            throw exceptionToThrow;
        }

        return Task.FromResult(new ChatGptCostQueryResult(effectiveStartOverride ?? period.Start, rowsToReturn));
    }
}

internal sealed class FakeChatGptDailyUsageRepository(
    IReadOnlyList<ChatGptDailyUsageRow>? rowsToReturn = null, Exception? exceptionToThrow = null)
    : IChatGptDailyUsageRepository
{
    public ChatGptCredential? ReceivedCredential { get; private set; }

    public DateRange? ReceivedPeriod { get; private set; }

    public ChatGptUsageGrouping? ReceivedGrouping { get; private set; }

    public int CallCount { get; private set; }

    public Task<IReadOnlyList<ChatGptDailyUsageRow>> QueryDailyUsageAsync(
        ChatGptCredential credential,
        DateRange period,
        ChatGptUsageGrouping grouping,
        CancellationToken cancellationToken = default)
    {
        CallCount++;
        ReceivedCredential = credential;
        ReceivedPeriod = period;
        ReceivedGrouping = grouping;

        if (exceptionToThrow is not null)
        {
            throw exceptionToThrow;
        }

        return Task.FromResult(rowsToReturn ?? []);
    }
}

/// <summary>Captures log messages so a test can assert a warning was (or wasn't) raised, and what it said.</summary>
internal sealed class CapturingLogger<T> : Microsoft.Extensions.Logging.ILogger<T>
{
    public List<(Microsoft.Extensions.Logging.LogLevel Level, string Message)> Entries { get; } = [];

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

    public void Log<TState>(
        Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state,
        Exception? exception, Func<TState, Exception?, string> formatter) =>
        Entries.Add((logLevel, formatter(state, exception)));

    public IEnumerable<string> Warnings =>
        Entries.Where(e => e.Level == Microsoft.Extensions.Logging.LogLevel.Warning).Select(e => e.Message);
}
