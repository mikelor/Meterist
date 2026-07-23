using Meterist.Core.Models;
using Meterist.Core.Persistence;
using Meterist.Core.Vendors;
using Microsoft.Extensions.Logging;

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
    private readonly IVendorRateConfigRepository _vendorRateConfigRepository;
    private readonly ILogger<SpendExtractionService> _logger;

    public SpendExtractionService(
        IEnumerable<IVendorSpendExtractor> extractors,
        IEnumerable<IVendorSpendNormalizer> normalizers,
        IRawExtractionRepository rawExtractionRepository,
        IDailySpendRepository dailySpendRepository,
        IVendorRateConfigRepository vendorRateConfigRepository,
        ILogger<SpendExtractionService> logger)
    {
        _extractors = extractors;
        _normalizers = normalizers;
        _rawExtractionRepository = rawExtractionRepository;
        _dailySpendRepository = dailySpendRepository;
        _vendorRateConfigRepository = vendorRateConfigRepository;
        _logger = logger;
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
        _logger.LogDebug(
            "[{Vendor}] Starting extraction for tenant '{TenantId}', {PeriodStart} to {PeriodEnd}.",
            displayName, tenantId, period.Start, period.End);

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
                _logger.LogWarning(
                    "[{Vendor}] Extraction succeeded but no normalizer is registered for this vendor.", displayName);
                return new VendorExtractionResult(
                    extractor.VendorId, displayName, VendorExtractionStatus.Failed, 0,
                    "Extractor succeeded but no normalizer is registered for this vendor.");
            }

            var applicableRates = await _vendorRateConfigRepository
                .GetApplicableRatesAsync(tenantId, extractor.VendorId, period, cancellationToken)
                .ConfigureAwait(false);
            var records = normalizer.Normalize(rawData, applicableRates);
            await _dailySpendRepository.UpsertAsync(records, cancellationToken).ConfigureAwait(false);

            _logger.LogDebug(
                "[{Vendor}] Succeeded: wrote {RecordCount} DailySpendRecord(s) for tenant '{TenantId}'.",
                displayName, records.Count, tenantId);

            return new VendorExtractionResult(
                extractor.VendorId, displayName, VendorExtractionStatus.Succeeded, records.Count, null);
        }
        catch (NotImplementedException ex)
        {
            _logger.LogDebug("[{Vendor}] Not implemented: {Message}", displayName, ex.Message);
            return new VendorExtractionResult(
                extractor.VendorId, displayName, VendorExtractionStatus.NotImplemented, 0, ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[{Vendor}] Extraction failed for tenant '{TenantId}'.", displayName, tenantId);
            return new VendorExtractionResult(
                extractor.VendorId, displayName, VendorExtractionStatus.Failed, 0, ex.Message);
        }
    }
}
