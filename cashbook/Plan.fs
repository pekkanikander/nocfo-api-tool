namespace Nocfo.Cashbook

open System
open System.Globalization

/// A document to record (P1): the items of one payment account and offset account over a run of days, dated
/// the last of them (C1–C3), or a single item (C4, K7). The amount is the change of the payment account.
type Document =
    { date:        DateOnly
      payment:     LedgerAccount
      offset:      LedgerAccount option
      amount:      decimal
      lines:       int
      receipts:    int
      flagged:     int
      description: string }

/// Something for the bookkeeper to check, and why (R2).
type Review =
    { origin: Origin
      reason: string }

/// What `plan` makes of the matches: the documents to record and the items to review.
type Plan =
    { documents: Document list
      reviews:   Review list }

module Plan =

    let private day (date: DateOnly) = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)

    /// A book line is posted, as cash, when it was paid from the till: cash-marked, or unmarked and matched to
    /// nothing (M9, M10). A MobilePay-marked line is a hint only and so is a matched unmarked line, and a
    /// book-only or ambiguous line waits for `match.csv` to be corrected (M6, M8).
    let private posted (lineMatch: LineMatch) =
        lineMatch.tier = Tier.Cash && lineMatch.line.method <> PaymentMethod.MobilePay

    /// Every transaction with the book line it is matched to, if any, by date, time and id.
    let private transactions (matches: Matches) =
        [ for lineMatch in matches.lines do
              for transaction in lineMatch.transactions -> transaction, Some lineMatch.line
          for transaction in matches.unmatched -> transaction, None ]
        |> List.sortBy (fun (transaction, _) -> transaction.date, transaction.time, transaction.id)

    let private ofLine (line: CashBookLine) : Item =
        { origin = Origin.Line line; date = line.date; amount = line.amount; text = line.text }

    let private ofTransaction (transaction: Transaction, hint: CashBookLine option) : Item =
        { origin = Origin.Transaction transaction
          date   = transaction.date
          amount = transaction.amount
          text   = match hint with
                   | Some line -> $"{transaction.text} {line.text}"
                   | None -> transaction.text }

    let private payment (rules: Rules) (item: Item) =
        match item.origin with
        | Origin.Line _        -> rules.accounts.cash
        | Origin.Transaction _ -> rules.accounts.mobilepay

    let private receipts (item: Item) =
        match item.origin with
        | Origin.Line line     -> line.receipts
        | Origin.Transaction _ -> 0

    /// An expense paid from the till, which is never coalesced (C4).
    let private tillExpense (item: Item) =
        match item.origin with
        | Origin.Line line     -> line.amount < 0M
        | Origin.Transaction _ -> false

    let private offsetName (offset: LedgerAccount option) =
        offset |> Option.map (fun offset -> offset.name) |> Option.defaultValue "?"

    /// `<marker> <payment>/<offset> <date>[–<last>] (<n>)` (C5), the offset `?` while there is none (K7).
    let private summary (rules: Rules) (payment: LedgerAccount) (offset: LedgerAccount option) (items: Classified list) =
        let dates = items |> List.map (fun classified -> classified.item.date)
        let first, last = List.min dates, List.max dates
        let span = if first = last then day first else $"{day first}–{day last}"
        $"{rules.posting.marker} {payment.name}/{offsetName offset} {span} ({items.Length})"

    /// `<marker> <cash>/<offset> <date> <mita> (<k> kuittia)` for an expense paid from the till (C4, C5).
    let private tillExpenseSummary (rules: Rules) (offset: LedgerAccount option) (line: CashBookLine) =
        $"{rules.posting.marker} {rules.accounts.cash.name}/{offsetName offset} {day line.date} {line.text} ({line.receipts} kuittia)"

    let private document (payment: LedgerAccount) (offset: LedgerAccount option) (description: string) (items: Classified list) : Document =
        { date        = items |> List.map (fun classified -> classified.item.date) |> List.max
          payment     = payment
          offset      = offset
          amount      = items |> List.sumBy (fun classified -> classified.item.amount)
          lines       = items.Length
          receipts    = items |> List.sumBy (fun classified -> receipts classified.item)
          flagged     = items |> List.filter (fun classified -> classified.flagged) |> List.length
          description = description }

    /// One document for an item on its own: a till expense (C4) or an unclassified item (K7).
    let private single (rules: Rules) (classified: Classified) =
        let description =
            match classified.item.origin with
            | Origin.Line line when line.amount < 0M -> tillExpenseSummary rules classified.offset line
            | _ -> summary rules (payment rules classified.item) classified.offset [ classified ]
        document (payment rules classified.item) classified.offset description [ classified ]

    /// The coalescing block of a payment and offset account: the till's, the fees' or MobilePay's (C2, C3).
    let private block (rules: Rules) (payment: LedgerAccount) (offset: LedgerAccount option) =
        if payment = rules.accounts.cash then rules.coalesce.cash
        elif offset = Some rules.accounts.fees then rules.coalesce.fees
        else rules.coalesce.mobilepay

    /// The day groups of one payment and offset account merged greedily into runs, in date order: a day joins
    /// the run when its gap to the run's last day is at most `max_gap` and the run then holds at most
    /// `max_days` days (C2).
    let private runs (coalesce: Coalesce) (days: (DateOnly * Classified list) list) =
        let join (runs: (DateOnly * Classified list) list list) (date: DateOnly, items: Classified list) =
            match runs with
            | ((last, _) :: _ as run) :: rest
                when date.DayNumber - last.DayNumber <= coalesce.max_gap && run.Length < coalesce.max_days ->
                ((date, items) :: run) :: rest
            | _ -> [ date, items ] :: runs
        days |> List.sortBy fst |> List.fold join [] |> List.rev |> List.map (List.rev >> List.collect snd)

    /// The classified items grouped by date, payment account and offset account (C1), the day groups of
    /// each account pair coalesced into runs under its block (C2, C3), one document per run.
    let private coalesced (rules: Rules) (classified: Classified list) =
        classified
        |> List.groupBy (fun classified -> payment rules classified.item, classified.offset)
        |> List.collect (fun ((payment, offset), items) ->
            items
            |> List.groupBy (fun classified -> classified.item.date)
            |> runs (block rules payment offset)
            |> List.map (fun run -> document payment offset (summary rules payment offset run) run))

    /// The reasons to look at an item (R2): what matching found, then what the plan found; book lines in file
    /// order, then transactions by date, time and id.
    let private reviews (matches: Matches) (transactions: (Transaction * CashBookLine option) list) (classified: Classified list) =
        let planned (origin: Origin) =
            match classified |> List.tryFind (fun classified -> classified.item.origin = origin) with
            | Some { offset = None }  -> [ "unclassified" ]
            | Some { flagged = true } -> [ "flagged" ]
            | _                       -> []
        let ofLine (lineMatch: LineMatch) =
            [ if lineMatch.tier = Tier.BookOnly || lineMatch.tier = Tier.Ambiguous then yield Tier.describe lineMatch.tier
              yield! lineMatch.note |> Option.map Note.reason |> Option.toList
              yield! planned (Origin.Line lineMatch.line) ]
        let ofTransaction (transaction: Transaction, hint: CashBookLine option) =
            [ match transaction.kind, hint with
              | TransactionKind.Unknown, _    -> yield "unknown-type"
              | TransactionKind.Expense, None -> yield "mp-expense-unbooked"
              | _ -> ()
              yield! planned (Origin.Transaction transaction) ]
        [ for lineMatch in matches.lines do
              for reason in ofLine lineMatch -> { origin = Origin.Line lineMatch.line; reason = reason }
          for transaction, hint in transactions do
              for reason in ofTransaction (transaction, hint) -> { origin = Origin.Transaction transaction; reason = reason } ]

    /// The plan of the matches: the posted book lines, the payments in and the MobilePay expenses classified
    /// (K1–K7) and the fees put on the fee account (K8), then coalesced into documents (C1–C5) sorted by date,
    /// payment account and offset account, an empty offset first (P1); and the items to review (R2). A payout
    /// is already booked from the bank, and a transaction of unknown type is reviewed only (I6).
    let build (rules: Rules) (matches: Matches) : Plan =
        let transactions = transactions matches
        let items =
            [ for lineMatch in matches.lines do
                  if posted lineMatch then ofLine lineMatch.line
              for transaction, hint in transactions do
                  if transaction.kind = TransactionKind.PaymentIn || transaction.kind = TransactionKind.Expense then
                      ofTransaction (transaction, hint) ]
        let fees =
            [ for transaction, _ in transactions do
                  if transaction.kind = TransactionKind.Fee then
                      { item = ofTransaction (transaction, None); offset = Some rules.accounts.fees; flagged = false } ]
        let classified = Classification.classify rules items @ fees
        let grouped, singles =
            classified |> List.partition (fun classified -> classified.offset.IsSome && not (tillExpense classified.item))
        { documents =
            coalesced rules grouped @ List.map (single rules) singles
            |> List.sortBy (fun document ->
                document.date, document.payment.number, Option.map (fun offset -> offset.number) document.offset)
          reviews = reviews matches transactions classified }
