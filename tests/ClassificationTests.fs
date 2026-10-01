module ClassificationTests

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

let private day = DateOnly(2026, 7, 18)

let private rule (account: string) (keywords: string list) : Rule =
    { account = account; name = account; keywords = keywords; no_text = false; from_date = None; to_date = None
      max_amount = None; min_per_day = None; flag = false }

let private line (text: string) (amount: decimal) : Item =
    let line : CashBookLine =
        { file_line = 0; page = "1"; date = day; text = text; amount = amount; method = PaymentMethod.Cash
          tapa = "KÄT"; receipts = 0; remark = "" }
    { origin = Origin.Line line; date = day; amount = amount; text = text }

let private payment (id: string) (text: string) (amount: decimal) : Item =
    let transaction : Transaction =
        { id = id; date = day; time = TimeOnly(16, 0); text = text; amount = amount; kind = TransactionKind.PaymentIn }
    { origin = Origin.Transaction transaction; date = day; amount = amount; text = text }

let private onDay (offset: int) (item: Item) = { item with date = day.AddDays offset }

let private classified (income: Rule list) (expense: Rule list) (items: Item list) =
    Classification.classify { rules with classification = { rules.classification with income = income; expense = expense } } items

let private accounts (classified: Classified list) =
    classified |> List.map (fun classified -> classified.offset |> Option.map (fun offset -> offset.number))

/// The account numbers the items land on under the income and expense rules given.
let private classify (income: Rule list) (expense: Rule list) (items: Item list) = accounts (classified income expense items)

// ── Rules (K1) ────────────────────────────────────────────────────────────────

[<Fact>]
let ``A keyword is a case-insensitive substring of the text`` () =
    test <@ classify [ rule "3010" [ "tanssi" ] ] [] [ payment "1" "Tanssit🌱" 10M; line "TANSSIT" 5M; line "Tanz" 5M ]
              = [ Some "3010"; Some "3010"; None ] @>

[<Fact>]
let ``no_text hits a blank text only`` () =
    let blank = { rule "3000" [] with no_text = true }
    test <@ classify [ blank ] [] [ line "" 1M; line "  " 1M; line "x" 1M ] = [ Some "3000"; Some "3000"; None ] @>

[<Fact>]
let ``from_date and to_date are inclusive, one left out being open`` () =
    let within = { rule "3010" [] with from_date = Some day; to_date = Some day }
    let since = { rule "3010" [] with from_date = Some day }
    test <@ classify [ within ] [] [ line "x" 1M; onDay 1 (line "x" 1M); onDay -1 (line "x" 1M) ] = [ Some "3010"; None; None ] @>
    test <@ classify [ since ] [] [ onDay 400 (line "x" 1M) ] = [ Some "3010" ] @>

[<Fact>]
let ``max_amount bounds the unsigned amount`` () =
    let small = { rule "4100" [] with max_amount = Some 10M }
    test <@ classify [] [ small ] [ line "x" -10M; line "x" -10.01M ] = [ Some "4100"; None ] @>

[<Fact>]
let ``min_per_day counts the same-day lines and transactions the preceding rules put on the account`` () =
    let rest = { rule "3000" [] with no_text = true; min_per_day = Some 3 }
    let items = [ line "Kahvila" 2M; payment "1" "kahvi" 2M; payment "2" "KAHVI" 2M; line "" 2M; onDay 1 (line "" 2M) ]
    test <@ classify [ rule "3000" [ "kahvi" ]; rest ] [] items = [ Some "3000"; Some "3000"; Some "3000"; Some "3000"; None ] @>
    test <@ classify [ rule "3000" [ "kahvi" ]; rest ] [] (List.tail items) = [ Some "3000"; Some "3000"; None; None ] @>

[<Fact>]
let ``A flag rule classifies the item and marks it flagged`` () =
    let flagged = { rule "3000" [] with flag = true }
    let result = classified [ rule "3010" [ "tanssi" ]; flagged ] [] [ line "Tanssit" 1M; line "x" 1M ]
    test <@ accounts result = [ Some "3010"; Some "3000" ] @>
    test <@ result |> List.map (fun classified -> classified.flagged) = [ false; true ] @>

