using System.Globalization;
using System.Net;
using System.Text;
using Meterist.Core.Models;
using Meterist.Core.Vendors;

namespace Meterist.Core.Reporting;

/// <summary>
/// Renders a <see cref="ReportData"/> into the "Client Spend &amp; Benchmark
/// Report" HTML, reusing the design system documented in
/// docs/client-report-generation.md (see <see cref="ReportStyles"/>). Raw
/// string composition, no templating engine — matches the project's existing
/// convention.
///
/// Two sections from the hand-built report are intentionally NOT generated
/// here: the "headline finding" callout (an inherently 2-way, judgment-based
/// pick of "one genuinely interesting fact") and "audit &amp; reconciliation
/// status" (verification basis like "invoice-verified" isn't tracked
/// anywhere in the data model). Both are left as a short "pending manual
/// entry" placeholder — a human can still hand-edit them in afterward.
/// </summary>
public static class ClientBenchmarkReportRenderer
{
    private static readonly CultureInfo Culture = CultureInfo.InvariantCulture;

    public static string Render(ReportData data)
    {
        var totalWeekCount = TotalWeekCount(data);

        var html = new StringBuilder();
        html.Append("<title>Client Spend &amp; Benchmark Report — Meterist</title>\n");
        html.Append("<style>\n").Append(ReportStyles.Css).Append("\n</style>\n\n");
        html.Append("<div class=\"page\">\n\n");

        RenderMasthead(html, data);
        RenderKpiCards(html, data);
        RenderVendorLedger(html, data);
        RenderConcentrationRisk(html, data);
        RenderRateCard(html, data);
        RenderWeeklySpend(html, data, totalWeekCount);
        RenderSpendByKind(html, data);
        UserSectionsRenderer.Render(html, data);
        RenderProjections(html, data, totalWeekCount);
        RenderPendingSections(html);
        RenderFootnote(html, data);

        html.Append("</div>\n");
        return html.ToString();
    }

    private static void RenderMasthead(StringBuilder html, ReportData data)
    {
        html.Append("""
              <header class="masthead">
                <div class="masthead-top">
                  <span class="wordmark">Meterist — Client Spend Report</span>
                  <span class="draft-chip">Draft · Auto-generated</span>
                </div>
                <h1>Client Spend &amp; Benchmark Report</h1>
                <div class="masthead-meta">
            """);
        html.Append($"      <span>Tracked extraction window: {FormatDate(data.Period.Start)} – {FormatDate(data.Period.End)}</span>\n");
        html.Append($"      <span>Tenants: {string.Join(", ", data.Tenants.Select(t => Encode(t.TenantId)))}</span>\n");
        html.Append($"      <span>Vendors: {string.Join(", ", VendorCatalog.All.Select(v => Encode(v.DisplayName)))}</span>\n");
        html.Append("    </div>\n");
        html.Append($"    <div class=\"generated-at\">Generated {data.GeneratedAt.ToString("MMM d, yyyy 'at' h:mm tt", Culture)}</div>\n");
        html.Append("""
                <div class="tick-rule"></div>
              </header>

            """);
    }

    private static void RenderKpiCards(StringBuilder html, ReportData data)
    {
        var days = data.Period.End.DayNumber - data.Period.Start.DayNumber + 1;

        html.Append("  <section>\n    <div class=\"eyebrow\">Total tracked spend</div>\n    <div class=\"kpi-grid\">\n");
        foreach (var tenant in data.Tenants)
        {
            var vendorsWithSpend = tenant.Vendors.Count(v => v.TotalGrossSpend > 0m);
            var perDay = days > 0 ? tenant.TotalGrossSpend / days : 0m;
            html.Append("      <div class=\"kpi-card\">\n");
            html.Append($"        <div class=\"kpi-label\">{Encode(tenant.TenantId)}</div>\n");
            html.Append($"        <div class=\"kpi-value tabular\">{Money(tenant.TotalGrossSpend)}</div>\n");
            html.Append($"        <div class=\"kpi-sub\">{vendorsWithSpend} vendor(s) · {FormatDate(data.Period.Start)}–{FormatDate(data.Period.End)} · avg {Money(perDay)}/day</div>\n");
            html.Append("      </div>\n");
        }
        html.Append("    </div>\n  </section>\n\n");
    }

