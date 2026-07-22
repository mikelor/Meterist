# Vendor Integration Reference

**Status:** Living document — expect edits as vendor APIs evolve. Three of the
four v1 vendor APIs are beta or shipped within the last ~2 months as of this
writing; treat schemas and endpoint details as subject to change, not final.

**Last verified:** 2026-07-21

## Purpose

This is the single source of truth for how Meterist talks to each vendor —
endpoints, auth, granularity, and known gaps. [`README.md`](../README.md) and
[`CLAUDE.md`](../CLAUDE.md) carry a summary; this document is where sprint
tickets for a specific vendor adapter should point for implementation detail.

---

## v1 Vendors (confirmed in scope)

### Claude Enterprise (seat-based — Chat/Code/Cowork)

- **API:** Claude Enterprise Analytics API — `docs.claude.com/en/manage-claude/analytics-api`,
  endpoints under `https://api.anthropic.com/v1/organizations/analytics/`
  (e.g. `GET .../user_cost_report`). Beta since May 2026.
- **Auth:** Dedicated **Analytics API key** (`read:analytics` scope). Only the
  org's *primary owner* can create one (claude.ai → Organization settings → API).
- **Granularity:** Daily-bucketed, per-user (`actor.user_id`/`email`/`name`),
  per-model, in fractional cents (`amount` = post-discount, `list_amount` =
  pre-discount). Historical data starts Jan 1, 2026. Data lands within
  4–24h but can revise for up to 30 days — query 30+ days back for
  invoicing-grade totals.
- **Overage semantics (load-bearing for the data model):**
  - Seat-based plans have **no overage concept by default** — usage is
    hard-capped once a member hits their limit.
  - Overage only exists if the org enables **"usage credits"**
    (Organization settings → Usage → Enable).
  - Once enabled, the cost/usage endpoints return **overage spend only** —
    in-allotment usage generates no cost and never appears in the response.
  - Usage-based Enterprise plans (Ecosync's current state, post
    seat→usage transition) get full dollar cost from the same endpoints.
  - **Required tenant config:** a per-sub-account flag for "usage credits
    enabled?" — if false, expect empty/zero from these endpoints by design,
    not a broken integration.
- **Per-user comparison:** confirmed to work, including on seat-based plans —
  returns real dollar `amount` per user, not just credit counts. The product
  UI itself has a "top-10 users-by-spend" leaderboard.
- **Spend Limits API** (`docs.claude.com/en/manage-claude/spend-limits-api`,
  separate `read:spend_limits`/`write:spend_limits` scopes): reads/sets
  per-member spend caps — a secondary signal, not the primary reporting source.
- **Compliance API** exists (audit/governance events) — confirmed irrelevant to spend.

### Claude API Platform (developer API — billed separately from Claude Enterprise)

- **API:** Two Admin API endpoints (Admin API key, distinct from the
  Analytics key above):
  - `GET /v1/organizations/cost_report` — **daily USD cost directly**,
    grouped by `workspace_id` or `description` (parses out model/geo).
    Covers token cost, web search, code execution. Priority Tier is billed
    separately and excluded from this endpoint — track via `usage_report`'s
    `service_tier` field instead.
  - `GET /v1/organizations/usage_report/messages` — token counts (not
    dollars), `bucket_width` of 1m/1h/1d, filterable by
    model/workspace/api_key/service_tier/context_window/inference_geo.
  - **Recommend `cost_report` as the primary source** — eliminates
    maintaining a rate table for this vendor entirely. Both beta; data
    lands ~5 min after the period; poll at most once/minute.
- **Auth:** Admin API key.
- **Overage:** not applicable — this product is pure usage-based; the
  entire `cost_report` figure *is* the spend.
- **Per-user:** not available natively — granularity is by `workspace_id`/
  `api_key_id`, a developer-API construct, not a named human user. If
  per-individual comparison is wanted for this vendor, it would require a
  1:1 API-key-to-person convention, not a direct query.
- **Current rate card** (verified live at
  `platform.claude.com/docs/en/about-claude/pricing`):

  | Model | Input | 5m cache write | 1h cache write | Cache read | Output |
  |---|---|---|---|---|---|
  | Sonnet 5 (through Aug 31, 2026) | $2/MTok | $2.50 | $4 | $0.20 | $10/MTok |
  | Sonnet 5 (from Sep 1, 2026) | $3/MTok | $3.75 | $6 | $0.30 | $15/MTok |
  | Opus 4.8 | $5/MTok | $6.25 | $10 | $0.50 | $25/MTok |
  | Haiku 4.5 | $1/MTok | $1.25 | $2 | $0.10 | $5/MTok |

  Web search: $10/1,000 searches, billed on top of token cost. Web fetch:
  no extra charge.
