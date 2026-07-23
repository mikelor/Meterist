using Meterist.Core.Models;

namespace Meterist.Core.Vendors;

/// <summary>
/// Maps one vendor's <see cref="RawVendorSpendData"/> into the normalized
/// <see cref="DailySpendRecord"/> shape shared by all vendors. <paramref
/// name="applicableRates"/> is resolved by
/// <see cref="Meterist.Core.Persistence.IVendorRateConfigRepository"/> for the
/// whole extraction period — most v1 vendors' APIs return dollar cost
/// directly and ignore it, but ChatGPT Enterprise's Cost API has no seat line
/// and no reliable dollar figure, so its normalizer resolves both the seat fee
/// and the credit-to-USD rate from this list.
/// </summary>
public interface IVendorSpendNormalizer
{
    Guid VendorId { get; }

    IReadOnlyList<DailySpendRecord> Normalize(
        RawVendorSpendData rawData,
        IReadOnlyList<VendorRateConfig> applicableRates);
}
