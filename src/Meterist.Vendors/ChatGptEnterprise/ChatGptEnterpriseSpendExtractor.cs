using Meterist.Core.Models;
using Meterist.Core.Secrets;
using Meterist.Core.Vendors;
using Microsoft.Extensions.Logging;

namespace Meterist.Vendors.ChatGptEnterprise;

/// <summary>
/// ChatGPT Enterprise. The Daily Usage API is the source of record whenever
/// the tenant's credential has a WorkspaceId: it is a plain query API with
/// months of history, so it can return days the COSTS export has already lost.
/// The COSTS compliance-log export (30-day file retention; can drop or shrink a
/// day still inside its window) is then fetched as a best-effort cross-check
/// and its rows are kept alongside in raw storage, but it never overrides a
/// Daily Usage figure. A tenant with no WorkspaceId falls back to COSTS alone,
/// with a warning. See docs/vendor-integration-reference.md.
/// </summary>
public sealed class ChatGptEnterpriseSpendExtractor : IVendorSpendExtractor
{
    private readonly ISecretStore _secretStore;
    private readonly IChatGptCostLogRepository _costLogRepository;
    private readonly IChatGptDailyUsageRepository _dailyUsageRepository;
    private readonly ILogger<ChatGptEnterpriseSpendExtractor> _logger;

    public ChatGptEnterpriseSpendExtractor(
        ISecretStore secretStore,
        IChatGptCostLogRepository costLogRepository,
        IChatGptDailyUsageRepository dailyUsageRepository,
        ILogger<ChatGptEnterpriseSpendExtractor> logger)
    {
        _secretStore = secretStore;
        _costLogRepository = costLogRepository;
        _dailyUsageRepository = dailyUsageRepository;
        _logger = logger;
    }

    public Guid VendorId => VendorCatalog.ChatGptEnterprise.Id;

    // True for raw credit consumption, which both sources carry. The credit-pool
    // size and contracted overage rate are NOT API-derivable though
    // (console/contract-only) — those must come from tenant-level config, not
    // this extractor.
    public bool SupportsOverage => true;

    // Real vendor-reported per-user dollar cost: COSTS carries payload.identity,
    // and the Daily Usage API can return per-user rows. Unlike Gemini
    // Enterprise, where per-user cost would be a derived estimate.
    public bool SupportsPerUserBreakdown => true;

    // A later pull of a day can legitimately return LESS than an earlier one:
    // the COSTS export can drop rows for a day still nominally inside its
    // 29-day window, and Daily Usage rows for recent days are eventually
    // consistent and can be revised. The daily-spend upsert must never let
    // this vendor's UsageOrOverage decrease on re-extraction, and now logs
    // each day where that rule overrides a pulled figure.
    public bool RequiresMonotonicUsage => true;

