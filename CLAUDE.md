# Claude Code Context: AI Vendor Spend Extraction Tool

## Project Goal

Build a .NET 10 C# application that programmatically extracts weekly spend data
from four AI vendor platforms (Claude, Claude API Platform, ChatGPT Enterprise,
Gemini Enterprise), **replacing** the manual CSV-export workflow and companion
Excel workbook (`AI_Vendor_Weekly_Spend_Model.xlsx`) currently used to track this.

This session is a fresh start — there is no existing code yet, and this
application is **not currently connected** to any Claude Code session. Treat
this file as onboarding context, not a description of working code.

## Background

The user is an IT/systems administrator who manages multiple AI vendor
platforms, and also administers AI vendor spend tracking for **multiple client
organizations** as part of a consulting practice. See `README.md` in this repo
for the full breakdown of the spreadsheet's current structure, data sources,
and known limitations — read that first.

## Project Docs

As of 2026-07-21, a `docs/` folder holds the working design docs — read
these before making architectural decisions, since they carry more current
detail than this file for anything vendor-technical:

- [`docs/product-design-document.md`](docs/product-design-document.md) —
  problem statement, v1 scope, future features/backlog, open questions.
  Source of truth for what's in v1 vs. deferred.
- [`docs/architecture.md`](docs/architecture.md) — system shape, data model,
  multi-tenancy, vendor adapter pattern, and an explicit list of open
  architectural decisions (output target, credential storage, deployment
  model) with their current status.
- [`docs/vendor-integration-reference.md`](docs/vendor-integration-reference.md) —
  living technical reference for each vendor's API (endpoints, auth,
  granularity, overage/per-user feasibility matrix, per-tenant credential
  shapes), plus deferred future-vendor research (Microsoft Copilot family).
  Update that file, not this one, when a vendor API detail changes.

## Confirmed Requirements

1. **Full spreadsheet replacement.** This is not a companion tool to the xlsx —
   it replaces it. Design accordingly (don't assume the spreadsheet stays in
   the loop long-term).
2. **Multi-tenant.** The application must support multiple customer
   organizations, each with their own vendor accounts, credentials, and spend
   data. The user administers this across multiple clients as a consultant and
   wants cross-tenant cost-projection capability as a consulting deliverable.
   Design implications:
   - Tenant isolation for credentials and data
   - Per-tenant vendor configuration (a tenant may not use all four vendors,
     may have different seat counts/rates, different billing cycles, etc.)
   - Likely need for cross-tenant reporting/benchmarking for the consultant's
     own use (e.g., "how does Client A's per-seat spend compare to Client B's")
3. **Support Changing Pricing Models.** This space is moving fast — vendors
   have changed billing structures mid-year already during this project (e.g.,
   Claude Enterprise seat-based → usage-based transition; new Gemini SKUs like
   Agent Gateway and Memory Bank appearing mid-2026). Pricing/rate data for
   every vendor must be modeled as **versioned, updatable configuration**, not
   hardcoded constants. Assume today's pricing model for any vendor could
   change again during this project's lifetime.
4. **Interface: start CLI, evolve toward a dashboard.** Build the core
   extraction/aggregation logic as a reusable library or service layer, not
   tightly coupled to a CLI presentation, so a dashboard can be layered on
   later without a rewrite.
5. **Research spike — COMPLETE (2026-07-21).** All four vendors were
   re-verified against current official docs. Bottom line: **all four now
   have a real, documented API path** — no vendor requires browser automation
   (Playwright/CoWork-driven) as a primary extraction path for MVP. Details
   below are the verified findings, not hypotheses. Re-check periodically —
   three of the four APIs are labeled beta/recently-shipped (May–Jun 2026)
   and schemas may still shift.

## What we know about each vendor's data access (VERIFIED 2026-07-21)

Full per-vendor detail (exact endpoints, auth, schemas, rate cards, and the
overage/per-user feasibility matrix) now lives in
[`docs/vendor-integration-reference.md`](docs/vendor-integration-reference.md)
— read that before implementing any vendor adapter. Headline summary:

- **All four v1 vendors have a real, documented API** — no vendor requires
  browser automation (Playwright/CoWork-driven) as a primary extraction
  path. Three of the four APIs are beta/recently-shipped (May–Jun 2026);
  re-check the reference doc periodically for schema drift.
- **Claude Enterprise** (Analytics API) returns native per-user dollar cost.
  **ChatGPT Enterprise** (`COSTS` compliance log export) returns native
  per-user *credit* usage, but a reliable per-row dollar figure is **not**
  consistently present — confirmed 2026-07-22 against real data (see
  `docs/vendor-integration-reference.md`); converting to dollars needs a
  configured credit-to-USD rate in the general case. **Gemini Enterprise**
  gives per-user activity but not per-user dollars (would be a derived
  estimate). **Claude API Platform** has no per-human-user concept at all
  (workspace/API-key scoped only).
- **Overage is not uniformly available**: Claude Enterprise only has an
  overage concept if the tenant has "usage credits" enabled; ChatGPT
  Enterprise's credit-pool size/overage rate remain console/contract-only
  even with the new API; Claude API Platform has no overage concept (pure
  usage pricing); Gemini Enterprise's is available at the aggregate SKU
  level via BigQuery.
- **Microsoft Copilot family** (GitHub Copilot, M365 Copilot, Copilot
  Studio, Security Copilot) was researched 2026-07-21 and **explicitly
  deferred from v1 scope** at the user's decision — GitHub Copilot has a
  clean GA dollar-denominated API if this is revisited later; findings are
  retained in the reference doc, not acted on now.