    private static void RenderVendorLedger(StringBuilder html, ReportData data)
    {
        html.Append("  <section>\n    <div class=\"eyebrow\">Cross-tenant benchmark</div>\n    <h2>The vendor ledger</h2>\n");
        html.Append("    <div class=\"table-scroll\">\n      <table>\n        <thead>\n          <tr>\n            <th>Vendor</th>\n");
        foreach (var tenant in data.Tenants)
        {
            html.Append($"            <th class=\"figure\">{Encode(tenant.TenantId)}</th>\n");
        }
        html.Append("          </tr>\n        </thead>\n        <tbody>\n");

        foreach (var vendor in VendorCatalog.All)
        {
            html.Append($"          <tr>\n            <th scope=\"row\">{Encode(vendor.DisplayName)}</th>\n");
            foreach (var tenant in data.Tenants)
            {
                var vendorData = tenant.Vendors.Single(v => v.Vendor.Id == vendor.Id);
                if (vendorData.TotalGrossSpend == 0m && vendorData.Weeks.Count == 0)
                {
                    html.Append("            <td class=\"figure tabular\"><span class=\"muted-dash\">—</span></td>\n");
                }
                else
                {
                    html.Append($"            <td class=\"figure tabular\">{Money(vendorData.TotalGrossSpend)}\n");
                    html.Append($"              <span class=\"breakdown\">seat {Money(vendorData.TotalSeatFee)} + usage {Money(vendorData.TotalUsageOrOverage)}</span>\n            </td>\n");
                }
            }
            html.Append("          </tr>\n");
        }

        html.Append("        </tbody>\n      </table>\n    </div>\n  </section>\n\n");
    }

    private static void RenderConcentrationRisk(StringBuilder html, ReportData data)
    {
        html.Append("  <section>\n    <div class=\"eyebrow\">Diversification risk</div>\n    <h2>Vendor concentration</h2>\n    <div class=\"concentration-grid\">\n\n");

        foreach (var tenant in data.Tenants)
        {
            var total = tenant.TotalGrossSpend;
            var withShare = tenant.Vendors
                .Where(v => v.TotalGrossSpend > 0m)
                .OrderByDescending(v => v.TotalGrossSpend)
                .ToList();

            var maxPercent = total > 0m ? withShare.Max(v => v.TotalGrossSpend) / total * 100m : 0m;
            var flag = maxPercent >= 70m
                ? $"<span class=\"risk-flag\">concentration risk — {maxPercent:0.#}% in one vendor</span>"
                : "<span class=\"good-flag\">no vendor above 70%</span>";

            html.Append("      <div class=\"concentration-row\">\n        <div class=\"concentration-head\">\n");
            html.Append($"          <span class=\"concentration-tenant\">{Encode(tenant.TenantId)}</span>\n          {flag}\n        </div>\n");
            html.Append("        <div class=\"bar\">\n");
            foreach (var v in withShare)
            {
                var pct = total > 0m ? v.TotalGrossSpend / total * 100m : 0m;
                html.Append($"          <div class=\"bar-segment {VendorBarClass(v.Vendor.Id)}\" style=\"flex: {pct.ToString("0.0", Culture)};\"></div>\n");
            }
            html.Append("        </div>\n        <div class=\"legend\">\n");
            foreach (var v in withShare)
            {
                var pct = total > 0m ? v.TotalGrossSpend / total * 100m : 0m;
                html.Append($"          <span class=\"legend-item\"><span class=\"legend-swatch\" style=\"background: var(--{VendorColorVar(v.Vendor.Id)});\"></span>{Encode(v.Vendor.DisplayName)} — {pct:0.0}%</span>\n");
            }
            html.Append("        </div>\n      </div>\n\n");
        }

        html.Append("    </div>\n  </section>\n\n");
    }