    public async Task<RawVendorSpendData> ExtractAsync(
        string tenantId,
        DateRange period,
        CancellationToken cancellationToken = default)
    {
        _logger.LogDebug(
            "Starting ChatGPT Enterprise extraction for tenant '{TenantId}', {PeriodStart} to {PeriodEnd}.",
            tenantId, period.Start, period.End);

        var credential = await LoadCredentialAsync(tenantId, cancellationToken).ConfigureAwait(false);

        _logger.LogDebug(
            "Resolved ChatGPT Enterprise credential for tenant '{TenantId}': organization '{OrganizationId}', "
            + "workspace '{WorkspaceId}'.",
            tenantId, credential.OrganizationId, credential.WorkspaceId ?? "(not set)");

        if (string.IsNullOrWhiteSpace(credential.WorkspaceId))
        {
            _logger.LogWarning(
                "ChatGPT Enterprise tenant '{TenantId}' has no WorkspaceId in its credential, so the Daily Usage API "
                + "cannot be used. Falling back to the COSTS export alone, which reaches back only about 29 days and "
                + "can lose or shrink days. Add WorkspaceId and run 'credentials set' again "
                + "(see docs/user-guide.md, ChatGPT Enterprise).",
                tenantId);

            return await ExtractFromCostsOnlyAsync(tenantId, credential, period, cancellationToken)
                .ConfigureAwait(false);
        }

        return await ExtractFromDailyUsageAsync(tenantId, credential, period, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<RawVendorSpendData> ExtractFromDailyUsageAsync(
        string tenantId, ChatGptCredential credential, DateRange period, CancellationToken cancellationToken)
    {
        // The source of record. Not wrapped in a try: if it fails (a key without
        // the "Enterprise analytics API: Read" permission, a wrong workspace ID,
        // an outage), the extraction fails loudly with a message saying so,
        // rather than quietly degrading to a source known to lose data.
        var usageRows = await _dailyUsageRepository
            .QueryDailyUsageAsync(credential, period, ChatGptUsageGrouping.Workspace, cancellationToken)
            .ConfigureAwait(false);

        var usageByDay = new Dictionary<DateOnly, ChatGptDailyUsageRow>();
        foreach (var row in usageRows)
        {
            if (!usageByDay.TryAdd(row.Day, row))
            {
                throw new InvalidOperationException(
                    $"The ChatGPT Daily Usage API returned more than one workspace-wide row for {row.Day:yyyy-MM-dd}; "
                    + "refusing to guess which is right.");
            }
        }

        // Per-user rows for the same period, kept in raw storage beside the workspace row.
        // Best effort, like the COSTS cross-check: the workspace totals above are the source of
        // record, so a failure here must not fail an extraction they already answered.
        var userRowsByDay = (await TryQueryPerUserAsync(tenantId, credential, period, cancellationToken)
                .ConfigureAwait(false))
            .GroupBy(row => row.Day)
            .ToDictionary(g => g.Key, g => g.ToList());

        var costsResult = await TryQueryCostsAsync(tenantId, credential, period, cancellationToken)
            .ConfigureAwait(false);

        // Only days from the export's effective start onward. The export is listed
        // by file write time, so the day just before its retention cutoff comes
        // back as a sliver of that day, not the whole of it; comparing it to a
        // complete Daily Usage day would always read as a disagreement, and using
        // it as a fallback would record a fraction of the day's usage.
        var costsEffectiveStart = costsResult?.EffectiveStart ?? DateOnly.MinValue;
        var costRowsByDay = (costsResult?.Rows ?? [])
            .Where(row => (DateOnly)row[ChatGptCostRowFields.RecordDate]! >= costsEffectiveStart)
            .GroupBy(row => (DateOnly)row[ChatGptCostRowFields.RecordDate]!)
            .ToDictionary(g => g.Key, g => g.ToList());

        // Every day in the period gets a key. A day with no usage returns no row
        // from either source, but a seat accrues regardless of usage, so it must
        // still reach the normalizer. Unlike the COSTS-only path there is no
        // retention boundary to stop at: the API does not say where its history
        // starts, and a day it has no row for is written as no usage. That is
        // safe for days already stored because usage is never lowered (see
        // RequiresMonotonicUsage), and a day before the history begins at worst
        // gets its seat fee with usage unknown.
        var recordsByDate = new Dictionary<DateOnly, IReadOnlyList<IReadOnlyDictionary<string, object?>>>();
        foreach (var date in period.EnumerateDays())
        {
            var rows = new List<IReadOnlyDictionary<string, object?>>();

            if (usageByDay.TryGetValue(date, out var usage))
            {
                rows.Add(ToRawRow(usage));
            }

            if (userRowsByDay.TryGetValue(date, out var userRows))
            {
                foreach (var userRow in userRows)
                {
                    rows.Add(ToUserRawRow(userRow));
                }
            }

            if (costRowsByDay.TryGetValue(date, out var costRows))
            {
                foreach (var costRow in costRows)
                {
                    rows.Add(new Dictionary<string, object?>(costRow)
                    {
                        [ChatGptDailyUsageRowFields.Source] = ChatGptRowSources.CostsExport,
                    });
                }
            }

            recordsByDate[date] = rows;
        }

        _logger.LogDebug(
            "ChatGPT Enterprise extraction for tenant '{TenantId}' used the Daily Usage API: {UsageDayCount} day(s) "
            + "with usage of {PeriodDayCount} requested; COSTS cross-check {CostsStatus}.",
            tenantId, usageByDay.Count, recordsByDate.Count,
            costsResult is null ? "unavailable" : $"supplied {costRowsByDay.Count} day(s)");

        return new RawVendorSpendData
        {
            VendorId = VendorId,
            TenantId = tenantId,
            Period = period,
            RecordsByDate = recordsByDate,
            PrimarySource = DailySpendSources.DailyUsageApi,
        };
    }

    // Best effort by design. The COSTS export is only a cross-check here, and it
    // is the slow, retention-limited, occasionally flaky source this change moves
    // away from, so its failure must not fail an extraction the Daily Usage API
    // already answered.
    private async Task<ChatGptCostQueryResult?> TryQueryCostsAsync(
        string tenantId, ChatGptCredential credential, DateRange period, CancellationToken cancellationToken)
    {
        try
        {
            return await _costLogRepository
                .QueryCostRowsAsync(credential, period, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "The ChatGPT Enterprise COSTS cross-check for tenant '{TenantId}' failed ({Message}); continuing "
                + "with the Daily Usage figures alone.",
                tenantId, ex.Message);
            return null;
        }
    }

    // Best effort by design; see the call site. A failure leaves the day without per-user rows,
    // and the next extraction that covers it fills them in.
    private async Task<IReadOnlyList<ChatGptDailyUsageRow>> TryQueryPerUserAsync(
        string tenantId, ChatGptCredential credential, DateRange period, CancellationToken cancellationToken)
    {
        try
        {
            return await _dailyUsageRepository
                .QueryDailyUsageAsync(credential, period, ChatGptUsageGrouping.PerUser, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "The ChatGPT Enterprise per-user Daily Usage query for tenant '{TenantId}' failed ({Message}); "
                + "continuing with the workspace totals alone. No per-user rows are stored for this period this run.",
                tenantId, ex.Message);
            return [];
        }
    }

    private static IReadOnlyDictionary<string, object?> ToUserRawRow(ChatGptDailyUsageRow row)
    {
        var raw = new Dictionary<string, object?>(ToRawRow(row))
        {
            [ChatGptDailyUsageRowFields.Source] = ChatGptRowSources.DailyUsageUser,
            [ChatGptDailyUsageRowFields.UserId] = row.UserId,
            [ChatGptDailyUsageRowFields.UserEmail] = row.Email,
            [ChatGptDailyUsageRowFields.ActorType] = row.ActorType,
        };
        return raw;
    }

    private static IReadOnlyDictionary<string, object?> ToRawRow(ChatGptDailyUsageRow row) =>
        new Dictionary<string, object?>
        {
            [ChatGptDailyUsageRowFields.Source] = ChatGptRowSources.DailyUsage,
            [ChatGptDailyUsageRowFields.Chats] = row.Chats,
            [ChatGptDailyUsageRowFields.Messages] = row.Messages,
            [ChatGptDailyUsageRowFields.Credits] = row.Credits,
            [ChatGptDailyUsageRowFields.CostUsd] = row.CostUsd,
            [ChatGptDailyUsageRowFields.EstimatedCostUsd] = row.EstimatedCostUsd,
            [ChatGptDailyUsageRowFields.UncachedTextInputTokens] = row.UncachedTextInputTokens,
            [ChatGptDailyUsageRowFields.CachedTextInputTokens] = row.CachedTextInputTokens,
            [ChatGptDailyUsageRowFields.TextOutputTokens] = row.TextOutputTokens,
            [ChatGptDailyUsageRowFields.TextTotalTokens] = row.TextTotalTokens,
            [ChatGptDailyUsageRowFields.Clients] = row.Clients,
            [ChatGptDailyUsageRowFields.Models] = row.Models,
        };

    // The original extraction, unchanged in behavior: used when there is no
    // WorkspaceId to address the Daily Usage API with.
    private async Task<RawVendorSpendData> ExtractFromCostsOnlyAsync(
        string tenantId, ChatGptCredential credential, DateRange period, CancellationToken cancellationToken)
    {
        var queryResult = await _costLogRepository
            .QueryCostRowsAsync(credential, period, cancellationToken)
            .ConfigureAwait(false);
        // Rows from before EffectiveStart are dropped, not just left un-gap-filled
        // (see below). The repository filters events against the *requested* start,
        // and the export is listed by file write time, so the day just before its
        // retention cutoff comes back as a sliver of that day. Grouping that sliver
        // would key the day and write a fraction of its usage as if it were the whole.
        var rows = queryResult.Rows
            .Where(row => (DateOnly)row[ChatGptCostRowFields.RecordDate]! >= queryResult.EffectiveStart)
            .ToList();

        var recordsByDate = rows
            .GroupBy(row => (DateOnly)row[ChatGptCostRowFields.RecordDate]!)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<IReadOnlyDictionary<string, object?>>)group.ToList());

        // A day with zero billed activity returns no rows at all from the COSTS
        // export — without this, that day would never reach the normalizer,
        // silently dropping its SeatFee too (seats accrue daily regardless of
        // usage). Every day needs a key — but only from EffectiveStart onward:
        // a day before that was silently clamped away by the vendor's 29-day
        // retention window, not confirmed zero-activity, and must never get a
        // synthesized $0 record that would overwrite a day that may already
        // hold real data from an earlier, unclamped pull (extraction upserts
        // by date, so a phantom zero here is a silent data-loss bug, not a
        // harmless default). When EffectiveStart is past period.End — the
        // whole requested period predates retention — this range is empty and
        // nothing gets gap-filled at all, which is exactly correct too.
        var gapFillRange = new DateRange(queryResult.EffectiveStart, period.End);
        foreach (var date in gapFillRange.EnumerateDays())
        {
            recordsByDate.TryAdd(date, Array.Empty<IReadOnlyDictionary<string, object?>>());
        }

        _logger.LogDebug(
            "ChatGPT Enterprise extraction for tenant '{TenantId}' (COSTS only) grouped {RowCount} row(s) into "
            + "{DayCount} day(s).",
            tenantId, rows.Count, recordsByDate.Count);

        return new RawVendorSpendData
        {
            VendorId = VendorId,
            TenantId = tenantId,
            Period = period,
            RecordsByDate = recordsByDate,
            PrimarySource = DailySpendSources.CostsExport,
        };
    }

    private async Task<ChatGptCredential> LoadCredentialAsync(string tenantId, CancellationToken cancellationToken)
    {
        var json = await _secretStore
            .GetCredentialAsync(tenantId, VendorId, cancellationToken)
            .ConfigureAwait(false);

        // A missing/malformed credential is a configuration problem, not "no
        // spend this period" — fail loudly rather than let it look like an
        // empty-but-successful extraction.
        if (string.IsNullOrWhiteSpace(json))
        {
            throw new InvalidOperationException(
                $"No ChatGPT Enterprise credential configured for tenant '{tenantId}'.");
        }

        if (!ChatGptCredential.TryParse(json, out var credential, out var error))
        {
            throw new InvalidOperationException(
                $"ChatGPT Enterprise credential for tenant '{tenantId}' is invalid. {error}");
        }

        return credential;
    }
}
