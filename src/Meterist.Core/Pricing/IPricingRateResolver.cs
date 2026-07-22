using Meterist.Core.Models;

namespace Meterist.Core.Pricing;

/// <summary>
/// Resolves the effective rate for a vendor + tenant + model/SKU + date,
/// preferring a tenant-specific <see cref="VendorRateConfig"/> override over
/// the public default. Not in the critical path for vendors whose API
/// already returns dollar cost directly — only load-bearing where an
/// adapter falls back to raw usage counts, or for seat-fee configuration
/// (which no vendor exposes via API at all).
/// </summary>
public interface IPricingRateResolver
{
    Task<VendorRateConfig?> ResolveRateAsync(
        string tenantId,
        string vendorName,
        string? modelOrSku,
        DateOnly effectiveDate,
        CancellationToken cancellationToken = default);
}