    private static void RenderRateCard(StringBuilder html, ReportData data)
    {
        html.Append("""
              <section>
                <div class="eyebrow">Contract terms</div>
                <h2>Rate card</h2>
                <p class="audit-intro">What Meterist has configured today for each tenant/vendor pairing — currently-active rates only. A vendor with no configured rate here is either pure usage-based pricing with no seat concept, or its subscription fee arrives already priced in the vendor's own billing export.</p>
            """);
        html.Append("    <div class=\"table-scroll\">\n      <table>\n        <thead>\n          <tr>\n            <th>Vendor (tenant)</th>\n            <th class=\"note-col\">Configured rates</th>\n          </tr>\n        </thead>\n        <tbody>\n");

        foreach (var tenant in data.Tenants)
        {
            foreach (var vendor in VendorCatalog.All)
            {
                var vendorData = tenant.Vendors.Single(v => v.Vendor.Id == vendor.Id);
                html.Append($"          <tr>\n            <th scope=\"row\">{Encode(vendor.DisplayName)} ({Encode(tenant.TenantId)})</th>\n");
                if (vendorData.CurrentRates.Count == 0)
                {
                    html.Append("            <td class=\"note\"><span class=\"muted-dash\">—</span></td>\n");
                }
                else
                {
                    html.Append("            <td class=\"note\">\n");
                    foreach (var rate in vendorData.CurrentRates)
                    {
                        html.Append($"              {FormatRateLine(rate)}<br/>\n");
                    }
                    html.Append("            </td>\n");
                }
                html.Append("          </tr>\n");
            }
        }

        html.Append("        </tbody>\n      </table>\n    </div>\n  </section>\n\n");
    }

    private static void RenderWeeklySpend(StringBuilder html, ReportData data, int totalWeekCount)
    {
        html.Append("""
              <section>
                <div class="eyebrow">Trend &amp; cadence</div>
                <h2>Weekly spend by vendor</h2>
                <p class="audit-intro">Weeks run Wednesday–Tuesday. A week cut short by where the report starts or ends is marked * and, like a ChatGPT Enterprise week with missing days, is left out of the projections below ("A ChatGPT Enterprise week with missing days is excluded from the trend projections below"); the footnote says which weeks, if any.</p>

            """);

        foreach (var tenant in data.Tenants)
        {
            // Column widths always sum to exactly 100% regardless of totalWeekCount --
            // table-layout:fixed columns summing over 100% render inconsistently across
            // browsers (some silently rescale every column down, which can visually
            // collapse narrow, non-wrapping dollar-figure cells into their neighbors).
            const double vendorColumnWidth = 12.0;
            const double totalColumnWidth = 16.0;
            var weekColumnWidth = (100.0 - vendorColumnWidth - totalColumnWidth) / totalWeekCount;

            // Percentages alone aren't enough: table-layout:fixed respects them
            // literally, so with many week columns each one's real pixel width can
            // shrink below what a dollar figure needs (white-space:nowrap on
            // .figure means it won't wrap, so it visually spills into the next
            // cell instead). Scale min-width with the column count so every
            // column gets enough real space; .table-scroll's overflow-x:auto
            // already handles the resulting horizontal scroll.
            const int vendorColumnMinPx = 110;
            const int weekColumnMinPx = 92;
            const int totalColumnMinPx = 130;
            var tableMinWidthPx = vendorColumnMinPx + (weekColumnMinPx * totalWeekCount) + totalColumnMinPx;

            html.Append($"    <h3 class=\"subsection-tenant\">{Encode(tenant.TenantId)}</h3>\n    <div class=\"table-scroll\">\n      <table class=\"weekly-table\" style=\"min-width:{tableMinWidthPx}px;\">\n        <thead>\n          <tr>\n            <th style=\"width:{vendorColumnWidth.ToString("0.0", Culture)}%;\">Vendor</th>\n");

            for (var i = 0; i < totalWeekCount; i++)
            {
                var (start, end) = WeekBounds(data.WeekAnchor, i);
                var partial = end > data.Period.End || start < data.Period.Start ? "*" : "";
                html.Append($"            <th class=\"figure\" style=\"width:{weekColumnWidth.ToString("0.0", Culture)}%;\">{FormatWeekLabel(start, end)}{partial}</th>\n");
            }
            html.Append($"            <th class=\"figure\" style=\"width:{totalColumnWidth.ToString("0.0", Culture)}%;\">Total</th>\n          </tr>\n        </thead>\n        <tbody>\n");

            var totalsByWeek = new decimal[totalWeekCount];
            foreach (var vendor in VendorCatalog.All)
            {
                var vendorData = tenant.Vendors.Single(v => v.Vendor.Id == vendor.Id);
                var byIndex = vendorData.Weeks.ToDictionary(w => w.WeekIndex);

                html.Append($"          <tr>\n            <th scope=\"row\">{Encode(vendor.DisplayName)}</th>\n");
                for (var i = 0; i < totalWeekCount; i++)
                {
                    if (byIndex.TryGetValue(i, out var week) && week.DaysPresent > 0)
                    {
                        totalsByWeek[i] += week.GrossSpend;
                        html.Append($"            <td class=\"figure tabular\">{Money(week.GrossSpend)}\n              <span class=\"breakdown\">seat {Money(week.SeatFee)} + usage {Money(week.UsageOrOverage)}</span>\n            </td>\n");
                    }
                    else
                    {
                        html.Append("            <td class=\"figure tabular\"><span class=\"muted-dash\">—</span></td>\n");
                    }
                }
                html.Append($"            <td class=\"figure tabular\">{Money(vendorData.TotalGrossSpend)}\n              <span class=\"breakdown\">seat {Money(vendorData.TotalSeatFee)} + usage {Money(vendorData.TotalUsageOrOverage)}</span>\n            </td>\n          </tr>\n");
            }

            html.Append("          <tr class=\"total-row\">\n            <th scope=\"row\">Total weekly spend</th>\n");
            decimal cumulative = 0m;
            var cumulativeRow = new StringBuilder("          <tr class=\"cumulative-row\">\n            <th scope=\"row\">Cumulative spend</th>\n");
            for (var i = 0; i < totalWeekCount; i++)
            {
                html.Append($"            <td class=\"figure tabular\">{Money(totalsByWeek[i])}</td>\n");
                cumulative += totalsByWeek[i];
                cumulativeRow.Append($"            <td class=\"figure tabular\">{Money(cumulative)}</td>\n");
            }
            html.Append($"            <td class=\"figure tabular\">{Money(cumulative)}</td>\n          </tr>\n");
            cumulativeRow.Append("            <td class=\"figure tabular\">&nbsp;</td>\n          </tr>\n");
            html.Append(cumulativeRow);

            html.Append("        </tbody>\n      </table>\n    </div>\n");
            html.Append("    <p class=\"small-note\">*Partial week — only part of it is inside the report period (it starts before the first day or ends after the last). Shown and counted in the totals, but left out of the projections.</p>\n\n");
        }

        html.Append("  </section>\n\n");
    }

