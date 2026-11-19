namespace Meterist.Core.Models;

/// <summary>
/// Normalized daily spend for one tenant/vendor pair, regardless of that
/// vendor's native API shape. Daily is the canonical stored grain — weekly,
/// monthly, and annual views are query-time aggregations over this, not
/// separately stored. Natural key is (TenantId, VendorId, Date): re-running
/// extraction for a day already stored is an upsert against this same key,
/// which is also how overlapping extraction timespans are handled — no
/// special-case "overlap" logic needed.
/// </summary>
public sealed class DailySpendRecord
{
    public required string TenantId { get; init; }

    public required Guid VendorId { get; init; }

    public required DateOnly Date { get; init; }

    public decimal SeatFee { get; init; }

    // Legitimately 0 for some vendor/tenant pairs (e.g. Claude API Platform always,
    // or Claude Enterprise when "usage credits" isn't enabled) — expected behavior,
    // not a missing-data signal. See IVendorSpendExtractor.SupportsOverage.
    public decimal UsageOrOverage { get; init; }

    public decimal GrossSpend { get; init; }

    public decimal CreditsApplied { get; init; }

    public decimal NetSpend { get; init; }

    /// <summary>
    /// Which data source supplied this day's figures, for vendors that have
    /// more than one (see <see cref="DailySpendSources"/>), so reports can
    /// disclose it. Null for vendors with a single source, where there is
    /// nothing to disclose. When a monotonic upsert keeps an earlier, higher
    /// usage figure, this stays the source of that figure.
    /// </summary>
    public string? Source { get; init; }
}

/// <summary>Well-known values for <see cref="DailySpendRecord.Source"/>.</summary>
public static class DailySpendSources
{
    /// <summary>ChatGPT Enterprise's COSTS compliance-log export (30-day file retention).</summary>
    public const string CostsExport = "costs-export";

    /// <summary>ChatGPT Enterprise's Daily Usage API.</summary>
    public const string DailyUsageApi = "daily-usage-api";
}
