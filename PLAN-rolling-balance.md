# PLAN — Rolling account balance and bank-statement reconciliation

Design for a `nocfo balance` command that produces a running balance for selected account(s),
honouring the opening balance, and a `nocfo reconcile` command that checks that balance against a
Finnish machine-readable bank statement (Nordea `.nda` / TITO "Konekielinen tiliote").

Status: **plan only, nothing implemented.** Written 2026-08-04 against the spec in
`api/openapi.json` (73 paths) and the sample statement in `tmp/`.

---

## 1. What the API already gives us

Two viable routes. They are not equivalent, and the choice determines most of the work.

### Route A — `POST /v1/business/{slug}/report/ledger/` (recommended)

Request `DateRangeTypedReportRequestSchemaRequest`: `{ "columns": [{ "date_from": …, "date_to": … }] }`.

Response `LedgerJsonResponse`:

```
accounts : LedgerReportAccountSchema[]
  number, name, opening_balance, totals?          // totals keys: debet, credit, balance_csum
  entries : LedgerReportEntrySchema[]
    number, date, debet, credit, balance_csum, description?
```

The server already computes exactly what is being asked for: `opening_balance` is documented as
"opening balance of the account before the listed period entries" — i.e. carried forward into
`date_from`, which is precisely the "adhere to the opening balance if there is one" requirement —
and `balance_csum` is the running cumulative balance after each entry.

- **One HTTP request** for all accounts and the whole period.
- Semantics are the server's, so the output agrees with what NoCFO's own UI shows. For an
  accounting tool that is the single most valuable property: a client-side recomputation that
  disagrees with the books is worse than useless.
- Costs: the response is one JSON blob (no pagination), so it is inherently buffered; entries carry
  no entry-id, account-id, contact or VAT fields; account selection has to be done client-side.

### Route B — client-side fold over documents and entries

`GET /document/?account={id}&date_from=&date_to=` — paginated **and filterable by account and date
range** — then `GET /document/{id}/entry/` per document, filter entries to the account, fold with a
running sum seeded from `Account.opening_balance`.

- Fully streaming, and entry-level detail is available (`amount`, `is_debet`, `description`,
  `vat_code`, `vat_rate`, `account_number`).
- Costs: N+1 requests (one per document); `Account.opening_balance` is the account's *configured*
  opening balance, not the balance carried into an arbitrary `date_from`, so an arbitrary start date
  requires summing everything before it anyway; ordering within a day is ours to invent and will not
  match the server's.

### Decision

**Route A is the default. Route B is a selectable source, added second, and exists mainly so the
two can be diffed against each other.**

Route A is one endpoint, one response type, no ordering ambiguity, no N+1, and it answers the
question as an accountant would. That makes it the right default.

Route B earns its place for a different reason: a client-side fold that reproduces the server's
ledger is the strongest available check that our understanding of opening balances, sign conventions
and date filtering is correct. Because §6 makes every balance source reduce to the same
`AsyncSeq<string * decimal>`, "does our fold agree with the server?" is the *same* comparison as
"does the ledger agree with the bank?" — no extra machinery, and both are diffable through a CSV
file.

Route A still means the rolling balance is not, in the default path, a pure domain fold computed
here. `ROADMAP.md` Phase 5 lists "Balance sheet / P&L report folds" as future work; Route B is the
first instalment of that, and Route A is the oracle it gets checked against.

---

## 2. Refactoring to do first

The brief asked what should be refactored so the new code stays minimal and the system stays
coherent. Answers below, including two deliberate refusals.

### R1 — Fix `TASK-defects.md` D1, D2 and D9 before starting

Not optional. `reconcile` takes a **user-supplied file path** and user-supplied dates; today a bad
path or an unparseable file would exit 134 with a .NET stack trace (D1), and dates going into a
query string need the escaping helper D9 asks for. Writing new commands on top of the current error
handling means writing the same defect twice.

D5 (`AsyncSeq.tryHead` buffering) is also worth doing first: both new commands are business-scoped,
so both inherit the "download every business before starting" cost.

### R2 — `Endpoints.fs`: a query-string builder (~12 lines)

