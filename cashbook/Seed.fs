namespace Nocfo.Cashbook

[<RequireQualifiedAccess>]
type AccountKind =
    | Income
    | Expense
    | Balance

type Account =
    { number: string
      name: string
      kind: AccountKind }

module Seed =

    let private quote (text: string) =
        "\"" + text.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\""

    let private stub (table: string) (account: Account) =
        String.concat "\n"
            [ $"# [[classification.{table}]]"
              $"# account  = {quote account.number}"
              $"# name     = {quote account.name}"
              $"# keywords = [{quote (account.name.ToLowerInvariant())}]" ]

    [<Literal>]
    let private Template = """[accounts]
# The asset and liability accounts of the business, to choose the roles from:
%s
cash           = { number = "", name = "" }
mobilepay      = { number = "", name = "" }
fees           = { number = "", name = "" }
reimbursements = { number = "", name = "" }

[mobilepay]
# Maksutapa values, by meaning (I6). Expense and payout values are still unknown.
payment_in = ["Maksa Lippaaseen"]
fee        = ["Palvelumaksu"]
expense    = []
payout     = []

[matching]
days_before = 1
days_after  = 3
max_subset  = 8
tolerance   = 0.50
# M8 cost weights: lower total cost wins
weight_unmatched     = 100
weight_near_miss     = 10
weight_date_distance = 1
weight_subset_size   = 0.1
weight_coherence     = -5

[classification]
majority_min    = 3      # K4
same_amount_min = 3      # K5

[classification.cafe]                # K6
account                  = ""
max_amount               = 10.00
min_transactions_per_day = 3

# Keywords are case-insensitive substrings. The first rule that hits wins, so order by priority.
# Below is one stub per revenue and expense account; uncomment the ones to use and edit their keywords.

%s

[coalesce.cash]
max_days = 3
max_gap  = 1

[coalesce.mobilepay]
max_days = 4
max_gap  = 1

[coalesce.fees]
max_days = 7
max_gap  = 7

[posting]
marker = "[kassakirja]"
"""

    /// A `rules.toml` skeleton for the chart of accounts: the role accounts blank, every revenue
    /// and expense account a commented-out rule stub, in account-number order.
    let render (accounts: Account list) : string =
        let ofKind kind =
            accounts
            |> List.filter (fun account -> account.kind = kind)
            |> List.sortBy (fun account -> account.number)
        let candidates =
            ofKind AccountKind.Balance
            |> List.map (fun account -> $"#   {account.number}  {account.name}")
            |> String.concat "\n"
        let stubs =
            List.map (stub "income") (ofKind AccountKind.Income) @ List.map (stub "expense") (ofKind AccountKind.Expense)
            |> String.concat "\n\n"
        sprintf Template candidates stubs
