# Meterist — Product Design Document

**Status:** Draft — v1 scope confirmed with the user; architecture and
implementation not yet started.
**Last updated:** 2026-07-21

## 1. Overview

Meterist is a .NET 11 C# application that replaces a manual Excel workbook
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
- **Prefer each vendor's current API over a legacy one** wherever the
  current API returns the same or better data. A legacy endpoint or export
  stays only as a cross-check or fallback, not as the source of record.
  (Added 2026-11-19 — see §7.)
- **Report usage per individual user as well as in aggregate**, wherever
  the vendor exposes it. (Added 2026-11-19 — see §7.)

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

## 7. Backlog: what is delivered, what is open, what is deferred

**Status as of 2026-11-21.** This section says what is in v1, what has been delivered since, and what is deliberately
deferred and why. **The GitHub issues are the source of truth for status** (milestones `Sprint 1` to `Sprint 5`, issues
#5 to #72); the history behind a decision lives in `CLAUDE.md`'s Suggested First Steps and in
[`vendor-integration-reference.md`](vendor-integration-reference.md), and is not repeated here.

Status key: ✅ delivered · 🟡 partly delivered · 🔵 open (issue number where one exists) · ⏸ deferred by choice.

### Two requirements that govern the backlog (direction set 2026-11-19)

1. **New APIs are preferred over legacy ones.** Where a vendor's current API returns the same or better data than the
   one Meterist calls today, Meterist moves to it and keeps the older path only as a cross-check or fallback. "Same or
   better" is judged on the data Meterist reports, not on freshness alone: a slower-arriving daily figure is acceptable
   for a daily-grain tool.
   - **ChatGPT Enterprise:** ✅ the Daily Usage API is the source of record; the `COSTS` export is a best-effort
     cross-check only (#5 to #10).
   - **Claude Enterprise:** ✅ cost moved to `/analytics/cost` with a legacy fallback setting (#21, #24). 🔵 Per-user activity by
     product (#23).
   - **Claude API Platform:** 🔵 move to the new analytics API with per-actor attribution (#25); the legacy `cost_report` still
     works.
   - **Gemini Enterprise:** the billing export has no newer replacement and stays the source of spend. 🔵 Newer surfaces
     are added alongside it for activity and seats (#33 to #39).
2. **Per-user reporting is a requirement, not a later extra.** Users are **named** in reports (name and email), by operator
   decision, so reports carry personal data and stay local under `artifacts/`. Where a vendor reports per-user activity
   but not per-user cost (Gemini), the dollar figure is an allocation and is labelled estimated; where usage attributes
   only to an API key (Claude API Platform workspace keys), it is reported at key level and labelled so. Every per-user
   view reconciles to the aggregate for the same period.
   - ✅ Storage, the `users usage` query, ChatGPT and Claude Enterprise per-user data (#15 to #19).
   - ✅ Per-user and active-user sections in the client report (#32).

### Data quality and extraction safeguards

| Item | Status | Where it landed or what is left |
|---|---|---|
| New-date inserts may silently fail to persist | ✅ #64 | Not reproducible against a throwaway database with the current code. Extraction now reads back every day it writes and fails the vendor, naming the dates, if one is missing. `audit coverage` stays as the independent check. |
| ChatGPT usage erosion inside the retention window | ✅ | The never-lower rule plus the Daily Usage API as source of record (`CLAUDE.md` item 13). 🔵 The remaining early-month gap against a console export is #71. |
| Missing days go unnoticed | ✅ #12 | `audit coverage`. |
| Daily extraction so retention windows are never missed | ✅ #13, #67 | `scripts/RunScheduledExtraction.ps1` and `Register-DailyExtractionTask.ps1`, including roster snapshots. Spend-limit snapshots are in it too, with their own exit bit (#83); `audit seats` is deliberately not. |
| Tax and support charges | ✅ #35, #53 | Never counted in any total; recorded as non-usage rows in the Gemini breakdown. |
| Gemini SKU filter guard | ✅ #34 | |
| Anomalous-period flagging (mark a tenant, vendor and date unreliable for trends, such as retention-clamped, credit-grant-affected or calendar-partial) | 🔵 #84 (decide first) | Not started. The retention-clamp case is largely gone now that the Daily Usage API is the source; the report excludes the trailing partial week and ChatGPT weeks with missing days in code. 🔵 Excluding a *leading* partial week for every vendor is #69. A stored reason code is still the cleaner design if more cases appear. |
| ChatGPT credit-grant manual config (approximate grant day and amount per tenant, as versioned config) | 🔵 #84 (decide first) | Not started. The grant size and day are still not exposed by any API (re-checked when #29 was built: the Spend Controls API reports per-user usage and caps, not the pool). Pairs with anomalous-period flagging. |
| ChatGPT credit-pool and overage tracking | ⏸ blocked | No API exposes pool size or grant; waits on a number from the tenant or account team, or on the manual config above. |
| Assumptions resting on data not yet seen | 🔵 #72 | Revisit once the data exists. |

### Reporting

| Item | Status | Where it landed or what is left |
|---|---|---|
| Automated report generation | ✅ | `report generate` builds the Client Spend & Benchmark Report from stored data: weekly buckets, the three projections and the vendor ledger are computed in code (see [`client-report-generation.md`](client-report-generation.md)). ✅ Leading partial week for every vendor (#69). 🔵 Left: a diff against the previous report's numbers before trusting a new one (#85). |
| Cross-tenant benchmarking | 🟡 | The report compares tenants side by side. A deeper per-seat comparison across tenants needs more tenants with data. |
| Per-employee spend comparison | ✅ #15 to #19, #32 | Storage, the `users usage` query, and the report's people sections: active users and seat utilization, top consumers, usage by person (reconciled to each vendor's aggregate) and idle seats. |
| `meterist audit` reconciliation command | 🟡 | `audit coverage` and `audit gemini-breakdown` exist. 🔵 Not built, no issue: an optional diff against an exported CSV or spreadsheet baseline. |
| ChatGPT first-week and source footnotes in the report | ✅ | Part of Sprint 1. |

### Seats, rosters, active users and spend caps (epic #27)

| Item | Status | Where it landed or what is left |
|---|---|---|
| Member rosters, as dated snapshots | ✅ #28, #30, #67 | Claude Enterprise and ChatGPT Enterprise; `roster snapshot`, `roster show`, taken in the scheduled run. |
| Claude Enterprise active users and seat counts | ✅ #22 | Stored per day by `extract`; `activity show`. |
| Per-member spend against limit | ✅ #26, #29 | Claude Spend Limits and ChatGPT Spend Controls; `limits snapshot`, `limits show`. Credits are shown as credits, not converted. |
| Seat-count drift and idle-seat detection | ✅ #31 | `audit seats` compares the billed seat count in the rate configuration with the vendor's assigned seats and the roster size, flags drift without correcting it, and lists rostered members with no activity in a configurable window. Claude Enterprise and ChatGPT Enterprise; Gemini once its roster (#37) lands. Not in the scheduled run yet. |
| Claude Enterprise per-user activity by product | ✅ #23 | Stored per day by `extract` (headline counts and the full per-product detail); `users activity`. Using it to date each seat's last activity in `audit seats` is a follow-up. |
| Budget alerts and threshold notifications | ⏸ | Natural next step now that the scheduled run and the limit snapshots exist; not yet a requirement. |

### Gemini Enterprise (epic #33)

All 🔵 and in Sprint 4: a spike on the user-activity log sink to BigQuery (#36), the licence roster (#37), adoption metrics
(#38) and an estimated per-user cost from activity share (#39). Per-user Gemini dollars will always be labelled estimated.

### Deferred by choice

| Item | Why deferred | Depends on |
|---|---|---|
| **Web dashboard** | CLI-first was the explicit sequencing decision, and core logic must not be coupled to the CLI. This is where the full **.NET Aspire AppHost** gets adopted (see [`architecture.md`](architecture.md) §9). | Core library stable (it is), output target decided (it is) |
| **Client-facing login** | v1 is operator-only; clients receive output. Direction if needed: Entra ID via Microsoft.Identity.Web, not Duende (see [`architecture.md`](architecture.md) §12). | A web dashboard, and a per-tenant access-control design |
| **GitHub Copilot integration** | Deferred 2026-07-21: a GA dollar API exists and the adapter would be like the others, but no tenant has confirmed spend on it. | A confirmed tenant need |
| **Microsoft 365 Copilot integration** | Deferred 2026-07-21: needs an Azure Cost Management connector type, and the flat seat fee has no API. | A confirmed tenant need, and that connector |
| **xlsx export bridge** | An optional escape hatch for the familiar spreadsheet view, generated *from* the data model rather than being its source. | Stable data model |

### Follow-ups already filed for Sprint 5

Make the ChatGPT `COSTS` cross-check fast enough for a daily run (#70), close the early-month gap between the Daily Usage
API and a console export (#71), and revisit assumptions that rest on data not yet seen (#72).

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
