using System.Globalization;
using System.Net;
using System.Text;
using Meterist.Core.Auditing;
using Meterist.Core.Models;
using Meterist.Core.Vendors;

namespace Meterist.Core.Reporting;

/// <summary>
/// The people-level sections of the Client Spend &amp; Benchmark Report: who holds and uses seats, who spends the most, the
/// per-user breakdown beneath each vendor's aggregate, and idle seats. Each section is left out when the stored data has
/// nothing for it, and every figure is shown with what it is: estimated (a price derived from credits, or an allocation),
/// key-level (usage that attributes to an API key, not a person) or unattributed.
/// <para>
/// These sections name people (name and email), by operator decision, so a populated report is personal data and stays
/// local under <c>artifacts/</c>. Per-user dollars are usage only; seat fees are a flat charge and are not attributed to
/// individuals. Each vendor's per-user total is shown beside the vendor's aggregate usage for the same days.
/// </para>
/// </summary>
internal static class UserSectionsRenderer
{
    private static readonly CultureInfo Culture = CultureInfo.InvariantCulture;

    // Rows shown per vendor before the rest are summed into one line: the totals still include everyone.
    private const int PerUserRowsShown = 25;
    private const int TopConsumersShown = 10;
    private const int IdleRowsShown = 40;

    public static void Render(StringBuilder html, ReportData data)
    {
        RenderActiveUsersAndSeats(html, data);
        RenderTopConsumers(html, data);
        RenderPerUserUsage(html, data);
        RenderIdleSeats(html, data);
    }

    // ---- active users and seat utilisation

    private static void RenderActiveUsersAndSeats(StringBuilder html, ReportData data)
    {
        var tenants = data.Tenants
            .Select(t => (t.TenantId, Vendors: t.Vendors.Where(v => v.Seats is not null || v.Activity is not null).ToList()))
            .Where(t => t.Vendors.Count > 0)
            .ToList();

        if (tenants.Count == 0)
        {
            return;
        }

        html.Append("""
              <section>
                <div class="eyebrow">Who is using it</div>
                <h2>Active users and seat utilization</h2>
                <p class="audit-intro"><b>Billed</b> is the seat count in the rate configuration. <b>Vendor-reported</b> is the vendor's own assigned-seat count where it gives one, otherwise the size of the latest roster snapshot. <b>Active</b> is the number of rostered people with any spend, credits or requests in the 30 days ending on the last day of the report; <b>idle</b> is the rest, not counting people added inside those 30 days. Utilization is active people divided by billed seats and by roster members.</p>

            """);

        foreach (var (tenantId, vendors) in tenants)
        {
            html.Append($"    <h3 class=\"subsection-tenant\">{Encode(tenantId)}</h3>\n    <div class=\"table-scroll\">\n      <table>\n        <thead>\n          <tr>\n            <th>Vendor</th>\n            <th class=\"figure\">Billed seats</th>\n            <th class=\"figure\">Vendor-reported</th>\n            <th class=\"figure\">Roster</th>\n            <th class=\"figure\">Active</th>\n            <th class=\"figure\">Idle</th>\n            <th class=\"figure\">Of billed</th>\n            <th class=\"figure\">Of roster</th>\n            <th>Vendor's own active users</th>\n          </tr>\n        </thead>\n        <tbody>\n");

            foreach (var v in vendors)
            {
                var seats = v.Seats;
                var idle = seats?.Idle;
                var assessed = idle is { Assessable: true };

                var reported = seats?.VendorReportedSeats is { } r
                    ? $"{r} <span class=\"muted-dash\">({Encode(seats.VendorReportedSource ?? string.Empty)})</span>"
                    : Dash();

                var ofBilled = assessed && seats!.BilledSeats is > 0 ? Percent(idle!.ActiveCount, seats.BilledSeats.Value) : Dash();
                var ofRoster = assessed && seats!.RosterCount is > 0 ? Percent(idle!.ActiveCount, seats.RosterCount.Value) : Dash();

                html.Append("          <tr>\n");
                html.Append($"            <th scope=\"row\">{Encode(v.Vendor.DisplayName)}</th>\n");
                html.Append($"            <td class=\"figure tabular\">{(seats?.BilledSeats is { } b ? b.ToString(Culture) : Dash())}</td>\n");
                html.Append($"            <td class=\"figure tabular\">{reported}</td>\n");
                html.Append($"            <td class=\"figure tabular\">{(seats?.RosterCount is { } rc ? rc.ToString(Culture) : Dash())}</td>\n");
                html.Append($"            <td class=\"figure tabular\">{(assessed ? idle!.ActiveCount.ToString(Culture) : Dash())}</td>\n");
                html.Append($"            <td class=\"figure tabular\">{(assessed ? idle!.IdleSeats.Count.ToString(Culture) : Dash())}</td>\n");
                html.Append($"            <td class=\"figure tabular\">{ofBilled}</td>\n");
                html.Append($"            <td class=\"figure tabular\">{ofRoster}</td>\n");
                html.Append($"            <td>{VendorActivityText(v.Activity)}</td>\n");
                html.Append("          </tr>\n");
            }

            html.Append("        </tbody>\n      </table>\n    </div>\n");

            foreach (var v in vendors.Where(x => x.Seats is not null))
            {
                AppendSeatNotes(html, v);
            }
        }

        html.Append("  </section>\n\n");
    }

