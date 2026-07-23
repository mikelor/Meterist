using Meterist.Core.Models;
using Meterist.Core.Vendors;

namespace Meterist.Core.Persistence;

/// <summary>
/// Replaces the earlier, unconsumed Meterist.Core.Pricing.IPricingRateResolver
/// (single-date, single-modelOrSku signature that didn't compose with
/// IVendorSpendNormalizer.Normalize's whole-period applicableRates list).
/// ChatGPT Enterprise is the first real consumer: its Cost API has no seat
/// line and no reliable dollar figure, so seat fee and credit-to-usd
/// conversion both have to come from here.
/// </summary>
public interface IVendorRateConfigRepository
{
    Task AddAsync(VendorRateConfig rate, CancellationToken cancellationToken = default);

    /// <summary>
    /// Closes out any existing open-ended (EffectiveTo == null) row in the same
    /// scope (same TenantId — including both null for a public default — same
    /// VendorId, same ModelOrSku) whose EffectiveFrom is before
    /// <paramref name="newEffectiveFrom"/>, by setting its EffectiveTo to the
    /// day before. Called by the CLI's `rates set` before AddAsync so a
    /// contract renewal never leaves two overlapping windows for the same
    /// rate — GetApplicableRatesAsync/normalizers assume at most one row
    /// covers any given day per (tenant-or-public, VendorId, ModelOrSku).
    /// Returns the number of rows closed (0 if none needed closing).
    /// </summary>
    Task<int> CloseOpenEndedRateAsync(
        string? tenantId, Guid vendorId, string? modelOrSku, DateOnly newEffectiveFrom,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the VendorRateConfig rows whose [EffectiveFrom, EffectiveTo]
    /// window overlaps <paramref name="period"/> for this vendor. Tenant-specific
    /// rows (TenantId == tenantId) fully replace the public default (TenantId ==
    /// null) for the same ModelOrSku — a simpler v1 policy than day-by-day
    /// interval merging between an override and a default that both partially
    /// cover the period. A ModelOrSku can still have multiple time-versions
    /// within what's returned (a rate change mid-period) — callers pick, per
    /// day, whichever row's window actually covers that day.
    /// </summary>
    Task<IReadOnlyList<VendorRateConfig>> GetApplicableRatesAsync(
        string tenantId, Guid vendorId, DateRange period, CancellationToken cancellationToken = default);
}
