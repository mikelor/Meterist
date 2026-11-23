# Meterist — Status Recap

*A summary of where the build stands and what reporting value the stored data supports.*

> **Updated 2026-11-22.** Section 1 describes what is built, section 2 shows where each of the ten reporting ideas from the
> July 2026 roadmap stands, and sections 3 and 4 list what is open and what to do next. The backlog and sprint plan live in
> `product-design-document.md` §7 and the GitHub issues. Each vendor's API details are in `vendor-integration-reference.md`.

---

## 1. Where the project stands

Meterist is a .NET 11 command-line tool that extracts AI vendor spend from each vendor's API into a local SQLite database,
audits it, and produces reports for several client organizations (tenants).

**All four vendors are live-verified with real data:**

| Vendor | Status | Notes |
|---|---|---|
| Claude Enterprise | ✅ Live-verified for both tenants | Seat fee from the rate configuration plus native per-user dollar usage from the Analytics API (`/analytics/cost`, with a setting to fall back to the older endpoint). For one tenant, a month's extracted usage matched the invoiced "extra usage units" line exactly, and the seat-fee proration matched the annual contract math. Also: the member roster, vendor-reported active users and seat counts, per-person activity, and spend-limit snapshots |
| Claude API Platform | ✅ Live-verified | Real dollar cost from the analytics API, attributed per person where personal or service-account keys are used (legacy workspace keys attribute only to the key). Pure usage pricing, so no seat fee, and a day with no usage has no record |
| ChatGPT Enterprise | ✅ Live-verified | Seat fee plus usage. The Daily Usage API (per user, per day) is the source of record, and a stored usage figure is never lowered by a later pull. The `COSTS` compliance-log export, which keeps about 29 days, is a cross-check over the most recent days. Also: the member roster and spend controls |
| Gemini Enterprise | ✅ Live-verified for spend | Aggregate SKU-level dollars from the BigQuery billing export, with a per-kind breakdown (tax and support recorded but never counted). The licence roster is built from Google's reference but not yet run live. Per-person activity needs a log sink that is not set up (#36) |

**Multi-tenancy is proven, not just designed.** Two real tenants each have independently configured, DPAPI-encrypted
credentials, rates and data in one shared database, confirmed through live extraction runs and not only unit tests.

**What is in place:**
- **Extraction** for every vendor, by date range, with `--dry-run` to see what would change before anything is written, a
  read-back after every real write that fails the vendor if a day did not land, and a non-zero exit code when a vendor fails.
- **Versioned rates.** Seat counts, per-seat rates and credit-to-dollar rates are dated rows; setting a new one closes the
  previous open-ended row, a backdated row is capped at the next existing one, and overlapping windows are rejected.
- **Scheduled runs.** A daily extraction for every tenant through yesterday, then the coverage check, with roster and
  spend-limit snapshots; the exit code is a bitmask that says which part failed.
- **Audits.** `audit coverage` (missing days), `audit seats` (billed against vendor-reported seats, and idle seats), and a
  per-person usage reconciliation against each vendor's aggregate.
- **Per-person views.** Usage by person, top consumers, rosters, active-user counts and spend-limit headroom.
- **Reports.** `report generate` writes the Client Spend & Benchmark Report as HTML: weekly spend, concentration, spend by
  kind of charge, per-person and seat-utilization sections, annualized projections (the seat part from the contract in force
  on the last day, quiet weeks of a usage-priced vendor counted as zero), and a cross-tenant ledger. Each run writes a
  manifest so the next report shows what changed since the last one.
- **Raw data kept apart.** Raw vendor records are stored separately from the normalized daily records, so a normalization bug
  never loses the underlying pull, and re-extraction is an idempotent upsert by tenant, vendor and date.
- **Tests that need no live vendor:** WireMock for the HTTP vendors and an in-memory fake for BigQuery.

---

## 2. The ten insights: where each one stands

These are the ten reporting ideas from the original July roadmap, with what exists as of 2026-11-22. Most of them are
sections of the Client Spend & Benchmark Report (`report generate`; each section is described in
`client-report-generation.md`). The backlog and sprint plan are in `product-design-document.md` §7 and the GitHub issues.

