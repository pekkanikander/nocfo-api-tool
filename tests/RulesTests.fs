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
let ``Payment and fee accounts are read as number and name`` () =
    test <@ rules.accounts.mobilepay = { number = "1930"; name = "MobilePay" } @>

[<Fact>]
let ``Amounts and weights are decimal whether the file writes them as integers or floats`` () =
    test <@ rules.matching.tolerance = 0.50M @>
    test <@ rules.matching.weight_unmatched = 100M @>
    test <@ rules.matching.weight_subset_size = 0.1M @>

[<Fact>]
let ``Coalescing limits are read per payment account`` () =
    test <@ rules.coalesce = { cash = { max_days = 3; max_gap = 1 }
                               mobilepay = { max_days = 4; max_gap = 1 }
                               fees = { max_days = 7; max_gap = 7 } } @>

[<Fact>]
let ``Rules keep file order and their keywords`` () =
    test <@ rules.classification.income |> List.map (fun rule -> rule.account)
              = [ "3000"; "3060"; "3010"; "3020"; "3030"; "3040"; "3070"; "3050"; "3080"; "3090"; "3000" ] @>
    test <@ rules.classification.expense.Head = { account = "4000"; name = "Alkufest kulut"; keywords = [ "alkufest" ]
                                                  no_text = false; from_date = None; to_date = None
                                                  max_amount = None; min_per_day = None; flag = false } @>

[<Fact>]
let ``The cafe rule carries its predicates and the flag`` () =
    test <@ List.last rules.classification.income
              = { account = "3000"; name = "Kahvila"; keywords = []; no_text = true; from_date = None; to_date = None
                  max_amount = Some 10.00M; min_per_day = Some 3; flag = true } @>

[<Fact>]
let ``A windowed rule needs no keywords and its bounds are dates`` () =
    let rules = parsed (fixture + "\n[[classification.income]]\naccount = \"3032\"\nname = \"Alkufest\"\nfrom = 2026-09-11\nto = 2026-09-13\n")
    test <@ List.last rules.classification.income
              = { account = "3032"; name = "Alkufest"; keywords = []; no_text = false
                  from_date = Some (DateOnly(2026, 9, 11)); to_date = Some (DateOnly(2026, 9, 13))
                  max_amount = None; min_per_day = None; flag = false } @>

[<Fact>]
let ``A rule without a predicate is rejected, and a flag is not one`` () =
    let result = errors "[[classification.expense]]\naccount = \"4000\"\nname = \"Kulut\"\nkeywords = []\nflag = true\n"
    test <@ result |> List.contains "[classification.expense #1] a rule needs a predicate: keywords, no_text, from/to, max_amount or min_per_day" @>

[<Fact>]
let ``A rule cannot require blank text and a keyword at once`` () =
    let result = errors "[[classification.income]]\naccount = \"3000\"\nname = \"Kahvila\"\nkeywords = [\"kahvi\"]\nno_text = true\n"
    test <@ result |> List.contains "[classification.income #1] 'no_text' excludes 'keywords'" @>

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
    let result = errors "[[classification.income]]\naccount = \"3000\"\nname = \"Kahvila\"\nno_text = \"yes\"\n"
    test <@ result |> List.contains "[classification.income #1] 'no_text' must be a boolean" @>

[<Fact>]
let ``A syntax error is reported with its position`` () =
    let result = errors "[matching\n"
    test <@ result.Head.StartsWith "(1,10) : error : " @>

// ── Checking against the chart of accounts ────────────────────────────────────

let private account number name kind : Account = { number = number; name = name; kind = kind }

let private chart =
    [ account "1900" "Kassa"          AccountKind.Balance
      account "1930" "MobilePay"      AccountKind.Balance
      account "8100" "Palvelumaksut"  AccountKind.Expense
      account "3000" "Kahvila"        AccountKind.Income
      account "3010" "Tanssit"        AccountKind.Income
      account "3020" "Rumpupiiri"     AccountKind.Income
      account "3030" "Yöpymiset"      AccountKind.Income
      account "3040" "Lahjoitukset"   AccountKind.Income
      account "3050" "Tilavuokra"     AccountKind.Income
      account "3060" "Pääsymaksut"    AccountKind.Income
      account "3070" "Jäsenmaksut"    AccountKind.Income
      account "3080" "Tapahtumat"     AccountKind.Income
      account "3090" "Alkufest tuotot" AccountKind.Income
      account "4000" "Alkufest kulut" AccountKind.Expense
      account "4100" "Kahvilan ostot" AccountKind.Expense ]

[<Fact>]
let ``A file whose accounts all exist under their names is clean`` () =
    test <@ Rules.check chart rules = [] @>

[<Fact>]
let ``An account the business lacks is an error, wherever the file names it`` () =
    let findings = Rules.check (chart |> List.filter (fun account -> account.number <> "3000")) rules
    test <@ findings = [ MissingAccount ("classification.income #1", "3000"); MissingAccount ("classification.income #11", "3000") ] @>
    test <@ findings |> List.forall Finding.isError @>

[<Fact>]
let ``A name that differs from NoCFO's is a warning`` () =
    let renamed = chart |> List.map (fun account -> if account.number = "1900" then { account with name = "Käteinen" } else account)
    let findings = Rules.check renamed rules
    test <@ findings = [ NameDiffers ("accounts.cash", "1900", "Kassa", "Käteinen") ] @>
    test <@ not (findings |> List.exists Finding.isError) @>
