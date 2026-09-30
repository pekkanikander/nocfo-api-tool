module RulesTests

open System
open System.IO
open Xunit
open Swensen.Unquote
open Nocfo.Cashbook

let private fixture = File.ReadAllText(Path.Combine(__SOURCE_DIRECTORY__, "fixtures", "cashbook", "rules.toml"))

let private parsed (text: string) =
    match Rules.parse text with
    | Ok rules -> rules
    | Error errors -> failwithf "%A" errors

let private errors (text: string) =
    match Rules.parse text with
    | Error errors -> errors
    | Ok _ -> failwith "Expected errors"

let private rules = parsed fixture

// ── Parsing ───────────────────────────────────────────────────────────────────

[<Fact>]
let ``Role accounts are read as number and name`` () =
    test <@ rules.accounts.mobilepay = { number = "1930"; name = "MobilePay" } @>

[<Fact>]
let ``Amounts and weights are decimal whether the file writes them as integers or floats`` () =
    test <@ rules.matching.tolerance = 0.50M @>
    test <@ rules.matching.weight_unmatched = 100M @>
    test <@ rules.matching.weight_subset_size = 0.1M @>
    test <@ rules.classification.cafe.max_amount = 10.00M @>

[<Fact>]
let ``Coalescing limits are read per payment account`` () =
    test <@ rules.coalesce = { cash = { max_days = 3; max_gap = 1 }
                               mobilepay = { max_days = 4; max_gap = 1 }
                               fees = { max_days = 7; max_gap = 7 } } @>

[<Fact>]
let ``Rules keep file order and their keywords`` () =
    test <@ rules.classification.income |> List.map (fun rule -> rule.account)
              = [ "3000"; "3060"; "3010"; "3020"; "3030"; "3040"; "3070"; "3050"; "3080"; "3090" ] @>
    test <@ rules.classification.expense.Head = { account = "4000"; name = "Alkufest kulut"; keywords = [ "alkufest" ]
                                                  from = None; ``to`` = None } @>

[<Fact>]
let ``A windowed rule needs no keywords and its bounds are dates`` () =
    let rules = parsed (fixture + "\n[[classification.income]]\naccount = \"3032\"\nname = \"Alkufest\"\nfrom = 2026-09-11\nto = 2026-09-13\n")
    test <@ List.last rules.classification.income
              = { account = "3032"; name = "Alkufest"; keywords = []
                  from = Some (DateOnly(2026, 9, 11)); ``to`` = Some (DateOnly(2026, 9, 13)) } @>

[<Fact>]
let ``A rule with neither keywords nor a window is rejected`` () =
    let result = errors "[[classification.expense]]\naccount = \"4000\"\nname = \"Kulut\"\nkeywords = []\n"
    test <@ result |> List.contains "[classification.expense #1] a rule needs keywords or a date window (from/to)" @>

[<Fact>]
let ``A window whose from is after its to is rejected`` () =
    let result = errors "[[classification.income]]\naccount = \"3032\"\nname = \"Alkufest\"\nfrom = 2026-09-13\nto = 2026-09-11\n"
    test <@ result |> List.contains "[classification.income #1] 'from' is after 'to'" @>

[<Fact>]
let ``A quoted date is not a date`` () =
    let result = errors "[[classification.income]]\naccount = \"3032\"\nname = \"Alkufest\"\nfrom = \"2026-09-11\"\n"
    test <@ result |> List.contains "[classification.income #1] 'from' must be a date (YYYY-MM-DD, unquoted)" @>

[<Fact>]
let ``An unknown key is named`` () =
    let result = errors "[matching]\nwindow_days = 3\n"
    test <@ result |> List.contains "[matching] unknown key 'window_days'" @>

[<Fact>]
let ``An unknown table is named`` () =
    let result = errors "[classifcation]\nmajority_min = 3\n"
    test <@ result |> List.contains "unknown table 'classifcation'" @>

[<Fact>]
let ``A mistyped key is both unknown and missing`` () =
    let result = errors (fixture.Replace("marker =", "markr ="))
    test <@ result = [ "[posting] missing key 'marker'"; "[posting] unknown key 'markr'" ] @>

[<Fact>]
let ``A value of the wrong type is rejected`` () =
    let result = errors "[matching]\ndays_before = \"1\"\n"
    test <@ result |> List.contains "[matching] 'days_before' must be an integer" @>

[<Fact>]
let ``A syntax error is reported with its position`` () =
    let result = errors "[matching\n"
    test <@ result.Head.StartsWith "(1,10) : error : " @>
