using Meterist.Core.Models;

namespace Meterist.Core.Vendors;

/// <summary>
/// Turns one vendor's raw rows into per-user daily usage, alongside (and separate from) the
/// <see cref="IVendorSpendNormalizer"/> that produces the daily totals. Optional per vendor: a
/// vendor with no per-user data simply has none registered, and extraction treats that as normal.
/// <para>
/// Implementations must not log names or emails (see <see cref="UserDailyUsageRecord"/>); a warning
/// about a day or a count is fine, a warning that names a person is not.
/// </para>
/// </summary>
public interface IVendorUserUsageNormalizer
{
    Guid VendorId { get; }

    /// <summary>
    /// Per-user rows for every day in <paramref name="rawData"/> that has any. A day with no
    /// per-user rows contributes nothing (the vendors return rows only for users with usage).
    /// <paramref name="applicableRates"/> is the same list the daily normalizer receives, for
    /// vendors whose per-user dollars need a configured rate.
    /// </summary>
    IReadOnlyList<UserDailyUsageRecord> Normalize(
        RawVendorSpendData rawData,
        IReadOnlyList<VendorRateConfig> applicableRates);
}
