# Meterist — Product Design Document

**Status:** Draft — v1 scope confirmed with the user; architecture and
implementation not yet started.
**Last updated:** 2026-07-21

## 1. Overview

Meterist is a .NET 10 C# application that replaces a manual Excel workbook
(`AI_Vendor_Weekly_Spend_Model.xlsx`) currently used to track weekly AI
vendor spend by hand. It pulls spend data programmatically from each
vendor's API instead of requiring a weekly manual login + CSV export per
vendor, and is built multi-tenant from day one to support a consulting
practice spanning multiple client organizations, not just the operator's own
org (Ecosync Universal).

This is a **full replacement**, not a companion tool — the spreadsheet
workflow is expected to go away once this is live.

## 2. Problem Statement

Today, tracking AI vendor spend across four products (Claude Enterprise,
Claude API Platform, ChatGPT Enterprise, Gemini Enterprise) requires:

- A manual login + CSV export from each vendor's console, every week
- Hand-reconstructed overage math for ChatGPT Enterprise (cumulative credits
  vs. included pool size)
- A manually maintained rate table for Claude API Platform (token counts →
  dollars)
- Redoing this entire process **per client organization**, with no
  cross-client view

This doesn't scale past a handful of clients, is error-prone (manual rate
table maintenance, manual overage reconstruction), and provides no
programmatic audit trail or historical query capability.

## 3. Goals

- Eliminate manual CSV exports as the primary data-collection method for
  all four v1 vendors.
- Support multiple tenant organizations with isolated credentials and data
  from day one — not retrofitted later.
- Model vendor pricing/rate data as versioned, updatable configuration,
  since billing structures have already changed mid-project for two of the
  four vendors.
- Preserve the analytical value of the existing spreadsheet (weekly
  tracking, annual projection scenarios) without inheriting its structure.
- Start with a CLI, but build the core logic so a dashboard can be layered
  on top later without a rewrite.

## 4. Non-Goals (for now)

- This is not a general-purpose FinOps/cloud-cost platform — scope is
  limited to the four confirmed AI vendor products (plus documented future
  candidates, see §7).
- Not building a client-facing self-service portal in v1 — assume the
  consultant/operator is the only user of the tool itself; clients receive
  reports/output, they don't log in (revisit if this assumption breaks —
  see Open Questions).
- Not replicating every spreadsheet tab pixel-for-pixel — replicating the
  *analytical value* (weekly trend, annual projection, vendor breakdown),
  not the exact worksheet layout.

## 5. Users / Personas

- **Primary user: the consulting operator.** An IT/systems administrator
  who manages AI vendor platforms for their own org (Ecosync Universal) and
  administers the same for multiple consulting clients. Needs: fast weekly
  spend visibility per client, low-maintenance rate/config upkeep, and a
  consulting deliverable (cross-client cost comparison/benchmarking).
- **Secondary (future): client stakeholders.** Would receive a
  report/dashboard view of their own org's spend only — no cross-tenant
  visibility, no self-service credential management. Not a v1 requirement;
  flagged as a design constraint to keep in mind (tenant data isolation
  must hold even if this is added later).

## 6. v1 Scope — Confirmed Features

1. **Multi-tenant core.** `TenantId`-scoped data and credentials; a tenant
   configures which of the four vendors it uses (not all clients use all
   four), with per-tenant seat counts/rates/billing cycles.
2. **Vendor integrations (org-level weekly spend), all four confirmed
   feasible via real APIs as of 2026-07-21** — see
   [`vendor-integration-reference.md`](vendor-integration-reference.md) for
   full technical detail:
   - Claude Enterprise (Analytics API)
   - Claude API Platform (Admin API `cost_report`)
   - ChatGPT Enterprise (unified Cost API)
   - Gemini Enterprise (Cloud Billing → BigQuery export)
3. **Versioned pricing/rate configuration.** Per-vendor, per-tenant
   overridable rate cards with effective date ranges — not hardcoded
   constants. Required because rates have already changed mid-project
   (Sonnet 5 pricing step-up Sep 1, 2026; Claude Enterprise seat→usage
   transition; new Gemini SKUs).
