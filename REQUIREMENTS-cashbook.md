# REQUIREMENTS — cash book and MobilePay Box posting

Status legend: **P** proposed, **A** agreed, **I** implemented (with tests).
Every rule that changes an output has at least one fixture under `tests/fixtures/cashbook/`;
a fixture is a directory with `kassakirja.csv`, `mobilepay.csv`, the expected `match.csv`
and `plan.csv`, and optionally its own `rules.toml` (else the shared one).
The real data stays in `tmp/` (gitignored); fixtures are synthetic.

Last edited 2026-10-06.

---

## 0. Context and stakes

- **Purpose.** The tool saves entering the drafts into NoCFO by hand; that is its measure of success.
  A run normally happens once, at most twice (after settling an ambiguous match), and the work then
  continues in NoCFO. Every case that sends the user back to the command line or into a CSV file
  counts against the tool, so such cases are kept to a minimum.
- A volunteer non-profit. A handwritten cash book (kassakirja) records *some* cash income and
  expenses and, until practice changes, *some* MobilePay Box payments marked `MP`.
- There is no reliable cash balance: cash is counted a few times a year and there is always
  more cash than the book shows. **The Kassa account is therefore not reconcilable** and the
  tool never tries to; it posts what the book says.
- The MobilePay Box export is complete and authoritative for MobilePay. **Book lines marked MP
  are never posted.** They are used only as classification hints and for cross-check reports.
- Consequently a wrong match has bounded consequences: at worst a misclassified revenue line,
  or an unmarked income line posted twice (once as cash, once from MobilePay). Rules M6 and
  M7 exist to keep that second case visible.
- Every document is created as a draft, so anything the tool gets wrong is corrected in NoCFO
  rather than by re-running the tool. Items needing attention are flagged and tagged on their
  draft, not listed in a separate CSV, so the one necessary follow-up happens where the
  correction itself will happen.
- The library is pure (no NoCFO dependency); the CLI verbs wrap it and hand the result to the
  existing document command machinery. A service or app can come later on the same library.

---

## 1. Inputs

| id | rule | status |
| --- | --- | --- |
| I1 | Cash book CSV: `sivu;pvm;mita;tulo;meno;tapa;kuitti;huom`, `;` separated, decimal comma, UTF-8 with or without BOM. `pvm` is ISO. Exactly one of `tulo`/`meno` is non-empty. | I |
| I2 | `tapa` is normalised: `MP`, `MP?` → MobilePay; `KÄT`, `KT`, `KÄ` → cash; empty → unmarked. Anything else is a CLI-side row error, not a NoCFO concern: it stops the run before any matching or posting; `--force` drops the offending rows instead and continues with the rest (P9). | P |
| I3 | `kuitti` is a receipt count (integer) or empty. `huom` is free text: never written to a generated CSV (P6), carried into NoCFO in the attached listing of the document its line posts into (P10, P12). | P |
| I4 | MobilePay export CSV: `Päivämäärä,Nimi,Tyyppi,Numero,Viesti,Summa,Valuutta,Maksutapa,Tapahtumatunnus`; date `dd/MM/yyyy HH:mm` local time; decimal point. The XLSX is converted outside the tool for now. | I |
| I5 | `Tapahtumatunnus` is the transaction identity. Several exports may overlap; transactions are de-duplicated on it. | I |
| I6 | `Maksutapa` values are enumerated in `rules.toml` `[mobilepay]`: `payment_in` (`Maksa Lippaaseen`), `fee` (`Palvelumaksu`), `expense` (paid from MobilePay), `payout` (to the bank, already booked from the bank feed, never posted). A value in no list is still posted, as its own document in K7's shape, flagged and tagged `unknown-type` and not also `unclassified`, since it never enters classification: the money is real and belongs in NoCFO whatever its type turns out to be. | P |
| I7 | Only EUR. A non-EUR row is a CLI-side row error, for the same reason as I2: the posted amount would be wrong outright, not merely unclassified. `--force` drops the row (P9). | P |
| I8 | `Nimi` is read and, for a transaction that posts, carried to NoCFO in the attached listing of its document (P11, P12); it is never written to `match.csv` or `plan.csv` (P6). | P |

## 2. Classification (offset account)

Every posted line needs an offset account. Signals, in priority order:

