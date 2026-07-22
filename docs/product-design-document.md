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

| Feature | Why deferred from v1 | Depends on |
|---|---|---|
| **Per-employee spend comparison** (leaderboard/benchmarking within a tenant) | Asymmetric vendor support — native for Claude Enterprise & ChatGPT Enterprise, estimated-only for Gemini, not applicable to Claude API Platform. Real value, but shouldn't block v1's org-level core. | v1 data model + per-vendor adapters already in place |
| **Cross-tenant benchmarking** ("Client A vs. Client B per-seat spend") | Explicitly called out as a consulting deliverable, but needs multiple tenants with real v1 data first before comparison is meaningful. | Multiple tenants live on v1 |
| **Web dashboard** | Confirmed requirement to build toward, but CLI-first was the explicit sequencing decision — core logic must not be coupled to CLI presentation. This is the point where the full **.NET Aspire AppHost** gets adopted (see [`architecture.md`](architecture.md) §9) to orchestrate the dashboard + worker + database as separate services. | Core library stable, output target decided |
| **Client-facing login** (client stakeholders view their own tenant's data directly) | Not a v1 requirement — v1 is operator-only, clients receive output rather than logging in. Direction set 2026-07-21: **Entra ID via Microsoft.Identity.Web**, not Duende IdentityServer (licensing cost + not clearly needed for this use case — see [`architecture.md`](architecture.md) §12 for the full reasoning). | Web dashboard existing; per-tenant access-control design |
| **GitHub Copilot integration** | Deferred 2026-07-21 — GA dollar-denominated API confirmed, structurally identical to existing vendor adapters, but not a confirmed active spend source for any current tenant. | Confirmed tenant need |
| **Microsoft 365 Copilot integration** | Deferred 2026-07-21 — heavier lift (needs a new Azure Cost Management connector type, not a REST-per-vendor adapter; flat seat fee has no API at all). | Confirmed tenant need + Azure Cost Management connector |
| **Budget alerts / threshold notifications** (e.g. Slack/email when a tenant approaches an overage threshold) | Not discussed as a requirement yet, but a natural extension once weekly extraction is automated and reliable. | Reliable scheduled extraction |
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
