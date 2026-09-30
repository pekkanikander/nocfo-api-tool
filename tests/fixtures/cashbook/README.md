# Cash-book fixtures

Each directory is one case from `REQUIREMENTS-cashbook.md`; the rules it exercises are listed
below. Inputs are `kassakirja.csv` and `mobilepay.csv`, expected outputs `match.csv`,
`plan.csv` and `review.csv`, all against the shared `rules.toml` unless the directory has its
own. All data is synthetic; names are single letters.

| case | rules |
| --- | --- |
| exact-multi | M3, K3 (hint fills empty `Viesti`), K6, C1 |
| unmarked-tulo | M1, M7, M9, M10 (recorded against the liability, receipt demanded), K1 rule order, K3, C4 |
| cross-day-near-miss | M4, M5, M8 (coherence beats an exact but scattered subset), K3 vs own `Viesti`, C2 for both cash (19–20) and MobilePay (21–22) |
