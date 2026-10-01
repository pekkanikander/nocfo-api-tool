module SeedTests

open System.Text.RegularExpressions
open Xunit
open Swensen.Unquote
open Tomlyn
open Tomlyn.Model
open Nocfo.Cashbook

let private account number name kind : Account = { number = number; name = name; kind = kind }

let private chart =
    [ account "4100" "Kahvilan ostot" AccountKind.Expense
      account "3060" "Pääsymaksut"    AccountKind.Income
      account "1930" "MobilePay"      AccountKind.Balance
      account "3000" "Kahvila"        AccountKind.Income
      account "1900" "Kassa"          AccountKind.Balance
      account "4000" "Alkufest kulut" AccountKind.Expense ]

let private rendered = Seed.render chart

let private table (parent: TomlTable) (key: string) = parent.[key] :?> TomlTable

let private rules (classification: TomlTable) (key: string) =
    [ for rule in (classification.[key] :?> TomlTableArray) -> string rule.["account"], string rule.["name"] ]

let private uncommentStubs (toml: string) =
    Regex.Replace(toml, @"^# (?=\[\[|account |name |keywords )", "", RegexOptions.Multiline)

[<Fact>]
let ``Payment and fee accounts are present and blank`` () =
    let accounts = table (Toml.ToModel rendered) "accounts"
    let blanks =
        [ for key in [ "cash"; "mobilepay"; "fees" ] ->
            let account = table accounts key
            string account.["number"], string account.["name"] ]
    test <@ blanks = List.replicate 3 ("", "") @>

[<Fact>]
let ``Asset and liability accounts are listed as candidates in number order`` () =
    test <@ rendered.Contains "\n#   1900  Kassa\n#   1930  MobilePay\ncash " @>

[<Fact>]
let ``A stub is commented out with empty keywords`` () =
    test <@ rendered.Contains "# [[classification.income]]\n# account  = \"3060\"\n# name     = \"Pääsymaksut\"\n# keywords = []\n" @>

[<Fact>]
let ``The skeleton is TOML with no rules and a blank cafe account`` () =
    let classification = table (Toml.ToModel rendered) "classification"
    let cafeAccount = string (table classification "cafe").["account"]
    let hasRules = classification.ContainsKey "income" || classification.ContainsKey "expense"
    test <@ cafeAccount = "" && not hasRules @>

[<Fact>]
let ``Uncommenting every stub yields the rules split by kind in number order`` () =
    let classification = table (Toml.ToModel (uncommentStubs rendered)) "classification"
    let income = rules classification "income"
    let expense = rules classification "expense"
    test <@ income = [ "3000", "Kahvila"; "3060", "Pääsymaksut" ] @>
    test <@ expense = [ "4000", "Alkufest kulut"; "4100", "Kahvilan ostot" ] @>

[<Fact>]
let ``Quotes and backslashes in a name survive the TOML round trip`` () =
    let name = "Myynti \"kahvila\" \\ muu"
    let classification = table (Toml.ToModel (uncommentStubs (Seed.render [ account "3000" name AccountKind.Income ]))) "classification"
    let income = rules classification "income"
    test <@ income = [ "3000", name ] @>
