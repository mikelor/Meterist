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

### Claude Enterprise (seat-based — Chat/Code/Cowork) — IMPLEMENTED (2026-07-22)

- **API:** Claude Enterprise Analytics API, verified 2026-07-22 against the
  real public reference (`platform.claude.com/docs/en/api/admin/analytics`
  and `.../manage-claude/analytics-api`, both publicly fetchable, same as
  Claude API Platform's docs). Endpoint used:
  `GET https://api.anthropic.com/v1/organizations/analytics/user_cost_report`
  — per-user cost. No organization ID in the path — the key scopes the
  request, same as Claude API Platform. Two sibling endpoints exist
  (`/cost_report` for org-wide totals without per-user breakdown,
  `/user_usage_report`/`/usage_report` for token counts instead of dollars)
  but weren't needed — `user_cost_report` alone gives both a per-day org
  total (by summing) and per-user identity to preserve in raw storage.
- **Auth:** Dedicated **Analytics API key** (`read:analytics` scope, header
  `x-api-key`), created by the org's *primary owner* only
  (`claude.ai/admin-settings/api-access`) — **a different key type from
  Claude API Platform's Admin key**, not interchangeable. Bare string
  credential, no wrapper JSON needed (no org ID or other per-tenant config).
- **⚠️ `bucket_width=1d` must be passed explicitly** — omitting it collapses
  the response to one row per user for the *entire* queried range, not one
  row per (user, day). Confirmed from the real reference: "When set, one
  actor may span multiple rows (one per time bucket)."
- **⚠️ Hard 31-day span cap per query, distinct from pagination** —
  `ending_at` "Defaults to min(now, starting_at + 31 days). Max span: 31
  days." Unlike Claude API Platform's `cost_report` (which only caps *page
  size*, with `has_more`/`next_page` handling arbitrarily long ranges), a
  multi-month extraction here requires an **outer loop stepping through
  ≤31-day windows**, each with its own inner pagination loop. New
  requirement not present in any of the other three adapters.
- **⚠️ `amount` is a decimal string in cents** — identical pitfall to Claude
  API Platform's `cost_report` ("post-discount, pre-credit... divide by
  100"). Same conversion, same unit-test discipline, this time gotten right
  from the start rather than discovered via a failing test.
- **⚠️ `starting_at`/`ending_at` are full ISO datetime strings** — the exact
  shape that broke `DateOnly.TryParse` while building
  `HttpClaudeCostReportRepository` for Claude API Platform. Built this
  adapter with the `DateTime.TryParse` + `DateOnly.FromDateTime` fix from
  the start, with a dedicated regression test using a non-midnight
  timestamp (`"2026-07-20T05:30:00Z"`) to prove it.
- **Date range limits:** `starting_at` "must be within last 365 days, no
  earlier than 2026-01-01T00:00:00Z" — clamped with a warning, mirroring
  ChatGPT's 30-day retention clamp.
- **Overage semantics — no code branch needed, confirmed by the real docs:**
  "The cost and usage endpoints apply to usage-based Enterprise plans; for
  seat-based Enterprise plans, they reflect usage credits only." There is
  **no API field indicating which kind of plan a tenant is on** — and none
  is needed: the endpoint naturally returns `$0` when usage credits aren't
  enabled, which the normalizer already treats as a legitimate
  `UsageOrOverage = 0` (same pattern as `SupportsOverage`-can-be-zero
  elsewhere). No new tenant-config flag was built for this.
- **Per-user:** confirmed real vendor-reported dollar `amount` per user
  (`actor.user_id`/`email`/`name`), not a derived estimate — this vendor's
  standout capability, preserved in raw storage even though the v1
  normalizer aggregates it into a daily org total.
- **Seat fee still isn't in this schema at all** — same gap as ChatGPT.
  Comes from `VendorRateConfig` via the same `IVendorRateConfigRepository`
  mechanism, reused unchanged for a second vendor (see the shared
  `VendorRateConfigExtensions.FindRateForDay`/`ProrateSeatFee` helpers in
  `Meterist.Core.Models`, extracted once this vendor needed the identical
  logic `ChatGptEnterpriseSpendNormalizer` already had).
