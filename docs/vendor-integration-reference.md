# Vendor Integration Reference

**Status:** Living document — expect edits as vendor APIs evolve. Three of the
four v1 vendor APIs are beta or shipped within the last ~2 months as of this
writing; treat schemas and endpoint details as subject to change, not final.

**Last verified:** 2026-07-21 for the per-vendor sections below, except where a
dated note says otherwise. The November 2026 update immediately below was
researched and live-tested on 2026-11-19 and supersedes any older statement
in this document that it contradicts.

## Purpose

This is the single source of truth for how Meterist talks to each vendor —
endpoints, auth, granularity, and known gaps. [`README.md`](../README.md) and
[`CLAUDE.md`](../CLAUDE.md) carry a summary; this document is where sprint
tickets for a specific vendor adapter should point for implementation detail.

---

## November 2026 update (researched and live-tested 2026-11-19)

All of this came from documentation plus read-only live calls against two real
tenants. Anything marked **unverified** rests on documentation alone.

**What changed, at a glance**

| Item | Status | Date | Consequence for Meterist |
|---|---|---|---|
| Anthropic Usage and Cost Analytics API (`/v1/organizations/analytics/usage` and `/cost`) | New, beta (`anthropic-beta: analytics-usage-cost-2026-08-24`) | 2026-10-29 | Per-user requests, tokens and cost, for Enterprise and Console, grouped by up to five dimensions. Same Analytics key (Enterprise) / Admin key (Console) already stored |
| Claude Enterprise `user_cost_report` and Claude API Platform `cost_report` | Reclassified "legacy but still supported", no sunset date announced | 2026-10-29 | No forced migration. New dimensions land only on the new endpoints; plan a migration (Sprint 3) |
| "Claude API Platform has no per-human-user concept" | Out of date where personal or service-account keys are used | 2026-08-27 | Attribution is per person for those keys; workspace keys still attribute only to the key |
| ChatGPT Unified Daily Usage API | GA | 2026-09-17 | Implemented as the ChatGPT source of record (see the ChatGPT section) |
| ChatGPT admin API reference | Public OpenAPI spec (`chatgpt.com/public/admin/api-reference/openapi.json`, "OpenAI Programmatic Admin Platform"), versioned, roughly 40 revisions since July | by 2026-11-18 | The reference is no longer login-gated; the spec carries a changelog worth polling |
| ChatGPT `COSTS` billing lines | `cost.unit` can be `CREDITS` or `USD`; never add the two | documented from 2026-07-16 | The normalizer already applies the credit rate only to `CREDITS` lines. Every stored line for both tenants is `CREDITS` |
| ChatGPT Codex per-user daily usage endpoint | Deprecated, retires 2026-12-15 | 2026-12-15 | Meterist does not call it |
| Gemini Enterprise quota and pay-as-you-go model | Per-feature daily quotas pooled per edition, overage pay-as-you-go; customers moved to new overage billing controls | from 2026-08-17 | See the SKU-filter finding in the Gemini section |
| Gemini Code Assist bundled with Gemini Enterprise Standard | Ends for existing subscriptions | 2027-03-31 | Expect seat-value changes in affected tenants |

**No vendor exposes contract-level balances.** Anthropic documents no
organization credit balance, prepaid commitment or invoice endpoint; OpenAI's
spec contains no credit pool size, grant, balance or expiration; Google
exposes no per-user quota consumption or per-user overage dollars. Pool and
grant tracking stays a manually entered configuration (see the backlog).

