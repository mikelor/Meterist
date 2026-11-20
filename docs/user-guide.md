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

- .NET 11 SDK installed (`dotnet --version` should report an `11.x` version
  — `global.json` pins the SDK version this repo expects).
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

#### Which billing lines count, and the "not counted" warning

A Google Cloud billing export is one big table of charges. Meterist decides which
of them are Gemini Enterprise spend using three lists of rules. Each rule is a
**service name** (matched exactly) plus a **SKU pattern** (a SQL `LIKE` pattern
where `%` matches any text; the match is case-sensitive).

| List | What it does |
| --- | --- |
| **Include** | A line matching any Include rule is counted as Gemini Enterprise spend. |
| **Ignore** | A line you knowingly leave out. It is not counted and does not trigger the warning. |
| **WatchTerms** | Words that make an uncounted line look Gemini-related. A line with non-zero cost containing one of these (in its service or SKU, any case) that is neither included nor ignored triggers the warning. |

**Defaults.** Out of the box Meterist counts:

| Rule (service / SKU pattern) | What it is | Counted as |
| --- | --- | --- |
| `Vertex AI Search` / `%Enterprise%` | The seat subscription and its per-request overage. Verified against real exports. | Seat fee (SKUs containing "Subscription"), otherwise usage |
| `Vertex AI` / `%Agent Platform%` | Agent Platform compute and memory (the Agent Gateway / Memory Bank family, seen from September 2026). | Usage |
| `Vertex AI` / `%Predictions%` | Raw Gemini model token usage (for example "... Text Input - Predictions"). | Usage |
| `Vertex AI Search` / `%Pay as you go%` and `%Pay-as-you-go%` | Pay-as-you-go usage above the edition quota (the billing model from 2026-08-17). **No such SKU has appeared in a live export yet**, so these two patterns are best guesses at the name. | Usage |

The only default Ignore rule is `Vertex AI Search` / `%Data Index%` (index
storage, not specific to the Enterprise plan). The default WatchTerms are
`Gemini`, `Agent Platform`, `Discovery Engine`, `Vertex AI Search`,
`Pay as you go` and `Pay-as-you-go`.

Anything that is not a "Subscription" SKU is stored as usage/overage, so the
agent, model and pay-as-you-go lines raise a tenant's usage figure, not its seat
fee. A tenant that runs its own Gemini API workloads in the same project will
therefore see that model usage in its Gemini Enterprise numbers. If that is not
what you want to report, take the rule out as described below.

##### The "not counted" warning

Google keeps changing how this product bills, so each extraction also looks for
charges the rules leave out. If it finds some, it logs one warning naming the
service, SKU, cost and dates:

```text
Tenant 'acme' has 2 Gemini-related billing line(s) with non-zero cost that the
Gemini Enterprise filter does not count: Vertex AI | Agent Platform Compute: 1.25
(2026-03-02 to 2026-03-20); ...
```

The warning never fails the extraction and never changes the figures; it is a
prompt to decide. Either the line belongs in Gemini Enterprise spend, so add an
Include rule, or it does not, so add an Ignore rule.

##### Tax and support fees are left out on purpose

Google''s billing export also carries `Invoice | Tax` (once a month) and, on accounts
with a support plan, `Support | ...` fees (every day). Meterist does **not** count
these: they are not usage, and a support plan covers the whole billing account, not just
Gemini Enterprise. So a Gemini total will sit below the Google invoice, by about a tenth
for tax alone and by more than a quarter on an account with a support plan. The client
report's footnote says so. Do not add Include rules for them: they would land in
`UsageOrOverage` and skew the totals and projections.

To see how large they are for a tenant, run a dry run with watch terms for them (the
"not counted" warning then lists each with its total and dates). This only reads:

```powershell
$env:Meterist__Gemini__BillingFilter__Include__0__Service = "Vertex AI Search"
$env:Meterist__Gemini__BillingFilter__Include__0__SkuLike = "%Enterprise%"
$env:Meterist__Gemini__BillingFilter__Include__1__Service = "Vertex AI"
$env:Meterist__Gemini__BillingFilter__Include__1__SkuLike = "%Agent Platform%"
$env:Meterist__Gemini__BillingFilter__Include__2__Service = "Vertex AI"
$env:Meterist__Gemini__BillingFilter__Include__2__SkuLike = "%Predictions%"
$env:Meterist__Gemini__BillingFilter__WatchTerms__0 = "invoice"
$env:Meterist__Gemini__BillingFilter__WatchTerms__1 = "support"
dotnet run --project src/Meterist.Cli -- extract --tenant <your-tenant-id> --vendor gemini-enterprise --from 2026-07-01 --to 2026-11-19 --dry-run
```

