namespace Nocfo.Cashbook

open System.Globalization
open System.IO
open CsvHelper
open CsvHelper.Configuration

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

    let private tier (tier: Tier) =
        match tier with
        | Tier.M1        -> "M1"
        | Tier.M2        -> "M2"
        | Tier.M3        -> "M3"
        | Tier.M4        -> "M4"
        | Tier.M5        -> "M5"
        | Tier.Cash      -> "cash"
        | Tier.BookOnly  -> "book-only"
        | Tier.Ambiguous -> "ambiguous"

    let private amount (value: decimal) = value.ToString("0.00", CashBook.decimalComma)

    let private row (lineMatch: LineMatch) : MatchRow =
        let line = lineMatch.line
        { book_page   = line.page
          book_date   = line.date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
          book_text   = line.text
          book_amount = amount line.amount
          book_tapa   = line.tapa
          tier        = tier lineMatch.tier
          mp_ids      = Matching.ids lineMatch.transactions
          mp_sum      = match lineMatch.transactions with
                        | [] -> ""
                        | transactions -> amount (transactions |> List.sumBy (fun transaction -> transaction.amount))
          note        = lineMatch.note }

    /// `match.csv` (R1): one row per book line in file order, `;` separated, LF line endings, a field
    /// quoted only when it contains `;`, `"` or a newline.
    let render (matches: Matches) : string =
        let quoted (args: ShouldQuoteArgs) = args.Field.IndexOfAny [| ';'; '"'; '\n'; '\r' |] >= 0
        let configuration =
            CsvConfiguration(CultureInfo.InvariantCulture, Delimiter = ";", NewLine = "\n",
                             ShouldQuote = ShouldQuote quoted)
        use writer = new StringWriter()
        use csv = new CsvWriter(writer, configuration)
        csv.WriteRecords(matches.lines |> List.map row)
        csv.Flush()
        writer.ToString()
