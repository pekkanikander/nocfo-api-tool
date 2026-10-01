namespace Nocfo.Cashbook

open System
open System.Text.RegularExpressions

/// How a book line was matched (M1–M5), or why it was not (M6–M10, R1).
[<RequireQualifiedAccess>]
type Tier =
    | M1
    | M2
    | M3
    | M4
    | M5
    | Cash
    | BookOnly
    | Ambiguous

/// The outcome of matching one book line: its tier, the transactions matched to it, and the note the
/// reports carry (M5, M7, M9, M10).
type LineMatch =
    { line:         CashBookLine
      tier:         Tier
      transactions: Transaction list
      note:         string }

/// What matching made of a book and an export: every book line in file order, and the transactions no
/// line claimed.
type Matches =
    { lines:     LineMatch list
      unmatched: Transaction list }

[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module Matching =

    /// A subset of a line's candidates summing to its amount, exactly or nearly (M3–M5), with the line's
    /// share of the M8 cost when it is chosen.
    type private Subset =
        { transactions: Transaction list
          cost:         decimal }

    /// A line to match, with the transactions it may match and the subsets of them worth choosing from.
    type private MatchableLine =
        { line:       CashBookLine
          candidates: Transaction list
          subsets:    Subset list }

    /// Transaction ids as the reports list them (R1): ascending, `+`-joined.
    let ids (transactions: Transaction list) =
        transactions |> List.map (fun transaction -> transaction.id) |> List.sort |> String.concat "+"

    let private idSet (transactions: Transaction list) =
        transactions |> List.map (fun transaction -> transaction.id) |> Set.ofList

    /// The words of a text: maximal runs of letters or digits, case folded.
    let private words (text: string) =
        Regex.Matches(text, @"[\p{L}\p{Nd}]+") |> Seq.map (fun word -> word.Value.ToLowerInvariant()) |> Set.ofSeq

    /// MobilePay-marked lines and unmarked income lines are matched; cash-marked lines and unmarked
    /// expense lines never are (M7, M10).
    let private matchable (line: CashBookLine) =
        match line.method with
        | PaymentMethod.MobilePay -> true
        | PaymentMethod.Unmarked  -> line.amount > 0M
        | PaymentMethod.Cash      -> false

    /// Payments in match income lines, MobilePay expenses match expense lines (M11).
    let private kindOf (line: CashBookLine) =
        if line.amount > 0M then TransactionKind.PaymentIn else TransactionKind.Expense

    /// The transactions a line may match: of its kind and sign, dated within the window (section 3).
    let private candidates (matching: Matching) (transactions: Transaction list) (line: CashBookLine) =
        transactions
        |> List.filter (fun transaction ->
            transaction.kind = kindOf line
            && Math.Sign transaction.amount = Math.Sign line.amount
            && transaction.date >= line.date.AddDays -matching.days_before
            && transaction.date <= line.date.AddDays matching.days_after)

    /// The candidates grouped into interchangeable classes (M2): same date, same amount and same
    /// coherence, each class earliest first by time of day, then by id.
    let private classes (coherent: Transaction -> bool) (candidates: Transaction list) =
        candidates
        |> List.sortBy (fun transaction -> transaction.date, transaction.time, transaction.id)
        |> List.groupBy (fun transaction -> transaction.date, transaction.amount, coherent transaction)
        |> List.map snd

    /// The subsets of at most `size` candidates whose magnitudes sum to `remaining`, within `slack`, a
    /// class contributing its earliest transactions (M2). A line and its candidates share a sign, so a
    /// partial sum past the target is abandoned.
    let rec private combinations (size: int) (slack: decimal) (remaining: decimal) (classes: Transaction list list) =
        if remaining < -slack then []
        else
            match classes with
            | [] -> if abs remaining <= slack then [ [] ] else []
            | cls :: rest ->
                [ for count in 0 .. min size cls.Length do
                      let taken = List.truncate count cls
                      let left = remaining - (taken |> List.sumBy (fun transaction -> abs transaction.amount))
                      for tail in combinations (size - count) slack left rest -> taken @ tail ]

    /// The subsets of a line worth choosing from, cheapest first: exact sums (M3, M4) and, for a
    /// MobilePay-marked line only, near misses (M5, M9).
    let private subsets (matching: Matching) (line: CashBookLine) (candidates: Transaction list) =
        let lineWords = words line.text
        let coherent (transaction: Transaction) = not (Set.isEmpty (Set.intersect lineWords (words transaction.text)))
        let slack = if line.method = PaymentMethod.MobilePay then matching.tolerance else 0M
        let cost (transactions: Transaction list) =
            let sum = transactions |> List.sumBy (fun transaction -> transaction.amount)
            let days =
                transactions |> List.sumBy (fun transaction -> abs (transaction.date.DayNumber - line.date.DayNumber))
            (if sum = line.amount then 0M else matching.weight_near_miss)
            + matching.weight_date_distance * decimal days
            + matching.weight_subset_size * decimal transactions.Length
            + matching.weight_coherence * decimal (transactions |> List.filter coherent |> List.length)
        combinations matching.max_subset slack (abs line.amount) (classes coherent candidates)
        |> List.filter (not << List.isEmpty)
        |> List.map (fun transactions -> { transactions = transactions; cost = cost transactions })
        |> List.sortBy (fun subset -> subset.cost)

    /// Lines whose subsets share a transaction compete for it and are assigned together; the rest are
    /// independent, so each group is searched on its own (M8). Groups and their lines keep file order.
    let private groups (lines: MatchableLine list) =
        let claimed (line: MatchableLine) = line.subsets |> List.collect (fun subset -> subset.transactions) |> idSet
        let rec grow (members: MatchableLine list) (ids: Set<string>) (rest: MatchableLine list) =
            match rest |> List.partition (fun line -> not (Set.isEmpty (Set.intersect ids (claimed line)))) with
            | [], _ -> members, rest
            | linked, others -> grow (members @ linked) (ids + (linked |> List.map claimed |> Set.unionMany)) others
        let rec split (lines: MatchableLine list) =
            match lines with
            | [] -> []
            | first :: rest ->
                let members, others = grow [ first ] (claimed first) rest
                members :: split others
        split lines

    /// The cheapest choice of a subset, or none, for every line of a group, no transaction taken twice
    /// (M8): a depth-first search over the lines, a line's subsets cheapest first, bounded below by the cost
    /// so far plus every remaining line's cheapest option. The first optimum found is kept, so the result is
    /// fixed by the order of the lines and their subsets.
    let private assign (weightUnmatched: decimal) (lines: MatchableLine list) : Subset option list =
        let cheapest (line: MatchableLine) =
            match line.subsets with
            | [] -> weightUnmatched
            | subset :: _ -> min subset.cost weightUnmatched
        let bounded =
            List.mapFoldBack (fun line after -> let bound = after + cheapest line in (line, bound), bound) lines 0M
            |> fst
        let rec search incumbent cost (taken: Set<string>) chosen remaining =
            match remaining with
            | [] -> if cost < fst incumbent then cost, List.rev chosen else incumbent
            | (_, bound) :: _ when cost + bound >= fst incumbent -> incumbent
            | (line, _) :: rest ->
                let incumbent =
                    line.subsets
                    |> List.filter (fun subset -> Set.isEmpty (Set.intersect taken (idSet subset.transactions)))
                    |> List.fold (fun incumbent subset ->
                        let took = taken + idSet subset.transactions
                        search incumbent (cost + subset.cost) took (Some subset :: chosen) rest)
                        incumbent
                search incumbent (cost + weightUnmatched) taken (None :: chosen) rest
        search (Decimal.MaxValue, []) 0M Set.empty [] bounded |> snd

    /// A matched line is ambiguous (M8) when another of its subsets costs the same and takes none of the
    /// transactions the other lines took.
    let private ambiguous (line: MatchableLine) (chosen: Subset) (othersTook: Set<string>) =
        line.subsets
        |> List.exists (fun subset ->
            subset <> chosen && subset.cost = chosen.cost
            && Set.isEmpty (Set.intersect othersTook (idSet subset.transactions)))

    /// The tier of a matched line (M1–M5), read off its subset.
    let private tier (line: MatchableLine) (subset: Subset) =
        let date = line.line.date
        if subset.transactions |> List.sumBy (fun transaction -> transaction.amount) <> line.line.amount then Tier.M5
        elif subset.transactions |> List.exists (fun transaction -> transaction.date <> date) then Tier.M4
        else
            match subset.transactions with
            | [ only ] ->
                let twin (transaction: Transaction) =
                    transaction.id <> only.id && transaction.date = date && transaction.amount = only.amount
                if line.candidates |> List.exists twin then Tier.M2 else Tier.M1
            | _ -> Tier.M3

    /// What the reports say about a line: MobilePay wins a near miss (M5); a cash line may have been paid
    /// by MobilePay after all (M7); an unmarked line is assumed cash (M9) or a till expense (M10).
    let private note (unmatched: Transaction list) (line: CashBookLine) (tier: Tier) =
        match tier, line.method with
        | Tier.M5, _ -> "contradiction: MobilePay wins"
        | Tier.Cash, PaymentMethod.Unmarked -> if line.amount > 0M then "assumed-cash" else "unmarked-expense"
        | Tier.Cash, PaymentMethod.Cash ->
            let sameDayAndAmount (transaction: Transaction) =
                transaction.kind = kindOf line && transaction.date = line.date && transaction.amount = line.amount
            match unmatched |> List.filter sameDayAndAmount with
            | [] -> ""
            | possible -> "possible-mp: " + ids possible
        | _ -> ""

    /// Matches the book lines to the transactions (section 3): every line gets a tier, a matched line its
    /// transactions, and the transactions no line claimed are kept for `plan`.
    let run (rules: Rules) (lines: CashBookLine list) (transactions: Transaction list) : Matches =
        let matching = rules.matching
        let matchables =
            [ for line in lines do
                  if matchable line then
                      let candidates = candidates matching transactions line
                      { line = line; candidates = candidates; subsets = subsets matching line candidates } ]
        let chosen =
            groups matchables |> List.collect (fun group -> List.zip group (assign matching.weight_unmatched group))
        let taken = chosen |> List.choose snd |> List.collect (fun subset -> subset.transactions) |> idSet
        let outcomes =
            chosen
            |> List.map (fun (line, choice) ->
                let outcome =
                    match choice with
                    | None when line.line.method = PaymentMethod.Unmarked -> Tier.Cash, []
                    | None -> Tier.BookOnly, []
                    | Some subset when ambiguous line subset (taken - idSet subset.transactions) -> Tier.Ambiguous, []
                    | Some subset -> tier line subset, subset.transactions
                line.line.file_line, outcome)
            |> Map.ofList
        let matched = outcomes |> Map.values |> Seq.collect snd |> List.ofSeq |> idSet
        let unmatched = transactions |> List.filter (fun transaction -> not (Set.contains transaction.id matched))
        let lineMatch (line: CashBookLine) =
            let tier, transactions = outcomes |> Map.tryFind line.file_line |> Option.defaultValue (Tier.Cash, [])
            { line = line; tier = tier; transactions = transactions; note = note unmatched line tier }
        { lines = List.map lineMatch lines; unmatched = unmatched }
