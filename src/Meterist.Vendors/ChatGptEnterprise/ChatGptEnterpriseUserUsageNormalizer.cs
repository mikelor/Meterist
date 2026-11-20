using Meterist.Core.Models;
using Meterist.Core.Vendors;
using Microsoft.Extensions.Logging;

namespace Meterist.Vendors.ChatGptEnterprise;

/// <summary>
/// Turns the per-user Daily Usage rows (<see cref="ChatGptRowSources.DailyUsageUser"/>) in a day's raw
/// data into <see cref="UserDailyUsageRecord"/>s, priced the same way the daily total is so the two
/// reconcile: native USD as it is, plus credits times the configured credit-to-USD rate, falling
/// back to the vendor's own estimate only when no rate is configured (and then the row is marked
/// <see cref="UserDailyUsageRecord.IsEstimated"/>).
/// <para>
/// Reconciliation: the per-user rows of a day should add up to that day's workspace row. The API's
/// per-user grouping omits deleted users, so a shortfall is possible and is not an error, but it is
/// worth saying so, as is a per-user total that exceeds the workspace total. Warnings name days and
/// figures only, never a person.
/// </para>
/// </summary>
public sealed class ChatGptEnterpriseUserUsageNormalizer : IVendorUserUsageNormalizer
{
    // Same shape as the daily normalizer's tolerances: the vendor rounds estimates to cents per day.
    private const decimal CreditsToleranceAbsolute = 5m;
    private const decimal ToleranceRelative = 0.02m;
    private const int ExamplesShown = 5;

    private readonly ILogger<ChatGptEnterpriseUserUsageNormalizer> _logger;
    private readonly TimeProvider _timeProvider;

