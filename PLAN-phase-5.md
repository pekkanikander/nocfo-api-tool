# PLAN — Phase 5: Broader API coverage

Written 2026-08-06 against `api/openapi.json` (73 paths / 159 schemas, Apr 2026 revision).
Status: **plan only, nothing implemented.**

---

## 1. Where coverage actually stands

This supersedes the inventory in `ROADMAP.md` Phase 5, which predates the Apr 2026 spec
growth and the rolling-balance work.

**Covered**: businesses, accounts, contacts, documents — full create/list/update/delete —
plus `map accounts`, and `POST /report/ledger/` via `balance` / `reconcile` (with TITO and
CSV comparison sources).

**Not covered**, grouped by kind:

| group | endpoints | note |
| --- | --- | --- |
| reports | 7 of 8: `journal`, `balance-sheet`, `balance-sheet-short`, `income-statement`, `income-statement-short`, `equity-changes`, `vat-report` | same POST pattern as the ledger; all types already generated |
| periods | `period/` CRUD | `Period`, `PaginatedPeriodList` generated |
| entries | `document/{id}/entry/` | read-only, N+1 by construction |
| small entities | `tags/`, `identifiers/`, `headers/`, `vat_period/` | plain CRUD |
| files | `files/`, `file_upload/`, document `attach_files`/`set_files`/`detach_files` | multipart — the one genuinely new mechanism |
| document actions | `lock`/`unlock`, `flag`/`unflag`, `copy`, relations, suggestions | POST actions, no CSV shape |
| invoicing | `invoice/` (+7 actions), `product/`, `purchase_invoice/` | a whole subtree, new in the Apr 2026 spec |
| misc | `constants/*`, `me/permissions/`, `user/`, `auth/jwt/` | reference data / identity |

"Transactions" as a top-level notion does not exist in the API. The two routes to
entry-level data are the `journal` report (one request for a whole period, grouped by
document) and per-document `/entry/` (one request per document). The journal report wins
for every reporting purpose.

---

## 2. Prioritisation

ROADMAP's own rule: user need, not completeness. Demonstrated needs so far —
reconciliation, chart-of-accounts migration, document import. What follows extends the
part that just proved useful (server-side reports) and closes a deferral from the
rolling-balance work (`--period`). Everything else waits for a concrete need.

Three tranches, in order.

---

## 3. Tranche A — the report family

The eight report endpoints share **two request schemas** —
`DateRangeTypedReportRequestSchemaRequest` (journal, ledger, income-statement ×2,
equity-changes, vat-report) and `PointInTimeTypedReportRequestSchemaRequest`
(balance-sheet ×2) — and **four response shapes**: `LedgerJsonResponse` (done),
`JournalJsonResponse`, `AccountingReportJsonResponse` (five endpoints),
`VatReportJsonResponse`. All generated already; **no regeneration needed**.

### New verb

```
nocfo report <name> -b <business> --from <date> --to <date> [-o out.csv] [--fields "…"]
    <name> ::= journal | balance-sheet | balance-sheet-short
             | income-statement | income-statement-short | equity-changes | vat
```

`balance` and `reconcile` stay as they are — they are questions, not report dumps, and
renaming a shipped verb buys nothing. Point-in-time reports (`balance-sheet*`) take
`--date` instead of `--from`/`--to`.

### Work items

1. **Generalise the fetch.** `Ledger.fetchLedger` becomes
   `Report.fetch<'Resp> : BusinessContext -> endpoint -> body -> Async<Result<'Resp, DomainError>>`
   — the existing function with the URL and response type abstracted out. `Endpoints.fs`
   gains one `report (slug) (name)` builder.

2. **Journal fold** (`Reports.fs`). Flatten `documents × entries` to
   `{ number; date; account_number; account_name; debet; credit; description }` — one CSV
   row per journal line, `decimal` at ingest per the house rule. This is what ROADMAP
   meant by "transactions, read-only streaming, useful for reports".

