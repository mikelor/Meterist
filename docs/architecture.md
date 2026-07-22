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
│  - Extraction orchestration (per-tenant, per-    │
│    vendor, per-week)                             │
│  - Pricing/rate resolution engine                │
│  - Aggregation into WeeklySpendRecord            │
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
├── VendorName: string
├── ExtractAsync(TenantId, DateRange) → RawVendorSpendData
└── SupportsOverage / SupportsPerUserBreakdown (capability flags —
    see the cross-vendor matrix in vendor-integration-reference.md;
    these genuinely vary per vendor, not a gap to paper over)
```

A separate `IVendorSpendNormalizer` (or equivalent mapping step) turns each
vendor's `RawVendorSpendData` into one or more `WeeklySpendRecord`s, applying
the resolved `VendorRateConfig` where the vendor's own API returns raw usage
rather than dollars (only Claude API Platform's `usage_report` case, if used
instead of `cost_report` — see vendor reference).

**Capability flags matter architecturally, not just descriptively:** e.g.
`SupportsOverage` is `false` for Claude API Platform by design (no seat
concept), and conditionally true for Claude Enterprise (depends on whether
"usage credits" is enabled for that tenant/vendor pairing) — the aggregation
engine must treat "no overage" as a valid, expected outcome for some
vendor/tenant combinations, not an extraction failure.

## 5. Data Model

Carried forward from initial design discussion, refined by research:

```
WeeklySpendRecord
├── TenantId (string/guid)
├── VendorName (string)
├── WeekStart (date)
├── WeekEnd (date)
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
├── VendorName (string)
├── RateType (e.g. "per-seat-monthly", "per-million-tokens-input",
│             "per-credit-overage")
├── ModelOrSku (nullable — for token/SKU-level rates)
├── Rate (decimal)
├── EffectiveFrom (date)
└── EffectiveTo (nullable date)
```

**Future addition (v1.x, not v1):**

```
UserSpendRecord
├── TenantId
├── VendorName
├── WeekStart
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

## 8. Pricing / Rate Resolution Engine

- Resolves the effective rate for a given vendor + tenant + model/SKU +
  date, preferring a tenant-specific override (`VendorRateConfig.TenantId`
  non-null) over the public default.
- Must handle overlapping/adjacent `EffectiveFrom`/`EffectiveTo` ranges
  cleanly (e.g. the confirmed Sonnet 5 rate step-up on Sep 1, 2026 is a
  real, dated example this engine needs to get right on day one).
- For vendors whose API returns dollar cost directly (Claude Enterprise
  Analytics API, Claude API Platform `cost_report`, ChatGPT Enterprise Cost
  API, Gemini Enterprise BigQuery cost columns), this engine is *not* in the
  critical path for spend calculation — it's only load-bearing where an
  adapter falls back to raw usage counts (e.g. Claude API Platform's
  `usage_report` as a cross-check) or for seat-fee configuration, which no
  vendor exposes via API at all.

## 9. Deployment Model & Scheduling — DECIDED (2026-07-21)

**Decision: local deployment for v1, with .NET Aspire adopted in two stages
rather than all at once.**

- **v1 runs locally**, operator-triggered via CLI
  (`meterist extract --tenant X --week 2026-07-14`). No hosted service,
  no container orchestration required to ship v1.
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
  re-running extraction for an already-pulled week must be safe (e.g.
  upsert by `TenantId + VendorName + WeekStart`), since at least two
  vendors' APIs (Claude Enterprise Analytics API, Claude API Platform
  cost/usage reports) explicitly document revision windows (data can change
  for up to 30 days / ~5 minutes after initial availability).

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
