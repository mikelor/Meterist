# vNext design note: contract term, seat changes and seat projection

Design note for issue #89 (vNext Design Sprint, low priority). **Nothing here changes today's behaviour and nothing is built.**
It records what a review of the seat and contract model found, sizes each gap against the real code, and recommends which are worth
building and in what order, so the design sprint can decide. Written 2026-11-22.

## Summary

| # | Gap | Size of the effect today | Recommendation |
|---|---|---|---|
| 3 | Projections lag a seat step | **Large and visible**: with a recent seat increase the seat part of every projection understates by 14 to 22% (worked example below) | **Build first.** Small, no schema change |
| 1 | Contract term not stored | Tiny in dollars (about 0.02% over or under for a term that mixes leap and non-leap years); the real gap is no renewal reminder | Build only together with a renewal reminder, as part of 2 |
| 2 | A seat change is a whole copied row | None in the numbers; `rates set` already makes it one command | Fold into 1 as a "contract" entity if and when 1 is wanted; not on its own |
| 4 | Invoices are lumps, the model is an accrual | None in the numbers; it is a reconciliation convenience | Defer; build only if a client asks to tick invoices off |

Separately, the per-vendor **pro-rata assumption** (last section) is worth confirming for ChatGPT Enterprise before relying on its seat
accrual for a mid-term change.

## What the model does today (verified against the code)

- A per-seat rate is a versioned `VendorRateConfig` row: `SeatCount` seats at `Rate`, with a `BillingCadence`. The daily seat fee is
  `SeatCount x Rate / days-in-cadence-period` (`ProrateSeatFee`): for an annual rate, 365 or 366 by the calendar year of that day; for a
  monthly rate, the days in that month.
- A seat added mid-term is a new row with its own `SeatCount` and `EffectiveFrom`; a renewal is a new row from the renewal date.
  `rates set` closes the previous open-ended row the day before, rejects any overlap, and caps a backdated row at the next existing one,
  so the operator never edits a row by hand. Summed over the term, the daily figures equal a pro-rata, co-termed invoice.
