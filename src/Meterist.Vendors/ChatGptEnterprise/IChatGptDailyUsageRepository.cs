using Meterist.Core.Vendors;

namespace Meterist.Vendors.ChatGptEnterprise;

public enum ChatGptUsageGrouping
{
    /// <summary>One workspace-wide row per UTC day (<c>group=workspace</c>). Includes usage from users since deleted.</summary>
    Workspace,

    /// <summary>One row per user per UTC day with usage. Deleted users are omitted, so these rows can sum to less than the workspace total.</summary>
    PerUser,
}

/// <summary>
/// One row of the Daily Usage API (<c>GET /v1/analytics/workspaces/{workspace_id}/usage</c>).
/// <see cref="UserId"/> and <see cref="Email"/> are null for workspace-grouped rows.
/// Amounts are nullable because the API distinguishes "no data" from zero:
/// <see cref="Credits"/> is null on a USD-only day, <see cref="CostUsd"/> is
/// null on a credit-only day, and the token fields are omitted when no
/// eligible token usage exists. A reported zero stays zero.
/// <see cref="EstimatedCostUsd"/> estimates only the credit-priced portion.
/// </summary>
public sealed record ChatGptDailyUsageRow(
    DateOnly Day,
    string? UserId,
    string? Email,
    int Chats,
    int Messages,
    decimal? Credits,
    decimal? CostUsd,
    decimal? EstimatedCostUsd,
    long? UncachedTextInputTokens,
    long? CachedTextInputTokens,
    long? TextOutputTokens,
    long? TextTotalTokens);

/// <summary>
/// Seam between the ChatGPT adapter and the Daily Usage API, mirroring
/// <see cref="IChatGptCostLogRepository"/>: real access on one side
/// (<see cref="HttpChatGptDailyUsageRepository"/>), a WireMock.Net stub on the
/// other for tests.
/// </summary>
public interface IChatGptDailyUsageRepository
{
    /// <summary>
    /// Returns every row for the period (both ends inclusive), oldest first,
    /// following pagination to the end. A day with no usage produces no row.
    /// Recent days are eventually consistent and may be revised, so callers
    /// should re-fetch a trailing window rather than trust a day once seen.
    /// Requires <see cref="ChatGptCredential.WorkspaceId"/>.
    /// </summary>
    Task<IReadOnlyList<ChatGptDailyUsageRow>> QueryDailyUsageAsync(
        ChatGptCredential credential,
        DateRange period,
        ChatGptUsageGrouping grouping,
        CancellationToken cancellationToken = default);
}
