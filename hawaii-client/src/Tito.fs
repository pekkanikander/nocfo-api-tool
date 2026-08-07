namespace Nocfo

open System
open System.Globalization
open System.Text
open Nocfo.Domain

/// Character set of a TITO file. Finnish banks emit pure 7-bit ISO 646-FI / SFS 4017, in which
/// the ASCII bracket positions carry Ä Ö Å ä ö å; some foreign systems emit 8-bit files instead.
type TitoCharset =
  | Auto
  | AsciiFi
  | Latin1
  | Utf8

/// T00 — statement header. Only the fields a reconciliation needs are decoded.
type TitoHeader =
  { account: string
    statement_number: string
    period_from: DateOnly
    period_to: DateOnly
    opening_date: DateOnly
    opening_balance: decimal
    currency: string
    iban: string }

/// T10 — one bank transaction, with its T11 detail lines attached verbatim.
type TitoTransaction =
  { number: string
    archive_id: string
    booking_date: DateOnly
    value_date: DateOnly option
    entry_code: string
    entry_text: string
    amount: decimal
    counterparty: string
    counterparty_account: string
    reference: string
    details: string list }

/// T40 — the closing balance of one day. This is what the reconciliation is built on.
type TitoDayBalance =
  { date: DateOnly
    balance: decimal
    available: decimal option }

type TitoRecord =
  | Header of TitoHeader
  | Transaction of TitoTransaction
  | Detail of subtype: string * text: string
  | DayBalance of TitoDayBalance
  /// Any record type this reader does not decode (T50 cumulative, T70 notice, bank extensions).
  | Other of tag: string

/// One T00 header and everything that follows it. A single file carries several statements,
/// possibly for different bank accounts.
type TitoStatement =
  { header: TitoHeader
    transactions: TitoTransaction list
    day_balances: TitoDayBalance list }

