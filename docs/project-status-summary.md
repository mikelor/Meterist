# Meterist — Status Recap & Insight Roadmap

*A summary for the original Claude Chat planning thread — where the build stands, how it compares to the spreadsheet MVP it's replacing, and what reporting value we can pull from the data already collected.*

---

## 1. Where the project stands

Meterist is the .NET tool replacing the manual `AI_Vendor_Weekly_Spend_Model.xlsx` + CSV-export workflow for tracking AI vendor spend across client organizations.

**All four v1 vendors are now code-complete and live-verified with real data:**

| Vendor | Status | Notes |
|---|---|---|
| Claude API Platform | ✅ Live-verified | Real dollar cost directly from the API; no seat concept, pure usage pricing |
| ChatGPT Enterprise | ✅ Live-verified | Seat fee + usage/credits; per-user activity preserved from compliance log export |
| Gemini Enterprise | ✅ Live-verified | Per-user activity, aggregate SKU-level dollars via BigQuery billing export |
| Claude Enterprise | ✅ Live-verified for zelleri | Reconciled against a real Anthropic invoice, not just a console spot-check: June's extracted usage ($70.61) matched the invoiced "extra usage units" line exactly, and the seat-fee proration ($1,972.60 for June) checks out against the annual $2,400/seat contract. Still needs a credential for ecosync — no Claude Enterprise data exists for that tenant yet |

**Multi-tenancy is proven, not just designed.** Two real tenants (zelleri, ecosync) each have independently configured, DPAPI-encrypted credentials, and both have successfully extracted and persisted isolated data into one shared SQLite database — confirmed via live extraction runs, not just unit tests.

**Other load-bearing pieces now in place:**
- Versioned, per-tenant **rate configuration** (`VendorRateConfig`) with an auto-close-on-renewal policy — setting a new rate automatically closes out the prior open-ended one, so historical spend always resolves against the rate that was actually in effect on that date.
- Credential storage is resilient: a corrupted/stale credential file logs a warning and is treated as empty rather than crashing the whole extraction run.
- Raw vendor data is persisted separately from normalized `DailySpendRecord`s, so a normalization bug never loses the underlying pull, and re-extraction is idempotent (upsert by natural key — tenant/vendor/date).

---

## 2. How this compares to the spreadsheet MVP

| | Spreadsheet MVP | Meterist today |
|---|---|---|
| **Data collection** | Manual CSV export per vendor, per week, by hand | Automated API pulls per vendor per tenant, on demand |
| **Scope** | One workbook per client | One shared database, N tenants, isolated credentials & rates per tenant |
| **Rate/pricing changes** | Manually edit formulas on renewal; history is whatever the last edit happened to leave behind | Versioned rate rows, auto-closed on renewal, full historical rate periods queryable |
| **Error handling** | Whoever notices a broken formula or missing tab | Structured per-vendor Succeeded / NotImplemented / Failed classification, resilient to corrupted credentials |
| **Granularity** | Weekly, vendor-provided totals only | Daily grain, with underlying line-item detail preserved (model, token type, workspace, SKU where the vendor exposes it) |
| **Cross-client comparison** | Not possible — every client is a separate file | Native — one query away, and the whole basis for the benchmarking insight below |
| **Auditability** | Spreadsheet version history, if anyone kept it | Raw extraction records kept distinct from normalized records; nothing is overwritten destructively |

The short version: the spreadsheet answered "what did we spend last week" for one client at a time. Meterist answers that plus "how does this client compare to our other clients" and "what's actually driving this cost," which the spreadsheet architecture could never have supported.

---

## 3. Nine insights we can report on right now

