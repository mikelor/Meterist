using Meterist.Core.Models;
using Meterist.Core.Vendors;
using Microsoft.Extensions.Logging;

namespace Meterist.Vendors.GeminiEnterprise;

/// <summary>
/// Turns Gemini Enterprise's day-grouped BigQuery rows into one
/// DailySpendRecord per date, splitting each day's cost into the
/// seat fee (the rows whose category is Seat) vs. everything else (overage, Agent
/// Gateway, Memory Bank — see docs/vendor-integration-reference.md). Rows marked as
/// non-usage (tax, support, rounding) are skipped: they are kept in the per-kind
/// breakdown, not in any total.
/// </summary>
public sealed class GeminiEnterpriseSpendNormalizer : IVendorSpendNormalizer
{
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
                // Tax, support fees and rounding are real charges on the account but not usage:
                // recorded in the breakdown, never in a total (docs/architecture.md section 5.2).
                if (!GeminiCostBreakdown.IsUsage(row))
                {
                    continue;
                }

                var cost = ToDecimal(row[GeminiBillingRowFields.Cost]);
                var creditsAmount = ToDecimal(row[GeminiBillingRowFields.CreditsAmount]);

                // The seat fee is the rows categorised as Seat (by the matching rule, or, for a rule with no
                // category and for rows stored before categories existed, by a Subscription SKU).
                if (string.Equals(GeminiCostBreakdown.CategoryOf(row), SpendCategories.Seat, StringComparison.Ordinal))
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
