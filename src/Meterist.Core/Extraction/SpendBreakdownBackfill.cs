using Meterist.Core.Persistence;
using Meterist.Core.Vendors;

namespace Meterist.Core.Extraction;

/// <summary>What a breakdown backfill read and what it stored (or, for a dry run, would store).</summary>
public sealed record SpendBreakdownBackfillResult(int DaysRead, int RowsNormalized, BreakdownReplaceResult Replace);

/// <summary>
/// Rebuilds the per-kind spend breakdown from raw extraction records already in the database, so history is
/// populated without calling the vendor's API again. It runs the vendor's
/// <see cref="IVendorSpendBreakdownNormalizer"/> over the stored raw rows, exactly as an extraction would, and
/// replaces the breakdown for the days that have raw records. Safe to repeat.
/// <para>
/// Only as complete as the raw rows are: raw storage keeps the latest pull per day, so a day pulled before a
/// vendor's query learned to return a charge (for Gemini, tax and support lines) has none of it. Re-running
/// extract for those days fetches it. Days with no stored raw record are not touched.
/// </para>
/// </summary>
public sealed class SpendBreakdownBackfill
{
    private readonly IRawExtractionRepository _rawExtractionRepository;
    private readonly IEnumerable<IVendorSpendBreakdownNormalizer> _normalizers;
    private readonly IDailySpendBreakdownRepository _breakdownRepository;

    public SpendBreakdownBackfill(
        IRawExtractionRepository rawExtractionRepository,
        IEnumerable<IVendorSpendBreakdownNormalizer> normalizers,
        IDailySpendBreakdownRepository breakdownRepository)
    {
        _rawExtractionRepository = rawExtractionRepository;
        _normalizers = normalizers;
        _breakdownRepository = breakdownRepository;
    }

    /// <exception cref="InvalidOperationException">The vendor has no breakdown normalizer.</exception>
    public async Task<SpendBreakdownBackfillResult> RunAsync(
        string tenantId, Guid vendorId, DateRange period, bool dryRun, CancellationToken cancellationToken = default)
    {
        var normalizer = _normalizers.FirstOrDefault(n => n.VendorId == vendorId)
            ?? throw new InvalidOperationException(
                $"{VendorCatalog.FindById(vendorId)?.DisplayName ?? vendorId.ToString()} has no spend breakdown normalizer, "
                + "so there is nothing to backfill.");

        var stored = await _rawExtractionRepository
            .GetRecordsJsonAsync(tenantId, vendorId, period, cancellationToken)
            .ConfigureAwait(false);

        var rawData = new RawVendorSpendData
        {
            VendorId = vendorId,
            TenantId = tenantId,
            Period = period,
            RecordsByDate = stored.ToDictionary(kvp => kvp.Key, kvp => UserUsageBackfill.ParseRows(kvp.Value)),
        };

        var rows = normalizer.Normalize(rawData);

        // Only the days that have a raw record are covered: a day with none is unknown, not empty.
        var days = stored.Keys.ToList();

        var replace = dryRun
            ? await _breakdownRepository.PlanReplaceAsync(tenantId, vendorId, days, rows, cancellationToken).ConfigureAwait(false)
            : await _breakdownRepository.ReplaceAsync(tenantId, vendorId, days, rows, cancellationToken).ConfigureAwait(false);

        return new SpendBreakdownBackfillResult(stored.Count, rows.Count, replace);
    }
}
