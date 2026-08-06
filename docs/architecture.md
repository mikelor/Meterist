# Meterist — Architecture Document (Initial)

**Status:** Draft/beginning — no code exists yet. This captures the shape
the implementation should take based on confirmed requirements and research
to date, and flags what's still an open decision rather than asserting
answers that haven't been agreed on.
**Last updated:** 2026-07-21

## 1. Guiding Constraints

These come directly from confirmed product requirements
([`product-design-document.md`](product-design-document.md)) and shape
every layer below:

- **Multi-tenant from day one** — not retrofitted after a single-tenant MVP.
- **Pricing/rate data is versioned config, not code** — two of four vendors
  have already changed billing structure mid-project.
- **Core logic must not depend on the CLI** — a dashboard layer comes later
  without a rewrite of extraction/aggregation logic.
- **Vendor integrations are asymmetric** — four very different native data
  shapes (Analytics API JSON, Admin API JSON, unified Cost API JSON,
  BigQuery SQL rows) must normalize into one internal model.

## 2. High-Level Shape

```
┌─────────────────────────────────────────────┐
│  Presentation layer (CLI today; dashboard    │
│  layered on later — thin, no business logic) │
└───────────────────┬───────────────────────────┘
                     │
┌────────────────────▼───────────────────────────┐
│  Core Library / Service Layer                   │
│  - SpendExtractionService: orchestration (per-   │
│    tenant, arbitrary date range — not locked to  │
│    one week at a time)                           │
│  - Pricing/rate resolution engine                │
│  - Raw persistence (day-grouped, latest-pull-per-│
│    day), then normalization into DailySpendRecord│
│  - Multi-tenant credential resolution            │
└───────┬───────────┬───────────┬───────────┬─────┘
        │           │           │           │
   ┌────▼───┐  ┌────▼────┐ ┌────▼────┐ ┌────▼─────┐
   │ Claude │  │Claude API│ │ ChatGPT │ │  Gemini   │
   │Enterprise│ │ Platform │ │Enterprise│ │Enterprise │
   │ adapter │  │ adapter  │ │ adapter │ │ adapter   │
   └────────┘  └──────────┘ └─────────┘ └───────────┘
```

Each vendor adapter implements a common interface (see §4) so adding a
future vendor (e.g. GitHub Copilot, per
[`vendor-integration-reference.md`](vendor-integration-reference.md)) means
writing one new adapter, not touching the core.

## 3. Multi-Tenancy Model

- `TenantId` is the top-level partition key across data and credentials —
  present on every persisted record (see §5 data model).
- Per-tenant vendor configuration: a tenant may use any subset of the
  supported vendors, with its own seat counts, negotiated rates
  (`VendorRateConfig` with a non-null `TenantId` overriding the public
  default), and billing-cycle alignment.
- Per-tenant credential isolation: four distinct credential *shapes* are
  required per tenant (Claude Analytics API key, Claude API Platform Admin
  API key, OpenAI workspace Admin key, GCP service account) — see §6.
- Cross-tenant reporting (the consulting benchmarking use case) reads
  *across* tenant partitions for the operator's own use, but per-tenant data
  must never be reachable by another tenant's credentials/access path if a
  client-facing view is ever added (see PRD §9 open question).

## 4. Vendor Adapter Pattern

A single interface every vendor integration implements, so the aggregation
engine and everything above it is vendor-agnostic:

```
IVendorSpendExtractor
├── VendorId: Guid   — stable identity; see §3.1 Vendor Identity. Matched to
│                       its normalizer by VendorId, not a display-name string.
├── ExtractAsync(TenantId, DateRange) → RawVendorSpendData
└── SupportsOverage / SupportsPerUserBreakdown (capability flags —
    see the cross-vendor matrix in vendor-integration-reference.md;
    these genuinely vary per vendor, not a gap to paper over)
```

A separate `IVendorSpendNormalizer` (or equivalent mapping step) turns each
vendor's `RawVendorSpendData` into one or more `DailySpendRecord`s, applying
the resolved `VendorRateConfig` (via `IVendorRateConfigRepository`, §8) where
the vendor's own API doesn't already return a usable dollar figure — either
because it returns raw usage instead of dollars (Claude API Platform's
`usage_report`, if used instead of `cost_report`), or because it has no seat
line and no reliable dollar figure at all (ChatGPT Enterprise's `COSTS`
export — see vendor reference). Gemini Enterprise's normalizer is the
counter-example: its BigQuery export already carries dollar cost directly and
ignores `applicableRates` entirely.

