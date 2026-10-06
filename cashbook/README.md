# Cash book to NoCFO

The `nocfo cashbook` verbs turn a handwritten cash book (kassakirja) and a MobilePay Box export
into NoCFO document drafts, so that none of them needs entering by hand. The tool classifies each
item to an offset account by the rules in a `rules.toml`, matches the cash book against the
export so that a MobilePay payment is never posted twice, coalesces the items of consecutive days
into one document each, and records the documents as drafts, each with a text listing of its rows
attached. Anything it is unsure of is still recorded, but flagged and tagged in NoCFO, where the
correction is made.

What is posted:

- **Cash lines** of the book (`tapa` = `KÄT`, `KT` or `KÄ`), income and expenses, against the cash
  account.
- **MobilePay payments in, expenses and fees** from the export, against the MobilePay account.
  The export is authoritative for MobilePay.

What is not posted:

- **Book lines marked `MP`.** They are only hints for classifying the MobilePay transactions
  they match, and a cross-check that the export covers the book.
- **MobilePay payouts** to the bank: those arrive through the bank feed.

Cash is not reconciled: the tool posts what the book says, nothing more.

## Inputs

### The cash book, `kassakirja.csv`

Semicolon-separated, decimal comma, UTF-8 (a byte-order mark is fine), one line per entry:

```
sivu;pvm;mita;tulo;meno;tapa;kuitti;huom
1;2026-07-18;Tanssit;44,70;;MP;;
1;2026-07-18;Tanssit;10,00;;KÄT;;
2;2026-07-19;Kahvimaito;;4,35;;1;Lidl
```

| column | meaning |
| --- | --- |
| `sivu` | page of the paper book |
| `pvm` | date, `YYYY-MM-DD` |
| `mita` | what the line is for; the text the classification keywords are sought in |
| `tulo` / `meno` | income or expense amount; exactly one of the two is given |
| `tapa` | `MP` or `MP?` for MobilePay, `KÄT`, `KT` or `KÄ` for cash, empty when not written down |
| `kuitti` | number of paper receipts, or empty |
| `huom` | a remark; carried into the listing attached to the document, never into a generated CSV |

An unmarked **income** line is matched against the export like an `MP` line: if a MobilePay
transaction fits, it is treated as MobilePay and not posted; if none does, it is posted as cash and
tagged `assumed-cash`. An unmarked **expense** line is always a till expense, posted as cash and
tagged `unmarked-expense`, so that the receipt gets checked.

### The MobilePay Box export, `mobilepay.csv`

The export as MobilePay produces it, converted from XLSX to CSV outside the tool:

```
Päivämäärä,Nimi,Tyyppi,Numero,Viesti,Summa,Valuutta,Maksutapa,Tapahtumatunnus
18/07/2026 19:14,A A,Henkilö,,Tanssit,10,EUR,Maksa Lippaaseen,1001
18/07/2026 19:12,Palvelumaksu,,,,-2,EUR,Palvelumaksu,f-1001
```

`Tapahtumatunnus` identifies a transaction, so overlapping exports may be concatenated: a repeated
id is read once. `Maksutapa` tells payments in, fees, expenses and payouts apart through the
`[mobilepay]` table of the rules file. Only EUR is accepted. `Nimi` is carried into the attached
listing of the document only, never into a generated CSV.

## The rules file, `rules.toml`

Start from `cashbook seed`, which writes the skeleton below filled with the chart of accounts of
the business: the asset and liability accounts listed as candidates for the first table, and one
commented-out rule stub per revenue and expense account.

```toml
[accounts]
cash           = { number = "1900", name = "Kassa" }
mobilepay      = { number = "1930", name = "MobilePay" }
fees           = { number = "8100", name = "Palvelumaksut" }
unclassified   = { number = "1990", name = "Selvittelytili" }

[mobilepay]
payment_in = ["Maksa Lippaaseen"]
fee        = ["Palvelumaksu"]
expense    = []
payout     = []

[matching]
days_before = 1
days_after  = 3
max_subset  = 8
tolerance   = 0.50
weight_unmatched     = 100
weight_near_miss     = 10
weight_date_distance = 1
weight_subset_size   = 0.1
weight_coherence     = -5

[classification]
majority_min    = 3
same_amount_min = 3

[[classification.income]]
account  = "3000"
name     = "Kahvila"
keywords = ["kahvila", "kahvi", "kaffe", "tee"]

[[classification.income]]
account  = "3010"
name     = "Tanssit"
keywords = ["tanssit"]

[[classification.income]]
account     = "3000"
name        = "Kahvila"
no_text     = true
max_amount  = 10.00
min_per_day = 3
flag        = true

[[classification.expense]]
account  = "4100"
name     = "Kahvilan ostot"
keywords = ["kahvi", "maito", "lidl"]

[coalesce.cash]
max_days = 3
max_gap  = 1

[coalesce.mobilepay]
max_days = 4
max_gap  = 1

[coalesce.fees]
max_days = 7
max_gap  = 7

[posting]
marker = "[kassakirja]"
```