    public ChatGptEnterpriseUserUsageNormalizer(
        ILogger<ChatGptEnterpriseUserUsageNormalizer> logger, TimeProvider? timeProvider = null)
    {
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public Guid VendorId => VendorCatalog.ChatGptEnterprise.Id;

    public IReadOnlyList<UserDailyUsageRecord> Normalize(
        RawVendorSpendData rawData, IReadOnlyList<VendorRateConfig> applicableRates)
    {
        var records = new List<UserDailyUsageRecord>();
        var disagreements = new List<string>();
        var skippedWithoutUserId = 0;
        var warnedMissingRate = false;

        // A day still in progress is incomplete in both groupings and would look like a disagreement.
        var today = DateOnly.FromDateTime(_timeProvider.GetUtcNow().UtcDateTime);

        foreach (var (date, rows) in rawData.RecordsByDate.OrderBy(kvp => kvp.Key))
        {
            var userRows = rows.Where(r => SourceOf(r) == ChatGptRowSources.DailyUsageUser).ToList();
            if (userRows.Count == 0)
            {
                continue;
            }

            var creditRate = applicableRates.FindRateForDay(ChatGptRateKeys.CreditUsdModelOrSku, date);
            var dayRecords = new List<UserDailyUsageRecord>(userRows.Count);

            foreach (var row in userRows)
            {
                var userId = row[ChatGptDailyUsageRowFields.UserId] as string;
                if (string.IsNullOrWhiteSpace(userId))
                {
                    skippedWithoutUserId++;
                    continue;
                }

                var (amount, estimated) = PriceOf(row, creditRate, ref warnedMissingRate, rawData.TenantId, date);

                dayRecords.Add(new UserDailyUsageRecord
                {
                    TenantId = rawData.TenantId,
                    VendorId = rawData.VendorId,
                    Date = date,
                    UserId = userId,
                    UserEmail = row[ChatGptDailyUsageRowFields.UserEmail] as string,
                    // The API's actor carries an email but no display name.
                    UserName = null,
                    Amount = amount,
                    Credits = row[ChatGptDailyUsageRowFields.Credits] as decimal?,
                    Requests = Convert.ToInt64(row[ChatGptDailyUsageRowFields.Messages] ?? 0),
                    IsEstimated = estimated,
                    Attribution = UsageAttribution.Person,
                });
            }

            records.AddRange(dayRecords);

            var workspaceRow = rows.FirstOrDefault(r => SourceOf(r) == ChatGptRowSources.DailyUsage);
            if (workspaceRow is not null && date < today)
            {
                var workspaceCredits = ToDecimal(workspaceRow[ChatGptDailyUsageRowFields.Credits]);
                var userCredits = dayRecords.Sum(r => r.Credits ?? 0m);

                if (Math.Abs(workspaceCredits - userCredits) > Tolerance(workspaceCredits, userCredits))
                {
                    disagreements.Add(
                        $"{date:yyyy-MM-dd}: workspace {workspaceCredits:F1} credits, users {userCredits:F1}");
                }
            }
        }

        if (skippedWithoutUserId > 0)
        {
            _logger.LogWarning(
                "ChatGPT Enterprise tenant '{TenantId}': {Count} per-user Daily Usage row(s) had no user id and were "
                + "not stored.",
                rawData.TenantId, skippedWithoutUserId);
        }

        if (disagreements.Count > 0)
        {
            _logger.LogWarning(
                "ChatGPT Enterprise tenant '{TenantId}': the per-user rows do not add up to the workspace total on "
                + "{Count} day(s) beyond tolerance. The per-user grouping omits deleted users, so a shortfall can be "
                + "expected; users summing to MORE than the workspace is not. {Examples}",
                rawData.TenantId, disagreements.Count,
                string.Join("; ", disagreements.Take(ExamplesShown))
                    + (disagreements.Count > ExamplesShown ? $"; and {disagreements.Count - ExamplesShown} more" : string.Empty));
        }

        _logger.LogDebug(
            "Normalized {RowCount} per-user ChatGPT Enterprise row(s) for tenant '{TenantId}'.",
            records.Count, rawData.TenantId);

        return records;
    }

    private (decimal Amount, bool Estimated) PriceOf(
        IReadOnlyDictionary<string, object?> row,
        VendorRateConfig? creditRate,
        ref bool warnedMissingRate,
        string tenantId,
        DateOnly date)
    {
        var credits = ToDecimal(row[ChatGptDailyUsageRowFields.Credits]);
        var estimatedUsd = row[ChatGptDailyUsageRowFields.EstimatedCostUsd] as decimal?;

        // Native USD-priced usage is dollars as they are; null on a credit-only day.
        var amount = ToDecimal(row[ChatGptDailyUsageRowFields.CostUsd]);
        var estimated = false;

        if (credits > 0m)
        {
            if (creditRate is not null)
            {
                amount += credits * creditRate.Rate;
            }
            else if (estimatedUsd is { } estimate)
            {
                amount += estimate;
                estimated = true;

                if (!warnedMissingRate)
                {
                    _logger.LogWarning(
                        "No '{ModelOrSku}' VendorRateConfig found for tenant '{TenantId}' covering {Date}; per-user "
                        + "dollars use the vendor's own estimate and are marked as estimated. Configure one via "
                        + "'rates set --rate-type {RateType} --model-or-sku {ModelOrSku} ...'.",
                        ChatGptRateKeys.CreditUsdModelOrSku, tenantId, date,
                        ChatGptRateKeys.CreditToUsdRateType, ChatGptRateKeys.CreditUsdModelOrSku);
                    warnedMissingRate = true;
                }
            }
            else if (!warnedMissingRate)
            {
                _logger.LogWarning(
                    "No '{ModelOrSku}' VendorRateConfig found for tenant '{TenantId}' and no vendor estimate; "
                    + "credit-priced per-user usage has no dollar amount (credits are still stored).",
                    ChatGptRateKeys.CreditUsdModelOrSku, tenantId);
                warnedMissingRate = true;
            }
        }

        return (amount, estimated);
    }

    private static string? SourceOf(IReadOnlyDictionary<string, object?> row) =>
        row.TryGetValue(ChatGptDailyUsageRowFields.Source, out var source) ? source as string : null;

    private static decimal Tolerance(decimal a, decimal b) =>
        Math.Max(CreditsToleranceAbsolute, ToleranceRelative * Math.Max(Math.Abs(a), Math.Abs(b)));

    private static decimal ToDecimal(object? value) => value switch
    {
        null => 0m,
        decimal d => d,
        _ => Convert.ToDecimal(value),
    };
}