| id | rule | status |
| --- | --- | --- |
| K1 | Rules file (`rules.toml`): `[[classification.income]]` and `[[classification.expense]]` lists, each rule an account number, a display name and one or more predicates. Income lines and MP payments in use the income list; till expenses and MP expenses use the expense list. A rule hits a line when every predicate it carries holds: `keywords`, the line's text contains one of the case-insensitive substrings; `no_text = true`, the line's text is blank; `from`/`to`, the line's date is within the inclusive window, a missing bound being open; `max_amount`, the line's unsigned amount is at most this; `min_per_day`, at least this many lines of the same day, book lines and MP transactions together, were classified to the rule's account by a preceding rule. A rule without any predicate is an error (V1). The date of a book line is `pvm` and of a MobilePay transaction its own date. The first rule that hits wins; file order is the priority. A rule may also carry `flag = true`: every line it classifies is posted as usual, but its document is flagged and tagged `flagged` in NoCFO (P7), for rules whose evidence is circumstantial. | P |
| K2 | The text of a cash-book line is `mita`. | I |
| K3 | The text of a MobilePay transaction is its `Viesti` and, when the transaction is matched (M-rules) to a book line, that line's `mita` as well: keywords are sought in both and `no_text` holds only when both are blank. The rules are applied once per transaction, so file order decides between a hit on `Viesti` and a hit on `mita`. Applies to payments in and to MP expenses alike. | I |
| K4 | Day-majority: an MP transaction still unclassified takes the account that at least `classification.majority_min` other MP transactions on the same day were classified to, if that account holds a strict majority of that day's classified MP transactions. | I |
| K5 | Same-amount: an MP transaction still unclassified that has the same amount that at least `classification.same_amount_min` other MP transactions on the same or adjacent day have, is classified like the majority of those having the same amount. | I |
| K6 | Domain-specific rules need no code. The earlier cafe rule is now a K1 rule using predicates already defined there (`no_text`, `max_amount`, `min_per_day`), placed last in the income list: an unmarked transaction of at most `max_amount` on a day on which the preceding rules already put at least `min_per_day` lines on the cafe account goes there too. | I |
| K7 | Otherwise the item is still recorded, posted balanced against the clearing account named `[accounts].unclassified` in `rules.toml`, flagged and tagged `unclassified` (P7); the bookkeeper changes the one account in NoCFO, and the payment account's balance is right from the start. It is one document on its own, never coalesced, with `?` for the offset name in its description (C5), as for every document whose offset is the clearing account. The offset-less draft that NoCFO's own bank imports show is not available: the API refuses a blueprint with a payment account and no offset entries (`Kredit-viennit puuttuvat`) and an unbalanced one, and those drafts carry their amount in the read-only `import_data`. | P |
| K8 | Fees classify to `[accounts].fees` unconditionally. | I |

## 3. Matching (book ↔ MobilePay)

Matching is for cash book lines with `tapa` = MP or unmarked-income only. Cash lines (KÄT etc) are never
matched (but see M7). Candidates are unmatched MP rows of the same sign (payments in for
income lines, MP expenses for expense lines) within the time window, which runs from
`matching.days_before` days before the book date to `matching.days_after` days after it.

