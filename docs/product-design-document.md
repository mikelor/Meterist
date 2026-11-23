# Meterist — Product Design Document

**Status:** Draft — v1 scope confirmed with the user; architecture and
implementation not yet started.
**Last updated:** 2026-07-21

## 1. Overview

Meterist is a .NET 11 C# application that tracks AI vendor spend. It pulls
spend data programmatically from each vendor's API instead of requiring a
weekly manual login + CSV export per vendor, and is built multi-tenant from
day one to support a consulting practice spanning multiple client
organizations, not just the operator's own org (Ecosync Universal).

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
- Provide weekly tracking and annual projection scenarios on top of a
  daily-grain data model.
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
6. **Historical backfill.** Extraction takes a date range, so any history a
   vendor still retains can be loaded in one run; history a vendor no longer
   retains cannot be recovered (retention per vendor is in
   `vendor-integration-reference.md`).
7. **Annual projection scenarios** (flat, trend-adjusted,
   latest-week-annualized) computed over the normalized data model.

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
   - **Claude API Platform:** ✅ cost moved to `/analytics/cost` with per-actor attribution and a legacy fallback setting (#25); the legacy `cost_report` still
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
| ChatGPT credit-pool and overage tracking | ⏸ blocked | No API exposes pool size or grant; waits on a number from the tenant or account team. The manual grant config that would have fed it was dropped for now (#84, see Deferred by choice). |
| Assumptions resting on data not yet seen | 🔵 #72 | Revisit once the data exists. |

### Reporting

| Item | Status | Where it landed or what is left |
|---|---|---|
| Automated report generation | ✅ | `report generate` builds the Client Spend & Benchmark Report from stored data: weekly buckets, the three projections and the vendor ledger are computed in code (see [`client-report-generation.md`](client-report-generation.md)). ✅ Leading partial week for every vendor (#69). ✅ A diff of each vendor week against the previous report's manifest, with decreases marked and a non-zero exit on request (#85). |
| Cross-tenant benchmarking | 🟡 | The report compares tenants side by side. A deeper per-seat comparison across tenants needs more tenants with data. |
| Per-employee spend comparison | ✅ #15 to #19, #32 | Storage, the `users usage` query, and the report's people sections: active users and seat utilization, top consumers, usage by person (reconciled to each vendor's aggregate) and idle seats. |
| `meterist audit` reconciliation command | 🟡 | `audit coverage` and `audit gemini-breakdown` exist. 🔵 Not built, no issue: an optional diff against an exported CSV baseline. |
| ChatGPT first-week and source footnotes in the report | ✅ | Part of Sprint 1. |

### Seats, rosters, active users and spend caps (epic #27)

| Item | Status | Where it landed or what is left |
|---|---|---|
| Member rosters, as dated snapshots | ✅ #28, #30, #67 | Claude Enterprise and ChatGPT Enterprise; `roster snapshot`, `roster show`, taken in the scheduled run. |
| Claude Enterprise active users and seat counts | ✅ #22 | Stored per day by `extract`; `activity show`. |
| Per-member spend against limit | ✅ #26, #29 | Claude Spend Limits and ChatGPT Spend Controls; `limits snapshot`, `limits show`. Credits are shown as credits, not converted. |
| Seat-count drift and idle-seat detection | ✅ #31 | `audit seats` compares the billed seat count in the rate configuration with the vendor's assigned seats and the roster size, flags drift without correcting it, and lists rostered members with no activity in a configurable window. Claude Enterprise and ChatGPT Enterprise; Gemini Enterprise too once its credential carries the Discovery Engine settings (#37): people with no licence are listed but not counted as seats, and refused sign-ins are noted. Not in the scheduled run yet. Claude Enterprise idle seats also count vendor-reported per-person activity (#91). |
| Claude Enterprise per-user activity by product | ✅ #23 | Stored per day by `extract` (headline counts and the full per-product detail); `users activity`. Using it to date each seat's last activity in `audit seats` is a follow-up. |
| Budget alerts and threshold notifications | ⏸ | Natural next step now that the scheduled run and the limit snapshots exist; not yet a requirement. |

### Gemini Enterprise (epic #33)

Sprint 4: 🔵 a spike on the user-activity log sink to BigQuery (#36, prepared, waiting on a client's agreement), ✅ the licence roster (#37, built from Google's reference, not yet run live), 🔵 adoption metrics
(#38) and an estimated per-user cost from activity share (#39). Per-user Gemini dollars will always be labelled estimated.

### Deferred by choice

| Item | Why deferred | Depends on |
|---|---|---|
| **Web dashboard** | CLI-first was the explicit sequencing decision, and core logic must not be coupled to the CLI. This is where the full **.NET Aspire AppHost** gets adopted (see [`architecture.md`](architecture.md) §9). | Core library stable (it is), output target decided (it is) |
| **Anomalous-period flagging** (a stored reason code marking a tenant, vendor and date unreliable for trends) | Decided 2026-11-22 (#84): not built. The retention-clamp case is gone (the Daily Usage API is the source of record and usage is never lowered); a calendar-partial week is kept out of every projection for every vendor in code (`ProjectionEligibility`, #69); a ChatGPT week with a day missing is flagged in code. Nothing left needs a person's memory except two unproven cases, a monthly credit-grant week and the first month of a tenant's API history (#71), and a per-day reason table is too heavy for either. | A credit-grant effect on the weekly figures actually being shown, or #71 finding early-month API figures unreliable. The smallest design then is a versioned per-tenant "figures trusted from" date, not a reason code on every day. |
| **ChatGPT credit-grant manual config** (approximate grant day and credit amount per tenant, as versioned config) | Decided 2026-11-22 (#84): not built. There is no confirmed number: the two recorded figures (about day 30 and 25,000 credits for one tenant, about day 14 and 30,000 for the other) are unconfirmed observations that differ by tenant, and the API still exposes no pool, grant or expiry. Nothing downstream needs them now: the flagging above is not built and no credit-pool overage view is planned. | The OpenAI account team confirming the contract terms (then a versioned per-tenant config in the `VendorRateConfig` pattern), or credit-pool overage tracking being wanted. |
| **Contract term, seat-change events, seat projection from the contract, invoice matching** | Open for the vNext design sprint (#89, low priority); the seat projection from the contract is built (#101, in review). The accrual is right today; four gaps are sized and ranked in [`vnext-contract-model.md`](vnext-contract-model.md). The one that changes a client-visible number is the seat projection lagging a seat step (a 14 to 22% understatement of the seat part in the worked example). | The design sprint's decision. Recommended first build: project the seat fee from the rate in force on the report's last day. |
| **Client-facing login** | v1 is operator-only; clients receive output. Direction if needed: Entra ID via Microsoft.Identity.Web, not Duende (see [`architecture.md`](architecture.md) §12). | A web dashboard, and a per-tenant access-control design |
| **GitHub Copilot integration** | Deferred 2026-07-21: a GA dollar API exists and the adapter would be like the others, but no tenant has confirmed spend on it. | A confirmed tenant need |
| **Microsoft 365 Copilot integration** | Deferred 2026-07-21: needs an Azure Cost Management connector type, and the flat seat fee has no API. | A confirmed tenant need, and that connector |
| **xlsx export bridge** | An optional export of the data to a spreadsheet file for anyone who wants one, generated *from* the data model rather than being its source. | Stable data model |

### Follow-ups already filed for Sprint 5

Make the ChatGPT `COSTS` cross-check fast enough for a daily run (#70, ✅ built: bounded parallel downloads, a last-N-days window and an off switch; after-timings still to measure), close the early-month gap between the Daily Usage
API and a console export (#71), and revisit assumptions that rest on data not yet seen (#72).

## 8. Success Criteria

- Zero manual CSV exports required for weekly reporting across all four v1
  vendors, for at least one full billing cycle.
- A new client/tenant can be onboarded (credentials + vendor config) without
  code changes.
- A vendor pricing change (e.g. the confirmed Sonnet 5 rate step-up on Sep 1,
  2026) requires a config update, not a code deploy.
- Weekly aggregation output reconciles to each vendor's own console or
  invoice figures within an agreed tolerance, for at least one billing cycle
  per vendor.

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