Every paged builder repeats `?page_size=100&page={page}`, and the new endpoints add `date_from`,
`date_to`, `account`. Add one helper that takes `(string * string option) list`, drops `None`,
escapes values, and joins. Then the three new endpoints are one line each and D9 is closed at the
same time.

### R3 — `Money` conversion at the boundary (~10 lines, in `Reports.fs`)

The API gives amounts as `float32` (`Account.balance`, `Entry.amount`) and `double`
(`LedgerReportEntrySchema.debet/credit/balance_csum`). TITO amounts are exact integer cents.
Comparing a `double` running balance against exact cents will produce spurious mismatches once
sums grow.

Convert to `decimal` immediately on ingest, round to 2 dp, and compare with an explicit tolerance
constant (default `0.00M`, i.e. exact, with `--tolerance` to relax). One function, not a type
system, and no `[<Measure>]` — units of measure do not survive the reflection-based CSV writer.

### R4 — Move `Reports.fs` last in the compile order

`Reports.fs` currently sits at position 8, before `Csv.fs`. The reconciliation fold needs both the
domain types and the TITO types, so the order becomes:

```
… PatchShape.fs, Domain.fs, CsvHelper.fs, Csv.fs, Tito.fs, Reports.fs
```

One `.fsproj` edit. Its dead contents (`addToTotals`) are replaced, per `TASK-defects.md` D7.

### R5 — What **not** to refactor

**Do not collapse the per-entity blocks in `Domain.fs`.** `executeAccountCommands`,
`executeDocumentCommands`, `executeContactCommands` and `executeBusinessCommands` are four
near-identical bodies differing in three lambdas, and `ROADMAP.md` names this as genericisation
candidate #1. It is a real cleanup — and it is entirely on the *write* path. This feature is
read-only. Doing both at once mixes a risky refactor of mutating code with a new feature and makes
both harder to review. Leave it; it stays on the roadmap.

**Do not build a shared "input channel" abstraction over CSV and NDA.** This is the trap the brief
warns about, so it deserves the explicit argument:

| | CSV reader | TITO reader |
| --- | --- | --- |
| framing | delimiter + quoting (CsvHelper) | fixed byte counts, self-declaring lengths |
| field identity | **named**, from a header row | **positional**, per record type, no header |
| record shape | one shape per file | six record types, one file, hierarchical (T11 attaches to the preceding T10) |
| typing | reflection over an F# record's properties | a hand-written layout table per record type |
| encoding | UTF-8 | 7-bit ISO 646-FI (see §4) |

`Csv.fs`'s core machinery — `collectRecordMetadata`, `buildRecordFromCsv`, `validateHeader` — is
entirely about resolving *names* to record fields. TITO has no names to resolve. An abstraction
spanning the two would have to be parameterised over framing, field identification, typing and
encoding simultaneously, which is another way of saying it would have no content. The genuinely
shared parts are already shared and need no work:

- the stream type `AsyncSeq<Result<'T, DomainError>>`;
- the **output** side — TITO-derived rows are plain F# records and go straight through the existing
  `Csv.writeCsvGeneric`;
- `--fields` selection, which applies unchanged to the new output types.

The SRTP opportunity in the TITO reader is a *different* one, and it is real: TITO records are
self-describing (`T` + 2-digit type + 3-digit length) and the per-type layouts are **data**, not
code. See §4.3.

---

## 3. New code

Roughly 500 lines of new hand-written F#, plus tests. No regeneration needed — `LedgerJsonResponse`,
`LedgerReportAccountSchema`, `LedgerReportEntrySchema`, `DateRangeTypedReportRequestSchemaRequest`,
`Entry`, `PaginatedEntryList` and `Period` are all already in `hawaii-client/generated/Types.fs`.

### `hawaii-client/src/Endpoints.fs` (+4 lines)

```fsharp
let ledgerReport (slug: string)          = $"/business/{seg slug}/report/ledger/"
let periodsBySlugPage (slug) (page)      = …   // for --period resolution
```

### `hawaii-client/src/Tito.fs` (new, ~250 lines)

The NDA/TITO reader. Self-contained; depends only on `AsyncSeq.fs` and the `DomainError` type.
See §4 for the format detail.