    private static void RenderProjections(StringBuilder html, ReportData data, int totalWeekCount)
    {
        html.Append($"""
              <section>
                <div class="eyebrow">Looking ahead</div>
                <h2>Annualized cost projection</h2>
                <p class="audit-intro"><b>Flat</b> averages the complete weeks above and multiplies by 52; <b>Trend-adjusted</b> fits a linear trend across those same weeks and projects the next week's rate forward × 52; <b>Latest week annualized</b> takes only the most recent complete week × 52. A partial week, one cut short by where the report starts or ends, is excluded from all three for every vendor, as is any ChatGPT Enterprise week with missing days (see the footnote).</p>

            """);

        foreach (var tenant in data.Tenants)
        {
            html.Append($"""
                <h3 class="subsection-tenant">{Encode(tenant.TenantId)}</h3>
                <div class="table-scroll">
                  <table class="weekly-table">
                    <thead>
                      <tr>
                        <th style="width:20%;">Vendor</th>
                        <th class="figure" style="width:20%;">Flat (avg×52)</th>
                        <th class="figure" style="width:24%;">Trend-adjusted (linear×52)</th>
                        <th class="figure" style="width:18%;">Latest week ×52</th>
                        <th class="figure" style="width:18%;">{totalWeekCount}-wk actual</th>
                      </tr>
                    </thead>
                    <tbody>

            """);

            ProjectionCalculator.Projection totalProjection = default;
            decimal totalActual = 0m;
            var anyProjection = false;

            foreach (var vendor in VendorCatalog.All)
            {
                var vendorData = tenant.Vendors.Single(v => v.Vendor.Id == vendor.Id);
                var completeWeeks = vendorData.Weeks
                    .Where(w => ProjectionEligibility.FeedsProjection(w, data.Period))
                    .OrderBy(w => w.WeekIndex)
                    .ToList();

                var seatProjection = ProjectionCalculator.Compute(completeWeeks.Select(w => w.SeatFee).ToList());
                var usageProjection = ProjectionCalculator.Compute(completeWeeks.Select(w => w.UsageOrOverage).ToList());
                var flat = seatProjection.Flat + usageProjection.Flat;
                var trend = seatProjection.TrendAdjusted + usageProjection.TrendAdjusted;
                var latest = seatProjection.LatestWeek + usageProjection.LatestWeek;
                var actual = vendorData.TotalGrossSpend;

                totalProjection = totalProjection with
                {
                    Flat = totalProjection.Flat + flat,
                    TrendAdjusted = totalProjection.TrendAdjusted + trend,
                    LatestWeek = totalProjection.LatestWeek + latest,
                };
                totalActual += actual;
                anyProjection |= completeWeeks.Count > 0;

                if (completeWeeks.Count == 0)
                {
                    html.Append($"""
                              <tr>
                                <th scope="row">{Encode(vendor.DisplayName)}</th>
                                <td class="figure tabular"><span class="muted-dash">—</span></td>
                                <td class="figure tabular"><span class="muted-dash">—</span></td>
                                <td class="figure tabular"><span class="muted-dash">—</span></td>
                                <td class="figure tabular">{Money(actual)}</td>
                              </tr>

                        """);
                    continue;
                }

                html.Append($"""
                          <tr>
                            <th scope="row">{Encode(vendor.DisplayName)}</th>
                            <td class="figure tabular">{Money(flat)}
                              <span class="breakdown">seat {Money(seatProjection.Flat)} + usage {Money(usageProjection.Flat)}</span>
                            </td>
                            <td class="figure tabular">{Money(trend)}
                              <span class="breakdown">seat {Money(seatProjection.TrendAdjusted)} + usage {Money(usageProjection.TrendAdjusted)}</span>
                            </td>
                            <td class="figure tabular">{Money(latest)}
                              <span class="breakdown">seat {Money(seatProjection.LatestWeek)} + usage {Money(usageProjection.LatestWeek)}</span>
                            </td>
                            <td class="figure tabular">{Money(actual)}</td>
                          </tr>

                    """);
            }

            html.Append($"""
                          <tr class="total-row">
                            <th scope="row">Total</th>
                            <td class="figure tabular">{(anyProjection ? Money(totalProjection.Flat) : NoProjection)}</td>
                            <td class="figure tabular">{(anyProjection ? Money(totalProjection.TrendAdjusted) : NoProjection)}</td>
                            <td class="figure tabular">{(anyProjection ? Money(totalProjection.LatestWeek) : NoProjection)}</td>
                            <td class="figure tabular">{Money(totalActual)}</td>
                          </tr>
                        </tbody>
                      </table>
                    </div>

                """);
        }

        html.Append("  </section>\n\n");
    }

