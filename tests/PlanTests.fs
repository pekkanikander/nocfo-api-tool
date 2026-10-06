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
[<InlineData("ambiguous-tie")>]
let ``A fixture's plan.csv is reproduced exactly`` (case: string) =
    let read, lines, transactions = fixture case
    let rendered = PlanReport.render (Plan.build rules (Matching.run rules lines transactions))
    test <@ rendered = read "plan.csv" @>

[<Theory>]
[<InlineData("exact-multi")>]
[<InlineData("unmarked-tulo")>]
[<InlineData("cross-day-near-miss")>]
[<InlineData("ambiguous-tie")>]
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
    { id = id; date = day; time = TimeOnly.Parse time; name = ""; text = text; amount = amount; kind = kind }

let private payment = transaction TransactionKind.PaymentIn

let private rows (text: string) = text.Split('\n', StringSplitOptions.RemoveEmptyEntries) |> List.ofArray |> List.tail

/// The rows of `plan.csv`, header dropped.
let private planned (lines: CashBookLine list) (transactions: Transaction list) =
    rows (PlanReport.render (Plan.build rules (Matching.run rules (book lines) transactions)))

[<Fact>]
let ``A cash-marked line and an unmarked line matched to nothing are posted as cash, the unmarked one tagged; a MobilePay-marked one is not posted (M6, M9, M10)`` () =
    let documents =
        planned [ line PaymentMethod.Cash "Tanssit" 10M; line PaymentMethod.Unmarked "Tanssit" 5M
                  line PaymentMethod.Unmarked "Alkufest" -2M; line PaymentMethod.MobilePay "Tanssit" 1M ] []
    test <@ documents = [ "2026-07-18;1900;3010;10,00;1;0;;[kassakirja] Kassa/Tanssit 2026-07-18 (1)"
                          "2026-07-18;1900;3010;5,00;1;0;assumed-cash;[kassakirja] Kassa/Tanssit 2026-07-18 (1)"
                          "2026-07-18;1900;4000;-2,00;1;0;unmarked-expense;[kassakirja] Kassa/Alkufest kulut 2026-07-18 Alkufest (0 kuittia)" ] @>

[<Fact>]
let ``A matched line is not posted; its transaction is, from the MobilePay account, its keywords sought in its Viesti and the line's mita (K3)`` () =
    let documents =
        planned [ line PaymentMethod.MobilePay "Tanssit" 10M; line PaymentMethod.Unmarked "" 7M ]
                [ payment "1" "16:00" "" 10M; payment "2" "16:10" "Tanssit🌱" 7M ]
    test <@ documents = [ "2026-07-18;1930;3010;17,00;2;0;;[kassakirja] MobilePay/Tanssit 2026-07-18 (2)" ] @>

[<Fact>]
let ``An unclassified item is one document of its own on the clearing account, tagged, with ? for the offset name (K7)`` () =
    let documents =
        planned [ line PaymentMethod.Cash "Zzz" 5M; line PaymentMethod.Cash "Zzz" 5M ] [ payment "1" "16:00" "Yyy" 2M ]
    test <@ documents = [ "2026-07-18;1900;1990;5,00;1;0;unclassified;[kassakirja] Kassa/? 2026-07-18 (1)"
                          "2026-07-18;1900;1990;5,00;1;0;unclassified;[kassakirja] Kassa/? 2026-07-18 (1)"
                          "2026-07-18;1930;1990;2,00;1;0;unclassified;[kassakirja] MobilePay/? 2026-07-18 (1)" ] @>

[<Fact>]
let ``A fee is posted from MobilePay to the fee account whatever its text, under the fees block (K8, C3)`` () =
    let fee (id: string) (text: string) = transaction TransactionKind.Fee id "23:59" text -0.50M
    let documents = planned [] [ fee "f-1" "Kahvila"; { fee "f-2" "" with date = day.AddDays 7 } ]
    test <@ documents = [ "2026-07-25;1930;8100;-1,00;2;0;;[kassakirja] MobilePay/Palvelumaksut 2026-07-18–2026-07-25 (2)" ] @>

[<Fact>]
let ``A payout is not posted; a transaction of unknown type is, on the clearing account, tagged unknown-type (I6)`` () =
    let documents =
        planned [] [ transaction TransactionKind.Payout "p-1" "06:00" "" -100M
                     transaction TransactionKind.Unknown "u-1" "16:00" "Tanssit" 3M ]
    test <@ documents = [ "2026-07-18;1930;1990;3,00;1;0;unknown-type;[kassakirja] MobilePay/? 2026-07-18 (1)" ] @>

[<Fact>]
let ``A MobilePay expense no line books is posted all the same, tagged, apart from the one the line books (M11, C1)`` () =
    let expense (id: string) (time: string) = transaction TransactionKind.Expense id time "Alkufest" -13M
    let documents = planned [ line PaymentMethod.MobilePay "Alkufest" -13M ] [ expense "e-1" "16:00"; expense "e-2" "17:00" ]
    test <@ documents = [ "2026-07-18;1930;4000;-13,00;1;0;;[kassakirja] MobilePay/Alkufest kulut 2026-07-18 (1)"
                          "2026-07-18;1930;4000;-13,00;1;0;mp-expense-unbooked;[kassakirja] MobilePay/Alkufest kulut 2026-07-18 (1)" ] @>