| table | what it says |
| --- | --- |
| `[accounts]` | The cash and MobilePay payment accounts, the account the MobilePay fees go to, and the clearing account an item the rules cannot classify is posted against. The `name` is what the document descriptions use; it may differ from the name in NoCFO. |
| `[mobilepay]` | The `Maksutapa` values of the export, by meaning. A value in no list is still posted, against the clearing account, tagged `unknown-type`. |
| `[matching]` | How far from the book date a transaction may be, how many transactions may add up to one book line, how far a near miss may be off, and the weights that choose between equally possible assignments. The defaults are reasonable; change them only when `match.csv` shows a systematic problem. |
| `[classification]` | The thresholds of the two fallbacks for a MobilePay transaction no rule classifies: the account that holds a majority of the day's classified transactions, and the account of the other transactions of the same amount on the same or an adjacent day. |
| `[[classification.income]]`, `[[classification.expense]]` | The rules, tried in file order; the first that hits wins, so put the specific before the general. Income lines and payments in use the income list, till expenses and MobilePay expenses the expense list. A rule hits when every predicate it carries holds. |
| `[coalesce.*]` | How many days with postings a document may span (`max_days`) and how many days without postings may lie between them (`max_gap`), per payment account and for the fees. |
| `[posting]` | The prefix of every description the tool writes. It is how `record` recognises its own documents on a repeated run, so keep it stable and unique to the tool. |

The predicates of a rule:

| predicate | holds when |
| --- | --- |
| `keywords` | the text contains one of the strings, case-insensitively |
| `no_text = true` | the text is blank |
| `from`, `to` | the date is within the inclusive window; a missing bound is open |
| `max_amount` | the unsigned amount is at most this |
| `min_per_day` | at least this many lines of the same day were already classified to the rule's account by the preceding rules |
| `flag = true` | not a predicate: every document the rule classifies into is flagged and tagged `flagged`, for rules whose evidence is circumstantial |

The text of a book line is its `mita`. The text of a MobilePay transaction is its `Viesti` and,
when a book line matches it, that line's `mita` as well. A rule without a predicate is an error.

The file is read strictly: a mistyped table or key is an error, not a rule silently ignored.
Every account number in it must exist in the business; `cashbook check` and `record` both verify
this and warn where the NoCFO name differs from the `name` in the file.

## Workflow

Business selection, `--profile`, `--verbose`, `--in`/`--out` and `--dryrun`/`-n` work as for the
other verbs; the global flags go before `cashbook`.

### 1. Write the rules file

```bash
nocfo cashbook seed -b <slug-or-vat> > rules.toml
nocfo cashbook check -b <slug-or-vat> -r rules.toml
```

Fill in the four accounts, uncomment the stubs for the accounts in use and give them keywords, and
check. The findings go to stdout: a missing account is an error (exit 65), a name that differs from
NoCFO's a warning.

### 2. Preview, when wanted

```bash
nocfo cashbook match -k kassakirja.csv -m mobilepay.csv -r rules.toml > match.csv
nocfo cashbook plan  -k kassakirja.csv -m mobilepay.csv -r rules.toml > plan.csv
```

Both are local: no network, nothing changes. `--from`/`--to` restrict both inputs to a date range,
on every verb. The summary described below goes to stderr; the exit status is 0 whenever the
inputs could be read.

`plan` matches afresh. With `--match match.csv` it takes the tiers and transaction ids of that
file as given instead, which is how a wrong match is corrected by hand: edit the row's `tier` and
`mp_ids`, then plan or record with `--match`. A book line is posted only at tier `cash`, so a line
moved by hand to an `M` tier becomes a hint, as an `MP` line is.

### 3. Record

```bash
nocfo -n cashbook record -b <slug-or-vat> -k kassakirja.csv -m mobilepay.csv -r rules.toml
nocfo    cashbook record -b <slug-or-vat> -k kassakirja.csv -m mobilepay.csv -r rules.toml
```

`record` checks the rules file against the chart of accounts, matches, plans and creates every
planned document as a draft, each with its listing attached and, when it carries reason tags,
flagged and tagged with them. The tags are created in the business on first use. Under `-n` it
prints `plan.csv` instead of uploading and exits as the upload would have, so the first command
shows what the second would do. `--match` works as for `plan`.

**Running it again** after a correction is safe: a planned document already recorded with the same
date, payment account, offset account, amount and description is skipped, and the rest are created.
Nothing is modified or deleted. Marker drafts within the plan's date range that no planned document
claims any more are listed on stderr, under `-n` as well, as
`unclaimed draft: <id>, <date>, <description>`; delete or keep them in NoCFO as the case requires. A changed document is therefore created anew next to the
old draft, which stays until deleted by hand.

### 4. Finish in NoCFO

Open the flagged drafts, fix the account where the tag says so, and approve the rest. The tag names
the reason:

| tag | reason | what to do |
| --- | --- | --- |
| `unclassified` | no rule and no fallback classified the item; it is posted against the clearing account, with `?` for the offset name | change the offset account |
| `unknown-type` | a `Maksutapa` value in no `[mobilepay]` list; posted against the clearing account | change the offset account and add the value to the rules file |
| `flagged` | classified by a rule with `flag = true` | confirm the account |
| `assumed-cash` | an unmarked income line no MobilePay transaction fitted; posted as cash | confirm it was cash |
| `ambiguous` | the line could be matched to several equally plausible sets of transactions; the transactions are posted on their own from the export, without the line's text as a hint | confirm the account, or settle the match in `match.csv` and record again |
| `unmarked-expense` | an expense line without `tapa`; posted as paid from the till | check the receipt |
| `mp-expense-unbooked` | a MobilePay expense with no book line | check that it is legitimate and add it to the book |

A document with any tag is flagged. A tagged item never dilutes into an untagged document: the
grouping that coalesces lines into documents keeps items with different tag sets apart.

## Outputs

### `match.csv`

One row per book line, in file order:

```
book_page;book_date;book_text;book_amount;book_tapa;tier;mp_ids;mp_sum;note
1;2026-07-18;Tanssit;44,70;MP;M3;1001+1002+1003+1004+1005;44,70;
1;2026-07-18;Tanssit;10,00;KÄT;cash;;;
```

| tier | meaning |
| --- | --- |
| `M1` | same day, one transaction of exactly the amount |
| `M2` | same day, one of several transactions of the amount; the one whose `Viesti` shares a word with `mita`, else the earliest |
| `M3` | same day, several transactions adding up to the amount |
| `M4` | as `M3`, within the `[matching]` window |
| `M5` | as `M4`, within `tolerance` of the amount; MobilePay wins, the book is not corrected |
| `cash` | a cash line, or an unmarked income line nothing matched |
| `book-only` | an `MP` line the export does not cover |
| `ambiguous` | several assignments are equally cheap; `mp_ids` lists the transactions of all of them |

`note` is `contradiction` for an `M5`, `possible-mp` for a cash line whose amount equals an
unmatched same-day transaction, `assumed-cash` and `unmarked-expense` for the unmarked lines, and
`outside-export` for a `book-only` line dated outside the export's own range, which the export
cannot be expected to cover.

### `plan.csv`

One row per document, sorted by date, payment account and offset account:

```
date;payment_account;offset_account;amount;lines;receipts;tags;description
2026-07-18;1900;3010;10,00;1;0;;[kassakirja] Kassa/Tanssit 2026-07-18 (1)
2026-07-18;1930;3010;44,70;5;0;;[kassakirja] MobilePay/Tanssit 2026-07-18 (5)
2026-07-18;1930;8100;-2,00;1;0;;[kassakirja] MobilePay/Palvelumaksut 2026-07-18 (1)
```

`amount` is the change of the payment account: income positive, expenses and fees negative.
`lines` is the number of source rows, `receipts` the sum of their `kuitti`, `tags` the `+`-joined
reason tags. The description is `<marker> <payment>/<offset> <date>[–<last date>] (<n>)`; a till
expense, which is never coalesced, reads `<marker> Kassa/<offset> <date> <mita> (<k> kuittia)`.

### The documents

Each document has one entry on each side: income debits the payment account and credits the offset
account, an expense or fee credits the paying account and debits the expense or fee account. The
rows behind it are in the attached text file, named after the description with `.txt` appended:
the description on the first line, then one tab-separated line per row with the date, the source
(`sivu <n>` or `MobilePay <HH:mm>`), the text, the remark or payer name, the signed amount and the
receipt count or transaction id. `huom` and `Nimi` reach NoCFO only there.

A document created by the tool can be found by its marker; the listings are files of the business
and are deleted separately (`list files`, `delete files`), since deleting a document leaves its
attachments behind.

### The summary on stderr

`match`, `plan` and `record` end with one line per `book-only` line within the export's range
(`book-only: page 3, 2026-07-20, 12,00`) and one per `possible-mp` line, naming the transactions
it may be; `plan` and `record` also with one line per reason tag with its count of documents
(`documents tagged assumed-cash: 2`). `record` adds a `Created document <id>` line per upload.

## Errors and `--force`

| situation | behaviour |
| --- | --- |
| malformed header, unparseable rules file, unknown table or key, rule without a predicate, account missing from the business, malformed or inconsistent `--match` file | the run stops, exit 65, regardless of `--force` |
| a malformed row: a bad date or amount, an unknown `tapa`, a non-EUR transaction | the run stops with exit 65, `record` before anything is uploaded; with `--force` (`record` only) the row is dropped, named on stderr as `<file>: <error>, dropped`, and the rest are recorded |
| a `book-only` line within the export's range | `record` stops with exit 65 (`nothing recorded: the book-only lines above are unmatched`); with `--force` the rest are recorded and the line stays unposted, as it would anyway. `match` and `plan` only report it. |
| anything resolved by flagging a draft | never affects the exit status |

Exit status 0 means every planned document is in NoCFO; 65 means an input problem described on
stderr; 64 a usage error; 77 an authentication failure.
