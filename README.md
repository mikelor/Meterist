# AI Vendor Spend Tracking

## Overview

This project tracks weekly spend across four AI vendor products used by Ecosync Universal:

1. **Claude** (Chat, Claude Code, Cowork) — Anthropic Enterprise plan
2. **Claude API Platform** — Anthropic's pay-as-you-go developer API (billed separately from Claude Enterprise seats)
3. **ChatGPT Enterprise** — OpenAI
4. **Gemini Enterprise** — Google Cloud

Today, this is tracked manually in an Excel workbook (`AI_Vendor_Weekly_Spend_Model.xlsx`). The next phase is a .NET 10 C# application intended to **replace this spreadsheet entirely**, pulling vendor spend data programmatically instead of relying on manual CSV exports.

### Project docs

- [`docs/product-design-document.md`](docs/product-design-document.md) — problem statement, v1 scope, future features/backlog, open questions
- [`docs/architecture.md`](docs/architecture.md) — system shape, data model, multi-tenancy, and flagged open decisions
- [`docs/vendor-integration-reference.md`](docs/vendor-integration-reference.md) — living technical reference for each vendor's API (endpoints, auth, granularity, overage/per-user matrix), including deferred future-vendor research (Microsoft Copilot family)

---

## Current State: The Spreadsheet

### Tab structure

| Tab | Purpose |
|---|---|
| **Annual Projection** | Three budget scenarios (Flat, Trend-adjusted, Latest-week-annualized) per vendor, projected from available weekly data |
| **Instructions** | Where to find each number in each vendor's console, plus a color-coding legend |
| **Summary** | Consolidated weekly spend across all four vendors, with a stacked bar chart broken out by vendor |
| **Claude** | Weekly seat fee + usage/overage for Claude Enterprise (Chat/Code/Cowork) |
| **Claude API Platform** | Weekly token-usage cost for the developer API, priced against Anthropic's published rate card |
| **ChatGPT Enterprise** | Weekly seat fee + credit-pool overage |
| **Gemini Enterprise** | Weekly license/subscription fee + quota overage |

Weekly tracking began the week of **Jun 24, 2026**, timed to a billing model change and a promo credit. Weeks run Sunday–Saturday.

### Data sources per vendor (as currently pulled manually)

**Claude (Enterprise seats)**
- Source: `claude.ai` → Settings → Analytics → "How much is Claude costing?" → Export spend report (CSV)
- Granularity: per-user, per-model **totals for the selected date range** — no per-day breakdown in the export, so each week requires its own custom-range export
- Key columns: `total_gross_spend_usd`, `total_net_spend_usd` (net already reflects any applied credits/discounts)
- Org is confirmed on the **current single-seat Enterprise plan** (no per-seat usage allotment — all usage billed at API rates on top of the flat seat fee)

**Claude API Platform**
- Source: `platform.claude.com` → Usage → CSV export
- Granularity: **per-day** rows (`usage_date_utc`), broken out by model and API key/workspace
- Contains raw token counts only (input/output/cache tiers) — no dollar figures. Cost must be computed from Anthropic's current published per-model rate card.
- ⚠️ Only one workspace ("Ecosync Universal Sandbox") has been pulled so far. Other API workspaces, if any, are not yet covered.
- Billed separately from Claude Enterprise seats.

**ChatGPT Enterprise**
- Source: Global Admin Console → Billing tab → Credit Usage Report (CSV export)
- Granularity: **per-day** (`date_partition` column), with `usage_credits` per row
- No per-week overage total is given directly — overage must be reconstructed by tracking cumulative credit usage against the org's included credit pool (pool size backed out from the reported "unbilled overage" figure)
- Overage rate confirmed at **$0.07/credit** (validated against reported unbilled overage of 8,510 credits ≈ $595.67, and a 20,000-credit/$1,400 limit)
- Seats: 12 active of 50 purchased. Billing likely follows the committed 50, not active 12 — **still needs confirmation from the order form**, along with the per-seat rate.

