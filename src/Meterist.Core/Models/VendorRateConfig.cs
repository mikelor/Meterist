namespace Meterist.Core.Models;

/// <summary>
/// A versioned rate for one vendor, resolved by effective date. A tenant-specific
/// override (non-null <see cref="TenantId"/>) takes precedence over the public
/// default (null <see cref="TenantId"/>) for the same vendor/model/date.
///
/// Also carries seat/license contract terms (SeatCount, BillingCadence) —
/// a seat term is the same *kind* of versioned tenant+vendor+date-scoped fact
/// a token rate is, so it reuses this model rather than a parallel table.
/// Not consumed by anything yet: no v1 vendor needs a rate-table lookup to
/// compute cost (three return dollars directly; Gemini's seat fee arrives
/// pre-prorated from BigQuery) — these fields are defined now, ahead of the
/// first vendor (Claude Enterprise or ChatGPT Enterprise) that will need to
/// derive a seat fee from contract terms rather than a vendor API.
/// </summary>
public sealed class VendorRateConfig
{
    public string? TenantId { get; init; }

    public required Guid VendorId { get; init; }

    public required string RateType { get; init; }

    public string? ModelOrSku { get; init; }

    public required decimal Rate { get; init; }

    // Only meaningful when RateType represents a per-seat rate. The billed
    // (e.g. committed, not necessarily active) seat count — see the ChatGPT
    // Enterprise 50-purchased/12-active example in vendor-integration-reference.md.
    public int? SeatCount { get; init; }

    public BillingCadence? BillingCadence { get; init; }

    public required DateOnly EffectiveFrom { get; init; }

    public DateOnly? EffectiveTo { get; init; }
}
