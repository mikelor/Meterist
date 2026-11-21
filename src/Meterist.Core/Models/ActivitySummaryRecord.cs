namespace Meterist.Core.Models;

/// <summary>
/// How many people were active on one day, as the vendor counts them, for a tenant and vendor. Natural key is
/// (TenantId, VendorId, Date, Product). It is aggregate data, with no names or emails, and is the vendor's own
/// figure rather than something Meterist derives.
/// <para>
/// One row per day for the whole organisation (<see cref="Product"/> is <see cref="AllProducts"/>) and one per
/// product the vendor breaks out (Claude: chat, claude_code, cowork, and so on). Per-product rows are rows, not
/// columns, so a product the vendor adds later is a new row rather than a migration. Seat, invite and adoption
/// figures are organisation-wide, so only the <see cref="AllProducts"/> row carries them.
/// </para>
/// <para>
/// Weekly and monthly counts are the trailing windows ending on <see cref="Date"/>, as the vendor defines them,
/// and are stored as given.
/// </para>
/// </summary>
public sealed class ActivitySummaryRecord
{
    /// <summary><see cref="Product"/> for the row that covers the whole organisation.</summary>
    public const string AllProducts = "all";

    public required string TenantId { get; init; }

    public required Guid VendorId { get; init; }

    public required DateOnly Date { get; init; }

    /// <summary>The product the counts are for, or <see cref="AllProducts"/>. Never null, so it can be part of a unique key.</summary>
    public string Product { get; init; } = AllProducts;

    public int? DailyActiveUsers { get; init; }

    public int? WeeklyActiveUsers { get; init; }

    public int? MonthlyActiveUsers { get; init; }

    /// <summary>Seats assigned that day. Organisation-wide, so only on the <see cref="AllProducts"/> row.</summary>
    public int? AssignedSeatCount { get; init; }

    /// <summary>Invitations sent and not yet accepted. Organisation-wide.</summary>
    public int? PendingInviteCount { get; init; }

    /// <summary>Active users as a percentage of assigned seats (30 means 30%), as the vendor reports it.</summary>
    public decimal? DailyAdoptionRate { get; init; }

    public decimal? WeeklyAdoptionRate { get; init; }

    public decimal? MonthlyAdoptionRate { get; init; }
}