Close the PowerShell window (or remove the variables) afterwards so the override does not
stay in effect. The three Include rules are the defaults minus the two unverified
pay-as-you-go patterns; they are only for this look.

##### Changing the rules

Add a section to [`appsettings.json`](../src/Meterist.Cli/appsettings.json), or
set the matching environment variables. **If the section is present it replaces
the defaults entirely**, so list every rule you want, including the ones you are
keeping. If you set only `Include`, the Ignore and WatchTerms lists become empty.
An empty `Include` list is rejected when extraction starts.

This is the complete default configuration, written out. Copy it as the starting
point for any change:

```json
{
  "Meterist": {
    "Gemini": {
      "BillingFilter": {
        "Include": [
          { "Service": "Vertex AI Search", "SkuLike": "%Enterprise%" },
          { "Service": "Vertex AI", "SkuLike": "%Agent Platform%" },
          { "Service": "Vertex AI", "SkuLike": "%Predictions%" },
          { "Service": "Vertex AI Search", "SkuLike": "%Pay as you go%" },
          { "Service": "Vertex AI Search", "SkuLike": "%Pay-as-you-go%" }
        ],
        "Ignore": [
          { "Service": "Vertex AI Search", "SkuLike": "%Data Index%" }
        ],
        "WatchTerms": [
          "Gemini", "Agent Platform", "Discovery Engine", "Vertex AI Search",
          "Pay as you go", "Pay-as-you-go"
        ]
      }
    }
  }
}
```

**To stop counting a category** (for example, report only the seat product and
keep Agent Platform and raw model usage out), move its rule from `Include` to
`Ignore`. Moving it, rather than deleting it, keeps the warning quiet about a
line you have decided about. This configuration puts Agent Platform and model
usage back out of the totals, as in the first version of the filter:

```json
{
  "Meterist": {
    "Gemini": {
      "BillingFilter": {
        "Include": [
          { "Service": "Vertex AI Search", "SkuLike": "%Enterprise%" },
          { "Service": "Vertex AI Search", "SkuLike": "%Pay as you go%" },
          { "Service": "Vertex AI Search", "SkuLike": "%Pay-as-you-go%" }
        ],
        "Ignore": [
          { "Service": "Vertex AI Search", "SkuLike": "%Data Index%" },
          { "Service": "Vertex AI", "SkuLike": "%Agent Platform%" },
          { "Service": "Vertex AI", "SkuLike": "%Predictions%" }
        ],
        "WatchTerms": [
          "Gemini", "Agent Platform", "Discovery Engine", "Vertex AI Search",
          "Pay as you go", "Pay-as-you-go"
        ]
      }
    }
  }
}
```

To drop pay-as-you-go as well, move the two `Pay` rules to `Ignore` the same way.
To count a new SKU Google introduces, add an Include rule for its service and a
pattern that matches its SKU name; the warning shows you the exact service and
SKU text to copy.

##### Seeing the split by kind of charge

The stored daily record only separates the seat fee from everything else, so
Agent Platform, model usage and pay-as-you-go all sit inside the usage figure.
To see them separately, run:

```powershell
dotnet run --project src/Meterist.Cli -- audit gemini-breakdown --tenant <your-tenant-id> --from 2026-07-01 --to 2026-11-19
```

It is read-only. It groups the stored raw billing rows into Seat, Enterprise
overage, Agent Platform, Model usage, Pay-as-you-go and Other (a SKU that matched
an Include rule but none of those; look at its name), with the cost, the number
of days that had a charge, and the SKU names behind each. It also compares its
total with the stored gross spend and says so if they differ, which means the raw
rows and the stored records come from different extractions or rules: re-run
`extract` for that period. The split is derived from SKU text, and a stored column
for it is planned.
**Environment variables.** Arrays are numbered from zero and each field is its
own variable, with `__` as the separator. For example, the first Include rule is:

```powershell
$env:Meterist__Gemini__BillingFilter__Include__0__Service = "Vertex AI Search"
$env:Meterist__Gemini__BillingFilter__Include__0__SkuLike = "%Enterprise%"
```