    private static void AppendSeatNotes(StringBuilder html, VendorReportData v)
    {
        var seats = v.Seats!;
        var name = Encode(v.Vendor.DisplayName);

        if (seats.HasDrift)
        {
            html.Append($"    <p class=\"small-note\"><span class=\"risk-flag\">Seat drift</span> {name}: {seats.BilledSeats} seat(s) are billed in the rate configuration but the vendor reports {seats.VendorReportedSeats} ({Encode(seats.VendorReportedSource ?? string.Empty)}). The rate card may be stale.</p>\n");
        }
        else if (seats.UnusedBilledSeats is { } unused)
        {
            html.Append($"    <p class=\"small-note\">{name}: {unused} of the {seats.BilledSeats} billed seat(s) have no member on the roster (committed but unused, or not yet provisioned). Not treated as drift.</p>\n");
        }

        foreach (var note in seats.Notes)
        {
            html.Append($"    <p class=\"small-note\">{name}: {Encode(note)}</p>\n");
        }

        if (!seats.Idle.Assessable)
        {
            html.Append($"    <p class=\"small-note\">{name}: idle seats not assessed. {Encode(seats.Idle.Reason ?? string.Empty)}</p>\n");
        }
        else if (seats.Idle.Reason is not null)
        {
            html.Append($"    <p class=\"small-note\">{name}: caveat. {Encode(seats.Idle.Reason)}</p>\n");
        }
    }

    private static string VendorActivityText(ActivityOverview? a)
    {
        if (a is null)
        {
            return Dash();
        }

        var parts = new List<string>();
        if (a.DailyActive is { } d) parts.Add($"{d} daily");
        if (a.WeeklyActive is { } w) parts.Add($"{w} weekly");
        if (a.MonthlyActive is { } m) parts.Add($"{m} monthly");
        if (parts.Count == 0)
        {
            return Dash();
        }

        return $"{string.Join(" · ", parts)} <span class=\"muted-dash\">(as of {FormatDate(a.AsOf)})</span>";
    }

    // ---- top consumers

    private static void RenderTopConsumers(StringBuilder html, ReportData data)
    {
        var tenants = new List<(string TenantId, List<Consumer> Consumers)>();
        foreach (var tenant in data.Tenants)
        {
            var consumers = Consumers(tenant).Where(c => c.Amount > 0m).Take(TopConsumersShown).ToList();
            if (consumers.Count > 0)
            {
                tenants.Add((tenant.TenantId, consumers));
            }
        }

        if (tenants.Count == 0)
        {
            return;
        }

        html.Append("""
              <section>
                <div class="eyebrow">Who is using it</div>
                <h2>Top consumers by spend</h2>
                <p class="audit-intro">People ranked by usage dollars across all vendors, matched by email where a vendor gives one. <b>Usage only:</b> seat fees are a flat charge and are not attributed to individuals, and on a seat-based plan usage dollars are zero by design, so a person with heavy use but no usage charge does not appear here (see the per-user tables for requests). A figure marked <span class="muted-dash">(est.)</span> is estimated, not vendor-reported dollars.</p>

            """);

        foreach (var (tenantId, consumers) in tenants)
        {
            html.Append($"    <h3 class=\"subsection-tenant\">{Encode(tenantId)}</h3>\n    <div class=\"table-scroll\">\n      <table>\n        <thead>\n          <tr>\n            <th class=\"figure\">#</th>\n            <th>Member</th>\n            <th class=\"figure\">Usage spend</th>\n            <th>By vendor</th>\n          </tr>\n        </thead>\n        <tbody>\n");

            var rank = 1;
            foreach (var c in consumers)
            {
                var split = string.Join("; ", c.ByVendor.Select(kv =>
                    $"{Encode(kv.Key.DisplayName)} {Money(kv.Value.Amount)}{(kv.Value.Estimated ? " <span class=\"muted-dash\">(est.)</span>" : string.Empty)}"));
                html.Append($"          <tr>\n            <td class=\"figure tabular\">{rank++}</td>\n            <th scope=\"row\">{Who(c.Name, c.Email)}</th>\n            <td class=\"figure tabular\">{Money(c.Amount)}{(c.Estimated ? " <span class=\"muted-dash\">(est.)</span>" : string.Empty)}</td>\n            <td>{split}</td>\n          </tr>\n");
            }

            html.Append("        </tbody>\n      </table>\n    </div>\n");
        }