[<Fact>]
let ``An item a flag rule classifies is posted on a document of its own, tagged flagged (K1, C1)`` () =
    let documents = planned [ for text in [ "Kahvila"; "Kahvila"; "Kahvila"; "" ] -> line PaymentMethod.Cash text 2M ] []
    test <@ documents = [ "2026-07-18;1900;3000;6,00;3;0;;[kassakirja] Kassa/Kahvila 2026-07-18 (3)"
                          "2026-07-18;1900;3000;2,00;1;0;flagged;[kassakirja] Kassa/Kahvila 2026-07-18 (1)" ] @>

[<Fact>]
let ``The transactions an ambiguous line contests post on their own, without its hint, tagged ambiguous (M8, M9)`` () =
    let documents =
        planned [ line PaymentMethod.MobilePay "Tanssit" 10M ]
                [ payment "1" "16:00" "" 4M; payment "2" "16:10" "" 6M; payment "3" "16:20" "" 3M; payment "4" "16:30" "" 7M ]
    let expected =
        [ for amount in [ "4,00"; "6,00"; "3,00"; "7,00" ] ->
              $"2026-07-18;1930;1990;{amount};1;0;ambiguous+unclassified;[kassakirja] MobilePay/? 2026-07-18 (1)" ]
    test <@ documents = expected @>

[<Fact>]
let ``A run holds at most max_days days with postings and is dated its last day (C2)`` () =
    let documents = planned [ for offset in 0 .. 3 -> { line PaymentMethod.Cash "Tanssit" 10M with date = day.AddDays offset } ] []
    test <@ documents = [ "2026-07-20;1900;3010;30,00;3;0;;[kassakirja] Kassa/Tanssit 2026-07-18–2026-07-20 (3)"
                          "2026-07-21;1900;3010;10,00;1;0;;[kassakirja] Kassa/Tanssit 2026-07-21 (1)" ] @>

[<Fact>]
let ``A day joins a run only when its gap to the run's last day is at most max_gap (C2)`` () =
    let documents = planned [ for offset in [ 0; 1; 3 ] -> { line PaymentMethod.Cash "Tanssit" 10M with date = day.AddDays offset } ] []
    test <@ documents = [ "2026-07-19;1900;3010;20,00;2;0;;[kassakirja] Kassa/Tanssit 2026-07-18–2026-07-19 (2)"
                          "2026-07-21;1900;3010;10,00;1;0;;[kassakirja] Kassa/Tanssit 2026-07-21 (1)" ] @>

[<Fact>]
let ``MobilePay postings coalesce under the mobilepay block (C3)`` () =
    let documents =
        planned [] [ for offset in 0 .. 4 -> { payment $"{offset}" "16:00" "Tanssit🌱" 10M with date = day.AddDays offset } ]
    test <@ documents = [ "2026-07-21;1930;3010;40,00;4;0;;[kassakirja] MobilePay/Tanssit 2026-07-18–2026-07-21 (4)"
                          "2026-07-22;1930;3010;10,00;1;0;;[kassakirja] MobilePay/Tanssit 2026-07-22 (1)" ] @>

[<Fact>]
let ``A till expense is one document per line, described by its mita and receipts (C4, C5)`` () =
    let expense (receipts: int) = { line PaymentMethod.Cash "Alkufest" -5M with receipts = receipts }
    let documents = planned [ expense 1; expense 2 ] []
    test <@ documents = [ "2026-07-18;1900;4000;-5,00;1;1;;[kassakirja] Kassa/Alkufest kulut 2026-07-18 Alkufest (1 kuittia)"
                          "2026-07-18;1900;4000;-5,00;1;2;;[kassakirja] Kassa/Alkufest kulut 2026-07-18 Alkufest (2 kuittia)" ] @>

[<Fact>]
let ``Documents are sorted by date, payment account and offset account (P1)`` () =
    let documents =
        planned [ { line PaymentMethod.Cash "Tanssit" 10M with date = day.AddDays 1 }; line PaymentMethod.Cash "Kahvila" 2M
                  line PaymentMethod.Cash "Zzz" 1M ]
                [ payment "1" "16:00" "Kahvi" 3M ]
    test <@ documents = [ "2026-07-18;1900;1990;1,00;1;0;unclassified;[kassakirja] Kassa/? 2026-07-18 (1)"
                          "2026-07-18;1900;3000;2,00;1;0;;[kassakirja] Kassa/Kahvila 2026-07-18 (1)"
                          "2026-07-18;1930;3000;3,00;1;0;;[kassakirja] MobilePay/Kahvila 2026-07-18 (1)"
                          "2026-07-19;1900;3010;10,00;1;0;;[kassakirja] Kassa/Tanssit 2026-07-19 (1)" ] @>

// ── Summary ───────────────────────────────────────────────────────────────────

[<Fact>]
let ``The summary counts the documents per tag and names the book-only and possible-mp lines (R4)`` () =
    let lines =
        book [ line PaymentMethod.Cash "Tanssit" 10M; line PaymentMethod.MobilePay "Tanssit" 1M; line PaymentMethod.Unmarked "Zzz" 5M ]
    let matches = Matching.run rules lines [ payment "1" "16:00" "" 10M ]
    test <@ Summary.ofDocuments (Plan.build rules matches) = [ "documents tagged assumed-cash: 1"; "documents tagged unclassified: 2" ] @>
    test <@ Summary.ofMatches matches = [ "possible-mp: page 1, 2026-07-18, 10,00 (MobilePay 1)"; "book-only: page 1, 2026-07-18, 1,00" ] @>

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