| id | rule | status |
| --- | --- | --- |
| M1 | Same day, exact amount, only one transaction with that exact amount. | I |
| M2 | Same day, several transactions of the exact amount, none yet matched. If exactly one has a `Viesti` sharing a word with the book `mita`, it matches; otherwise the earliest by time of day. They are interchangeable for classification, so the choice affects only the report. | I |
| M3 | Same day, a subset of at most `matching.max_subset` transactions summing exactly to the amount. | I |
| M4 | As M3 but within the window. | I |
| M5 | Near miss: as M4 but the sum differs by at most `matching.tolerance`. Reported as `contradiction`; MobilePay wins, nothing is corrected in the book. | I |
| M6 | An MP-marked book line with no match at any tier, dated within the MobilePay export's own date range (its earliest to latest transaction), is counted `book-only` in the run's stderr summary (R4) and makes the run exit non-zero (P9): MobilePay is authoritative, so a line it does not cover is a genuine inconsistency with no document to flag, not something NoCFO can absorb. One dated outside that range is simply skipped, no count and no effect on the exit status: MobilePay cannot be expected to cover it; its `match.csv` row keeps the tier `book-only` and carries the note `outside-export` (R1). Either way it is still not posted. | P |
| M7 | A cash-marked book line whose amount equals an unmatched same-day MP transaction is noted `possible-mp` in `match.csv` and counted in the run's stderr summary (R4) — the one case a physical cash count, which already runs independently, would take time to catch. It is still posted as cash, unflagged: the amount posted is correct either way, so no draft needs a bookkeeper's attention, and the exit status is unaffected (P9). | P |
| M8 | When several assignments satisfy M1–M5, choose the one with the lowest cost: unmatched lines, then near misses, then total date distance, then subset size; a transaction whose `Viesti` shares a word with the book `mita` lowers the cost. Weights live in `[matching]`. A tie leaves the line itself unmatched and reported `ambiguous` in `match.csv`, its `mp_ids` listing the transactions of the equally cheap subsets (R1), so a hand correction knows what to choose; those transactions return to the unmatched pool and post from the export on their own (M9), flagged and tagged `ambiguous`, so the drafts are visible without forcing a rerun. | P |
| M9 | An unmarked income line that matches at M1–M4 is treated as MP (hint only, not posted). One that does not match is treated as cash, flagged and tagged `assumed-cash`. One left ambiguous (M8) is MP all the same and not posted; its transactions are posted without its `mita` as a hint (K3), and settling it in `match.csv` only restores the hint. | P |
| M10 | An unmarked expense line was paid from the till: it is posted as a cash expense, flagged and tagged `unmarked-expense`, so that the paper receipt is checked. | P |
| M11 | An MP-marked expense line matches MP expense rows with M1–M8 applied to the negative amounts. An MP expense row with no book match is still recorded (K3 on `Viesti`), flagged and tagged `mp-expense-unbooked`, since for now every MP expense SHOULD also be in the book. | P |

## 4. Coalescing

| id | rule | status |
| --- | --- | --- |
| C1 | Postable lines are grouped by (date, payment account, offset account, tag set) and summed; one document per group, flagged exactly when its tag set is non-empty, so a tagged item never dilutes into an untagged document (P7). | P |
| C2 | Runs of consecutive days with the same (payment account, offset account, tag set) are merged into one document, dated the last day, when `[coalesce.<payment>]` allows: `max_days` bounds the number of days with postings in the run and `max_gap` the allowed gap between them (default 1, i.e. strictly consecutive). Default `max_days` is 3 for cash and 4 for MobilePay. | I |
| C3 | Fees use the same mechanism with their own `[coalesce.fees]` block, default `max_days = 7`. | I |
| C4 | Expenses paid from the till are never coalesced: one document per book line, receipt count in the description. | I |
| C5 | Description: `<marker> <payment name>/<offset name> <date>[–<last date>] (<n>)`; for a till expense `<marker> Kassa/<offset name> <date> <mita> (<k> kuittia)`. The marker is `[posting].marker`, names come from `rules.toml`. | I |

Every document lists its original rows, in human-readable form, in an attached text file (P12): a coalesced
document is then a self-contained voucher, and `huom` and `Nimi` reach NoCFO for a single-row document too
(P10, P11).

The paper book is the daily record the law requires for cash; it is archived for six years, so
the summary posting is a permitted combined entry.

## 5. Posting

