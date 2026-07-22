# Meterist — User Guide

This is the operator-facing guide for running Meterist day to day. For how
the tool is designed internally, see [`architecture.md`](architecture.md);
for what's in scope for v1 vs. deferred, see
[`product-design-document.md`](product-design-document.md).

## Overview

Meterist extracts AI vendor spend data programmatically and stores it
locally, replacing the manual weekly CSV-export/spreadsheet workflow. It's
multi-tenant — each client organization ("tenant") has its own credentials
and data — and runs as a CLI you operate from a terminal.

**Current vendor status:**

| Vendor | Short name (for `--vendor`) | Status |
|---|---|---|
| Gemini Enterprise | `gemini-enterprise` | Implemented |
| Claude Enterprise | `claude-enterprise` | Not yet implemented |
| Claude API Platform | `claude-api-platform` | Not yet implemented |
| ChatGPT Enterprise | `chatgpt-enterprise` | Not yet implemented |

### What to use for `--tenant`

There's no tenant registry or lookup table — `--tenant` is any string you
choose, and it's purely the partition key tying a stored credential to that
same client's extracted data. The only real requirement is using the exact
same string across every `credentials set` and `extract` call for a given
client organization.

Pick a distinct value per client (and keep test/sandbox instances separate
from real ones — e.g. `ecosync-test` rather than reusing whatever you'd use
for the real `ecosync` account later), since Meterist won't stop you from
mixing data under the same tenant string.

Running `extract` for a tenant always reports a result for every vendor —
unimplemented ones report a clear "Not implemented" status rather than
silently doing nothing.

## Prerequisites

- .NET 10 SDK installed (`dotnet --version` should report a `10.x` version
  — `global.json` pins the exact feature band this repo expects).
- Run all commands from the repo root; the examples below use
  `dotnet run --project src/Meterist.Cli --` as the entry point.

## Setup

Each vendor needs a credential stored per tenant before it can be
extracted. Credentials are stored via `credentials set` and read from a
file you construct yourself — see the per-vendor instructions below for
what that file needs to contain.

### Gemini Enterprise

