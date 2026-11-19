using Meterist.Core.Models;
using Meterist.Core.Vendors;
using Microsoft.Extensions.Logging;

namespace Meterist.Vendors.ChatGptEnterprise;

/// <summary>
/// Turns ChatGPT Enterprise's day-grouped raw rows into one DailySpendRecord
/// per date. A day's raw rows can come from two sources, told apart by the
/// <see cref="ChatGptDailyUsageRowFields.Source"/> tag (an untagged row is a
/// COSTS row, the only shape stored before the Daily Usage API was added):
/// <list type="bullet">
/// <item>The Daily Usage API row, when there is one, is the source of record:
/// usage is its credits times the configured credit-to-USD rate, plus any
/// natively USD-priced usage. The vendor's own estimate is a cross-check, and
/// the fallback only when no rate is configured.</item>
/// <item>Otherwise the day is computed from its COSTS billing lines, exactly as
/// before this API existed.</item>
/// </list>
/// Unlike Gemini, neither source carries a seat/subscription line, so the seat
/// fee comes from configured VendorRateConfig rows (see ChatGptRateKeys and
/// docs/user-guide.md's "Configuring rates" section).
/// When both sources have a day, their credits are compared and any
/// disagreement is logged; COSTS never overrides the Daily Usage figure.
/// </summary>
public sealed class ChatGptEnterpriseSpendNormalizer : IVendorSpendNormalizer
{
    private const string CreditsUnit = "CREDITS";

    // How far the two sources may differ before it is worth saying so. The
    // vendor's estimate is rounded to cents per day, and the COSTS export can
    // lag by hours, so exact agreement is not expected.
    private const decimal CreditsToleranceAbsolute = 5m;
    private const decimal DollarsToleranceAbsolute = 0.05m;
    private const decimal ToleranceRelative = 0.02m;

    // How many example days a summary warning spells out.
    private const int ExamplesShown = 5;

    private readonly ILogger<ChatGptEnterpriseSpendNormalizer> _logger;
    private readonly TimeProvider _timeProvider;

