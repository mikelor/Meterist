using Meterist.Core.Models;
using Meterist.Core.Vendors;
using Microsoft.Extensions.Logging;

namespace Meterist.Vendors.ClaudeEnterprise;

/// <summary>
/// Turns Claude Enterprise's day-grouped user_cost_report rows into one
/// DailySpendRecord per date. Simpler than ChatGPT's normalizer — the
/// Analytics API already returns real dollar cost (no credit-to-USD
/// conversion needed), but this vendor's schema still has no seat/
/// subscription line at all, so SeatFee still has to come from a configured
/// VendorRateConfig row (see ClaudeEnterpriseRateKeys and
/// docs/user-guide.md's "Configuring rates" section).
/// </summary>
public sealed class ClaudeEnterpriseSpendNormalizer : IVendorSpendNormalizer
{
    private readonly ILogger<ClaudeEnterpriseSpendNormalizer> _logger;

    public ClaudeEnterpriseSpendNormalizer(ILogger<ClaudeEnterpriseSpendNormalizer> logger)
    {
        _logger = logger;
    }

    public Guid VendorId => VendorCatalog.ClaudeEnterprise.Id;

    public IReadOnlyList<DailySpendRecord> Normalize(
        RawVendorSpendData rawData,
        IReadOnlyList<VendorRateConfig> applicableRates)
    {
        // Warn once per extraction call, not once per day, to avoid log spam
        // across a multi-week range when a rate simply hasn't been entered yet.
        var warnedMissingSeatRate = false;

        var records = new List<DailySpendRecord>(rawData.RecordsByDate.Count);

        foreach (var (date, rows) in rawData.RecordsByDate.OrderBy(kvp => kvp.Key))
        {
            var seatRate = applicableRates.FindRateForDay(ClaudeEnterpriseRateKeys.SeatModelOrSku, date);
            var seatFee = 0m;

            if (seatRate is not null)
            {
                seatFee = seatRate.ProrateSeatFee(date);
            }
            else if (!warnedMissingSeatRate)
            {
                _logger.LogWarning(
                    "No '{ModelOrSku}' VendorRateConfig found for tenant '{TenantId}' covering {Date} — "
                    + "SeatFee will be 0 for any day without a matching config. Configure one via "
                    + "'rates set --rate-type {RateType} --model-or-sku {ModelOrSku} ...'.",
                    ClaudeEnterpriseRateKeys.SeatModelOrSku, rawData.TenantId, date,
                    ClaudeEnterpriseRateKeys.PerSeatRateType, ClaudeEnterpriseRateKeys.SeatModelOrSku);
                warnedMissingSeatRate = true;
            }

            // Legitimately 0 on a seat-based tenant without "usage credits"
            // enabled — the Analytics API has no overage concept to report in
            // that case, not a broken integration. See SupportsOverage's doc
            // comment on ClaudeEnterpriseSpendExtractor.
            var usageOrOverage = rows.Sum(row => ToDecimal(row[ClaudeAnalyticsCostRowFields.AmountUsd]));

            var grossSpend = seatFee + usageOrOverage;

            records.Add(new DailySpendRecord
            {
                TenantId = rawData.TenantId,
                VendorId = rawData.VendorId,
                Date = date,
                SeatFee = seatFee,
                UsageOrOverage = usageOrOverage,
                GrossSpend = grossSpend,
                CreditsApplied = 0m,
                NetSpend = grossSpend,
            });
        }

        _logger.LogDebug(
            "Normalized {DayCount} day(s) of Claude Enterprise raw data into {RecordCount} DailySpendRecord(s) "
            + "for tenant '{TenantId}'.",
            rawData.RecordsByDate.Count, records.Count, rawData.TenantId);

        return records;
    }

    private static decimal ToDecimal(object? value) => value switch
    {
        null => 0m,
        decimal d => d,
        _ => Convert.ToDecimal(value),
    };
}