**One-time GCP setup** (skip any step already done for this tenant's GCP project):

1. **Enable Billing export to BigQuery.** In the GCP console, go to
   **Cloud Billing → Billing export** for the billing account behind this
   Gemini Enterprise instance, and enable the **Standard usage cost**
   export (not Detailed — Meterist only needs `service`, `sku`, `cost`,
   `usage_start_time`, and `credits`, all present in Standard). Pick or
   create a BigQuery dataset to receive it.

   Export only applies going forward — it won't backfill history from
   before you enabled it, and a newly-enabled export can take a little
   while before rows start appearing.

2. **Create a service account** in the GCP project that owns that BigQuery
   dataset. Grant it both:
   - `roles/bigquery.dataViewer` (read the exported data)
   - `roles/bigquery.user` (run query jobs) — both are required, one alone
     isn't enough.

   Generate and download a JSON key for it.

3. **Find the export table name.** After the export is enabled, open
   BigQuery in the console and look inside your chosen dataset — Google
   auto-creates a table named like
   `gcp_billing_export_v1_XXXXXX_XXXXXX_XXXXXX` (your billing account ID,
   dashes replaced with underscores). Note the exact table name.

**Build the credential file.** Meterist's Gemini credential is a small JSON
object that nests the downloaded service account key *inside* it — build
it with PowerShell rather than by hand to avoid JSON-escaping mistakes:

```powershell
$serviceAccountJson = Get-Content -Raw -Path "C:\path\to\downloaded-service-account-key.json"
$credential = @{
    ServiceAccountJson = $serviceAccountJson
    BillingProjectId   = "your-gcp-project-id"
    BillingDatasetId   = "your_billing_export_dataset"
    BillingTableId     = "gcp_billing_export_v1_XXXXXX_XXXXXX_XXXXXX"
}
$credential | ConvertTo-Json | Set-Content -Path "C:\path\to\gemini-credential.json"
```

**Store it:**

```powershell
dotnet run --project src/Meterist.Cli -- credentials set --tenant <your-tenant-id> --vendor gemini-enterprise --from-file "C:\path\to\gemini-credential.json"
```

### Claude Enterprise / Claude API Platform / ChatGPT Enterprise

Not yet implemented — see
[`vendor-integration-reference.md`](vendor-integration-reference.md) for
each vendor's confirmed API and what's still blocking (a real Admin key
spike for ChatGPT Enterprise; confirming whether "usage credits" is
enabled for Claude Enterprise). Setup instructions will be added here once
each adapter is built.

## Usage

### Extracting spend

```powershell
dotnet run --project src/Meterist.Cli -- extract --tenant <your-tenant-id> --from 2026-07-01 --to 2026-07-21
```

- `--from`/`--to` accept any date range — Meterist stores data at daily
  grain internally, so you can request a single day, a week, or several
  months in one call; re-running an overlapping range safely updates
  already-stored days rather than duplicating them.
- Add `--vendor <short-name>` to restrict the run to one vendor (useful
  while only some vendors are implemented, or to retry just the one that
  failed).

### Reading the result table

| Status | Meaning |
|---|---|
| **Succeeded** | Extraction and storage completed; Records shows how many days were written. |
| **Not implemented** | This vendor's adapter doesn't exist yet — expected for the three not-yet-built vendors, not an error. |
| **Failed** | Something went wrong (bad credential, network/auth failure, etc.) — the Detail column has the specific error. |

## Troubleshooting

- **Debug logging is on by default** ([`appsettings.json`](../src/Meterist.Cli/appsettings.json)
  sets `Meterist` categories to `Debug`, while quieting EF Core's own very
  chatty SQL command logging down to `Warning`). This shows the exact
  BigQuery SQL and parameters sent for Gemini Enterprise, the resolved
  project/dataset/table for a tenant's credential (never the secret itself),
  row/record counts at each pipeline stage, and full exception stack traces
  on failure — this is usually the fastest way to see *why* something
  returned 0 records or failed, before resorting to the raw-data table
  below. To go back to quieter output, edit that file's `Meterist` level to
  `Information` or `Warning`.
- **"Succeeded" with 0 records** is a legitimate outcome, not a bug — it
  means there was no billable activity for that vendor/tenant in the
  requested range, or (for a newly-enabled Gemini BigQuery export) data
  hasn't started flowing yet.
- **Unexpectedly 0 records when you expect data (Gemini Enterprise):**
  check the SKU/service filter in
  [`BigQueryGeminiBillingRepository.cs`](../src/Meterist.Vendors/GeminiEnterprise/BigQueryGeminiBillingRepository.cs)
  (`service.description LIKE '%Gemini Enterprise%'`) against the real
  `service.description` values in your BigQuery export — the filter was
  written from documentation, not a live account, so it's the first thing
  worth confirming if a query that should return rows doesn't.
- **Inspecting stored data directly:** the SQLite database lives at
  `%LOCALAPPDATA%\Meterist\meterist.db`. Open it with any SQLite viewer
  (e.g. [DB Browser for SQLite](https://sqlitebrowser.org/)) and check:
  - `DailySpendRecords` — the normalized, per-day canonical figures.
  - `RawDailyExtractionRecords` — the untouched raw vendor data per day
    (its `RecordsJson` column), useful for confirming raw numbers against
    the vendor's own console before normalization logic is suspected.
- **Where credentials/keys live locally:** encrypted credential files are
  under `%LOCALAPPDATA%\Meterist\secrets`, and the DPAPI key ring used to
  encrypt them is under `%LOCALAPPDATA%\Meterist\keys`. Both are tied to
  your Windows user profile on this machine by design (see
  [`architecture.md`](architecture.md) §6) — they won't work if copied to
  another machine or user account.