    public ChatGptEnterpriseSpendNormalizer(
        ILogger<ChatGptEnterpriseSpendNormalizer> logger, TimeProvider? timeProvider = null)
    {
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public Guid VendorId => VendorCatalog.ChatGptEnterprise.Id;

    public IReadOnlyList<DailySpendRecord> Normalize(
        RawVendorSpendData rawData,
        IReadOnlyList<VendorRateConfig> applicableRates)
    {
        // Warn once per extraction call, not once per day, to avoid log spam
        // across a multi-week range when a rate simply hasn't been entered yet.
        var warnedMissingSeatRate = false;
        var warnedMissingCreditRate = false;
        var warnedEstimateFallback = false;

        var creditsDisagreements = new List<string>();
        var rateDisagreements = new List<string>();
        var costsOnlyDays = new List<DateOnly>();

        // A day still in progress is incomplete in both sources and would
        // always look like a disagreement.
        var today = DateOnly.FromDateTime(_timeProvider.GetUtcNow().UtcDateTime);

        var records = new List<DailySpendRecord>(rawData.RecordsByDate.Count);

        foreach (var (date, rows) in rawData.RecordsByDate.OrderBy(kvp => kvp.Key))
        {
            var seatRate = applicableRates.FindRateForDay(ChatGptRateKeys.SeatModelOrSku, date);
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
                    ChatGptRateKeys.SeatModelOrSku, rawData.TenantId, date,
                    ChatGptRateKeys.PerSeatRateType, ChatGptRateKeys.SeatModelOrSku);
                warnedMissingSeatRate = true;
            }

            var creditRate = applicableRates.FindRateForDay(ChatGptRateKeys.CreditUsdModelOrSku, date);

            var usageRow = rows.FirstOrDefault(IsDailyUsageRow);
            var costRows = rows.Where(r => !IsDailyUsageRow(r)).ToList();

            decimal usageOrOverage;
            string? source;

            if (usageRow is not null)
            {
                source = DailySpendSources.DailyUsageApi;
                usageOrOverage = UsageFromDailyUsage(
                    usageRow, date, creditRate, rawData.TenantId, rateDisagreements,
                    ref warnedMissingCreditRate, ref warnedEstimateFallback);

                if (costRows.Count > 0 && date < today)
                {
                    var usageCredits = ToDecimal(usageRow[ChatGptDailyUsageRowFields.Credits]);
                    var costsCredits = SumCostsCredits(costRows);
                    if (Math.Abs(usageCredits - costsCredits) > Tolerance(CreditsToleranceAbsolute, usageCredits, costsCredits))
                    {
                        creditsDisagreements.Add(
                            $"{date:yyyy-MM-dd}: Daily Usage {usageCredits:F1} credits, COSTS {costsCredits:F1}");
                    }
                }
            }
            else if (costRows.Count > 0)
            {
                source = DailySpendSources.CostsExport;
                usageOrOverage = UsageFromCosts(
                    costRows, date, creditRate, rawData.TenantId, ref warnedMissingCreditRate);

                // The Daily Usage API had nothing for a day that COSTS shows
                // activity on. Use what is known rather than record no usage,
                // but say so: it is the two sources disagreeing.
                if (rawData.PrimarySource == DailySpendSources.DailyUsageApi)
                {
                    costsOnlyDays.Add(date);
                }
            }
            else
            {
                // No rows from any source: no usage reported for the day.
                source = rawData.PrimarySource ?? DailySpendSources.CostsExport;
                usageOrOverage = 0m;
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
                // Neither source has a separate discount/credit field the way
                // Gemini's BigQuery export does — a narrower schema, not a gap.
                CreditsApplied = 0m,
                NetSpend = grossSpend,
                Source = source,
            });
        }

        LogDisagreements(rawData.TenantId, creditsDisagreements, rateDisagreements, costsOnlyDays);

        _logger.LogDebug(
            "Normalized {DayCount} day(s) of ChatGPT Enterprise raw data into {RecordCount} DailySpendRecord(s) "
            + "for tenant '{TenantId}'.",
            rawData.RecordsByDate.Count, records.Count, rawData.TenantId);

        return records;
    }

    private decimal UsageFromDailyUsage(
        IReadOnlyDictionary<string, object?> row,
        DateOnly date,
        VendorRateConfig? creditRate,
        string tenantId,
        List<string> rateDisagreements,
        ref bool warnedMissingCreditRate,
        ref bool warnedEstimateFallback)
    {
        var credits = ToDecimal(row[ChatGptDailyUsageRowFields.Credits]);
        var estimatedUsd = row[ChatGptDailyUsageRowFields.EstimatedCostUsd] as decimal?;

        // cost_usd is natively USD-priced usage: dollars as they are, never
        // run through the credit rate. It is null on a credit-only day.
        var usage = ToDecimal(row[ChatGptDailyUsageRowFields.CostUsd]);

        if (credits > 0m)
        {
            if (creditRate is not null)
            {
                // The configured, versioned rate is the figure of record: the
                // vendor's estimate is made at the workspace's *current* overage
                // rate and is rounded to cents, so it cannot be right for a day
                // priced under an earlier rate.
                var fromRate = credits * creditRate.Rate;
                usage += fromRate;

                if (estimatedUsd is { } estimate
                    && Math.Abs(fromRate - estimate) > Tolerance(DollarsToleranceAbsolute, fromRate, estimate))
                {
                    rateDisagreements.Add($"{date:yyyy-MM-dd}: rate gives {fromRate:F2}, vendor estimate {estimate:F2}");
                }
            }
            else if (estimatedUsd is { } estimate)
            {
                usage += estimate;

                if (!warnedEstimateFallback)
                {
                    _logger.LogWarning(
                        "No '{ModelOrSku}' VendorRateConfig found for tenant '{TenantId}' covering {Date} — using the "
                        + "vendor's own estimated USD for credit-priced usage. The estimate uses the workspace's "
                        + "current overage rate, so configure one via 'rates set --rate-type {RateType} "
                        + "--model-or-sku {ModelOrSku} ...' to price historical days correctly.",
                        ChatGptRateKeys.CreditUsdModelOrSku, tenantId, date,
                        ChatGptRateKeys.CreditToUsdRateType, ChatGptRateKeys.CreditUsdModelOrSku);
                    warnedEstimateFallback = true;
                }
            }
            else if (!warnedMissingCreditRate)
            {
                WarnMissingCreditRate(tenantId);
                warnedMissingCreditRate = true;
            }
        }

        return usage;
    }

    // The original calculation, unchanged: used for days whose only data is COSTS billing lines.
    private decimal UsageFromCosts(
        IReadOnlyList<IReadOnlyDictionary<string, object?>> costRows,
        DateOnly date,
        VendorRateConfig? creditRate,
        string tenantId,
        ref bool warnedMissingCreditRate)
    {
        var usageOrOverage = 0m;

        foreach (var row in costRows)
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
                if (creditRate is not null)
                {
                    usageOrOverage += costValue * creditRate.Rate;
                }
                else if (!warnedMissingCreditRate)
                {
                    WarnMissingCreditRate(tenantId);
                    warnedMissingCreditRate = true;
                }
            }
            else
            {
                // Already a direct currency unit — pass through unconverted.
                usageOrOverage += costValue;
            }
        }

        return usageOrOverage;
    }

    private void WarnMissingCreditRate(string tenantId) =>
        _logger.LogWarning(
            "No '{ModelOrSku}' VendorRateConfig found for tenant '{TenantId}' — CREDITS-denominated "
            + "usage cannot be converted to dollars and will be excluded from UsageOrOverage. "
            + "Configure one via 'rates set --rate-type {RateType} --model-or-sku {ModelOrSku} ...'.",
            ChatGptRateKeys.CreditUsdModelOrSku, tenantId,
            ChatGptRateKeys.CreditToUsdRateType, ChatGptRateKeys.CreditUsdModelOrSku);

    private void LogDisagreements(
        string tenantId, List<string> creditsDisagreements, List<string> rateDisagreements, List<DateOnly> costsOnlyDays)
    {
        if (creditsDisagreements.Count > 0)
        {
            _logger.LogWarning(
                "ChatGPT Enterprise tenant '{TenantId}': Daily Usage and the COSTS export disagree on credits for "
                + "{Count} day(s) beyond tolerance; the Daily Usage figure was used. {Examples}",
                tenantId, creditsDisagreements.Count, Examples(creditsDisagreements));
        }

        if (rateDisagreements.Count > 0)
        {
            _logger.LogWarning(
                "ChatGPT Enterprise tenant '{TenantId}': the configured credit-to-USD rate disagrees with the vendor's "
                + "estimated USD on {Count} day(s) beyond tolerance, so the rate may be stale (the vendor estimates "
                + "at the workspace's current rate; the configured rate was used). {Examples}",
                tenantId, rateDisagreements.Count, Examples(rateDisagreements));
        }

        if (costsOnlyDays.Count > 0)
        {
            _logger.LogWarning(
                "ChatGPT Enterprise tenant '{TenantId}': the Daily Usage API reported no usage for {Count} day(s) on "
                + "which the COSTS export shows activity, so those days were computed from COSTS: {Examples}",
                tenantId, costsOnlyDays.Count,
                Examples(costsOnlyDays.Select(d => d.ToString("yyyy-MM-dd")).ToList()));
        }
    }

    private static string Examples(List<string> items) =>
        string.Join("; ", items.Take(ExamplesShown)) + (items.Count > ExamplesShown ? $"; and {items.Count - ExamplesShown} more" : string.Empty);

    private static bool IsDailyUsageRow(IReadOnlyDictionary<string, object?> row) =>
        row.TryGetValue(ChatGptDailyUsageRowFields.Source, out var source)
        && string.Equals(source as string, ChatGptRowSources.DailyUsage, StringComparison.Ordinal);

    private static decimal SumCostsCredits(IReadOnlyList<IReadOnlyDictionary<string, object?>> costRows) =>
        costRows
            .Where(r => string.Equals((string?)r[ChatGptCostRowFields.CostUnit], CreditsUnit, StringComparison.OrdinalIgnoreCase))
            .Sum(r => ToDecimal(r[ChatGptCostRowFields.CostValue]));

    private static decimal Tolerance(decimal absolute, decimal a, decimal b) =>
        Math.Max(absolute, ToleranceRelative * Math.Max(Math.Abs(a), Math.Abs(b)));

    private static decimal ToDecimal(object? value) => value switch
    {
        null => 0m,
        decimal d => d,
        _ => Convert.ToDecimal(value),
    };
}