**Gemini Enterprise**
- Source: Google Cloud Billing → Reports, grouped by SKU, custom date range → CSV export
- Granularity: per-week manual export (no daily granularity pulled so far, though Cloud Billing BigQuery export could provide this if enabled)
- Key SKUs: "Gemini Enterprise Plus: Subscription" (seat/license fee) and a (not yet seen) "Gemini Enterprise Overage" SKU
- New SKU lines to watch for: Agent Gateway billing (started Jul 13, 2026) and Memory Bank/Sessions billing (starts Sep 1, 2026)

This is the workflow being replaced. See below for the verified programmatic
alternative to each of these manual exports.

---

## Verified Programmatic Access (Research Spike Findings — 2026-07-21)

A dedicated research pass re-checked all four vendors' current API surfaces,
since the notes above were largely unverified assumptions. **Result: all four
vendors now have a real, documented API path as of mid-2026 — no vendor
requires browser automation (e.g. a Playwright/CoWork-driven console flow) as
a primary extraction method for MVP.** Three of the four APIs are recent
(beta or shipped within the last ~2 months), so treat schemas as subject to
change, not permanent.

| Vendor | API | Auth | Granularity |
|---|---|---|---|
| Claude Enterprise | Analytics API (`.../organizations/analytics/user_cost_report`) | Analytics API key (primary-owner provisioned) | Daily, per-user, per-model, USD |
| Claude API Platform | Admin API `cost_report` + `usage_report/messages` | Admin API key | Daily USD cost directly (no rate table needed) |
| ChatGPT Enterprise | Unified Cost API (shipped Jun 18, 2026) | Workspace Admin key | Per-user, per-product (ChatGPT+Codex), up to 120 days history |
| Gemini Enterprise | Cloud Billing → BigQuery export | Service account (BigQuery IAM) | Hourly, SKU-level (subscription + overage) |

Full endpoint/auth/schema detail, the overage/per-user feasibility matrix,
and the deferred Microsoft Copilot research now live in
[`docs/vendor-integration-reference.md`](docs/vendor-integration-reference.md)
— that document is kept current as vendor APIs evolve; this README stays a
high-level summary.

**Headline takeaway on overage/per-user (full matrix in the reference doc):**
overage is extractable for 3 of 4 vendors with real caveats (Claude
Enterprise needs "usage credits" enabled; ChatGPT Enterprise's credit-pool
size/rate stay console/contract-only), and native per-user dollar comparison
only works for Claude Enterprise and ChatGPT Enterprise today — recommended
as a v1.x/v2 feature, not a v1 blocker.

### Known limitations / manual steps today

- Every vendor requires a manual login + CSV export each week; nothing is pulled automatically
- Claude Enterprise exports require a fresh custom-range pull per week (no daily granularity to slice after the fact)
- ChatGPT overage requires manual reconstruction math (cumulative credits vs. pool size)
- Claude API Platform requires manually maintaining a pricing table that changes over time (e.g., Sonnet 5 introductory pricing ends Aug 31, 2026)
- ChatGPT Enterprise per-seat rate is still an open item
- Only 3 complete weeks of data exist as of this writing — Annual Projection scenarios should be treated as directional, not committed budget numbers, until more weeks accumulate

---

## Future Direction

### Planned: .NET 10 C# extraction tool (spreadsheet replacement)

The next phase is a .NET 10 C# application that fully replaces this spreadsheet, pulling vendor spend data programmatically instead of manual exports.

**Confirmed requirements:**

- **Replaces the spreadsheet entirely** — this is not a companion tool; the xlsx workflow goes away once this is live.
- **Multi-tenant.** The tool must support administering spend tracking for multiple customer organizations, not just Ecosync Universal. This reflects a real consulting use case — the user administers AI vendor spend for multiple client organizations and sees a cost-projection model as valuable from a consulting delivery perspective. Architectural implications include: tenant isolation, per-tenant vendor credentials/configuration, and likely cross-tenant reporting/benchmarking for the consultant's own use.
- **Support Changing Pricing Models.** Vendor pricing and billing structures are moving fast (seat-based vs. usage-based shifts, introductory pricing windows, new SKUs like Agent Gateway/Memory Bank appearing mid-year). Pricing/rate data must be modeled as versioned, updatable configuration — never hardcoded constants — across all four vendors.
- **Interface path: start CLI, evolve toward a dashboard.** Initial delivery can be a console application, but the architecture should not preclude a future web/desktop dashboard layer on top of the same core logic.
- **Output target — decided 2026-07-21.** Database + reporting layer, SQLite for v1, migrating to a server-based RDBMS (engine TBD) once hosted. See [`docs/architecture.md`](docs/architecture.md) §7.