    private static void RenderPendingSections(StringBuilder html)
    {
        html.Append("""
              <section>
                <div class="eyebrow">Editorial</div>
                <h2>Headline finding</h2>
                <p class="pending-note">Pending manual entry — picking one genuinely interesting cross-tenant fact is a judgment call, not a formula. Add it by hand before sharing this report.</p>
              </section>

              <section>
                <div class="eyebrow">Data integrity</div>
                <h2>Audit &amp; reconciliation status</h2>
                <p class="pending-note">Pending manual entry — verification basis (e.g. invoice-confirmed, console-checked) isn't tracked in the data model yet. Add it by hand before sharing this report.</p>
              </section>

            """);
    }

    // What the weekly figures are made of, and what they leave out. Shown only where it adds something:
    // a vendor that is nothing but a seat fee has no breakdown worth a table. The usage kinds are what the
    // figures above count; the non-usage ones (tax, support) are on the vendor's invoice but in no figure here,
    // so this is where a reader can see why a total sits below an invoice. The projections do not use any of it.
    private static void RenderSpendByKind(StringBuilder html, ReportData data)
    {
        var tenants = data.Tenants
            .Select(t => (t.TenantId, Vendors: t.Vendors.Where(v => v.HasNotableBreakdown).ToList()))
            .Where(t => t.Vendors.Count > 0)
            .ToList();

        if (tenants.Count == 0)
        {
            return;
        }

        html.Append("""
              <section>
                <div class="eyebrow">What the figures contain</div>
                <h2>Spend by kind of charge</h2>
                <p class="audit-intro">The figures above count the <b>usage and seat</b> kinds. <b>Not counted</b> kinds, such as tax and support fees, are real charges on the vendor's invoice but are in no figure or projection in this report, so a vendor's invoice can total more than its figure above.</p>

            """);

        foreach (var (tenantId, vendors) in tenants)
        {
            html.Append($"    <h3 class=\"subsection-tenant\">{Encode(tenantId)}</h3>\n    <div class=\"table-scroll\">\n      <table>\n        <thead>\n          <tr>\n            <th>Vendor</th>\n            <th>Kind</th>\n            <th class=\"figure\">Days with a charge</th>\n            <th class=\"figure\">Amount</th>\n          </tr>\n        </thead>\n        <tbody>\n");

            foreach (var vendor in vendors)
            {
                // A kind under half a cent would read as a bare "$0.00" row; it stays in the subtotals below.
                foreach (var kind in vendor.Breakdown.Where(b => !b.IsNegligible))
                {
                    var label = kind.IsUsage ? Encode(kind.Category) : $"{Encode(kind.Category)} <span class=\"muted-dash\">(not counted)</span>";
                    html.Append($"          <tr>\n            <th scope=\"row\">{Encode(vendor.Vendor.DisplayName)}</th>\n            <td>{label}</td>\n            <td class=\"figure tabular\">{kind.Days}</td>\n            <td class=\"figure tabular\">{Money(kind.Amount)}</td>\n          </tr>\n");
                }

                var counted = vendor.Breakdown.Where(b => b.IsUsage).Sum(b => b.Amount);
                var notCounted = vendor.Breakdown.Where(b => !b.IsUsage).Sum(b => b.Amount);
                html.Append($"          <tr>\n            <th scope=\"row\">{Encode(vendor.Vendor.DisplayName)}</th>\n            <td><b>Counted in the figures</b></td>\n            <td class=\"figure tabular\"></td>\n            <td class=\"figure tabular\"><b>{Money(counted)}</b></td>\n          </tr>\n");
                if (notCounted != 0m)
                {
                    html.Append($"          <tr>\n            <th scope=\"row\">{Encode(vendor.Vendor.DisplayName)}</th>\n            <td><b>Not counted</b></td>\n            <td class=\"figure tabular\"></td>\n            <td class=\"figure tabular\"><b>{Money(notCounted)}</b></td>\n          </tr>\n");
                }
            }

            html.Append("        </tbody>\n      </table>\n    </div>\n");
        }

        html.Append("  </section>\n\n");
    }

