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

### Claude Enterprise (seat-based — Chat/Code/Cowork) — IMPLEMENTED (2026-07-22), MIGRATED to `/analytics/cost` (2026-11-20)

- **Current source (issue #24).** `HttpClaudeAnalyticsCostRepository` reads
  `GET /v1/organizations/analytics/cost` with `anthropic-beta: analytics-usage-cost-2026-08-24`,
  `time_bucket_width=1d`, `group_by[]=actor&group_by[]=product`, an explicit
  `ending_at`, 31-day windows and cursor pagination. It requests **every actor**,
  not only people, so the daily total counts all cost; each row keeps the actor
  type (`user_actor`, `service_account_actor`, `api_actor`, `federated_actor`).
  Rows carry `product`, `requests` (from `cost.requests`, which the spike found
  equal to `/usage` `requests`, so no second API call) and the actor's
  `email_address` and `name`. The legacy `user_cost_report` client remains behind
  `Meterist:ClaudeEnterprise:UseLegacyCostReport`, off by default; a 404 from the
  new endpoint says so and names the setting. Pagination fails loudly on a blank or
  repeated cursor in both clients rather than returning a partial month.
- **Verified end to end 2026-11-20:** a dry run over 50 days on both tenants,
  against figures first stored from the legacy report, reported every
  previously stored day unchanged (49 of 49 on each) plus the one new day; the
  legacy fallback returned the same. Row counts differ by design (the new
  grouping adds product), the totals do not.
- **Per-user storage (issue #18).** `ClaudeEnterpriseUserUsageNormalizer` turns the
  raw rows into one `UserDailyUsageRecord` per actor per day, summing dollars and
  requests across products. Dollars are **vendor-reported** (the API's own per-actor
  cost, after discounts and before credits), so rows are not estimated, unlike
  ChatGPT's credit-derived figures. `Credits` is empty.
  - **Seat-based plans:** cost is usage credits only, so a busy user can cost
    nothing. Rows with a zero amount and a request count are kept: that is an
    active user.
  - **Attribution by actor type:** `user_actor` is a `Person`;
    `service_account_actor` and `api_actor` are `ApiKey`; anything else
    (including `federated_actor`, a cloud identity not known to be a person, or an
    unknown type) is `Unrecognized` and named in a warning by type, never by person.
  - **Cost with no actor** (usage the API could not attribute) is kept in one
    synthetic bucket with user id `unattributed`, always `Unrecognized`, so per-user
    figures add up to the daily total. It is never a person; person-level views
    must filter on `Attribution`. One tenant showed a single such actor-day in the
    first 19 days of November.
  - **Older raw rows:** rows stored before the actor type was kept (from the legacy
    per-user report) have no `ActorType` key at all and are treated as people when
    they carry a user id; a row that has the key but no value is `Unrecognized`.
    They carry no request count.
  - **Backfill:** `meterist backfill user-usage` rebuilds these rows from the raw
    records already stored, with no API call (see the user guide).
  - The stored daily record is unchanged. `Source` is not set for this vendor
    (both endpoints agree, so there is nothing to disclose).
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
  - **ChatGPT Spend Controls `usage_limits/users` (issue #29, 2026-11-20, read-only, both tenants, the existing admin key and workspace).**
    - **The stored admin key already works** (it carries `chatgpt.enterprise.usage_limit.read`); no second credential. The
      write endpoints are deliberately not used.
    - **Shape:** `object`, `data`, `has_more`, `cursor`, `last_id`. A row has `id`, `account_user_id`, `name`, `email`, `role`
      (`account-owner`, `account-admin`, `analytics-viewer`, `standard-user`), `seat_type` (`default`, `automation`, or null),
      `is_scim_managed`, `last_month_usage` (**null on both tenants**), `current_month_usage` plus
      `current_month_usage_amount {amount, unit}`, `override_monthly_usage_limit` (null even for members whose limit comes from
      an override), `inherited_monthly_usage_limit` (null) and `effective_monthly_usage_limit`.
    - **`effective_monthly_usage_limit` is null for no limit** (all 29 members on one tenant, 16 of 33 on the other); otherwise
      `{limit: {limit, type}, source: {kind, ...}}` with `type` `limited` seen (`unlimited` and a `limit_amount {amount, unit}` are
      documented but did not appear) and `source.kind` `group_default` (with `group_id` and `group_name`) or `individual_override`
      (no other fields). Note the nesting: the number is `effective_monthly_usage_limit.limit.limit`.
    - **Units: credits** on both tenants (`credit`, fractional usage such as 298.0161). Amounts are not converted to dollars; a
      USD-billed workspace reports `usd`.
    - **Pagination:** `limit` up to 500 (both tenants fit in one page), and the response's opaque 88-character `cursor` goes
      back as `cursor` (it is not `last_id`). Paging 5 at a time reproduced exactly the full list on both tenants.
    - **How Meterist uses it:** `HttpChatGptSpendControlsRepository` follows `has_more` and `cursor` with blank and repeated-cursor
      guards, marks its requests sensitive and registers its client without factory request logging (the cursor may encode a
      member). A null limit or `unlimited` is stored as no limit, a limit it cannot read fails the snapshot rather than being stored
      as no limit, and missing usage is stored as missing, never zero.
  - **Live test of `/analytics/users` (issue #23, 2026-11-21, read-only, both tenants, the existing Analytics key).**
    - **No beta header** is needed; the same key works.
    - **Two modes:** `date=` for one day, or `starting_date` and `ending_date` (exclusive end) for a rollup over a range, max 366
      days, not before 2026-01-01. **The rollup is exactly the sum of the single days:** for seven days, every user's chat message
      total from seven single-day calls equalled the rollup, and no user appeared in only one. So single days lose nothing.
    - **Rows exist only for people active in the period**: 5 and 6 rows for single days on one tenant, 10 users in a week and 12
      over the whole year there, 23 and 37 on the other. A response is `data` and `next_page` (`limit` up to 1000, one page was
      enough). A whole year as a rollup was about 41 KB and 128 KB.
    - **Row shape:** `user` (`type` `user`, `id`, `email_address`) beside nested per-product metrics, about 110 integer leaves:
      `chat_metrics`, `claude_code_metrics` (`core_metrics` with sessions, commits, pull requests and `lines_of_code`, and
      `tool_actions` accepted and rejected counts), `office_metrics` (`excel`, `outlook`, `powerpoint`, `word`),
      `cowork_metrics`, `design_metrics`, `science_metrics`, `chat_cowork_unified_metrics` (`chat` and `sessions`),
      `claude_tag_metrics`, `web_search_count`, `last_activity_date`, and `distinct_user_count` (null per person). A product the
      person did not touch is an object of zeros or a null, so a missing count is zero activity. `user.id` is the same id as the
      roster member id.
    - **Dates:** the newest day is yesterday (UTC); a later `date` or `ending_date` is a 400 naming it ("Latest available data
      for this query is 2026-11-20"), and a date before 2026-01-01 is a 400 ("Data is only available from 2026-01-01 onwards").
    - **The cursor is personal data.** `next_page` is base64 of a small JSON object (`last_email`, `date`, `desc`, `qh`), so it
      carries a member's **email address**. The cursors of `/spend_limits/effective` and `/analytics/cost` are opaque. Meterist
      therefore registers this client without the HTTP client factory's request logging, marks every request sensitive so
      OpenTelemetry drops it from traces, and never logs a URL.
    - **How Meterist uses it:** single-day mode, only for days the planner picks (see the user guide), the headline counts as
      columns and the whole row as JSON; a day the API does not have yet is skipped and left alone.
  - **Live test of `/spend_limits/effective` (issue #26, 2026-11-20, read-only, both tenants, the existing Analytics
    key).**
    - **One key holds all three scopes.** The Analytics key Meterist already stores for each tenant is accepted by this
      endpoint, as it is by `/analytics/*` and `/users`, so one key carries `read:analytics`, `read:members` and
      `read:spend_limits` together and no second credential is needed.
    - **No beta header** is needed (the `spend-limit-reads-2026-09-26` value exists but is not required).
    - **Works on both tenants**, including the seat-based one (where most members have no limit), so it is not limited to
      tenants that spend on usage credits in practice; the documented prerequisite is that usage credits are turned on.
    - **Shape:** `data` plus `next_page` (opaque cursor, sent back as `page`). A row has `scope` (`type` `user`, `user_id`),
      `actor` (`type` `user_actor`, `user_id`, `name`, `email_address`, `deleted`), `amount`, `currency` (`USD`), `period`
      (`monthly`), `source`, `spend_limit_id` and `period_to_date_spend`. 13 and 42 rows came back in one page each.
    - **Units: `amount` and `period_to_date_spend` are decimal strings in cents**, like `/analytics/cost`. For one tenant the
      sum of members' `period_to_date_spend` equalled the cost report's month-to-date total in cents to within a tenth of a cent;
      for the other it was about 8% higher, so the figure is the vendor's running number and not a ledger.
    - **A null `amount` means no limit.** 9 of 13 members on the seat-based tenant, 2 of 42 on the other.
    - **`source`** is `{type: seat_tier, seat_tier}`, `{type: rbac_group, rbac_group_id}` or `{type: user, user_id}` (the
      documented `organization` default did not appear). Meterist stores the type and the tier or group id.
  - **Live test of `/analytics/summaries` (issue #22, 2026-11-20, read-only, both tenants, the existing Analytics
    key).**
    - **No beta header** is needed (unlike `/usage` and `/cost`); the same key works.
    - **Shape:** `data` holds one row per day (the same rows are also under `summaries`), plus `next_page`. A row has
      `starting_at`, `ending_at`, `daily/weekly/monthly_active_user_count`, `assigned_seat_count`,
      `pending_invite_count`, `daily/weekly/monthly_adoption_rate` (a percentage, `30` means 30%), and per-product
      counts named `{product}_{daily|weekly|monthly}_active_user_count` for `chat`, `claude_code`, `cowork`,
      `chat_cowork_unified`, `office_agent`, `claude_design`, `science` (plus `science_entitled_user_count`) and
      `claude_tag`. A product the organisation lacks comes back as `null`, not `0` (`claude_tag` did on both tenants).
    - **Dates:** `starting_date` is inclusive and `ending_date` **exclusive**; `ending_date` is required (a query with
      only a start is a 400, "starting_date must be before ending_date"). Data starts **2026-01-01** (earlier is a 400,
      "Data is only available from 2026-01-01 onwards"). The newest day available is **yesterday (UTC)**; an
      `ending_date` beyond today is a 400 naming the latest day ("Latest available data for this query is
      2026-11-19"). A whole year (322 days) came back in one request, about 840 KB, with no pagination
      (`next_page` empty); the documented span cap is 366 days.
    - **Seat history:** `assigned_seat_count` is per day, so seat changes over time are recoverable from it. One tenant
      showed 0 seats on 2026-01-01 and 42 on 2026-11-18, the other 10 and 11, so it also shows when a tenant began.
    - **How Meterist uses it:** `HttpClaudeActivitySummaryRepository` asks for at most 366 days per request, clamps the
      start to 2026-01-01 and the end to yesterday, and if the API still says its latest day is earlier (before the day's
      snapshot is published) asks once more for just that. Every numeric field is kept by its API name and the products
      are found by the `{product}_{period}_active_user_count` pattern, so a new product is stored without a code change.
      Rate limit: one request per window, far inside the shared 60 per minute.
  - **Live test of `/analytics/usage` and `/analytics/cost` (spike #21, 2026-11-20,
    read-only, both tenants, one Analytics key each). Verdict: GO for migrating
    the Claude Enterprise adapter (#24).**
    - **Beta header and key:** the existing Analytics key (`read:analytics`) works
      with `anthropic-beta: analytics-usage-cost-2026-08-24`. Without the header
      the same URL returns **404 not_found_error**, so the header is mandatory,
      not an opt-in nicety. `anthropic-version: 2023-06-01` is still sent.
    - **Totals match the legacy `user_cost_report` exactly.** For a full closed
      month (October) on both tenants, the new `/cost` per day equalled the
      legacy report per day with a maximum difference of 0, both for the whole org
      and grouped by `actor` (same row counts, same distinct users). Neither tenant
      has enough users per bucket to hit the legacy top-100 truncation, so that
      documented difference was not exercised.
    - **Seat-based plan: usage counts seat-covered activity, cost does not.** One
      tenant is seat-based: in October its cost was zero on 30 of 31 days, yet
      `requests` was in the hundreds to thousands every day. So `/usage`
      `requests` (and `cost.requests`, which equals it) measure activity
      including seat-included use, while `amount` reflects usage credits only,
      exactly as Anthropic's docs say for cost. The other tenant (usage credits)
      had non-zero cost every day. Consequence: per-user **cost** on a seat plan is
      near zero by design and is not a measure of use; per-user **requests** are the
      activity signal. Reports must not read "$0" as "inactive".
    - **`ending_at` is required with `time_bucket_width`:** confirmed, HTTP 400
      ("`ending_at` is required when `time_bucket_width` is set so pagination stays
      consistent across pages").
    - **Empty buckets return no row:** confirmed. A future week returned status
      200 with zero rows, and a 12-day window reaching into tomorrow returned 11
      rows. The client must zero-fill (the existing daily path already does).
    - **Earliest date:** a window starting before 2026-01-01 is a 400 naming
      2026-01-01T00:00:00Z as the earliest.
    - **Freshness:** the response `data_refreshed_at` was about 35 minutes old at
      the time of the call and the current (partial) day already had rows. Anthropic
      documents 4 to 24 hours; that was not observed in this one sample, so keep
      treating the latest day as incomplete. A month's figures can still be revised
      until about 7 days after it ends.
    - **Row shape (confirmed):** `starting_at`, `ending_at`, `group`, and `cost`
      (`currency`, `amount`, `list_amount`, `requests`) or `usage` (`uncached_input_tokens`,
      `cache_creation`, `cache_read_input_tokens`, `output_tokens`, `total_tokens`,
      `server_tool_use`, `requests`). With `group_by[]=actor` the actor carries
      `type`, `user_id`, `email_address`, `name` and `deleted`, so the new endpoint
      has the identity (name and email) that per-user reports need. Amounts are
      cents as six-decimal strings, as before.
    - **Differences the migration must handle:** parameter `time_bucket_width` (not
      `bucket_width`), no default bucket width, `group_by[]` and `actor_types[]` as
      repeated bracketed parameters, the mandatory beta header, and the
      `email_address` field name (the legacy report used `email`). The beta label
      means the schema can still move, so keep the legacy endpoint as a fallback
      and cross-check rather than removing it.
    - **Key scopes, partly settled (2026-11-20, issue #30):** the Analytics key already stored for each tenant is
      **accepted by the member list** (`GET /v1/organizations/users`, which needs `read:members` or `read:org_audit`)
      on both tenants, so it carries more than `read:analytics` and no second credential is needed for the roster.
      Whether it also carries `read:spend_limits` (issue #26) is still untested.
  - **Member roster (issue #30, 2026-11-20).** `HttpClaudeMemberRosterRepository` reads `GET /v1/organizations/users`
    (`limit=1000`, cursor `after_id` taken from `last_id` while `has_more`) with the stored key and
    `anthropic-version: 2023-06-01`. A member is `id`, `type`, `email`, `name`, `role` and `added_at`; there is no
    seat type or last-activity field (last activity comes from `/analytics/users`). Roles seen live: `user`,
    `managed`, `owner`, `membership_admin` and `primary_owner`. Name and email were present on every member of both
    tenants, and ids were unique. Pagination fails loudly on a blank or repeated cursor, a member with no id, or
    running out of pages, because a partial list stored as a snapshot would read as people having left; an empty
    list is also refused (an organisation always has an owner). Stored as dated snapshots by
    `meterist roster snapshot` (see the user guide).

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
  read any of this yet (it uses `cost_report`, now designated legacy). Superseded 2026-11-21 (#25): see below.
- **Migrated to `/analytics/cost` (issue #25, 2026-11-21).** `HttpClaudeApiAnalyticsCostRepository` reads
  `GET /v1/organizations/analytics/cost` with the **same Admin key** (`x-api-key`), `anthropic-version: 2023-06-01` and
  `anthropic-beta: analytics-usage-cost-2026-08-24`, `time_bucket_width=1d`, an explicit exclusive `ending_at`, 31-day
  windows, `limit=1000` and `group_by[]=actor, api_key_id, workspace_id, product` (four of the five allowed), with no
  `actor_types[]` filter so the daily total counts everything. The legacy client stays behind
  `Meterist:ClaudeApiPlatform:UseLegacyCostReport`; a 404 names it. Built from Anthropic's published reference.
  - **Reconciled live 2026-11-21 (October 2026, `extract --dry-run`, against figures first stored from the legacy
    `cost_report`).** One tenant: 14 stored days totalling $10,090.52; the new endpoint returned 14 day-rows and the dry run
    reported all 14 unchanged, with one per-actor row per day to be inserted. The other tenant had no stored October days
    and the new endpoint returned none, consistent with no API usage that month. Only one tenant had data, so this is one
    month and one account, not a broad test; a month with several actors, personal keys or a workspace key would exercise
    more of the attribution.
  - **Documented behaviour the adapter relies on:** the Admin key (or an `org:admin` OAuth token) authenticates both
    analytics endpoints; `amount` is a six-decimal string in **cents** (post-discount, pre-credit); actors are `user_actor`
    (`user_id`, `email_address`, `name`, `deleted`), `service_account_actor` (`service_account_id`), `api_actor`
    (`api_key_id`) and `federated_actor` (`provider`, `subject`); a personal or service-account key reports its person or
    service account as the actor and the key in `api_key_id`, while a legacy workspace key is linked to no identity so the
    key appears as both `actor` and `api_key_id`; `actor` is null for usage that cannot be attributed; combinations with no
    usage return no row; `starting_at` must be on or after 2026-01-01 and within 365 days; data refreshes within about 4
    hours and can take up to 24, and a month can be revised until about 7 days after it ends; an expired cursor is HTTP 410.
  - **Rows kept:** per actor, key, workspace and product per day, with `requests` (`cost.requests`, which is null whenever
    `cost_type` or `token_type` is a grouping, so those are not requested). Model and token type are therefore **not**
    kept, unlike `cost_report`'s `description` grouping, which no longer exists on this endpoint.
  - **Per-actor storage:** `ClaudeApiPlatformUserUsageNormalizer` writes one `UserDailyUsageRecord` per actor per day:
    `user_actor` is a `Person`; `service_account_actor` and `api_actor` (a workspace key, reported at key level) are
    `ApiKey`; a federated actor or an unknown type is `Unrecognized`, named in a warning by type only. Cost with no actor
    goes in a synthetic `unattributed` bucket so per-actor figures add up to the daily total. Legacy rows carry no actor and
    produce no per-user rows.
  - **Unverified:** which `product` values make up Console API usage (`messages_api`, `message_batches_api`,
    `managed_agents_api`, `claude_console` are candidates; the reference does not say), so no product filter is applied.
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

- **COSTS cross-check cost, and the settings that bound it (issue #70, 2026-11-22).** Observed before the change: over a
  30-day window the export is hundreds of file downloads made one after another, and one dry run took about **12 minutes per
  tenant** (no timeouts or retries, so the cost is the number of sequential requests). Now: files are downloaded with a bounded
  degree of parallelism (default 4, `Meterist:ChatGpt:CostsCrossCheck:MaxParallelDownloads`), read back in listing order so the
  rows and the first-listed duplicate are exactly what the sequential loop gave; `RecentDays` limits the window to the last N
  days; `Enabled` turns it off. The scheduled run uses the last 7 days. Every run reports `COSTS cross-check: N file(s) in
  Ns (up to P at a time)`.
  - **Before (measured 2026-11-22, one tenant, `extract --dry-run` over 2026-10-24 to 2026-11-21, 29 days):** listing the
    files took 10 pages of up to 100; **912 files were downloaded one at a time in 615 s (about 0.67 s per file)**,
    giving 3,479 deduplicated events and 2,433 billing-line rows. That is the 12 minutes the issue reported; the
    cost is the number of requests.
  - **After, default of 4 at a time (measured 2026-11-22, same tenant and window as above):** the same **912 files in 143 s**
    (142.6 s), **4.3 times faster** than the 615 s sequential run, with an identical result: the same 3,479 deduplicated events and
    2,433 billing-line rows, the same four days where Daily Usage and `COSTS` disagree on credits, the same 15 days that
    kept their higher stored usage, the same new day and the same per-user rows. No 429 or retry was logged, so four at a
    time looks acceptable to the endpoint on this tenant, though only one run at this level has been made.
  - **After, `RecentDays=7`:** not yet measured. The files are about 31 a day, so a week is roughly 220 files, which is about
    35 s at four at a time. That figure is an estimate; the run will confirm it, and whether the older days' comparison
    simply drops out of the output (it should: no disagreement warning for a day before the window).
  - Concurrency is a guess at what the endpoint tolerates (no rate limit is documented for the file download), so 4 is a
    conservative start, and the shared resilience handler still retries a 429 or 5xx.
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
    See "Daily Usage history depth" below for what the live probe found (issue #10).
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
  - **Daily Usage history depth and reliability (spike #10, live-probed 2026-11-20, read-only).**
    - **History is a fixed start date per workspace, not a rolling window.**
      Requesting from 2026-01-01 returned data from a different first day for
      each tenant (2026-04-19 and 2026-05-25), the same first days as the
      probe a day earlier (so no day-over-day creep), and the two spans (216
      and 180 days) are not one fixed length. The start date looks like when
      the workspace's analytics began. It is a property of each workspace, so
      check it per tenant; it is not documented by the vendor.
    - **A row exists only for a day with usage.** One tenant had a single
      day without a row in its whole history; the other had 19, all of them
      weekend days. Meterist writes such a day as no usage, which is correct, but
      it means a missing row cannot be told apart from a day the API does not
      cover. Only a day before the tenant's first day is outside coverage.
    - **Per-user and workspace grouping agree exactly** (one tenant's May:
      identical credit totals either way), so the daily workspace row can be
      trusted as the sum of its users.
    - **The earliest weeks are real but tiny, not truncated.** The first days
      of history carry a handful of credits and ramp up over weeks, with no
      abrupt cutoff, which looks like a workspace still being rolled out
      rather than the API under-reporting.
    - **Unexplained early-month gap, still open.** For one tenant, May credits
      from the API are about 55% of the console Workspace Analytics CSV for the
      same month (the CSV downloaded in July), while June matches to one credit.
      Both groupings of the API agree with each other, and May is dominated by
      Codex clients (about three quarters of that month's credits), so a
      difference in whether the CSV counts some Codex or other usage that the
      unified API did not record in its first weeks is the leading but unproven
      explanation. Resolving it needs the CSV broken down by client and day.
    - **Trust boundary, provisional:** treat the API as authoritative from the
      first full month after a tenant's first day, and the first month as
      unconfirmed against the console.
  - **COSTS vs Daily Usage, explained (spike #10, 2026-11-20).** Compared per
    day over the last 29 days for both tenants; the two sources agree to within
    a small fraction of a percent overall, and the differences have three
    separate causes:
    - **`COSTS` lines with no user attached.** On one tenant, Mondays carried
      `COSTS` lines with an empty `user_id` (model usage with no person
      behind it). The Daily Usage API returns per-user rows only, so those
      lines never appear in it. On the two Mondays checked in detail, the
      unattributed lines equalled the day's whole difference to the cent. The
      other tenant showed none. Why the lines recur on Mondays is not known
      (a weekly scheduled or system job is the obvious candidate).
    - **Non-token SKUs.** `COSTS` also carries lines such as `site hosting | ...`
      (a few cents a day) that the API's per-model totals do not include. This
      is the steady small gap on most days of the same tenant.
    - **`COSTS` missing events, and unfinished days.** On the other tenant the
      API was sometimes higher, by multiples of about ten credits, so a few
      `COSTS` events were absent on some days of any weekday. The most recent
      day in the data was far lower in `COSTS` (about 14%), consistent with the
      export not having received that day's events yet, which is another
      reason it is only a cross-check.
    - **Consequence:** stored ChatGPT usage, taken from the Daily Usage API,
      understates the invoice-relevant total by the unattributed and non-token
      lines, around one percent or less for the tenant affected. If that ever
      matters, the fix is to add the `COSTS`-only residual (lines with no user,
      and non-token SKUs) as a separate reconciling amount, not to switch source.
    - Reproduce with `scripts/QueryChatGptSourceComparison.sql` (per-day
      totals) and `scripts/QueryChatGptMondayDetail.sql` (per user and SKU).
  - **Workspace member roster (issue #28, 2026-11-20).** `HttpChatGptWorkspaceUserRepository` reads
    `GET /v1/manage/workspaces/{workspace_id}/users` (`limit=1000`, `after=` the previous page's `last_id` while
    `has_more`) with the admin key and workspace ID already stored for the tenant. **The key Meterist already stores is
    accepted** on both tenants, so no second credential is needed. A member is `object` (`workspace.user`), `id`,
    `email`, `name`, `role`, `seat_type` and `is_scim_managed`; there is **no join date and no last-active field**
    (so `AddedAt` is empty, and idle seats are roster members with no Daily Usage rows in the period, a later join).
    Seen live: roles `owner`, `admin`, `member` and `analytics_viewer`; `seat_type` `chatgpt` on every member (the
    documented other value is `codex`); `is_scim_managed` false on every member; name and email on every member; ids
    unique. The list is **active members only**, excludes service accounts and is eventually consistent, so a removed
    member drops out of the next snapshot. The pagination parameter is `after`: a `page` parameter is silently ignored
    and returns the first page again, which a naive client would loop on (the client also refuses a cursor it has
    already seen). Failures are loud: a blank or repeated cursor, a member with no id, or running out of pages throws,
    because a partial list stored as a snapshot would read as people having left. A 401, 403 or 404 names the
    `chatgpt.enterprise.user.read` scope and the workspace ID (a key for another workspace gets the same answer).
    Stored by `meterist roster snapshot`, and by the scheduled run's `roster snapshot-all`.
  - **Per-user rows are stored (issue #17).** With a `WorkspaceId`, each
    extraction also queries the API with the default (per-user) grouping and
    keeps those rows in raw storage tagged `DailyUsageUser`, with the vendor's
    client and model breakdowns as sent. They are normalized into
    `UserDailyUsageRecords` by `ChatGptEnterpriseUserUsageNormalizer`:
    - **Identity:** `UserId` is the vendor's user id and `UserEmail` the actor's
      email. The actor has **no display name**, so `UserName` is empty.
    - **Amount:** priced exactly like the daily total (native USD as it is, plus
      credits times the configured credit-to-USD rate), so per-user dollars add
      up to the day's `UsageOrOverage`. Only when no rate is configured does a
      row use the vendor's own estimate. **Any amount that includes converted
      credits is marked `IsEstimated`**: a per-user dollar figure derived from
      credits is not a billed amount, even at the contracted rate. Only native USD
      (`cost_usd`, null on both tenants' credit-billed workspaces) is
      vendor-reported, so in practice every ChatGPT per-user row is estimated and
      reports must say so.
    - **Requests** is the API's message count; **Credits** is stored as sent.
    - **Reconciliation:** each completed day's per-user credits are compared with
      its workspace row (tolerance 5 credits or 2%). A mismatch logs a warning
      naming the date and the two figures, never a person. The per-user
      grouping omits users who have since been deleted, so a shortfall is
      expected now and then; users summing to more than the workspace is not.
    - **Best effort:** if the per-user query fails, the extraction still succeeds
      on the workspace totals (the source of record), logs a warning, and stores
      no per-user rows for that run; the next run that covers the period fills
      them in.
    - Rows with no user id are skipped and counted in a warning.
    - **Actor type:** only `ACCOUNT_USER` (the one value seen live) is stored as
      `Person`. Any other type, or none, is stored as `Unrecognized` so totals still
      reconcile, left out of person-level reporting, and named in a warning (type
      names and counts only), so it gets classified rather than assumed.
    - Not yet done: the COSTS-only fallback (no `WorkspaceId`) stores no per-user rows.
- **Other documented surfaces (2026-11-19 research, not used yet):** the
  **Spend Controls** API under `/v1/manage/workspaces/{workspace_id}/usage_limits/`
  (scope `chatgpt.enterprise.usage_limit.read`) lists every user with
  `current_month_usage`, `last_month_usage`, the effective monthly limit and
  its source, and `seat_type`; it is month-to-date only and has no pool size or
  balance. `GET /v1/manage/workspaces/{workspace_id}/users` (added 2026-08-21)
  returns active members with `role` and `seat_type`, but **no last-active
  field**, so idle seats are computed as roster minus users with Daily Usage
  rows. **Built and live-tested 2026-11-20 (issue #28):** see "Workspace member
  roster" under the Daily Usage notes above. Other Compliance Logs event types (`CODEX_TURN`, `CONVERSATION_MESSAGE`,
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
  one, which Meterist leaves out by design (decided 2026-11-20, issue #35, see
  `docs/architecture.md` §5.2), so Meterist totals will not match the Google invoice.
  Sized over 2026-07-01 to 2026-11-19: tax about 9% and 10% of the Gemini figure for
  the two tenants, and one tenant's Enhanced Support (a minimum plus a variable fee)
  about 16%, so its total sits about 26% below the invoice. Above-quota and
  Claude-model SKUs have never been observed. **Implemented (issue #34):** the
  included service/SKU rules are now configuration (Meterist:Gemini:BillingFilter),
  and every extraction warns about non-zero Gemini-looking lines the rules leave
  out. Decision (revised 2026-11-20): Agent Platform compute and memory, raw Gemini model usage (Predictions SKUs) and pay-as-you-go lines are all counted by default, as usage-priced spend; only Data Index storage is ignored. Each is a configurable Include rule that can be moved to Ignore. The pay-as-you-go SKU names are unverified guesses, and watch terms still flag any such line the rules miss. All of it lands in UsageOrOverage in the daily record. **Update (issue #53, 2026-11-20):** the split by kind is now stored in a per-kind breakdown table (seat, overage, Agent Platform, model usage, pay-as-you-go), classified by a configured Category on each billing rule, and tax, support and rounding are recorded in it as non-usage rows that enter no total. See `docs/architecture.md` section 5.1a and the user guide.
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
    object carries a last-login field: **yes, `lastLoginTime`** (see below).
  - **Licence roster implemented (issue #37, 2026-11-22), from Google's reference, not yet run against a live tenant.**
    `HttpGeminiUserLicenseRepository` reads `GET /v1/projects/{project}/locations/{location}/userStores/{userStore}/userLicenses`
    with the tenant's service account (`cloud-platform` scope), `pageSize=1000` and `nextPageToken`; a blank or repeated token,
    a record with no `userPrincipal` or a malformed body fails the snapshot instead of storing a partial roster. A record is
    `userPrincipal` (the email, and the only identifier, so it becomes the member id), `userProfile` (full name),
    `licenseAssignmentState` (`ASSIGNED`, `NO_LICENSE`, `NO_LICENSE_ATTEMPTED_LOGIN`, `BLOCKED`, deprecated `UNASSIGNED`,
    and the unspecified zero value, which protobuf JSON leaves out), `licenseConfig`, `createTime`, `updateTime` and
    **`lastLoginTime`** (empty until the first login). So the last-login field **does exist**; the roster stores no
    last-login column yet, a follow-up if idle-seat dating wants it. There is no seat type or edition field: the licence
    state is stored as the member's seat type. `NO_LICENSE_ATTEMPTED_LOGIN` applies only to a person's first refused sign-in
    (someone who had a licence and lost it is `NO_LICENSE`), so it is an unmet-demand signal.
    Settings beyond billing live in the stored credential: `DiscoveryEngineProjectId` (required; without it the roster is
    *Skipped*, not failed), `DiscoveryEngineLocation` (default `global`), `UserStoreId` (default `default_user_store`).
    **Unverified, to confirm on the first live dry run:** the default user store id (Google's reference does not name one),
    whether the project id or number is accepted, the regional host for `us`/`eu` (assumed `https://us-discoveryengine.googleapis.com/`;
    the reference shows only global and us), a predefined role containing `discoveryengine.userStores.listUserLicenses`
    (the reference names the permission only), and whether `X-Goog-User-Project` is needed.
    People with no licence are in the roster but are not seats: `RosterSeatTypes` keeps them out of the seat count and the idle
    list, and `audit seats` notes how many tried to sign in without a licence.
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
See the Product Design Document's backlog section (§7) for how this
should be sequenced. *Updated 2026-11-19:* per-user data is now documented for
Claude (both products, via the analytics endpoints) and ChatGPT (Daily Usage);
none of it is stored per user yet (Sprint 2).

## Per-Tenant Credential Types Required (v1 vendors)

| Vendor | Credential type | Who provisions it | Notes |
|---|---|---|---|
| Claude Enterprise | Analytics API key | Org primary owner only | `read:analytics` scope, and (live-tested 2026-11-20) the member list works with the same key; spend-limit reads would need `read:spend_limits` (untested) |
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
