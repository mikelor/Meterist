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

**Optional: the licence roster (who holds a Gemini licence).** The roster reads Google's Discovery Engine user licence list,
which is a different API from the billing export and may be in a different project. To turn it on, add up to three more
fields to the credential before storing it; a credential without `DiscoveryEngineProjectId` keeps working for billing and the
roster is simply reported as *Skipped* ("not set up"):

```powershell
$credential = @{
    ServiceAccountJson       = $serviceAccountJson
    BillingProjectId         = "your-gcp-project-id"
    BillingDatasetId         = "your_billing_export_dataset"
    BillingTableId           = "gcp_billing_export_v1_XXXXXX_XXXXXX_XXXXXX"
    DiscoveryEngineProjectId = "project-that-owns-the-gemini-enterprise-app"
    DiscoveryEngineLocation  = "global"             # or "us" / "eu"; omit for global
    UserStoreId              = "default_user_store" # omit unless yours differs
}
```

The service account needs a role that includes the `discoveryengine.userStores.listUserLicenses` permission on that project
(for example a Discovery Engine viewer or admin role; Google's reference names the permission but not a role), and the Discovery
Engine API must be enabled there. This reading is built from Google's reference and has **not** yet been run against a live
tenant: the user store id (`default_user_store`) and the regional host are the assumptions to confirm on the first
`roster snapshot --dry-run`. A `404` names the settings to check; a `403` names the permission.

#### Which billing lines count, and the "not counted" warning

A Google Cloud billing export is one big table of charges. Meterist decides what to
do with each line using four lists of rules. Each rule is a **service name** (matched
exactly) plus a **SKU pattern** (a SQL `LIKE` pattern where `%` matches any text; the
match is case-sensitive), and the first rule in a list that matches a line decides it.

| List | What it does |
| --- | --- |
| **Include** | A line matching any Include rule is counted as Gemini Enterprise spend. Its rule's **Category** says what kind of charge it is (Seat, ModelUsage...), for the breakdown. |
| **NonUsage** | A real charge on the billing account that is not usage: tax, support, rounding. It is recorded in the breakdown, labelled not counted, and is **never part of any total**. Every NonUsage rule needs a Category. |
| **Ignore** | A line you knowingly leave out entirely. It is neither counted nor recorded, and does not trigger the warning. |
| **WatchTerms** | Words that make an unhandled line look Gemini-related. A line with non-zero cost containing one of these (in its service or SKU, any case) that is not included, not in NonUsage and not ignored triggers the warning. |

**Defaults.** Out of the box Meterist counts these, in this order (the order matters:
the seat SKU contains the word "Enterprise" too, so it must be matched first):

| Rule (service / SKU pattern) | Category | What it is |
| --- | --- | --- |
| `Vertex AI Search` / `%Subscription%` | `Seat` | The seat subscription. Becomes the **seat fee**. Verified against real exports. |
| `Vertex AI Search` / `%Enterprise%` | `EnterpriseOverage` | Per-request overage on the Enterprise plan. |
| `Vertex AI` / `%Agent Platform%` | `AgentPlatform` | Agent Platform compute and memory (the Agent Gateway / Memory Bank family, seen from September 2026). |
| `Vertex AI` / `%Predictions%` | `ModelUsage` | Raw Gemini model token usage (for example "... Text Input - Predictions"). |
| `Vertex AI Search` / `%Pay as you go%` and `%Pay-as-you-go%` | `PayAsYouGo` | Pay-as-you-go usage above the edition quota (the billing model from 2026-08-17). **No such SKU has appeared in a live export yet**, so these two patterns are best guesses at the name. |

Everything with the `Seat` category is the day's seat fee; every other counted line is
usage/overage. So the agent, model and pay-as-you-go lines raise a tenant's usage
figure, not its seat fee. A tenant that runs its own Gemini API workloads in the same
project will therefore see that model usage in its Gemini Enterprise numbers. If that
is not what you want to report, take the rule out as described below.

The default **NonUsage** rules, recorded but never counted:

| Rule (service / SKU pattern) | Category | What it is |
| --- | --- | --- |
| `Invoice` / `%Tax%` | `Tax` | Tax, once a month. |
| `Invoice` / `%Rounding%` | `Rounding` | A cent or two of invoice rounding. |
| `Support` / `%` | `Support` | Cloud support plan fees, on accounts that have one. |

The only default Ignore rule is `Vertex AI Search` / `%Data Index%` (index
storage, not specific to the Enterprise plan). The default WatchTerms are
`Gemini`, `Agent Platform`, `Discovery Engine`, `Vertex AI Search`,
`Pay as you go` and `Pay-as-you-go`.

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
Include rule, or it is a real charge that is not usage, so add a NonUsage rule, or it
does not matter, so add an Ignore rule.

##### Tax and support fees: recorded, never counted

Google's billing export also carries tax (once a month) and, on accounts with a support
plan, support fees (every day). Meterist records them as **NonUsage** charges but does
**not** count them: they are not usage, and a support plan covers the whole billing
account, not just Gemini Enterprise. So a Gemini total sits below the Google invoice, by
about a tenth for tax alone and by more than a quarter on an account with a support plan.
The client report's footnote says so, and its "Spend by kind of charge" section shows the
amounts, so a reader can see why. Do not move them into Include: they would land in the
usage figure and skew the totals and projections.

To see the size of these for a tenant, extract, then run `audit gemini-breakdown`
(below): the not-counted kinds are listed with their amounts, and a line says how much
higher the invoice for the period would be.

##### Changing the rules

Add a section to [`appsettings.json`](../src/Meterist.Cli/appsettings.json), or
set the matching environment variables. **If the section is present it replaces
the defaults entirely**, so list every rule you want, including the ones you are
keeping. If you set only `Include`, the NonUsage, Ignore and WatchTerms lists become
empty. An empty `Include` list is rejected when extraction starts, as is a NonUsage
rule without a Category.

A **Category** is a short label (1 to 64 characters, no leading or trailing spaces)
that you choose. The ones above are only the defaults; a new kind of charge Google
introduces needs a rule with its own label, not a code change or a migration. An
Include rule may leave the Category out: the kind is then guessed from the SKU text
(Subscription is a Seat, "Agent Platform" is AgentPlatform, and so on), which keeps an
older configuration working, but an explicit Category is clearer.

This is the complete default configuration, written out. Copy it as the starting
point for any change:

```json
{
  "Meterist": {
    "Gemini": {
      "BillingFilter": {
        "Include": [
          { "Service": "Vertex AI Search", "SkuLike": "%Subscription%", "Category": "Seat" },
          { "Service": "Vertex AI Search", "SkuLike": "%Enterprise%", "Category": "EnterpriseOverage" },
          { "Service": "Vertex AI", "SkuLike": "%Agent Platform%", "Category": "AgentPlatform" },
          { "Service": "Vertex AI", "SkuLike": "%Predictions%", "Category": "ModelUsage" },
          { "Service": "Vertex AI Search", "SkuLike": "%Pay as you go%", "Category": "PayAsYouGo" },
          { "Service": "Vertex AI Search", "SkuLike": "%Pay-as-you-go%", "Category": "PayAsYouGo" }
        ],
        "NonUsage": [
          { "Service": "Invoice", "SkuLike": "%Tax%", "Category": "Tax" },
          { "Service": "Invoice", "SkuLike": "%Rounding%", "Category": "Rounding" },
          { "Service": "Support", "SkuLike": "%", "Category": "Support" }
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
usage out of the totals, as in the first version of the filter:

```json
{
  "Meterist": {
    "Gemini": {
      "BillingFilter": {
        "Include": [
          { "Service": "Vertex AI Search", "SkuLike": "%Subscription%", "Category": "Seat" },
          { "Service": "Vertex AI Search", "SkuLike": "%Enterprise%", "Category": "EnterpriseOverage" },
          { "Service": "Vertex AI Search", "SkuLike": "%Pay as you go%", "Category": "PayAsYouGo" },
          { "Service": "Vertex AI Search", "SkuLike": "%Pay-as-you-go%", "Category": "PayAsYouGo" }
        ],
        "NonUsage": [
          { "Service": "Invoice", "SkuLike": "%Tax%", "Category": "Tax" },
          { "Service": "Invoice", "SkuLike": "%Rounding%", "Category": "Rounding" },
          { "Service": "Support", "SkuLike": "%", "Category": "Support" }
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
To count a new SKU Google introduces, add an Include rule for its service, a pattern
that matches its SKU name and a Category of your choice; the warning shows you the
exact service and SKU text to copy. To record a new non-usage charge, add a NonUsage
rule the same way.

##### Seeing the split by kind of charge

Each extraction stores, besides the daily totals, one row per day, kind of charge and SKU
(a "breakdown"), including the non-usage kinds. To see them:

```powershell
dotnet run --project src/Meterist.Cli -- audit gemini-breakdown --tenant <your-tenant-id> --from 2026-07-01 --to 2026-11-19
```

It is read-only. It lists each kind with whether it is in the totals, the number of days
it had a charge, its amount and the SKU names behind it. Below the table it checks that
the usage kinds add up to the stored gross spend for the same days and says so if they do
not (which means the breakdown and the daily records came from different extractions or
rules: re-run `extract` for that period), shows the not-counted total, and counts any days
that have spend but no breakdown rows. A kind worth under half a cent (Google's invoice
rounding line, for example) is not listed, since it would only read as a bare `$0.00`; a line
under the table names it, and it is still included in every total. The client report shows the same split in its
"Spend by kind of charge" section, for a vendor that has something beyond a seat fee or any
not-counted charge; the weekly figures and projections do not use it.

**Filling in history without calling BigQuery.** To build breakdown rows for days you
already extracted, run:

```powershell
dotnet run --project src/Meterist.Cli -- backfill spend-breakdown --tenant <your-tenant-id> --vendor gemini-enterprise --from 2026-07-01 --to 2026-11-19 --dry-run
```

It reads the raw records already stored, needs no credentials and no network, and
`--dry-run` shows the count without writing. Run it again without `--dry-run` to store
them. It is safe to repeat. It can only use what the raw records hold: days pulled before
tax and support lines were part of the query have none of them, so **re-run `extract`
for those days** to fetch them. A day with no stored raw record is left untouched.

Re-running `extract` replaces a day's breakdown rows with what the billing export says
now, so a line Google later revises or removes does not linger.

**Environment variables.** Arrays are numbered from zero and each field is its
own variable, with `__` as the separator. For example, the first Include rule is:

```powershell
$env:Meterist__Gemini__BillingFilter__Include__0__Service = "Vertex AI Search"
$env:Meterist__Gemini__BillingFilter__Include__0__SkuLike = "%Subscription%"
$env:Meterist__Gemini__BillingFilter__Include__0__Category = "Seat"
```

Environment variables merge with `appsettings.json` index by index, so a leftover
`Include__3` from an earlier experiment can survive; for anything beyond a quick
test, prefer editing the JSON.

**After you change the rules.** They apply to future extractions only. Re-run
`extract` for the earlier days you want recalculated. Gemini days are recalculated from the billing export on each pull, so re-extracting after narrowing or widening the rules gives the new figures and the new breakdown. To see the effect before anything is written, run `extract --dry-run` first.

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

**Where the cost comes from.** The extraction reads Anthropic's newer
`/analytics/cost` endpoint (beta) with the **same Admin API key**, grouped by who
the usage belongs to: a **person** where personal keys or Console sign-in are in
use, a **service account**, or a **workspace key** (which is linked to no
identity, so it is reported as the key itself and labelled that way). The daily
total counts all of it. Anthropic documents a lag of 4 to 24 hours for this
endpoint against about 5 minutes for the older `cost_report`, and a month's
figures can still be revised until about 7 days after it ends; the latest day is
therefore always incomplete, which a daily tool tolerates. Per-actor rows are
stored for `users usage` and the report's people sections; the stored daily record
is unchanged.

**Reconcile before you rely on it.** The new endpoint is beta. One month (October 2026) on one
tenant matched the stored legacy figures day for day when tested. Before relying on it for another
tenant, run `extract --dry-run` for a closed month: it reports every stored day that would change. A day
that differs is worth understanding first (Priority Tier commitment fees are billed separately and appear
in neither endpoint).

If Anthropic changes or ends the beta, the extraction fails with a message naming
the setting that goes back to the older `cost_report` (which has no per-actor
data and stores no per-user rows):

```json
{ "Meterist": { "ClaudeApiPlatform": { "UseLegacyCostReport": true } } }
```

or set the environment variable `Meterist__ClaudeApiPlatform__UseLegacyCostReport=true`
for one run. The setting applies to every tenant.

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
| **Failed** | Something went wrong (bad credential, network/auth failure, etc.) — the Detail column has the specific error. Includes a **failed read-back**: after a real (not `--dry-run`) write, Meterist reads back every day it wrote, and if one is missing the vendor fails with the dates, for example `could not read back 1 day(s) (2026-11-19)`. If you see that, the day was not saved: run the window again and check the database file's location and permissions. |

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
`unchanged`. "Succeeded" now means the days written were read back from the
database in the same run (see the **Failed** row above). That check runs inside
Meterist, so for an independent view use [`audit coverage`](#checking-for-missing-days),
or inspect the data yourself (see [Inspecting stored data](#troubleshooting)) from your own terminal:
a sandboxed or remote tool can show a stale copy of the database file.

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

### Seat drift and idle seats

```powershell
dotnet run --project src/Meterist.Cli -- audit seats --tenant <your-tenant-id>
dotnet run --project src/Meterist.Cli -- audit seats --tenant <your-tenant-id> --vendor chatgpt-enterprise --idle-days 45
```

`audit seats` answers two questions per tenant and vendor, for Claude Enterprise and ChatGPT Enterprise (any vendor with a
roster; Gemini will join when its roster lands). `--tenant` can be repeated, `--vendor` narrows it, `--as-of <date>` audits
an earlier day (default: today, UTC) and `--idle-days` sets the idle window (default 30). It only reads stored data and
never calls a vendor, so run `roster snapshot` and `extract` first.

- **Are we billed for the seats the vendor says we have?** It compares the **billed** seat count (the `SeatCount` on the
  seat rate in force that day, from `rates set`) with the vendor's own figure: the **assigned seats** from Claude's
  activity summary where there is one, otherwise the **roster** size. The rule depends on which figure it is. Billing
  follows **assigned seats**, so any difference from the billed count is **drift**: the rate card is stale (a seat was
  added or removed and nobody ran `rates set`) or the vendor changed something. A **roster** is only who has been
  provisioned, and the billed count may be a commitment larger than that (50 billed, 29 members), so fewer members than
  billed seats is reported as information (`21 of the 50 billed seats have no member on the roster`), **not drift**, and
  never sets the exit code. More members than billed seats is drift either way. Drift is flagged, never corrected: check the
  latest invoice, then run `rates set` (`activity show` lists Claude's assigned seats day by day, which dates the change).
  The roster size, the assigned count and the roster's seat types are shown alongside. If the roster and the assigned count
  disagree with each other, that is noted as two different measures, not called drift.
- **Which seats are idle?** A rostered member with no activity in the window. Activity is any stored per-user row for a
  person with spend, credits or **requests** above zero (requests count because cost is zero by design on a seat-based plan, so
  $0 never means inactive). For **Claude Enterprise** the vendor-reported per-person activity (`users activity`) counts too: a
  person with an activity row that day is active even at $0 cost, and a last-active day before the window is the later of the
  two sources. The coverage caveats below apply to the union of both. Usage is matched to the roster by the vendor's member id, then by email. Members added inside the
  window are counted as new, not idle. People who are active but not on the roster (left, or not matched) are counted
  separately.
- **It says when it cannot tell.** With no roster snapshot, or no per-user activity stored for the window, it says idle
  seats were not assessed instead of listing everyone. If stored usage starts partway through the window, or **ends more than
  three days before the audit date**, it still answers but with a caveat naming the date, because a seat active only in the
  missing stretch would be listed as idle (run `extract` to bring the data up to date). Any window from 1 to 3650 days is
  read in full; a longer or shorter `--idle-days` is rejected.
- **Exit code.** Non-zero on drift as defined above, so a scheduled run can alert; unused billed seats, idle seats and
  notes never set it.

The idle list names people, so keep the output out of anything you commit; it stays in your terminal or under `artifacts/`.

### Per-person activity (Claude Enterprise)

For **Claude Enterprise**, `extract` also stores what each person *did* each day (messages, Claude Code sessions, commits and
pull requests, Cowork sessions, Office add-in use, web searches), from the Analytics API's users endpoint with the same key
(nothing new to set up). It appears in the extract result as `user activity: 6 inserted, 40 unchanged`. Read it with:

```powershell
dotnet run --project src/Meterist.Cli -- users activity --tenant <your-tenant-id> --from 2026-11-01 --to 2026-11-20
dotnet run --project src/Meterist.Cli -- users activity --tenant <your-tenant-id> --from 2026-11-01 --to 2026-11-20 --top 10
```

- **Activity is not cost.** A person can be busy and cost nothing on a seat plan, and a chat message is not a model request
  (one message with tool use is several requests), so this sits beside `users usage` and does not replace it. The vendor's own
  daily active-user counts are in `activity show`.
- **One request per day, so it only asks for the days worth asking about.** The vendor serves a single day per request, so a
  daily run that re-reads a long window would otherwise make hundreds of requests. `extract` fetches the newest three days (the
  vendor revises recent days) and any day that has no stored rows while the organisation's active-user count says someone was
  active. A first extract over a long window fetches every active day once (a few minutes for a year); after that a daily run
  fetches about three. A day not published yet (the newest day arrives the next day, around 13:00 UTC) is skipped and picked up
  later. Nothing is lost by running `extract` often.
- **Everything the vendor sent is kept.** The headline counts are columns; the full per-product metrics (including products
  with no column) are stored as JSON with each row, so a count promoted to a column later needs no new call to the vendor.
- **A failure marks the vendor failed.** The spend is already saved by then, so nothing is lost, but the run exits non-zero and
  says why (for example a key without the `read:analytics` scope).

The output names people (by email), so keep it out of anything you commit.

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

### Rosters: who holds a seat

A roster is a vendor's member list. Spend and usage say what people did; the roster says who *has* a seat, which is
what you need to spot idle seats later. It works for **Claude Enterprise** and **ChatGPT Enterprise**, using the same key your spend
extraction already stores (nothing new to set up), with two exceptions: a ChatGPT tenant's stored credential must include its
`WorkspaceId` (the same one the Daily Usage API needs; without it the snapshot stops with a message saying so). Use
`--vendor claude-enterprise` or `--vendor chatgpt-enterprise` in the commands below. `roster show` includes each member's seat
type (ChatGPT: `chatgpt` or `codex`) and whether they are managed by your identity provider (SCIM). Neither vendor reports a
last-active date, so who is idle is worked out later by comparing the roster with usage.

**Gemini Enterprise** has a roster too (`--vendor gemini-enterprise`), but only once its credential carries the Discovery
Engine settings described in the [Gemini section](#gemini-enterprise); until then `roster snapshot-all` shows it as
*Skipped*. Its list is a **licence list**, not just seat holders: each person's seat type is their licence state
(`ASSIGNED`, `NO_LICENSE`, `NO_LICENSE_ATTEMPTED_LOGIN`, `BLOCKED`). Only `ASSIGNED` people count as seats in `audit seats`
and can be idle; people with no licence are listed in the roster and in the audit's seat-type breakdown but are not
seats, and the audit notes how many **tried to sign in without a licence** (demand your licences do not cover). Google's
list also carries a last-login time per person; Meterist does not store it yet. A licence list can legitimately be empty (nobody
holds a licence or has tried to sign in yet): that is reported as a success with "the list is empty, so nothing was stored", not as a
failure, and no snapshot is written, so an earlier snapshot stays the latest (other vendors always have an owner, so an empty list
from them is still refused as a likely key-scope problem).

```powershell
dotnet run --project src/Meterist.Cli -- roster snapshot --tenant <your-tenant-id> --vendor claude-enterprise --dry-run
dotnet run --project src/Meterist.Cli -- roster snapshot --tenant <your-tenant-id> --vendor claude-enterprise
dotnet run --project src/Meterist.Cli -- roster show --tenant <your-tenant-id> --vendor claude-enterprise
```

- **`roster snapshot`** fetches the member list and stores it as a snapshot dated today (UTC). `--dry-run` shows the
  member count by role and what would change, writing nothing. Taking another snapshot the same day replaces that
  day's; earlier days are kept.
- **`roster show`** prints a stored snapshot, by name and email with each member's role and when they were added, and
  says how many snapshots are stored. `--on-or-before 2026-10-31` shows the latest one taken on or before a date.
- **Take snapshots regularly.** Vendors keep no membership history, so a snapshot is the only record of who was on
  the list that day. The [daily scheduled run](#running-extraction-on-a-schedule) takes them for you, for every tenant,
  right after extraction and the coverage check.
- **`roster snapshot-all --tenant a --tenant b [--dry-run]`** is what that scheduled run calls. For each tenant it
  snapshots every vendor that has a member list **and** that the tenant has a stored credential for, and shows the rest
  as `Skipped` (a tenant that uses only some vendors is not a failure). One vendor or tenant failing does not stop the
  others. It exits `1` if any snapshot failed, `0` otherwise.
- **It stops rather than store a wrong list.** If the member list cannot be read in full (a cursor problem, a member
  with no id) or comes back empty, or the vendor is unavailable (a rate limit, a server error, a timeout, an
  unreadable response), the command prints a message saying so, exits non-zero and stores nothing, because a partial
  snapshot would look like people leaving. A `401`/`403` means the stored key lacks the `read:members` (or `read:org_audit`)
  scope.

The output names people, so keep it out of anything you commit; it stays in the database and under `artifacts/`.

### Active users and seat counts (Claude Enterprise)

For **Claude Enterprise**, `extract` also stores the vendor's own daily active-user and seat counts, from the Analytics
API's summaries endpoint, with the same Analytics key (nothing new to set up). They appear in the extract result as
`activity summaries: 12 inserted, 300 unchanged`, and `--dry-run` shows what would change without writing it.

```powershell
dotnet run --project src/Meterist.Cli -- activity show --tenant <your-tenant-id> --vendor claude-enterprise --from 2026-11-01 --to 2026-11-19
dotnet run --project src/Meterist.Cli -- activity show --tenant <your-tenant-id> --vendor claude-enterprise --from 2026-11-01 --to 2026-11-19 --product claude_code
```

- **What you get:** per day, how many people were active (daily, weekly and monthly), how many seats were assigned, how
  many invitations are pending, and the daily adoption rate (active users as a percentage of assigned seats). With
  `--product` the same counts for one product (`chat`, `claude_code`, `cowork`, and so on; `activity show` lists
  nothing for a product the tenant does not use). These are Anthropic's figures, counts only, with no names.
- **The newest day arrives a day late.** Anthropic publishes a day's summary the next day (about 13:00 UTC), so an
  extract run today stores activity up to yesterday. A later run picks up the rest; nothing is lost.
- **Data starts on 2026-01-01.** An earlier `--from` is clamped to that day.
- **Seat counts are history you cannot get back later.** `assigned_seat_count` is stored per day, so it shows seat
  changes over time, which the roster (a snapshot of who holds a seat today) does not.
- **A failure here marks the vendor failed.** The spend is already saved by then, so nothing is lost, but the run
  exits non-zero and says why (for example a key without the `read:analytics` scope).

### Spend limits and headroom (Claude Enterprise and ChatGPT Enterprise)

Both vendors let an organisation cap each member's monthly spend. Meterist reads each member's effective limit and what they
have spent against it, with the key your spend extraction already stores (the live tests found it carries the needed scope,
so nothing new to set up). Use `--vendor claude-enterprise` or `--vendor chatgpt-enterprise` in the commands below.

- **Claude Enterprise** uses the Spend Limits API, which needs usage credits turned on and the key's `read:spend_limits` scope.
  Amounts are in US dollars.
- **ChatGPT Enterprise** uses Spend Controls (read-only; Meterist never changes a limit), which needs the key's
  `chatgpt.enterprise.usage_limit.read` scope and the credential's `WorkspaceId`. Amounts are in the workspace's own unit:
  **credits** for a credit-billed workspace (both live tenants), shown as `credit` and **not converted to dollars**, or `usd`.
  Where the vendor reports it, `limits show` adds a **Last month** column; it was not available on either tenant, and
  unavailable is shown as `-`, never as zero. The source shows where a limit comes from: `individual_override`,
  `group_default` (with the group name), `workspace_default` or `role_based_personal_budget`.

```powershell
dotnet run --project src/Meterist.Cli -- limits snapshot --tenant <your-tenant-id> --vendor claude-enterprise --dry-run
dotnet run --project src/Meterist.Cli -- limits snapshot --tenant <your-tenant-id> --vendor claude-enterprise
dotnet run --project src/Meterist.Cli -- limits show --tenant <your-tenant-id> --vendor claude-enterprise
```

- **`limits snapshot`** fetches every member's limit and stores it as a snapshot dated today (UTC). `--dry-run` shows the
  counts and what would change, writing nothing. The vendor keeps no history of these figures, so a snapshot is the only
  record of where a member stood that day; taking another the same day replaces that day's, and earlier days are kept.
- **`limits show`** prints the latest stored snapshot as headroom: limit, spent, share used, amount left, and where the
  limit comes from (a limit set for the member, or inherited from a group, a seat tier or the organisation), most used first.
  Members with **no limit** are listed last and show `no limit`. A limit of **zero** is a limit, not the absence of one: it is
  shown as `0.00`, with the amount left as zero (negative once anything is spent), `n/a` for the share used (a share of zero is
  undefined), and ranked at the top once the member has spent anything. `--on-or-before 2026-10-31` shows an earlier snapshot.
- **A headroom signal, not a ledger.** Anthropic calls the spend figure informational: it can read zero when unavailable
  and can differ from the cost reports (for one Claude tenant it matched to a fraction of a cent, for another it was several
  percent higher). Use the cost reports for money, and this to see who is near a cap.
- **It stops rather than store a wrong list.** If the list cannot be read in full, or comes back empty (which usually means
  usage credits are off or the key lacks the scope), the command says so, exits non-zero and stores nothing. A `401`/`403` means
  the key lacks `read:spend_limits` or usage credits are not enabled.
- **`limits snapshot-all --tenant a --tenant b [--dry-run]`** is what the [daily scheduled run](#running-extraction-on-a-schedule)
  calls. For each tenant it snapshots every vendor that has spend limits **and** that the tenant has a stored credential for,
  and shows the rest as `Skipped`. One vendor or tenant failing does not stop the others, and each tenant has its own database
  context. It exits `1` if any snapshot failed (a key the vendor rejects is a failure, not a skip), `0` otherwise.

The output names people, so keep it out of anything you commit.

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
window, then takes each tenant's [roster snapshots](#rosters-who-holds-a-seat) and
[spend-limit snapshots](#spend-limits-and-headroom-claude-enterprise-and-chatgpt-enterprise),
and writes everything to `%LOCALAPPDATA%\Meterist\logs\extract-yyyy-MM-dd.log`
(a second run the same day appends).

Its exit code, shown as the task's "Last Run Result", is `0` when everything is
clean. Otherwise it is the **sum** of what failed, so the number tells you exactly
what went wrong:

| Failure | Adds |
| --- | --- |
| A vendor extraction failed | `1` |
| The coverage check found missing days | `2` |
| A roster snapshot failed | `4` |
| A spend-limit snapshot failed | `8` |

So `1`, `2`, `4` and `8` are each one problem alone, `3` is extraction and coverage, `5`
is extraction and roster, `12` is roster and spend limits, `15` is all four, and so on:
add up the rows that apply. The roster and spend-limit snapshots run even when extraction
failed, because they are independent of it and a day without a snapshot cannot be
reconstructed later. Before roster snapshots the codes were only `0` to `3`, and before
spend-limit snapshots `0` to `7`; those mean the same as before.

**What counts as a spend-limit failure.** Only a vendor with a spend-limit source and a stored
credential is attempted; one without a credential is skipped, not failed. A key the vendor
**rejects** for spend limits (Claude: no `read:spend_limits` scope, or usage credits not turned on;
ChatGPT: no usage-limit scope or no `WorkspaceId`) is a **failure**, not a skip. That is deliberate:
these figures exist only as a point in time and cannot be backfilled, so quietly skipping a tenant
that cannot be read would lose days nobody can recover and nobody would notice. The message names
the scope to check. If a tenant genuinely has no spend limits to read, leave it out of `-Tenants` or
do not store a credential for that vendor.

**`audit seats` is not in the run.** Seat drift is expected for a day or two after any seat
change (the billed count changes the day you set the rate, the vendor's figure a day or two later),
which would set a failure code for no real fault, and the idle list names people, which should not go
into a log file. Run `audit seats` by hand when you change seats and now and then.

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

- **"The stored credentials for tenant ... could not be read."** The tenant has a credential file, but it will not
  decrypt (or is damaged). Credentials are encrypted for **one Windows account on one machine**, so this almost always
  means Meterist is running as a different user (a scheduled task must run as the account that stored them) or on a
  different machine. It is reported as a **failure**, never as "no credential": extraction shows the vendor as
  `Failed`, roster snapshots as `Failed`, and the scheduled run exits non-zero, so a run can never look clean while
  collecting nothing. Run as the original account, or store each vendor's credential again with `credentials set`
  (a `set` always works, even over an unreadable file; any other vendor's credential in that file is lost and must be
  set again).
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
