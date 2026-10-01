namespace Nocfo.Cashbook

open System
open System.Globalization
open System.IO
open CsvHelper
open CsvHelper.Configuration
open CsvReader

/// How every report writes its cells: `;` separated, LF line endings, a field quoted only when it contains
/// `;`, `"` or a newline, amounts with the decimal comma.
module private Report =

    let amount (value: decimal) = value.ToString("0.00", CashBook.decimalComma)

    let day (date: DateOnly) = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)

    let render (rows: 'T list) : string =
        let quoted (args: ShouldQuoteArgs) = args.Field.IndexOfAny [| ';'; '"'; '\n'; '\r' |] >= 0
        let configuration =
            CsvConfiguration(CultureInfo.InvariantCulture, Delimiter = ";", NewLine = "\n",
                             ShouldQuote = ShouldQuote quoted)
        use writer = new StringWriter()
        use csv = new CsvWriter(writer, configuration)
        csv.WriteRecords rows
        csv.Flush()
        writer.ToString()

/// One row of `match.csv` (R1).
type MatchRow =
    { book_page:   string
      book_date:   string
      book_text:   string
      book_amount: string
      book_tapa:   string
      tier:        string
      mp_ids:      string
      mp_sum:      string
      note:        string }

module MatchReport =

    let private header =
        [ "book_page"; "book_date"; "book_text"; "book_amount"; "book_tapa"; "tier"; "mp_ids"; "mp_sum"; "note" ]

    /// The cells that identify the book line of a row.
    let private book (line: CashBookLine) =
        [ line.page; Report.day line.date; line.text; Report.amount line.amount ]

    /// The note as `match.csv` words it (R1).
    let private note (note: Note) =
        match note with
        | Note.Contradiction       -> "contradiction: MobilePay wins"
        | Note.PossibleMp possible -> "possible-mp: " + Matching.ids possible
        | Note.AssumedCash
        | Note.UnmarkedExpense     -> Note.reason note

    let private row (lineMatch: LineMatch) : MatchRow =
        let line = lineMatch.line
        { book_page   = line.page
          book_date   = Report.day line.date
          book_text   = line.text
          book_amount = Report.amount line.amount
          book_tapa   = line.tapa
          tier        = Tier.describe lineMatch.tier
          mp_ids      = Matching.ids lineMatch.transactions
          mp_sum      = match lineMatch.transactions with
                        | [] -> ""
                        | transactions -> Report.amount (transactions |> List.sumBy (fun transaction -> transaction.amount))
          note        = lineMatch.note |> Option.map note |> Option.defaultValue "" }

    /// `match.csv` (R1): one row per book line in file order.
    let render (matches: Matches) : string = Report.render (matches.lines |> List.map row)

    /// A row read back: the line it describes, and the tier and transactions it gives the line.
    type private Verdict =
        { file_line:    int
          book:         string list
          tier:         Tier
          transactions: Transaction list }

    /// Reads a `match.csv` back as the matches of the book lines (section 8): the rows describe the lines
    /// one to one in file order, their tiers and transaction ids are taken as given, and the notes are
    /// recomputed.
    let read (lines: CashBookLine list) (transactions: Transaction list) (text: string) : Result<Matches, string list> =
        let byId = transactions |> List.map (fun transaction -> transaction.id, transaction) |> Map.ofList
        let verdict (row: Row) : Verdict =
            { file_line    = row.Line
              book         = [ row.String "book_page"; row.String "book_date"; row.String "book_text"; row.String "book_amount" ]
              tier         = row.Parse("tier", "M1–M5, cash, book-only or ambiguous", Tier.parse, Tier.Cash)
              transactions =
                [ for id in (row.String "mp_ids").Split('+', StringSplitOptions.RemoveEmptyEntries) do
                      match Map.tryFind id byId with
                      | Some transaction -> yield transaction
                      | None -> row.Fail $"unknown transaction id '{id}'" ] }
        let disagreement (line: CashBookLine, verdict: Verdict) =
            let expected, actual = String.concat ";" (book line), String.concat ";" verdict.book
            if expected = actual then None
            else Some $"[L{verdict.file_line}] the row must describe book line L{line.file_line} '{expected}', not '{actual}'"
        CsvReader.read ";" header text verdict
        |> Result.bind (fun verdicts ->
            if verdicts.Length <> lines.Length then
                Error [ $"[L1] expected as many rows as book lines ({lines.Length}), found {verdicts.Length}" ]
            else
                match List.zip lines verdicts |> List.choose disagreement with
                | [] ->
                    let tiers = List.zip lines verdicts |> List.map (fun (line, verdict) -> line, verdict.tier, verdict.transactions)
                    Ok (Matching.ofTiers tiers transactions)
                | errors -> Error errors)

/// One row of `plan.csv` (P1).
type PlanRow =
    { date:            string
      payment_account: string
      offset_account:  string
      amount:          string
      lines:           int
      receipts:        int
      flagged:         int
      description:     string }

module PlanReport =

    let private row (document: Document) : PlanRow =
        { date            = Report.day document.date
          payment_account = document.payment.number
          offset_account  = document.offset |> Option.map (fun offset -> offset.number) |> Option.defaultValue ""
          amount          = Report.amount document.amount
          lines           = document.lines
          receipts        = document.receipts
          flagged         = document.flagged
          description     = document.description }

    /// `plan.csv` (P1): one row per document, in the plan's order.
    let render (documents: Document list) : string = Report.render (documents |> List.map row)

/// One row of `review.csv` (R2).
type ReviewRow =
    { source: string
      ref:    string
      date:   string
      amount: string
      text:   string
      reason: string }

module ReviewReport =

    let private row (review: Review) : ReviewRow =
        match review.origin with
        | Origin.Line line ->
            { source = "book"
              ref    = $"L{line.file_line}"
              date   = Report.day line.date
              amount = Report.amount line.amount
              text   = line.text
              reason = review.reason }
        | Origin.Transaction transaction ->
            { source = "mp"
              ref    = transaction.id
              date   = Report.day transaction.date
              amount = Report.amount transaction.amount
              text   = transaction.text
              reason = review.reason }

    /// `review.csv` (R2): one row per item and reason, in the plan's order.
    let render (reviews: Review list) : string = Report.render (reviews |> List.map row)
