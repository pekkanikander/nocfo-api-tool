namespace Nocfo.Cashbook

open System
open System.Globalization

/// A document to record (P1): the items of one payment account, offset account and tag set over a run of
/// days, dated the last of them (C1–C3), or a single item (C4, K7, I6). The amount is the change of the payment
/// account, the rows are the items in the document's order (P12), the tags the reason tags of its rows (P7).
type Document =
    { date:        DateOnly
      payment:     LedgerAccount
      offset:      LedgerAccount
      amount:      decimal
      rows:        Origin list
      tags:        string list
      description: string }

module Plan =

    /// An item with the account it posts against and every reason tag it carries (P7), sorted.
    type private Posting =
        { item:   Item
          offset: LedgerAccount
          tags:   string list }

    let private day (date: DateOnly) = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)

    /// A book line is posted, as cash, when it was paid from the till: cash-marked, or unmarked and matched to
    /// nothing (M9, M10). A MobilePay-marked line is a hint only, matched or not (M6), and so is an unmarked
    /// line that matched, even ambiguously (M8, M9).
    let private posted (lineMatch: LineMatch) =
        lineMatch.tier = Tier.Cash && lineMatch.line.method <> PaymentMethod.MobilePay

    /// Every transaction with the book line it is matched to, if any, by date, time and id. An ambiguous line's
    /// transactions are contested, not matched: they are among the unmatched and post without a hint (M8, M9).
    let private transactions (matches: Matches) =
        [ for lineMatch in matches.lines do
              if lineMatch.tier <> Tier.Ambiguous then
                  for transaction in lineMatch.transactions -> transaction, Some lineMatch.line
          for transaction in matches.unmatched -> transaction, None ]
        |> List.sortBy (fun (transaction, _) -> transaction.date, transaction.time, transaction.id)

    /// The ids of the transactions the ambiguous lines contest (M8).
    let private contested (matches: Matches) =
        [ for lineMatch in matches.lines do
              if lineMatch.tier = Tier.Ambiguous then
                  for transaction in lineMatch.transactions -> transaction.id ]
        |> Set.ofList

    /// A posted book line, tagged by what matching assumed of it when it is unmarked (M9, M10).
    let private ofLine (lineMatch: LineMatch) : Item =
        let line = lineMatch.line
        { origin = Origin.Line line
          date   = line.date
          amount = line.amount
          text   = line.text
          tags   = match lineMatch.note with
                   | Some (Note.AssumedCash | Note.UnmarkedExpense as note) -> [ Note.reason note ]
                   | _ -> [] }

    /// A transaction, the `mita` of the line it is matched to joined to its text (K3), tagged when its type is
    /// unknown (I6), when it is an expense the book lacks (M11) and when it is contested (M8).
    let private ofTransaction (contested: Set<string>) (transaction: Transaction, hint: CashBookLine option) : Item =
        { origin = Origin.Transaction transaction
          date   = transaction.date
          amount = transaction.amount
          text   = match hint with
                   | Some line -> $"{transaction.text} {line.text}"
                   | None -> transaction.text
          tags   = [ if transaction.kind = TransactionKind.Unknown then "unknown-type"
                     if transaction.kind = TransactionKind.Expense && hint.IsNone then "mp-expense-unbooked"
                     if Set.contains transaction.id contested then "ambiguous" ] }

    /// A classified item posts against the account classification chose, or the clearing account (K7); it
    /// carries matching's tags, `flagged` when the rule that classified it flags (K1) and `unclassified` when
    /// no account was found (K7).
    let private posting (rules: Rules) (classified: Classified) : Posting =
        { item   = classified.item
          offset = classified.offset |> Option.defaultValue rules.accounts.unclassified
          tags   =
            [ yield! classified.item.tags
              if classified.flagged then "flagged"
              if classified.offset.IsNone then "unclassified" ]
            |> List.sort }

    /// A transaction posted without classification: a fee on the fee account (K8), one of unknown type on the
    /// clearing account (I6).
    let private direct (contested: Set<string>) (offset: LedgerAccount) (transaction: Transaction) : Posting =
        let item = ofTransaction contested (transaction, None)
        { item = item; offset = offset; tags = item.tags }

    let private payment (rules: Rules) (item: Item) =
        match item.origin with
        | Origin.Line _        -> rules.accounts.cash
        | Origin.Transaction _ -> rules.accounts.mobilepay

    /// An expense paid from the till, which is never coalesced (C4).
    let private tillExpense (item: Item) =
        match item.origin with
        | Origin.Line line     -> line.amount < 0M
        | Origin.Transaction _ -> false

    /// A posting that is a document on its own: a till expense (C4) or an item on the clearing account (K7, I6).
    let private alone (rules: Rules) (posting: Posting) =
        tillExpense posting.item || posting.offset = rules.accounts.unclassified

    /// The name of an offset account, `?` for the clearing account (K7).
    let private offsetName (rules: Rules) (offset: LedgerAccount) =
        if offset = rules.accounts.unclassified then "?" else offset.name

    /// `<marker> <payment>/<offset> <date>[–<last>] (<n>)` (C5).
    let private summary (rules: Rules) (payment: LedgerAccount) (offset: LedgerAccount) (postings: Posting list) =
        let dates = postings |> List.map (fun posting -> posting.item.date)
        let first, last = List.min dates, List.max dates
        let span = if first = last then day first else $"{day first}–{day last}"
        $"{rules.posting.marker} {payment.name}/{offsetName rules offset} {span} ({postings.Length})"

    /// `<marker> <cash>/<offset> <date> <mita> (<k> kuittia)` for an expense paid from the till (C4, C5).
    let private tillExpenseSummary (rules: Rules) (offset: LedgerAccount) (line: CashBookLine) =
        $"{rules.posting.marker} {rules.accounts.cash.name}/{offsetName rules offset} {day line.date} {line.text} ({line.receipts} kuittia)"

    /// The document of the postings of one payment account, offset account and tag set (C1), a till expense
    /// described by its line (C4).
    let private document (rules: Rules) (payment: LedgerAccount, offset: LedgerAccount, tags: string list) (postings: Posting list) : Document =
        { date        = postings |> List.map (fun posting -> posting.item.date) |> List.max
          payment     = payment
          offset      = offset
          amount      = postings |> List.sumBy (fun posting -> posting.item.amount)
          rows        = postings |> List.map (fun posting -> posting.item.origin)
          tags        = tags
          description =
            match postings with
            | [ { item = { origin = Origin.Line line } } ] when line.amount < 0M -> tillExpenseSummary rules offset line
            | _ -> summary rules payment offset postings }

    /// What a posting is grouped by (C1): its payment account, offset account and tag set.
    let private key (rules: Rules) (posting: Posting) = payment rules posting.item, posting.offset, posting.tags

    /// The coalescing block of a payment and offset account: the till's, the fees' or MobilePay's (C2, C3).
    let private block (rules: Rules) (payment: LedgerAccount) (offset: LedgerAccount) =
        if payment = rules.accounts.cash then rules.coalesce.cash
        elif offset = rules.accounts.fees then rules.coalesce.fees
        else rules.coalesce.mobilepay

    /// The day groups of one key merged greedily into runs, in date order: a day joins the run when its gap to
    /// the run's last day is at most `max_gap` and the run then holds at most `max_days` days (C2).
    let private runs (coalesce: Coalesce) (days: (DateOnly * Posting list) list) =
        let join (runs: (DateOnly * Posting list) list list) (date: DateOnly, postings: Posting list) =
            match runs with
            | ((last, _) :: _ as run) :: rest
                when date.DayNumber - last.DayNumber <= coalesce.max_gap && run.Length < coalesce.max_days ->
                ((date, postings) :: run) :: rest
            | _ -> [ date, postings ] :: runs
        days |> List.sortBy fst |> List.fold join [] |> List.rev |> List.map (List.rev >> List.collect snd)

    /// The postings grouped by date, payment account, offset account and tag set (C1), the day groups of each
    /// key coalesced into runs under its block (C2, C3), one document per run.
    let private coalesced (rules: Rules) (postings: Posting list) =
        postings
        |> List.groupBy (key rules)
        |> List.collect (fun ((payment, offset, tags), postings) ->
            postings
            |> List.groupBy (fun posting -> posting.item.date)
            |> runs (block rules payment offset)
            |> List.map (document rules (payment, offset, tags)))

    /// The documents of the matches: the posted book lines, the payments in and the MobilePay expenses
    /// classified (K1–K7), the fees put on the fee account (K8) and the transactions of unknown type on the
    /// clearing account (I6), then coalesced (C1–C5) and sorted by date, payment account and offset account
    /// (P1). A payout is already booked from the bank.
    let build (rules: Rules) (matches: Matches) : Document list =
        let transactions = transactions matches
        let contested = contested matches
        let items =
            [ for lineMatch in matches.lines do
                  if posted lineMatch then ofLine lineMatch
              for transaction, hint in transactions do
                  if transaction.kind = TransactionKind.PaymentIn || transaction.kind = TransactionKind.Expense then
                      ofTransaction contested (transaction, hint) ]
        let byKind =
            [ for transaction, _ in transactions do
                  match transaction.kind with
                  | TransactionKind.Fee     -> direct contested rules.accounts.fees transaction
                  | TransactionKind.Unknown -> direct contested rules.accounts.unclassified transaction
                  | TransactionKind.PaymentIn | TransactionKind.Expense | TransactionKind.Payout -> () ]
        let singles, grouped =
            Classification.classify rules items |> List.map (posting rules) |> List.append byKind |> List.partition (alone rules)
        coalesced rules grouped @ List.map (fun posting -> document rules (key rules posting) [ posting ]) singles
        |> List.sortBy (fun document -> document.date, document.payment.number, document.offset.number)