    private static void RenderFootnote(StringBuilder html, ReportData data)
    {
        html.Append("""
              <footer class="footnote">
                <ul>
                  <li><b>Figures are Gross Spend</b> (seat fee + usage/overage), pulled live from each vendor's API through Meterist — not estimated or modeled.</li>
                  <li><b>Tax and support fees are not included.</b> Figures are the usage and seat charges each vendor reports, so a figure can differ from the amount on that vendor's invoice. In particular, tax and cloud support fees (which Google bills on the same account as Gemini Enterprise) are left out of the figures on purpose; where they were recorded, the Spend by kind of charge section lists them.</li>
                  <li><b>People are named.</b> The usage-by-person, top-consumer and idle-seat sections show names and emails, so a populated report is personal data: it stays under the git-ignored <code>artifacts/</code> folder and is not shared outside the engagement. Per-user dollars are usage only (seat fees are not attributed to people), and any figure marked estimated is derived, not vendor-reported.</li>
                  <li><b>Date ranges differ by vendor within a tenant</b> — this reflects each vendor's actual available extraction window at pull time, not a data gap.</li>

            """);

        AppendPartialWeekFootnote(html, data);
        AppendChatGptFootnotes(html, data);

        html.Append("""
                  <li><b>Headline finding and audit status are not auto-generated</b> — see the placeholders above; both need a human's judgment or knowledge not captured in the data model.</li>
                </ul>
                <div class="colophon">Generated by `meterist report generate` from Meterist's live extraction store · Draft for internal review — not yet client-facing</div>
              </footer>

            """);
    }