```fsharp
type TitoAmount    = decimal                     // exact, from integer cents
type TitoRecord =
  | Header      of TitoHeader                    // T00
  | Transaction of TitoTransaction               // T10
  | Detail      of subtype: string * text: string// T11
  | DayBalance  of TitoDayBalance                // T40
  | Cumulative  of TitoCumulative                // T50
  | Notice      of string                        // T70
  | Unknown     of tag: string * raw: string

module Tito =
  val readRecords    : TextReader -> AsyncSeq<Result<TitoRecord, DomainError>>
  val readStatements : TextReader -> AsyncSeq<Result<TitoStatement, DomainError>>
```

`TitoStatement` is the useful unit: one `T00` header plus the transactions that follow it, with
`T11` details folded into their preceding `T10`, plus the day balances. A single file contains
several statements (the sample has two).

### `hawaii-client/src/Reports.fs` (replaced, ~140 lines)

```fsharp
type BalanceRow =                        // one CSV row of the rolling balance
  { account_number: string; account_name: string
    date: string; document_number: string; description: string
    debet: decimal; credit: decimal; amount: decimal; balance: decimal }

type ReconcileRow =                      // one CSV row of the day-by-day comparison
  { date: string
    ledger_balance: decimal option
    statement_balance: decimal option
    difference: decimal option
    status: string }                     // ok | differs | ledger-only | statement-only

module Balance =
  val rows        : LedgerJsonResponse -> AsyncSeq<BalanceRow>
  val dailyClosing: AsyncSeq<BalanceRow> -> AsyncSeq<string * decimal>

module Reconcile =
  val daily : AsyncSeq<string * decimal> -> AsyncSeq<string * decimal> -> AsyncSeq<ReconcileRow>
```

`Balance.rows` emits, per selected account, a synthetic opening row followed by one row per entry.
The opening row makes the CSV self-checking: `last balance = opening balance + Σ(debet − credit)`.

`Reconcile.daily` is an ordered merge on date — reuse `Streams.alignByKey` verbatim. Dates as
ISO `yyyy-MM-dd` strings sort correctly under ordinal comparison, so R2's D3 hazard does not apply,
but assert sortedness in a test.

Everything comparable is funnelled through one type. A **balance source** is anything that can be
reduced to a date-ordered `AsyncSeq<string * decimal>` of daily closing balances:

```fsharp
type BalanceSource =
  | Ledger                    // Route A — POST /report/ledger/
  | Entries                   // Route B — client-side fold over documents + entries
  | Nda  of path: string      // T40 day-balance records
  | Csv  of path: string      // date,balance — i.e. this tool's own --daily output

module BalanceSource =
  val parse : string -> Result<BalanceSource, string>          // "ledger" | "entries" | "nda:…" | "csv:…"
  val open_ : BusinessContext -> Query -> BalanceSource -> AsyncSeq<Result<string * decimal, DomainError>>
```

This is the whole reason the CSV reader is worth wiring in: `Csv` as a source costs about five
lines — the rows are a two-field record read by the existing `Csv.readCsvGeneric` — and in exchange
every pairing becomes expressible, including the two that matter for correctness:

| left | right | question answered |
| --- | --- | --- |
| `ledger` | `nda:statement.nda` | do the books agree with the bank? |
| `ledger` | `entries` | does our own fold agree with the server's ledger? |
| `csv:a` | `csv:b` | offline regression, no network, fully deterministic |
| `csv:golden` | `nda:fixture.nda` | does the TITO reader still decode the same balances? |

### `hawaii-client/src/Domain.fs` (+~35 lines)

`fetchLedger : BusinessContext -> DateOnly -> DateOnly -> Async<Result<LedgerJsonResponse, DomainError>>`,
built on the existing `Http.postJson`. No new HTTP verb needed.

### `tools/Arguments.fs`, `tools/Program.fs` (+~90 lines)

See §5.

### `tests/` (new: `TitoTests.fs`, `BalanceTests.fs`)

See §6.

---

## 4. The NDA / TITO format

Verified by decoding `tmp/Konekielinen tiliote(26-04-01).nda` (17 493 bytes, 115 records, two
statements). "NDA" is Nordea's file extension; the format itself is the shared Finnish standard
**TITO — Konekielinen tiliote** (Finanssiala), so OP and Danske statements should parse with the
same code, using different subsets of the record types.

