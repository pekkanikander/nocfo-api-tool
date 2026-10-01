# REQUIREMENTS — cash book and MobilePay Box posting

Status legend: **P** proposed, **A** agreed, **I** implemented (with tests).
Every rule that changes an output has at least one fixture under `tests/fixtures/cashbook/`;
a fixture is a directory with `kassakirja.csv`, `mobilepay.csv`, the expected `match.csv`,
`plan.csv` and `review.csv`, and optionally its own `rules.toml` (else the shared one).
The real data stays in `tmp/` (gitignored); fixtures are synthetic.

Last edited 2026-10-01.

---

## 0. Context and stakes

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
  rather than by re-running the tool.
- The library is pure (no NoCFO dependency); the CLI verbs wrap it and hand the result to the
  existing `create documents` machinery. A service or app can come later on the same library.

---

## 1. Inputs

| id | rule | status |
| --- | --- | --- |
| I1 | Cash book CSV: `sivu;pvm;mita;tulo;meno;tapa;kuitti;huom`, `;` separated, decimal comma, UTF-8 with or without BOM. `pvm` is ISO. Exactly one of `tulo`/`meno` is non-empty. | I |
| I2 | `tapa` is normalised: `MP`, `MP?` → MobilePay; `KÄT`, `KT`, `KÄ` → cash; empty → unmarked. Anything else is a review item. | P |
| I3 | `kuitti` is a receipt count (integer) or empty. `huom` is free text and carried into reports, never into NoCFO. | I |
| I4 | MobilePay export CSV: `Päivämäärä,Nimi,Tyyppi,Numero,Viesti,Summa,Valuutta,Maksutapa,Tapahtumatunnus`; date `dd/MM/yyyy HH:mm` local time; decimal point. The XLSX is converted outside the tool for now. | I |
| I5 | `Tapahtumatunnus` is the transaction identity. Several exports may overlap; transactions are de-duplicated on it. | I |
| I6 | `Maksutapa` values are enumerated in `rules.toml` `[mobilepay]`: `payment_in` (`Maksa Lippaaseen`), `fee` (`Palvelumaksu`), `expense` (paid from MobilePay), `payout` (to the bank, already booked from the bank feed, never posted). A value in no list goes to review as `unknown-type` and is not posted. | I |
| I7 | Only EUR. A non-EUR row is a hard error. | I |
| I8 | `Nimi` is read for matching heuristics only and never written to any output that reaches NoCFO (see P6). | I |

## 2. Classification (offset account)

Every posted line needs an offset account. Signals, in priority order:

| id | rule | status |
| --- | --- | --- |
| K1 | Rules file (`rules.toml`): `[[classification.income]]` and `[[classification.expense]]` lists, each rule an account number, a display name and one or more predicates. Income lines and MP payments in use the income list; till expenses and MP expenses use the expense list. A rule hits a line when every predicate it carries holds: `keywords`, the line's text contains one of the case-insensitive substrings; `no_text = true`, the line's text is blank; `from`/`to`, the line's date is within the inclusive window, a missing bound being open; `max_amount`, the line's unsigned amount is at most this; `min_per_day`, at least this many lines of the same day, book lines and MP transactions together, were classified to the rule's account by a preceding rule. A rule without any predicate is an error (V1). The date of a book line is `pvm` and of a MobilePay transaction its own date. The first rule that hits wins; file order is the priority. A rule may also carry `flag = true`: every line it classifies is posted as usual but reported `flagged` in `review.csv`, for rules whose evidence is circumstantial. | P |
| K2 | The text of a cash-book line is `mita`. | P |
| K3 | The text of a MobilePay transaction is its `Viesti` and, when the transaction is matched (M-rules) to a book line, that line's `mita` as well: keywords are sought in both and `no_text` holds only when both are blank. The rules are applied once per transaction, so file order decides between a hit on `Viesti` and a hit on `mita`. Applies to payments in and to MP expenses alike. | P |
| K4 | Day-majority: an MP transaction still unclassified takes the account that at least `classification.majority_min` other MP transactions on the same day were classified to, if that account holds a strict majority of that day's classified MP transactions. | P |
| K5 | Same-amount: an MP transaction still unclassified that has the same amount that at least `classification.same_amount_min` other MP transactions on the same or adjacent day have, is classified like the majority of those having the same amount. | P |
| K6 | Domain-specific rules need no code. The cafe rule is a K1 rule with `no_text`, `max_amount` and `min_per_day`, placed last in the income list: an unmarked transaction of at most `max_amount` on a day on which the preceding rules already put at least `min_per_day` lines on the cafe account goes there too. | P |
| K7 | Otherwise the item goes to `review.csv` with reason `unclassified` and is still recorded, as a draft with the payment account set and no offset account, which is the shape NoCFO's own bank imports have; the bookkeeper completes it in NoCFO. | P |
| K8 | Fees classify to `[accounts].fees` unconditionally. | P |