Claude Enterprise is now live for zelleri but still absent for ecosync (no credential yet), so cross-tenant insights below (#2 especially) currently draw on three vendors for ecosync and four for zelleri. Priority is now split from effort into its own column (P0 = do next, P1 = near-term, P2 = medium-term, P3 = backlog), so the two can be weighed independently — a few items are low effort but still not P0 because nothing depends on them yet.

| # | Insight | Why it matters | Priority | Effort |
|---|---|---|---|---|
| 1 | **Executive TCO rollup** — total spend per tenant, vendor mix %, run-rate projection | The one-slide number an exec actually reads | P0 | Low |
| 2 | **Cross-tenant benchmarking** — effective $/seat, usage intensity, compared across clients | The actual consulting differentiator — no off-the-shelf tool can do this, only Meterist's multi-tenant model can | P0 | Medium |
| 3 | **Vendor concentration / diversification risk** — % of spend on a single vendor | A client at 80%+ on one vendor has no renewal leverage — worth flagging proactively | P0 | Low |
| 4 | **Cost driver breakdown within a vendor** — by model, workspace, or SKU (Claude API Platform and Gemini both preserve this in raw data) | Chargeback-ready detail: "Workspace X is 60% of your Claude spend" | P2 | Medium (needs raw data, not just normalized records) |
| 5 | **Seat efficiency signal** (ChatGPT specifically, since it has both seat fee and usage/overage) | Tells you if a client is over- or under-provisioned on seats | P2 | Low |
| 6 | **Anomaly / spike detection** — days that jump well above trailing average | Catches a runaway script or misuse before it becomes a real problem | P2 | Low-Medium |
| 7 | **Day-of-week seasonality** | Mostly a trust-builder — validates the data looks like real human usage before making bolder claims with it | P3 | Low |
| 8 | **Data-quality / coverage disclosure** — which vendors give real per-user dollars vs. workspace- or SKU-level aggregates, and any gaps in configured rates | Not glamorous, but protects credibility by not implying more precision than the data actually has | P1 | Low |
| 9 | **Audit / reconciliation** — for a given tenant/vendor/date range, show the extracted `DailySpendRecord` total side by side with the value pulled straight from the vendor's own console/billing UI, flagging any delta past a small tolerance | The single highest-leverage item on this list right now: it's the automated version of the manual smoke-test comparison already planned before trusting any vendor as "live-verified." It also doubles as an early warning for schema drift — three of the four vendor APIs are recent (beta or shipped within ~2 months per the README), so a silent field-mapping change would otherwise surface as a wrong number in a client report with no way to tell why | **P0** | Medium |

**Recommended first deliverable:** combine #1 + #2 + #3 into one compact "Client Spend & Benchmark Report" — spend, mix, and comparative risk in one page. It's the smallest bundle that reads as a complete story and it's the one built entirely on the multi-tenant capability that's already proven working.

**On #9 specifically:** this is worth sequencing *before* or alongside the reporting bundle above, not after it. Right now, "live-verified" rests on tests passing against mocked/WireMock fixtures plus a manual spot-check — #9 turns that spot-check into something repeatable and reportable, which matters more once real client numbers are on the line. Concretely, this only needs three pieces already sitting in the codebase: raw extraction records are already persisted separately from normalized ones (so there's something to reconcile *against*, not just re-derive), the vendor adapters already know how to call each API on demand, and `VendorExtractionResult`'s Succeeded/NotImplemented/Failed pattern is a natural fit to extend into a fourth state (`Mismatch`) once a reconciliation check exists. A reasonable v1 for this: a `meterist audit <tenant> <vendor> <date-range>` command that re-pulls the vendor's own reported total for the period (not the per-day breakdown, just a lightweight total-cost check) and diffs it against `SUM(DailySpendRecord)` for the same scope, surfacing a clear match/mismatch/tolerance-exceeded result per vendor.

**A manual precedent for #9 already happened:** zelleri's Claude Enterprise invoices (an annual seat contract + a June overage invoice) gave a real number to reconcile against by hand — June's extracted usage matched the invoiced overage exactly, and the seat-fee proration matched the annual contract math. That's exactly the shape #9 would automate; it's the first proof this kind of check is both possible and worth having on demand rather than only when an invoice happens to land in an inbox.

---

## 4. Open questions worth discussing back in Chat

- **Cadence:** should these insights become a recurring templated report (e.g., generated monthly per client) or a one-off proof-of-value pitch showing Meterist beats the spreadsheet?
- **Priority:** is chargeback detail (#4) or cross-client benchmarking (#2) more valuable to the consulting business model right now — they pull toward different next-build priorities.
- **Claude Enterprise for ecosync:** now live and invoice-validated for zelleri — worth chasing down an Analytics API key for ecosync too, so cross-tenant insights (#2 especially) cover all four vendors for both tenants instead of three.
- **Interface direction:** this pushes the still-open CLI-vs-dashboard question. A report generator might be the natural next increment either way — worth deciding whether it's a new `report` CLI command, an exported file (CSV/PDF), or the first real reason to start the dashboard.
- **Audit tolerance:** #9 needs a defined "close enough" threshold before it's useful — vendor-reported totals and `DailySpendRecord` sums may legitimately differ by pennies from rounding/proration, so an exact-match check would generate constant false positives. Worth deciding per-vendor tolerance (e.g., dollar or percentage-based) rather than a single global rule, given how differently each vendor's rate/proration logic works today.

---

## 5. Suggested next steps

1. Prototype #9 (audit/reconciliation) first, or alongside the #1+#2+#3 reporting bundle — it's what turns "live-verified" from a one-time manual spot-check into something repeatable, and de-risks trusting the other insights before they reach a client.
2. Pick one report from the insight list (or the recommended #1+#2+#3 bundle) to prototype next.
3. Decide delivery format — plain-text/Markdown summary from a CLI command, an exported file, or a small HTML dashboard.
4. Decide whether report generation lives inside Meterist itself or as a downstream layer reading from the same SQLite database.
