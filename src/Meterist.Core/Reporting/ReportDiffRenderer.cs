using System.Globalization;
using System.Net;
using System.Text;
using Meterist.Core.Vendors;

namespace Meterist.Core.Reporting;

/// <summary>
/// The report's "what changed since the last report" section. Shown only when there was a previous report to compare with;
/// says so plainly when nothing moved, and says why when the two could not be compared, so a clean-looking report never
/// hides a check that was not made.
/// </summary>
public static class ReportDiffRenderer
{
    private static readonly CultureInfo Culture = CultureInfo.InvariantCulture;

    public static void Render(StringBuilder html, ReportDiff? diff)
    {
        if (diff is null)
        {
            return;
        }

        html.Append("  <section>\n    <div class=\"eyebrow\">Against the previous report</div>\n    <h2>What changed since the last report</h2>\n");

        if (!diff.Compared)
        {
            html.Append($"    <p class=\"pending-note\">Not compared: {Encode(diff.NotComparedReason ?? "no usable previous report")}.</p>\n  </section>\n\n");
            return;
        }

        html.Append(
            $"    <p class=\"audit-intro\">Weekly totals against the report generated {diff.PreviousGeneratedAt:yyyy-MM-dd HH:mm} UTC "
            + $"({diff.PreviousPeriodStart:yyyy-MM-dd} to {diff.PreviousPeriodEnd:yyyy-MM-dd}). A figure counts as moved past {Money(diff.Tolerance)}. "
            + "Stored usage is not supposed to fall, so a <b>decrease</b> is the one to explain before trusting this report.</p>\n");

        if (diff.Changes.Count == 0)
        {
            html.Append($"    <p class=\"audit-intro\">No weekly figure moved ({diff.WeeksCompared} vendor weeks compared).</p>\n");
        }
        else
        {
            html.Append("    <div class=\"table-scroll\">\n      <table class=\"weekly-table\">\n        <thead>\n          <tr>\n"
                + "            <th>Change</th><th>Tenant</th><th>Vendor</th><th>Week</th>\n"
                + "            <th class=\"figure\">Previous</th><th class=\"figure\">Now</th>\n          </tr>\n        </thead>\n        <tbody>\n");
            foreach (var change in diff.Changes)
            {
                var label = change.Kind == WeekChangeKind.Decrease ? "<b>Decrease</b>" : "Increase";
                var vendor = VendorCatalog.FindByShortName(change.Vendor)?.DisplayName ?? change.Vendor;
                html.Append(
                    $"          <tr>\n            <td>{label}</td><td>{Encode(change.TenantId)}</td><td>{Encode(vendor)}</td>"
                    + $"<td>{change.WeekStart:MMM d}–{change.WeekEnd:MMM d}</td>\n"
                    + $"            <td class=\"figure tabular\">{Money(change.Previous.GrossSpend)}\n              <span class=\"breakdown\">seat {Money(change.Previous.SeatFee)} + usage {Money(change.Previous.UsageOrOverage)}</span></td>\n"
                    + $"            <td class=\"figure tabular\">{Money(change.Current.GrossSpend)}\n              <span class=\"breakdown\">seat {Money(change.Current.SeatFee)} + usage {Money(change.Current.UsageOrOverage)}</span></td>\n          </tr>\n");
            }

            html.Append("        </tbody>\n      </table>\n    </div>\n");
        }

        var notes = new List<string>();
        if (diff.WeeksOnlyInCurrent > 0)
        {
            notes.Add($"{diff.WeeksOnlyInCurrent} week(s) are new in this report");
        }

        if (diff.WeeksOnlyInPrevious > 0)
        {
            notes.Add($"{diff.WeeksOnlyInPrevious} week(s) were in the previous report only");
        }

        if (diff.WeeksCoveringDifferentDays > 0)
        {
            notes.Add($"{diff.WeeksCoveringDifferentDays} week(s) cover different days in the two reports and were not compared");
        }

        if (notes.Count > 0)
        {
            html.Append($"    <p class=\"audit-intro\">Not changes: {Encode(string.Join("; ", notes))}.</p>\n");
        }

        html.Append("  </section>\n\n");
    }

    private static string Money(decimal value) => "$" + value.ToString("N2", Culture);

    private static string Encode(string value) => WebUtility.HtmlEncode(value);
}