4. **Daily spend aggregation into a normalized data model** — one shape
   across all four vendors (`DailySpendRecord`, see
   [`architecture.md`](architecture.md) §5), regardless of each vendor's
   very different native API/export shape. Daily is the stored grain;
   weekly/monthly/annual views are query-time aggregations over it.
5. **CLI interface** to trigger extraction and view aggregated
   reports/exports.
6. **Historical data continuity.** Ecosync's spreadsheet-tracked weeks
   (beginning Jun 24, 2026) should be importable/reconcilable so trend data
   isn't lost in the cutover.
7. **Annual projection scenarios**, carrying over the spreadsheet's three
   budgeting approaches (flat, trend-adjusted, latest-week-annualized) as
   logic against the new normalized data model.

## 7. Future Features (v1.x / v2+ Backlog)

Ordered roughly by expected value vs. effort — useful as a starting point
for sprint sequencing, not a committed roadmap.

**Reprioritized 2026-08-05** after a head-to-head reconciliation between
Meterist's live data and the `AI_Vendor_Weekly_Spend_Model.xlsx` spreadsheet
it replaces. That exercise validated the core premise — it caught two real
bugs in the spreadsheet (an off-by-one-row `SUM` formula in its Summary tab,
and a `TREND(...,26.5)` formula that silently extrapolates a full year of
unmoderated growth instead of projecting one week ahead) — but also exposed
gaps in Meterist itself: every number in the reconciliation report was
computed by hand from ad-hoc SQL scripts and manually edited into HTML, which
is the same category of risk (manual arithmetic, human transcription) the
project exists to remove from the vendor side. The five new/updated items
below lead the table as a result; everything already here follows unchanged.
(A sixth item from this reprioritization, CLI support for backdating rates,
shipped 2026-08-06 — see `CLAUDE.md`'s Suggested First Steps.)

**Fixed, 2026-08-21**: the extractor data-destruction bug confirmed the
same day (ChatGPT Enterprise silently overwriting already-correct
historical usage with `$0` once a day aged past OpenAI's 29-day retention
window, on every re-run) is resolved — see
`docs/vendor-integration-reference.md`'s ChatGPT Enterprise section and
`CLAUDE.md`'s Suggested First Steps for the fix detail. It was never a
deferred-by-choice backlog item, so it doesn't get a row in the table below
now that it's done.

**New, urgent, unresolved (found 2026-08-22)**: while verifying the fix
above, a second and potentially more serious issue surfaced — see the
table's top row. Until this is root-caused, **don't treat `extract`'s
"Succeeded, wrote N record(s)" status as proof that data was actually
persisted** for a day being extracted for the first time.

| Feature | Why deferred from v1 | Depends on |
|---|---|---|
| **🔴 Investigate: new-date inserts may silently fail to persist** (URGENT, unresolved, not deferred) | Found 2026-08-22 while live-verifying the retention-clamp fix above. Repeated `extract` runs for zelleri/ChatGPT Enterprise, each requesting a date range whose final day(s) had never been extracted before, consistently reported `Succeeded` with the correct record count (e.g. "wrote 32 DailySpendRecord(s)") — but a fresh `sqlite3` query immediately afterward showed those newest day(s) simply absent from both `DailySpendRecords` **and** `RawDailyExtractionRecords`, with the table's row count and `MAX(Date)` unchanged from before the run. Reproduced 3 times across different `--to` dates (Aug 21, Aug 21 again a day later, Aug 22) — each time the *new* trailing day(s) failed to appear while every already-existing day updated normally. **Ruled out** as explanations: (1) the day being "incomplete"/still in progress — reproduced even after the day had fully elapsed; (2) something specific to whichever day is `period.End` — reproduced even when a later run made that same day no longer the range's end; (3) a stale process holding a file lock — inspected all 7 `dotnet` processes running on the machine at the time (`Get-CimInstance Win32_Process` with full command lines) and confirmed none were connected to Meterist's database (two were an unrelated NuGet MCP server, three were normal MSBuild node-reuse workers, one was a `dotnet run` for a completely different project). **Not yet tested**: whether this reproduces for ecosync or for any vendor other than ChatGPT Enterprise — all reproduction so far is zelleri + ChatGPT Enterprise specifically, so it's unconfirmed whether this is vendor/tenant-specific or a general `EfDailySpendRepository`/`EfRawExtractionRepository` persistence issue. One side effect from this investigation: an incidental bad write (a wrong-looking `SeatFee`, resolving to a rate shape matching a different tenant's contract) was observed on one affected day via diagnostic logging, then deleted once identified — worth relating to the primary symptom above during investigation, but not confirmed to share the same root cause. All temporary diagnostic code added during this investigation was reverted (`git diff` confirmed clean) before this write-up. **Re-tested 2026-08-29**: a normal `ExtractAllThroughToday.ps1` run (not a rapid debugging loop) persisted four consecutive brand-new days (Aug 26–29) correctly across all 8 tenant/vendor combinations — this specific failure did not recur. Not closing this on one clean run: three earlier reproductions plus this one non-reproduction don't rule out an intermittent trigger, possibly tied to the rapid-repeat conditions of the original debugging session rather than normal single-pass extraction. | A dedicated, focused investigation — ideally with a debugger attached rather than repeated live `extract` runs against real tenant data, given each attempt risks another accidental write |
| **🟡 Investigate: ChatGPT usage erosion within the still-valid retention window** (unresolved, related to the fix above) | Found 2026-08-29. The 2026-08-21 `EffectiveStart` fix (see item 11 in `CLAUDE.md` and the dated addendum in `docs/vendor-integration-reference.md`) stops the *complete* overwrite-with-`$0` case once a day is fully outside OpenAI's 29-day retention window — but a subtler pattern survives it: a week still nominally *inside* the window can lose usage across successive pulls, monotonically, even post-fix. zelleri's Jul 22–28 week read $337.43 → $325.35 → $319.97 usage across three pulls (2026-08-05, -20, -29), each strictly lower, the last two both taken after the fix landed. Consistent with the vendor trimming a day's rows before the full 29 days are up, not with the fix failing to work — but it means the fix alone isn't sufficient to guarantee a ChatGPT week's figure is final once captured. Current mitigation: the reconciliation report keeps using the earliest-captured figure per week, on the empirically observed rule that these numbers only ever shrink pull-over-pull. A possible fix: extend the extractor to never let a re-pull replace an existing day's `UsageOrOverage` with a *lower* value. | The `EffectiveStart` fix (done); needs its own root-cause investigation, likely via a raw-JSONL diff across two pulls of the same day rather than guesswork |
| **Anomalous-period flagging** (mark a tenant/vendor/date as unreliable for trend analysis — e.g. retention-clamped, credit-grant-affected, calendar-partial) | New 2026-08-05. The reconciliation session manually re-derived "exclude this week from trend analysis" twice by hand (once for OpenAI's 29-day compliance-log retention clamp, once for a suspected monthly credit-grant week) with no way for the database to remember that judgment. A small, low-risk EF Core addition — a reason code on `DailySpendRecord` or a small companion table keyed the same way — removes the need to re-derive this every reporting cycle. | Nothing — can start immediately |
| **ChatGPT Enterprise credit-grant manual config** | New 2026-08-05, extends the credit-pool/overage tracking row below. Add a manually-entered per-tenant value (approximate day-of-month + credit amount) using the same versioned-config pattern as `VendorRateConfig`, so refining the number later is a data update, not a code change. Current observational estimates, both **unconfirmed**: ecosync ~day 30 / ~25,000 credits; zelleri ~day 14 / ~30,000 credits — see the dated addendum in `docs/vendor-integration-reference.md`. | Confirmed API-infeasible (below); pairs naturally with anomalous-period flagging so a tenant's grant day auto-flags going forward |
| **Automated report generation** | New 2026-08-05, expanded 2026-08-21 with a concrete process spec from doing this by hand across several pulls. The reconciliation report's weekly Wed–Tue buckets, Flat/Trend-adjusted/Latest-week-annualized projections, and seat/usage splits were all hand-computed — this already produced one real error (a $439K/$110K trend-figure mistake, caught only by independently re-deriving the math), on top of the extractor bug above being caught only by manually diffing pulls. **Process observed, to inform the tool's design:** (1) run `ExtractAllThroughToday.ps1 -To <date>`; (2) run `QueryAllTotals.cmd`/`QueryWeeklyTotals.cmd` for per-vendor aggregate and Wed–Tue-bucketed totals with seat/usage split and `days_present`; (3) diff each vendor/week against the *previous* report's numbers before trusting them — this is how the retention-clamp overwrite bug was caught, and any automated version needs the same check, not just a fresh number each time; (4) recompute Flat (avg of complete weeks × 52), Trend-adjusted (OLS linear regression on complete weeks, projected one week forward × 52), and Latest-week-annualized (most recent complete week × 52) per vendor, excluding the trailing partial week and any vendor-specific excluded weeks (e.g. ChatGPT's retention-clamped week); (5) update every dependent section together — KPI totals, the vendor ledger, concentration percentages (and bar segment order, which can flip), the headline finding's ratio, both weekly tables, both projection tables, the methodology section's worked example (it cites a live figure), and the audit table's day-counts — missing one produces an internally-inconsistent report. Move steps 2–5 into C# against the existing `DailySpendRecord` query surface; once anomalous-period flagging exists, have it auto-exclude flagged weeks instead of requiring a human to remember which weeks are safe. | Anomalous-period flagging (for unattended, correct exclusion) — bucketing/computation logic itself can be sequenced first |
| **`meterist audit` reconciliation command** | Already planned as P0 (see the live report's own Audit section). New 2026-08-05 note: the spreadsheet reconciliation found real value in a byte-level compare, so the same command could optionally diff against an exported CSV/xlsx baseline, not only a vendor's own reported total. | Core extraction pipeline (already in place) |
| **Seat-count drift detection** | New 2026-08-05. `VendorRateConfig.SeatCount` is manually entered and only updated when someone remembers to run `rates set` after a contract change — this session found ecosync's Claude Enterprise seat count had risen 20→24→29 via real invoiced proration events, caught only because the user happened to supply an invoice. Several vendors expose a members/seats listing independent of the cost/usage endpoints already integrated; a periodic check flagging (not auto-correcting) a mismatch against the vendor's actual current seat count would catch a stale rate card early. | Per-vendor research spike (not yet done) to confirm each members/seats API surface |
| **Per-employee spend comparison** (leaderboard/benchmarking within a tenant) | Asymmetric vendor support — native for Claude Enterprise & ChatGPT Enterprise, estimated-only for Gemini, not applicable to Claude API Platform. Real value, but shouldn't block v1's org-level core. | v1 data model + per-vendor adapters already in place |
| **Cross-tenant benchmarking** ("Client A vs. Client B per-seat spend") | Explicitly called out as a consulting deliverable, but needs multiple tenants with real v1 data first before comparison is meaningful. | Multiple tenants live on v1 |
| **Web dashboard** | Confirmed requirement to build toward, but CLI-first was the explicit sequencing decision — core logic must not be coupled to CLI presentation. This is the point where the full **.NET Aspire AppHost** gets adopted (see [`architecture.md`](architecture.md) §9) to orchestrate the dashboard + worker + database as separate services. | Core library stable, output target decided |
| **Client-facing login** (client stakeholders view their own tenant's data directly) | Not a v1 requirement — v1 is operator-only, clients receive output rather than logging in. Direction set 2026-07-21: **Entra ID via Microsoft.Identity.Web**, not Duende IdentityServer (licensing cost + not clearly needed for this use case — see [`architecture.md`](architecture.md) §12 for the full reasoning). | Web dashboard existing; per-tenant access-control design |
| **GitHub Copilot integration** | Deferred 2026-07-21 — GA dollar-denominated API confirmed, structurally identical to existing vendor adapters, but not a confirmed active spend source for any current tenant. | Confirmed tenant need |
| **Microsoft 365 Copilot integration** | Deferred 2026-07-21 — heavier lift (needs a new Azure Cost Management connector type, not a REST-per-vendor adapter; flat seat fee has no API at all). | Confirmed tenant need + Azure Cost Management connector |
| **Budget alerts / threshold notifications** (e.g. Slack/email when a tenant approaches an overage threshold) | Not discussed as a requirement yet, but a natural extension once weekly extraction is automated and reliable. | Reliable scheduled extraction |
| **ChatGPT Enterprise credit-pool/overage tracking** (compare cumulative credits consumed this billing period against the tenant's contracted monthly grant, flag when exceeded) | Confirmed 2026-07-22: no vendor API surface exposes the credit pool size, grant amount, or expiration policy — exhaustively checked the full OpenAPI spec plus the legacy Credit Usage Report CSV, both consumption-only (see `docs/vendor-integration-reference.md`). Real zelleri data (May/June 2026 both landing at ~29–30k credits/month, a plateau rather than continued growth) suggests this may already be worth tracking. Superseded in sequencing by the manual-config item above, which starts from an unconfirmed estimate rather than waiting on the account team. | Confirmed contract grant-size number from the tenant/account team (nice-to-have, not blocking, per the manual-config item above) |
| **Automated scheduled pulls** (vs. on-demand CLI trigger) | v1 can be operator-triggered; automation adds its own reliability/alerting requirements — deployment/scheduling approach now decided, see [`architecture.md`](architecture.md) §9. | Output target decided |
| **xlsx export bridge** | Optional escape hatch for anyone still wanting the familiar spreadsheet view, generated *from* the new data model rather than being the source of truth. | Core data model stable |

## 8. Success Criteria

- Zero manual CSV exports required for weekly reporting across all four v1
  vendors, for at least one full billing cycle.
- A new client/tenant can be onboarded (credentials + vendor config) without
  code changes.
- A vendor pricing change (e.g. the confirmed Sonnet 5 rate step-up on Sep 1,
  2026) requires a config update, not a code deploy.
- Weekly aggregation output matches the spreadsheet's historical numbers
  within an agreed tolerance, for the overlapping weeks, to validate the
  cutover before the spreadsheet is retired.

## 9. Open Questions

Carried over from the research/decision process so far — see
[`README.md`](../README.md) Open Questions/TODOs for the full running list.
Highlights that affect scope/sequencing directly:

- **Resolved 2026-07-21** (see [`architecture.md`](architecture.md) §§6–7):
  output target is a database + reporting layer, SQLite for v1, migrating
  to a server-based RDBMS (engine TBD) once hosted; credential storage is
  DPAPI-encrypted local files behind `ISecretStore` for v1, with the future
  cloud secrets manager product deliberately left open rather than
  pre-committed to Azure Key Vault. Both are now unblocked for
  implementation.
- Whether Ecosync's Claude Enterprise sub-accounts have "usage credits"
  enabled (determines if any overage will ever appear for that tenant).
- ChatGPT Enterprise Cost API's exact schema — needs a hands-on spike with
  a real Admin key before the extraction/mapping code is finalized.
- Whether client stakeholders will ever need direct access to their own
  tenant's data (currently assumed: operator-only access, clients receive
  output) — a tentative direction (Entra ID + Microsoft.Identity.Web) is
  now set for *if* this happens, see [`architecture.md`](architecture.md)
  §12, but the underlying "will this ever be needed" question is still open.
- **Resolved 2026-07-21** (see [`architecture.md`](architecture.md) §§9–11):
  deployment model (local + staged Aspire adoption), observability
  (OpenTelemetry via Aspire ServiceDefaults), and testing strategy
  (WireMock.Net + BigQuery fake). One new implicit item to confirm
  explicitly before writing cloud-specific code: leaning toward **Azure**
  as the eventual hosting target, as a consequence of the Aspire + Entra ID
  choices — flagged in architecture.md §13, not yet a formal sign-off.
