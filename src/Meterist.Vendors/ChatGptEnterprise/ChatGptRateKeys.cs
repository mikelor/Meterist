namespace Meterist.Vendors.ChatGptEnterprise;

/// <summary>
/// The RateType/ModelOrSku string conventions ChatGptEnterpriseSpendNormalizer
/// looks for in VendorRateConfig rows — a `rates set` invocation must use
/// these exact ModelOrSku values (see docs/user-guide.md) for the normalizer
/// to find them.
/// </summary>
public static class ChatGptRateKeys
{
    public const string PerSeatRateType = "per-seat";

    public const string SeatModelOrSku = "seat";

    public const string CreditToUsdRateType = "credit-to-usd";

    public const string CreditUsdModelOrSku = "credit-usd";
}
