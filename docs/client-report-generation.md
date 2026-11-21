# Client Spend & Benchmark Report — how it's built

This documents the Client Spend & Benchmark Report: what it contains, how it is
generated, and the design conventions it follows. The report is produced by the
`report generate` command from the data Meterist has already extracted; the
populated HTML is **intentionally excluded from version control** — see the
privacy rule below.

## Generating a report

```powershell
dotnet run --project src/Meterist.Cli -- report generate --tenant <tenant-id> --tenant <another-tenant-id> --from 2026-07-01 --to 2026-11-18 --out artifacts/<name>.html
```

- `--tenant` is repeatable; each tenant becomes a column or row group in the report.
- `--from` / `--to` are inclusive. `--out` defaults to
  `artifacts/client-spend-benchmark-report.html`, and the folder is created if
  needed. Use a dated file name so earlier reports are kept for comparison.
- **`report generate` only reads the database; it never calls a vendor.** Run
  `extract` first (see the [user guide](user-guide.md)) or the report reflects
  whatever was last stored. Before trusting a report, run
  `audit coverage` over the same window: a missing day on a seat-priced vendor
  silently understates the totals.
- The aggregation lives in `Meterist.Core.Reporting` (`ReportDataAggregator`),
  and the HTML is rendered by `ClientBenchmarkReportRenderer`. All figures,
  including the weekly buckets and projections, are computed in code, not by
  hand.

## Privacy rule — read this first

`artifacts/` is gitignored on purpose (see `.gitignore`). Anything placed
there can contain real per-tenant financial data pulled live from vendor
APIs. Treat it exactly like `env/`:

- Generate reports locally under `artifacts/` and stop there.
- Never `git add` or otherwise stage a populated report.
- If a report needs to be shared outside this repo, that's a manual,
  deliberate action by the user — not something a coding session does on
  its own.

## What the report covers

The current version is the "Client Spend & Benchmark Report" — insights #1
(TCO rollup), #2 (cross-tenant benchmarking), #3 (concentration risk), and #9
(audit/reconciliation status) from `docs/project-status-summary.md` §3,
bundled into one page:

1. **Masthead** — title, tracked date range, tenants/vendors covered, a
   DRAFT status chip if the report isn't ready for client eyes yet.
2. **KPI cards** — total tracked Gross Spend per tenant, vendor count, date
   range, average $/day.
3. **Vendor ledger table** — per-vendor $ comparison across tenants, with
   the seat-fee/usage split called out where a vendor has both (ChatGPT
   Enterprise today).
4. **Headline finding callout** — one genuinely interesting cross-tenant
   fact worth surfacing prominently (e.g., identical seat cost but a large
   usage-intensity gap between two tenants). Pick this from whatever the
   data actually shows — don't force one if nothing stands out.
5. **Concentration risk** — a segmented bar per tenant showing vendor mix
   %, with a risk flag if any single vendor exceeds roughly 70–75% of that
   tenant's tracked spend, or a "diversified" flag if not.
6. **Rate card** — per tenant/vendor pairing: seat fee (normalized to
   $/seat/month, actual contracted cadence noted alongside), seat count,
   overage mechanism, and the rate's effective-from date. This is
   deliberately **not** a per-vendor table — the same vendor can have a
   different seat rate and a different overage rate per tenant (confirmed
   real-world case: zelleri and ecosync both run ChatGPT Enterprise at 50
   seats but different seat rates and different credit-to-USD rates), so
   rows are one per (vendor, tenant) pair, labeled `Vendor (tenant)` to
   match the row-label convention already used in the audit table below.
   Not every vendor has a seat/overage rate to show: Claude API Platform is
   pure usage-based pricing with no seat concept at all, and Gemini
   Enterprise's subscription fee arrives pre-priced from the BigQuery
   billing export rather than being computed from a configured
   `VendorRateConfig` row — both show `—` by design, not a data gap.
