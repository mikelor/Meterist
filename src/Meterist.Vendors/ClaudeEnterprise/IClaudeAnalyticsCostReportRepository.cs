using Meterist.Core.Vendors;

namespace Meterist.Vendors.ClaudeEnterprise;

/// <summary>
/// Seam between ClaudeEnterpriseSpendExtractor and the Claude Enterprise
/// Analytics API's user_cost_report endpoint, per docs/architecture.md §11:
/// real access on one side (HttpClaudeAnalyticsCostReportRepository), a
/// WireMock.Net stub on the other.
/// </summary>
public interface IClaudeAnalyticsCostReportRepository
{
    /// <summary>
    /// Returns one row per (user, day) across the whole period — internally
    /// chunked into &lt;=31-day windows and paginated within each, since the
    /// underlying API caps a single query's span — keyed by
    /// <see cref="ClaudeAnalyticsCostRowFields"/>.
    /// </summary>
    Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> QueryUserCostRowsAsync(
        string analyticsApiKey, DateRange period, CancellationToken cancellationToken = default);
}
