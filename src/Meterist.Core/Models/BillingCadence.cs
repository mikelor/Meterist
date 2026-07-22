namespace Meterist.Core.Models;

/// <summary>
/// How often a seat/license rate is billed — needed to prorate a
/// VendorRateConfig's Rate down to a daily figure unambiguously (an enum
/// rather than string-matching "monthly" vs "annual" inside RateType).
/// </summary>
public enum BillingCadence
{
    Monthly,
    Annual,
    OneTime,
}