7. **Weekly spend by vendor** — per tenant, Wednesday-to-Tuesday weekly
   buckets with the seat-fee and usage split. A week cut short by where the report starts or ends (a short first
   week, or a trailing week that hasn't fully elapsed) is marked with an asterisk and explained beneath the table.
8. **Annualized cost projection** — per tenant and vendor, three scenarios
   (flat, trend-adjusted, latest-week annualized) computed only from weeks
   that lie **fully inside the report period**, the same rule for every vendor
   (`ProjectionEligibility`). A report that starts or ends mid-week has a short
   first or last week; it stays in the weekly table and in the actual totals but
   is left out of all three projections, because a short week averaged in with
   full ones drags the average and the trend down even when every requested day
   is stored. Any ChatGPT Enterprise week with missing days is left out as well
   (a separate rule, see the footnote rules below). Start a report on a
   Wednesday to include its first week. Fewer than one full week gives no
   projection (shown as a dash).
9. **Audit & reconciliation status** — per vendor, how the numbers were
   verified (currently: manual comparison against the vendor's own console
   at build time, not yet automated) and a forward-looking note on the
   planned `meterist audit` command (insight #9).
9a. **Spend by kind of charge** — for a vendor with anything beyond a seat fee, or any not-counted charge
   (tax, support), a table of the stored per-kind breakdown: the usage kinds the figures count, and the not-counted
   kinds that are on the vendor's invoice but in no figure or projection. Omitted for a tenant where nothing
   is notable. A kind under half a cent (invoice rounding) is not listed, though it stays in the
   subtotals, and does not by itself make the section appear. Informational: the weekly figures and
   projections do not use it.
9b. **People sections** — four sections that sit between the spend sections and the projections, each left out when
   the stored data has nothing for it. They **name people** (name and email), by operator decision, so a populated
   report is personal data (see the privacy rule above). Per-user dollars are **usage only**: a seat fee is a flat
   charge and is not attributed to individuals.
   - **Active users and seat utilization** — per tenant and vendor: billed seats (from the rate configuration), the
     vendor-reported seats (its assigned-seat count where it gives one, otherwise the roster size), roster members,
     people active and idle in the 30 days ending on the last day of the report, utilization against billed seats and
     against the roster, and the vendor's own daily, weekly and monthly active users where it reports them (Claude
     Enterprise). Seat drift, unused billed seats and any data caveat (no roster, usage stored only from part-way
     through the window, usage that ends early) are noted beneath, with the same rules as `audit seats`. A vendor with
     no assessable idle data shows a dash, never a zero.
   - **Top consumers by spend** — per tenant, the ten people with the most usage dollars across all vendors, matched by
     email (case-insensitive) where a vendor gives one, with each vendor's share. Usage attributed to an API key or left
     unattributed is not a person's spend and is left out, and so is anyone with no usage dollars (on a seat-based plan
     cost is zero by design, so use shows in requests, in the next section).
   - **Usage by person** — beneath each vendor's aggregate: each person (named) with spend, credits, requests and days
     active, the first 25 rows then the rest summed in one line, and a total. Each table says where its figures come
     from and ends with a reconciliation line comparing the per-user total with the vendor's aggregate usage for the
     same days (`Reconciles`, or `Does not reconcile` with the difference), and how many usage days have no per-user
     rows. Rows are labelled: **(est.)** for a price estimated from credits (ChatGPT) or an allocation, **API key
     (key-level)** for usage the vendor attributes to a key (Claude API Platform workspace keys), and **Unattributed** for
     usage the vendor did not tie to anyone.
   - **Idle seats** — per tenant and vendor, the rostered people with no activity in the 30 days, longest idle first, with
     role, seat type and last active day before the window (`none stored` means no earlier activity is on file, not that
     there was none). Up to 40 are listed.
10. **Footnote** — methodology and caveats: what "Gross Spend" means, that tax
   and support fees are not included (so a figure can differ from an invoice;
   see `docs/architecture.md` §5.2), why
   date ranges can differ by vendor within a tenant, which vendors are
   excluded and why, and any zero-activity vendor explained (new account vs.
   missing pull).
   Two ChatGPT Enterprise notes are built from the data rather than fixed
   text: how many days each tenant has from the Daily Usage API versus the
   `COSTS` export (from `DailySpendRecords.Source`), and which weeks, if any,
   were left out of the trend projections. A ChatGPT week is left out only
   when a day inside the report period has no stored record (the vendor is
   seat-priced, so every real day has a record). A week that is short only
   because the report starts or ends midweek is not treated as missing data
   (it is handled by the partial-week rule above), and a complete first week
   is real data and is included.
   A general **partial-week note** is built from the data for every vendor: it
   says what day of the week the report starts and ends on, which first or last
   week was left out of the projections, and, per tenant, which vendors were
   affected. When the report starts on a Wednesday it says so and that no first
   week was left out.

## Data sourcing