- Only one workspace seen so far ("Ecosync Universal Sandbox") — still
  confirm whether other workspaces/API keys exist in production, and across
  other tenants.

### ChatGPT Enterprise

- **API:** Unified **Cost API**, shipped June 18, 2026 as part of OpenAI's
  "New usage analytics and updated spend controls" release. Unifies ChatGPT
  + Codex credit consumption. A separate **Spend Controls API** manages
  limits. The old CSV export ("Credit Usage Report") still exists as a
  fallback/UI path, not removed.
- **Auth:** Workspace-scoped **Admin key** (Global Admin Console →
  Credentials → Admin keys). Reportedly exposes up to 120 days of history.
- **Do not confuse with** the pre-existing `/v1/organization/costs`
  developer-platform Admin API — that's a different product (api.openai.com
  token spend, project-scoped), confirmed to NOT cover ChatGPT/Codex
  workspace credits.
- **Per-user:** confirmed — the Cost API breaks down credit spend "by user,
  product, and model" and supports identifying top users.
- **Overage — the key gap, not eliminated by the new API:** the Cost API
  returns raw credit consumption (now via API instead of CSV), but does
  **not** expose the credit pool size, the "unbilled overage" dollar figure,
  or the contracted overage rate as API fields. Those remain
  **console-only** (Global Admin Console → Billing) or **contract-only**
  ("the exact rates are not exposed in public documentation—only accessible
  through your OpenAI account agreement"). The manual reconstruction (sum
  credits → subtract unbilled overage from console → back into pool size →
  walk cumulative usage) still applies; only the "sum credits" step is now
  API-automatable. Overage rate observed for Ecosync: $0.07/credit — confirm
  per-tenant, do not assume universal.
- **Schema risk:** no public field-level API reference was found (OpenAI
  gates the full reference behind authenticated developer access). **Spike
  this with a real Admin key before finalizing the extraction/mapping code**
  — this is the least-documented of the four v1 APIs.
- Seat count/rate: Ecosync has 50 purchased / 12 active seats. Enterprise
  contracts typically bill against committed seats, not active usage —
  confirm via contract/order form per tenant, not assumption.
- Compliance API confirmed to NOT carry billing/credit data.

### Gemini Enterprise

- **API:** Cloud Billing export to **BigQuery** — the strongest automation
  candidate of the four v1 vendors.
- **Setup:** one-time, per-billing-account console step (needs Billing
  Account Costs Manager/Administrator + BigQuery Admin roles). Once
  enabled, ongoing queries run via a normal **service account** with
  standard BigQuery IAM (`BigQuery User`/`Data Viewer`) — fully automatable
  after that one manual step.
- **Granularity:** **hourly** (finer than daily), SKU-level. Confirmed to
  include both the subscription SKU and overage SKU(s) for Gemini
  Enterprise specifically.
- **Per-user:** available for *activity*, not *cost* — the key gap for this vendor:
  - The standard/detailed/FOCUS BigQuery billing export schemas have **no
    user-identity column at all**, at any tier — cost stays aggregated at
    project/SKU level.
  - A *separate* path exists for per-user activity: a restricted,
    allowlist-only "User-Level metrics" view, and a BigQuery telemetry
    export table (`discoveryengine_googleapis_com_gemini_enterprise_user_activity`)
    capturing IAM email identity plus message/interaction counts.
  - Any per-user *dollar* figure would have to be a derived approximation
    (e.g., allocate overage proportionally to message-count share) — never
    a value the vendor reports directly. **Label this clearly as estimated
    in any UI/report if built.**
- **Seat/license counts:** Discovery Engine API confirmed — subscription-details
  GET plus `distributeLicenseConfig`/`retractLicenseConfig`, callable via
  service account. Response includes `licenseCount`.
- **New SKU lines** confirmed on Google's own pricing page: **Agent
  Gateway** billing started **Jul 13, 2026** ($0.085/vCPU-hour, billed as
  Agent Compute); **Memory Bank/Sessions** billing starts **Sep 1, 2026**
  (Agent Storage $0.30/GiB-month + read/write compute).

---

## Cross-Vendor Overage & Per-User Matrix