        html.Append("  </section>\n\n");
    }

    private sealed record Consumer(
        string Name, string? Email, decimal Amount, bool Estimated,
        IReadOnlyDictionary<VendorIdentity, (decimal Amount, bool Estimated)> ByVendor);

    // Persons only: usage attributed to an API key or left unattributed is not a person's spend. Matched across vendors by
    // email (case-insensitive) where there is one, otherwise kept per vendor and id.
    private static IEnumerable<Consumer> Consumers(TenantReportData tenant)
    {
        var people = new Dictionary<string, (string Name, string? Email, Dictionary<VendorIdentity, (decimal Amount, bool Estimated)> ByVendor)>(StringComparer.Ordinal);

        foreach (var vendor in tenant.Vendors.Where(v => v.UserUsage is not null))
        {
            foreach (var user in vendor.UserUsage!.Users.Where(u => u.Attribution == UsageAttribution.Person))
            {
                var key = !string.IsNullOrWhiteSpace(user.Email)
                    ? "email:" + user.Email.Trim().ToUpperInvariant()
                    : $"id:{vendor.Vendor.Id}:{user.UserId}";

                if (!people.TryGetValue(key, out var person))
                {
                    person = (user.DisplayName, user.Email, new Dictionary<VendorIdentity, (decimal, bool)>());
                    people[key] = person;
                }

                person.ByVendor[vendor.Vendor] = (user.Amount, user.IsEstimated);
            }
        }

        return people.Values
            .Select(p => new Consumer(
                p.Name, p.Email, p.ByVendor.Values.Sum(v => v.Amount), p.ByVendor.Values.Any(v => v.Estimated),
                p.ByVendor.OrderByDescending(kv => kv.Value.Amount).ToDictionary(kv => kv.Key, kv => kv.Value)))
            .OrderByDescending(c => c.Amount)
            .ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase);
    }

    // ---- per-user breakdown beneath each vendor's aggregate

    private static void RenderPerUserUsage(StringBuilder html, ReportData data)
    {
        var tenants = data.Tenants
            .Select(t => (t.TenantId, Vendors: t.Vendors.Where(v => v.UserUsage is not null).ToList()))
            .Where(t => t.Vendors.Count > 0)
            .ToList();

        if (tenants.Count == 0)
        {
            return;
        }

        html.Append("""
              <section>
                <div class="eyebrow">Beneath the aggregate</div>
                <h2>Usage by person</h2>
                <p class="audit-intro">For each vendor that reports it, the people behind the usage figure above, named, with spend, credits, requests and days active. <b>Usage only</b>: the seat fee is a flat charge and is not attributed to individuals. Each table ends with the vendor's own aggregate usage for the same days, so the two can be checked against each other. <span class="muted-dash">(est.)</span> marks a price estimated from credits rather than reported in dollars; <b>API key</b> rows are usage the vendor attributes to a key, not a person; <b>Unattributed</b> rows are usage the vendor did not tie to anyone.</p>

            """);

        foreach (var (tenantId, vendors) in tenants)
        {
            html.Append($"    <h3 class=\"subsection-tenant\">{Encode(tenantId)}</h3>\n");

            foreach (var v in vendors)
            {
                AppendVendorUsageTable(html, v);
            }
        }

        html.Append("    <p class=\"small-note\">These tables name people. The report is personal data: it is generated under the git-ignored <code>artifacts/</code> folder, and is not to be committed or shared outside the engagement.</p>\n");
        html.Append("  </section>\n\n");
    }

    private static void AppendVendorUsageTable(StringBuilder html, VendorReportData v)
    {
        var usage = v.UserUsage!;
        var hasCredits = usage.Users.Any(u => u.Credits is not null);
        var hasRequests = usage.Users.Any(u => u.Requests is not null);

        html.Append($"    <h4 class=\"vendor-subhead\">{Encode(v.Vendor.DisplayName)} <span class=\"muted-dash\">· {Encode(SourceNote(v.Vendor))}</span></h4>\n");
        html.Append("    <div class=\"table-scroll\">\n      <table>\n        <thead>\n          <tr>\n            <th>Member</th>\n            <th>Counted as</th>\n            <th class=\"figure\">Usage spend</th>\n");
        if (hasCredits) html.Append("            <th class=\"figure\">Credits</th>\n");
        if (hasRequests) html.Append("            <th class=\"figure\">Requests</th>\n");
        html.Append("            <th class=\"figure\">Days active</th>\n          </tr>\n        </thead>\n        <tbody>\n");

        foreach (var user in usage.Users.Take(PerUserRowsShown))
        {
            AppendUserRow(html, user, hasCredits, hasRequests);
        }

        var rest = usage.Users.Skip(PerUserRowsShown).ToList();
        if (rest.Count > 0)
        {
            html.Append($"          <tr>\n            <th scope=\"row\">{rest.Count} other(s)</th>\n            <td></td>\n            <td class=\"figure tabular\">{Money(rest.Sum(u => u.Amount))}</td>\n");
            if (hasCredits) html.Append($"            <td class=\"figure tabular\">{NumberOrDash(rest.Any(u => u.Credits is not null) ? rest.Sum(u => u.Credits ?? 0m) : null)}</td>\n");
            if (hasRequests) html.Append($"            <td class=\"figure tabular\">{NumberOrDash(rest.Any(u => u.Requests is not null) ? rest.Sum(u => u.Requests ?? 0L) : null)}</td>\n");
            html.Append("            <td class=\"figure tabular\"></td>\n          </tr>\n");
        }

        html.Append($"          <tr>\n            <th scope=\"row\">All {usage.Users.Count} {(usage.Users.Count == 1 ? "row" : "rows")}</th>\n            <td></td>\n            <td class=\"figure tabular\"><b>{Money(usage.TotalAmount)}</b></td>\n");
        if (hasCredits) html.Append($"            <td class=\"figure tabular\"><b>{NumberOrDash(usage.TotalCredits)}</b></td>\n");
        if (hasRequests) html.Append($"            <td class=\"figure tabular\"><b>{NumberOrDash(usage.TotalRequests)}</b></td>\n");
        html.Append("            <td class=\"figure tabular\"></td>\n          </tr>\n");
        html.Append("        </tbody>\n      </table>\n    </div>\n");

        var rec = usage.Reconciliation;
        var days = $"{rec.DaysCompared} day(s) with per-user rows";
        if (rec.Reconciles)
        {
            html.Append($"    <p class=\"small-note\"><span class=\"good-flag\">Reconciles</span> Per-user usage {Money(rec.PerUserAmount)} matches {Encode(v.Vendor.DisplayName)}'s aggregate usage {Money(rec.AggregateUsage)} for the same {days}, within rounding.");
        }
        else
        {
            html.Append($"    <p class=\"small-note\"><span class=\"risk-flag\">Does not reconcile</span> Per-user usage {Money(rec.PerUserAmount)} differs from {Encode(v.Vendor.DisplayName)}'s aggregate usage {Money(rec.AggregateUsage)} for the same {days} by {Money(Math.Abs(rec.Difference))}.");
        }

        if (rec.UsageDaysWithoutUserRows > 0)
        {
            html.Append($" {rec.UsageDaysWithoutUserRows} day(s) with usage have no per-user rows, so their usage is not in this table.");
        }

        html.Append($" Seat fees ({Money(usage.AggregateSeatFee)}) are not attributed to people.</p>\n");
    }

    private static void AppendUserRow(StringBuilder html, UserUsageSummary user, bool hasCredits, bool hasRequests)
    {
        var amount = $"{Money(user.Amount)}{(user.IsEstimated ? " <span class=\"muted-dash\">(est.)</span>" : string.Empty)}";
        var counted = user.Attribution switch
        {
            UsageAttribution.Person => "<span class=\"muted-dash\">Person</span>",
            UsageAttribution.ApiKey => "API key (key-level)",
            UsageAttribution.Allocated => "Allocated (est.)",
            _ => "Unattributed",
        };

        html.Append($"          <tr>\n            <th scope=\"row\">{Who(user.Name, user.Email, user.UserId)}</th>\n            <td>{counted}</td>\n            <td class=\"figure tabular\">{amount}</td>\n");
        if (hasCredits) html.Append($"            <td class=\"figure tabular\">{NumberOrDash(user.Credits)}</td>\n");
        if (hasRequests) html.Append($"            <td class=\"figure tabular\">{NumberOrDash(user.Requests)}</td>\n");
        html.Append($"            <td class=\"figure tabular\">{user.DaysActive}</td>\n          </tr>\n");
    }

    // Where each vendor's per-user figures come from, so a reader can tell the dollars are reported or derived.
    private static string SourceNote(VendorIdentity vendor)
    {
        if (vendor.Id == VendorCatalog.ChatGptEnterprise.Id)
        {
            return "Daily Usage API; dollars are priced from credits at the configured rate (estimated)";
        }

        if (vendor.Id == VendorCatalog.ClaudeEnterprise.Id)
        {
            return "Analytics API; vendor-reported cost, requests count seat-covered use too";
        }

        if (vendor.Id == VendorCatalog.ClaudeApiPlatform.Id)
        {
            return "Admin API; usage by key, and by person only where personal keys are used";
        }

        return "per-user rows as stored";
    }

    // ---- idle seats

    private static void RenderIdleSeats(StringBuilder html, ReportData data)
    {
        var tenants = data.Tenants
            .Select(t => (t.TenantId, Vendors: t.Vendors.Where(v => v.Seats is { Idle: { Assessable: true, IdleSeats.Count: > 0 } }).ToList()))
            .Where(t => t.Vendors.Count > 0)
            .ToList();

        if (tenants.Count == 0)
        {
            return;
        }

        html.Append("""
              <section>
                <div class="eyebrow">Who is using it</div>
                <h2>Idle seats</h2>
                <p class="audit-intro">Rostered people with no spend, credits or requests in the 30 days ending on the last day of the report, longest idle first. A seat that was never seen active in the stored data shows <i>none stored</i>, which means no earlier activity is on file, not that there was none. People added inside the 30 days are not listed.</p>

            """);

        foreach (var (tenantId, vendors) in tenants)
        {
            html.Append($"    <h3 class=\"subsection-tenant\">{Encode(tenantId)}</h3>\n");

            foreach (var v in vendors)
            {
                var idle = v.Seats!.Idle;
                html.Append($"    <h4 class=\"vendor-subhead\">{Encode(v.Vendor.DisplayName)} <span class=\"muted-dash\">· {idle.IdleSeats.Count} idle of {v.Seats.RosterCount?.ToString(Culture) ?? "?"} on the roster</span></h4>\n");
                html.Append("    <div class=\"table-scroll\">\n      <table>\n        <thead>\n          <tr>\n            <th>Member</th>\n            <th>Role</th>\n            <th>Seat type</th>\n            <th>Last active before the window</th>\n          </tr>\n        </thead>\n        <tbody>\n");

                foreach (var seat in idle.IdleSeats.Take(IdleRowsShown))
                {
                    html.Append($"          <tr>\n            <th scope=\"row\">{Who(seat.Name, seat.Email, seat.MemberId)}</th>\n            <td>{Encode(seat.Role ?? "—")}</td>\n            <td>{Encode(seat.SeatType ?? "—")}</td>\n            <td class=\"tabular\">{(seat.LastActiveBeforeWindow is { } last ? FormatDate(last) : "none stored")}</td>\n          </tr>\n");
                }

                if (idle.IdleSeats.Count > IdleRowsShown)
                {
                    html.Append($"          <tr>\n            <th scope=\"row\">{idle.IdleSeats.Count - IdleRowsShown} more</th>\n            <td></td>\n            <td></td>\n            <td></td>\n          </tr>\n");
                }

                html.Append("        </tbody>\n      </table>\n    </div>\n");
            }
        }

        html.Append("  </section>\n\n");
    }

    // ---- helpers

    // "Name <email>" when both exist, otherwise whichever does, otherwise the vendor's id.
    private static string Who(string? name, string? email, string? fallbackId = null)
    {
        if (!string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(email) && !string.Equals(name, email, StringComparison.OrdinalIgnoreCase))
        {
            return $"{Encode(name)} <span class=\"muted-dash\">&lt;{Encode(email)}&gt;</span>";
        }

        return Encode(!string.IsNullOrWhiteSpace(name) ? name : !string.IsNullOrWhiteSpace(email) ? email : fallbackId ?? "unknown");
    }

    private static string Percent(int part, int whole) =>
        (100m * part / whole).ToString("0", Culture) + "%";

    private static string NumberOrDash(decimal? value) =>
        value is { } v ? v.ToString("#,##0.##", Culture) : Dash();

    private static string NumberOrDash(long? value) =>
        value is { } v ? v.ToString("#,##0", Culture) : Dash();

    private static string Dash() => "<span class=\"muted-dash\">—</span>";

    private static string FormatDate(DateOnly date) => date.ToString("MMM d, yyyy", Culture);

    private static string Money(decimal value) => value.ToString("C2", CultureInfo.GetCultureInfo("en-US"));

    private static string Encode(string value) => WebUtility.HtmlEncode(value);
}
