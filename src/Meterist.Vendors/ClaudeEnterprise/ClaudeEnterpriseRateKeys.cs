namespace Meterist.Vendors.ClaudeEnterprise;

/// <summary>
/// The RateType/ModelOrSku string conventions ClaudeEnterpriseSpendNormalizer
/// looks for in VendorRateConfig rows. Only the seat side is needed — unlike
/// ChatGPT Enterprise, this vendor's Analytics API already returns real
/// dollar cost, so there's no credit-to-USD conversion rate to configure.
/// Reusing the same literal strings as ChatGptRateKeys is safe: rows are
/// always scoped by VendorId first.
/// </summary>
public static class ClaudeEnterpriseRateKeys
{
    public const string PerSeatRateType = "per-seat";

    public const string SeatModelOrSku = "seat";
}
