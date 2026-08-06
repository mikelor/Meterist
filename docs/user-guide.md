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
| ChatGPT Enterprise | `chatgpt-enterprise` | Implemented — also requires `rates set` before extracting, see [Configuring rates](#configuring-rates) below |
| Claude API Platform | `claude-api-platform` | Implemented |
| Claude Enterprise | `claude-enterprise` | Implemented — also requires `rates set` before extracting, see [Configuring rates](#configuring-rates) below |

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

### ChatGPT Enterprise

**One-time Admin key setup:**

1. In **OpenAI Admin Console → Billing**, click **Create admin key**.
2. Select **Restricted** permissions, and set only:
   - **Compliance logging platform → Costs → Read**
   Leave everything else (Workspace analytics, Codex analytics API, Usage
   limits, Group Management, etc.) at `None` — Meterist only ever needs to
   read cost data, and Read is the only permission level this scope offers
   anyway (it's a reporting-only surface).
3. Copy the generated key immediately — it's shown once. This is the
   `AdminApiKey` value below.
4. Find your **Organization ID** (`org-...`) under **Settings → General** —
   this is the API Platform Organization ID, **not** the Workspace ID shown
   just below it on the same page. The `COSTS` export is organization-scoped.

**Build the credential file:**

```powershell
$credential = @{
    OrganizationId = "org-XXXXXXXXXXXXXXXXXXXXXXXX"
    AdminApiKey    = "<the admin key you copied>"
}
$credential | ConvertTo-Json | Set-Content -Path "C:\path\to\chatgpt-credential.json"
```

**Store it:**

```powershell
dotnet run --project src/Meterist.Cli -- credentials set --tenant <your-tenant-id> --vendor chatgpt-enterprise --from-file "C:\path\to\chatgpt-credential.json"
```

**Required before extracting — configure rates.** Unlike Gemini, ChatGPT
Enterprise's `COSTS` export has no seat/subscription line and no reliable
dollar figure for usage — both have to come from rates you enter yourself.
See [Configuring rates](#configuring-rates) below; skipping this step means
`extract` will still succeed, but every day's `SeatFee` will be `0` and
`UsageOrOverage` will be `0` for any credit-denominated usage.

### Claude API Platform

**One-time Admin API key setup:**

1. In the **Claude Console** (`platform.claude.com`), go to **Settings →
   Organization**, and create an **Admin API key** (format
   `sk-ant-admin01-...`). You need to be an Organization Admin — this is a
   distinct key type from a regular Claude API key.
2. Copy the key value — this is the entire credential. Unlike Gemini/ChatGPT,
   there's no wrapping JSON needed (no org ID, no dataset/table to also
   supply) — just save the raw key string to a file.

**Store it:**

```powershell
"sk-ant-admin01-..." | Set-Content -Path "C:\path\to\claude-api-key.txt" -NoNewline
dotnet run --project src/Meterist.Cli -- credentials set --tenant <your-tenant-id> --vendor claude-api-platform --from-file "C:\path\to\claude-api-key.txt"
```

No rate configuration needed — the Cost Report API returns real dollar cost
directly, and this product has no seat/subscription concept at all (the
entire daily total lands in `UsageOrOverage`, with `SeatFee` always `0`).

### Claude Enterprise

**One-time Analytics API key setup:**

1. Sign in to **claude.ai** as the organization's **primary owner** — only
   the primary owner can enable API access and create Analytics API keys,
   even other org admins can't do this.
2. Go to **claude.ai → Organization settings → API**
   (`claude.ai/admin-settings/api-access`), enable public API access, and
   create an **Analytics API key**. This is a different key type from Claude
   API Platform's Admin key — **not interchangeable** with it.
3. Copy the key value — this is the entire credential, same as Claude API
   Platform (no wrapping JSON needed).

**Store it:**

```powershell
"<the analytics key you copied>" | Set-Content -Path "C:\path\to\claude-enterprise-key.txt" -NoNewline
dotnet run --project src/Meterist.Cli -- credentials set --tenant <your-tenant-id> --vendor claude-enterprise --from-file "C:\path\to\claude-enterprise-key.txt"
```

**Required before extracting — configure the seat rate.** Like ChatGPT
Enterprise, this vendor's Analytics API has no seat/subscription line at
all — the seat fee has to come from a rate you enter yourself. See
[Configuring rates](#configuring-rates) below. Unlike ChatGPT, no
credit-to-USD rate is needed — per-user cost is already real dollars.

A `UsageOrOverage` of `$0` across the board is a **legitimate result**, not
a bug, if this tenant's Claude Enterprise plan is seat-based without "usage
credits" enabled (Organization settings → Usage → Enable, in the Claude
Console) — that plan type has no overage concept at all until credits are
turned on.

## Configuring rates

Some vendors' APIs don't return a usable dollar figure for everything
Meterist needs — a flat per-seat license fee is never exposed by any
vendor's API, and ChatGPT Enterprise's usage data arrives as raw credits
with no reliable dollar conversion either (see
[`vendor-integration-reference.md`](vendor-integration-reference.md)). For
those cases, you enter the rate once via `rates set`, and it's applied
automatically (with historical versioning — a rate change doesn't retroactively
alter days already extracted before it took effect).

**ChatGPT Enterprise needs two rates configured per tenant** before its
`SeatFee`/`UsageOrOverage` will be non-zero — use exactly these
`--rate-type`/`--model-or-sku` values (the normalizer looks them up by
these literal strings):

```powershell
# Seat fee: $/seat/month, prorated daily across each calendar month
dotnet run --project src/Meterist.Cli -- rates set --tenant <your-tenant-id> --vendor chatgpt-enterprise --rate-type per-seat --model-or-sku seat --rate 30 --seats 50 --cadence Monthly --effective-from 2026-01-01

# Credit-to-USD conversion: dollars per credit
dotnet run --project src/Meterist.Cli -- rates set --tenant <your-tenant-id> --vendor chatgpt-enterprise --rate-type credit-to-usd --model-or-sku credit-usd --rate 0.07 --effective-from 2026-01-01
```

- Omit `--tenant` to set a **public default** rate shared by any tenant
  without its own override, instead of a tenant-specific one.
- `--effective-to` is optional — leave it open-ended day to day. **When a
  contract renews with a new rate, just run `rates set` again** with the new
  `--rate` and the renewal's `--effective-from` — Meterist automatically
  closes out the previous open-ended row for that same
  tenant/vendor/`--model-or-sku`, ending it the day before the new rate
  starts, so historical days extracted under the old rate are never
  recomputed. You'll see a `Closed 1 previous open-ended rate(s)...` message
  confirming this happened.
- **Backdating an earlier contract you didn't have on file yet** works the
  same single-command way — no manual deletes or careful ordering needed.
  Run `rates set` with an `--effective-from` *earlier* than a rate you
  already have, and omit `--effective-to`: Meterist looks up the earliest
  rate already on file for that scope and caps the new row's `EffectiveTo`
  to the day before it, printing a `Capped this rate's EffectiveTo to
  ...` message so you can confirm the boundary it picked. If the date range
  you end up with (whether auto-capped or explicitly given) would overlap
  an existing row for that scope, the command refuses to insert it and
  lists the conflicting row instead — so a typo'd date can't silently create
  two ambiguous overlapping rates.

**Confirm what's stored:**

```powershell
dotnet run --project src/Meterist.Cli -- rates list --tenant <your-tenant-id> --vendor chatgpt-enterprise
```

**Claude Enterprise needs just the seat rate** (no credit-to-USD rate — its
`amount` is already real dollars):

```powershell
dotnet run --project src/Meterist.Cli -- rates set --tenant <your-tenant-id> --vendor claude-enterprise --rate-type per-seat --model-or-sku seat --rate 60 --seats 50 --cadence Monthly --effective-from 2026-01-01
```

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
| **Not implemented** | This vendor's adapter doesn't exist yet — no v1 vendors are in this state as of 2026-07-22, all four are implemented. |
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
  (`service.description = "Vertex AI Search" AND sku.description LIKE
  '%Enterprise%'`, corrected 2026-07-22 against a live account — see that
  file's doc comment) against the real `service.description`/`sku.description`
  values in your BigQuery export.
- **ChatGPT Enterprise: `SeatFee` is always `0`, or `UsageOrOverage` is `0`
  despite real usage:** you're missing a rate config — run `rates list
  --tenant <id> --vendor chatgpt-enterprise` to confirm both the `seat` and
  `credit-usd` rows exist and their `EffectiveFrom`/`EffectiveTo` window
  actually covers the days you extracted (see
  [Configuring rates](#configuring-rates)). Debug logging will also show a
  one-time warning per extraction call when a rate is missing.
- **ChatGPT Enterprise: `extract` fails with a 401/403:** the Admin key is
  invalid, expired, or missing the **Compliance logging platform → Costs →
  Read** scope — recreate it per the [ChatGPT Enterprise](#chatgpt-enterprise)
  setup section above.
- **ChatGPT Enterprise: expect data from more than ~29 days ago and it's
  missing:** the compliance log export has a hard 30-day retention window —
  older days simply aren't retrievable from this vendor's API at all, not a
  Meterist bug. The extractor logs a warning when it clamps a request to
  stay inside that window.
- **Claude API Platform: `extract` fails with a 401:** the Admin API key is
  invalid, expired, or isn't actually an Admin key (`sk-ant-admin01-...`) —
  a regular Claude API key won't work for these endpoints. Recreate it per
  the [Claude API Platform](#claude-api-platform) setup section above.
- **Claude Enterprise: `SeatFee` is always `0`:** you're missing the seat
  rate — run `rates list --tenant <id> --vendor claude-enterprise` to
  confirm the `seat` row exists and its `EffectiveFrom`/`EffectiveTo` window
  covers the days you extracted.
- **Claude Enterprise: `UsageOrOverage` is `0` despite real usage in the
  Claude Console:** confirm whether this tenant's plan has "usage credits"
  enabled (Claude Console → Organization settings → Usage) — on a
  seat-based plan without it, the Analytics API has no overage to report by
  design, not a bug. See the [Claude Enterprise](#claude-enterprise) setup
  note above.
- **Claude Enterprise: `extract` fails with a 401/403:** the Analytics API
  key is invalid or expired, or it's actually an Admin key
  (`sk-ant-admin01-...`) instead of an Analytics key — the two key types
  aren't interchangeable. Recreate it per the
  [Claude Enterprise](#claude-enterprise) setup section above; only the
  org's primary owner can do this.
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
