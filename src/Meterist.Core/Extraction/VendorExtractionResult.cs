using Meterist.Core.Persistence;

namespace Meterist.Core.Extraction;

public enum VendorExtractionStatus
{
    Succeeded,
    NotImplemented,
    Failed,

    /// <summary>Not attempted: the tenant has no credential stored for this vendor.</summary>
    Skipped,
}

/// <summary>
/// Per-vendor outcome of one SpendExtractionService run — distinguishes
/// "not built yet" from "actually broke" from "worked," per
/// docs/architecture.md §10's requirement that extraction status be visible
/// to the operator, not swallowed. <see cref="Changes"/> is what the run did
/// to each day (or, for a dry run, would do); null unless the vendor succeeded.
/// </summary>
public sealed record VendorExtractionResult(
    Guid VendorId,
    string DisplayName,
    VendorExtractionStatus Status,
    int RecordCount,
    string? Detail,
    IReadOnlyList<DailySpendChange>? Changes = null,
    UserUsageUpsertResult? UserUsage = null,
    BreakdownReplaceResult? Breakdown = null,
    ActivitySummaryUpsertResult? ActivitySummaries = null,
    UserActivityReplaceResult? UserActivity = null);

public static class DailySpendChangeExtensions
{
    /// <summary>One-line account of a breakdown replace, e.g. "3 inserted, 40 unchanged, 1 removed". Zero counts are left out.</summary>
    public static string Summarize(this BreakdownReplaceResult result)
    {
        var parts = new List<string>();
        if (result.Inserted > 0) parts.Add($"{result.Inserted} inserted");
        if (result.Updated > 0) parts.Add($"{result.Updated} updated");
        if (result.Removed > 0) parts.Add($"{result.Removed} removed");
        if (result.Unchanged > 0) parts.Add($"{result.Unchanged} unchanged");
        return parts.Count == 0 ? "none" : string.Join(", ", parts);
    }

    /// <summary>
    /// True when the run would add, change or remove anything other than the daily spend records: per-user rows, the kind
    /// breakdown, activity summaries or per-person activity. A dry run that only looks at spend days would say "nothing would
    /// change" over a Detail line reporting thousands of new rows.
    /// </summary>
    public static bool WouldChangeOtherStoredData(this VendorExtractionResult result) =>
        result.UserUsage is { } usage && usage.Inserted + usage.Updated > 0
        || result.Breakdown is { } breakdown && breakdown.Inserted + breakdown.Updated + breakdown.Removed > 0
        || result.ActivitySummaries is { } summaries && summaries.Inserted + summaries.Updated > 0
        || result.UserActivity is { } activity && activity.Inserted + activity.Updated + activity.Removed > 0;

    /// <summary>One-line account of a per-person activity replace, e.g. "6 inserted, 40 unchanged". Zero counts are left out.</summary>
    public static string Summarize(this UserActivityReplaceResult result)
    {
        var parts = new List<string>();
        if (result.Inserted > 0) parts.Add($"{result.Inserted} inserted");
        if (result.Updated > 0) parts.Add($"{result.Updated} updated");
        if (result.Removed > 0) parts.Add($"{result.Removed} removed");
        if (result.Unchanged > 0) parts.Add($"{result.Unchanged} unchanged");
        return parts.Count == 0 ? "none" : string.Join(", ", parts);
    }

    /// <summary>One-line account of an activity-summary upsert, e.g. "2 inserted, 40 unchanged". Zero counts are left out.</summary>
    public static string Summarize(this ActivitySummaryUpsertResult result)
    {
        var parts = new List<string>();
        if (result.Inserted > 0) parts.Add($"{result.Inserted} inserted");
        if (result.Updated > 0) parts.Add($"{result.Updated} updated");
        if (result.Unchanged > 0) parts.Add($"{result.Unchanged} unchanged");
        return parts.Count == 0 ? "none" : string.Join(", ", parts);
    }

    /// <summary>One-line account of a per-user upsert, e.g. "2 inserted, 40 unchanged". Zero counts are left out.</summary>
    public static string Summarize(this UserUsageUpsertResult result)
    {
        var parts = new List<string>();
        if (result.Inserted > 0) parts.Add($"{result.Inserted} inserted");
        if (result.Updated > 0) parts.Add($"{result.Updated} updated");
        if (result.Unchanged > 0) parts.Add($"{result.Unchanged} unchanged");
        return parts.Count == 0 ? "none" : string.Join(", ", parts);
    }

    /// <summary>
    /// One-line account of a set of changes, e.g. "3 inserted, 118 unchanged,
    /// 1 kept higher usage". Zero counts are left out. It exists so that a
    /// "Succeeded, N records" status can be checked against what actually
    /// changed instead of taken on trust.
    /// </summary>
    public static string Summarize(this IReadOnlyList<DailySpendChange> changes)
    {
        var parts = new List<string>();

        void Add(DailySpendChangeKind kind, string label)
        {
            var count = changes.Count(c => c.Kind == kind);
            if (count > 0)
            {
                parts.Add($"{count} {label}");
            }
        }

        Add(DailySpendChangeKind.Inserted, "inserted");
        Add(DailySpendChangeKind.Updated, "updated");
        Add(DailySpendChangeKind.Relabelled, "relabelled");
        Add(DailySpendChangeKind.KeptHigherUsage, "kept higher usage");
        Add(DailySpendChangeKind.Unchanged, "unchanged");

        return parts.Count == 0 ? "no days" : string.Join(", ", parts);
    }
}
