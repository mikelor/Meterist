using Meterist.Core.Models;
using Meterist.Core.Vendors;

namespace Meterist.Vendors.GeminiEnterprise;

/// <summary>
/// Turns Gemini's day-grouped billing rows into one <see cref="DailySpendBreakdownRecord"/> per day, category
/// and SKU. The category is the one the matching billing rule named, or, for a rule with none and for raw rows
/// stored before categories existed, the SKU-text classification (<see cref="GeminiCostBreakdown"/>). Rows flagged
/// as non-usage (tax, support, rounding) are stored with <c>IsUsage</c> false, so they explain the gap to the
/// invoice without ever entering a total. Cost is before credits, as in the daily record's GrossSpend.
/// <para>
/// The usage rows sum to the day's GrossSpend by construction: both read the same raw rows, and a row is
/// usage in one exactly when it is in the other. Rows with a zero cost are not stored.
/// </para>
/// </summary>
public sealed class GeminiEnterpriseBreakdownNormalizer : IVendorSpendBreakdownNormalizer
{
    public Guid VendorId => VendorCatalog.GeminiEnterprise.Id;

    public IReadOnlyList<DailySpendBreakdownRecord> Normalize(RawVendorSpendData rawData)
    {
        var records = new List<DailySpendBreakdownRecord>();

        foreach (var (date, rows) in rawData.RecordsByDate.OrderBy(kvp => kvp.Key))
        {
            var lines = rows
                .Select(row => (
                    Category: GeminiCostBreakdown.CategoryOf(row),
                    Detail: row.TryGetValue(GeminiBillingRowFields.SkuDescription, out var sku) ? sku as string ?? string.Empty : string.Empty,
                    IsUsage: GeminiCostBreakdown.IsUsage(row),
                    Cost: ToDecimal(row.TryGetValue(GeminiBillingRowFields.Cost, out var cost) ? cost : null)))
                // One row per (category, detail) a day: the same SKU under two services would otherwise collide.
                .GroupBy(l => (l.Category, l.Detail, l.IsUsage))
                .Select(g => (g.Key.Category, g.Key.Detail, g.Key.IsUsage, Cost: g.Sum(l => l.Cost)))
                .Where(l => l.Cost != 0m);

            foreach (var line in lines)
            {
                records.Add(new DailySpendBreakdownRecord
                {
                    TenantId = rawData.TenantId,
                    VendorId = rawData.VendorId,
                    Date = date,
                    Category = line.Category,
                    Detail = line.Detail,
                    Amount = line.Cost,
                    IsUsage = line.IsUsage,
                });
            }
        }

        return records;
    }

    private static decimal ToDecimal(object? value) => value switch
    {
        null => 0m,
        decimal d => d,
        _ => Convert.ToDecimal(value),
    };
}
