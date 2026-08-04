namespace Nocfo.Domain

open System
open System.Globalization
open System.IO
open FSharp.Control
open NocfoApi.Types
open Nocfo
open Nocfo.Domain
open NocfoClient

/// The API reports money as `float`; every accumulation and comparison downstream is `decimal`.
module Money =
  let ofFloat (value: float) : decimal = Math.Round(decimal value, 2)

/// One line of a rolling balance: an account's entries in date order, each with the balance
/// after it. The first row of each account is the balance carried into the period.
[<CLIMutable>]
type BalanceRow =
  { account_number: string
    account_name: string
    date: string
    document_number: string
    description: string option
    debet: decimal
    credit: decimal
    amount: decimal
    balance: decimal }

/// The closing balance of one day, which is the granularity a bank statement offers.
[<CLIMutable>]
type DailyBalanceRow =
  { date: string
    balance: decimal }

[<CLIMutable>]
type ReconcileRow =
  { date: string
    left_balance: decimal
    right_balance: decimal
    difference: decimal
    status: string }

module Balance =

  /// Ledger entry dates come back as ISO date-times (`2025-01-21T00:00:00`); a rolling balance
  /// is keyed on the day, and comparing an unnormalised date-time against a bank statement's
  /// date would never match.
  let private day (text: string) : string =
    match DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None) with
    | true, value -> value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
    | _ -> raise (DomainStreamException (DomainError.Unexpected $"The ledger report returned an unparseable date: '{text}'"))

  /// The accounts of a ledger report, narrowed to a selection of account numbers.
  /// An empty selection means every account in the report.
  let selectAccounts (numbers: Set<string>) (ledger: LedgerJsonResponse) : LedgerReportAccountSchema list =
    if Set.isEmpty numbers then ledger.accounts
    else ledger.accounts |> List.filter (fun account -> numbers.Contains account.number)

  /// The rolling balance of one account: the opening carry-in, then one row per entry.
  /// `balance_csum` already includes the opening balance, so it is the running balance as is.
  let accountRows (dateFrom: string) (account: LedgerReportAccountSchema) : BalanceRow list =
    let opening =
      { account_number  = account.number
        account_name    = account.name
        date            = day dateFrom
        document_number = ""
        description     = Some "Opening balance"
        debet           = 0M
        credit          = 0M
        amount          = 0M
        balance         = Money.ofFloat account.opening_balance }

    let entryRow (entry: LedgerReportEntrySchema) =
      let debet  = Money.ofFloat entry.debet
      let credit = Money.ofFloat entry.credit
      { account_number  = account.number
        account_name    = account.name
        date            = day entry.date
        document_number = entry.number
        description     = entry.description
        debet           = debet
        credit          = credit
        amount          = debet - credit
        balance         = Money.ofFloat entry.balance_csum }

    opening :: (account.entries |> List.map entryRow)

  let rows (numbers: Set<string>) (ledger: LedgerJsonResponse) : BalanceRow list =
    let dateFrom = defaultArg ledger.date_from ""
    ledger |> selectAccounts numbers |> List.collect (accountRows dateFrom)

  /// The closing balance of each day the account moved on. The opening row is included, so a
  /// reconciliation has a balance to carry forward from before the first transaction.
  let dailyClosing (rows: BalanceRow list) : DailyBalanceRow list =
    rows
    |> List.groupBy (fun row -> row.date)
    |> List.map (fun (date, group) -> { date = date; balance = (List.last group).balance })

  /// Alignment merges two ordered streams; an unordered one desynchronises it silently.
  let requireAscending (side: string) (rows: AsyncSeq<DailyBalanceRow>) : AsyncSeq<DailyBalanceRow> =
    asyncSeq {
      let mutable previous = None
      for row in rows do
        match previous with
        | Some earlier when earlier > row.date ->
            raise (StreamOrderException $"The {side} balances are not in date order: {earlier} precedes {row.date}")
        | _ -> ()
        previous <- Some row.date
        yield row
    }