### 4.1 Vendor Identity — DECIDED (2026-07-21)

Vendor identity is a stable `Guid` (`VendorId`) plus a CLI-friendly
`VendorShortName` (e.g. `gemini-enterprise`), not the display name — a
rebrand or merger shouldn't orphan historical data or break credential
lookups. The vendor set is small and fixed in code (not tenant-created), so
this is a compile-time `VendorCatalog` (one `VendorIdentity` constant per
vendor: `Id`, `ShortName`, `DisplayName`), not a database table. `ISecretStore`,
`VendorRateConfig`, `DailySpendRecord`, and the CLI's `--vendor` argument all
key off `VendorId`/`VendorShortName`; `DisplayName` is resolved from the
catalog only where shown to a human, and can change freely since nothing is
keyed on it.

**Capability flags matter architecturally, not just descriptively:** e.g.
`SupportsOverage` is `false` for Claude API Platform by design (no seat
concept), and conditionally true for Claude Enterprise (depends on whether
"usage credits" is enabled for that tenant/vendor pairing) — the aggregation
engine must treat "no overage" as a valid, expected outcome for some
vendor/tenant combinations, not an extraction failure.

## 5. Data Model — REVISED (2026-07-22): daily grain, not weekly

Stakeholders confirmed a need for timespan-based extraction (e.g. "backfill
the last 4 months") rather than one-week-at-a-time, so **daily is the
canonical stored grain**. Weekly, monthly, and annual views (Annual
Projection, the spreadsheet's existing convention) are query-time
aggregations over daily records, not a separately stored table. Overlapping
extraction timespans need no special handling: the natural key
`(TenantId, VendorId, Date)` means re-extracting an already-stored day is
just another upsert against that same key — identical code path to a new day.

```
DailySpendRecord
├── TenantId (string/guid)
├── VendorId (guid)                  — see §4.1, not a display-name string
├── Date (date)                      — the grain; no WeekStart/WeekEnd
├── SeatFee (decimal)
├── UsageOrOverage (decimal)         — legitimately 0 for some vendor/tenant
│                                       combinations, see §4
├── GrossSpend (decimal)
├── CreditsApplied (decimal)
└── NetSpend (decimal)
```

```
VendorRateConfig
├── TenantId (nullable — null = default/public rate applies to all tenants)
├── VendorId (guid)
├── RateType (e.g. "per-seat", "per-million-tokens-input", "per-credit-overage")
├── ModelOrSku (nullable — for token/SKU-level rates)
├── Rate (decimal)
├── SeatCount (nullable int)         — the *billed* seat count (e.g. committed,
│                                       not necessarily active — see the ChatGPT
│                                       Enterprise 50-purchased/12-active
│                                       example in vendor-integration-reference.md)
├── BillingCadence (nullable enum: Monthly/Annual/OneTime)
├── EffectiveFrom (date)
└── EffectiveTo (nullable date)
```

`SeatCount`/`BillingCadence` were added 2026-07-22 alongside the `VendorId`
migration: a seat/license contract term (rate × seat count, billed monthly
or annually) is the same *kind* of versioned tenant+vendor+date-scoped fact
`VendorRateConfig` already models, so it reuses the same
`EffectiveFrom`/`EffectiveTo` versioning rather than a parallel table — a
rate-card change correctly leaves already-computed historical
`DailySpendRecord`s alone. **Not consumed by anything yet**: no v1 vendor
needs it (three return dollars directly; Gemini's seat fee arrives
pre-prorated from BigQuery) — these fields sit defined-but-unused until
Claude Enterprise or ChatGPT Enterprise needs to derive a seat fee from
contract terms rather than a vendor API (the same deferred
`IPricingRateResolver` implementation below).

### 5.1 Raw Data — structurally daily, not a convention

`RawVendorSpendData` groups records by calendar day
(`IReadOnlyDictionary<DateOnly, ...>`) rather than a flat list, so "daily is
the raw grain" is a property of the type itself — every vendor extractor is
required to satisfy this shape, not just encouraged to via a naming
convention. Gemini's BigQuery query aggregates to `(date, sku)` at the SQL
level for exactly this reason, rather than returning hourly rows for later
bucketing in C#.

Raw data is persisted (one row per `(TenantId, VendorId, Date)`, that day's
records as a JSON blob) **before** normalization runs, so a normalization bug
found later can be fixed and replayed against the original pull — this
matters because some vendors have limited retention (Claude Enterprise
Analytics API only goes back to Jan 1, 2026; ChatGPT Enterprise ~120 days),
so the vendor API may no longer have the historical window by the time a bug
is found. **Scope cut:** this keeps only the *latest* pull per day (an
upsert, same semantics as the canonical layer) — not a full audit log of
every historical extraction attempt. A true append-only history is a larger,
separate feature (unbounded growth, different retention story) and is
deliberately not built.

**Future addition (v1.x, not v1):**

```
UserSpendRecord
├── TenantId
├── VendorId
├── Date
├── UserId / Email
├── Amount (decimal)
└── IsEstimated (bool)    — true for Gemini Enterprise (no vendor-reported
                             per-user $, would be a derived allocation);
                             false for Claude Enterprise / ChatGPT Enterprise
                             (vendor-reported natively); N/A for Claude API
                             Platform (no per-human-user concept there)
```

Flagging `IsEstimated` at the schema level (rather than a documentation
footnote) matters — any report/UI built on this later must not present an
estimate with the same confidence as vendor-reported data.

## 6. Credentials — DECIDED (2026-07-21)

**Decision: DPAPI-encrypted local files for v1, behind an `ISecretStore`
interface, with the specific future cloud backend left open.** Confirmed
requirement: four distinct credential shapes per tenant (Claude Analytics
API key, Claude API Platform Admin API key, OpenAI workspace Admin key, GCP
service account JSON), so whatever storage mechanism is chosen needs to
handle heterogeneous secret shapes, not just uniform API-key strings.

- **v1: one DPAPI-encrypted file per tenant** (via .NET's Data Protection
  APIs / `ProtectedData`), holding whichever of the four credential shapes
  that tenant actually uses as a small JSON payload. Chosen over Windows
  Credential Manager specifically because Credential Manager's per-entry
  size limit (~2.5KB for generic credentials) is tight for a GCP service
  account JSON key — DPAPI-encrypted files handle all four shapes uniformly
  with one mechanism instead of needing two different stores for "short API
  keys" vs. "JSON blobs."
- **Access goes through `ISecretStore`** regardless of backend, so the v1
  choice is swappable later without touching adapter code.
- **DPAPI is intentionally user/machine-scoped** — decryption only works
  under the same Windows user profile on the same machine it was encrypted
  on. This is a feature, not a limitation to work around: it means DPAPI
  **cannot** silently follow this tool into a hosted deployment. The moment
  Meterist runs somewhere other than the operator's own machine, this
  breaks loudly and forces a deliberate migration to a real cloud secrets
  manager, rather than a local-security-model secret store quietly ending
  up in a hosted, multi-tenant-facing environment.
- **Which cloud secrets manager to migrate to is explicitly left open** —
  not pre-committed to Azure Key Vault just because §9/§12 lean toward
  Azure for hosting/auth. Revisit when the deployment model actually
  changes, informed by whatever hosting decision is made at that time.

## 7. Output / Persistence Target — DECIDED (2026-07-21)

**Decision: database + reporting layer, using SQLite for v1.** Of the three
options considered, this was reinforced by the vendor research: three of
the four v1 vendors (Claude Enterprise, Claude API Platform, ChatGPT
Enterprise) already return dollar cost directly from their APIs, so a
database isn't gated on rate-table computation — it's storing what the
API already gives us. Combined with the multi-tenant cross-tenant-query
requirement and the planned dashboard evolution, a database is the only
option that doesn't need bolting-on later.

- **SQLite specifically for v1**, not a server-based RDBMS — this follows
  from the §9 Aspire decision to keep v1 dependency-light (no Docker, no
  AppHost). Introducing a containerized database now would mean running a
  container just to ship v1, undercutting that reasoning. SQLite is a
  single file, zero-config, fully queryable via EF Core.
- **One database file with `TenantId`-scoped tables** — not one file per
  tenant. Per-tenant files would fight the cross-tenant benchmarking use
  case that's the point of multi-tenancy here.
- **Migration path**: "SQLite locally → a server-based RDBMS (e.g.
  Postgres) once hosted" is a well-worn EF Core pattern, left open which
  specific engine rather than pre-committing — same reasoning as §6.
  Requires discipline now: avoid SQLite-specific type-affinity behavior
  (particularly around decimals and dates) so that later migration is a
  provider swap, not a rewrite.
- **CSV/xlsx stays a future export view** generated from this database (the
  "xlsx export bridge" backlog item in the PRD), not the primary store —
  satisfies anyone still wanting a familiar spreadsheet without
  compromising the "full replacement" goal.

Regardless of backend, the aggregation engine (§2) depends on a repository
abstraction, not a concrete store, so this decision doesn't leak into
vendor adapter or pricing-engine code.

## 8. Pricing / Rate Resolution Engine — WIRED UP (2026-07-22)

- `IVendorRateConfigRepository` (`Meterist.Core.Persistence`), implemented by
  `EfVendorRateConfigRepository` — replaces an earlier, unconsumed
  `IPricingRateResolver` interface whose single-date, single-`modelOrSku`
  signature didn't compose with `IVendorSpendNormalizer.Normalize`'s
  whole-period `applicableRates` list. `SpendExtractionService` now calls
  `GetApplicableRatesAsync(tenantId, vendorId, period, ct)` once per vendor
  per extraction and passes the real result into `Normalize` — no longer
  "not consumed yet."
- `GetApplicableRatesAsync` returns rows whose `[EffectiveFrom, EffectiveTo]`
  window overlaps the period, for this vendor, where `TenantId` is either the
  requested tenant or `null` (the public default). **v1 policy:** a
  tenant-specific row fully replaces the public default for the same
  `ModelOrSku` — simpler than day-by-day interval merging between an
  override and a default that both partially cover the period. A
  `ModelOrSku` can still have multiple time-versions within what's returned
  (a rate change mid-period, e.g. the confirmed Sonnet 5 rate step-up on Sep
  1, 2026) — callers (normalizers) pick, per day, whichever row's window
  actually covers that day.
- **`CloseOpenEndedRateAsync`** — called by the CLI's `rates set` before
  `AddAsync`, closes out any existing open-ended (`EffectiveTo == null`) row
  in the same scope (same `TenantId`-or-public, `VendorId`, `ModelOrSku`) by
  setting its `EffectiveTo` to the day before the new row's
  `EffectiveFrom`. This is what makes a contract renewal a single `rates
  set` call rather than a manual two-step "close the old row, then add the
  new one" — without it, two open-ended rows for the same scope would leave
  `GetApplicableRatesAsync`'s per-day lookup with an undefined tie-break.
- **`FindNextEffectiveFromAsync`/`FindOverlappingRatesAsync` — added
  2026-08-06** to support backdating a rate without hand-computing its
  boundary date. `FindNextEffectiveFromAsync` returns the earliest
  `EffectiveFrom` already on file for the scope that's later than a given
  date; `rates set` uses it to auto-cap a backdated row's `EffectiveTo` to
  the day before, when `--effective-to` is omitted — the same kind of
  derivation `CloseOpenEndedRateAsync` already does for the forward-renewal
  case, just running the other direction. `FindOverlappingRatesAsync` is a
  general safety net run right before `AddAsync` (after
  `CloseOpenEndedRateAsync`, so a legitimate renewal's freshly-closed
  predecessor doesn't false-positive against itself): the schema has no
  unique constraint preventing two rows in the same scope from overlapping
  (the index at `MeteristDbContext.OnModelCreating` is intentionally
  non-unique, to support the public-default-null-`TenantId` semantics), so
  this is the only thing that stops a bad boundary date from silently
  producing two ambiguous rows.
- **Corrected claim:** this was originally scoped as "not in the critical
  path for vendors whose API returns dollar cost directly," listing ChatGPT
  Enterprise Cost API among them. That assumption didn't survive contact
  with the real schema (see `docs/vendor-integration-reference.md`): the
  `COSTS` compliance-log export has **no seat/subscription line at all** and
  **no reliably-present dollar figure** (`estimated_cost_usd` was absent in
  a real sample). ChatGPT Enterprise is the first real consumer of this
  engine — `ChatGptEnterpriseSpendNormalizer` resolves both a `per-seat`
  rate (prorated daily by `BillingCadence`) and a `credit-to-usd` rate (used
  only when the vendor's own estimate isn't present) from this list, using
  fixed `ModelOrSku` string keys (`ChatGptRateKeys` in
  `Meterist.Vendors.ChatGptEnterprise`) so code and the `rates set` CLI
  command can't silently drift apart. Claude Enterprise/API Platform still
  return dollar cost directly and are expected to remain out of this
  engine's critical path once built.

## 9. Deployment Model & Scheduling — DECIDED (2026-07-21)

**Decision: local deployment for v1, with .NET Aspire adopted in two stages
rather than all at once.**

- **v1 runs locally**, operator-triggered via CLI
  (`meterist extract --tenant X --from 2026-07-01 --to 2026-07-31`). No
  hosted service, no container orchestration required to ship v1.
- **Aspire's `ServiceDefaults` pattern is adopted now**, decoupled from the
  full Aspire AppHost — this is just a shared project wiring up
  OpenTelemetry, health checks, and Polly-based resilience into the CLI/core
  library. It requires no container runtime and no orchestration; it's the
  mechanism behind the observability decision in §10.
- **The full Aspire AppHost (multi-project orchestration, container
  resources, `azd` deployment) is deferred** until the dashboard + scheduled
  worker + database triad actually exists as separate processes (PRD §7,
  v1.x/v2) — that's where Aspire's service-discovery and local-orchestration
  value actually shows up. Adopting it for a single CLI process today would
  add ceremony (AppHost project, Docker Desktop dependency for any
  containerized local resources) without a payoff yet.
- **Implication for §7 (Output/Persistence Target) and eventual hosting:**
  Aspire's turnkey deployment path (`azd up`) targets Azure Container
  Apps/AKS specifically — it is not a cross-cloud abstraction, though the
  container images it produces are portable OCI images. Given Entra ID is
  also the planned direction for future client auth (§12), defaulting
  toward **Azure as the eventual cloud target** is a reasonable, consistent
  choice — flagged here as an implicit commitment worth being deliberate
  about, not an accident of tooling choice.
- **Idempotency requirement carries over regardless of deployment model:**
  re-running extraction for an already-pulled day must be safe — the
  `TenantId + VendorId + Date` natural key (§5) makes this an upsert by
  construction — since at least two vendors' APIs (Claude Enterprise
  Analytics API, Claude API Platform cost/usage reports) explicitly document
  revision windows (data can change for up to 30 days / ~5 minutes after
  initial availability).

## 10. Error Handling & Observability — DECIDED (2026-07-21)

**Decision: adopt OpenTelemetry from day one**, via the Aspire
`ServiceDefaults` pattern described in §9 — traces, metrics, and structured
logs wired into the CLI/core library as NuGet packages, with no dependency
on the full Aspire AppHost. Locally this exports to the Aspire dashboard;
later (once hosted) it can export to Azure Monitor/Application Insights or
any OTel-compatible backend without changing instrumentation code.

This directly addresses the risk that three of the four v1 vendor APIs are
beta or shipped within the last ~2 months — schema drift and transient
failures should be expected, not treated as exceptional. At minimum, v1
needs:

- Per-tenant, per-vendor extraction status (succeeded / failed / partial)
  surfaced somewhere the operator will actually see it — a silent failure
  in a billing tool used for client-facing consulting deliverables is a
  real business risk, not just a bug.
- A clear distinction in logs/output between "vendor returned zero because
  there's genuinely nothing to report" (e.g. Claude Enterprise seat-based
  without usage credits enabled) and "extraction failed" — §4's capability
  flags exist partly to make this distinction mechanical rather than
  something a human has to remember per vendor.

