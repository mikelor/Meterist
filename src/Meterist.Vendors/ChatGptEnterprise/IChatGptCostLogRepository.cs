using Meterist.Core.Vendors;

namespace Meterist.Vendors.ChatGptEnterprise;

/// <summary>
/// Seam between ChatGptEnterpriseSpendExtractor and the OpenAI Programmatic
/// Admin Platform's COSTS compliance log export, per docs/architecture.md
/// §11: real access on one side (HttpChatGptCostLogRepository), a WireMock.Net
/// stub on the other for tests — this is the HTTP-vendor-shape case that
/// package was reserved for (unlike Gemini's BigQuery client, which isn't
/// sensible to mock directly).
/// </summary>
public interface IChatGptCostLogRepository
{
    /// <summary>
    /// Returns one row per billing line across all deduped COSTS events in the
    /// period, keyed by <see cref="ChatGptCostRowFields"/>.
    /// </summary>
    Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> QueryCostRowsAsync(
        ChatGptCredential credential, DateRange period, CancellationToken cancellationToken = default);
}
