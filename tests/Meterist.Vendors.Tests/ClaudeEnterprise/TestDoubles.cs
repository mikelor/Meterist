using Meterist.Core.Vendors;
using Meterist.Vendors.ClaudeEnterprise;

namespace Meterist.Vendors.Tests.ClaudeEnterprise;

internal sealed class FakeClaudeAnalyticsCostReportRepository(
    IReadOnlyList<IReadOnlyDictionary<string, object?>> rowsToReturn) : IClaudeAnalyticsCostReportRepository
{
    public FakeClaudeAnalyticsCostReportRepository() : this([])
    {
    }

    public string? ReceivedAnalyticsApiKey { get; private set; }

    public DateRange? ReceivedPeriod { get; private set; }

    public Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> QueryUserCostRowsAsync(
        string analyticsApiKey, DateRange period, CancellationToken cancellationToken = default)
    {
        ReceivedAnalyticsApiKey = analyticsApiKey;
        ReceivedPeriod = period;
        return Task.FromResult(rowsToReturn);
    }
}
