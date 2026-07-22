namespace Meterist.Core.Models;

/// <summary>
/// A versioned rate for one vendor, resolved by effective date. A tenant-specific
/// override (non-null <see cref="TenantId"/>) takes precedence over the public
/// default (null <see cref="TenantId"/>) for the same vendor/model/date.
/// </summary>
public sealed class VendorRateConfig
{
    public string? TenantId { get; init; }

    public required string VendorName { get; init; }

    public required string RateType { get; init; }

    public string? ModelOrSku { get; init; }

    public required decimal Rate { get; init; }

    public required DateOnly EffectiveFrom { get; init; }

    public DateOnly? EffectiveTo { get; init; }
}
