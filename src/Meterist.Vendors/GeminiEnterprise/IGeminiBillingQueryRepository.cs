using Meterist.Core.Vendors;

namespace Meterist.Vendors.GeminiEnterprise;

/// <summary>
/// Seam between GeminiEnterpriseSpendExtractor and BigQuery, per
/// docs/architecture.md §11: real access on one side (BigQueryGeminiBillingRepository),
/// an in-memory fake on the other for tests — BigQuery's SQL/gRPC client isn't a
/// sensible thing to mock directly.
/// </summary>
public interface IGeminiBillingQueryRepository
{
    /// <summary>
    /// Returns one row per billing line matching the tenant's Gemini Enterprise
    /// export within the period, keyed by <see cref="GeminiBillingRowFields"/>.
    /// </summary>
    Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> QueryBillingRowsAsync(
        GeminiCredential credential,
        DateRange period,
        CancellationToken cancellationToken = default);
}