/// Reader for the Finnish machine-readable bank statement (TITO, "Konekielinen tiliote"),
/// which Nordea delivers with an `.nda` extension.
///
/// The record layouts below were reverse-engineered from one Nordea file and then verified
/// field by field against the Finanssiala specification, "Konekielinen tiliote, Palvelukuvaus"
/// v3.3 (20.8.2007), §3.4. Only the fields this tool consumes are decoded; T80/T81
/// notification records and the remaining record types are skipped as `Other`.
module Tito =

  /// SFS 4017 puts Finnish letters where ASCII has brackets and braces.
  let private asciiFiLetters =
    dict [ '[', 'Ä'; '\\', 'Ö'; ']', 'Å'; '{', 'ä'; '|', 'ö'; '}', 'å' ]

  let private transliterate (text: string) =
    text |> String.map (fun c ->
      match asciiFiLetters.TryGetValue c with
      | true, letter -> letter
      | _            -> c)

  /// `Auto` reads ISO 646-FI when the file is entirely 7-bit, which is what the standard
  /// prescribes, and Latin-1 otherwise. Reading an ISO 646-FI file as Latin-1 leaves a literal
  /// `{` in every Finnish name, which is silent corruption rather than an error.
  let decode (charset: TitoCharset) (bytes: byte[]) : string =
    let sevenBit = bytes |> Array.forall (fun b -> b < 0x80uy)
    match charset with
    | AsciiFi          -> transliterate (Encoding.ASCII.GetString bytes)
    | Auto when sevenBit -> transliterate (Encoding.ASCII.GetString bytes)
    | Auto | Latin1    -> Encoding.Latin1.GetString bytes
    | Utf8             -> Encoding.UTF8.GetString bytes

  /// A record layout is data: the fields of a record type, sliced out of its fixed-width line.
  type private Field = { Name: string; Offset: int; Width: int }

  let private slice (layout: Field list) (line: string) : Map<string, string> =
    let take (field: Field) =
      if field.Offset >= line.Length then ""
      else line.Substring(field.Offset, min field.Width (line.Length - field.Offset)).Trim()
    layout |> List.map (fun field -> field.Name, take field) |> Map.ofList

  /// Sign followed by 18 digits, in cents.
  let private amount (name: string) (text: string) : Result<decimal, DomainError> =
    let digits = text.TrimStart('+', '-')
    match Int64.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture) with
    | true, cents ->
        let sign = if text.StartsWith('-') then -1M else 1M
        Ok (sign * decimal cents / 100M)
    | _ -> Error (DomainError.BadData $"Field '{name}' is not a TITO amount: '{text}'")

  let private date (name: string) (text: string) : Result<DateOnly, DomainError> =
    match DateOnly.TryParseExact(text, "yyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None) with
    | true, value -> Ok value
    | _ -> Error (DomainError.BadData $"Field '{name}' is not a TITO date (yymmdd): '{text}'")

  /// A date the bank did not set comes through as zeroes or as blanks.
  let private optionalDate (name: string) (text: string) : Result<DateOnly option, DomainError> =
    if text.Trim('0') = "" then Ok None else date name text |> Result.map Some

  let private optionalAmount (name: string) (text: string) : Result<decimal option, DomainError> =
    if text = "" then Ok None else amount name text |> Result.map Some

  let private headerLayout =
    [ { Name = "account";          Offset =   9; Width = 14 }
      { Name = "statement_number"; Offset =  23; Width =  3 }
      { Name = "period_from";      Offset =  26; Width =  6 }
      { Name = "period_to";        Offset =  32; Width =  6 }
      { Name = "opening_date";     Offset =  65; Width =  6 }
      { Name = "opening_balance";  Offset =  71; Width = 19 }
      { Name = "currency";         Offset =  96; Width =  3 }
      { Name = "iban";             Offset = 292; Width = 18 } ]

  let private transactionLayout =
    [ { Name = "number";               Offset =   6; Width =  6 }
      { Name = "archive_id";           Offset =  12; Width = 18 }
      { Name = "booking_date";         Offset =  30; Width =  6 }
      { Name = "value_date";           Offset =  36; Width =  6 }
      { Name = "entry_code";           Offset =  49; Width =  3 }
      { Name = "entry_text";           Offset =  52; Width = 35 }
      { Name = "amount";               Offset =  87; Width = 19 }
      { Name = "counterparty";         Offset = 108; Width = 35 }
      { Name = "counterparty_account"; Offset = 144; Width = 14 }
      { Name = "reference";            Offset = 159; Width = 20 } ]

  let private dayBalanceLayout =
    [ { Name = "date";      Offset =  6; Width =  6 }
      { Name = "balance";   Offset = 12; Width = 19 }
      { Name = "available"; Offset = 31; Width = 19 } ]

  let private parseHeader (line: string) =
    let fields = slice headerLayout line
    date "period_from" fields.["period_from"] |> Result.bind (fun periodFrom ->
    date "period_to" fields.["period_to"] |> Result.bind (fun periodTo ->
    date "opening_date" fields.["opening_date"] |> Result.bind (fun openingDate ->
    amount "opening_balance" fields.["opening_balance"] |> Result.map (fun openingBalance ->
      Header { account          = fields.["account"]
               statement_number = fields.["statement_number"]
               period_from      = periodFrom
               period_to        = periodTo
               opening_date     = openingDate
               opening_balance  = openingBalance
               currency         = fields.["currency"]
               iban             = fields.["iban"] }))))

  let private parseTransaction (line: string) =
    let fields = slice transactionLayout line
    date "booking_date" fields.["booking_date"] |> Result.bind (fun bookingDate ->
    optionalDate "value_date" fields.["value_date"] |> Result.bind (fun valueDate ->
    amount "amount" fields.["amount"] |> Result.map (fun value ->
      Transaction { number               = fields.["number"]
                    archive_id           = fields.["archive_id"]
                    booking_date         = bookingDate
                    value_date           = valueDate
                    entry_code           = fields.["entry_code"]
                    entry_text           = fields.["entry_text"]
                    amount               = value
                    counterparty         = fields.["counterparty"]
                    counterparty_account = fields.["counterparty_account"]
                    reference            = fields.["reference"]
                    details              = [] })))

  let private parseDayBalance (line: string) =
    let fields = slice dayBalanceLayout line
    date "date" fields.["date"] |> Result.bind (fun value ->
    amount "balance" fields.["balance"] |> Result.bind (fun balance ->
    optionalAmount "available" fields.["available"] |> Result.map (fun available ->
      DayBalance { date = value; balance = balance; available = available })))

  let private parseRecord (line: string) : Result<TitoRecord, DomainError> =
    match line.Substring(0, 3) with
    | "T00" -> parseHeader line
    | "T10" -> parseTransaction line
    | "T11" -> Ok (Detail (slice [ { Name = "subtype"; Offset = 6; Width = 2 } ] line |> Map.find "subtype",
                           (if line.Length > 8 then line.Substring(8).Trim() else "")))
    | "T40" -> parseDayBalance line
    | tag   -> Ok (Other tag)

  /// Records are `T` + 2-digit type + 3-digit declared length + fixed-width fields. Files are
  /// normally CRLF-terminated, but the standard permits an unterminated fixed-length stream, so
  /// the declared length frames the record and any line break after it is skipped.
  let records (text: string) : Result<TitoRecord list, DomainError> =
    let rec skipBreaks position =
      if position < text.Length && (text.[position] = '\r' || text.[position] = '\n')
      then skipBreaks (position + 1)
      else position

    let rec loop position acc =
      let position = skipBreaks position
      if position >= text.Length then Ok (List.rev acc)
      elif text.[position] <> 'T' || position + 6 > text.Length then
        Error (DomainError.BadData $"Expected a TITO record identifier at offset {position}.")
      else
        match Int32.TryParse(text.Substring(position + 3, 3), NumberStyles.None, CultureInfo.InvariantCulture) with
        | false, _ ->
            Error (DomainError.BadData
                     $"Record at offset {position} does not declare a length: '{text.Substring(position, 6)}'")
        | true, length ->
            if length < 6 then
              Error (DomainError.BadData
                       $"Record at offset {position} declares length {length}, shorter than its own header.")
            elif position + length > text.Length then
              Error (DomainError.BadData
                       $"Record at offset {position} declares length {length} but the file ends after {text.Length - position} characters.")
            else
              let line = text.Substring(position, length)
              if line.IndexOfAny [| '\r'; '\n' |] >= 0 then
                Error (DomainError.BadData
                         $"Record at offset {position} declares length {length} but ends after {line.IndexOfAny [| '\r'; '\n' |]} characters.")
              else
                match parseRecord line with
                | Error err -> Error err
                | Ok record -> loop (position + length) (record :: acc)

    loop 0 []

  /// Groups records into statements: each T00 opens one, T11 details attach to the T10 they
  /// follow, and record types this reader does not decode are skipped rather than rejected.
  let statements (records: TitoRecord list) : Result<TitoStatement list, DomainError> =
    let attachDetail text = function
      | [] -> Error (DomainError.BadData $"A T11 detail record precedes any transaction: '{text}'")
      | (latest: TitoTransaction) :: rest -> Ok ({ latest with details = latest.details @ [ text ] } :: rest)

    let finish (statement: TitoStatement) =
      { statement with
          transactions = List.rev statement.transactions
          day_balances = List.rev statement.day_balances }

    let rec loop remaining (current: TitoStatement option) acc =
      match remaining with
      | [] ->
          match current with
          | Some statement -> Ok (List.rev (finish statement :: acc))
          | None -> Ok (List.rev acc)
      | record :: rest ->
          match record, current with
          | Header header, _ ->
              let acc = current |> Option.fold (fun acc statement -> finish statement :: acc) acc
              loop rest (Some { header = header; transactions = []; day_balances = [] }) acc
          | (Transaction _ | Detail _ | DayBalance _), None ->
              Error (DomainError.BadData "The statement file does not start with a T00 header record.")
          | Transaction transaction, Some statement ->
              loop rest (Some { statement with transactions = transaction :: statement.transactions }) acc
          | Detail (_, text), Some statement ->
              match attachDetail text statement.transactions with
              | Error err -> Error err
              | Ok transactions -> loop rest (Some { statement with transactions = transactions }) acc
          | DayBalance balance, Some statement ->
              loop rest (Some { statement with day_balances = balance :: statement.day_balances }) acc
          | Other _, _ ->
              loop rest current acc

    loop records None []

  let read (charset: TitoCharset) (bytes: byte[]) : Result<TitoStatement list, DomainError> =
    decode charset bytes |> records |> Result.bind statements

  /// The day balances of one bank account, in date order: the spec does not order the statements
  /// within a file (Nordea emits an annual download latest month first), so the selected
  /// statements are sorted by period; the balances within each statement are kept as parsed.
  /// A bookkeeping account number cannot be matched against a bank account number, so a file
  /// carrying several accounts has to be told which one is meant; `account` matches the tail of
  /// the account number or of the IBAN.
  let dayBalances (account: string option) (statements: TitoStatement list)
    : Result<TitoDayBalance list, DomainError> =
    let matches (header: TitoHeader) =
      match account with
      | None -> true
      | Some wanted ->
          header.account.EndsWith(wanted, StringComparison.OrdinalIgnoreCase)
          || header.iban.EndsWith(wanted, StringComparison.OrdinalIgnoreCase)

    let selected = statements |> List.filter (fun statement -> matches statement.header)
    let accounts = selected |> List.map (fun statement -> statement.header.account) |> List.distinct

    match accounts with
    | [] ->
        let available =
          statements |> List.map (fun statement -> statement.header.account) |> List.distinct
        Error (DomainError.Invalid
                 ("No statement in the file is for account " + defaultArg account "(any)" +
                  ". The file carries: " + String.Join(", ", available)))
    | [ _ ] ->
        Ok (selected
            |> List.sortBy (fun statement -> statement.header.period_from)
            |> List.collect (fun statement -> statement.day_balances))
    | several ->
        Error (DomainError.Invalid
                 ("The statement file carries several bank accounts (" + String.Join(", ", several) +
                  "). Select one by appending '#<account>' to the source, e.g. nda:statement.nda#" + List.head several))
