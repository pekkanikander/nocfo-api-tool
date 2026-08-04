module BalanceTests

open Xunit
open Swensen.Unquote
open NocfoApi.Types
open Nocfo.Domain

let private entry (number: string) (date: string) (debet: float) (credit: float) (balance: float) =
    { number = number
      date = date
      debet = debet
      credit = credit
      balance_csum = balance
      description = Some ("Entry " + number) }

let private account (number: string) (openingBalance: float) (entries: LedgerReportEntrySchema list) =
    { number = number
      name = "Account " + number
      opening_balance = openingBalance
      entries = entries
      totals = None }

let private ledger (accounts: LedgerReportAccountSchema list) =
    { report_type = "ledger"
      date_from = Some "2025-01-01"
      date_to = Some "2025-01-31"
      accounts = accounts
      totals = None }

let private bankAccount =
    account "1920" 1000.0
        [ entry "250102-1" "2025-01-02T00:00:00" 0.0 100.0  900.0
          entry "250110-1" "2025-01-10T00:00:00" 250.0 0.0 1150.0
          entry "250110-2" "2025-01-10T00:00:00" 0.0 50.0  1100.0 ]

[<Fact>]
let ``Each account opens with the balance carried into the period`` () =
    let rows = Balance.rows Set.empty (ledger [ bankAccount ])
    test <@ rows.Head = { account_number = "1920"
                          account_name = "Account 1920"
                          date = "2025-01-01"
                          document_number = ""
                          description = Some "Opening balance"
                          debet = 0M
                          credit = 0M
                          amount = 0M
                          balance = 1000M } @>

[<Fact>]
let ``Entry date-times are narrowed to days`` () =
    let rows = Balance.rows Set.empty (ledger [ bankAccount ])
    test <@ rows |> List.map (fun row -> row.date)
              = [ "2025-01-01"; "2025-01-02"; "2025-01-10"; "2025-01-10" ] @>

[<Fact>]
let ``An entry amount is its debit less its credit`` () =
    let rows = Balance.rows Set.empty (ledger [ bankAccount ])
    test <@ rows |> List.map (fun row -> row.amount) = [ 0M; -100M; 250M; -50M ] @>

[<Fact>]
let ``The running balance is the report's own cumulative sum`` () =
    let rows = Balance.rows Set.empty (ledger [ bankAccount ])
    test <@ rows |> List.map (fun row -> row.balance) = [ 1000M; 900M; 1150M; 1100M ] @>

[<Fact>]
let ``Floating-point amounts are rounded to cents`` () =
    let rows = Balance.rows Set.empty (ledger [ account "1920" 0.1 [ entry "1" "2025-01-02" 0.2 0.0 0.30000000000000004 ] ])
    test <@ rows |> List.map (fun row -> row.balance) = [ 0.10M; 0.30M ] @>

[<Fact>]
let ``An empty selection covers every account`` () =
    let rows = Balance.rows Set.empty (ledger [ bankAccount; account "1910" 5.0 [] ])
    test <@ rows |> List.map (fun row -> row.account_number) |> List.distinct = [ "1920"; "1910" ] @>

[<Fact>]
let ``A selection narrows the report to the accounts named`` () =
    let rows = Balance.rows (Set.ofList [ "1910" ]) (ledger [ bankAccount; account "1910" 5.0 [] ])
    test <@ rows |> List.map (fun row -> row.account_number) = [ "1910" ] @>

[<Fact>]
let ``A daily closing balance is the last balance of the day`` () =
    let result = Balance.rows Set.empty (ledger [ bankAccount ]) |> Balance.dailyClosing
    test <@ result = [ { date = "2025-01-01"; balance = 1000M }
                       { date = "2025-01-02"; balance = 900M }
                       { date = "2025-01-10"; balance = 1100M } ] @>