- **Spend Limits API** (`docs.claude.com/en/manage-claude/spend-limits-api`,
  separate `read:spend_limits`/`write:spend_limits` scopes): reads/sets
  per-member spend caps — a secondary signal, not used by this adapter.
- **Compliance API** exists (audit/governance events) — confirmed irrelevant to spend.

### Claude API Platform (developer API — billed separately from Claude Enterprise) — IMPLEMENTED (2026-07-22)

- **API:** Two Admin API endpoints (Admin API key, distinct from the
  Analytics key above):
  - `GET https://api.anthropic.com/v1/organizations/cost_report` — **daily
    USD cost directly**, grouped by `workspace_id` and/or `description`
    (grouping by `description` also parses out `model`/`token_type`/
    `cost_type`/`inference_geo`). Covers token cost, web search, code
    execution. Priority Tier is billed separately and excluded from this
    endpoint — track via `usage_report`'s `service_tier` field instead. **No
    organization ID in the path at all** — the Admin API key itself scopes
    the request.
  - `GET /v1/organizations/usage_report/messages` — token counts (not
    dollars), `bucket_width` of 1m/1h/1d, filterable by
    model/workspace/api_key/service_tier/context_window/inference_geo. Not
    used by the adapter — `cost_report` alone is sufficient for v1.
  - **`cost_report` used as the sole source** — confirmed 2026-07-22 against
    the real public API reference
    (`platform.claude.com/docs/en/api/usage-cost-api` and
    `.../api/admin-api/usage-cost/get-cost-report`, both publicly fetchable,
    unlike ChatGPT's admin-console-gated docs). Eliminates maintaining a
    rate table for this vendor entirely — `ClaudeApiPlatformSpendNormalizer`
    is the simplest of the four, ignoring `applicableRates` completely (no
    seat fee, no credit conversion). Data lands ~5 min after the period; poll
    at most once/minute. Pagination via `has_more`/`next_page`, same shape
    as ChatGPT's `COSTS` list endpoint.
  - **⚠️ `amount` is a decimal string in *cents*, not dollars** — e.g.
    `"12345"` means $123.45. Confirmed directly against the docs' own
    example (`"amount": "123.78912"` for one model/token-type/day line
    item — plausible only as cents, not dollars, at that granularity). Get
    this wrong and every cost is 100x too large; `HttpClaudeCostReportRepository`
    divides by 100 explicitly and a unit test pins the conversion.
  - **⚠️ Implementation gotcha found via live test failure, not docs:**
    `starting_at`/`ending_at` are full ISO-8601 datetime strings
    (`"2026-07-20T00:00:00Z"`), and `DateOnly.TryParse` **rejects** these
    outright ("contains parts which are not specific to the DateOnly") —
    every bucket silently got skipped until caught by a WireMock-backed
    test asserting non-empty results. Fixed by parsing as `DateTime` first
    (`DateTimeStyles.AdjustToUniversal | AssumeUniversal`), then
    `DateOnly.FromDateTime(...)`. Worth remembering for any other vendor
    whose timestamps carry a time component — Gemini/ChatGPT's date fields
    were already bare `YYYY-MM-DD` strings, so this specific pitfall never
    came up until now.
  - Auth headers: `anthropic-version: 2023-06-01` and `x-api-key:
    <admin-key>`, both per-request (never as shared `HttpClient` default
    headers) — same discipline as ChatGPT's per-request `Authorization`,
    since the typed client is pooled across tenants.
- **Auth:** Admin API key — a **bare string credential**, no wrapper JSON
  record needed (unlike Gemini/ChatGPT), since there's no other per-tenant
  config (no org ID, no dataset/table).
- **Overage:** not applicable — this product is pure usage-based; the
  entire `cost_report` figure *is* the spend. `SeatFee` is always `0` for
  this vendor; the whole daily total lands in `UsageOrOverage`.
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

- **⚠️ Schema spiked 2026-07-22 against the real, authenticated OpenAI
  Programmatic Admin Platform reference** (`chatgpt.com/admin/api-reference`,
  v2.5.3 as of that date — this page 403s without an active admin session,
  which is why it wasn't visible in the 2026-07-21 research pass). The
  marketing "unified Cost API" turns out to be a `COSTS` event type inside
  the **Compliance Logs Platform** (immutable JSONL file export), not a
  simple REST reporting endpoint — see below for the concrete shape. Not yet
  tested against a live pull (that's the next step — zelleri Admin key is
  being provisioned); everything below is transcribed from the official
  reference, not yet verified against real response bytes.
- **Base URL:** `https://api.chatgpt.com/v1/`. Auth: `Authorization: Bearer
  <admin_api_key>`, an Admin key created in the OpenAI Admin Console
  (Billing → Create admin key in the UI seen 2026-07-22; the reference doc
  says "Credentials > Admin keys"). Scope needed: `chatgpt.enterprise.compliance_logs_platform.costs.read`
  (shown in the console as **Compliance logging platform → Costs → Read**),
  or the broader `chatgpt.enterprise.compliance_logs_platform.read`. Read is
  the only permission level offered for this scope — there is no write
  variant, consistent with it being a reporting-only surface.
- **⚠️ Org-scoped, not workspace-scoped** — a doc correction landed the same
  day we spiked this (v2.5.3, 2026-07-21): COSTS files are listed via
  `GET /compliance/organizations/{organization_id}/logs?event_type=COSTS`
  using the **API Platform Organization ID** (`org-...`, found in workspace
  Settings → General → "Organization ID" — NOT the Workspace ID). The
  workspace-scoped logs route (`/compliance/workspaces/{workspace_id}/logs`)
  does not carry `COSTS`.
- **Two-step fetch, like Gemini's BigQuery export** (confirmed against the
  OpenAPI spec directly, `docs/chatgpt/openapi.json` — a user-supplied
  download of the same v2.5.3 reference, not just the rendered HTML page):
  (1) **list files** — `after`/`before` are ISO 8601 timestamps bounding
  `end_time`, `limit` ≤ 100, response is `{ data: ComplianceLogFileMetadata[],
  has_more, last_end_time }`; paginate by feeding `last_end_time` back in as
  `after`. Each `ComplianceLogFileMetadata` item has `id` (pass as
  `log_file_id` to the download call), `event_type`, `end_time`, `file_name`,
  `file_size`, `file_sha256` (hex SHA-256 — worth verifying downloaded bytes
  against this before parsing). (2) **download a file** —
  `GET /compliance/organizations/{organization_id}/logs/{log_file_id}`
  responds `307` with a `Location` header holding a **short-lived signed
  URL** and no body on the redirect itself — follow it immediately, then
  parse the fetched body as JSONL. Files expire after a **30-day retention
  window** — this is a forward-sync design (`after`/`before` paging), not a
  full historical export; there's no self-service backfill beyond 30 days
  (OpenAI support can do a manual "rehydration" for `CONVERSATION_MESSAGE`
  specifically, but that offer wasn't stated to cover `COSTS`).
- **Latency:** OpenAI states 3–5 hours for `COSTS` specifically (other log
  types target a p99 <30min SLA — costs are explicitly slower). Events use
  an "at least once" contract — **de-duplicate on `event_id`** before
  aggregating.
- **Grain: hourly, per-user, per-model, per-workspace-group-combination** —
  finer than we need. Each `COSTS` record is one `(day, hour, identity,
  group-membership-combination, product, surface, client, model,
  service_tier, reasoning)` row; a user in two workspace groups can produce
  two rows for the same hour. Our extractor needs to aggregate hour→day and
  drop the per-user/group dimensions for the v1 canonical `DailySpendRecord`
  (the per-user breakdown remains available from the raw JSONL later if the
  future "per-employee spend" feature needs it — this is exactly the kind of
  case `IRawExtractionRepository` exists for).
- **Per-user:** confirmed, richer than expected — `payload.identity` carries
  `user_id`, `email`, `name`, `groups[]` (workspace group id/name), and
  optionally `agent` (Workspace Agent id/name) when the usage came from an
  agent rather than a direct chat.
- **⚠️ Dollar cost — `estimated_cost_usd` does NOT reliably appear, corrected
  2026-07-22 against a real zelleri pull:** a real row (org
  `org-QfdtNaipyr41iIOkKRw3fa7y`, 2026-07-13) had four `billing[]` entries,
  all `cost.unit = "CREDITS"` (values like `3.3408`, `6.09925`), and **none**
  carried `estimated_cost_usd` — despite the doc's claim that it's
  "populated only when the SKU has a cost in CREDITS" (i.e., exactly this
  case). So the field is real but evidently conditional on something the doc
  doesn't state (an overage-rate/contract configuration on the org, maybe,
  or a rollout that hasn't reached this tenant/plan) — **don't assume it
  will be present; treat it as opportunistic, not load-bearing.** Practical
  effect: the credit→USD conversion the manual reconstruction used to do
  (console `estimated overage` ÷ cumulative credits → per-credit rate) is
  **still needed** in the general case, same as before this spike — model it
  as a per-tenant configurable rate (fits the existing `VendorRateConfig`
  versioned-config story), and treat `estimated_cost_usd` as a bonus
  shortcut to use *when present*, falling back to the configured rate ×
  `cost.value` (in credits) otherwise. Each `billing[]` entry also has
  `sku` (free-form string, e.g. `"GPT-5.6 Sol - Cached Input"`) and
  `quantity` (`value`+`unit`, e.g. tokens) alongside `cost`. The credit
  **pool size** and **contracted overage rate** remain
  console/contract-only, unchanged from the original assessment.
- **Other real-data notes from the same pull:** `payload.product` was
  `"Work"` in the real row vs. `"chatgpt"` in the doc's illustrative example
  — confirms these are free-form strings, not a fixed enum; don't hardcode
  expected values. A file's write-time can lag its contained events by
  hours (a file named `COSTS_2026-07-14T00:44:02...` contained an event with
  `day: "2026-07-13", hour: 19`) — consistent with the documented 3–5h
  latency. File volume is high relative to the 30-day retention window (10
  files covered well under a day of activity for one small tenant) — the
  real extractor needs a proper `after`/`last_end_time` pagination loop from
  the first call, not a single fetch. `payload.identity` carries real PII
  (name, email) — fine for internal cost tracking, but worth being
  deliberate later about where it surfaces (dashboards, logs) if the future
  per-employee breakdown feature is built on top of this.
- **Seat fee is still not in this schema at all** — `COSTS` is a usage/credit
  ledger, not a subscription invoice. The flat per-seat license fee
  (Ecosync: 50 purchased / 12 active seats) still has to come from
  `VendorRateConfig`/contract terms, same as before.
- **⚠️ Confirmed 2026-07-22: there is no Billing API — the credit pool/grant/
  expiration side is genuinely absent from every documented surface, not
  just the ones checked so far.** Cross-checked two independent sources
  against a real zelleri question ("credits are added monthly, the amount
  dropped from 50,000 to 30,000, and credits appear to expire — where can we
  get this programmatically?"):
  - Exhaustively searched all 74 paths and the full text of
    `docs/chatgpt/openapi.json` for `credit_pool`, `grant`, `balance`,
    `expir*`, `top_up`, `renewal` — zero hits for anything credit-grant- or
    expiration-related. The only `grant`/`expir` hits are unrelated (access
    grants in `AUDIT_LOG` actions; token/signed-URL/file expiration).
  - Cross-checked the legacy **"Credit Usage Report" CSV** (still downloadable
    from the Admin Console UI — the same export the original 2026-07-21
    research flagged as "not removed") against a real zelleri download
    (Jan 1–Jul 17, 2026, 1,477 rows). Its columns —
    `date_partition, account_id, account_user_id, email, name, public_id,
    usage_type, usage_credits, usage_quantity, usage_units` — are the same
    underlying per-user/day/usage-type consumption ledger `COSTS` now serves
    (`usage_type` values like `codex`, `chat.completion.5.pro`,
    `chat_tool.imagegen`, `deep_research.completion`, `voice.audio.4o`, and
    the `api.gpt_5_x`/`codex_fast` model families all match what `COSTS`
    returns under different, customer-facing SKU names). **But the CSV only
    has consumption rows** — no negative values, no lump-sum rows that would
    represent a monthly credit grant. Confirms the grant/pool/expiration
    mechanics aren't exposed via *either* the old CSV path or the new API
    path — this isn't a gap specific to `COSTS`, it's not published anywhere
    OpenAI ships programmatically today.
  - Real public pricing-model change found (not zelleri-specific, but
    plausibly explains the timing of the observed grant change): **April 2,
    2026**, OpenAI moved Codex and other advanced features (Deep Research,
    Thinking models, Image Gen, Advanced Voice) to usage-based/credit
    pricing, funded from "a shared credit pool purchased at the contract
    level rather than per-seat caps" ([Flexible pricing for ChatGPT
    Enterprise plans](https://help.openai.com/en/articles/11487671-flexible-pricing-for-chatgpt-enterprise-plans)).
    Zelleri's monthly consumed-credit total (from the CSV) roughly doubled
    right after: Jan 5,648 → Feb 12,831 → Mar 12,126 → Apr 15,913 → **May
    29,830 → Jun 29,167** → Jul (17 days) 16,508 (≈30,090 if the pace holds
    for the full month). May/June landing right at ~29–30k rather than
    continuing the prior growth trend looks like a plateau-at-a-cap pattern,
    not organic leveling off — plausible evidence zelleri is already at or
    near a 30,000/month ceiling, though the specific grant number is a
    contract fact that needs confirming with the OpenAI account team, not
    something derivable from usage data alone.
  - **Practical conclusion:** don't build a pool-size lookup against any
    vendor API — there isn't one. If pool tracking is wanted, it has to be a
    manually-entered config value (see the backlog item in
    `product-design-document.md` §7), compared against the cumulative
    `COSTS`-derived credit consumption Meterist already extracts.
- **Do not confuse with** the pre-existing `/v1/organization/costs`
  developer-platform Admin API — different product (api.openai.com token
  spend, project-scoped), confirmed to NOT cover ChatGPT/Codex workspace
  credits.
- A separate **Spend Controls** surface (`https://api.chatgpt.com/v1/usage`,
  scopes `chatgpt.enterprise.usage_limit.read`/`.write`) manages/reads
  monthly hard-cap limits at workspace/group/user level — write access,
  useful for a possible future "set spend alerts from Meterist" feature, but
  out of scope for the read-only extraction this tool needs today.

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
- **⚠️ Service/SKU naming — corrected 2026-07-22 against a live test account
  (originally guessed from documentation, and the guess was wrong):**
  - The subscription and overage lines are billed under
    `service.description = "Vertex AI Search"` — **not** a service literally
    named "Gemini Enterprise." That string only appears at the SKU level:
    `"Gemini Enterprise Plus: Subscription - one month term"` (the seat fee)
    and `"Vertex AI Search: Search API Request Count - Enterprise"` (the
    overage/usage line — the "Gemini Enterprise Overage" SKU name guessed
    earlier doesn't exist; this is the real one).
  - A sibling SKU, `"Vertex AI Search and Conversation: Data Index"`, is
    **not** Enterprise-plan-specific and must be excluded — the working
    filter is `service.description = "Vertex AI Search" AND sku.description
    LIKE "%Enterprise%"` (implemented in `BigQueryGeminiBillingRepository`).
  - Plain `service.description = "Vertex AI"` (no "Search") carries raw
    Gemini model token usage (input/output/thinking tokens, grounding tool
    calls, ReasoningEngine fees) — this is pay-as-you-go API consumption, a
    **different product/billing surface** than the seat-licensed Gemini
    Enterprise product, structurally closer to what this project calls
    "Claude API Platform" than "Gemini Enterprise." Deliberately excluded
    from this adapter, not a gap.
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
