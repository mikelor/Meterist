using Meterist.Core.Vendors;
using Meterist.Vendors.ChatGptEnterprise;

namespace Meterist.Vendors.Tests.ChatGptEnterprise;

internal sealed class FakeChatGptCostLogRepository(
    IReadOnlyList<IReadOnlyDictionary<string, object?>> rowsToReturn, DateOnly? effectiveStartOverride = null)
    : IChatGptCostLogRepository
{
    public FakeChatGptCostLogRepository() : this([])
    {
    }

    public ChatGptCredential? ReceivedCredential { get; private set; }

    public DateRange? ReceivedPeriod { get; private set; }

    public Task<ChatGptCostQueryResult> QueryCostRowsAsync(
        ChatGptCredential credential, DateRange period, CancellationToken cancellationToken = default)
    {
        ReceivedCredential = credential;
        ReceivedPeriod = period;
        return Task.FromResult(new ChatGptCostQueryResult(effectiveStartOverride ?? period.Start, rowsToReturn));
    }
}
