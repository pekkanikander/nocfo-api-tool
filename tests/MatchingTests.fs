module MatchingTests

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

[<Theory>]
[<InlineData("exact-multi")>]
[<InlineData("unmarked-tulo")>]
[<InlineData("cross-day-near-miss")>]
let ``A fixture's match.csv is reproduced exactly`` (case: string) =
    let read name = File.ReadAllText(Path.Combine(fixtures, case, name))
    let lines = unwrap (CashBook.read (read "kassakirja.csv"))
    let transactions = unwrap (MobilePay.read rules.mobilepay (read "mobilepay.csv"))
    let rendered = MatchReport.render (Matching.run rules lines transactions)
    test <@ rendered = read "match.csv" @>

// ── Hand-built cases ──────────────────────────────────────────────────────────

let private day = DateOnly(2026, 7, 18)

let private line (method: PaymentMethod) (text: string) (amount: decimal) : CashBookLine =
    { file_line = 0; page = "1"; date = day; text = text; amount = amount; method = method
      tapa = ""; receipts = 0; remark = "" }

let private book (lines: CashBookLine list) = lines |> List.mapi (fun index line -> { line with file_line = index + 2 })

let private payment (id: string) (time: string) (text: string) (amount: decimal) : Transaction =
    { id = id; date = day; time = TimeOnly.Parse time; text = text; amount = amount; kind = TransactionKind.PaymentIn }

let private outcome (lines: CashBookLine list) (transactions: Transaction list) =
    let matches = Matching.run rules (book lines) transactions
    matches.lines |> List.map (fun lineMatch -> lineMatch.tier, Matching.ids lineMatch.transactions, lineMatch.note)

[<Fact>]
let ``Of two equal same-day payments the earliest by time of day is taken (M2)`` () =
    let later = payment "1" "19:00" "" 10M
    let earlier = payment "9" "16:00" "" 10M
    test <@ outcome [ line PaymentMethod.MobilePay "Tanssit" 10M ] [ later; earlier ] = [ Tier.M2, "9", "" ] @>

[<Fact>]
let ``Of two equal same-day payments the one whose Viesti shares a word with mita is taken (M2)`` () =
    let later = payment "1" "19:00" "Tanssit" 10M
    let earlier = payment "9" "16:00" "" 10M
    test <@ outcome [ line PaymentMethod.MobilePay "Tanssit" 10M ] [ later; earlier ] = [ Tier.M2, "1", "" ] @>

[<Fact>]
let ``Two same-day exact subsets of the same cost leave the line ambiguous and its candidates unmatched (M8)`` () =
    let transactions =
        [ payment "1" "16:00" "" 4M; payment "2" "16:10" "" 6M
          payment "3" "16:20" "" 3M; payment "4" "16:30" "" 7M ]
    let matches = Matching.run rules (book [ line PaymentMethod.MobilePay "Tanssit" 10M ]) transactions
    test <@ matches.lines |> List.map (fun lineMatch -> lineMatch.tier, lineMatch.transactions)
              = [ Tier.Ambiguous, [] ] @>
    test <@ matches.unmatched = transactions @>

[<Fact>]
let ``A MobilePay-marked line with no match is book-only, and the transactions stay unmatched (M6)`` () =
    let fee = { payment "f-1" "16:00" "" -2M with kind = TransactionKind.Fee }
    let other = payment "1" "16:00" "" 7M
    let matches = Matching.run rules (book [ line PaymentMethod.MobilePay "Tanssit" 10M ]) [ fee; other ]
    test <@ matches.lines |> List.map (fun lineMatch -> lineMatch.tier, lineMatch.note) = [ Tier.BookOnly, "" ] @>
    test <@ matches.unmatched = [ fee; other ] @>

[<Fact>]
let ``An unmarked income line with no match is assumed cash (M9)`` () =
    test <@ outcome [ line PaymentMethod.Unmarked "Tanssit" 10M ] [ payment "1" "16:00" "" 7M ]
              = [ Tier.Cash, "", "assumed-cash" ] @>

[<Fact>]
let ``A near miss matches a MobilePay-marked line but not an unmarked one (M5, M9)`` () =
    let near = payment "1" "16:00" "" 10.20M
    test <@ outcome [ line PaymentMethod.MobilePay "Tanssit" 10M ] [ near ]
              = [ Tier.M5, "1", "contradiction: MobilePay wins" ] @>
    test <@ outcome [ line PaymentMethod.Unmarked "Tanssit" 10M ] [ near ] = [ Tier.Cash, "", "assumed-cash" ] @>

[<Fact>]
let ``A MobilePay-marked expense line matches a MobilePay expense, both negative (M11)`` () =
    let expense = { payment "e-1" "16:00" "Maito" -13M with kind = TransactionKind.Expense }
    let income = payment "1" "16:00" "" 13M
    test <@ outcome [ line PaymentMethod.MobilePay "Lidl" -13M ] [ income; expense ] = [ Tier.M1, "e-1", "" ] @>

[<Fact>]
let ``A cash-marked line is never matched but an unmatched same-day equal transaction is noted (M7)`` () =
    let twin = payment "1" "16:00" "" 10M
    test <@ outcome [ line PaymentMethod.Cash "Tanssit" 10M ] [ twin ] = [ Tier.Cash, "", "possible-mp: 1" ] @>

[<Fact>]
let ``A transaction serves one line only, the cheaper assignment winning (M8)`` () =
    let only = payment "1" "16:00" "" 10M
    test <@ outcome [ line PaymentMethod.MobilePay "a" 10M; line PaymentMethod.MobilePay "b" 10M ] [ only ]
              = [ Tier.M1, "1", ""; Tier.BookOnly, "", "" ] @>

// ── Period ────────────────────────────────────────────────────────────────────

[<Fact>]
let ``A period holds the dates within its bounds, a bound left out being open`` () =
    let within = Period.contains { from_date = Some day; to_date = None }
    test <@ within day && within (day.AddDays 400) && not (within (day.AddDays -1)) @>
    test <@ Period.contains { from_date = None; to_date = Some day } (day.AddDays 1) = false @>