module Reconcile =

  /// Compares two daily-balance streams day by day. A balance holds until it next changes, so a
  /// date present on only one side is still compared against the other side's last known
  /// balance; `left-only` and `right-only` mark only the days before a side has any balance.
  let daily (tolerance: decimal) (left: AsyncSeq<DailyBalanceRow>) (right: AsyncSeq<DailyBalanceRow>)
    : AsyncSeq<ReconcileRow> =
    let aligned =
      Streams.alignByKey
        (fun (row: DailyBalanceRow) -> row.date)
        (fun (row: DailyBalanceRow) -> row.date)
        (Balance.requireAscending "left" left)
        (Balance.requireAscending "right" right)

    asyncSeq {
      let mutable lastLeft  : decimal option = None
      let mutable lastRight : decimal option = None

      for alignment in aligned do
        let date, leftBalance, rightBalance =
          match alignment with
          | Aligned (l, r)     -> l.date, Some l.balance, Some r.balance
          | MissingRight l     -> l.date, Some l.balance, lastRight
          | MissingLeft r      -> r.date, lastLeft, Some r.balance

        lastLeft  <- leftBalance  |> Option.orElse lastLeft
        lastRight <- rightBalance |> Option.orElse lastRight

        match leftBalance, rightBalance with
        | Some l, Some r ->
            let difference = l - r
            yield { date            = date
                    left_balance    = l
                    right_balance   = r
                    difference      = difference
                    status          = if abs difference <= tolerance then "ok" else "differs" }
        | Some l, None ->
            yield { date = date; left_balance = l; right_balance = 0M; difference = 0M; status = "left-only" }
        | None, Some r ->
            yield { date = date; left_balance = 0M; right_balance = r; difference = 0M; status = "right-only" }
        | None, None -> ()
    }

/// Where a daily-balance stream comes from: the server's ledger, a bank statement, or a CSV
/// that some earlier command wrote.
type BalanceSource =
  | LedgerSource
  | NdaSource of path: string * account: string option
  | CsvSource of path: string

type BalanceQuery =
  { dateFrom: string option
    dateTo: string option
    accounts: Set<string>
    charset: TitoCharset }

module BalanceSource =

  let parse (text: string) : Result<BalanceSource, DomainError> =
    match text with
    | "ledger" -> Ok LedgerSource
    | _ when text.StartsWith("nda:", StringComparison.Ordinal) ->
        match text.Substring(4).Split('#') with
        | [| path |]          -> Ok (NdaSource (path, None))
        | [| path; account |] -> Ok (NdaSource (path, Some account))
        | _ -> Error (DomainError.Invalid $"A bank statement source takes at most one '#account' selector: '{text}'")
    | _ when text.StartsWith("csv:", StringComparison.Ordinal) -> Ok (CsvSource (text.Substring 4))
    | _ ->
        Error (DomainError.Invalid
                 $"Unknown balance source '{text}'. Expected 'ledger', 'nda:<path>[#<account>]' or 'csv:<path>'.")

  let private fromLedger (business: BusinessContext option) (query: BalanceQuery) =
    match business, query.dateFrom, query.dateTo with
    | Some context, Some dateFrom, Some dateTo ->
        Ledger.fetch context dateFrom dateTo
        |> AsyncResult.bind (fun ledger ->
          match Balance.rows query.accounts ledger with
          | [] ->
              Error (DomainError.Invalid
                       "The ledger report has no account matching the selection for that period.")
          | rows ->
              let accounts = rows |> List.map (fun row -> row.account_number) |> List.distinct
              match accounts with
              | [ _ ] -> Ok (Balance.dailyClosing rows)
              | several ->
                  Error (DomainError.Invalid
                           ("A daily balance is for one account at a time; the selection covers " +
                            String.Join(", ", several) + ". Narrow it with --account.")))
    | None, _, _ ->
        async.Return (Error (DomainError.Invalid "The 'ledger' source needs a business; pass --business."))
    | _ ->
        async.Return (Error (DomainError.Invalid "The 'ledger' source needs a period; pass --from and --to."))

  let private fromNda (query: BalanceQuery) (path: string) (account: string option) =
    File.ReadAllBytes path
    |> Tito.read query.charset
    |> Result.bind (Tito.dayBalances account)
    |> Result.map (List.map (fun (balance: TitoDayBalance) ->
         { date = balance.date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture); balance = balance.balance }))
    |> async.Return

  let private fromCsv (path: string) =
    async {
      use reader = new StreamReader(path)
      let! rows = Csv.readCsvGeneric<DailyBalanceRow> reader None |> AsyncSeq.toListAsync
      return Ok rows
    }

  /// Opens a source as a daily-balance list. Every source is bounded by a statement period or a
  /// report period, so this materialises rather than streaming.
  let read (business: BusinessContext option) (query: BalanceQuery) (source: BalanceSource)
    : Async<Result<DailyBalanceRow list, DomainError>> =
    match source with
    | LedgerSource                -> fromLedger business query
    | NdaSource (path, account)   -> fromNda query path account
    | CsvSource path              -> fromCsv path
