namespace Nocfo.Cashbook

open System.IO
open System.Reflection

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

    let private template =
        use reader = new StreamReader(Assembly.GetExecutingAssembly().GetManifestResourceStream "rules-template.toml")
        reader.ReadToEnd()

    let private quote (text: string) =
        "\"" + text.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\""

    let private stub (table: string) (account: Account) =
        String.concat "\n"
            [ $"# [[classification.{table}]]"
              $"# account  = {quote account.number}"
              $"# name     = {quote account.name}"
              "# keywords = []" ]

    /// A `rules.toml` skeleton for the chart of accounts: the payment and fee accounts blank, every revenue
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
        template.Replace("{{candidates}}", candidates).Replace("{{stubs}}", stubs)
