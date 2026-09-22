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
                <p class="audit-intro">Weeks run Wednesday–Tuesday. ChatGPT Enterprise's first week is always excluded from trend projections below (see the footnote) — OpenAI's compliance log retains only 29 days from whenever extraction runs, so usage data for that week is permanently gone from the vendor's side.</p>

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
                var partial = end > data.Period.End ? "*" : "";
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
            html.Append("    <p class=\"small-note\">*Partial week — not all 7 days have elapsed yet.</p>\n\n");
        }

        html.Append("  </section>\n\n");
    }

    private static void RenderProjections(StringBuilder html, ReportData data, int totalWeekCount)
    {
        html.Append($"""
              <section>
                <div class="eyebrow">Looking ahead</div>
                <h2>Annualized cost projection</h2>
                <p class="audit-intro"><b>Flat</b> averages the complete weeks above and multiplies by 52; <b>Trend-adjusted</b> fits a linear trend across those same weeks and projects the next week's rate forward × 52; <b>Latest week annualized</b> takes only the most recent complete week × 52. The trailing partial week is excluded from all three, as is ChatGPT Enterprise's first week (retention-clamp data loss).</p>

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

            foreach (var vendor in VendorCatalog.All)
            {
                var vendorData = tenant.Vendors.Single(v => v.Vendor.Id == vendor.Id);
                var completeWeeks = vendorData.Weeks
                    .Where(w => !w.ExcludeFromProjection && w.WeekEnd <= data.Period.End)
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
                            <td class="figure tabular">{Money(totalProjection.Flat)}</td>
                            <td class="figure tabular">{Money(totalProjection.TrendAdjusted)}</td>
                            <td class="figure tabular">{Money(totalProjection.LatestWeek)}</td>
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

    private static void RenderFootnote(StringBuilder html, ReportData data)
    {
        html.Append("""
              <footer class="footnote">
                <ul>
                  <li><b>Figures are Gross Spend</b> (seat fee + usage/overage), pulled live from each vendor's API through Meterist — not estimated or modeled.</li>
                  <li><b>Date ranges differ by vendor within a tenant</b> — this reflects each vendor's actual available extraction window at pull time, not a data gap.</li>
                  <li><b>ChatGPT Enterprise's first week is excluded from trend projections</b> — OpenAI's compliance log retains COSTS data for only 29 days from whenever extraction runs, not from the requested start date, so usage data for that week is permanently gone from the vendor's side.</li>
                  <li><b>Headline finding and audit status are not auto-generated</b> — see the placeholders above; both need a human's judgment or knowledge not captured in the data model.</li>
                </ul>
                <div class="colophon">Generated by `meterist report generate` from Meterist's live extraction store · Draft for internal review — not yet client-facing</div>
              </footer>

            """);
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
