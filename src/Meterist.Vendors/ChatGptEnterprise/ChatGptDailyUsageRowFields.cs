namespace Meterist.Vendors.ChatGptEnterprise;

/// <summary>
/// Which source a raw ChatGPT row came from. Rows are tagged under
/// <see cref="ChatGptDailyUsageRowFields.Source"/>. A row with no tag is a
/// <see cref="CostsExport"/> row: that is the only shape stored before the
/// Daily Usage API was added, and those raw records must keep normalizing.
/// </summary>
public static class ChatGptRowSources
{
    public const string DailyUsage = "DailyUsage";

    public const string CostsExport = "CostsExport";

    /// <summary>
    /// One user's Daily Usage API row for one day (default grouping). Stored beside the
    /// workspace-wide <see cref="DailyUsage"/> row so per-user usage can be normalized later
    /// without another API call, and so a day can be reconciled against its workspace total.
    /// </summary>
    public const string DailyUsageUser = "DailyUsageUser";
}

/// <summary>
/// Dictionary keys for a raw row that came from the Daily Usage API: one
/// workspace-wide row per day, holding that day's totals. Counterpart to
/// <see cref="ChatGptCostRowFields"/> for COSTS billing lines.
/// </summary>
public static class ChatGptDailyUsageRowFields
{
    public const string Source = "Source";
    public const string Chats = "Chats";
    public const string Messages = "Messages";
    public const string Credits = "Credits";
    public const string CostUsd = "CostUsd";
    public const string EstimatedCostUsd = "EstimatedCostUsd";
    public const string UncachedTextInputTokens = "UncachedTextInputTokens";
    public const string CachedTextInputTokens = "CachedTextInputTokens";
    public const string TextOutputTokens = "TextOutputTokens";
    public const string TextTotalTokens = "TextTotalTokens";

    // Present on DailyUsageUser rows only. UserEmail is personal data.
    public const string UserId = "UserId";
    public const string UserEmail = "UserEmail";
    public const string ActorType = "ActorType";

    // The per-client and per-model breakdowns the API returns, kept as the vendor sent them
    // (a JSON array) for later use. Present on both DailyUsage and DailyUsageUser rows.
    public const string Clients = "Clients";
    public const string Models = "Models";
}
