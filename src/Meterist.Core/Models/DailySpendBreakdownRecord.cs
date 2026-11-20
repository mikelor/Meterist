namespace Meterist.Core.Models;

/// <summary>
/// One kind of charge within one day's spend for a tenant and vendor: the finer split behind a
/// <see cref="DailySpendRecord"/>. Natural key is (TenantId, VendorId, Date, Category, Detail). Rows exist
/// only where a charge exists, so a new kind of charge a vendor introduces is a new row, not a migration.
/// <para>
/// <see cref="IsUsage"/> separates what the daily record counts from what it deliberately does not. Rows
/// with <see cref="IsUsage"/> true add up to the day's <see cref="DailySpendRecord.GrossSpend"/> (the seat fee
/// and the usage or overage, before credits). Rows with it false (tax, support fees, rounding) are kept so
/// the gap to a vendor's invoice can be explained, and are never part of any total.
/// </para>
/// </summary>
public sealed class DailySpendBreakdownRecord
{
    public required string TenantId { get; init; }

    public required Guid VendorId { get; init; }

    public required DateOnly Date { get; init; }

    /// <summary>
    /// What kind of charge this is, as configured (for Gemini: Seat, EnterpriseOverage, AgentPlatform,
    /// ModelUsage, PayAsYouGo, Other, and for non-usage rows Tax, Support, Rounding). A free-form label rather
    /// than an enum so configuration can introduce one without a code change.
    /// </summary>
    public required string Category { get; init; }

    /// <summary>
    /// The line within the category, usually the vendor's own SKU text, so a specific model or plan stays
    /// visible. Empty (never null, so it can be part of a unique key) when the category has no finer line.
    /// </summary>
    public string Detail { get; init; } = string.Empty;

    /// <summary>Cost in USD before any credits, as the vendor's billing data states it.</summary>
    public decimal Amount { get; init; }

    /// <summary>True when the amount is counted in the daily record's spend; false for tax, support and similar.</summary>
    public bool IsUsage { get; init; } = true;
}

/// <summary>Well-known <see cref="DailySpendBreakdownRecord.Category"/> labels the code itself relies on.</summary>
public static class SpendCategories
{
    /// <summary>The seat or subscription fee: the part of a day's spend that is the daily record's SeatFee.</summary>
    public const string Seat = "Seat";

    /// <summary>A usage category that is not otherwise classified.</summary>
    public const string Other = "Other";
}
