# Database Schema Reference

**Engine:** SQLite (v1 — see `architecture.md` §7 for the planned migration
to a server-based RDBMS once hosted).
**File location:** `%LOCALAPPDATA%\Meterist\meterist.db`.
**Created/versioned by:** EF Core migrations in
`src/Meterist.Data/Migrations/` — as of this writing, three migrations:
`20260722033812_InitialCreate`, `20261119172104_AddDailySpendRecordSource` and
`20261120160643_AddUserDailyUsageRecords`.

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
`AddUserDailyUsageRecords`; **nothing writes to it yet**. The ChatGPT and Claude
Enterprise adapters will populate it in later sprint issues.

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
| `IsEstimated` | `INTEGER` | No | `1` when `Amount` is an estimate rather than a billed figure; reports must label it. |
| `Attribution` | `TEXT` | No | `Person`, `ApiKey` or `Allocated` (`UsageAttribution`), stored as text so it is readable in SQL and reordering the enum cannot relabel history. `ApiKey`: the vendor reported the usage against a key, not a person. `Allocated`: Meterist divided an aggregate across users. |

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
`UserDailyUsageRecords` table and its index at the end were added afterwards, taken
from a freshly migrated empty database, not the live file; regenerate the whole dump
from the live database once `AddUserDailyUsageRecords` has been applied there:

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
```
