namespace Nocfo.Cashbook

open System
open System.Globalization
open CsvReader

/// What a MobilePay transaction is, by its `Maksutapa` (I6).
[<RequireQualifiedAccess>]
type TransactionKind =
    | PaymentIn
    | Fee
    | Expense
    | Payout
    | Unknown

/// One transaction of the MobilePay Box export (I4–I6), signed as exported: fees and expenses negative.
/// The text is its `Viesti`, the name its `Nimi` (I8).
type Transaction =
    { id:     string
      date:   DateOnly
      time:   TimeOnly
      name:   string
      text:   string
      amount: decimal
      kind:   TransactionKind }

module MobilePay =

    let private header =
        [ "Päivämäärä"; "Nimi"; "Tyyppi"; "Numero"; "Viesti"; "Summa"; "Valuutta"; "Maksutapa"; "Tapahtumatunnus" ]

    let private asStamp (text: string) =
        match DateTime.TryParseExact(text, "dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None) with
        | true, stamp -> Some stamp
        | _ -> None

    let private asAmount (text: string) =
        let styles = NumberStyles.AllowLeadingSign ||| NumberStyles.AllowDecimalPoint
        match Decimal.TryParse(text, styles, CultureInfo.InvariantCulture) with
        | true, amount -> Some amount
        | _ -> None

    let private asEuro (text: string) = if text = "EUR" then Some () else None

    let private kindOf (kinds: MobilePayKinds) (maksutapa: string) =
        if   List.contains maksutapa kinds.payment_in then TransactionKind.PaymentIn
        elif List.contains maksutapa kinds.fee        then TransactionKind.Fee
        elif List.contains maksutapa kinds.expense    then TransactionKind.Expense
        elif List.contains maksutapa kinds.payout     then TransactionKind.Payout
        else TransactionKind.Unknown

    let private transaction (kinds: MobilePayKinds) (row: Row) : Transaction =
        row.Parse("Valuutta", "EUR", asEuro, ())
        let stamp = row.Parse("Päivämäärä", "a date and time (dd/MM/yyyy HH:mm)", asStamp, DateTime.MinValue)
        { id     = row.String "Tapahtumatunnus"
          date   = DateOnly.FromDateTime stamp
          time   = TimeOnly.FromDateTime stamp
          name   = row.String "Nimi"
          text   = row.String "Viesti"
          amount = row.Parse("Summa", "an amount (decimal point)", asAmount, 0M)
          kind   = kindOf kinds (row.String "Maksutapa") }

    /// Reads a MobilePay Box export (I4–I7), keeping the first of the rows that repeat a transaction id (I5)
    /// and leaving out every malformed row, reported with its file line (P9).
    let readRows (kinds: MobilePayKinds) (text: string) : Result<Transaction list * string list, string> =
        CsvReader.readRows "," header text (transaction kinds)
        |> Result.map (fun (transactions, errors) -> List.distinctBy (fun transaction -> transaction.id) transactions, errors)

    /// Every transaction of a MobilePay Box export, or every error in it.
    let read (kinds: MobilePayKinds) (text: string) : Result<Transaction list, string list> =
        CsvReader.strict (readRows kinds text)