    // Which weeks the report left out of the projections because it starts or ends mid-week, per tenant and for every
    // vendor alike, built from the data so it names the weeks and vendors that were actually affected.
    private static void AppendPartialWeekFootnote(StringBuilder html, ReportData data)
    {
        var period = data.Period;
        var leading = new List<string>();
        var trailing = new List<string>();

        foreach (var tenant in data.Tenants)
        {
            var leadingVendors = tenant.Vendors
                .Where(v => v.Weeks.Any(w => ProjectionEligibility.IsLeadingPartial(w, period)))
                .Select(v => Encode(v.Vendor.DisplayName)).ToList();
            var trailingVendors = tenant.Vendors
                .Where(v => v.Weeks.Any(w => ProjectionEligibility.IsTrailingPartial(w, period)))
                .Select(v => Encode(v.Vendor.DisplayName)).ToList();

            if (leadingVendors.Count > 0)
            {
                leading.Add($"{Encode(tenant.TenantId)}: {string.Join(", ", leadingVendors)}");
            }

            if (trailingVendors.Count > 0)
            {
                trailing.Add($"{Encode(tenant.TenantId)}: {string.Join(", ", trailingVendors)}");
            }
        }

        var firstWeek = FormatWeekLabel(data.WeekAnchor, data.WeekAnchor.AddDays(6));
        var lastStart = WeekBounds(data.WeekAnchor, TotalWeekCount(data) - 1);

        html.Append(
            "      <li><b>Partial weeks are left out of the projections, for every vendor.</b> Weeks run Wednesday to Tuesday, so a "
            + "report that starts or ends mid-week has a short first or last week. A week is used in the Flat, Trend-adjusted and "
            + "Latest-week projections only if it lies fully inside the report period; a short week would drag the average and "
            + "the trend down even when every requested day is stored. Partial weeks stay in the weekly table (marked *) and in "
            + "the actual totals.");

        if (leading.Count == 0)
        {
            html.Append(
                $" The report starts on {period.Start.DayOfWeek}, {FormatDate(period.Start)}, which opens a full week, so no first week was left out.");
        }
        else
        {
            html.Append(
                $" The report starts on {period.Start.DayOfWeek}, {FormatDate(period.Start)}, partway through the week of {firstWeek}, "
                + $"so that first week was left out. Left out at the start — {string.Join("; ", leading)}. Start a report on a "
                + "Wednesday to include its first week.");
        }

        if (trailing.Count > 0)
        {
            html.Append(
                $" The report ends on {period.End.DayOfWeek}, {FormatDate(period.End)}, before the week of "
                + $"{FormatWeekLabel(lastStart.Start, lastStart.End)} finished, so that last week was left out. Left out at the end — "
                + $"{string.Join("; ", trailing)}.");
        }

        html.Append("</li>\n");
    }

    // The two ChatGPT Enterprise notes are built from the data rather than fixed
    // text, so the report says what actually happened for these tenants: which
    // source supplied the days, and which weeks (if any) were left out of the
    // projections. Fixed text here once described a limitation that no longer applied.
    private static void AppendChatGptFootnotes(StringBuilder html, ReportData data)
    {
        var chatGptByTenant = data.Tenants
            .Select(t => (t.TenantId, Vendor: t.Vendors.Single(v => v.Vendor.Id == VendorCatalog.ChatGptEnterprise.Id)))
            .ToList();

        var withSources = chatGptByTenant.Where(x => x.Vendor.SourceDayCounts.Count > 0).ToList();
        if (withSources.Count > 0)
        {
            var perTenant = string.Join(
                "; ", withSources.Select(x => $"{Encode(x.TenantId)}: {DescribeSources(x.Vendor.SourceDayCounts)}"));

            html.Append(
                "      <li><b>ChatGPT Enterprise data sources</b> — each stored day records where its figures came from. "
                + "The Daily Usage API is the source of record; the COSTS compliance-log export is a cross-check. A day is "
                + "labelled <i>COSTS export</i> when its figure comes from that export: either the export reported more usage "
                + "than the API for the same day (a stored usage figure is never lowered), or the API did not cover the day "
                + $"(for example a tenant with no workspace ID stored). Days by source — {perTenant}.</li>\n");
        }

        var excluded = chatGptByTenant
            .Select(x => (x.TenantId, Weeks: x.Vendor.Weeks
                .Where(w => w.ExcludeFromProjection && w.WeekEnd <= data.Period.End)
                .OrderBy(w => w.WeekIndex)
                .ToList()))
            .Where(x => x.Weeks.Count > 0)
            .ToList();

        if (excluded.Count == 0)
        {
            html.Append(
                "      <li><b>No ChatGPT Enterprise week was excluded from the trend projections for missing data.</b> A week counts as "
                + "missing data only when a day inside the report period has no stored record; a week shortened by where the "
                + "report starts or ends is not treated as missing data (the partial-week note above covers those, which are "
                + "left out of the projections for every vendor).</li>\n");
            return;
        }

        var listed = string.Join(
            "; ",
            excluded.Select(x => $"{Encode(x.TenantId)}: " + string.Join(
                ", ", x.Weeks.Select(w => FormatWeekLabel(w.WeekStart, w.WeekEnd)))));

        html.Append(
            "      <li><b>ChatGPT Enterprise weeks with missing days are excluded from the trend projections</b> — a week in "
            + "which a day inside the report period has no stored record would understate a normal week. Often that is a "
            + "tenant with no workspace ID stored, where only the COSTS export's roughly 29 days were available, but the "
            + "report cannot tell why a day is absent, only that it is. "
            + $"Excluded: {listed}.</li>\n");
    }