| id | rule | status |
| --- | --- | --- |
| P1 | `plan.csv` is the reviewable intermediate: `date;payment_account;offset_account;amount;lines;receipts;tags;description`, sorted by date, payment account, offset account. `amount` is the change of the payment account (income positive, expenses and fees negative); `receipts` is the number of paper receipts expected (sum of `kuitti`); `tags` is the `+`-joined reason tags of its lines (empty when none), the same tags the document carries in NoCFO (P7). `record` renders the documents from it deterministically, every document a draft; there is no intermediate documents CSV. | P |
| P2 | Income: `debet_account_id` = payment account, `credit_entries` on the offset account. Expense: `credit_account_id` = the paying account (cash or MobilePay), `debet_entries` on the expense account. Fee: as an expense from MobilePay on the fee account. One entry on each side; the rows are in the attached listing (P12). Entry amounts are euros with two decimals (verified on api-tst 2026-10-06; the spec's "smallest currency unit" is wrong), written from `decimal` as JSON numbers, never through `float32`; VAT fields are left to the API's defaults (`Veroton`). | P |
| P3 | Account numbers in `rules.toml` are resolved to ids once per run through the accounts list of the business. | P |
| P4 | *Withdrawn: every document is a draft, see P1.* | — |
| P5 | Idempotency: recording an unchanged plan again changes nothing in NoCFO; when the plan changes, the documents in NoCFO change with it. Overlapping exports are handled by I5. Drafts and finalised documents are told apart, and nothing is deleted. First version (agreed 2026-10-06), for the run repeated after a correction: `record` lists the business's documents dated within the plan's range (`date_from`/`date_to`) whose description starts with the marker; a planned document equal to one of them in date, payment account, offset account, amount and description is not created again, the rest are created, and the marker drafts no planned document claimed are listed on stderr; nothing is modified or deleted, a finalised document least of all. *Still open:* how a changed row is reconciled with the document it once produced. | P |
| P6 | Personal names (`Nimi`, and any payer/payee name the cash book might carry) never reach a generated CSV file (`match.csv`, `plan.csv`): those are reviewable, diffable, and may end up committed as test fixtures. They do reach the NoCFO document itself (I8, I3, P10, P11), where NoCFO's own matching and search can use them; that is wanted, not merely tolerated. | A |
| P7 | A document whose lines carry any reason tag is posted flagged (`is_flagged = true`) and carrying those tags (`tag_ids`), one per distinct reason: `flagged` (K1), `unclassified` (K7), `unknown-type` (I6), `assumed-cash` (M9), `ambiguous` (M8), `unmarked-expense` (M10), `mp-expense-unbooked` (M11). Tag names are the bare reason codes, found or created once per run in the target business; a run never creates the same tag twice, and the API refuses a duplicate name anyway (verified). `possible-mp` and `book-only` are never tags: they carry no document to flag (M7, M6). | P |
| P8 | `record` is the one command for ordinary use: it matches, plans and posts in a single run against a business. With `--dry-run` it prints `plan.csv` instead of posting. `match` and `plan` remain as offline previews needing no network; `--match` on either takes a `match.csv` as given instead of matching afresh, for the rare case the automatic match needs correcting by hand. | P |
| P9 | Exit status and `--force`: a row the tool itself cannot resolve (I2, I7) is an input error like any other malformed row (a bad date, a bad amount); an MP-marked book line MobilePay does not cover (M6) is a data inconsistency with no document to carry it. Either kind stops the run with exit status 65 (`EX_DATAERR`) and blocks the upload by default; `--force` drops just the offending rows, each named on stderr — M6's stay unposted exactly as they already are — and uploads the rest. Nothing resolved instead by flagging and tagging a draft in NoCFO (K1, K7, I6, M8, M9, M10, M11) ever affects the exit status, since that work happens there, not at the CLI; nor does `possible-mp` (M7), which needs no flag at all since the amount posted is already correct. A malformed header, a malformed rules file, a missing V2 account, or a malformed or inconsistent `--match` file stops the run regardless of `--force`: there is no single row to drop and no safe default to fall back on. | P |
| P10 | A book line's `huom`, when present, reaches NoCFO on its row's line in the attached listing of the document it posts into (P12). A MobilePay transaction has no equivalent field. | P |
| P11 | A MobilePay transaction's `Nimi`, when present, reaches NoCFO the same way: on its row's line in the attached listing (P12). A book line has no equivalent field. | P |
| P12 | The listing: a UTF-8 plain-text file attached to every document at creation (`POST /file_upload/` with base64 content, then `attachment_ids`), named after the description with `/` replaced by `-` and `.txt` appended. Its first line is the description; then one line per source row in the document's order, giving the date, the source (book page, or MobilePay with the time of day), the text, the remark or name, the signed amount with a decimal comma, and the receipt count or the transaction id. A document has no free-text field beyond its 256-character `description`, which is why a file carries the rows; one entry per row, verified to work as well, was passed over because a document with one entry per side is quicker to accept and to correct. | P |

## 6. Reports

| id | rule | status |
| --- | --- | --- |
| R1 | `match.csv`: `book_page;book_date;book_text;book_amount;book_tapa;tier;mp_ids;mp_sum;note`, one row per book line in file order. `book_amount` is signed (expenses negative). Tiers: `M1`–`M5`, `cash`, `book-only`, `ambiguous`. `mp_ids` is `+`-joined: the transactions matched, or for an `ambiguous` line those of its equally cheap subsets (M8). Notes: `contradiction` (M5), `possible-mp` (M7), `assumed-cash` (M9), `unmarked-expense` (M10), `outside-export` (M6). | P |
| R2 | *Withdrawn: every item needing attention is flagged and tagged on its own draft in NoCFO instead (K1, K7, I6, M8, M9, M10, M11); see P7 and R4.* | — |
| R3 | Amounts are `decimal` end to end (house rule). | A |
| R4 | The run ends with a summary on stderr: one line per tag of P7 with its non-zero count of documents this run, and one line per `book-only` and `possible-mp` book line (M6, M7), naming it by page, date and amount, since `record` writes no `match.csv` to hold the detail. A `book-only` line is why the run exits non-zero (M6, P9); a `possible-mp` line never affects the exit status (M7). Nothing here is written to a file; the detail is in the possibly flagged, tagged document drafts themselves, or in the `plan.csv` output for a `--dry-run`. | P |

## 7. Rules file

| id | rule | status |
| --- | --- | --- |
| V1 | The file is parsed strictly: an unknown table or key is an error, so a mistyped key cannot silently disable a rule. | I |
| V2 | Every account number in the file, payment, fee and clearing accounts and rule accounts alike, must exist in the business. `cashbook check` verifies this against the live accounts list and warns where the NoCFO account name differs from the `name` in the file. `record` runs the same check first and refuses on any missing account. | I |
| V3 | `cashbook seed` writes a rules file skeleton from the live accounts list: the payment, fee and clearing accounts left blank, and one commented-out rule stub per revenue and expense account with empty keywords, in account-number order. Keyword inference from examples is not algorithmic; keywords are written by hand, or later derived from corrected `match.csv`/`plan.csv` files. | I |

## 8. CLI surface (proposed)

```
nocfo cashbook match   -k kassakirja.csv -m mobilepay.csv [-r rules.toml] [--from --to]                      → match.csv
nocfo cashbook plan    -k ... -m ... [-r ...] [--match match.csv] [--from --to]                               → plan.csv
nocfo cashbook record  -b <slug> -k ... -m ... [-r ...] [--match match.csv] [--from --to] [--dry-run] [--force]
nocfo cashbook check   -b <slug> -r rules.toml                                                                → report, exit code
nocfo cashbook seed    -b <slug>                                                                              → rules.toml skeleton
```

`match` and `plan` need no network and remain offline previews; `check`, `seed` and `record` do. `record` is
the one command for ordinary use (P8): it matches, plans and posts in a single run, printing `plan.csv`
instead of posting when `--dry-run` is given. Like `plan`, it re-runs the matching unless `--match` supplies
a (possibly hand-corrected) `match.csv`; the tiers and `mp_ids` in it are taken as given. A book line is
posted only at tier `cash`, so a cash-marked line given an M-tier by hand becomes a hint like an MP-marked
one and its money is not posted twice. Exit status and `--force` are P9: `match` and `plan` end with the R4
summary as well but exit 0 on anything they could read, since the summary and the CSV are their whole point;
`record --dry-run` exits as the upload would have. `record` uses the existing profile and verbose flags.

## 9. Open questions

1. Account numbers for Kassa, MobilePay Box, fees, and the offsets (user to
   fill `rules.toml`; `cashbook seed` will help once it exists).
2. `BlueprintEntryRequest.amount` is documented as "smallest currency unit" but typed
   `float32`; verify against `bookkeeping_blueprint_retrieve` before P2 is implemented.
   → Closed 2026-10-06: euros with two decimals, both on api-tst's existing documents and on a probe
   (`10.00` posts as 10 €); `bookkeeping_blueprint_retrieve` is not an operation in the spec (P2).
3. Does the Box export ever contain payouts to the bank, and under which `Maksutapa`? (I6.)
   Owner: Yes, it does, but we don't have an answer for now. We have to guess and make it a parameter in the TOML, as others.
   This matters less now that a value guessed wrong is still posted, flagged `unknown-type`, rather than dropped (I6).
4. Whether the API accepts a draft whose blueprint has the payment account but no offset
   entries (K7). NoCFO's own bank imports look like that, so probably yes; verify online.
   → Closed 2026-10-06: it does not (`Kredit-viennit puuttuvat`), nor an unbalanced one; an empty
   blueprint is accepted. See K7's open design and question 8.
5. Whether M7 is worth keeping once MP is no longer written into the book at all.
   Owner: Most probably not, but that is for later => to the ROADMAP once we are otherwise ready.
   (The current mechanism was demoted to a stderr count and a `match.csv` note on 2026-10-05; this
   question is about dropping it altogether, not about today's form.)
6. The actual `Maksutapa` values of MobilePay expenses and payouts (I6).
   Owner: We don't have an answer for now. We have to guess and make it a parameter in the TOML, as others.
   As with 3, a wrong guess now only means a document is flagged `unknown-type` instead of correctly typed.
7. Should the matching window be asymmetric? Payments tend to arrive on or after the event
   day, rarely before. `days_before`/`days_after` instead of `window_days` would express that.
   Owner: Yes, it should be asymmetric.  Please amend M4 accordingly.
   → Done: `days_before`/`days_after`, see the section 3 intro.
8. K7's shape now that an offset-less draft is impossible: a clearing account (recommended) or an
   empty draft; see K7.
   Owner (2026-10-06): the clearing account. → Done: `[accounts].unclassified` (K7, I6, V2, V3).
9. The per-row listing of a coalesced document: one entry per row (recommended) or an attached
   file; see section 4.
   Owner (2026-10-06): the attached file, as less work when accepting the drafts and still a full
   record in NoCFO. → Done: P12, for every document.

## 10. Decision log

- 2026-09-29 — Library plus CLI verbs first; app or service later on the same library.
- 2026-09-29 — MobilePay authoritative; book MP lines are hints only, never posted.
- 2026-09-29 — Kassa is not reconcilable; no cash-balance logic.
- 2026-09-29 — Unmarked expenses are reimbursed private outlays; excluded from posting (M10).
- 2026-09-29 — No payer names in documents (P6).
- 2026-09-29 — Practice change: stop writing MP into the book once the rules file is good enough.
- 2026-09-29 — Every document is a draft; no `--final` (P1, P4 withdrawn).
- 2026-09-29 — Reimbursed outlays are recorded after all, against a liability, flagged (M10 revised).
- 2026-09-29 — Paper books are archived six years; multi-day coalescing of cash is permitted (C2).
- 2026-09-29 — Unclassified items are recorded as offset-less drafts; no suspense account (K7).
- 2026-09-29 — Delete-and-replace idempotency withdrawn; P5 to be designed separately.
- 2026-09-29 — CLI verb `post` renamed `record`.
- 2026-09-30 — M2 added (several equal same-day amounts); M-rules renumbered.
- 2026-09-30 — Matching window is asymmetric: `days_before`/`days_after`.
- 2026-09-30 — A classification rule may carry a date window (`from`/`to`); keywords are optional within one (K1).
- 2026-09-30 — Unmarked expenses were paid from the till: posted as cash with a receipt reminder in `review.csv`; the reimbursements role is dropped (M10 revised again).
- 2026-09-30 — Rules file validated strictly and against live accounts (V1–V2); `seed` produces a skeleton only (V3).
- 2026-10-01 — The cafe rule is an ordinary rule: predicates `no_text`, `max_amount` and `min_per_day` added to K1, the `[classification.cafe]` table dropped (K6). A MobilePay transaction's text is `Viesti` and the matched `mita` together, one pass (K3).
- 2026-10-01 — Matching implemented: the M8 cost chooses the assignment and the tier is read off the chosen subset; same-day transactions of the same amount are interchangeable, the earliest taken (M2 generalised); an unmarked income line never takes a near miss (M9); lines sharing no candidate are assigned independently.
- 2026-10-01 — A rule may carry `flag = true`: its lines are reported `flagged` in `review.csv` and counted per document in `plan.csv` (K1, R2, P1).
- 2026-10-01 — `max_days` counts the days with postings in a run, not calendar days (C2).
- 2026-10-01 — An unclassified item is one document on its own, `?` for the offset name (K7, C5).
- 2026-10-01 — An ambiguous unmarked income line is MP under M9 and not posted; settling it in `match.csv` only restores the K3 hint (M8, M9).
- 2026-10-01 — Plan implemented: `cashbook plan` classifies (K1–K8), coalesces (C1–C5) and writes `plan.csv` (P1) and `review.csv` (R2); `--match` takes a `match.csv` as given, its tiers and ids checked against the inputs.
- 2026-10-05 — A book line is posted only at tier `cash`, whatever its marking (section 8). `review.csv` order fixed (R2).
- 2026-10-05 — Idempotency: an unchanged plan changes nothing in NoCFO; a changed plan changes the documents (P5).
- 2026-10-05 — Purpose recorded (section 0): drafts in NoCFO with the least command-line and CSV work; a run normally happens once, at most twice.
- 2026-10-05 — `review.csv` withdrawn (R2). Every item needing attention is instead flagged (`is_flagged`) and tagged (`tag_ids`) on its own draft, using NoCFO tags now that the tool supports them: `flagged`, `unclassified`, `unknown-type`, `assumed-cash`, `ambiguous`, `unmarked-expense`, `mp-expense-unbooked`, found or created once per run by their bare reason code (P7). Coalescing groups by tag set too, so a tagged item never merges into an untagged document (C1).
- 2026-10-05 — `record` becomes the one command for ordinary use: match, plan and post in a single run; `--dry-run` prints `plan.csv` instead of posting. `match` and `plan` remain as offline previews; `--match` stays for the rare hand correction (P8, section 8).
- 2026-10-05 — Exit status and `--force` (point 2 of this date's discussion): non-zero and upload-blocking only for CLI-side row errors (I2, I7) and structural failures (malformed header, rules file, `--match` file, or a missing V2 account); `--force` drops the offending rows and uploads the rest. Nothing resolved by a flag or a tag affects the exit status, since that work happens in NoCFO, not at the CLI (P9).
- 2026-10-05 — `possible-mp` demoted from a review item to a `match.csv` note plus a stderr count (R4): the amount posted is correct either way, so no draft needs flagging; the one real risk, a genuine double-booking, is already caught, if slowly, by the periodic physical cash count (M7).
- 2026-10-05 — `contradiction` needs no CLI-side handling: M5 already resolves it (MobilePay wins); it stays in `match.csv` only, with no tag and no stderr count.
- 2026-10-05 — `unknown-type` MobilePay rows are posted after all, flagged and tagged, instead of withheld: the money is real regardless of its guessed type (I6).
- 2026-10-05 — `book-only` mostly disappears once book lines dated outside the MobilePay export's own date range are skipped rather than matched against it (M6).
- 2026-10-06 — `huom` (book lines) and `Nimi` (MobilePay transactions) reach NoCFO after all, as part of a document's free-text field, alongside the per-row listing a coalesced document carries; they still never reach a generated CSV file (I3, I8, P6 revised, P10, P11).
- 2026-10-06 — `book-only` causes a non-zero exit (joining I2, I7 under P9), since MobilePay not covering a marked line is a data inconsistency with no document to flag; `possible-mp` confirmed to never affect it, since the money is already correctly posted (M6, M7).
- 2026-10-06 — api-tst probed: entry amounts are euros (question 2 closed); an offset-less or unbalanced blueprint is refused and an empty one accepted (question 4 closed, K7 reopened as question 8); `is_flagged`, `tag_ids` and `attachment_ids` are honoured on create, a duplicate tag name is refused, both upload forms work, and `description` is capped at 256 characters, so a coalesced document's per-row listing goes into its entries or into an attachment (question 9).
- 2026-10-06 — `tags` in `plan.csv` are `+`-joined like `mp_ids` (P1); a skipped MP-marked line keeps the tier `book-only` with the note `outside-export` (M6, R1); an ambiguous line's `mp_ids` list the contested transactions (M8, R1); a document is flagged exactly when it is tagged (C1, C2); `record` builds the documents straight from the plan, no documents CSV (P1); `--force` drops any malformed row and the blocking exit status is 65 (P9); the stderr summary names each `book-only` and `possible-mp` line and counts tagged documents (R4).
- 2026-10-06 — P5 first version agreed: a planned document equal in date, accounts, amount and description to an existing marker document is not created again; marker drafts left unclaimed are listed; nothing is modified or deleted.
- 2026-10-06 — Unclassified and unknown-type items post against a clearing account, `[accounts].unclassified`, flagged and tagged; the 2026-09-29 decision against a suspense account is reversed, the offset-less draft being impossible (K7, I6, question 8).
- 2026-10-06 — Every document carries its rows as an attached text file, named after its description (P12); one entry per row passed over (question 9). `match` and `plan` print the R4 summary but always exit 0 on readable input; the P9 exit status is `record`'s (section 8).
