namespace Nocfo.Cashbook

open System
open System.Globalization
open CsvReader

/// How a cash-book line says it was paid (I2).
[<RequireQualifiedAccess>]
type PaymentMethod =
    | MobilePay
    | Cash
    | Unmarked

/// One line of the cash book (I1–I3). The amount is signed: `tulo`, or `-meno`. `tapa` is kept as
/// written, since the reports print it so.
type CashBookLine =
    { file_line: int
      page:      string
      date:      DateOnly
      text:      string
      amount:    decimal
      method:    PaymentMethod
      tapa:      string
      receipts:  int
      remark:    string }

module CashBook =

    /// Amounts as the book writes them (I1).
    let decimalComma = NumberFormatInfo(NumberDecimalSeparator = ",")

    let private header = [ "sivu"; "pvm"; "mita"; "tulo"; "meno"; "tapa"; "kuitti"; "huom" ]

    let private asDate (text: string) =
        match DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None) with
        | true, date -> Some date
        | _ -> None

    let private asAmount (text: string) =
        let styles = NumberStyles.AllowLeadingSign ||| NumberStyles.AllowDecimalPoint
        match Decimal.TryParse(text, styles, decimalComma) with
        | true, amount -> Some amount
        | _ -> None

    let private asCount (text: string) =
        if text = "" then Some 0 else
        match Int32.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture) with
        | true, count -> Some count
        | _ -> None

    let private asPaymentMethod (text: string) =
        match text.Trim().ToUpperInvariant() with
        | "MP" | "MP?"         -> Some PaymentMethod.MobilePay
        | "KÄT" | "KT" | "KÄ"  -> Some PaymentMethod.Cash
        | ""                   -> Some PaymentMethod.Unmarked
        | _                    -> None

    let private line (row: Row) : CashBookLine =
        let amount =
            match row.String "tulo" = "", row.String "meno" = "" with
            | false, true -> row.Parse("tulo", "an amount (decimal comma)", asAmount, 0M)
            | true, false -> -(row.Parse("meno", "an amount (decimal comma)", asAmount, 0M))
            | _ ->
                row.Fail "exactly one of 'tulo' and 'meno' must be given"
                0M
        { file_line = row.Line
          page      = row.String "sivu"
          date      = row.Parse("pvm", "a date (YYYY-MM-DD)", asDate, DateOnly.MinValue)
          text      = row.String "mita"
          amount    = amount
          method    = row.Parse("tapa", "MP, MP?, KÄT, KT, KÄ or empty", asPaymentMethod, PaymentMethod.Unmarked)
          tapa      = row.String "tapa"
          receipts  = row.Parse("kuitti", "a count or empty", asCount, 0)
          remark    = row.String "huom" }

    /// Reads a cash book (I1–I3); every malformed line is reported with its file line.
    let read (text: string) : Result<CashBookLine list, string list> = CsvReader.read ";" header text line