The report is built from Meterist's own SQLite store
(`%LOCALAPPDATA%\Meterist\meterist.db`), specifically `DailySpendRecords`
and, for the rate card, `VendorRateConfigs`; the people sections also read `UserDailyUsageRecords`,
`MemberRosterRecords` and `ActivitySummaryRecords` (all stored by `extract` and `roster snapshot`, so `report generate`
still never calls a vendor). The queries below are what the
aggregator is equivalent to. They are kept as a way to **spot-check a report
against the database**, not as a process to follow by hand. Per-tenant
aggregate:

```sql
SELECT TenantId, VendorId, COUNT(*), MIN(Date), MAX(Date),
       ROUND(SUM(GrossSpend), 2), ROUND(SUM(SeatFee), 2), ROUND(SUM(UsageOrOverage), 2)
FROM DailySpendRecords
WHERE TenantId = '<tenant>'
GROUP BY TenantId, VendorId;
```

Map the `VendorId` GUIDs to names via
[`VendorCatalog`](../src/Meterist.Core/Vendors/VendorCatalog.cs) — don't
assume a mapping from memory, the four vendor GUIDs aren't in any obviously
memorable order.

The **rate card** section pulls from a different table — `VendorRateConfigs`,
not `DailySpendRecords` — since it shows configured contract terms, not
extracted spend. Only the currently-active row per tenant/vendor/rate-type
matters (`EffectiveTo IS NULL`):

```sql
SELECT TenantId, VendorId, RateType, ModelOrSku, Rate, SeatCount,
       BillingCadence, EffectiveFrom, EffectiveTo
FROM VendorRateConfigs
WHERE EffectiveTo IS NULL
ORDER BY TenantId, VendorId, RateType;
```

`BillingCadence` is stored as an int (`0` = Monthly, `1` = Annual, `2` =
OneTime) — map it before display. Normalize seat fees to $/seat/month for
the table (`Rate / 12` for Annual, `Rate` as-is for Monthly) but keep the
actual cadence and raw contracted rate visible alongside (e.g. "$200.00/mo
· Annual · $2,400/seat/yr") — don't silently discard the real contract
terms in favor of the normalized figure.

**Tooling note:** a sandboxed Bash tool reading paths under
`%LOCALAPPDATA%` (outside this repo's working directory) can return a
stale/cached view of this database file that never updates, even after
real writes from the user's own terminal. If aggregate numbers look wrong,
zero, or frozen across repeated queries, don't trust it — ask the user to
run the query themselves in their own PowerShell session and paste back the
result.

## Design system

Established for this report; reuse it for consistency rather than
re-deriving a design plan from scratch each time.

- **Concept:** a ledger / instrument-panel register — fitting a metering
  tool's own subject matter — rather than a generic SaaS dashboard look.
- **Palette (light):** `--bg #f1f2ed` (cool sage-tinted paper), `--text
  #1c2027`, `--accent #b8863b` (brass — used once, for the headline
  finding callout), `--good #3f7a5c`, `--risk #b14a3d`. Dark-mode tokens
  mirror these, brightened for contrast — see the `<style>` block in the
  HTML for the full token set and `@media (prefers-color-scheme: dark)` /
  `data-theme` overrides.
- **Vendor identity colors** (for the concentration bar legend only, kept
  distinct from the accent/semantic colors above): ChatGPT Enterprise
  teal-blue, Gemini Enterprise muted violet, Claude API Platform muted
  olive.
- **Type:** serif display face (Georgia/Iowan Old Style stack) for
  headings, plain utility sans (Segoe UI/Helvetica Neue stack) for body
  copy and labels, monospace (SF Mono/Consolas stack) for every dollar
  figure and table column — the numbers read like a meter's digital
  readout, tying back to the product name.
- **Layout:** single-column report, ~840px max width, section-by-section
  top to bottom in the order listed above. No large hero — this is a
  polished memo, not a landing page.

## Regeneration checklist

1. Confirm which tenants and date range to cover.
2. Extract through the end of the range (`extract`, or the scheduled run
   described in the user guide), and take a roster snapshot (`roster snapshot-all`), so the people sections have a
   roster to compare with and per-user usage to read.
3. Run `audit coverage` for the same tenants and range, and resolve any gaps
   on seat-priced vendors before generating.
4. Run `report generate` with a dated `--out` file.
5. Spot-check a few figures against the aggregate query above, and compare
   against the previous report for large unexplained changes.
6. Stop there — do not stage or commit the populated file.

Changing the report's layout or design belongs in `ClientBenchmarkReportRenderer`
(and its tests), following the design system above.
