using Meterist.Core.Models;

namespace Meterist.Core.Vendors;

/// <summary>
/// Maps one vendor's <see cref="RawVendorSpendData"/> into the normalized
/// <see cref="DailySpendRecord"/> shape shared by all vendors. Applies a
/// resolved <see cref="VendorRateConfig"/> only where the vendor's own API
/// returns raw usage instead of dollars (e.g. Claude API Platform's
/// usage_report, if used instead of cost_report) — most v1 vendors already
/// return dollar cost directly and don't need rate resolution at this step.
/// </summary>
public interface IVendorSpendNormalizer
{
    Guid VendorId { get; }

    IReadOnlyList<DailySpendRecord> Normalize(
        RawVendorSpendData rawData,
        IReadOnlyList<VendorRateConfig> applicableRates);
}