| Vendor | Overage extractable? | Per-user usage? | Per-user $ cost? |
|---|---|---|---|
| Claude Enterprise (seat-based) | Yes, but only if tenant has "usage credits" enabled — otherwise no overage concept exists | Yes | Yes (native) |
| Claude API Platform | N/A — no seat/overage concept, it's all usage-priced | No (workspace/API-key granularity only) | No |
| ChatGPT Enterprise | Partial — raw credits via API now; pool size/overage rate still console/contract-only | Yes | Yes (via Cost API) |
| Gemini Enterprise | Yes, at aggregate SKU level via BigQuery | Yes (separate telemetry path, permission-gated) | No — derived/allocated only |

**Implication:** per-employee spend comparison is cleanly supportable today
for Claude Enterprise and ChatGPT Enterprise, partially for Gemini (usage
counts real, cost estimated), and not meaningful for Claude API Platform.
See the Product Design Document's Future Features section for how this
should be sequenced.

## Per-Tenant Credential Types Required (v1 vendors)

| Vendor | Credential type | Who provisions it | Notes |
|---|---|---|---|
| Claude Enterprise | Analytics API key | Org primary owner only | `read:analytics` scope |
| Claude API Platform | Admin API key | Org admin | Separate key from Analytics API |
| ChatGPT Enterprise | Workspace Admin key | Workspace owner/admin | Global Admin Console → Credentials |
| Gemini Enterprise | GCP service account (JSON key or workload identity) | GCP project owner/IAM admin | Needs BigQuery IAM roles; billing export enabled once per billing account |

Four distinct credential *shapes* per tenant, not one uniform type — the
credential storage design needs to accommodate this from day one.

---

## Deferred / Future Vendor Candidates (not in v1 scope)

Researched 2026-07-21 at the user's request, then explicitly deferred from
v1 — retained here so the groundwork isn't lost if priorities change.

### Microsoft Copilot family

Microsoft has several distinct products under the "Copilot" name with
different billing models — they are not one integration.

- **GitHub Copilot (Business/Enterprise) — the easy candidate if this is ever added.**
  All plans moved to usage-based (token) billing June 1, 2026. The
  **Billing Usage REST API is GA** and returns real dollar amounts:
  `GET /organizations/{org}/settings/billing/usage` (also
  `/ai_credit/usage`, `/usage/summary`), filterable by `year`/`month`/`day`,
  `user`, `model`, `product`, `sku`. Daily, per-user, in dollars. 24-month
  retention. Auth: PAT (classic) or GitHub App with `manage_billing:copilot`
  or `read:org` scope, org-admin permission required. A separate metrics API
  gives engagement stats (active users/repos), not cost. Structurally the
  closest analog to the four v1 vendors — GA, dollar-denominated, simple
  token auth.
- **Microsoft 365 Copilot — a heavier lift, mixed API surface.**
  Usage/activity API is GA via Microsoft Graph
  (`copilot/reports/getMicrosoft365CopilotUsageUserDetail`, v1.0 GA, v2 beta
  adds prompt counts), but it's **activity only** — no dollar cost. Real
  dollar cost for the usage-based "Copilot Credits" components (Cowork,
  Copilot Chat pay-as-you-go, Copilot Retrieval API, SharePoint agents)
  comes from the **Azure Cost Management API** (GA, daily, real dollars, via
  Azure service principal + Cost Management Reader RBAC role), filterable
  by meter/tag. The traditional flat per-seat license fee has **no API at
  all** — console/CSV-export only, same limitation as the products this
  tool is trying to replace.
- **Copilot Studio** — now billed in the same "Copilot Credit" currency as
  M365, via the connected Azure subscription. No Copilot-Studio-specific
  REST cost API; same Azure Cost Management API path as M365 Copilot
  applies, with console-only CSV drill-down for per-environment/agent detail.
- **Security Copilot** — billed via Security Compute Units (Azure meter,
  hourly provisioned + overage); same Azure Cost Management API path. No
  dedicated billing API; granular usage is console-dashboard-only.
- **Copilot for Sales/Service** — no billing API found; deprioritize.

**If this is picked up later:** integrate GitHub Copilot first (GA,
dollar-denominated, structurally identical to the existing four vendors —
just another adapter, no new connector type needed). M365 Copilot / Copilot
Studio / Security Copilot would share one new **Azure Cost Management
connector type** (not a vendor-specific REST adapter like the others), plus
accept that the M365 flat seat-license fee stays a manual figure — it
doesn't fully escape the CSV-export problem this tool exists to solve.

**Decision (2026-07-21):** left out of v1 scope. Revisit only if a real
GitHub Copilot Business/Enterprise or M365 Copilot seat is actually in
paid use for Ecosync or another client — this was speculative
future-proofing, not a confirmed need.