- The report projects the **seat part and the usage part separately**, each with Flat (average of complete weeks x 52), Trend-adjusted
  (linear fit of those weeks, projected one week on, x 52) and Latest week x 52 (`ProjectionCalculator`, applied in
  `ClientBenchmarkReportRenderer`). Which weeks feed them is `ProjectionEligibility` (#69).
- Gemini Enterprise has no rate row for seats: its seat fee arrives already prorated from the billing export. Everything below about
  seat rows therefore applies to Claude Enterprise and ChatGPT Enterprise, not Gemini.

## Gap 3: the projections lag a seat step (recommended first)

**Finding, confirmed.** All three projections build the seat figure from past weekly seat fees, so weeks at an older seat count pull it
below what is being paid now. Worked example in seat-days (multiply by the per-seat daily rate for dollars), using one tenant's real seat
history, 10 seats until Nov 6, 11 from Nov 7, 13 from Nov 17 and 14 from Nov 20, with a report that ends Fri Nov 21:

| | Seat-days | vs the run rate of 14 seats x 7 days = 98 |
|---|---|---|
| Week Nov 4 to 10 | 74 | |
| Week Nov 11 to 17 | 79 | |
| Week Nov 18 to 24 | 96 | partial (report ends Nov 21), **excluded** from all three |
| Flat (average of the two complete weeks) | 76.5 | **-21.9%** |
| Trend-adjusted | 84 | **-14.3%** |
| Latest week | 79 | **-19.4%** |

Once a report runs through Nov 24 the week of Nov 18 is complete and the figures improve (flat -15.3%, trend +7.1%, latest -2.0%), but
the trend then over-shoots, and the seat part is wrong for a week or two after every seat change. A longer window (the client reports
start in July) drags the average down further. The cause is the method, not a bug: past weeks cannot know today's seat count.

**Options.**
- **A (recommended).** Project the seat fee from the rate in force on the report's last day: `SeatCount x Rate`, annualised by cadence
  (annual rate: as is; monthly rate: x 12), and trend only the usage part. The seat figure is then identical in Flat, Trend and Latest,
  which is honest: seats are a known contract amount, not a random series. A rate row with no `SeatCount`, no rate row (Gemini), or a
  one-time cadence falls back to today's weekly projection, so those cases are unchanged. Where the contract method applies, the figure does change by a small, explained amount (below).
- B. Keep the weekly method and add a "current seats" row beside it. Cheaper but leaves a misleading headline number.
- C. Do nothing and tell readers to use Latest week after a full week has passed. This is the status quo and is what the footnote could say.

**Why first:** it is the only gap that changes a number a client reads, it needs no new table, and it is independent of the rest (the
seat part only). A renewal inside the projection horizon changes the picture, but without a stored term (gap 1) the projection simply
assumes the current rate continues, which is what it does today.

**An intentional change in the numbers, even when seats never change.** The weekly method annualises with 52 weeks, which is 364
days, so a constant seat charge is projected as 364/365 of the annual price (364/366 in a leap year): a constant $1,200 a year projects
to **$1,196.71** today (**$1,193.44** in a leap year), and the contract-based method would return **$1,200.00**, about 0.27% (0.55% in a
leap year) higher. That is a correction of a small standing bias in the seat part, not a regression, and it should be said in the
report footnote and the release notes. The usage part keeps the 52-week convention, so seat and usage are annualised on different bases
afterwards; whether to move usage to 365 days as well is a separate, tiny decision.

**To decide when building:** whether the report should say "seat fee projected from the contract in force on <date>" next to the figure,
and whether a seat count that changed in the last N days is called out.

**Tests, with explicit expected values** (not "equal to today's output", which is false by the 364/365 factor above): a constant
$1,200 annual rate projects to $1,200.00 in Flat, Trend and Latest alike (today: $1,196.71); the same in a leap year ($1,200.00, today
$1,193.44); a seat step mid-window projects `new SeatCount x Rate` (the worked example above: 14 seats, not the 76.5 to 84 seat-day
averages); a renewal row projects the renewed terms; a monthly cadence projects `SeatCount x Rate x 12`; a rate with no `SeatCount`, a
one-time cadence, or a vendor with no rate rows (Gemini) falls back to the weekly method and matches today's output exactly.

## Gap 1: the contract term is not stored

**Finding, confirmed.** The divisor is days in the calendar year of the accrual day, not the length of the term, so a term that mixes
leap and non-leap calendar years does not accrue to exactly one annual price, whether or not it crosses a Feb 29. Both directions occur:

| Term | Accrual factor | Error |
|---|---|---|
| 2025-12-01 to 2026-11-30 (no leap year) | 31/365 + 334/365 = 1.000000 | none |
| 2028-01-01 to 2028-12-31 (one whole leap year) | 366/366 = 1.000000 | none |
| 2027-12-01 to 2028-11-30 (spans Feb 29: 366 days, December at /365) | 31/365 + 335/366 = 1.000232 | +0.023% |
| 2028-12-01 to 2029-11-30 (December falls in leap 2028, no Feb 29: 365 days, December at /366) | 31/366 + 334/365 = 0.999768 | -0.023% |

It is exact only when every day of the term uses the same divisor as the term's length. In dollars the error is cents per thousand, so it
is not worth a fix on its own.

The real gap is that the model **does not know when the term ends.** Nothing warns that a renewal is coming or that the rate and seat count
are about to change, and an open-ended row keeps accruing at the old rate past the term end until someone enters the renewal. That is
operationally more important than the divisor error, and it is where a contract entity pays off.

**Options.** (a) Add `TermStart` and `TermEnd` to the rate row (optional) and divide by the term's length; (b) a separate `Contract`
entity that owns the term, with rate rows referencing it. (a) is a small migration; (b) is gap 2.

**Recommendation.** Do not build the leap-day correction alone. Build a **renewal reminder** (`audit contracts`, or a line in the
scheduled run's output: "term ends in N days, no renewal row") only when the operator wants it, and let the term fields come with it.

## Gap 2: a seat change is a whole copied row

**Finding, partly mitigated.** The row copy is real, but `rates set` takes the full row once and closes the old one, so a seat change is a
single command and no hand editing; the history is the stack of rows. What is missing is a reason and a record of who changed it, and a
natural place for the term (gap 1).

**Options.** Model the contract once and seat changes as dated events against it (`SeatChange(date, delta or new count, note)`), with the
rate rows derived. That gives "who changed what, when" and a natural home for the term and renewal.

**Recommendation.** Not on its own: the numbers are already right and the cost is a data-model change plus migrating existing rows. Take
it only as the vehicle for gap 1's reminder, and only if per-change history or an audit trail is actually wanted. If it is built, migrate
by deriving the contract and events from the existing rows rather than asking for re-entry.

## Gap 4: invoices are lumps, the model is an accrual

**Finding, confirmed.** An annual prepayment and add-on invoices arrive as lumps; Meterist shows a smooth daily figure, and there is no
way to tick an invoice line off against the days it covers.

**Option.** An optional `Invoice` record (tenant, vendor, reference, amount, covers-from, covers-to, kind) and an `audit invoices` that
sums the accrued seat fee over the covered range and compares it with the invoice, within a tolerance. It is the invoice-side twin of the
report-to-report diff already built (#85), and would take a CSV import to be bearable.

**Recommendation.** Defer. It needs manual data entry for every invoice, the benefit is reconciliation rather than correctness, and the
accrual is already right. Build it if a client wants "does this match my invoices" as a deliverable.

## The pro-rata assumption, per vendor

The accrual assumes a mid-term seat is charged **pro rata at the annual per-seat rate**. If a vendor charges a full year for a mid-term
seat, the daily accrual understates that period.

| Vendor | Where the seat fee comes from | Status |
|---|---|---|
| Claude Enterprise | Rate row (this model) | The contract was described as pro rata at the contract rate. Not yet checked against an invoice line |
| ChatGPT Enterprise | Rate row (this model) | **Unchecked** |
| Gemini Enterprise | The billing export, already prorated | Not applicable: the export is the truth |

**How to check cheaply:** take one mid-term add-on invoice line and compare it with `seats added x annual rate x days remaining / days in
the term`. A match confirms it; a full-year charge disproves it, and that vendor then needs a different treatment (a one-off charge on the
add date rather than a daily accrual).

## Proposed build issues (not filed; for the design sprint to approve)

1. **Project the seat fee from the contract in force** (gap 3, option A). Seat part from `SeatCount x Rate` at the report's last day,
   annualised by cadence; trend only usage; fall back to the weekly method where there is no usable rate. Tests with the explicit expected values above (a constant $1,200 a year projects to $1,200.00, not today's $1,196.71), and the intentional change called out in the footnote.
   Small. Recommended next.
2. **Renewal reminder with a stored contract term** (gaps 1 and 2 together, if wanted). Term start and end on the contract, a reminder in
   `audit` and the scheduled run, accrual divided by the term length. Medium; a migration.
3. **Invoice matching** (gap 4). Optional invoice records and `audit invoices`. Medium; manual entry. Only on demand.

## Out of scope

Changing today's accrual or projections (that is what the build issues above would do, each separately reviewed), per-vendor pricing
changes, and Gemini's seat fee, which comes from the billing export and needs none of this.
