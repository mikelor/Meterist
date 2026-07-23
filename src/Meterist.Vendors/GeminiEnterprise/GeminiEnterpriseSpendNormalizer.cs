using Meterist.Core.Models;
using Meterist.Core.Vendors;
using Microsoft.Extensions.Logging;

namespace Meterist.Vendors.GeminiEnterprise;

/// <summary>
/// Turns Gemini Enterprise's day-grouped BigQuery rows into one
/// DailySpendRecord per date, splitting each day's cost into the
/// subscription seat fee vs. everything else (overage, Agent Gateway,
/// Memory Bank — see docs/vendor-integration-reference.md).
/// </summary>
public sealed class GeminiEnterpriseSpendNormalizer : IVendorSpendNormalizer
{
    private const string SubscriptionMarker = "Subscription";

    private readonly ILogger<GeminiEnterpriseSpendNormalizer> _logger;

    public GeminiEnterpriseSpendNormalizer(ILogger<GeminiEnterpriseSpendNormalizer> logger)
    {
        _logger = logger;
    }

    public Guid VendorId => VendorCatalog.GeminiEnterprise.Id;

    public IReadOnlyList<DailySpendRecord> Normalize(
        RawVendorSpendData rawData,
        IReadOnlyList<VendorRateConfig> applicableRates)
    {
        // Not used: Gemini's BigQuery export already carries dollar cost
        // directly (no token/usage-count rate-table math needed) — see
        // IVendorSpendNormalizer's doc comment on when rate resolution is and
        // isn't in the critical path. ChatGptEnterpriseSpendNormalizer is the
        // first real consumer of this list.
        _ = applicableRates;

        var records = new List<DailySpendRecord>(rawData.RecordsByDate.Count);

        foreach (var (date, rows) in rawData.RecordsByDate.OrderBy(kvp => kvp.Key))
        {
            var seatFee = 0m;
            var usageOrOverage = 0m;
            var creditsApplied = 0m;

            foreach (var row in rows)
            {
                var skuDescription = (string?)row[GeminiBillingRowFields.SkuDescription] ?? string.Empty;
                var cost = ToDecimal(row[GeminiBillingRowFields.Cost]);
                var creditsAmount = ToDecimal(row[GeminiBillingRowFields.CreditsAmount]);

                if (skuDescription.Contains(SubscriptionMarker, StringComparison.OrdinalIgnoreCase))
                {
                    seatFee += cost;
                }
                else
                {
                    usageOrOverage += cost;
                }

                // GCP billing credits are reported as negative amounts (a credit
                // reduces cost) — CreditsApplied is stored as a positive magnitude.
                creditsApplied += Math.Abs(creditsAmount);
            }

            var grossSpend = seatFee + usageOrOverage;

            records.Add(new DailySpendRecord
            {
                TenantId = rawData.TenantId,
                VendorId = rawData.VendorId,
                Date = date,
                SeatFee = seatFee,
                UsageOrOverage = usageOrOverage,
                GrossSpend = grossSpend,
                CreditsApplied = creditsApplied,
                NetSpend = grossSpend - creditsApplied,
            });
        }

        _logger.LogDebug(
            "Normalized {DayCount} day(s) of Gemini Enterprise raw data into {RecordCount} DailySpendRecord(s) for tenant '{TenantId}'.",
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
