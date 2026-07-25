using Meterist.Core.Models;
using Meterist.Core.Vendors;
using Microsoft.Extensions.Logging;

namespace Meterist.Vendors.ClaudeApiPlatform;

/// <summary>
/// Turns Claude API Platform's day-grouped cost_report rows into one
/// DailySpendRecord per date. The simplest normalizer of the four vendors —
/// no seat fee, no overage split, no rate resolution: cost_report already
/// returns real dollar cost directly, and this product has no seat concept
/// at all, so the whole daily total is "usage."
/// </summary>
public sealed class ClaudeApiPlatformSpendNormalizer : IVendorSpendNormalizer
{
    private readonly ILogger<ClaudeApiPlatformSpendNormalizer> _logger;

    public ClaudeApiPlatformSpendNormalizer(ILogger<ClaudeApiPlatformSpendNormalizer> logger)
    {
        _logger = logger;
    }

    public Guid VendorId => VendorCatalog.ClaudeApiPlatform.Id;

    public IReadOnlyList<DailySpendRecord> Normalize(
        RawVendorSpendData rawData,
        IReadOnlyList<VendorRateConfig> applicableRates)
    {
        // Not used: cost_report already carries dollar cost directly, and
        // this vendor has no seat/credit concept to resolve a rate for — see
        // IVendorSpendNormalizer's doc comment on when rate resolution is
        // and isn't in the critical path.
        _ = applicableRates;

        var records = new List<DailySpendRecord>(rawData.RecordsByDate.Count);

        foreach (var (date, rows) in rawData.RecordsByDate.OrderBy(kvp => kvp.Key))
        {
            var usageOrOverage = rows.Sum(row => ToDecimal(row[ClaudeApiCostRowFields.AmountUsd]));

            records.Add(new DailySpendRecord
            {
                TenantId = rawData.TenantId,
                VendorId = rawData.VendorId,
                Date = date,
                SeatFee = 0m,
                UsageOrOverage = usageOrOverage,
                GrossSpend = usageOrOverage,
                CreditsApplied = 0m,
                NetSpend = usageOrOverage,
            });
        }

        _logger.LogDebug(
            "Normalized {DayCount} day(s) of Claude API Platform raw data into {RecordCount} DailySpendRecord(s) "
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
