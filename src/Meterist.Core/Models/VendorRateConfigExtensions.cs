namespace Meterist.Core.Models;

/// <summary>
/// Shared day-lookup/proration logic for normalizers that need to resolve a
/// seat fee from configured rates rather than a vendor-reported figure —
/// first needed by ChatGptEnterpriseSpendNormalizer, extracted here once
/// ClaudeEnterpriseSpendNormalizer needed the identical logic (real
/// duplication across two call sites, not speculative reuse).
/// </summary>
public static class VendorRateConfigExtensions
{
    /// <summary>
    /// Finds the row in <paramref name="rates"/> matching <paramref name="modelOrSku"/>
    /// whose [EffectiveFrom, EffectiveTo] window covers <paramref name="date"/>.
    /// Assumes callers already scoped <paramref name="rates"/> to one vendor
    /// (e.g. via IVendorRateConfigRepository.GetApplicableRatesAsync).
    /// </summary>
    public static VendorRateConfig? FindRateForDay(
        this IReadOnlyList<VendorRateConfig> rates, string? modelOrSku, DateOnly date) =>
        rates.FirstOrDefault(r => r.ModelOrSku == modelOrSku
            && r.EffectiveFrom <= date && (r.EffectiveTo is null || r.EffectiveTo >= date));

    /// <summary>
    /// Prorates a per-seat rate's SeatCount * Rate across the days in its
    /// billing cadence. OneTime (or no cadence set) isn't meaningfully
    /// prorateable in a daily model — treated as already a single day's
    /// amount, a documented v1 limitation.
    /// </summary>
    public static decimal ProrateSeatFee(this VendorRateConfig seatRate, DateOnly date)
    {
        var totalAmount = (seatRate.SeatCount ?? 1) * seatRate.Rate;

        var daysInCadencePeriod = seatRate.BillingCadence switch
        {
            BillingCadence.Monthly => DateTime.DaysInMonth(date.Year, date.Month),
            BillingCadence.Annual => DateTime.IsLeapYear(date.Year) ? 366 : 365,
            _ => 1,
        };

        return totalAmount / daysInCadencePeriod;
    }
}
