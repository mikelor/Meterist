using Meterist.Core.Models;
using Meterist.Core.Vendors;
using Microsoft.Extensions.Logging;

namespace Meterist.Vendors.ChatGptEnterprise;

/// <summary>
/// Turns ChatGPT Enterprise's day-grouped COSTS billing-line rows into one
/// DailySpendRecord per date. Unlike Gemini, this vendor's export has no
/// seat/subscription line and no reliable dollar figure — both the seat fee
/// and the credit-to-USD conversion have to come from configured
/// VendorRateConfig rows (see ChatGptRateKeys and docs/user-guide.md's
/// "Configuring rates" section). This is the first real consumer of
/// IVendorSpendNormalizer's applicableRates parameter.
/// </summary>
public sealed class ChatGptEnterpriseSpendNormalizer : IVendorSpendNormalizer
{
    private const string CreditsUnit = "CREDITS";

    private readonly ILogger<ChatGptEnterpriseSpendNormalizer> _logger;

    public ChatGptEnterpriseSpendNormalizer(ILogger<ChatGptEnterpriseSpendNormalizer> logger)
    {
        _logger = logger;
    }

    public Guid VendorId => VendorCatalog.ChatGptEnterprise.Id;

    public IReadOnlyList<DailySpendRecord> Normalize(
        RawVendorSpendData rawData,
        IReadOnlyList<VendorRateConfig> applicableRates)
    {
        var seatRates = applicableRates.Where(r => r.ModelOrSku == ChatGptRateKeys.SeatModelOrSku).ToList();
        var creditRates = applicableRates.Where(r => r.ModelOrSku == ChatGptRateKeys.CreditUsdModelOrSku).ToList();

        // Warn once per extraction call, not once per day, to avoid log spam
        // across a multi-week range when a rate simply hasn't been entered yet.
        var warnedMissingSeatRate = false;
        var warnedMissingCreditRate = false;

        var records = new List<DailySpendRecord>(rawData.RecordsByDate.Count);

        foreach (var (date, rows) in rawData.RecordsByDate.OrderBy(kvp => kvp.Key))
        {
            var seatRate = FindRateForDay(seatRates, date);
            var seatFee = 0m;

            if (seatRate is not null)
            {
                seatFee = ProrateSeatFee(seatRate, date);
            }
            else if (!warnedMissingSeatRate)
            {
                _logger.LogWarning(
                    "No '{ModelOrSku}' VendorRateConfig found for tenant '{TenantId}' covering {Date} — "
                    + "SeatFee will be 0 for any day without a matching config. Configure one via "
                    + "'rates set --rate-type {RateType} --model-or-sku {ModelOrSku} ...'.",
                    ChatGptRateKeys.SeatModelOrSku, rawData.TenantId, date,
                    ChatGptRateKeys.PerSeatRateType, ChatGptRateKeys.SeatModelOrSku);
                warnedMissingSeatRate = true;
            }

            var usageOrOverage = 0m;

            foreach (var row in rows)
            {
                var estimatedUsd = row[ChatGptCostRowFields.EstimatedCostUsdValue] as decimal?;
                if (estimatedUsd is not null)
                {
                    // Opportunistic use of the vendor's own estimate when present
                    // — see docs/vendor-integration-reference.md: it wasn't
                    // present in the real zelleri sample, so this is a bonus
                    // shortcut, not something to rely on.
                    usageOrOverage += estimatedUsd.Value;
                    continue;
                }

                var costValue = ToDecimal(row[ChatGptCostRowFields.CostValue]);
                var costUnit = (string?)row[ChatGptCostRowFields.CostUnit] ?? string.Empty;

                if (string.Equals(costUnit, CreditsUnit, StringComparison.OrdinalIgnoreCase))
                {
                    var creditRate = FindRateForDay(creditRates, date);
                    if (creditRate is not null)
                    {
                        usageOrOverage += costValue * creditRate.Rate;
                    }
                    else if (!warnedMissingCreditRate)
                    {
                        _logger.LogWarning(
                            "No '{ModelOrSku}' VendorRateConfig found for tenant '{TenantId}' — CREDITS-denominated "
                            + "usage cannot be converted to dollars and will be excluded from UsageOrOverage. "
                            + "Configure one via 'rates set --rate-type {RateType} --model-or-sku {ModelOrSku} ...'.",
                            ChatGptRateKeys.CreditUsdModelOrSku, rawData.TenantId,
                            ChatGptRateKeys.CreditToUsdRateType, ChatGptRateKeys.CreditUsdModelOrSku);
                        warnedMissingCreditRate = true;
                    }
                }
                else
                {
                    // Already a direct currency unit — pass through unconverted.
                    usageOrOverage += costValue;
                }
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
                // The COSTS schema has no separate discount/credit field the way
                // Gemini's BigQuery export does — a narrower schema, not a gap.
                CreditsApplied = 0m,
                NetSpend = grossSpend,
            });
        }

        _logger.LogDebug(
            "Normalized {DayCount} day(s) of ChatGPT Enterprise raw data into {RecordCount} DailySpendRecord(s) "
            + "for tenant '{TenantId}'.",
            rawData.RecordsByDate.Count, records.Count, rawData.TenantId);

        return records;
    }

    private static VendorRateConfig? FindRateForDay(IReadOnlyList<VendorRateConfig> rates, DateOnly date) =>
        rates.FirstOrDefault(r => r.EffectiveFrom <= date && (r.EffectiveTo is null || r.EffectiveTo >= date));

    private static decimal ProrateSeatFee(VendorRateConfig seatRate, DateOnly date)
    {
        var totalAmount = (seatRate.SeatCount ?? 1) * seatRate.Rate;

        var daysInCadencePeriod = seatRate.BillingCadence switch
        {
            BillingCadence.Monthly => DateTime.DaysInMonth(date.Year, date.Month),
            BillingCadence.Annual => DateTime.IsLeapYear(date.Year) ? 366 : 365,
            // OneTime isn't meaningfully "prorateable" in a daily model — treated
            // as already a single day's amount, a documented v1 limitation.
            _ => 1,
        };

        return totalAmount / daysInCadencePeriod;
    }

    private static decimal ToDecimal(object? value) => value switch
    {
        null => 0m,
        decimal d => d,
        _ => Convert.ToDecimal(value),
    };
}