As of 2026-07-21, the initial .NET 10 solution scaffold exists under [`src/`](src/) and [`tests/`](tests/) — project structure, domain interfaces, vendor adapter stubs, and DI wiring are in place, but no vendor extraction logic is implemented yet.

### Research spike — ✅ complete (2026-07-21)

See the "Verified Programmatic Access" section above for full findings. All
four vendor integration paths are now confirmed real APIs rather than
unverified hypotheses. Anthropic's **Compliance API** was confirmed to exist
but is irrelevant to spend extraction (audit/governance events only) — same
for OpenAI's Compliance Logs Platform. Remaining open item: the ChatGPT
Enterprise Cost API's exact field-level schema wasn't publicly documented and
needs a hands-on spike with a real Admin key.

### Ideas discussed so far

- Automating the weekly pull/aggregation cycle instead of manual CSV exports
- Keeping vendor pricing tables as maintainable/updatable data rather than hardcoded constants, since rates change (e.g., introductory pricing periods, new SKUs)
- Exploring all output-target options (xlsx write, CSV/JSON, database) before committing
- Multi-tenant design to support a consulting practice spanning multiple client organizations

---

## Open Questions / TODOs

- [ ] Confirm ChatGPT Enterprise per-seat rate and whether billing is based on 50 committed seats or 12 active seats
- [ ] Confirm whether other Claude API Platform workspaces exist beyond "Ecosync Universal Sandbox"
- [x] Complete research spike on all four vendors' programmatic access options (see "Verified Programmatic Access" above) — done 2026-07-21
- [ ] Spike the ChatGPT Enterprise Cost API with a real Admin key to confirm exact field-level schema (no public reference found)
- [ ] Confirm whether Ecosync's Claude Enterprise sub-accounts have "usage credits" enabled (determines whether any overage will ever appear for seat-based tenants)
- [x] Decide the .NET tool's output target — **decided 2026-07-21**: database + reporting layer, SQLite for v1, server-based RDBMS (engine TBD) once hosted; see [`docs/architecture.md`](docs/architecture.md) §7
- [x] Decide credential storage approach for API keys/service accounts across vendors AND across tenants — **decided 2026-07-21**: DPAPI-encrypted local files behind `ISecretStore` for v1, future cloud secrets manager intentionally left open; see [`docs/architecture.md`](docs/architecture.md) §6
- [ ] Design the multi-tenant data model (tenant isolation, per-tenant vendor config, cross-tenant reporting)
- [ ] Design the pricing-model abstraction so new vendor pricing structures can be added without code changes
- [ ] Decide whether/when to build per-user (employee-level) spend comparison — feasible today for Claude Enterprise and ChatGPT Enterprise, partial for Gemini (usage only, cost estimated), not applicable to Claude API Platform; recommended as a v1.x/v2 feature, not a v1 blocker
- [x] Microsoft Copilot family (GitHub Copilot, M365 Copilot, Copilot Studio, Security Copilot) researched 2026-07-21 and **explicitly deferred from v1 scope** — revisit only if a real paid seat exists for a tenant; findings retained in [`docs/vendor-integration-reference.md`](docs/vendor-integration-reference.md)
- [ ] Re-run the Annual Projection once 6–8 weeks of complete data are available (informational only now that the spreadsheet itself is being replaced — logic should carry over into the new tool)

See [`docs/product-design-document.md`](docs/product-design-document.md) and
[`docs/architecture.md`](docs/architecture.md) for how these open items map
to v1 scope, future features, and architectural open decisions.