## 3. Matching (book ↔ MobilePay)

Matching is for book lines with `tapa` = MP or unmarked-income only. Cash lines are never
matched (but see M7). Candidates are unmatched MP rows of the same sign (payments in for
income lines, MP expenses for expense lines) within the window, which runs from
`matching.days_before` days before the book date to `matching.days_after` days after it.

| id | rule | status |
| --- | --- | --- |
| M1 | Same day, exact amount, only one transaction with that exact amount. | I |
| M2 | Same day, several transactions of the exact amount, none yet matched. If exactly one has a `Viesti` sharing a word with the book `mita`, it matches; otherwise the earliest by time of day. They are interchangeable for classification, so the choice affects only the report. | I |
| M3 | Same day, a subset of at most `matching.max_subset` transactions summing exactly to the amount. | I |
| M4 | As M3 but within the window. | I |
| M5 | Near miss: as M4 but the sum differs by at most `matching.tolerance`. Reported as `contradiction`; MobilePay wins, nothing is corrected in the book. | I |
| M6 | An MP-marked book line with no match at any tier is reported `book-only`. It is still not posted. | I |
| M7 | A cash-marked book line whose amount equals an unmatched same-day MP transaction is reported `possible-mp` as a warning. It is still posted as cash. | I |
| M8 | When several assignments satisfy M1–M5, choose the one with the lowest cost: unmatched lines, then near misses, then total date distance, then subset size; a transaction whose `Viesti` shares a word with the book `mita` lowers the cost. Weights live in `[matching]`. Ties are reported `ambiguous` and left unmatched. | I |
| M9 | An unmarked income line that matches at M1–M4 is treated as MP (hint only, not posted). One that does not match is treated as cash and reported `assumed-cash`. | I |
| M10 | An unmarked expense line was paid from the till: it is posted as a cash expense and reported `unmarked-expense` in `review.csv`, so that the paper receipt is checked. | I |
| M11 | An MP-marked expense line matches MP expense rows with M1–M8 applied to the negative amounts. An MP expense row with no book match is still recorded (K3 on `Viesti`) but reported `mp-expense-unbooked`, since for now every MP expense SHOULD also be in the book. | P |

## 4. Coalescing

| id | rule | status |
| --- | --- | --- |
| C1 | Postable lines are grouped by (date, payment account, offset account) and summed; one document per group. | A |
| C2 | Runs of consecutive days with the same (payment account, offset account) are merged into one document dated the last day, when `[coalesce.<payment>]` allows: `max_days` bounds the run length and `max_gap` the allowed gap between days (default 1, i.e. strictly consecutive). Default `max_days` is 3 for cash and 4 for MobilePay. | P |
| C3 | Fees use the same mechanism with their own `[coalesce.fees]` block, default `max_days = 7`. | P |
| C4 | Expenses paid from the till are never coalesced: one document per book line, receipt count in the description. | P |
| C5 | Description: `<marker> <payment name>/<offset name> <date>[–<last date>] (<n>)`; for a till expense `<marker> Kassa/<offset name> <date> <mita> (<k> kuittia)`. The marker is `[posting].marker`, names come from `rules.toml`. | P |

The paper book is the daily record the law requires for cash; it is archived for six years, so
the summary posting is a permitted combined entry.

## 5. Posting

| id | rule | status |
| --- | --- | --- |
| P1 | `plan.csv` is the reviewable intermediate: `date;payment_account;offset_account;amount;lines;receipts;flagged;description`, sorted by date, payment account, offset account. `amount` is the change of the payment account (income positive, expenses and fees negative); `receipts` is the number of paper receipts expected (sum of `kuitti`); `flagged` is the number of its lines reported `flagged` (K1). The documents CSV is rendered from it deterministically, every document a draft. | A |
| P2 | Income: `debet_account_id` = payment account, `credit_entries` = one entry on the offset account. Expense: `credit_account_id` = the paying account (cash or MobilePay), `debet_entries` = one entry on the expense account. Fee: as an expense from MobilePay on the fee account. | P |
| P3 | Account numbers in `rules.toml` are resolved to ids once per run through the accounts list of the business. | P |
| P4 | *Withdrawn: every document is a draft, see P1.* | — |
| P5 | Idempotency: *open, to be designed separately.* The earlier delete-and-replace form is withdrawn; any design must distinguish drafts from finalised documents and never delete. Overlapping exports are handled by I5. | — |
| P6 | No personal name reaches a document. Payer detail, if ever wanted, goes into a separately generated PDF attachment (out of scope now; attachments are not yet covered by the API tool). | A |

