module PlanTests

open System
open System.IO
open Xunit
open Swensen.Unquote
open Nocfo.Cashbook

let private fixtures = Path.Combine(__SOURCE_DIRECTORY__, "fixtures", "cashbook")

let private rules =
    match Rules.parse (File.ReadAllText(Path.Combine(fixtures, "rules.toml"))) with
    | Ok rules -> rules
    | Error errors -> failwithf "%A" errors

let private unwrap (result: Result<'T, string list>) =
    match result with
    | Ok value -> value
    | Error errors -> failwithf "%A" errors

// ── Fixtures ──────────────────────────────────────────────────────────────────

let private fixture (case: string) =
    let read name = File.ReadAllText(Path.Combine(fixtures, case, name))
    let lines = unwrap (CashBook.read (read "kassakirja.csv"))
    let transactions = unwrap (MobilePay.read rules.mobilepay (read "mobilepay.csv"))
    read, lines, transactions

[<Theory>]
[<InlineData("exact-multi")>]
[<InlineData("unmarked-tulo")>]
[<InlineData("cross-day-near-miss")>]
let ``A fixture's plan.csv and review.csv are reproduced exactly`` (case: string) =
    let read, lines, transactions = fixture case
    let plan = Plan.build rules (Matching.run rules lines transactions)
    test <@ PlanReport.render plan.documents = read "plan.csv" @>
    test <@ ReviewReport.render plan.reviews = read "review.csv" @>

[<Theory>]
[<InlineData("exact-multi")>]
[<InlineData("unmarked-tulo")>]
[<InlineData("cross-day-near-miss")>]
let ``A fixture's match.csv read back gives the matches that wrote it`` (case: string) =
    let read, lines, transactions = fixture case
    test <@ MatchReport.read lines transactions (read "match.csv") = Ok (Matching.run rules lines transactions) @>

// ── Hand-built cases ──────────────────────────────────────────────────────────

let private day = DateOnly(2026, 7, 18)

let private line (method: PaymentMethod) (text: string) (amount: decimal) : CashBookLine =
    { file_line = 0; page = "1"; date = day; text = text; amount = amount; method = method
      tapa = ""; receipts = 0; remark = "" }

let private book (lines: CashBookLine list) = lines |> List.mapi (fun index line -> { line with file_line = index + 2 })

let private transaction (kind: TransactionKind) (id: string) (time: string) (text: string) (amount: decimal) : Transaction =
    { id = id; date = day; time = TimeOnly.Parse time; text = text; amount = amount; kind = kind }

let private payment = transaction TransactionKind.PaymentIn

let private rows (text: string) = text.Split('\n', StringSplitOptions.RemoveEmptyEntries) |> List.ofArray |> List.tail

/// The rows of `plan.csv` and of `review.csv`, headers dropped.
let private planned (lines: CashBookLine list) (transactions: Transaction list) =
    let plan = Plan.build rules (Matching.run rules (book lines) transactions)
    rows (PlanReport.render plan.documents), rows (ReviewReport.render plan.reviews)

[<Fact>]
let ``A cash-marked line and an unmarked line matched to nothing are posted as cash, a MobilePay-marked one is not (M6, M9, M10)`` () =
    let documents, reviews =
        planned [ line PaymentMethod.Cash "Tanssit" 10M; line PaymentMethod.Unmarked "Tanssit" 5M
                  line PaymentMethod.Unmarked "Alkufest" -2M; line PaymentMethod.MobilePay "Tanssit" 1M ] []
    test <@ documents = [ "2026-07-18;1900;3010;15,00;2;0;0;[kassakirja] Kassa/Tanssit 2026-07-18 (2)"
                          "2026-07-18;1900;4000;-2,00;1;0;0;[kassakirja] Kassa/Alkufest kulut 2026-07-18 Alkufest (0 kuittia)" ] @>
    test <@ reviews = [ "book;L3;2026-07-18;5,00;Tanssit;assumed-cash"
                        "book;L4;2026-07-18;-2,00;Alkufest;unmarked-expense"
                        "book;L5;2026-07-18;1,00;Tanssit;book-only" ] @>

[<Fact>]
let ``A matched line is not posted; its transaction is, from the MobilePay account, its keywords sought in its Viesti and the line's mita (K3)`` () =
    let documents, reviews =
        planned [ line PaymentMethod.MobilePay "Tanssit" 10M; line PaymentMethod.Unmarked "" 7M ]
                [ payment "1" "16:00" "" 10M; payment "2" "16:10" "Tanssit🌱" 7M ]
    test <@ documents = [ "2026-07-18;1930;3010;17,00;2;0;0;[kassakirja] MobilePay/Tanssit 2026-07-18 (2)" ] @>
    test <@ reviews = [] @>

[<Fact>]
let ``An unclassified item is one document of its own with ? for the offset account, and is reviewed (K7)`` () =
    let documents, reviews =
        planned [ line PaymentMethod.Cash "Zzz" 5M; line PaymentMethod.Cash "Zzz" 5M ] [ payment "1" "16:00" "Yyy" 2M ]
    test <@ documents = [ "2026-07-18;1900;;5,00;1;0;0;[kassakirja] Kassa/? 2026-07-18 (1)"
                          "2026-07-18;1900;;5,00;1;0;0;[kassakirja] Kassa/? 2026-07-18 (1)"
                          "2026-07-18;1930;;2,00;1;0;0;[kassakirja] MobilePay/? 2026-07-18 (1)" ] @>
    test <@ reviews = [ "book;L2;2026-07-18;5,00;Zzz;unclassified"; "book;L3;2026-07-18;5,00;Zzz;unclassified"
                        "mp;1;2026-07-18;2,00;Yyy;unclassified" ] @>

[<Fact>]
let ``A fee is posted from MobilePay to the fee account whatever its text, under the fees block (K8, C3)`` () =
    let fee (id: string) (text: string) = transaction TransactionKind.Fee id "23:59" text -0.50M
    let documents, reviews = planned [] [ fee "f-1" "Kahvila"; { fee "f-2" "" with date = day.AddDays 7 } ]
    test <@ documents = [ "2026-07-25;1930;8100;-1,00;2;0;0;[kassakirja] MobilePay/Palvelumaksut 2026-07-18–2026-07-25 (2)" ] @>
    test <@ reviews = [] @>

[<Fact>]
let ``A payout is neither posted nor reviewed; a transaction of unknown type is reviewed only (I6)`` () =
    let documents, reviews =
        planned [] [ transaction TransactionKind.Payout "p-1" "06:00" "" -100M
                     transaction TransactionKind.Unknown "u-1" "16:00" "Tanssit" 3M ]
    test <@ documents = [] @>
    test <@ reviews = [ "mp;u-1;2026-07-18;3,00;Tanssit;unknown-type" ] @>

[<Fact>]
let ``A MobilePay expense no line books is posted all the same, and reviewed (M11)`` () =
    let expense (id: string) (time: string) = transaction TransactionKind.Expense id time "Alkufest" -13M
    let documents, reviews = planned [ line PaymentMethod.MobilePay "Alkufest" -13M ] [ expense "e-1" "16:00"; expense "e-2" "17:00" ]
    test <@ documents = [ "2026-07-18;1930;4000;-26,00;2;0;0;[kassakirja] MobilePay/Alkufest kulut 2026-07-18 (2)" ] @>
    test <@ reviews = [ "mp;e-2;2026-07-18;-13,00;Alkufest;mp-expense-unbooked" ] @>

[<Fact>]
let ``An item a flag rule classifies is posted, counted flagged and reviewed (K1)`` () =
    let documents, reviews = planned [ for text in [ "Kahvila"; "Kahvila"; "Kahvila"; "" ] -> line PaymentMethod.Cash text 2M ] []
    test <@ documents = [ "2026-07-18;1900;3000;8,00;4;0;1;[kassakirja] Kassa/Kahvila 2026-07-18 (4)" ] @>
    test <@ reviews = [ "book;L5;2026-07-18;2,00;;flagged" ] @>

[<Fact>]
let ``A run holds at most max_days days with postings and is dated its last day (C2)`` () =
    let documents, _ = planned [ for offset in 0 .. 3 -> { line PaymentMethod.Cash "Tanssit" 10M with date = day.AddDays offset } ] []
    test <@ documents = [ "2026-07-20;1900;3010;30,00;3;0;0;[kassakirja] Kassa/Tanssit 2026-07-18–2026-07-20 (3)"
                          "2026-07-21;1900;3010;10,00;1;0;0;[kassakirja] Kassa/Tanssit 2026-07-21 (1)" ] @>

[<Fact>]
let ``A day joins a run only when its gap to the run's last day is at most max_gap (C2)`` () =
    let documents, _ = planned [ for offset in [ 0; 1; 3 ] -> { line PaymentMethod.Cash "Tanssit" 10M with date = day.AddDays offset } ] []
    test <@ documents = [ "2026-07-19;1900;3010;20,00;2;0;0;[kassakirja] Kassa/Tanssit 2026-07-18–2026-07-19 (2)"
                          "2026-07-21;1900;3010;10,00;1;0;0;[kassakirja] Kassa/Tanssit 2026-07-21 (1)" ] @>

[<Fact>]
let ``MobilePay postings coalesce under the mobilepay block (C3)`` () =
    let documents, _ =
        planned [] [ for offset in 0 .. 4 -> { payment $"{offset}" "16:00" "Tanssit🌱" 10M with date = day.AddDays offset } ]
    test <@ documents = [ "2026-07-21;1930;3010;40,00;4;0;0;[kassakirja] MobilePay/Tanssit 2026-07-18–2026-07-21 (4)"
                          "2026-07-22;1930;3010;10,00;1;0;0;[kassakirja] MobilePay/Tanssit 2026-07-22 (1)" ] @>

[<Fact>]
let ``A till expense is one document per line, described by its mita and receipts (C4, C5)`` () =
    let expense (receipts: int) = { line PaymentMethod.Cash "Alkufest" -5M with receipts = receipts }
    let documents, _ = planned [ expense 1; expense 2 ] []
    test <@ documents = [ "2026-07-18;1900;4000;-5,00;1;1;0;[kassakirja] Kassa/Alkufest kulut 2026-07-18 Alkufest (1 kuittia)"
                          "2026-07-18;1900;4000;-5,00;1;2;0;[kassakirja] Kassa/Alkufest kulut 2026-07-18 Alkufest (2 kuittia)" ] @>

[<Fact>]
let ``Documents are sorted by date, payment account and offset account, an empty offset first (P1)`` () =
    let documents, _ =
        planned [ { line PaymentMethod.Cash "Tanssit" 10M with date = day.AddDays 1 }; line PaymentMethod.Cash "Kahvila" 2M
                  line PaymentMethod.Cash "Zzz" 1M ]
                [ payment "1" "16:00" "Kahvi" 3M ]
    test <@ documents = [ "2026-07-18;1900;;1,00;1;0;0;[kassakirja] Kassa/? 2026-07-18 (1)"
                          "2026-07-18;1900;3000;2,00;1;0;0;[kassakirja] Kassa/Kahvila 2026-07-18 (1)"
                          "2026-07-18;1930;3000;3,00;1;0;0;[kassakirja] MobilePay/Kahvila 2026-07-18 (1)"
                          "2026-07-19;1900;3010;10,00;1;0;0;[kassakirja] Kassa/Tanssit 2026-07-19 (1)" ] @>

[<Fact>]
let ``Review rows are the book lines in file order, then the transactions by time, matching's reasons before the plan's (R2)`` () =
    let unknown (id: string) (time: string) = transaction TransactionKind.Unknown id time "" 1M
    let _, reviews = planned [ line PaymentMethod.Unmarked "Zzz" 5M ] [ unknown "u-2" "17:00"; unknown "u-1" "16:00" ]
    test <@ reviews = [ "book;L2;2026-07-18;5,00;Zzz;assumed-cash"; "book;L2;2026-07-18;5,00;Zzz;unclassified"
                        "mp;u-1;2026-07-18;1,00;;unknown-type"; "mp;u-2;2026-07-18;1,00;;unknown-type" ] @>

// ── match.csv read back ───────────────────────────────────────────────────────

let private header = "book_page;book_date;book_text;book_amount;book_tapa;tier;mp_ids;mp_sum;note\n"

[<Fact>]
let ``A match.csv's tiers and transaction ids are taken as given and the notes recomputed`` () =
    let lines = book [ line PaymentMethod.MobilePay "Tanssit" 10M; line PaymentMethod.Cash "Kahvila" 3M ]
    let transactions = [ payment "1" "16:00" "" 10M; payment "2" "16:10" "" 10M; payment "3" "16:20" "" 3M ]
    let text = header + "1;2026-07-18;Tanssit;10,00;;M1;2;10,00;\n1;2026-07-18;Kahvila;3,00;;cash;;;\n"
    let expected =
        { lines = [ { line = lines.[0]; tier = Tier.M1; transactions = [ transactions.[1] ]; note = None }
                    { line = lines.[1]; tier = Tier.Cash; transactions = []; note = Some (Note.PossibleMp [ transactions.[2] ]) } ]
          unmatched = [ transactions.[0]; transactions.[2] ] }
    test <@ MatchReport.read lines transactions text = Ok expected @>

[<Fact>]
let ``A match.csv with the wrong number of rows, a row of another line, an unknown tier or an unknown id is rejected`` () =
    let lines = book [ line PaymentMethod.MobilePay "Tanssit" 10M ]
    let read (rows: string) = MatchReport.read lines [ payment "1" "16:00" "" 10M ] (header + rows)
    test <@ read "" = Error [ "[L1] expected as many rows as book lines (1), found 0" ] @>
    test <@ read "1;2026-07-18;Tanssi;10,00;;M1;1;10,00;\n"
              = Error [ "[L2] the row must describe book line L2 '1;2026-07-18;Tanssit;10,00', not '1;2026-07-18;Tanssi;10,00'" ] @>
    test <@ read "1;2026-07-18;Tanssit;10,00;;M9;7;10,00;\n"
              = Error [ "[L2] 'tier' must be M1–M5, cash, book-only or ambiguous, not 'M9'"; "[L2] unknown transaction id '7'" ] @>
