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
}