### 4.1 Encoding — the thing that will silently corrupt output if missed

The file is **pure 7-bit ASCII** (verified: no byte ≥ 0x80; maximum byte is 0x7C) and uses the
**ISO 646-FI / SFS 4017** national variant, in which the ASCII bracket positions carry Finnish
letters:

| byte | 0x5B | 0x5C | 0x5D | 0x7B | 0x7C | 0x7D |
| --- | --- | --- | --- | --- | --- | --- |
| ASCII | `[` | `\` | `]` | `{` | `\|` | `}` |
| SFS 4017 | Ä | Ö | Å | ä | ö | å |

Evidence in the sample: `L{nsi-Uusimaa` → `Länsi-Uusimaa`, `Tilill{ olevat varat` → `Tilillä olevat
varat`.

This is **not** Latin-1 and **not** CP850. Reading the file as either produces `{` literally in every
Finnish name and place. Apply a six-character translation on decode.

Other banks do emit 8-bit NDA files. Make the transliteration conditional: apply it only when the
input contains no byte ≥ 0x80, and expose `--charset ascii-fi|latin1|utf8` to override. Default
`auto`.

### 4.2 Framing

Every record is `T` + 2-digit type + 3-digit **declared length**, followed by fixed-width fields.
In the sample the declared length equals the actual line length for all 115 records, and records are
CRLF-terminated — but the standard permits an unterminated fixed-length stream. Parse by declared
length; treat CRLF/LF as an optional separator; tolerate a trailing empty line.

Record types present in the sample: `T00` ×2, `T10` ×31, `T11` ×44, `T40` ×15, `T50` ×21, `T70` ×2.
Unknown types must be skipped with a warning, never fail the parse.

### 4.3 Layouts as data, not code

This is where the generalisation belongs. Express each record type as a layout table and share one
slicer:

```fsharp
type Field = { Name: string; Offset: int; Width: int }

module Layout =
  let t10 = [ { Name = "txn_no"; Offset = 6; Width = 6 }; … ]

let inline slice (layout: Field list) (line: string) : Map<string, string>
```

Six tables of ~20 rows replace ~120 hand-written substring expressions, and adding a bank-specific
record type becomes a data edit. Decoders for the three primitive field kinds — padded text, `YYMMDD`
date, signed 18-digit cents — are three small functions used by every table.

### 4.4 `T10` — transaction, 188 bytes (**verified against the sample**)

| offset | width | field |
| ---: | ---: | --- |
| 0 | 3 | record id `T10` |
| 3 | 3 | declared length |
| 6 | 6 | transaction number |
| 12 | 18 | archive id (*arkistointitunnus*) |
| 30 | 6 | booking date (*kirjauspäivä*) |
| 36 | 6 | value date (*arvopäivä*) |
| 42 | 6 | payment date (*maksupäivä*) |
| 48 | 1 | transaction type (*tapahtumatunnus*) |
| 49 | 3 | entry code (*kirjausselitteen koodi*) |
| 52 | 35 | entry text (*kirjausselite*) |
| 87 | 19 | amount: sign + 18 digits, **in cents** |
| 106 | 1 | receipt code (*kvitenssi*) |
| 107 | 1 | delivery method (*välitystapa*) |
| 108 | 35 | counterparty name |
| 143 | 1 | name source |
| 144 | 14 | counterparty account number |
| 158 | 1 | account-number-change code |
| 159 | 20 | reference (*viite*) |
| 179 | 8 | form number |
| 187 | 1 | level code (*tasotunnus*) |

Decoding all 31 `T10` records with this table yields clean values throughout — e.g.
`txn_no=000001, archive_id=GSIPC2919622421001, book=260302, value=260228, code=710,
text="Viitemaksu", amount=+9000 → 90.00 €, name="KETTINEN MERJA LIISA HELENA", ref=160704`.

> Caveat: this table was **derived from one sample file**, not from the Finanssiala specification.
> It sums to exactly 188 and every field decodes sensibly, but confirm it against the published
> spec before trusting it in production. The same applies to §4.5–4.7.

### 4.5 `T00` — statement header, 322 bytes

Fields confirmed through offset 182: version (3), account number (14, matches the IBAN tail),
statement number (3), period start / end (6+6), creation date (6), creation time (4), customer id
(17), **opening-balance date (6)**, **opening balance (19, cents)**, transaction count (6), currency
(3), account name (30), limit (18), account owner name (35). The remaining 140 bytes hold the bank
name and contact lines and end with IBAN (18) + BIC (11).

Only four of these are actually needed: IBAN, statement period, opening balance and its date.

### 4.6 `T40` — day balance, 50 bytes

`T40` + `050` + date (6) + closing balance (19, cents) + available balance (19, cents).
Sums to 50 exactly. **This is the record the reconciliation is built on.**

### 4.7 `T50` — cumulative, 67 bytes; `T11` — detail; `T70` — notice

`T50`: period code (1) + date (6) + deposit count (8) + deposit sum (19) + withdrawal count (8) +
withdrawal sum (19). Period codes 1–4 observed. Useful as an independent checksum; not required.

`T11`: `T11` + length (3) + 2-char subtype + variable payload attached to the preceding `T10`.
Observed subtypes: `00` (free text, lengths 43/148/183/323), `03` (42), `06` (78), `11` (323).
**v1: capture the payload text verbatim as the transaction's `detail` and do not interpret it.**
Only subtype `00` carries anything the reconciliation would use, and even that is cosmetic.

`T70`: free-text notice. Ignore.

### 4.8 Sign convention

`+` is money into the account, `-` out, from the account holder's perspective. A bank account is an
asset, so `+` corresponds to a **debit** in the books. Get this backwards and every reconciliation
inverts; assert it in a test.

---

## 5. CLI surface

```
nocfo balance -b <business>
              [--account 1910,1920] [--account-type ASS]
              --from <yyyy-mm-dd> --to <yyyy-mm-dd>
              [--source ledger|entries] [--daily] [-o out.csv] [--fields "…"]

nocfo reconcile [-b <business>] [--account 1910]
                [--from <yyyy-mm-dd>] [--to <yyyy-mm-dd>]
                --left  <source> --right <source>
                [--charset auto|ascii-fi|latin1|utf8] [--tolerance 0.00] [-o diff.csv]

    <source> ::= ledger | entries | nda:<path> | csv:<path>
```

`--left`/`--right` rather than `--statement`, because the interesting comparisons are not all
"books versus bank" (§3). `-b`, `--from` and `--to` become mandatory only when a source is `ledger`
or `entries`; two file sources need no network and no configuration at all, which is what makes the
comparison testable offline.

Two new top-level verbs rather than new entities under `list`. A rolling balance is a *report*, not
an entity, and the existing `<verb> <entity>` DUs (`EntitiesArgs`) are all entity-shaped.

> Alternative worth considering: a single `report` verb with `ledger` / `balance-sheet` /
> `income-statement` subcommands, mirroring the eight `/report/…` endpoints. That scales better if
> the other seven reports are ever implemented, at the cost of a longer thing to type today.
> Recommendation: start with `balance` and `reconcile`; promote to `report ledger` if and when a
> second report lands. Renaming a verb is cheap.

Output shapes:

- default — one row per ledger entry, preceded by a synthetic opening-balance row per account;
- `--daily` — one row per date with the closing balance, which is what `reconcile` consumes and
  what a human actually reads.

`reconcile` exit codes: `EX_OK` when every day agrees, `EX_DATAERR` when any day differs, plus the
usual mapped codes for transport and configuration failures.

---

## 6. Reconciliation design

Three independent levels of increasing strength and cost.

| level | compares | cost | catches |
| --- | --- | --- | --- |
| **L0** | opening balance (T00) vs ledger `opening_balance`; final T40 vs last `balance_csum` | O(1) | "do the books agree with the bank at all?" |
| **L1** | per-day closing balance: T40 stream vs ledger daily closing stream | O(days) | **which day** the divergence starts |
| **L2** | individual T10 rows vs individual ledger entries | O(n) + matching policy | *which transaction* is missing or wrong |

**Implement L0 and L1. Defer L2.**

L1 is a straight ordered merge on ISO date through the existing `Streams.alignByKey`, and it
answers the question an accountant actually asks — "from which day do we stop agreeing?" — for
maybe a tenth of L2's code. Once L1 names the day, L2's job on that day is small enough to do by eye.

L2 is deferred for a substantive reason, not laziness: bank booking dates and bookkeeping dates
routinely differ by 1–3 days, one bank transaction can map to several entries (and vice versa), and
the sample already shows the same reference `160704` on payments from two different payers. Any
matching rule is therefore a heuristic with a policy — date window, split handling, tie-breaking —
and that policy needs to be chosen deliberately, not invented mid-implementation. When it is built:
greedy match on `(date, amount)` within a ±N-day window, then report the unmatched residue on both
sides. No fuzzy name matching.

### CSV as a first-class source

`csv:<path>` reads a two-column `date,balance` file through the existing `Csv.readCsvGeneric` — the
same shape `nocfo balance --daily` writes, so every run of `balance` produces a file that
`reconcile` can consume. That round-trip property is what makes the feature testable and is worth
more than the five lines it costs:

- **Route A vs Route B** is `reconcile --left csv:ledger.csv --right csv:entries.csv`, or directly
  `--left ledger --right entries`. If the server's ledger and our own fold disagree on any day, the
  fold is wrong — and the diff names the day.
- **Regression testing** needs no network and no mock: check in a pair of small CSVs and assert the
  `ReconcileRow` stream. The comparison logic is then covered independently of both the API and the
  TITO parser.
- **The TITO reader** is tested the same way: one synthetic `.nda` fixture plus a golden
  `date,balance` CSV, compared with `--left csv:golden.csv --right nda:fixture.nda`. A change in the
  T40 offsets shows up as a diff, not as a silent shift.
- Users get an escape hatch for statement formats we do not parse: convert to two columns in a
  spreadsheet and compare anyway.

The reader must reject an unsorted CSV explicitly rather than let `alignByKey` desynchronise
silently — the same class of bug as `TASK-defects.md` D3.

---

## 7. Testing

Unit tests only; no new network dependency.

- `TitoTests.fs`
  - SFS-4017 decode: `L{nsi-Uusimaa` → `Länsi-Uusimaa`; 8-bit input passes through untouched
  - amount decode: `+000000000000009000` → `90.00M`; `-000000000000000194` → `-1.94M`; exact decimal
  - date decode: `260309` → `2026-03-09`, including the century-pivot rule
  - `T10` layout: one golden record decoded field-by-field
  - `T11` attaches to the preceding `T10`; multiple details on one transaction
  - two `T00` headers in one file produce two statements
  - unknown record type is skipped, not fatal
  - a record whose declared length disagrees with its actual length is a clean `DomainError`
- `BalanceTests.fs`
  - opening row emitted with the right balance when `opening_balance ≠ 0`
  - `last balance = opening + Σ(debet − credit)` on a synthetic ledger
  - empty account (opening balance, no entries) yields exactly one row
  - `Reconcile.daily` on: identical streams; a one-day gap on each side; a value mismatch
  - tolerance boundary: difference exactly at tolerance is `ok`, one cent beyond is `differs`
  - an unsorted `csv:` source is rejected, not silently mis-merged
- `ReconcileTests.fs` — end-to-end through `BalanceSource`, two checked-in CSV fixtures plus the
  synthetic `.nda`. No network, no HTTP stub: `csv:`/`nda:` sources make the whole comparison path
  reachable offline, which is the main practical argument for having them.

**Test fixtures must be synthetic.** `tmp/Konekielinen tiliote(26-04-01).nda` contains real
individuals' names, IBANs and payment references, and `tmp/` is currently *not* gitignored
(`TASK-defects.md` D10). Hand-write a small statement — two statements, four transactions, three
day balances, one unknown record type — and check *that* in under `tests/fixtures/`.

An online case in `tests-online/test-list.sh` covering `balance --daily` against the api-tst
fixture business would be a cheap sanity check on the endpoint contract.

---

## 8. Sequencing

1. `TASK-defects.md` D1, D2, D9 (+ D5) — prerequisites, not part of this feature.
2. R2 query builder; R4 compile-order move; R3 decimal helper.
3. `fetchLedger` + `Balance.rows` + `nocfo balance` + `BalanceTests`. **Independently useful — ship here.**
4. `Reconcile.daily` + `BalanceSource` with `Ledger` and `Csv` only + `nocfo reconcile`
   + `ReconcileTests` driven by two checked-in CSVs. Still no bank format, no network in tests.
5. `Tito.fs` + `TitoTests` with a synthetic fixture; add the `Nda` source. The golden-CSV comparison
   from step 4 becomes the TITO reader's regression test for free.
6. Route B as `--source entries` / the `Entries` source, validated against `Ledger` by the step-4
   machinery.
7. Only if wanted: L2 transaction-level matching.

Steps 3 and 5 are independent and could proceed in parallel; step 4 is deliberately placed before
the bank format so that the comparison logic is already trusted when the parser lands.

---

## 9. Open questions

1. **Verb naming** — `balance`/`reconcile` versus `report ledger`/`report reconcile` (§5).
2. **`--account` selector** — account *numbers* (stable across environments, what a human knows) or
   *ids*? The ledger response keys on `number`, so numbers are the natural choice; confirm.
3. **Period selection** — is `--from`/`--to` enough, or should `--period <id>` resolve dates from
   `GET /period/`? The endpoint exists and `PaginatedPeriodList` is already generated.
4. **Multi-currency** — `T00` carries a currency code (`EUR` in the sample). Assume single-currency
   and fail loudly on anything else, or handle it?
5. **TITO spec access** — the layouts in §4 are reverse-engineered from one file. Is the Finanssiala
   specification available to check them against before implementation?

---

## 10. As built (2026-08-04)

Steps 1–5 are implemented; steps 6 and 7 are not.

### Deviations from the plan

- **R2 (query-string builder) skipped.** With Route B deferred, the ledger report is a POST with a
  JSON body and the builder would have had no second caller. `Endpoints.ledgerReport` is four
  lines of string interpolation like its neighbours.
- **Route B (step 6) deferred.** `GET /business/{slug}/document/` takes `account`, `date_from`,
  `date_to`, `page`, `page_size` and `search`, but **no ordering parameter**. A client-side running
  balance needs date order, so Route B would have to buffer the period and invent an intra-day
  order — the same class of hazard as `TASK-defects.md` D3. That is a design decision, not an
  implementation detail.
- **`DomainError` gained `Invalid` and `BadData`.** Caller-input errors (unknown source, missing
  period, a selection matching no account) and malformed input files were both landing on
  `Unexpected`, which exits `EX_SOFTWARE` (70). They now exit `EX_USAGE` (64) and `EX_DATAERR` (65).
- **`ReconcileRow` columns are `left_balance` / `right_balance`**, not `ledger_` / `statement_`:
  either side can be any source.
- **Balances carry forward.** A date only one source has is compared against the other's last known
  balance, since a balance holds until it next moves. `left-only` / `right-only` therefore mark only
  the days before a side has any balance at all, rather than every gap.
- **`Tito.decode` takes `byte[]`, not a `TextReader`**, because choosing between ISO 646-FI and
  Latin-1 needs the raw bytes.
- **A `.nda` source names its bank account as `nda:<path>#<account>`.** The sample file holds two
  statements for two different accounts, which §4 did not anticipate; the selector matches the tail
  of the account number or of the IBAN, and a file with several accounts and no selector fails with
  the list of accounts it holds.
- **`T10.value_date` is `DateOnly option`.** The sample carries `000000` on some transactions.

### Corrections to the plan's own findings

- Ledger entry dates come back as ISO **date-times** (`2025-01-21T00:00:00`), not days. `Balance`
  narrows them to `yyyy-MM-dd`; comparing them unnormalised against a bank date never matches.
- `balance_csum` **includes** `opening_balance` (verified against api-tst), so it is the running
  balance as it stands.
- In `T00` the IBAN sits at offset **292** (width 18), not 293; BIC at 311 (width 11).

### Open questions, answered by the implementation

1. Verbs are `balance` and `reconcile`.
2. `--account` takes account **numbers**, which is what the ledger response keys on.
3. `--from` / `--to` only; `--period` is not implemented.
4. Multi-currency is not handled: `T00.currency` is decoded but not checked.
5. The Finanssiala specification was not available; the layouts remain reverse-engineered from one
   Nordea file and are documented as such in `Tito.fs` and `CLAUDE.md`.
