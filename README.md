# Meterist

Meterist is a .NET 11 command-line tool that extracts AI vendor spend from each vendor's own API, stores it
in a local SQLite database, and produces spend and projection reports. It is multi-tenant: one consultant or
administrator can track several client organizations, each with its own credentials, rates and data.

## What it covers

| Vendor | Source of spend data | Credential per tenant |
|---|---|---|
| **Claude Enterprise** (Chat, Claude Code, Cowork) | Anthropic Analytics API: native per-user dollar cost | Analytics API key |
| **Claude API Platform** | Anthropic analytics API: dollar cost, attributed per person or key | Admin API key |
| **ChatGPT Enterprise** | OpenAI Daily Usage API (per user, per day), with the `COSTS` compliance-log export as a cross-check | Workspace Admin key |
| **Gemini Enterprise** | Google Cloud Billing export in BigQuery, plus the user licence list | Google Cloud service account |

All four have a documented API; none needs browser automation. Three of the four APIs are recent, so
response schemas may still change. Endpoints, authentication, schemas and per-vendor caveats are in
[`docs/vendor-integration-reference.md`](docs/vendor-integration-reference.md), which is kept current as
the vendors change.

## What it does

- **Extracts daily spend** per tenant and vendor into one shared database, split into a seat fee and usage or
  overage, with the raw vendor records kept separately from the normalized ones.
- **Versioned pricing.** Seat counts, per-seat rates and credit-to-dollar rates are dated configuration, not
  constants, so a renewal, a seat change or a vendor pricing change is a new row, not a code change.
- **Per-person views.** Usage by person, rosters (who holds a seat), seat drift and idle seats, active-user
  counts, and per-member spend limits with headroom.
- **Audits.** A coverage check for missing days, seat drift, and reconciliation of per-person usage against each
  vendor's aggregate.
- **Reports.** A client spend and benchmark report (weekly spend, annualized projections, cross-tenant
  comparison) as HTML, with a manifest per run so the next report can show what changed since the last.
- **Scheduled runs.** A daily extraction that checks coverage and reports problems through its exit code.

## Quick start

Prerequisites: the .NET 11 SDK (`global.json` pins it). Run everything from the repo root.

```powershell
dotnet build
dotnet test
dotnet run --project src/Meterist.Cli -- --help
```

The usual order for a new tenant, with the details of each step in the [user guide](docs/user-guide.md):

```powershell
dotnet run --project src/Meterist.Cli -- credentials set ...      # one per vendor, stored encrypted
dotnet run --project src/Meterist.Cli -- rates set ...            # seat rate, and ChatGPT's credit-to-dollar rate
dotnet run --project src/Meterist.Cli -- extract --tenant <id> --from 2026-07-01 --to 2026-07-31 --dry-run
dotnet run --project src/Meterist.Cli -- extract --tenant <id> --from 2026-07-01 --to 2026-07-31
dotnet run --project src/Meterist.Cli -- audit coverage --tenant <id> --from 2026-07-01 --to 2026-07-31
dotnet run --project src/Meterist.Cli -- report generate --tenant <id> --from 2026-07-01 --to 2026-07-31 --out artifacts/<name>.html   # add --period month|quarter|year to group the spend table
```

`--dry-run` queries the vendors and shows what would change without writing anything. `--tenant` is any string
you choose; use the same one for a client's credentials, rates and extractions.

### Commands

| Command | Purpose |
|---|---|
| `credentials` | Store and manage per-tenant vendor credentials |
| `rates` | Set and list versioned seat and pricing rates |
| `extract` | Pull spend (and per-person usage) for a tenant and date range |
| `audit` | Data-quality checks: coverage, seat drift and idle seats |
| `report` | Generate the HTML report and compare it with the previous one |
| `users` | Per-person usage and activity views |
| `roster` | Vendor member lists: who holds a seat |
| `activity` | A vendor's own active-user and seat counts |
| `limits` | Per-member spend limits and headroom |
| `backfill` | Rebuild derived data from what is already stored |

## Privacy

Generated reports, report manifests and the database hold real spend figures and, in places, named people.
They live under the git-ignored `artifacts/` folder and the local data folder, and must never be committed.
See [`docs/client-report-generation.md`](docs/client-report-generation.md).

## Repository layout

| Path | Contents |
|---|---|
| `src/Meterist.Cli` | The command-line interface, a thin layer over the library |
| `src/Meterist.Core` | Domain model, extraction, aggregation, auditing and reporting |
| `src/Meterist.Vendors` | One adapter per vendor behind a common interface |
| `src/Meterist.Data` | SQLite persistence (Entity Framework Core) |
| `src/Meterist.Secrets` | Per-tenant encrypted credential storage |
| `src/Meterist.ServiceDefaults` | Logging and telemetry defaults |
| `tests/` | Unit and integration tests, with mocked vendor APIs |
| `scripts/` | Scheduled extraction and maintenance scripts |
| `docs/` | Design, reference and operator documentation |

## Documentation

- [`docs/user-guide.md`](docs/user-guide.md): operator guide: per-vendor setup, running extraction, reading results, troubleshooting
- [`docs/client-report-generation.md`](docs/client-report-generation.md): what the report contains and how it is generated
- [`docs/architecture.md`](docs/architecture.md): system shape, data model, multi-tenancy and the vendor adapter pattern
- [`docs/database-schema.md`](docs/database-schema.md): the stored tables and columns
- [`docs/vendor-integration-reference.md`](docs/vendor-integration-reference.md): each vendor's API, and what can and cannot be extracted
- [`docs/product-design-document.md`](docs/product-design-document.md): scope, backlog and open questions
- [`docs/vnext-contract-model.md`](docs/vnext-contract-model.md): design note on contract terms and seat changes (partly built)
- [`docs/migration.md`](docs/migration.md): moving Meterist to another machine
- [`docs/project-status-summary.md`](docs/project-status-summary.md): status recap, open questions and next steps

Work is tracked in GitHub issues and milestones.
