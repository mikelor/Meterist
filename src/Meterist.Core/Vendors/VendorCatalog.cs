namespace Meterist.Core.Vendors;

/// <summary>
/// Single source of truth for vendor identity — the vendor set is small and
/// fixed in code (not tenant-created), so this is a compile-time catalog
/// rather than a database table. The CLI resolves `--vendor &lt;shortname&gt;`
/// arguments here; DI/orchestration match an IVendorSpendExtractor to its
/// IVendorSpendNormalizer by VendorId here too.
/// </summary>
public static class VendorCatalog
{
    public static readonly VendorIdentity ClaudeEnterprise = new(
        Guid.Parse("8f14e45f-ceea-467e-adde-3f82edcd1a11"), "claude-enterprise", "Claude Enterprise");

    public static readonly VendorIdentity ClaudeApiPlatform = new(
        Guid.Parse("c9e1a1a0-3b1e-4b8a-9d9e-2f6a1b0c9d22"), "claude-api-platform", "Claude API Platform");

    public static readonly VendorIdentity ChatGptEnterprise = new(
        Guid.Parse("3d6f1c2e-8a4b-4e3a-8b2f-7c5e9a1d4f33"), "chatgpt-enterprise", "ChatGPT Enterprise");

    public static readonly VendorIdentity GeminiEnterprise = new(
        Guid.Parse("a27b6d3c-1f9e-4a7d-9c3b-6e2d8f0a5b44"), "gemini-enterprise", "Gemini Enterprise");

    public static IReadOnlyList<VendorIdentity> All { get; } =
    [
        ClaudeEnterprise,
        ClaudeApiPlatform,
        ChatGptEnterprise,
        GeminiEnterprise,
    ];

    public static VendorIdentity? FindByShortName(string shortName) =>
        All.FirstOrDefault(v => string.Equals(v.ShortName, shortName, StringComparison.OrdinalIgnoreCase));

    public static VendorIdentity? FindById(Guid id) =>
        All.FirstOrDefault(v => v.Id == id);
}
