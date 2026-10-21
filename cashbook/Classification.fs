namespace Nocfo.Cashbook

open System

/// Where an item to post comes from: the cash book, or the MobilePay export.
[<RequireQualifiedAccess>]
type Origin =
    | Line of CashBookLine
    | Transaction of Transaction

/// An item to post, with the text its keywords are sought in: a book line's `mita` (K2), or a transaction's
/// `Viesti` and, when it is matched, the `mita` of its book line (K3); and the reason tags matching gave it
/// (M8–M11, P7). The amount is signed.
type Item =
    { origin: Origin
      date:   DateOnly
      amount: decimal
      text:   string
      tags:   string list }

/// An item with the offset account it goes to, or none (K7), and whether the rule that chose it flagged it (K1).
type Classified =
    { item:    Item
      offset:  LedgerAccount option
      flagged: bool }

[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module Classification =

    /// Whether every predicate of the rule holds for the item (K1), `min_per_day` counted over the items of the
    /// same day already on the rule's account.
    let private hits (rule: Rule) (onAccount: Classified list) (item: Item) =
        let window : Period = { from_date = rule.from_date; to_date = rule.to_date }
        let contains (keyword: string) = item.text.Contains(keyword, StringComparison.OrdinalIgnoreCase)
        let sameDay = onAccount |> List.filter (fun other -> other.item.date = item.date) |> List.length
        (rule.keywords = [] || List.exists contains rule.keywords)
        && (not rule.no_text || String.IsNullOrWhiteSpace item.text)
        && Period.contains window item.date
        && Option.forall (fun most -> abs item.amount <= most) rule.max_amount
        && Option.forall (fun least -> sameDay >= least) rule.min_per_day

    /// The rules applied one by one in file order to the eligible items, each rule taking the still-unclassified
    /// items it hits (K1).
    let private byRules (eligible: Item -> bool) (rules: Rule list) (classified: Classified list) =
        let apply (classified: Classified list) (rule: Rule) =
            let onAccount =
                classified |> List.filter (fun other -> other.offset |> Option.exists (fun offset -> offset.number = rule.account))
            classified
            |> List.map (fun candidate ->
                if candidate.offset.IsNone && eligible candidate.item && hits rule onAccount candidate.item then
                    { candidate with offset = Some { number = rule.account; name = rule.name }; flagged = rule.flag }
                else candidate)
        List.fold apply classified rules

    /// The account a strict majority of the classified ones are on, with their number.
    let private majority (classified: Classified list) =
        let offsets = classified |> List.choose (fun other -> other.offset)
        match offsets |> List.countBy id |> List.sortByDescending snd with
        | (offset, count) :: _ when count * 2 > offsets.Length -> Some (offset, count)
        | _ -> None

    /// Day-majority (K4): the account at least `majority_min` of the other transactions of the day are on, when
    /// it holds a strict majority of the day's classified ones.
    let private dayMajority (classification: Classification) (others: Classified list) (item: Item) =
        match majority (others |> List.filter (fun other -> other.item.date = item.date)) with
        | Some (offset, count) when count >= classification.majority_min -> Some offset
        | _ -> None

    /// Same-amount (K5): when at least `same_amount_min` of the other transactions of the same or an adjacent
    /// day have the item's amount, the account a strict majority of the classified ones among them are on.
    let private sameAmount (classification: Classification) (others: Classified list) (item: Item) =
        let twins =
            others
            |> List.filter (fun other ->
                other.item.amount = item.amount && abs (other.item.date.DayNumber - item.date.DayNumber) <= 1)
        if twins.Length >= classification.same_amount_min then majority twins |> Option.map fst else None

    /// A transaction the rules left unclassified takes the day's majority account (K4), failing that the
    /// same-amount majority (K5), both read off the rules' result alone: neither feeds the other. The other
    /// transactions are those of the item's sign, so that an income item never takes an expense account.
    let private byNeighbours (classification: Classification) (classified: Classified list) =
        let transactions =
            classified
            |> List.filter (fun other ->
                match other.item.origin with
                | Origin.Transaction _ -> true
                | Origin.Line _ -> false)
        classified
        |> List.map (fun candidate ->
            match candidate.item.origin, candidate.offset with
            | Origin.Transaction _, None ->
                let others =
                    transactions
                    |> List.filter (fun other ->
                        other.item <> candidate.item && Math.Sign other.item.amount = Math.Sign candidate.item.amount)
                let offset =
                    dayMajority classification others candidate.item
                    |> Option.orElseWith (fun () -> sameAmount classification others candidate.item)
                { candidate with offset = offset }
            | _ -> candidate)

    /// The offset account of every item: an income item takes the first income rule that hits, an expense item
    /// the first expense rule (K1), a transaction neither hits its neighbours' account (K4, K5), and what is
    /// left stays unclassified (K7). The result keeps the items' order.
    let classify (rules: Rules) (items: Item list) : Classified list =
        items
        |> List.map (fun item -> { item = item; offset = None; flagged = false })
        |> byRules (fun item -> item.amount > 0M) rules.classification.income
        |> byRules (fun item -> item.amount < 0M) rules.classification.expense
        |> byNeighbours rules.classification