3. **Accounting-report fold** (`Reports.fs`). `AccountingReportJsonResponse` is
   `labels: string list` + `rows: { name; level; is_sum_row; values: float list }`.
   The CLI sends exactly **one** column (one date range / one date), so `values` has one
   element and the CSV row is fixed: `{ name; level; is_sum_row; value }`. Multi-column
   comparisons (e.g. this year vs last) are deferred; the fold should fail loudly if
   `labels.Length <> 1` rather than silently taking the head. The docstring says rows may
   carry nested account-level drill-down — inspect a real api-tst response and either
   flatten those as additional rows or drop them behind a `--details` flag; decide on the
   evidence, not the docstring.

4. **VAT report fold.** `VatReportJsonResponse` is `totals` / `summary` / `mapping`
   dictionaries; shape the CSV after looking at a real response. Last in the tranche.

### Estimate

~50 lines in `Domain.fs`/`Endpoints.fs`, ~150 in `Reports.fs`, ~60 in
`Arguments.fs`/`Program.fs`. Unit tests are pure folds over hand-built responses, exactly
the `BalanceTests.fs` pattern.

---

## 4. Tranche B — periods and `--period`

1. `list periods` — read-only. `Period` is all bookkeeping state flags
   (`is_locked`, `is_taxed`, …) plus `start_date`/`end_date`; a row type and wiring, no
   delta/create. Period *mutation* is a state machine best driven from the UI — out of
   scope until a need shows.

2. `--period <id|start-date>` on `balance`, `reconcile` and `report`: resolve
   `--from`/`--to` from `GET /period/`. Closes the open question deferred from the
   rolling-balance plan. Mutually exclusive with explicit `--from`/`--to`.

Small: one stream, one resolver function, an Argu case shared by three verbs.

---

## 5. Tranche C — tags (optional, the SRTP litmus test)

`Tag` is the smallest entity in the API (`name`, `description`, `color`). Implementing
full CRUD for it is less about tags — the need is speculative — and more about
**measuring** ROADMAP's central claim that a new entity now costs "a type definition and
a few lines of wiring". Whatever friction appears is, by definition, the next
genericisation target in `Domain.fs`. Do it when a quiet moment allows; skip if the
measurement isn't wanted.

---

## 6. Deferred, with reasons

- **Entries / Route B (`--source entries`)** — the journal report returns the same data
  in one request; `/entry/` is N+1; and as a *verification* source Route B is weaker than
  hoped, since the journal is computed by the same server as the ledger. Revisit only if
  entry-level reconciliation (the old L2) becomes a need.
- **Files / attachments** — needs `Http.postMultipart`, the only new transport mechanism
  in the API. Well-bounded (`file_upload` → id → `action/set_files/`), but build it against
  a concrete workflow, not speculatively.
- **Document actions** (lock, flag, copy, relations) — no CSV shape, no demonstrated need.
- **Invoicing subtree** — large (16 paths, its own URL prefix), and nothing in current
  usage touches it. If invoice export ever becomes a need, it is a Phase 6 of its own.
- **identifiers, headers, vat_period, constants, user** — no demonstrated need.

---

## 7. Testing

- Unit: journal flatten (multi-entry documents, missing dates), accounting-report flatten
  (sum rows, level nesting, the `labels.Length <> 1` guard), decimal conversion,
  `--period` resolution against a hand-built period list.
- Online (`tests-online/test-list.sh`): one read-only smoke case per report name against
  the api-tst fixture business — same cheap contract check the ledger already has.

---

## 8. Sequencing

1. `Report.fetch` generalisation + `report journal` + tests. **Independently useful — ship.**
2. `report balance-sheet` / `income-statement` (+`-short`, `equity-changes`) via the shared
   accounting-report fold.
3. `report vat`.
4. `list periods` + `--period` on the three date-taking verbs.
5. Tags, if the litmus test is wanted.

---

## 9. Open questions

1. Which reports do you actually read month to month? That should reorder items 2–3.
2. Is VAT handled in NoCFO's UI, or is `report vat` worth pulling forward?
3. Does anything in your workflow need invoice/purchase-invoice export? A yes re-scopes
   the plan.
4. For `balance-sheet*`: is `--date <yyyy-mm-dd>` the right spelling for point-in-time,
   or reuse `--to`?
