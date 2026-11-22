namespace Meterist.Vendors.ChatGptEnterprise;

/// <summary>
/// Settings for the ChatGPT <c>COSTS</c> compliance-log cross-check, bound from <c>Meterist:ChatGpt:CostsCrossCheck</c>
/// (appsettings, or environment variables such as <c>Meterist__ChatGpt__CostsCrossCheck__RecentDays</c>). The cross-check is
/// optional by design: the Daily Usage API is the source of record and the export only ever corroborates it, so none of
/// these settings can change a stored daily figure, the never-lower rule or the <c>Source</c> labels.
/// <para>
/// The export is hundreds of separate file downloads over a 30-day window, which is what these settings keep affordable
/// for a daily run. The defaults leave behaviour as it was (the whole retention window), only faster, because the files
/// are now fetched a few at a time.
/// </para>
/// </summary>
public sealed class ChatGptCostsCrossCheckOptions
{
    public const string ConfigurationSection = "Meterist:ChatGpt:CostsCrossCheck";

    public const int DefaultMaxParallelDownloads = 4;
    public const int MaxAllowedParallelDownloads = 16;

    /// <summary>
    /// False turns the cross-check off: the export is not read at all and the run output says it was skipped. Days then
    /// carry no <c>COSTS</c> rows in raw storage, and a day the Daily Usage API has no row for is stored as no usage
    /// instead of falling back to the export.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// When set, only the last this-many days of the extraction period are cross-checked (ending on the period's last day),
    /// so a daily run reads a few days of files instead of the whole retention window. Null (the default) is the whole
    /// retention window. Days outside it keep their Daily Usage figures but no longer get fresh <c>COSTS</c> rows in raw
    /// storage, and are not compared. Must be between 1 and 29 (the export keeps about 29 days).
    /// </summary>
    public int? RecentDays { get; set; }

    /// <summary>How many export files are downloaded at once. 1 is the old sequential behaviour. Must be 1 through 16.</summary>
    public int MaxParallelDownloads { get; set; } = DefaultMaxParallelDownloads;

    /// <summary>What is wrong with the settings, or null. Out-of-range values are an error, not quietly adjusted.</summary>
    public string? Problem =>
        RecentDays is { } days && (days < 1 || days > 29)
            ? $"{ConfigurationSection}:RecentDays must be between 1 and 29 (the export keeps about 29 days), not {days}."
            : MaxParallelDownloads is < 1 or > MaxAllowedParallelDownloads
                ? $"{ConfigurationSection}:MaxParallelDownloads must be between 1 and {MaxAllowedParallelDownloads}, not {MaxParallelDownloads}."
                : null;

    /// <summary>The parallelism to use (the setting, kept within range as a last guard).</summary>
    public int EffectiveParallelism => Math.Clamp(MaxParallelDownloads, 1, MaxAllowedParallelDownloads);

    /// <summary>
    /// The part of <paramref name="period"/> the cross-check covers: all of it, or its last <see cref="RecentDays"/> days.
    /// </summary>
    public Meterist.Core.Vendors.DateRange WindowFor(Meterist.Core.Vendors.DateRange period)
    {
        if (RecentDays is not { } days || days < 1)
        {
            return period;
        }

        var start = period.End.AddDays(1 - days);
        return start <= period.Start ? period : new Meterist.Core.Vendors.DateRange(start, period.End);
    }
}
