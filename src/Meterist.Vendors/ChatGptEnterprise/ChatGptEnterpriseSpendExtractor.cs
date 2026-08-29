using System.Text.Json;
using Meterist.Core.Secrets;
using Meterist.Core.Vendors;
using Microsoft.Extensions.Logging;

namespace Meterist.Vendors.ChatGptEnterprise;

/// <summary>
/// ChatGPT Enterprise via the OpenAI Programmatic Admin Platform's COSTS
/// compliance log export — see docs/vendor-integration-reference.md for
/// setup/schema detail.
/// </summary>
public sealed class ChatGptEnterpriseSpendExtractor : IVendorSpendExtractor
{
    private readonly ISecretStore _secretStore;
    private readonly IChatGptCostLogRepository _costLogRepository;
    private readonly ILogger<ChatGptEnterpriseSpendExtractor> _logger;

    public ChatGptEnterpriseSpendExtractor(
        ISecretStore secretStore,
        IChatGptCostLogRepository costLogRepository,
        ILogger<ChatGptEnterpriseSpendExtractor> logger)
    {
        _secretStore = secretStore;
        _costLogRepository = costLogRepository;
        _logger = logger;
    }

    public Guid VendorId => VendorCatalog.ChatGptEnterprise.Id;

    // True for raw credit consumption, which the COSTS export does carry.
    // The credit-pool size and contracted overage rate are NOT API-derivable
    // though (console/contract-only) — those must come from tenant-level
    // config, not this extractor.
    public bool SupportsOverage => true;

    // Real vendor-reported per-user dollar cost (payload.identity), unlike
    // Gemini Enterprise where per-user cost would be a derived estimate.
    public bool SupportsPerUserBreakdown => true;

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
            "Resolved ChatGPT Enterprise credential for tenant '{TenantId}': organization '{OrganizationId}'.",
            tenantId, credential.OrganizationId);

        var queryResult = await _costLogRepository
            .QueryCostRowsAsync(credential, period, cancellationToken)
            .ConfigureAwait(false);
        var rows = queryResult.Rows;

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
            "ChatGPT Enterprise extraction for tenant '{TenantId}' grouped {RowCount} row(s) into {DayCount} day(s).",
            tenantId, rows.Count, recordsByDate.Count);

        return new RawVendorSpendData
        {
            VendorId = VendorId,
            TenantId = tenantId,
            Period = period,
            RecordsByDate = recordsByDate,
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

        try
        {
            return JsonSerializer.Deserialize<ChatGptCredential>(json)
                ?? throw new InvalidOperationException(
                    $"ChatGPT Enterprise credential for tenant '{tenantId}' deserialized to null.");
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                $"ChatGPT Enterprise credential for tenant '{tenantId}' is not valid JSON.", ex);
        }
    }
}