## 6. Reports

| id | rule | status |
| --- | --- | --- |
| R1 | `match.csv`: `book_page;book_date;book_text;book_amount;book_tapa;tier;mp_ids;mp_sum;note`, one row per book line in file order. `book_amount` is signed (expenses negative). Tiers: `M1`–`M5`, `cash`, `book-only`, `ambiguous`. `mp_ids` is `+`-joined. | I |
| R2 | `review.csv`: `source;ref;date;amount;text;reason`; source `book` with ref `L<file line>`, or `mp` with the transaction id. Reasons: `unclassified`, `ambiguous`, `book-only`, `contradiction`, `possible-mp`, `assumed-cash`, `unmarked-expense`, `mp-expense-unbooked`, `flagged`, `unknown-type`. The informational tier `cash` appears only in `match.csv`. | P |
| R3 | Amounts are `decimal` end to end (house rule). | A |

## 7. Rules file

| id | rule | status |
| --- | --- | --- |
| V1 | The file is parsed strictly: an unknown table or key is an error, so a mistyped key cannot silently disable a rule. | I |
| V2 | Every account number in the file, payment and fee accounts and rule accounts alike, must exist in the business. `cashbook check` verifies this against the live accounts list and warns where the NoCFO account name differs from the `name` in the file. `record` runs the same check first and refuses on any missing account. | I |
| V3 | `cashbook seed` writes a skeleton from the live accounts list: the payment and fee accounts left blank, and one commented-out rule stub per revenue and expense account with empty keywords, in account-number order. Keyword inference from examples is not algorithmic; keywords are written by hand, or later derived from corrected `match.csv`/`plan.csv` files. | I |

## 8. CLI surface (proposed)

```
nocfo cashbook match  -k kassakirja.csv -m mobilepay.csv [-r rules.toml] [--from --to]   → match.csv
nocfo cashbook plan   -k ... -m ... [-r ...] [--match match.csv] [--review review.csv]     → plan.csv
nocfo cashbook record -b <slug> --plan plan.csv
nocfo cashbook check  -b <slug> -r rules.toml                                              → report, exit code
nocfo cashbook seed   -b <slug>                                                            → rules.toml skeleton
```

`match` and `plan` need no network; `check`, `seed` and `record` do. `plan` re-runs the matching unless `--match` supplies a
(possibly hand-corrected) `match.csv`; the tiers and `mp_ids` in it are taken as given.
`record` uses the existing profile, dry-run and verbose flags. (`record` replaces the earlier
`post`; `enter` and `submit` were the alternatives.)

## 9. Open questions

1. Account numbers for Kassa, MobilePay Box, fees, and the offsets (user to
   fill `rules.toml`; `cashbook seed` will help once it exists).
2. `BlueprintEntryRequest.amount` is documented as "smallest currency unit" but typed
   `float32`; verify against `bookkeeping_blueprint_retrieve` before P2 is implemented.
3. Does the Box export ever contain payouts to the bank, and under which `Maksutapa`? (I6.)
   Owner: Yes, it does, but we don't have an answer for now. We have to guess and make it a parameter in the TOML, as others.
4. Whether the API accepts a draft whose blueprint has the payment account but no offset
   entries (K7). NoCFO's own bank imports look like that, so probably yes; verify online.
5. Whether M7 warnings are worth keeping once MP is no longer written into the book.
   Owner: Most probably not, but that is for later => to the ROADMAP once we are otherwise ready.
6. The actual `Maksutapa` values of MobilePay expenses and payouts (I6).
   Owner: We don't have an answer for now. We have to guess and make it a parameter in the TOML, as others.
7. Should the matching window be asymmetric? Payments tend to arrive on or after the event
   day, rarely before. `days_before`/`days_after` instead of `window_days` would express that.
   Owner: Yes, it should be asymmetric.  Please amend M4 accordingly.
   → Done: `days_before`/`days_after`, see the section 3 intro.

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
