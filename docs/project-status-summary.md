# Meterist — Status Recap & Insight Roadmap

*A summary for the original Claude Chat planning thread — where the build stands, how it compares to the spreadsheet MVP it's replacing, and what reporting value we can pull from the data already collected.*

---

## 1. Where the project stands

Meterist is the .NET tool replacing the manual `AI_Vendor_Weekly_Spend_Model.xlsx` + CSV-export workflow for tracking AI vendor spend across client organizations.

**All four v1 vendors are code-complete. Three are live-verified with real data:**

| Vendor | Status | Notes |
|---|---|---|
| Claude API Platform | ✅ Live-verified | Real dollar cost directly from the API; no seat concept, pure usage pricing |
| ChatGPT Enterprise | ✅ Live-verified | Seat fee + usage/credits; per-user activity preserved from compliance log export |
| Gemini Enterprise | ✅ Live-verified | Per-user activity, aggregate SKU-level dollars via BigQuery billing export |
| Claude Enterprise | ⚠️ Code-complete, not yet live-verified | 61 passing tests, full adapter built; blocked only on obtaining a real Analytics API key from a tenant's *primary org owner* — not a code gap |

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

## 3. Eight insights we can report on right now

Assuming Claude Enterprise stays excluded from the numbers for now (three vendors, two tenants, real data):

| # | Insight | Why it matters | Priority / effort |
|---|---|---|---|
| 1 | **Executive TCO rollup** — total spend per tenant, vendor mix %, run-rate projection | The one-slide number an exec actually reads | High priority, low effort |
| 2 | **Cross-tenant benchmarking** — effective $/seat, usage intensity, compared across clients | The actual consulting differentiator — no off-the-shelf tool can do this, only Meterist's multi-tenant model can | High priority, medium effort |
| 3 | **Vendor concentration / diversification risk** — % of spend on a single vendor | A client at 80%+ on one vendor has no renewal leverage — worth flagging proactively | High priority, low effort |
| 4 | **Cost driver breakdown within a vendor** — by model, workspace, or SKU (Claude API Platform and Gemini both preserve this in raw data) | Chargeback-ready detail: "Workspace X is 60% of your Claude spend" | Medium-high priority, medium effort (needs raw data, not just normalized records) |
| 5 | **Seat efficiency signal** (ChatGPT specifically, since it has both seat fee and usage/overage) | Tells you if a client is over- or under-provisioned on seats | Medium priority, low effort |
| 6 | **Anomaly / spike detection** — days that jump well above trailing average | Catches a runaway script or misuse before it becomes a real problem | Medium priority, low-medium effort |
| 7 | **Day-of-week seasonality** | Mostly a trust-builder — validates the data looks like real human usage before making bolder claims with it | Low-medium priority, low effort |
| 8 | **Data-quality / coverage disclosure** — which vendors give real per-user dollars vs. workspace- or SKU-level aggregates, and any gaps in configured rates | Not glamorous, but protects credibility by not implying more precision than the data actually has | Medium priority, low effort |

**Recommended first deliverable:** combine #1 + #2 + #3 into one compact "Client Spend & Benchmark Report" — spend, mix, and comparative risk in one page. It's the smallest bundle that reads as a complete story and it's the one built entirely on the multi-tenant capability that's already proven working.

---

## 4. Open questions worth discussing back in Chat

- **Cadence:** should these insights become a recurring templated report (e.g., generated monthly per client) or a one-off proof-of-value pitch showing Meterist beats the spreadsheet?
- **Priority:** is chargeback detail (#4) or cross-client benchmarking (#2) more valuable to the consulting business model right now — they pull toward different next-build priorities.
- **Claude Enterprise timing:** worth chasing down a live Analytics API key now, or defer until the reporting layer proves value with the three vendors already live?
- **Interface direction:** this pushes the still-open CLI-vs-dashboard question. A report generator might be the natural next increment either way — worth deciding whether it's a new `report` CLI command, an exported file (CSV/PDF), or the first real reason to start the dashboard.

---

## 5. Suggested next steps

1. Pick one report from the insight list (or the recommended #1+#2+#3 bundle) to prototype first.
2. Decide delivery format — plain-text/Markdown summary from a CLI command, an exported file, or a small HTML dashboard.
3. Decide whether report generation lives inside Meterist itself or as a downstream layer reading from the same SQLite database.
