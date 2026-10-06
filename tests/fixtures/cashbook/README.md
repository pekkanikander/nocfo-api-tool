# Cash-book fixtures

Each directory is one case from `REQUIREMENTS-cashbook.md`; the rules it exercises are listed
below. Inputs are `kassakirja.csv` and `mobilepay.csv`, expected outputs `match.csv` and
`plan.csv`, all against the shared `rules.toml` unless the directory has its own. All data is synthetic; names are single letters.

| case | rules |
| --- | --- |
| exact-multi | M3, K3 (hint fills empty `Viesti`), C1 |
| unmarked-tulo | M1, M7, M9, M10 (posted as cash, tagged `unmarked-expense`), K1 rule order, K3, C4 |
| cross-day-near-miss | M4, M5, M8 (coherence beats an exact but scattered subset), K3 vs own `Viesti`, C2 for both cash (19–20) and MobilePay (21–22) |
| ambiguous-tie | M8 (a tie: the contested transactions post on their own, tagged `ambiguous`), M9 (no hint), M6 (`outside-export`), I6 (`unknown-type` on the clearing account), K7, C1 (grouping by tag set) |
