using Meterist.Core.Models;

namespace Meterist.Core.Reporting;

/// <summary>
/// One kind of charge totalled over a period: its amount, how many days it appeared on, and the lines
/// (usually SKUs) behind it. <see cref="IsUsage"/> false means a non-usage charge (tax, support) that is
/// shown for reconciliation and is never part of any total.
/// </summary>
public sealed record BreakdownCategoryTotal(
    string Category, bool IsUsage, decimal Amount, int Days, IReadOnlyList<string> Details);

public static class SpendBreakdownSummary
{
    /// <summary>
    /// Totals per category (and usage flag). Usage categories come first, the seat fee at the top and the rest
    /// by amount, then the non-usage ones by amount. A category whose rows net to zero is left out.
    /// </summary>
    public static IReadOnlyList<BreakdownCategoryTotal> Summarize(IEnumerable<DailySpendBreakdownRecord> rows) =>
        rows
            .GroupBy(r => (r.Category, r.IsUsage))
            .Select(g => new BreakdownCategoryTotal(
                g.Key.Category,
                g.Key.IsUsage,
                g.Sum(r => r.Amount),
                g.Where(r => r.Amount != 0m).Select(r => r.Date).Distinct().Count(),
                g.Select(r => r.Detail).Where(d => d.Length > 0).Distinct().OrderBy(d => d, StringComparer.Ordinal).ToList()))
            .Where(t => t.Amount != 0m)
            .OrderByDescending(t => t.IsUsage)
            .ThenByDescending(t => t.IsUsage && t.Category == SpendCategories.Seat)
            .ThenByDescending(t => t.Amount)
            .ThenBy(t => t.Category, StringComparer.Ordinal)
            .ToList();
}
