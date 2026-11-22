namespace Meterist.Core.Reporting;

public enum WeekChangeKind
{
    /// <summary>A figure rose by more than the tolerance.</summary>
    Increase,

    /// <summary>A figure fell by more than the tolerance. Stored usage is not supposed to fall, so this is the one to explain.</summary>
    Decrease,
}

/// <summary>One vendor week whose figures moved between two reports.</summary>
public sealed record WeekChange(
    string TenantId,
    string Vendor,
    DateOnly WeekStart,
    DateOnly WeekEnd,
    ManifestWeek Previous,
    ManifestWeek Current,
    WeekChangeKind Kind);

/// <summary>What comparing a report with the one before it found.</summary>
public sealed record ReportDiff(
    bool Compared,
    string? NotComparedReason,
    DateTime? PreviousGeneratedAt,
    DateOnly? PreviousPeriodStart,
    DateOnly? PreviousPeriodEnd,
    decimal Tolerance,
    int WeeksCompared,
    int WeeksOnlyInPrevious,
    int WeeksOnlyInCurrent,
    int WeeksCoveringDifferentDays,
    IReadOnlyList<WeekChange> Changes)
{
    public int Decreases => Changes.Count(c => c.Kind == WeekChangeKind.Decrease);

    public int Increases => Changes.Count(c => c.Kind == WeekChangeKind.Increase);

    /// <summary>A diff that was not made, with the reason, so the report says so instead of showing a clean bill.</summary>
    public static ReportDiff NotCompared(string reason, decimal tolerance) =>
        new(false, reason, null, null, null, tolerance, 0, 0, 0, 0, []);
}

/// <summary>
/// Compares a new report's weekly figures with the previous report's, the check that catches a vendor silently
/// returning less for days already stored. It compares weekly totals (seat fee, usage or overage, gross), not projections,
/// which move legitimately with the choice of weeks.
/// <para>
/// What is and is not a change. A week in only one of the two reports is new or aged out, not a change. Two reports can also
/// cover different days of the same week (a different start or end date): when the new one covers every day the old one did
/// and more, a rise is expected (a trailing partial week that has since completed) and only a fall is reported; when it
/// covers fewer or different days the two figures are not comparable and the week is counted, not flagged. A figure counts
/// as moved only past the tolerance, so cent-level noise does not alarm. Reports for a different set of tenants, or a
/// manifest of another version, are not compared at all, and the result says so.
/// </para>
/// </summary>
public static class ReportDiffer
{
    /// <summary>Half a dollar: well above the sub-cent noise of summed floating-point cost, well below a real change.</summary>
    public const decimal DefaultTolerance = 0.50m;

    public static ReportDiff Compare(ReportManifest previous, ReportManifest current, decimal tolerance = DefaultTolerance)
    {
        ArgumentNullException.ThrowIfNull(previous);
        ArgumentNullException.ThrowIfNull(current);
        if (tolerance < 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(tolerance), "The tolerance cannot be negative.");
        }

        if (previous.Version != current.Version)
        {
            return ReportDiff.NotCompared(
                $"the previous report's manifest is version {previous.Version} and this one is version {current.Version}", tolerance);
        }

        var previousTenants = previous.Tenants.Select(t => t.TenantId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var currentTenants = current.Tenants.Select(t => t.TenantId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!previousTenants.SetEquals(currentTenants))
        {
            return ReportDiff.NotCompared(
                "the previous report covered a different set of tenants "
                + $"({string.Join(", ", previousTenants.OrderBy(t => t, StringComparer.OrdinalIgnoreCase))}), so its figures are not a baseline for this one",
                tolerance);
        }

        var previousWeeks = Index(previous);
        var currentWeeks = Index(current);

        var changes = new List<WeekChange>();
        int compared = 0, differentDays = 0;

        foreach (var (key, nowEntry) in currentWeeks)
        {
            if (!previousWeeks.TryGetValue(key, out var beforeEntry))
            {
                continue;
            }

            var (now, before) = (nowEntry.Week, beforeEntry.Week);
            var coverageBefore = Coverage(before, previous);
            var coverageNow = Coverage(now, current);
            var sameDays = coverageBefore == coverageNow;
            var supersetOfBefore = coverageNow.Start <= coverageBefore.Start && coverageNow.End >= coverageBefore.End;

            if (!sameDays && !supersetOfBefore)
            {
                differentDays++;
                continue;
            }

            compared++;

            var deltas = new[]
            {
                now.SeatFee - before.SeatFee,
                now.UsageOrOverage - before.UsageOrOverage,
                now.GrossSpend - before.GrossSpend,
            };

            var fell = deltas.Any(d => d < -tolerance);
            var rose = deltas.Any(d => d > tolerance);

            // Covering more days than before explains a rise; it never explains a fall.
            if (fell)
            {
                changes.Add(new WeekChange(nowEntry.TenantId, key.Vendor, key.WeekStart, now.WeekEnd, before, now, WeekChangeKind.Decrease));
            }
            else if (rose && sameDays)
            {
                changes.Add(new WeekChange(nowEntry.TenantId, key.Vendor, key.WeekStart, now.WeekEnd, before, now, WeekChangeKind.Increase));
            }
        }

        var onlyInPrevious = previousWeeks.Keys.Count(k => !currentWeeks.ContainsKey(k));
        var onlyInCurrent = currentWeeks.Keys.Count(k => !previousWeeks.ContainsKey(k));

        var ordered = changes
            .OrderBy(c => c.Kind == WeekChangeKind.Decrease ? 0 : 1)
            .ThenBy(c => c.TenantId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(c => c.Vendor, StringComparer.Ordinal)
            .ThenBy(c => c.WeekStart)
            .ToList();

        return new ReportDiff(
            true, null, previous.GeneratedAt, previous.PeriodStart, previous.PeriodEnd, tolerance,
            compared, onlyInPrevious, onlyInCurrent, differentDays, ordered);
    }

    private readonly record struct WeekKey(string Tenant, string Vendor, DateOnly WeekStart);

    private static Dictionary<WeekKey, (string TenantId, ManifestWeek Week)> Index(ReportManifest manifest)
    {
        var index = new Dictionary<WeekKey, (string TenantId, ManifestWeek Week)>();
        foreach (var tenant in manifest.Tenants)
        {
            foreach (var vendor in tenant.Vendors)
            {
                foreach (var week in vendor.Weeks)
                {
                    // Tenant ids compare case-insensitively everywhere else; fold here too. A repeated key keeps the first.
                    index.TryAdd(new WeekKey(tenant.TenantId.ToUpperInvariant(), vendor.Vendor, week.WeekStart), (tenant.TenantId, week));
                }
            }
        }

        return index;
    }

    // The days of the week a report actually counted: the week clipped to the report's own period.
    private static (DateOnly Start, DateOnly End) Coverage(ManifestWeek week, ReportManifest report) =>
        (week.WeekStart < report.PeriodStart ? report.PeriodStart : week.WeekStart,
         week.WeekEnd > report.PeriodEnd ? report.PeriodEnd : week.WeekEnd);
}