## 11. Testing Strategy — DECIDED (2026-07-21)

**Decision: a mocking environment, split by vendor integration shape**, since
three of four vendor APIs are beta and one (Claude Enterprise Analytics API)
is explicitly rate-limited (60 req/min org-wide):

- **HTTP/REST vendors** (Claude Enterprise, Claude API Platform, ChatGPT
  Enterprise) — mock via recorded response fixtures (e.g. WireMock.Net),
  captured from real API responses where possible so fixtures reflect actual
  vendor schemas rather than assumed ones.
- **Gemini Enterprise (BigQuery)** — a different shape; not a simple HTTP
  mock target. Wrap BigQuery access behind a repository interface and
  provide an in-memory fake implementation for tests, rather than mocking
  the SQL/gRPC client directly.
- **Live-contract verification, separate from the automated suite:**
  because mocks can silently drift from real vendor behavior as these beta
  APIs evolve, maintain a periodic (not per-commit) check against a real
  sandbox tenant per vendor to catch drift the mocks wouldn't surface.

## 12. Client-Facing Authentication (Future Phase) — Direction Set (2026-07-21)

Not a v1 requirement (see PRD §6/§9 — v1 is operator-only, clients receive
output rather than logging in), but a direction has been set so future work
doesn't have to re-litigate it:

