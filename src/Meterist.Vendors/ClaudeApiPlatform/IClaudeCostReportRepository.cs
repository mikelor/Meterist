using Meterist.Core.Vendors;

namespace Meterist.Vendors.ClaudeApiPlatform;

/// <summary>
/// Seam between ClaudeApiPlatformSpendExtractor and the Anthropic Admin API's
/// Cost Report endpoint, per docs/architecture.md §11: real access on one
/// side (HttpClaudeCostReportRepository), a WireMock.Net stub on the other.
/// </summary>
public interface IClaudeCostReportRepository
{
    /// <summary>
    /// Returns one row per cost line item across all paginated time buckets
    /// in the period, keyed by <see cref="ClaudeApiCostRowFields"/>.
    /// </summary>
    Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> QueryCostRowsAsync(
        string adminApiKey, DateRange period, CancellationToken cancellationToken = default);
}