## Target Data Model

Full data model (`DailySpendRecord`, `VendorRateConfig`, and the future
`UserSpendRecord`) plus the vendor adapter pattern now live in
[`docs/architecture.md`](docs/architecture.md) — that is the source of
truth going forward. Key point carried over: **overage semantics vary by
vendor/plan and the model must represent that explicitly** (e.g.
`UsageOrOverage` is legitimately `0` for Claude API Platform always, and for
Claude Enterprise whenever "usage credits" isn't enabled for that
tenant/vendor pairing) — that's expected behavior, not a data gap to
investigate.

## Constraints & Unknowns — resolve these before/during build

1. **Output target — DECIDED 2026-07-21.** Database + reporting layer,
   SQLite for v1 (one file, `TenantId`-scoped tables), migrating to a
   server-based RDBMS once hosted (specific engine intentionally left
   open) — see `docs/architecture.md` §7. No longer blocking implementation.
2. **Credential storage — DECIDED 2026-07-21, and multi-tenant.** Confirmed
   credential types needed per tenant: a Claude Enterprise **Analytics API
   key**, a Claude API Platform **Admin API key**, an OpenAI **workspace
   Admin key**, and a Google Cloud **service account** (JSON key or
   workload identity) — four distinct shapes per tenant, not one. v1 uses
   DPAPI-encrypted local files per tenant behind an `ISecretStore`
   interface; the future cloud secrets manager (post-hosting) is
   intentionally left open, not pre-committed to Azure Key Vault — see
   `docs/architecture.md` §6. No longer blocking implementation.
3. **ChatGPT Enterprise Cost API schema spiked 2026-07-22 against the real
   OpenAI Programmatic Admin Platform reference** (authenticated-only doc,
   gated behind an active admin session — not publicly fetchable). Full
   shape now in `docs/vendor-integration-reference.md`: it's a `COSTS` event
   type inside the Compliance Logs Platform (JSONL file export via
   org-scoped `/compliance/organizations/{organization_id}/logs`), hourly +
   per-user grain, with a vendor-computed `estimated_cost_usd` per SKU line
   that may substantially close the overage-dollar gap (though it's an
   estimate at current rates, not an authoritative invoice figure). **Not
   yet verified against a real response** — still need a live Admin key pull
   before finalizing the extractor/mapping code. Credit pool size / true
   contracted overage rate remain contract/console-only regardless.
4. **Pricing data changes over time and per tenant.** Any hardcoded rate table
   will go stale and won't generalize across clients. Design for updatability
   and per-tenant overrides from the start — this is a first-class requirement,
   not an afterthought. This now also applies to **API response schemas**
   (three of the four vendor APIs are beta/recently-shipped as of Jul 2026)
   — don't assume today's field names are permanent either.
5. **Deployment model, scheduling, observability, and testing strategy are
   now decided (2026-07-21)** — see `docs/architecture.md` §§9–11: local
   deployment for v1, staged .NET Aspire adoption (ServiceDefaults/OpenTelemetry
   now, full AppHost deferred to the dashboard/worker phase), and a
   mocking environment split by vendor shape (WireMock.Net for HTTP vendors,
   an in-memory fake for Gemini/BigQuery). Client-facing auth direction also
   set (Entra ID + Microsoft.Identity.Web over Duende) for the future
   client-login phase — see `docs/architecture.md` §12.
6. **Microsoft Copilot family was researched and explicitly deferred from v1**
   (2026-07-21) — do not add it speculatively; revisit only if a real paid
   GitHub Copilot or M365 Copilot seat is confirmed in use for a tenant. See
   `docs/vendor-integration-reference.md`'s "Deferred / Future Vendor
   Candidates" section.

## Suggested First Steps for This Claude Code Session

1. ~~Run the research spike~~ — **done 2026-07-21**, see
   `docs/vendor-integration-reference.md`.
2. ~~Draft Product Design Document and Architecture Document~~ — **done
   2026-07-21**, see `docs/`. Use these (not this file) as the working
   source of truth for scope and system design going forward; keep this
   file as onboarding/session context only.
3. ~~Confirm the output target and credential-storage approach~~ — **decided
   2026-07-21**, see `docs/architecture.md` §§6–7.
4. ~~Design the multi-tenant data model and the pricing-config abstraction~~
   — **done**, see `docs/architecture.md` §§3, 5, 8.
5. ~~Prioritize Gemini Enterprise via BigQuery export first for a working
   end-to-end proof of concept~~ — **done**, built and verified live against
   the zelleri tenant (2026-07-22).
6. ~~Spike the ChatGPT Enterprise Cost API's actual schema~~ — **done
   2026-07-22**, against both the real OpenAPI spec and a live zelleri pull;
   see `docs/vendor-integration-reference.md`. **Adapter built** the same
   day: the `COSTS` compliance-log export has no seat line and no reliable
   dollar figure, which made this the first real consumer of
   `IVendorRateConfigRepository` (now wired up, see `docs/architecture.md`
   §8) and the `rates set`/`rates list` CLI commands. Claude Enterprise and
   Claude API Platform remain the two not-yet-built vendors.
7. ~~Decide deployment model and scheduling approach~~ — **decided
   2026-07-21**, see `docs/architecture.md` §9.
8. Revisit CLI vs. dashboard sequencing once the core library/service layer
   is in place — the interface should be a thin layer on top, not something
   the core logic depends on.