- **Tentative default: Entra ID as the identity provider, via
  Microsoft.Identity.Web** (Microsoft's own ASP.NET Core library for Entra
  ID integration), rather than standing up a separate authorization-server
  product in front of it. Microsoft.Identity.Web supports multi-tenant
  sign-in directly — relevant here since each client organization would
  likely authenticate against *their own* Entra ID tenant, not a shared one.
- **Duende IdentityServer was considered and deliberately not chosen as the
  default**, for two reasons worth remembering if this is revisited:
  1. **Licensing**: Duende IdentityServer is a commercial product (since
     2022) — its free tier is scoped to dev/test or small-business revenue
     thresholds. This is a consulting business tool, so production use would
     likely require a paid license.
  2. **Not clearly needed**: Duende's value is being your *own* token-issuing
     authorization server — worthwhile when federating multiple upstream
     IdPs, or when multiple first-party apps/APIs need one centralized
     issuer. The concrete need here ("let a client org's own Entra ID users
     see only their org's data") doesn't require that extra layer.
- **Revisit Duende only if a concrete gap appears** — e.g. a client without
  an Entra ID tenant needs to log in some other way, or multiple internal
  services need one centralized token issuer beyond what Entra ID's app
  registration model provides directly.
- Note the incidental synergy with §9: an Entra ID app registration would
  likely already be needed for Graph/Azure Cost Management auth if Microsoft
  365 Copilot is ever added as a vendor (see
  [`vendor-integration-reference.md`](vendor-integration-reference.md)) —
  the same tenant app registration could plausibly serve both purposes.

## 13. Open Decisions Summary (for sprint planning)

| Decision | Status | Blocks |
|---|---|---|
| Output/persistence target | **Decided 2026-07-21** — database + reporting layer, SQLite for v1, migrate to a server-based RDBMS (engine TBD) once hosted (§7) | Repository layer implementation can now start |
| Credential storage backend | **Decided 2026-07-21** — DPAPI-encrypted local files behind `ISecretStore` for v1; future cloud secrets manager product intentionally left open, not pre-committed to Key Vault (§6) | `ISecretStore` implementation can now start |
| Deployment model & scheduling | **Decided 2026-07-21** — local v1, Aspire ServiceDefaults now, full AppHost deferred to dashboard/worker phase (§9) | Unblocked now that output target is also decided |
| Observability | **Decided 2026-07-21** — OpenTelemetry via Aspire ServiceDefaults, decoupled from full Aspire (§10) | Unblocks day-one instrumentation of vendor adapters |
| Testing strategy | **Decided 2026-07-21** — WireMock.Net for HTTP vendors, in-memory fake for Gemini/BigQuery, periodic live-contract checks (§11) | Unblocks writing the first vendor adapter tests |
| Client-facing auth (future phase) | **Direction set 2026-07-21** — Entra ID + Microsoft.Identity.Web tentative default over Duende (§12) | Not a current blocker; informs future auth work only |
| Eventual server-based DB engine (post-SQLite) | Open, intentionally deferred (§7) | Only matters once hosted; no v1 impact |
| Eventual cloud secrets manager product (post-DPAPI) | Open, intentionally deferred (§6) | Only matters once hosted; no v1 impact |
| Eventual cloud hosting target | Implicit lean toward Azure (via Aspire + Entra ID choices, §9) — not explicitly confirmed | Worth an explicit sign-off before any cloud-specific code is written |
| ChatGPT Enterprise Cost API exact schema | Needs a spike with a real Admin key | ChatGPT Enterprise adapter implementation |
| Ecosync Claude Enterprise "usage credits enabled?" status | Needs confirmation | Whether the Claude Enterprise adapter should expect any overage data for that tenant |
