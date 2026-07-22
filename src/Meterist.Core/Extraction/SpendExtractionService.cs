using Meterist.Core.Models;
using Meterist.Core.Persistence;
using Meterist.Core.Vendors;

namespace Meterist.Core.Extraction;

/// <summary>
/// Orchestrates extract → persist-raw → normalize → persist-canonical for
/// one tenant across all registered vendors (or one, if filtered), over an
/// arbitrary date range. Raw is persisted before normalization runs, so a
/// normalization bug doesn't lose the underlying pull. NotImplementedException
/// is classified separately from any other failure — see VendorExtractionResult.
/// </summary>
public sealed class SpendExtractionService
{
    private readonly IEnumerable<IVendorSpendExtractor> _extractors;
    private readonly IEnumerable<IVendorSpendNormalizer> _normalizers;
    private readonly IRawExtractionRepository _rawExtractionRepository;
    private readonly IDailySpendRepository _dailySpendRepository;

    public SpendExtractionService(
        IEnumerable<IVendorSpendExtractor> extractors,
        IEnumerable<IVendorSpendNormalizer> normalizers,
        IRawExtractionRepository rawExtractionRepository,
        IDailySpendRepository dailySpendRepository)
    {
        _extractors = extractors;
        _normalizers = normalizers;
        _rawExtractionRepository = rawExtractionRepository;
        _dailySpendRepository = dailySpendRepository;
    }

    public async Task<IReadOnlyList<VendorExtractionResult>> ExtractAsync(
        string tenantId,
        DateRange period,
        Guid? vendorFilter = null,
        CancellationToken cancellationToken = default)
    {
        var extractorsToRun = vendorFilter is null
            ? _extractors
            : _extractors.Where(e => e.VendorId == vendorFilter.Value);

        var results = new List<VendorExtractionResult>();

        foreach (var extractor in extractorsToRun)
        {
            var displayName = VendorCatalog.FindById(extractor.VendorId)?.DisplayName
                ?? extractor.VendorId.ToString();

            results.Add(await ExtractOneVendorAsync(
                extractor, displayName, tenantId, period, cancellationToken).ConfigureAwait(false));
        }

        return results;
    }

    private async Task<VendorExtractionResult> ExtractOneVendorAsync(
        IVendorSpendExtractor extractor,
        string displayName,
        string tenantId,
        DateRange period,
        CancellationToken cancellationToken)
    {
        try
        {
            var rawData = await extractor.ExtractAsync(tenantId, period, cancellationToken).ConfigureAwait(false);

            // Raw persisted first so it survives even if normalization throws.
            await _rawExtractionRepository.UpsertAsync(
                tenantId, extractor.VendorId, rawData.RecordsByDate, DateTime.UtcNow, cancellationToken)
                .ConfigureAwait(false);

            // Resolved only after a successful extraction: a NotImplementedException
            // thrown by the extractor itself (the common case for a not-yet-built
            // vendor) must be classified as NotImplemented below, not masked by this
            // check running first and reporting a generic "no normalizer" failure.
            var normalizer = _normalizers.FirstOrDefault(n => n.VendorId == extractor.VendorId);
            if (normalizer is null)
            {
                return new VendorExtractionResult(
                    extractor.VendorId, displayName, VendorExtractionStatus.Failed, 0,
                    "Extractor succeeded but no normalizer is registered for this vendor.");
            }

            // No rate resolution yet — deliberate scope cut, see VendorRateConfig's
            // doc comment. No v1 vendor's normalizer needs this list populated.
            var records = normalizer.Normalize(rawData, []);
            await _dailySpendRepository.UpsertAsync(records, cancellationToken).ConfigureAwait(false);

            return new VendorExtractionResult(
                extractor.VendorId, displayName, VendorExtractionStatus.Succeeded, records.Count, null);
        }
        catch (NotImplementedException ex)
        {
            return new VendorExtractionResult(
                extractor.VendorId, displayName, VendorExtractionStatus.NotImplemented, 0, ex.Message);
        }
        catch (Exception ex)
        {
            return new VendorExtractionResult(
                extractor.VendorId, displayName, VendorExtractionStatus.Failed, 0, ex.Message);
        }
    }
}
