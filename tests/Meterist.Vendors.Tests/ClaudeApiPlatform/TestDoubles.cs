using Meterist.Core.Vendors;
using Meterist.Vendors.ClaudeApiPlatform;

namespace Meterist.Vendors.Tests.ClaudeApiPlatform;

internal sealed class FakeClaudeCostReportRepository(IReadOnlyList<IReadOnlyDictionary<string, object?>> rowsToReturn)
    : IClaudeCostReportRepository
{
    public FakeClaudeCostReportRepository() : this([])
    {
    }

    public string? ReceivedAdminApiKey { get; private set; }

    public DateRange? ReceivedPeriod { get; private set; }

    public Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> QueryCostRowsAsync(
        string adminApiKey, DateRange period, CancellationToken cancellationToken = default)
    {
        ReceivedAdminApiKey = adminApiKey;
        ReceivedPeriod = period;
        return Task.FromResult(rowsToReturn);
    }
}
