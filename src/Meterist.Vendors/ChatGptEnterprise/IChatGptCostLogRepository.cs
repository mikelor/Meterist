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
/// <summary>
/// EffectiveStart is the start date the query actually used -- may be later
/// than the requested period's Start if the vendor's 29-day retention
/// window clamped it. Callers must treat any day before EffectiveStart as
/// unknown, never as confirmed zero, since the vendor can no longer tell us
/// either way.
/// </summary>
public sealed record ChatGptCostQueryResult(
    DateOnly EffectiveStart,
    IReadOnlyList<IReadOnlyDictionary<string, object?>> Rows);

public interface IChatGptCostLogRepository
{
    /// <summary>
    /// Returns one row per billing line across all deduped COSTS events in the
    /// period, keyed by <see cref="ChatGptCostRowFields"/>, alongside the
    /// EffectiveStart the query actually used -- see <see cref="ChatGptCostQueryResult"/>.
    /// </summary>
    Task<ChatGptCostQueryResult> QueryCostRowsAsync(
        ChatGptCredential credential, DateRange period, CancellationToken cancellationToken = default);
}