    private static string DescribeSources(IReadOnlyDictionary<string, int> counts)
    {
        // The source of record first, then the cross-check, then anything else by name.
        var order = new[] { DailySpendSources.DailyUsageApi, DailySpendSources.CostsExport };

        var parts = counts
            .OrderBy(kvp => Array.IndexOf(order, kvp.Key) is var i && i >= 0 ? i : order.Length)
            .ThenBy(kvp => kvp.Key, StringComparer.Ordinal)
            .Select(kvp => kvp.Key switch
            {
                DailySpendSources.DailyUsageApi => $"{kvp.Value} from the Daily Usage API",
                DailySpendSources.CostsExport => $"{kvp.Value} from the COSTS export",
                _ => $"{kvp.Value} from {Encode(kvp.Key)}",
            });

        return string.Join(", ", parts);
    }

    private static int TotalWeekCount(ReportData data) =>
        ((data.Period.End.DayNumber - data.WeekAnchor.DayNumber) / 7) + 1;

    private static (DateOnly Start, DateOnly End) WeekBounds(DateOnly anchor, int weekIndex)
    {
        var start = anchor.AddDays(weekIndex * 7);
        return (start, start.AddDays(6));
    }

    private static string FormatWeekLabel(DateOnly start, DateOnly end)
    {
        var startStr = start.ToString("MMM d", Culture);
        var endStr = start.Month == end.Month ? end.Day.ToString(Culture) : end.ToString("MMM d", Culture);
        return $"{startStr}–{endStr}";
    }

    private static string FormatDate(DateOnly date) => date.ToString("MMM d, yyyy", Culture);

    // Shown where there is no projection, as for a vendor: a total of nothing is not $0.00 a year.
    private const string NoProjection = "<span class=\"muted-dash\">—</span>";

    private static string Money(decimal value) => value.ToString("C2", CultureInfo.GetCultureInfo("en-US"));

    private static string Encode(string value) => WebUtility.HtmlEncode(value);

    private static string VendorBarClass(Guid vendorId) => VendorSlug(vendorId) switch
    {
        "chatgpt" => "bar-seg-chatgpt",
        "gemini" => "bar-seg-gemini",
        "claude-api" => "bar-seg-claude",
        _ => "bar-seg-claude-ent",
    };

    private static string VendorColorVar(Guid vendorId) => VendorSlug(vendorId) switch
    {
        "chatgpt" => "vendor-chatgpt",
        "gemini" => "vendor-gemini",
        "claude-api" => "vendor-claude",
        _ => "vendor-claude-ent",
    };

    private static string VendorSlug(Guid vendorId)
    {
        if (vendorId == VendorCatalog.ChatGptEnterprise.Id) return "chatgpt";
        if (vendorId == VendorCatalog.GeminiEnterprise.Id) return "gemini";
        if (vendorId == VendorCatalog.ClaudeApiPlatform.Id) return "claude-api";
        return "claude-ent";
    }

    private static string FormatRateLine(VendorRateConfig rate)
    {
        var cadence = rate.BillingCadence switch
        {
            Models.BillingCadence.Monthly => "Monthly",
            Models.BillingCadence.Annual => "Annual",
            Models.BillingCadence.OneTime => "One-time",
            _ => null,
        };

        var parts = new List<string> { Encode(rate.RateType) };
        if (!string.IsNullOrEmpty(rate.ModelOrSku))
        {
            parts.Add(Encode(rate.ModelOrSku));
        }
        var rateFormat = rate.Rate == Math.Round(rate.Rate, 2) ? "C2" : "C4";
        parts.Add(rate.Rate.ToString(rateFormat, CultureInfo.GetCultureInfo("en-US")));
        if (cadence is not null)
        {
            parts.Add(cadence);
        }
        if (rate.SeatCount is int seats)
        {
            parts.Add($"{seats} seats");
        }
        parts.Add($"since {FormatDate(rate.EffectiveFrom)}");

        return string.Join(" · ", parts);
    }
}