[<Fact>]
let ``Rules apply in file order, the first that hits taking the item`` () =
    let vuokra, neuvosto = rule "3050" [ "vuokra" ], rule "3080" [ "neuvosto" ]
    test <@ classify [ vuokra; neuvosto ] [] [ line "Neuvoston vuokra" 1M ] = [ Some "3050" ] @>
    test <@ classify [ neuvosto; vuokra ] [] [ line "Neuvoston vuokra" 1M ] = [ Some "3080" ] @>

[<Fact>]
let ``An income item takes the income rules and an expense item the expense rules`` () =
    test <@ classify [ rule "3000" [ "x" ] ] [ rule "4000" [ "x" ] ] [ line "x" 5M; line "x" -5M ]
              = [ Some "3000"; Some "4000" ] @>

// ── Neighbours (K4, K5) ───────────────────────────────────────────────────────

let private tanssit, kahvila = rule "3010" [ "tanssi" ], rule "3000" [ "kahvi" ]

[<Fact>]
let ``A transaction no rule hits takes the account a majority of the day's other transactions are on (K4)`` () =
    let others = [ for id in [ "1"; "2"; "3" ] -> payment id "Tanssit" 10M ]
    test <@ classify [ tanssit ] [] (payment "4" "" 7M :: others) = [ Some "3010"; Some "3010"; Some "3010"; Some "3010" ] @>
    test <@ classify [ tanssit ] [] (payment "4" "" 7M :: List.tail others) |> List.head = None @>
    test <@ classify [ tanssit ] [] (onDay 1 (payment "4" "" 7M) :: others) |> List.head = None @>
    test <@ classify [ tanssit ] [] (line "" 7M :: others) |> List.head = None @>
    test <@ classify [ tanssit ] [] (payment "4" "" 7M :: [ for _ in 1 .. 3 -> line "Tanssit" 10M ]) |> List.head = None @>

[<Fact>]
let ``The day's majority must be a strict one (K4)`` () =
    let split = [ for id in [ "1"; "2"; "3" ] -> payment id "Tanssit" 10M ] @ [ for id in [ "5"; "6"; "7" ] -> payment id "Kahvi" 10M ]
    test <@ classify [ tanssit; kahvila ] [] (payment "4" "" 7M :: split) |> List.head = None @>

[<Fact>]
let ``A transaction no rule hits takes the account a majority of the same-amount transactions of adjacent days are on (K5)`` () =
    let twins = [ onDay -1 (payment "1" "Tanssit" 10M); onDay 1 (payment "2" "Tanssit" 10M); onDay 1 (payment "3" "" 10M) ]
    test <@ classify [ tanssit ] [] (payment "4" "" 10M :: twins) |> List.head = Some "3010" @>
    test <@ classify [ tanssit ] [] (payment "4" "" 10.01M :: twins) |> List.head = None @>
    test <@ classify [ tanssit ] [] (payment "4" "" 10M :: List.tail twins) |> List.head = None @>
    test <@ classify [ tanssit ] [] (payment "4" "" 10M :: onDay 2 (payment "5" "Tanssit" 10M) :: List.tail twins) |> List.head = None @>

[<Fact>]
let ``The same-amount majority must be a strict one (K5)`` () =
    let twins = [ for id in [ "1"; "2" ] -> payment id "Tanssit" 10M ] @ [ for id in [ "5"; "6" ] -> onDay 1 (payment id "Kahvi" 10M) ]
    test <@ classify [ tanssit; kahvila ] [] (payment "4" "" 10M :: twins) |> List.head = None @>

[<Fact>]
let ``The day majority and the same-amount majority read the rules' result alone, not each other's (K4, K5)`` () =
    let rules =
        { rules with classification = { rules.classification with majority_min = 1; same_amount_min = 1
                                                                  income = [ tanssit; kahvila ]; expense = [] } }
    let items =
        [ payment "a" "Tanssit" 10M; payment "p" "Kahvi" 10M; payment "d" "" 7M; onDay 1 (payment "s" "Kahvi" 7M)
          payment "e" "" 9M ]
    test <@ accounts (Classification.classify rules items) = [ Some "3010"; Some "3000"; Some "3000"; Some "3000"; None ] @>

[<Fact>]
let ``An income transaction never takes an expense account from its neighbours (K4)`` () =
    let expenses = [ for id in [ "1"; "2"; "3" ] -> payment id "Alkufest" -5M ]
    test <@ classify [] [ rule "4000" [ "alkufest" ] ] (payment "4" "" 3M :: expenses) |> List.head = None @>