**MCP adds no data.** Every MCP server inspected wraps an API Meterist already
reaches, with tighter limits. **OpenTelemetry** is the only channel carrying
data the admin APIs lack (per-request events, active time, tool decisions),
but it is push-only, estimate-grade, and limited to agent and CLI surfaces;
not adopted.

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
- **Newer endpoints (2026-10-29 research, not yet used by the adapter):**
  - **Legacy designation.** `user_cost_report` is now "legacy but still
    supported" with no sunset date. Its replacement is `GET
    /v1/organizations/analytics/cost` (and `/usage` for tokens plus a
    `requests` count), which accepts up to five `group_by[]` dimensions
    (`actor`, `product`, `model`, `service_tier`, `rbac_group_id`,
    `session_id`, `workspace_id`, `api_key_id`, ...). Same 31-day span cap,
    2026-01-01 floor, 365-day lookback and cents-denominated amounts, but
    `time_bucket_width` has no default (`ending_at` becomes mandatory once it
    is set) and **buckets with no usage produce no rows**, so a client must
    zero-fill. The legacy reports silently drop groups past the top 100 per
    bucket; the new API returns every group, so its totals can run higher.
    Freshness is slower: typically within 4 hours, up to 24.
  - **Engagement endpoints** (explicitly not legacy):
    `/analytics/summaries` (daily/weekly/monthly active users,
    `assigned_seat_count`, pending invites, adoption rates; up to 366 days)
    and `/analytics/users` (per-user activity by product: message counts,
    Claude Code sessions, commits, pull requests, Cowork and Office metrics,
    `last_activity_date`). Next-day snapshot, around 13:00 UTC.
    `assigned_seat_count` gives seat-count drift detection a vendor-reported
    input.
  - **Roster:** the Admin API user-management endpoints (out of beta
    2026-08-19) return members, invites and RBAC groups. Joined to
    `last_activity_date` they identify idle seats.
  - **Spend Limits API** (needs usage credits enabled and a key with
    `read:spend_limits`): `GET /v1/organizations/spend_limits/effective`
    returns each member's effective monthly limit, where it comes from, and
    `period_to_date_spend`. Anthropic calls that figure informational (it can
    read `"0"` when unavailable), so treat it as a headroom signal, not a
    ledger.
  - **Unverified:** whether `/analytics/usage` on a seat-based plan includes
    seat-covered usage or only usage-credit usage (Anthropic says cost and
    usage endpoints on seat-based plans "reflect usage credits only"), and
    whether one key can carry `read:analytics`, `read:members` and
    `read:spend_limits` together.

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
- **Per-user:** *corrected 2026-11-19.* The original finding here, that there
  is no per-human concept at all, is out of date. Since 2026-08-27 personal
  keys and service-account keys act as a user or service account, and the new
  `/analytics/*` endpoints group Console usage by `actor`. Legacy workspace
  keys still attribute only to the key (it appears as both `actor` and
  `api_key_id`). So per-person attribution exists exactly where a tenant has
  adopted personal keys or Console-authenticated Claude Code; otherwise it
  would still need a 1:1 API-key-to-person convention. The adapter does not
  read any of this yet (it uses `cost_report`, now designated legacy).
- **Legacy designation:** `/v1/organizations/cost_report` was reclassified
  "legacy but still supported" on 2026-10-29, no sunset date. It keeps roughly
  5-minute freshness; the new `/analytics/cost` is the migration target.
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

- **Daily Usage API — the source of record, IMPLEMENTED (2026-11).** `GET
  https://api.chatgpt.com/v1/analytics/workspaces/{workspace_id}/usage`,
  the Unified Daily Usage API (GA 2026-09-17), queried with
  `group=workspace`, `start_time`/`end_time` as Unix seconds, and
  `limit=1000` (paginated by `next_page`). It needs the tenant's workspace
  ID (stored as the optional `WorkspaceId` in the ChatGPT credential) and an
  Admin key with **Enterprise analytics API: Read** (scope
  `enterprise.analytics.usage.read`). A key used against another tenant's
  workspace returns HTTP 403. Each row is one user on one day: `start_time`,
  `end_time`, `user_id`, `actor`, `totals` (`chats`, `messages`, `credits`,
  `cost_usd`, `estimated_cost_usd`, token counts), and `clients[]` /
  `models[]` breakdowns. A row exists only for a user-day with usage, so
  active users are derived by counting distinct users.
  - **Why it is the source of record:** it is a query API, not an expiring
    file export, so it sidesteps the 29-day `COSTS` retention that lost data
    (see the retention notes below). In live tests its history reached back to
    April and May 2026 for the two tenants, months beyond `COSTS`, which let
    the days lost to the overwrite bug and to a missed-run gap be recovered.
    Its exact history depth is still undocumented (issue #10).
  - **Money semantics:** `credits` is credit-priced usage; `cost_usd` is
    natively USD-priced usage (`null` on every row for credit-billed
    workspaces, which both tenants are); `estimated_cost_usd` estimates the
    credit-priced portion at the workspace's current overage rate. A
    contracted rate still comes from Meterist's rate configuration.
  - **How the adapter uses it:** with a `WorkspaceId`, `ChatGptEnterpriseSpendExtractor`
    uses the Daily Usage API for the usage figures and the `COSTS` export as a
    best-effort cross-check; a Daily Usage failure fails the extraction loudly
    rather than falling back silently. Without a `WorkspaceId` it falls back to
    `COSTS` alone. Each stored day records which source supplied it
    (`DailySpendRecords.Source`).
  - **Known differences to settle (issue #10):** `COSTS` counts slightly more
    than Daily Usage on some days (under half a percent in total for one
    tenant, mostly Mondays); and one tenant's May credits from the API
    disagree substantially with the console CSV downloaded in July, so treat
    anything before June as unconfirmed.
  - **Not yet stored:** the per-user rows are not persisted yet; the adapter
    keeps the daily total. Per-user storage is Sprint 2.
- **Other documented surfaces (2026-11-19 research, not used yet):** the
  **Spend Controls** API under `/v1/manage/workspaces/{workspace_id}/usage_limits/`
  (scope `chatgpt.enterprise.usage_limit.read`) lists every user with
  `current_month_usage`, `last_month_usage`, the effective monthly limit and
  its source, and `seat_type`; it is month-to-date only and has no pool size or
  balance. `GET /v1/manage/workspaces/{workspace_id}/users` (added 2026-08-21)
  returns active members with `role` and `seat_type`, but **no last-active
  field**, so idle seats are computed as roster minus users with Daily Usage
  rows. Other Compliance Logs event types (`CODEX_TURN`, `CONVERSATION_MESSAGE`,
  and others) exist; `CONVERSATION_MESSAGE` exposes message content and is not
  needed for spend. Availability of each endpoint is per workspace and plan,
  so confirm per tenant.
- **⚠️ Schema spiked 2026-07-22 against the real, authenticated OpenAI
  Programmatic Admin Platform reference** (`chatgpt.com/admin/api-reference`,
  v2.5.3 as of that date — it was login-gated then, which is why it wasn't
  visible in the 2026-07-21 research pass; **as of 2026-11-18 the same
  reference is public as an OpenAPI spec**, see the November 2026 update). The
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
  - **⚠️ Confirmed 2026-08-21, fixed 2026-08-21: this retention window
    actively destroyed already-correct historical data on re-extraction —
    it doesn't just limit how far back a *first* pull can reach.** The
    window is 29 days from whenever extraction actually *runs*, not from
    the requested period start. `ExtractAllThroughToday.ps1` always
    re-pulls from a fixed `2026-07-01`, and extraction upserts by
    `(TenantId, VendorId, Date)` — so a day that was correctly captured in
    an earlier pull got **silently overwritten with `$0`** once it aged
    past 29 days old, because the vendor legitimately returns nothing for
    it and nothing distinguished "vendor confirms zero" from "vendor can no
    longer tell us." One pull erased ~$2,374 of real usage across two
    tenants this way before it was caught by manually diffing against the
    prior report — `RawDailyExtractionRecords` is also latest-pull-only
    (see `docs/database-schema.md`), so there was no backup copy anywhere
    in the database. **Fix:** `IChatGptCostLogRepository.QueryCostRowsAsync`
    now returns a `ChatGptCostQueryResult` carrying the *actual* effective
    start date the query used (already computed internally for the clamp,
    previously discarded before reaching the caller) alongside the rows.
    `ChatGptEnterpriseSpendExtractor` gap-fills only from that
    `EffectiveStart` forward — a day older than it never gets a key in
    `RecordsByDate` at all, so it never reaches the upsert and any existing
    row for it is left completely untouched. Note this fix stops *further*
    damage; it doesn't resurrect data already overwritten by pulls before
    2026-08-21 — those days' real figures survive only in the report
    history/chat transcript, not the live database.
  - **⚠️ New, found 2026-08-29, unresolved: a subtler erosion pattern
    survives the fix.** A week still nominally *inside* the 29-day window
    (not yet clamped away entirely) can lose usage across successive pulls,
    monotonically — never gains it back. zelleri's Jul 22–28 week read
    $337.43 usage (pull of 2026-08-05) → $325.35 (2026-08-20) → $319.97
    (2026-08-29), each pull strictly lower than the last, while the fix
    above was already deployed and active for the later two pulls. This is
    consistent with OpenAI trimming a day's rows before the full 29 days
    are up (event-level or file-level retention, not a clean day boundary),
    not with the `EffectiveStart` fix failing — the fix only prevents the
    complete overwrite-with-`$0` case once a day is *fully* outside the
    window; it does nothing to stop a day still nominally inside the
    window from returning fewer real rows on a later pull than an earlier
    one did. Until this is addressed, the reconciliation report keeps using
    the *earliest* captured figure for each ChatGPT week as the most
    complete one on record, on the empirically-observed rule that these
    figures only ever shrink pull-over-pull, never grow. A possible future
    fix: extend the extractor to never let a re-pull's `UsageOrOverage`
    replace an existing `DailySpendRecord` with a *lower* value — only ever
    increase it — as a second safeguard layered on top of the
    `EffectiveStart` gap-fill.
    **Resolved 2026-11:** the never-lower rule was implemented (with a
    0.005 noise tolerance), and the Daily Usage API replaced `COSTS` as the
    source of record, so the erosion no longer reaches the stored figures.
    A multi-week extraction gap longer than the window still loses `COSTS`
    data, which is what `audit coverage` and the scheduled daily run exist to
    prevent.
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
- **⚠️ Dollar cost — `estimated_cost_usd` did NOT reliably appear in July,
  corrected 2026-07-22 against a real pull. *Superseded 2026-11-19: the field
  is now populated on every credit line since August, and its implied rate
  exactly matched the configured credit-to-USD rate for both tenants, so the
  configured rate is now only a fallback.* The normalizer still prefers the
  vendor estimate when present and applies the configured rate only to
  `CREDITS` lines. The July observation:** a real row (2026-07-13) had four `billing[]` entries,
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
  - **Addendum, 2026-08-05:** weekly reconciliation-report work surfaced
    further, still-**unconfirmed** observational evidence of a recurring
    monthly grant for both tenants: possibly ~day 30 / ~25,000 credits for
    ecosync, and ~day 14 / ~30,000 credits for zelleri. Neither the exact
    day, the exact amount, nor whether this genuinely differs by tenant is
    confirmed — there simply isn't enough billing history yet to be sure,
    and (per the conclusion above) no API can confirm it directly either
    way. Recorded here so the next session building the credit-grant
    manual-config backlog item doesn't have to re-derive this from the
    report or chat history; treat both figures as a starting point to
    refine once each account team confirms the real contract terms.
- **Do not confuse with** the pre-existing `/v1/organization/costs`
  developer-platform Admin API — different product (api.openai.com token
  spend, project-scoped), confirmed to NOT cover ChatGPT/Codex workspace
  credits.
- A separate **Spend Controls** surface (scopes
  `chatgpt.enterprise.usage_limit.read`/`.write`) manages and reads monthly
  hard-cap limits at workspace/group/user level. The read side is described
  under "Other documented surfaces" above (the path given in the July notes,
  `/v1/usage`, was superseded by `/v1/manage/workspaces/{workspace_id}/usage_limits/`).
  Write access would suit a possible future "set spend alerts from Meterist"
  feature but is out of scope for the read-only extraction this tool needs.

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
- **Billing model change (2026-08-17) and SKU-filter finding (live-tested
  2026-11-19).** Gemini Enterprise now runs on per-feature daily quotas pooled
  per edition (for example 160 Assistant queries per user per day on
  Standard, 200 on Plus), with usage above the pool billed pay-as-you-go,
  Claude models billed only pay-as-you-go, a no-license pay-as-you-go edition
  (GA 2026-08-01), monthly spend limits, and prepaid credits for self-serve
  accounts (GA 2026-11-05). A `SELECT DISTINCT service.description,
  sku.description` over each tenant's export since 2026-07-01 found: the
  **only Gemini Enterprise charge for either tenant is the subscription SKU**,
  which the current filter captures, and no overage SKU has posted since
  July. One tenant has `Vertex AI | Agent Platform Compute` and `Agent
  Platform Memory` rows since 2026-09-11 (the Agent Gateway / Memory Bank
  family) at a negligible total, which sit **outside** the filter. The export
  also contains `Invoice | Tax` for both tenants and `Support | ...` fees for
  one, which Meterist leaves out by design, so Meterist totals will not match
  the Google invoice until that is decided (issue #35). Above-quota and
  Claude-model SKUs have never been observed. **Implemented (issue #34):** the
  included service/SKU rules are now configuration (Meterist:Gemini:BillingFilter),
  and every extraction warns about non-zero Gemini-looking lines the rules leave
  out. Decision: Agent Platform compute and memory are pay-as-you-go consumption,
  not part of the seat product, so they are **not** counted by default (they are
  watched, and can be counted by adding an Include rule). A live run found only
  those Agent Platform lines plus a day of raw Gemini model token usage (also
  ignored by default) outside the filter. See the user guide.
- **Newer per-user and licence paths (documented 2026-07 to 2026-11, not
  used yet):**
  - A Cloud Logging **log sink** to BigQuery (Google blog, 2026-07-15) lands
    the `discoveryengine.googleapis.com` user-activity log with the user
    identity in `useriamprincipal`; it is the realistic source of per-user
    request counts. Its column schema is **unverified**, and setting it up
    creates a sink in the client's cloud project, which needs the client's
    agreement (issue #36).
  - `analytics:exportMetrics` (v1alpha) writes the last 30 days of aggregate
    adoption and seat metrics to a BigQuery table; quota is 5 calls per day
    per project and each export covers a rolling 30 days, so it must be called
    daily and accumulated, the same hazard as `COSTS`.
  - `userStores.userLicenses.list` (GA v1) enumerates per-user licence records
    filterable by `licenseAssignmentState` (`ASSIGNED`, `NO_LICENSE`,
    `NO_LICENSE_ATTEMPTED_LOGIN`); joined to the activity table it yields
    licensed-but-inactive users and an unmet-demand signal. Whether the
    object carries a last-login field is unverified.
  - Per-user Gemini *dollars* remain unavailable from any documented source:
    no billing export schema has a user column, and per-user quota counters
    are internal.
- **New SKU lines** confirmed on Google's own pricing page: **Agent
  Gateway** billing started **Jul 13, 2026** ($0.085/vCPU-hour, billed as
  Agent Compute); **Memory Bank/Sessions** billing starts **Sep 1, 2026**
  (Agent Storage $0.30/GiB-month + read/write compute).

---

## Cross-Vendor Overage & Per-User Matrix

| Vendor | Overage extractable? | Per-user usage? | Per-user $ cost? |
|---|---|---|---|
| Claude Enterprise (seat-based) | Yes, but only if tenant has "usage credits" enabled — otherwise no overage concept exists | Yes | Yes (native) |
| Claude API Platform | N/A — no seat/overage concept, it's all usage-priced | Only where personal or service-account keys are used (otherwise workspace/API-key granularity) | Same: per actor via the new analytics endpoints, per key otherwise |
| ChatGPT Enterprise | Partial — credits via the Daily Usage API; pool size/overage rate still console/contract-only | Yes (Daily Usage API, per user per day) | Credits natively, dollars via the vendor estimate or the configured rate |
| Gemini Enterprise | Yes, at aggregate SKU level via BigQuery | Yes (separate telemetry path, permission-gated) | No — derived/allocated only |

**Implication:** per-employee spend comparison is cleanly supportable today
for Claude Enterprise and ChatGPT Enterprise, partially for Gemini (usage
counts real, cost estimated), and not meaningful for Claude API Platform.
See the Product Design Document's Future Features section for how this
should be sequenced. *Updated 2026-11-19:* per-user data is now documented for
Claude (both products, via the analytics endpoints) and ChatGPT (Daily Usage);
none of it is stored per user yet (Sprint 2).

## Per-Tenant Credential Types Required (v1 vendors)

| Vendor | Credential type | Who provisions it | Notes |
|---|---|---|---|
| Claude Enterprise | Analytics API key | Org primary owner only | `read:analytics` scope. Roster and spend-limit reads would need `read:members` / `read:spend_limits` (unverified whether one key can hold all three) |
| Claude API Platform | Admin API key | Org admin | Separate key from Analytics API |
| ChatGPT Enterprise | Workspace Admin key plus the organization ID and, for the Daily Usage API, the workspace ID | Workspace owner/admin | Global Admin Console → Credentials. Scopes: Compliance logging platform → Costs (Read), and Enterprise analytics API (Read) for Daily Usage |
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
