# Database Schema Reference

**Engine:** SQLite (v1 — see `architecture.md` §7 for the planned migration
to a server-based RDBMS once hosted).
**File location:** `%LOCALAPPDATA%\Meterist\meterist.db`.
**Created/versioned by:** EF Core migrations in
`src/Meterist.Data/Migrations/` — as of this writing, eight migrations:
`20260722033812_InitialCreate`, `20261119172104_AddDailySpendRecordSource`,
`20261120160643_AddUserDailyUsageRecords`, `20261120183237_AddDailySpendBreakdownRecords`,
`20261120193404_AddMemberRosterRecords`, `20261121063137_AddMemberRosterIsScimManaged` and
`20261121065711_AddActivitySummaryRecords` and `20261121071447_AddSpendLimitRecords`.

This document is generated from the **live database schema**, not from the
C# model or migration source alone — those can drift from what's actually on
disk. To verify this doc hasn't gone stale, run `scripts/DumpSchema.cmd` and
diff its output against the [Appendix](#appendix-verbatim-schema-dump) below.

## Vendor ID reference

`VendorId` columns store one of these four fixed GUIDs (`VendorCatalog.cs`)
— not obviously memorable, so keep this table handy when reading raw rows:

| VendorId | Vendor |
|---|---|
| `8f14e45f-ceea-467e-adde-3f82edcd1a11` | Claude Enterprise |
| `c9e1a1a0-3b1e-4b8a-9d9e-2f6a1b0c9d22` | Claude API Platform |
| `3d6f1c2e-8a4b-4e3a-8b2f-7c5e9a1d4f33` | ChatGPT Enterprise |
| `a27b6d3c-1f9e-4a7d-9c3b-6e2d8f0a5b44` | Gemini Enterprise |

SQLite stores GUIDs as their uppercase text form — always compare with
`UPPER(VendorId) = UPPER('<guid>')` in ad-hoc SQL (see `scripts/QueryAllTotals.cmd`
for the established pattern).

## Tables

### `DailySpendRecords`

The canonical stored grain: one row per tenant/vendor/day, regardless of
that vendor's native API shape. Weekly, monthly, and annual figures are
query-time aggregations over this table, not separately stored.

| Column | Type (SQLite) | Nullable | Notes |
|---|---|---|---|
| `Id` | `INTEGER` | No | Surrogate PK, `AUTOINCREMENT`. Not part of the C# model — added as an EF Core shadow property so the domain model (`Meterist.Core.Models.DailySpendRecord`) stays persistence-agnostic. |
| `TenantId` | `TEXT` | No | |
| `VendorId` | `TEXT` | No | One of the four GUIDs above. |
| `Date` | `TEXT` | No | ISO `yyyy-MM-dd` (`DateOnly`). |
| `SeatFee` | `TEXT` | No | `decimal(18,4)` — see [Decimal storage](#decimal-storage). Legitimately `0` for Claude API Platform always (no seat concept) and for any vendor/tenant pair with no seat rate configured. |
| `UsageOrOverage` | `TEXT` | No | `decimal(18,4)`. Legitimately `0` for Claude Enterprise when the tenant doesn't have "usage credits" enabled — expected, not a data gap. |
| `GrossSpend` | `TEXT` | No | `decimal(18,4)` — `SeatFee + UsageOrOverage`. |
| `CreditsApplied` | `TEXT` | No | `decimal(18,4)` — promo/credit-grant offsets, currently always `0` in practice (no vendor adapter populates this yet). |
| `NetSpend` | `TEXT` | No | `decimal(18,4)` — `GrossSpend - CreditsApplied`. |
| `Source` | `TEXT` | Yes | Which data source supplied the day, for vendors with more than one, so reports can disclose it. Values (`DailySpendSources` in `Meterist.Core.Models`): `costs-export` (ChatGPT Enterprise's COSTS compliance-log export) or `daily-usage-api` (its Daily Usage API). `NULL` for vendors with a single source. Added by `AddDailySpendRecordSource`, which labelled every existing ChatGPT Enterprise row `costs-export` (all of them came from that export). When the never-lower rule keeps an earlier, higher usage figure, `Source` stays the source of that figure. |

**Primary key:** `Id` (surrogate).
**Natural key / unique index:** `IX_DailySpendRecords_TenantId_VendorId_Date`
on `(TenantId, VendorId, Date)`, **unique**. Re-extracting an already-stored
day is an upsert against this index — the natural key doubles as the
overlapping-extraction-window handling, with no special-case merge logic
needed.

### `UserDailyUsageRecords`

One user's usage for one day on one vendor, the per-user companion of
`DailySpendRecords` (`Meterist.Core.Models.UserDailyUsageRecord`). Added by
`AddUserDailyUsageRecords`. **ChatGPT Enterprise populates it** (issue #17), from
the Daily Usage API when the tenant's credential has a `WorkspaceId`, and **Claude
Enterprise** (issue #18), from the analytics cost rows or, with `backfill user-usage`,
from raw records already stored.

A row exists only for a user-day with usage, because the vendors return
nothing for idle users. So no row means no usage, and the number of active
users on a day is the number of distinct `UserId`s.

| Column | Type (SQLite) | Nullable | Notes |
|---|---|---|---|
| `Id` | `INTEGER` | No | Surrogate PK, `AUTOINCREMENT`. Shadow property, same rationale as above. |
| `TenantId` | `TEXT` | No | |
| `VendorId` | `TEXT` | No | |
| `Date` | `TEXT` | No | ISO `yyyy-MM-dd`. |
| `UserId` | `TEXT` | No | The vendor's stable identifier for the user, or for the API key when the vendor attributes to a key. Max 256. Part of the natural key, so it is an id, not an email address that can be renamed. |
| `UserEmail` | `TEXT` | Yes | Max 320. **Personal data.** |
| `UserName` | `TEXT` | Yes | Max 256. **Personal data.** |
| `Amount` | `TEXT` | No | `decimal(18,4)`, USD. Vendor-reported where the vendor gives a dollar figure, otherwise derived (then `IsEstimated` is `1`). |
| `Credits` | `TEXT` | Yes | `decimal(18,4)`. Credits consumed, for vendors that bill in credits. `NULL` where the vendor has no such concept. |
| `Requests` | `INTEGER` | Yes | Requests or messages, whichever the vendor reports. `NULL` where no count is reported. |
| `IsEstimated` | `INTEGER` | No | `1` when `Amount` is derived rather than vendor-reported (for example credits converted at a rate, even a contracted one); reports must label it. |
| `Attribution` | `TEXT` | No | `Person`, `ApiKey`, `Allocated` or `Unrecognized` (`UsageAttribution`), stored as text so it is readable in SQL and reordering the enum cannot relabel history. `ApiKey`: the vendor reported the usage against a key, not a person. `Allocated`: Meterist divided an aggregate across users. `Unrecognized`: an actor type Meterist does not know; stored so totals reconcile, not counted as a person. |

**Primary key:** `Id` (surrogate).
**Natural key / unique index:** `IX_UserDailyUsageRecords_TenantId_VendorId_Date_UserId`
on `(TenantId, VendorId, Date, UserId)`, **unique**. Storing a user-day already
stored is an upsert against this index, replacing every field. Rows absent from a
later pull are **not** deleted: the vendors return only users with usage, so an
absent row may just be a window edge. A single batch holding two rows for one key
is rejected.

**Personal data.** Users are named in reports by operator decision, so this table
holds names and emails. Keep them out of logs at Information level and above and
out of anything committed to the repository; populated reports stay under
`artifacts/`. The model is a class with the default `ToString`, and the
repository's error messages name only the date, so an accidental log line does
not print a person.

### `DailySpendBreakdownRecords`

The per-kind split behind a day's spend (`Meterist.Core.Models.DailySpendBreakdownRecord`): one
row per tenant, vendor, day, kind of charge and line. Added by `AddDailySpendBreakdownRecords`;
**Gemini Enterprise populates it** (issue #53) on every extraction, from the configured billing rules,
or with `backfill spend-breakdown` from raw records already stored. Rows exist only where a charge
exists, so a new kind of charge a vendor introduces is a new row, not a migration.

| Column | Type (SQLite) | Nullable | Notes |
|---|---|---|---|
| `Id` | `INTEGER` | No | Surrogate PK, `AUTOINCREMENT`. Shadow property, same rationale as above. |
| `TenantId` | `TEXT` | No | |
| `VendorId` | `TEXT` | No | |
| `Date` | `TEXT` | No | ISO `yyyy-MM-dd`. |
| `Category` | `TEXT` | No | The kind of charge, as configured. Max 64. A free-form label rather than an enum, so configuration can add one. For Gemini: `Seat`, `EnterpriseOverage`, `AgentPlatform`, `ModelUsage`, `PayAsYouGo`, `Other` (usage) and `Tax`, `Support`, `Rounding` (not usage). `Seat` is the part of a day's spend that is the daily record's `SeatFee`. |
| `Detail` | `TEXT` | No | The line within the category, usually the vendor's SKU text, so a specific model or plan stays visible. Max 256. Empty string (never `NULL`, so the key is enforceable) when a category has no finer line. |
| `Amount` | `TEXT` | No | `decimal(18,4)`, USD, **before credits** (as `GrossSpend` is). Never zero: zero-cost lines are not stored. |
| `IsUsage` | `INTEGER` | No | `1` when the amount is counted in the daily record's spend; `0` for tax, support and rounding, which are kept to explain the gap to a vendor's invoice and are **never part of any total**. |

**Primary key:** `Id` (surrogate).
**Natural key / unique index:** `IX_DailySpendBreakdownRecords_TenantId_VendorId_Date_Category_Detail`
on `(TenantId, VendorId, Date, Category, Detail)`, **unique**.

**Invariant:** for a day, the rows with `IsUsage = 1` add up to that day's `DailySpendRecords.GrossSpend`.
The Gemini normalizers guarantee it by reading the same raw rows; `audit gemini-breakdown` checks it.

**Written by replacing, not upserting.** An extraction states which days its pull covered and the stored rows
for exactly those days become the batch: new keys inserted, changed amounts updated, and rows on a covered day
that are absent from the batch **removed** (Google can revise or drop a line). Days the batch does not cover
are never touched, and a batch that is malformed (two rows for one key, a row for another tenant or vendor or
an uncovered day, a blank category) is rejected before anything is staged.

### `MemberRosterRecords`

A vendor's member list as it stood on a day (`Meterist.Core.Models.MemberRosterRecord`): one row per tenant,
vendor, snapshot date and member. Added by `AddMemberRosterRecords` (and `IsScimManaged` by
`AddMemberRosterIsScimManaged`); **Claude Enterprise** (issue #30) and **ChatGPT Enterprise** (issue #28) populate it with
`roster snapshot`, and the scheduled run does so for both.

A roster is what a tenant *has* (who holds a seat), not what people *did*. Snapshots are dated and kept, because
the vendors expose no membership history: a snapshot is the only record of who was on the list that day.

| Column | Type (SQLite) | Nullable | Notes |
|---|---|---|---|
| `Id` | `INTEGER` | No | Surrogate PK, `AUTOINCREMENT`. Shadow property, same rationale as above. |
| `TenantId` | `TEXT` | No | |
| `VendorId` | `TEXT` | No | |
| `SnapshotDate` | `TEXT` | No | ISO `yyyy-MM-dd`, the **UTC** date the snapshot was taken. |
| `MemberId` | `TEXT` | No | The vendor's stable member id (max 256). Part of the natural key, so not an email address. |
| `Email` | `TEXT` | Yes | Max 320. **Personal data.** |
| `Name` | `TEXT` | Yes | Max 256. **Personal data.** |
| `Role` | `TEXT` | Yes | The vendor's own role name (Claude: `user`, `managed`, `owner`, `membership_admin`, `primary_owner`). Max 64. |
| `SeatType` | `TEXT` | Yes | The seat or licence type, for vendors that report one: ChatGPT (`chatgpt` or `codex`). Claude does not, so it is `NULL`. Max 64. |
| `IsScimManaged` | `INTEGER` | Yes | `1` when the member is managed by the organisation's identity provider (SCIM), which the vendor then will not let an API change; `0` when not; `NULL` when the vendor does not say (Claude). `NULL` is not `false`. |
| `AddedAt` | `TEXT` | Yes | When the member joined, as the vendor reports it (ISO 8601, UTC). |

**Primary key:** `Id` (surrogate).
**Natural key / unique index:** `IX_MemberRosterRecords_TenantId_VendorId_SnapshotDate_MemberId` on
`(TenantId, VendorId, SnapshotDate, MemberId)`, **unique**.

**Stored as a whole.** Taking a snapshot replaces that day's rows exactly: new members added, changed fields
updated, and members absent from the new list **removed** from that day's snapshot. Other days are never touched.
A batch with the same member id twice, a row for another tenant, vendor or date, or a blank id is rejected before
anything is staged, and an empty list is refused by the service rather than stored as "everyone left".

**Personal data.** Names and emails stay in the database and in output under `artifacts/`, out of logs and out
of anything committed. The model is a class with the default `ToString`, and error messages name no member.

### `ActivitySummaryRecords`

The vendor's own daily active-user and seat counts (`Meterist.Core.Models.ActivitySummaryRecord`): one row per tenant,
vendor, day and product. Added by `AddActivitySummaryRecords`; **Claude Enterprise** (issue #22, from the Analytics API's
`/analytics/summaries`) populates it during `extract`. **Aggregate counts only: no names or emails.** Use `activity show` to read it.

One row per day with `Product = 'all'` for the whole organisation, plus one per product the vendor breaks out
(Claude: `chat`, `claude_code`, `cowork`, `chat_cowork_unified`, `office_agent`, `claude_design`, `science`, ...). Products are
rows rather than columns so one the vendor adds later needs no migration. A product the API reports as null (one the
organisation does not have) gets no row.

| Column | Type (SQLite) | Nullable | Notes |
|---|---|---|---|
| `Id` | `INTEGER` | No | Surrogate PK, `AUTOINCREMENT`. Shadow property. |
| `TenantId` | `TEXT` | No | |
| `VendorId` | `TEXT` | No | |
| `Date` | `TEXT` | No | ISO `yyyy-MM-dd`, the UTC day the counts are for. |
| `Product` | `TEXT` | No | `all` for the whole organisation, otherwise the product name (max 64). Never null, so the key is enforceable. |
| `DailyActiveUsers` | `INTEGER` | Yes | Distinct active users that day. |
| `WeeklyActiveUsers` | `INTEGER` | Yes | Trailing window ending on `Date`, as the vendor defines it. |
| `MonthlyActiveUsers` | `INTEGER` | Yes | Likewise. |
| `AssignedSeatCount` | `INTEGER` | Yes | Seats assigned that day. Organisation-wide, so only on the `all` row. |
| `PendingInviteCount` | `INTEGER` | Yes | Invitations not yet accepted. `all` row only. |
| `DailyAdoptionRate` | `TEXT` | Yes | Percent of assigned seats active (`30` means 30%), as the vendor reports it. `all` row only. |
| `WeeklyAdoptionRate` | `TEXT` | Yes | Likewise. |
| `MonthlyAdoptionRate` | `TEXT` | Yes | Likewise. |

**Primary key:** `Id` (surrogate).
**Natural key / unique index:** `IX_ActivitySummaryRecords_TenantId_VendorId_Date_Product` on
`(TenantId, VendorId, Date, Product)`, **unique**.

**Upserted, never deleted.** Re-extracting a day replaces its figures; a day absent from a later pull is left alone,
because the vendor publishes the newest day the next day and an absent row does not mean no activity. The first
`extract` after a day is published is what stores it, so a day shows up in this table a day later than its spend.

### `SpendLimitRecords`

Each member's spend limit and what they had spent against it, as the vendor reported it on a day
(`Meterist.Core.Models.SpendLimitRecord`): one row per tenant, vendor, snapshot date and member. Added by
`AddSpendLimitRecords`; **Claude Enterprise** (issue #26, from the Spend Limits API) populates it with `limits snapshot`.
Snapshots are dated and kept, because the vendor exposes no history; one per day shows how fast a member is using their
limit. Read it with `limits show`.

The figures are a **headroom signal, not a ledger**: the vendor calls the spend to date informational. Live-tested, the
sum of members' spend equalled the cost report for one tenant and was several percent higher for another.

| Column | Type (SQLite) | Nullable | Notes |
|---|---|---|---|
| `Id` | `INTEGER` | No | Surrogate PK, `AUTOINCREMENT`. Shadow property. |
| `TenantId` | `TEXT` | No | |
| `VendorId` | `TEXT` | No | |
| `SnapshotDate` | `TEXT` | No | ISO `yyyy-MM-dd`, the **UTC** date the snapshot was taken. |
| `MemberId` | `TEXT` | No | The vendor's stable member id (max 256). Part of the natural key. |
| `Email` | `TEXT` | Yes | Max 320. **Personal data.** |
| `Name` | `TEXT` | Yes | Max 256. **Personal data.** |
| `LimitAmount` | `TEXT` | Yes | The limit for the period, in dollars (the API sends cents; stored divided by 100). **`NULL` means the member has no limit, not zero.** |
| `PeriodToDateSpend` | `TEXT` | Yes | Spend against the limit this period, in dollars, informational. |
| `Currency` | `TEXT` | Yes | As the vendor states it (`USD`). Max 16. |
| `Period` | `TEXT` | Yes | The period the limit applies to (`monthly`). Max 32. |
| `SourceType` | `TEXT` | Yes | Where the limit comes from: `user` (set for this member), `rbac_group`, `seat_tier` or `organization`. Max 64. |
| `SourceDetail` | `TEXT` | Yes | The group id or seat tier name it is inherited from; `NULL` for a per-member limit. Max 256. |
| `SpendLimitId` | `TEXT` | Yes | The vendor's id for the limit. Max 256. |

**Primary key:** `Id` (surrogate).
**Natural key / unique index:** `IX_SpendLimitRecords_TenantId_VendorId_SnapshotDate_MemberId` on
`(TenantId, VendorId, SnapshotDate, MemberId)`, **unique**.

**Stored as a whole,** like the roster: retaking a snapshot replaces that day's rows exactly (members absent from the new
list are removed from that day), other days are never touched, a malformed batch is rejected before anything is staged, and
an empty list is refused rather than stored. **Personal data:** names and emails stay in the database and out of logs and
anything committed.

### `RawDailyExtractionRecords`

Raw, unnormalized vendor API responses, kept alongside the normalized
`DailySpendRecords` for debugging/re-normalization without a fresh API pull.

| Column | Type (SQLite) | Nullable | Notes |
|---|---|---|---|
| `Id` | `INTEGER` | No | Surrogate PK, `AUTOINCREMENT`. Shadow property, same rationale as above. |
| `TenantId` | `TEXT` | No | |
| `VendorId` | `TEXT` | No | |
| `Date` | `TEXT` | No | ISO `yyyy-MM-dd`. |
| `ExtractedAtUtc` | `TEXT` | No | ISO 8601 UTC timestamp of the pull that produced this row. |
| `RecordsJson` | `TEXT` | No | The vendor's raw response rows for this day, serialized as JSON. Shape varies per vendor — not normalized. |

**Primary key:** `Id` (surrogate).
**Natural key / unique index:** `IX_RawDailyExtractionRecords_TenantId_VendorId_Date`
on `(TenantId, VendorId, Date)`, **unique**. This table is **latest-pull-per-day**,
not a full audit history — a re-extraction overwrites the prior raw payload
for that day rather than appending a new row (see
`IRawExtractionRepository`'s doc comment for the scope note).

### `VendorRateConfigs`

Versioned, tenant-scoped rate configuration — seat fees, credit-to-USD
conversion rates, etc. Exists because several vendor APIs don't return a
seat/subscription line or a reliable dollar figure at all (ChatGPT
Enterprise's `COSTS` export, for one), so that piece has to come from
configuration instead of the API response.

| Column | Type (SQLite) | Nullable | Notes |
|---|---|---|---|
| `Id` | `INTEGER` | No | Surrogate PK, `AUTOINCREMENT`. Shadow property, same rationale as above. |
| `TenantId` | `TEXT` | **Yes** | `NULL` means a **public default** rate applied to any tenant without its own override — not a missing value. |
| `VendorId` | `TEXT` | No | |
| `RateType` | `TEXT` | No | e.g. `per-seat`, `credit-to-usd`. Free-form string, not an enum — normalizers look it up by literal value (see `ChatGptRateKeys`/`ClaudeEnterpriseRateKeys`). |
| `ModelOrSku` | `TEXT` | **Yes** | e.g. `seat`, `credit-usd`. Scopes a rate to a specific line item within a vendor; `NULL` is a valid bucket value in its own right, not "unset." |
| `Rate` | `TEXT` | No | `decimal(18,6)` — six decimal places (finer than the spend tables' four) since some conversion rates, like $/credit, are sub-cent. |
| `SeatCount` | `INTEGER` | Yes | Used to prorate a per-seat monthly/annual rate into a daily `SeatFee`. |
| `BillingCadence` | `INTEGER` | Yes | Enum stored as its underlying int: `0` = `Monthly`, `1` = `Annual`, `2` = `OneTime`. |
| `EffectiveFrom` | `TEXT` | No | ISO `yyyy-MM-dd`. |
| `EffectiveTo` | `TEXT` | Yes | ISO `yyyy-MM-dd`, or `NULL` for open-ended (still the current rate). |

**Primary key:** `Id` (surrogate).
**Index:** `IX_VendorRateConfigs_TenantId_VendorId_ModelOrSku_EffectiveFrom`
on `(TenantId, VendorId, ModelOrSku, EffectiveFrom)`, **intentionally
non-unique** — standard SQL unique indexes don't enforce uniqueness across
`NULL` values the way the public-default-`TenantId` business rule would
need, so this is a query-performance index only, not a constraint.
**Nothing in the schema prevents two rows for the same scope from having
overlapping `[EffectiveFrom, EffectiveTo]` windows** — that's enforced at
the application layer instead, by `rates set`'s `CloseOpenEndedRateAsync`
(auto-closes a superseded open-ended row) and `FindOverlappingRatesAsync`
(rejects an insert that would still collide after that), both in
`EfVendorRateConfigRepository`.

### EF Core bookkeeping tables

`__EFMigrationsHistory` (applied migration IDs) and `__EFMigrationsLock`
(prevents two concurrent `dotnet ef database update` runs) are managed
entirely by EF Core's migration machinery — never queried or written to
directly by application code. `sqlite_sequence` is SQLite's own internal
bookkeeping for `AUTOINCREMENT` columns.

## Decimal storage

SQLite has no native `DECIMAL` type. EF Core's SQLite provider stores
`decimal` properties as `TEXT` to preserve exact precision (rather than
`REAL`, which is floating-point and would lose it). Each decimal column has
an explicit `HasPrecision(...)` in `MeteristDbContext.OnModelCreating` —
deliberately, so that migrating to a server-based RDBMS later (`architecture.md`
§7) is a provider swap, not a data-shape rewrite.

## Appendix: verbatim schema dump

Captured via `scripts/DumpSchema.cmd` (`sqlite3 <db> ".schema"`) against the
live database, 2026-11-19 (after `AddDailySpendRecordSource` was applied). The
`UserDailyUsageRecords` and `DailySpendBreakdownRecords` tables and their indexes at the end
were added afterwards (as were `MemberRosterRecords`, `ActivitySummaryRecords` and `SpendLimitRecords`), taken from a freshly migrated empty database,
not the live file; regenerate the whole dump from the live database once
`AddMemberRosterRecords` has been applied there:

> **Why `MemberRosterRecords` also ends with `, "IsScimManaged" INTEGER NULL);` on its own line:** the same reason as
> `DailySpendRecords` below: the column was added by a later migration, so the real schema shows it spliced in before the
> closing parenthesis. The dump here was made from a freshly migrated database, which does the same.
>
> **Why `DailySpendRecords` ends with `, "Source" TEXT NULL);` on its own
> line.** That is not a typo and not hand-edited: it is exactly how SQLite
> records a column added after a table was created (`ALTER TABLE ... ADD
> COLUMN` splices the new column in before the closing parenthesis). It is
> valid SQL, and it is kept verbatim so a diff against `DumpSchema.cmd`
> output stays meaningful. Columns that were part of a table's original
> `CREATE TABLE` don't look like this.

```sql
CREATE TABLE IF NOT EXISTS "__EFMigrationsLock" (
    "Id" INTEGER NOT NULL CONSTRAINT "PK___EFMigrationsLock" PRIMARY KEY,
    "Timestamp" TEXT NOT NULL
);
CREATE TABLE IF NOT EXISTS "__EFMigrationsHistory" (
    "MigrationId" TEXT NOT NULL CONSTRAINT "PK___EFMigrationsHistory" PRIMARY KEY,
    "ProductVersion" TEXT NOT NULL
);
CREATE TABLE IF NOT EXISTS "DailySpendRecords" (
    "Id" INTEGER NOT NULL CONSTRAINT "PK_DailySpendRecords" PRIMARY KEY AUTOINCREMENT,
    "TenantId" TEXT NOT NULL,
    "VendorId" TEXT NOT NULL,
    "Date" TEXT NOT NULL,
    "SeatFee" TEXT NOT NULL,
    "UsageOrOverage" TEXT NOT NULL,
    "GrossSpend" TEXT NOT NULL,
    "CreditsApplied" TEXT NOT NULL,
    "NetSpend" TEXT NOT NULL
, "Source" TEXT NULL);
CREATE TABLE sqlite_sequence(name,seq);
CREATE TABLE IF NOT EXISTS "RawDailyExtractionRecords" (
    "Id" INTEGER NOT NULL CONSTRAINT "PK_RawDailyExtractionRecords" PRIMARY KEY AUTOINCREMENT,
    "TenantId" TEXT NOT NULL,
    "VendorId" TEXT NOT NULL,
    "Date" TEXT NOT NULL,
    "ExtractedAtUtc" TEXT NOT NULL,
    "RecordsJson" TEXT NOT NULL
);
CREATE TABLE IF NOT EXISTS "VendorRateConfigs" (
    "Id" INTEGER NOT NULL CONSTRAINT "PK_VendorRateConfigs" PRIMARY KEY AUTOINCREMENT,
    "TenantId" TEXT NULL,
    "VendorId" TEXT NOT NULL,
    "RateType" TEXT NOT NULL,
    "ModelOrSku" TEXT NULL,
    "Rate" TEXT NOT NULL,
    "SeatCount" INTEGER NULL,
    "BillingCadence" INTEGER NULL,
    "EffectiveFrom" TEXT NOT NULL,
    "EffectiveTo" TEXT NULL
);
CREATE UNIQUE INDEX "IX_DailySpendRecords_TenantId_VendorId_Date" ON "DailySpendRecords" ("TenantId", "VendorId", "Date");
CREATE UNIQUE INDEX "IX_RawDailyExtractionRecords_TenantId_VendorId_Date" ON "RawDailyExtractionRecords" ("TenantId", "VendorId", "Date");
CREATE INDEX "IX_VendorRateConfigs_TenantId_VendorId_ModelOrSku_EffectiveFrom" ON "VendorRateConfigs" ("TenantId", "VendorId", "ModelOrSku", "EffectiveFrom");
CREATE TABLE IF NOT EXISTS "UserDailyUsageRecords" (
    "Id" INTEGER NOT NULL CONSTRAINT "PK_UserDailyUsageRecords" PRIMARY KEY AUTOINCREMENT,
    "TenantId" TEXT NOT NULL,
    "VendorId" TEXT NOT NULL,
    "Date" TEXT NOT NULL,
    "UserId" TEXT NOT NULL,
    "UserEmail" TEXT NULL,
    "UserName" TEXT NULL,
    "Amount" TEXT NOT NULL,
    "Credits" TEXT NULL,
    "Requests" INTEGER NULL,
    "IsEstimated" INTEGER NOT NULL,
    "Attribution" TEXT NOT NULL
);
CREATE UNIQUE INDEX "IX_UserDailyUsageRecords_TenantId_VendorId_Date_UserId" ON "UserDailyUsageRecords" ("TenantId", "VendorId", "Date", "UserId");
CREATE TABLE IF NOT EXISTS "DailySpendBreakdownRecords" (
    "Id" INTEGER NOT NULL CONSTRAINT "PK_DailySpendBreakdownRecords" PRIMARY KEY AUTOINCREMENT,
    "TenantId" TEXT NOT NULL,
    "VendorId" TEXT NOT NULL,
    "Date" TEXT NOT NULL,
    "Category" TEXT NOT NULL,
    "Detail" TEXT NOT NULL DEFAULT '',
    "Amount" TEXT NOT NULL,
    "IsUsage" INTEGER NOT NULL
);
CREATE UNIQUE INDEX "IX_DailySpendBreakdownRecords_TenantId_VendorId_Date_Category_Detail" ON "DailySpendBreakdownRecords" ("TenantId", "VendorId", "Date", "Category", "Detail");
CREATE TABLE IF NOT EXISTS "MemberRosterRecords" (
    "Id" INTEGER NOT NULL CONSTRAINT "PK_MemberRosterRecords" PRIMARY KEY AUTOINCREMENT,
    "TenantId" TEXT NOT NULL,
    "VendorId" TEXT NOT NULL,
    "SnapshotDate" TEXT NOT NULL,
    "MemberId" TEXT NOT NULL,
    "Email" TEXT NULL,
    "Name" TEXT NULL,
    "Role" TEXT NULL,
    "SeatType" TEXT NULL,
    "AddedAt" TEXT NULL
, "IsScimManaged" INTEGER NULL);
CREATE UNIQUE INDEX "IX_MemberRosterRecords_TenantId_VendorId_SnapshotDate_MemberId" ON "MemberRosterRecords" ("TenantId", "VendorId", "SnapshotDate", "MemberId");
CREATE TABLE IF NOT EXISTS "ActivitySummaryRecords" (
    "Id" INTEGER NOT NULL CONSTRAINT "PK_ActivitySummaryRecords" PRIMARY KEY AUTOINCREMENT,
    "TenantId" TEXT NOT NULL,
    "VendorId" TEXT NOT NULL,
    "Date" TEXT NOT NULL,
    "Product" TEXT NOT NULL DEFAULT 'all',
    "DailyActiveUsers" INTEGER NULL,
    "WeeklyActiveUsers" INTEGER NULL,
    "MonthlyActiveUsers" INTEGER NULL,
    "AssignedSeatCount" INTEGER NULL,
    "PendingInviteCount" INTEGER NULL,
    "DailyAdoptionRate" TEXT NULL,
    "WeeklyAdoptionRate" TEXT NULL,
    "MonthlyAdoptionRate" TEXT NULL
);
CREATE UNIQUE INDEX "IX_ActivitySummaryRecords_TenantId_VendorId_Date_Product" ON "ActivitySummaryRecords" ("TenantId", "VendorId", "Date", "Product");
CREATE TABLE IF NOT EXISTS "SpendLimitRecords" (
    "Id" INTEGER NOT NULL CONSTRAINT "PK_SpendLimitRecords" PRIMARY KEY AUTOINCREMENT,
    "TenantId" TEXT NOT NULL,
    "VendorId" TEXT NOT NULL,
    "SnapshotDate" TEXT NOT NULL,
    "MemberId" TEXT NOT NULL,
    "Email" TEXT NULL,
    "Name" TEXT NULL,
    "LimitAmount" TEXT NULL,
    "PeriodToDateSpend" TEXT NULL,
    "Currency" TEXT NULL,
    "Period" TEXT NULL,
    "SourceType" TEXT NULL,
    "SourceDetail" TEXT NULL,
    "SpendLimitId" TEXT NULL
);
CREATE UNIQUE INDEX "IX_SpendLimitRecords_TenantId_VendorId_SnapshotDate_MemberId" ON "SpendLimitRecords" ("TenantId", "VendorId", "SnapshotDate", "MemberId");
```
