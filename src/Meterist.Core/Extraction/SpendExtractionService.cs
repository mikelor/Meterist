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
    private readonly IEnumerable<IVendorUserUsageNormalizer> _userUsageNormalizers;
    private readonly IUserDailyUsageRepository? _userDailyUsageRepository;
    private readonly IEnumerable<IVendorSpendBreakdownNormalizer> _breakdownNormalizers;
    private readonly IDailySpendBreakdownRepository? _breakdownRepository;
    private readonly IEnumerable<IVendorActivitySummarySource> _activitySources;
    private readonly IActivitySummaryRepository? _activityRepository;

    public SpendExtractionService(
        IEnumerable<IVendorSpendExtractor> extractors,
        IEnumerable<IVendorSpendNormalizer> normalizers,
        IRawExtractionRepository rawExtractionRepository,
        IDailySpendRepository dailySpendRepository,
        IVendorRateConfigRepository vendorRateConfigRepository,
        ILogger<SpendExtractionService> logger,
        IEnumerable<IVendorUserUsageNormalizer>? userUsageNormalizers = null,
        IUserDailyUsageRepository? userDailyUsageRepository = null,
        IEnumerable<IVendorSpendBreakdownNormalizer>? breakdownNormalizers = null,
        IDailySpendBreakdownRepository? breakdownRepository = null,
        IEnumerable<IVendorActivitySummarySource>? activitySources = null,
        IActivitySummaryRepository? activityRepository = null)
    {
        _extractors = extractors;
        _normalizers = normalizers;
        _rawExtractionRepository = rawExtractionRepository;
        _dailySpendRepository = dailySpendRepository;
        _vendorRateConfigRepository = vendorRateConfigRepository;
        _logger = logger;
        _userUsageNormalizers = userUsageNormalizers ?? [];
        _userDailyUsageRepository = userDailyUsageRepository;
        _breakdownNormalizers = breakdownNormalizers ?? [];
        _breakdownRepository = breakdownRepository;
        _activitySources = activitySources ?? [];
        _activityRepository = activityRepository;
    }

    /// <param name="dryRun">
    /// When true, vendors are still queried and normalized (read-only), but
    /// nothing is written: raw data is not persisted and daily records are not
    /// upserted. Each result's <see cref="VendorExtractionResult.Changes"/>
    /// then holds what the real run would do.
    /// </param>
    public async Task<IReadOnlyList<VendorExtractionResult>> ExtractAsync(
        string tenantId,
        DateRange period,
        Guid? vendorFilter = null,
        CancellationToken cancellationToken = default,
        bool dryRun = false)
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
                extractor, displayName, tenantId, period, dryRun, cancellationToken).ConfigureAwait(false));
        }

        return results;
    }

    private async Task<VendorExtractionResult> ExtractOneVendorAsync(
        IVendorSpendExtractor extractor,
        string displayName,
        string tenantId,
        DateRange period,
        bool dryRun,
        CancellationToken cancellationToken)
    {
        _logger.LogDebug(
            "[{Vendor}] Starting {Mode} for tenant '{TenantId}', {PeriodStart} to {PeriodEnd}.",
            displayName, dryRun ? "dry-run extraction" : "extraction", tenantId, period.Start, period.End);

        try
        {
            var rawData = await extractor.ExtractAsync(tenantId, period, cancellationToken).ConfigureAwait(false);

            // Raw persisted first so it survives even if normalization throws.
            // A dry run writes nothing, raw data included.
            if (!dryRun)
            {
                await _rawExtractionRepository.UpsertAsync(
                    tenantId, extractor.VendorId, rawData.RecordsByDate, DateTime.UtcNow, cancellationToken)
                    .ConfigureAwait(false);
            }

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

            var changes = dryRun
                ? await _dailySpendRepository
                    .PlanUpsertAsync(records, extractor.RequiresMonotonicUsage, cancellationToken)
                    .ConfigureAwait(false)
                : await _dailySpendRepository
                    .UpsertAsync(records, extractor.RequiresMonotonicUsage, cancellationToken)
                    .ConfigureAwait(false);

            LogKeptHigherUsage(displayName, tenantId, changes, dryRun);

            // Per-user rows go in after the daily totals, which are the source of record and are
            // already written. Counts only: the rows are personal data and are not logged.
            var userUsage = await StoreUserUsageAsync(
                extractor.VendorId, rawData, applicableRates, dryRun, cancellationToken).ConfigureAwait(false);

            _logger.LogDebug(
                "[{Vendor}] Succeeded: {Verb} {RecordCount} DailySpendRecord(s) for tenant '{TenantId}' ({Summary}).",
                displayName, dryRun ? "would write" : "wrote", records.Count, tenantId, changes.Summarize());

            // The per-kind breakdown, replaced for every day the pull covered. Like the per-user rows it
            // follows the daily totals, which are the source of record and are already written.
            var breakdown = await StoreBreakdownAsync(extractor.VendorId, rawData, dryRun, cancellationToken)
                .ConfigureAwait(false);

            // The vendor's own active-user and seat counts, last: like the rows above they follow the daily totals,
            // which are the source of record and are already written. Aggregate counts, nothing personal.
            var activity = await StoreActivitySummariesAsync(tenantId, extractor.VendorId, period, dryRun, cancellationToken)
                .ConfigureAwait(false);

            var detail = (dryRun ? "dry run: " : string.Empty) + changes.Summarize();
            if (userUsage is not null)
            {
                detail += $"; per-user rows: {userUsage.Summarize()}";
            }

            if (breakdown is not null)
            {
                detail += $"; breakdown rows: {breakdown.Summarize()}";
            }

            if (activity is not null)
            {
                detail += $"; activity summaries: {activity.Summarize()}";
            }

            return new VendorExtractionResult(
                extractor.VendorId, displayName, VendorExtractionStatus.Succeeded, records.Count,
                detail, changes, userUsage, breakdown, activity);
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

    // Null when the vendor has no per-user normalizer (or no repository is wired), which is normal.
    private async Task<UserUsageUpsertResult?> StoreUserUsageAsync(
        Guid vendorId,
        RawVendorSpendData rawData,
        IReadOnlyList<VendorRateConfig> applicableRates,
        bool dryRun,
        CancellationToken cancellationToken)
    {
        var normalizer = _userUsageNormalizers.FirstOrDefault(n => n.VendorId == vendorId);
        if (normalizer is null || _userDailyUsageRepository is null)
        {
            return null;
        }

        var rows = normalizer.Normalize(rawData, applicableRates);

        return dryRun
            ? await _userDailyUsageRepository.PlanUpsertAsync(rows, cancellationToken).ConfigureAwait(false)
            : await _userDailyUsageRepository.UpsertAsync(rows, cancellationToken).ConfigureAwait(false);
    }

    // Null when the vendor has no breakdown normalizer (or no repository is wired), which is normal.
    private async Task<BreakdownReplaceResult?> StoreBreakdownAsync(
        Guid vendorId, RawVendorSpendData rawData, bool dryRun, CancellationToken cancellationToken)
    {
        var normalizer = _breakdownNormalizers.FirstOrDefault(n => n.VendorId == vendorId);
        if (normalizer is null || _breakdownRepository is null)
        {
            return null;
        }

        var rows = normalizer.Normalize(rawData);
        var days = rawData.Period.EnumerateDays().ToList();

        return dryRun
            ? await _breakdownRepository
                .PlanReplaceAsync(rawData.TenantId, vendorId, days, rows, cancellationToken).ConfigureAwait(false)
            : await _breakdownRepository
                .ReplaceAsync(rawData.TenantId, vendorId, days, rows, cancellationToken).ConfigureAwait(false);
    }

    // Null when the vendor has no activity-summary source (or no repository is wired), which is normal.
    private async Task<ActivitySummaryUpsertResult?> StoreActivitySummariesAsync(
        string tenantId, Guid vendorId, DateRange period, bool dryRun, CancellationToken cancellationToken)
    {
        var source = _activitySources.FirstOrDefault(s => s.VendorId == vendorId);
        if (source is null || _activityRepository is null)
        {
            return null;
        }

        var rows = await source.FetchAsync(tenantId, period, cancellationToken).ConfigureAwait(false);

        return dryRun
            ? await _activityRepository.PlanUpsertAsync(rows, cancellationToken).ConfigureAwait(false)
            : await _activityRepository.UpsertAsync(rows, cancellationToken).ConfigureAwait(false);
    }

    // The never-lower rule used to operate silently. A day it overrides is a
    // day where the vendor just reported less than we already hold, which is
    // exactly the signal worth seeing, so say so, with the figures.
    private void LogKeptHigherUsage(
        string displayName, string tenantId, IReadOnlyList<DailySpendChange> changes, bool dryRun)
    {
        var kept = changes.Where(c => c.Kind == DailySpendChangeKind.KeptHigherUsage).ToList();
        if (kept.Count == 0)
        {
            return;
        }

        const int Shown = 5;
        var examples = string.Join("; ", kept.Take(Shown).Select(c =>
            $"{c.Date:yyyy-MM-dd} kept {c.Stored.UsageOrOverage:F2}, pulled {c.Incoming.UsageOrOverage:F2}"));

        _logger.LogWarning(
            "[{Vendor}] {Mode}: {Count} day(s) for tenant '{TenantId}' kept their higher stored usage rather than "
            + "the lower figure just pulled (usage is never lowered for this vendor): {Examples}{More}",
            displayName, dryRun ? "Dry run" : "Extraction", kept.Count, tenantId, examples,
            kept.Count > Shown ? $"; and {kept.Count - Shown} more" : string.Empty);
    }
}