Environment variables merge with `appsettings.json` index by index, so a leftover
`Include__3` from an earlier experiment can survive; for anything beyond a quick
test, prefer editing the JSON.

**After you change the rules.** They apply to future extractions only. Re-run
`extract` for the earlier days you want recalculated. Gemini days are recalculated from the billing export on each pull, so re-extracting after narrowing or widening the rules gives the new figures. To see the effect before anything is written, run `extract --dry-run` first.

### ChatGPT Enterprise

**One-time Admin key setup:**

1. In **OpenAI Admin Console → Billing**, click **Create admin key**.
2. Select **Restricted** permissions, and set only:
   - **Compliance logging platform → Costs → Read** — what `extract` uses
     today.
   - **Enterprise analytics API → Read** — for the Daily Usage API
     (`/v1/analytics/workspaces/{workspace_id}/usage`), which Meterist is
     moving to as its primary ChatGPT source. Nothing calls it yet; set it
     now so the key doesn't have to be recreated later.
   Leave everything else (Workspace analytics, Codex analytics API, Usage
   limits, Group Management, etc.) at `None` — Meterist only reads usage and
   cost data, and Read is the only permission level these scopes offer
   anyway (they're reporting-only surfaces).
3. Copy the generated key immediately — it's shown once. This is the
   `AdminApiKey` value below.
4. From **Settings → General**, note two different IDs that sit next to each
   other and are easy to swap:
   - **Organization ID** (`org-...`) — the API Platform Organization ID,
     **not** the Workspace ID. The `COSTS` export is organization-scoped.
   - **Workspace ID** (a UUID such as `11111111-2222-3333-4444-555555555555`)
     — the Daily Usage API is addressed by it. Optional for now; `extract`
     works without it, but add it so the credential is ready when the Daily
     Usage API is wired in.

**Build the credential file:**

```powershell
$credential = @{
    OrganizationId = "org-XXXXXXXXXXXXXXXXXXXXXXXX"
    WorkspaceId    = "00000000-0000-0000-0000-000000000000"   # optional
    AdminApiKey    = "<the admin key you copied>"
}
$credential | ConvertTo-Json | Set-Content -Path "C:\path\to\chatgpt-credential.json"
```

`credentials set` checks this file before storing it: it must be valid JSON
with a non-empty `OrganizationId` and `AdminApiKey`, and if `WorkspaceId` is
present it must be a UUID. Pasting the `org-...` Organization ID into
`WorkspaceId` is rejected with an explanation rather than stored.

**Adding the Workspace ID to an existing credential:** the stored credential
is encrypted, so edit the source file you built it from (or rebuild it), add
`WorkspaceId`, and run `credentials set` again — it replaces the stored value.

**Store it:**

```powershell
dotnet run --project src/Meterist.Cli -- credentials set --tenant <your-tenant-id> --vendor chatgpt-enterprise --from-file "C:\path\to\chatgpt-credential.json"
```

**Required before extracting — configure rates.** Unlike Gemini, ChatGPT
Enterprise has no seat/subscription line in either of its data sources and no
reliable dollar figure for usage — both have to come from rates you enter
yourself. See [Configuring rates](#configuring-rates) below; skipping this
step means `extract` will still succeed, but every day's `SeatFee` will be `0`
and `UsageOrOverage` will be `0` for any credit-denominated usage.

**Which data source Meterist uses.** ChatGPT Enterprise has two:

- **The Daily Usage API** is the source of record whenever the credential has
  a `WorkspaceId`. It is a plain query API with months of history, so it
  returns days the other source has already lost, and a normal extraction over
  a long range backfills them. Usage is credits times your configured
  `credit-usd` rate (the rate is versioned, so a day is priced under the rate
  in effect that day), plus any natively USD-priced usage as dollars. If no
  rate is configured it falls back to the vendor's own estimated USD, with a
  warning.
- **The `COSTS` compliance-log export** (about 29 days of history, and it can
  drop or shrink days inside that window) is fetched alongside as a
  cross-check. Its rows are kept in raw storage and compared with the Daily
  Usage figures; a day where the two disagree on credits beyond a small
  tolerance is logged, and **`COSTS` never overrides a Daily Usage figure**. If
  fetching it fails, the extraction still succeeds on Daily Usage alone.

Usage is never lowered by a re-extraction, so a day that either source once
reported higher keeps the higher figure (each day's `Source` says which one).
When this overrides a figure just pulled, a warning logs the dates and both
numbers. On real data the two sources differed by less than half a percent of
total usage over four months, with `COSTS` slightly higher on some days (about
$0.03 a day, and more on Mondays), so a handful of days keep their `COSTS`
figure; that is expected, not an error. The extra `COSTS` amount is usage with no user attached and non-token lines such as site hosting, which the Daily Usage API does not return (see the vendor reference).

**Per-user usage.** With a `WorkspaceId`, `extract` also stores one row per
user per day (who used how many credits and what that cost) in the local
database, next to the daily totals. The result line gains a count, for example
`per-user rows: 12 inserted, 340 unchanged`, and `extract --dry-run` shows what
it would add without writing anything. These rows hold email addresses, so they
stay in the database and in reports under `artifacts/`; Meterist does not log
them. Dollars use your configured credit rate, like the daily total, but they are
marked as estimated: a per-user dollar figure derived from credits is an allocation,
not a billed amount. Only people (actor type `ACCOUNT_USER`) are counted as people;
any other kind of actor is stored as "unrecognized" and named in a warning. If the per-user query fails
the extraction still succeeds on the daily totals and logs a warning; run it again
to fill the rows in. A warning that per-user rows "do not add up" to the workspace
total names the date and the credits, and usually means a user has since been
deleted.

**If the Daily Usage call fails** (the admin key lacks "Enterprise analytics
API: Read", the `WorkspaceId` belongs to a different workspace, an outage),
the ChatGPT extraction **fails with a message saying so**; it does not quietly
fall back to `COSTS`, since that is the source known to lose data. Fix the
permission or ID and re-run. A tenant with **no** `WorkspaceId` stored does
fall back to `COSTS` alone, with a warning, so add one as described above.

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

**Which Anthropic endpoint is used.** By default Meterist reads cost from the
newer **Usage and Cost Analytics API** (`/analytics/cost`, beta), grouped by user
and product. It uses the same Analytics key as before; nothing needs to change on
your side. In a live test on two tenants its daily totals matched the older
`user_cost_report` to the cent for a full month. It also records each user's
request count, product and account type for later per-user reporting.

If Anthropic changes or ends that beta, the extraction fails with a message saying
so and naming the setting below. To go back to the older endpoint, set
`Meterist:ClaudeEnterprise:UseLegacyCostReport` to `true` in
[`appsettings.json`](../src/Meterist.Cli/appsettings.json):

```json
{ "Meterist": { "ClaudeEnterprise": { "UseLegacyCostReport": true } } }
```

or set the environment variable `Meterist__ClaudeEnterprise__UseLegacyCostReport=true`
for one run. The setting applies to every tenant. Re-extract after switching: the
two endpoints agreed in testing, so nothing should change, and `extract --dry-run`
will show if it does.

**Per-user usage.** `extract` also stores one row per user per day (their cost and
request count) in the local database, and the result line gains a count such as
`per-user rows: 102 inserted`. Dollars are Anthropic's own per-user figures, not
estimates. On a seat-based plan the cost is usage credits only and is near zero, so
a user can show `$0.00` and still have hundreds of requests: read the request count
as activity, not the dollars. Service accounts and API actors are stored but marked
as not people. Cost the API cannot attribute to anyone is kept under the user id
`unattributed` so the per-user figures still add up to the daily total. As with
ChatGPT, these rows hold email addresses, so they stay in the database and in
reports under `artifacts/`, and are not logged.

**Filling in history without calling the API.** To build per-user rows for days you
already extracted, run:

```powershell
dotnet run --project src/Meterist.Cli -- backfill user-usage --tenant <your-tenant-id> --vendor claude-enterprise --from 2026-07-01 --to 2026-11-19 --dry-run
```

It reads the raw records already stored, so it needs no API key and no network, and
`--dry-run` shows the count without writing. Run it again without `--dry-run` to
store them. It is safe to repeat. Days extracted before the per-user table existed
have raw rows from the older endpoint, which carry users and dollars but no request
counts; a later `extract` over those days adds the request counts.

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
- Add `--dry-run` to see what a run **would** do without writing anything:
  the vendors are still queried (read-only), but no raw data and no daily
  records are stored. Use it before a large backfill, or to check what a
  change in a vendor's data will do to what you already hold. See
  [Dry runs](#dry-runs) below.

### Reading the result table

| Status | Meaning |
|---|---|
| **Succeeded** | Extraction and storage completed; Records shows how many days were processed and Detail says what actually happened to them (see below). |
| **Not implemented** | This vendor's adapter doesn't exist yet — no v1 vendors are in this state as of 2026-07-22, all four are implemented. |
| **Failed** | Something went wrong (bad credential, network/auth failure, etc.) — the Detail column has the specific error. |

On success, **Detail** breaks the days down so you can check the result
instead of taking "Succeeded" on trust, e.g. `3 inserted, 118 unchanged`:

| Count | Meaning |
|---|---|
| **inserted** | No record existed for that day; a new one was stored. |
| **updated** | A record existed and the new figures replaced it. |
| **relabelled** | The figures are identical and only the day's `Source` label changed, for example a day first stored from one source and now confirmed by another. |
| **kept higher usage** | A record existed with *meaningfully higher* usage than the figure just pulled (by more than half a cent), and the vendor's never-lower rule kept it (ChatGPT Enterprise only today). A warning is logged with the dates and both figures. A shortfall of half a cent or less is just the two sources agreeing to within rounding: the stored number still isn't lowered, but it isn't reported as overridden. |
| **unchanged** | A record existed and already held exactly these figures. |

A run over days you already have should normally report them as
`unchanged`; if a run claims to have written days but you see no
`inserted`, check the data directly (see
[Inspecting stored data](#troubleshooting)).

### Dry runs

```powershell
dotnet run --project src/Meterist.Cli -- extract --tenant <your-tenant-id> --vendor chatgpt-enterprise --from 2026-07-01 --to 2026-11-11 --dry-run
```

A dry run prints the same result table (with Detail prefixed `dry run:`),
then, for each vendor, a table of the days that would change: new days,
updated days, and days where the never-lower rule would keep a higher stored
figure. It shows gross spend and usage side by side (stored versus pulled),
plus the `Source` of the figures. Days that would be left unchanged, and days
that would only change their source label, are counted but not listed.
Nothing is written, including raw data.

### Where each day's figures came from

For vendors with more than one data source (ChatGPT Enterprise), each stored
day records which one supplied it in `DailySpendRecords.Source`
(`costs-export` or `daily-usage-api`), so reports can disclose it. Vendors
with a single source leave it empty. See
[`database-schema.md`](database-schema.md).

### Checking for missing days

```powershell
dotnet run --project src/Meterist.Cli -- audit coverage --tenant <your-tenant-id> --from 2026-07-01 --to 2026-11-18
```

`--tenant` can be repeated, and `--vendor <short-name>` narrows the check to
one vendor. Use a `--to` a day or two behind today, since the most recent day
may not be extractable yet.

A stretch of days that were never extracted looks exactly like a quiet
stretch: nothing errors, the totals are simply low, and some vendors (ChatGPT
Enterprise's cost export keeps about 29 days) can't recover the days later.
This command lists, per tenant and vendor, the days with no stored record:

- **Seat-priced vendors** (Claude Enterprise, ChatGPT Enterprise, Gemini
  Enterprise) accrue a fee every day, so every day should have a record. Any
  missing day after the vendor's first stored record is a **gap** and fails
  the check.
- **Usage-priced vendors** (Claude API Platform) have no record on a day with
  no usage, so missing days are normal and never fail the check.
- Days before a vendor's first stored record are reported as **not yet
  backfilled**: history never extracted, which is normal for a newly onboarded
  tenant, so it doesn't fail the check either.
- A vendor with no records at all for the tenant shows as **no data** and
  doesn't fail the check.

The exit code is `0` when no seat-priced vendor has a gap, and non-zero when
one does (or when the command couldn't run, for example an unknown vendor or
`--from` after `--to`), so a scheduled run can alert on it.

### Per-user usage

Once per-user rows are stored (ChatGPT Enterprise and Claude Enterprise; see the
vendor sections above, and `backfill user-usage` for history), `users usage` shows
who used what over a period, by name or email:

```powershell
dotnet run --project src/Meterist.Cli -- users usage --tenant <your-tenant-id> --from 2026-11-01 --to 2026-11-19
dotnet run --project src/Meterist.Cli -- users usage --tenant <your-tenant-id> --from 2026-11-01 --to 2026-11-19 --vendor claude-enterprise --top 10
```

It is read-only and prints one table per vendor that has per-user rows. Each user has
their total dollars, credits, requests (messages, for ChatGPT) and days active,
highest spend first. `--top N` shows only the first N users and one line for the rest.
`--vendor` limits it to one vendor short name.

**Below each table** the vendor's aggregate for the same days is shown, so you can see
the per-user figures add up. Seat fees are listed separately: they belong to the
seats, not to anyone's usage, so they are never in the per-user dollars. Three things
are called out:

- **A mismatch.** If the per-user total differs from the aggregate by more than about
  2% (or a few cents), it says so with the difference. Usual causes: a user the vendor
  has since deleted (ChatGPT leaves them out of per-user data), a day whose stored
  total kept a higher earlier figure, or per-user rows stored from a different pull
  than the daily total.
- **Days without per-user rows.** If the aggregate has usage on days the per-user
  rows do not cover, they are counted and left out of the comparison, so a short
  per-user history does not read as a mismatch. Backfill or re-extract those days.
- **Marked figures.** `~` before an amount means it is an estimate (ChatGPT dollars
  derived from credits). `(API key)` and `(not a person)` mark usage the vendor did not
  attribute to a named person, including the `unattributed` bucket for Claude cost with
  no actor. Treat those as separate from your people.

On a seat-based Claude plan a user can show `$0.00` with hundreds of requests: the
dollars are usage credits only, so read requests and days active as the activity
signal. The output names people, so keep it out of anything you commit; reports that
include it belong under `artifacts/`.

### Running extraction on a schedule

Run extraction daily. ChatGPT Enterprise's cost export keeps only about 29
days, so a stretch of missed runs can lose data that can't be recovered later.

```powershell
./scripts/Register-DailyExtractionTask.ps1 -Time 06:00
```

This registers a Windows Task Scheduler job ("Meterist daily extraction") that
runs [`scripts/RunScheduledExtraction.ps1`](../scripts/RunScheduledExtraction.ps1)
each day. That script extracts every tenant from `2026-07-01` through
yesterday, runs the [coverage check](#checking-for-missing-days) over the same
window, and writes everything to `%LOCALAPPDATA%\Meterist\logs\extract-yyyy-MM-dd.log`
(a second run the same day appends). Its exit code, shown as the task's
"Last Run Result", is `0` when everything is clean, `1` for an extraction
failure, `2` for a coverage gap, and `3` for both.

- The task runs as you, and only while you're logged on: credentials are
  encrypted per Windows user, so it has to be the account that stored them.
  If the machine was off at the scheduled time, it runs at the next
  opportunity.
- It runs through *yesterday*, because today's data isn't complete yet.
- Tenants default to `zelleri` and `ecosync`. Pass `-Tenants` to
  `RunScheduledExtraction.ps1` (or edit the default) to add a new one once its
  credentials are stored.
- Try it now with `Start-ScheduledTask -TaskName 'Meterist daily extraction'`;
  remove it with `./scripts/Register-DailyExtractionTask.ps1 -Remove`.

**Tenants that use only some vendors.** `extract --configured-only` runs only
the vendors the tenant has a stored credential for and shows the rest as
`Skipped`, instead of reporting them as `Failed`. The scripts above use it, so
a tenant such as one with only Claude API Platform needs no special handling.
Without the flag, a vendor with no credential is a failure.

**Exit code of `extract`.** `extract` now exits `1` when any vendor extraction
failed (it used to exit `0` regardless), so scripts and schedulers can detect
failures. Skipped and not-implemented vendors don't count as failures.

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
  missing:** that only happens for a tenant with no `WorkspaceId` stored, which
  falls back to the `COSTS` compliance-log export and its hard 30-day
  retention window — not a Meterist bug. The extractor logs a warning when it
  clamps a request to stay inside that window. Add a `WorkspaceId` (see
  [ChatGPT Enterprise](#chatgpt-enterprise)) and re-extract: the Daily Usage
  API reaches back months.
- **ChatGPT Enterprise: `extract` fails mentioning "Enterprise analytics API"
  or `WorkspaceId`:** the Daily Usage API returned 401, 403 or 404. Check the
  admin key has **Enterprise analytics API → Read** and that `WorkspaceId` is
  the workspace this key belongs to (a key gets a 403 for another
  workspace's ID).
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