| # | Insight | Status | What exists, and what is left |
|---|---|---|---|
| 1 | **Executive TCO rollup**: total spend per tenant, vendor mix, run-rate projection | ✅ Built | The report's headline cards (total tracked spend per tenant, vendor count, average $/day), the vendor ledger and the annualized projection |
| 2 | **Cross-tenant benchmarking**: effective $/seat, usage intensity, compared across clients | 🟡 Partly built | The vendor ledger puts each vendor's spend side by side per tenant, split into seat and usage, and the rate card shows each tenant's seat count and rate. Not built: a computed effective $/seat or usage-intensity ratio as its own figure |
| 3 | **Vendor concentration**: share of spend on one vendor | ✅ Built | The report's concentration section flags any vendor above 70% of a tenant's tracked spend, and says "no vendor above 70%" otherwise |
| 4 | **Cost drivers within a vendor**: by model, workspace or SKU | 🟡 Partly built | "Spend by kind of charge" lists the usage kinds and the not-counted charges (tax, support) where they matter; per-person usage and top consumers show who drives usage, for Claude Enterprise, ChatGPT Enterprise and Claude API Platform (attributed per person or key where the vendor allows). Not built: a split by model or workspace |
| 5 | **Seat efficiency**: over- or under-provisioned seats | ✅ Built | Billed against vendor-reported seats, active users and idle seats in the report, and `audit seats`; it works for every vendor with a roster source: Claude Enterprise and ChatGPT Enterprise, and Gemini Enterprise once its roster is run live (built, not yet run live), not only ChatGPT |
| 6 | **Anomaly and spike detection**: days far above the trailing average | 🔵 Not built, no issue | A stored "unreliable period" flag was decided against (#84), and nothing flags a spike. Revisit if a runaway-usage case shows up |
| 7 | **Day-of-week seasonality** | 🔵 Not built, no issue | Low value until a client asks; the daily data supports it |
| 8 | **Data-quality and coverage disclosure**: what precision each vendor gives, and gaps | ✅ Built | `audit coverage` and the scheduled coverage check; the report's footnotes say which weeks were left out and why, which ChatGPT days came from which source, that tax and support are excluded, and that per-user figures are marked estimated where derived; a vendor with no rate shows a dash with the reason |
| 9 | **Audit and reconciliation**: extracted totals against the vendor's own figures | 🟡 Partly built | Built: the coverage check, per-person usage reconciled to each vendor's aggregate, seat drift, the ChatGPT cross-check of the Daily Usage API against the `COSTS` export, the post-write read-back, and the report-to-report diff that flags a figure that fell. Not built: a command that re-pulls the vendor's own period total and compares it with the stored sum, or an invoice match (deferred, see `vnext-contract-model.md`). Invoices are still reconciled by hand |
| 10 | **Cost projection**: flat, trend-adjusted and latest-week-annualized | ✅ Built | The report's projection section. Partial weeks are left out; the seat part comes from the contract in force on the report's last day; a usage-priced vendor's quiet weeks count as zero. Known limit: the linear Trend figure overstates a series that peaks and then stops (#106) |

The first deliverable proposed in July was a single report combining #1, #2 and #3 (spend, mix and comparative risk).
That report exists and has grown to cover most of the rest. The two largest gaps are a vendor-total reconciliation
command (#9) and the split by model or workspace (#4).

---

## 3. Open questions

- **Report cadence.** Reports are generated on demand. Should each client get a recurring one (for example monthly), and if so
  from the scheduled run or by hand?
- **Dashboard.** The CLI plus the HTML report cover today's use. Is there a reason yet to start the dashboard layer, or does
  the report stay the interface? (`product-design-document.md` §7 has the dashboard as a deferred item.)
- **Next build priority.** A split of spend by model or workspace (#4) for chargeback, or a computed effective $/seat and
  usage-intensity figure for cross-client benchmarking (#2)? They pull in different directions.
- **A vendor-total check (#9).** Re-pulling the vendor's own period total and comparing it with the stored sum needs a
  per-vendor tolerance first: totals can differ by pennies through rounding and proration, and one global rule would raise
  false alarms. Is it worth building, or do the existing audits and the report diff cover it?
- **The Trend figure.** The linear Trend overstates a series that peaks and then stops (#106). Flag it, fit only recent
  weeks, or leave it?
- **Contract term and renewal.** Renewals are entered by hand with `rates set`, and nothing reminds anyone that a term is
  ending. A stored term and a reminder are scoped in `vnext-contract-model.md` (#89); build them only when wanted.
- **Gemini per-person activity.** It needs a log sink in the client's Google Cloud project (#36), which needs the client's
  agreement. Until then Gemini has spend and, once its roster runs live, seats, but no per-person view.

---

## 4. Suggested next steps

1. **When a contract term ends, enter the renewal.** Run `rates set --effective-from <renewal date>` before the next report,
   or the old rate keeps accruing past the term and the seat projection keeps using it.
2. **Run the Gemini licence roster live** for a tenant, check it against the console, and record the result.
3. **Work through the assumptions checklist (#72)** as live data allows; four items still need it.
4. **Decide the Trend handling (#106)** before quoting Trend figures for a vendor whose usage has stopped.
5. **Decide whether to build the vendor-total reconciliation (#9)** once a tolerance per vendor is agreed.
6. **Split spend by model or workspace (#4)** if a client asks for chargeback.
7. **Revisit the Gemini activity spike (#36)** when a client agrees to the log sink; per-person Gemini cost (#39) depends on it.
