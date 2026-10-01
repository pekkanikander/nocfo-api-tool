namespace Nocfo.Cashbook

open System
open TomlReader

type LedgerAccount =
    { number: string
      name:   string }

/// The payment accounts and the fee account, as the file names them.
type Accounts =
    { cash:      LedgerAccount
      mobilepay: LedgerAccount
      fees:      LedgerAccount }

/// The `Maksutapa` values of each kind of MobilePay transaction (I6).
type MobilePayKinds =
    { payment_in: string list
      fee:        string list
      expense:    string list
      payout:     string list }

/// The matching window and the M8 cost weights.
type Matching =
    { days_before:          int
      days_after:           int
      max_subset:           int
      tolerance:            decimal
      weight_unmatched:     decimal
      weight_near_miss:     decimal
      weight_date_distance: decimal
      weight_subset_size:   decimal
      weight_coherence:     decimal }

/// A classification rule (K1). It hits a line when every predicate it carries holds: the text
/// contains a keyword, the text is blank, the date is within the window (a bound left out is
/// open), the amount is at most `max_amount`, and at least `min_per_day` lines of the day are
/// already on its account. `flag` is not a predicate: it sends the lines the rule classifies to
/// review.
type Rule =
    { account:     string
      name:        string
      keywords:    string list
      no_text:     bool
      from_date:   DateOnly option
      to_date:     DateOnly option
      max_amount:  decimal  option
      min_per_day: int      option
      flag:        bool }

type Classification =
    { majority_min:    int
      same_amount_min: int
      income:          Rule list
      expense:         Rule list }

type Coalesce =
    { max_days:  int
      max_gap:   int }

type Coalescing =
    { cash:      Coalesce
      mobilepay: Coalesce
      fees:      Coalesce }

type Posting =
    { marker:    string }

type Rules =
    { accounts:       Accounts
      mobilepay:      MobilePayKinds
      matching:       Matching
      classification: Classification
      coalesce:       Coalescing
      posting:        Posting }

/// What `check` found about one account reference in the file (V2).
type Finding =
    | MissingAccount of reference: string * number: string
    | NameDiffers of reference: string * number: string * file: string * nocfo: string

module Finding =

    /// A missing account is an error that `record` refuses on; a differing name is a warning.
    let isError (finding: Finding) =
        match finding with
        | MissingAccount _ -> true
        | NameDiffers    _ -> false

    let describe (finding: Finding) =
        match finding with
        | MissingAccount (reference, number) ->
            $"error: {reference}: account {number} does not exist"
        | NameDiffers    (reference, number, file, nocfo) ->
            $"warning: {reference}: account {number} is named '{nocfo}' in NoCFO, '{file}' in the file"

module Rules =

    let private ledgerAccount (t: Table) : LedgerAccount =
        { number     = t.String "number"
          name       = t.String "name" }

    let private accounts (t: Table) : Accounts =
        { cash       = t.Table("cash", ledgerAccount)
          mobilepay  = t.Table("mobilepay", ledgerAccount)
          fees       = t.Table("fees", ledgerAccount) }

    let private mobilePayKinds (t: Table) : MobilePayKinds =
        { payment_in = t.Strings "payment_in"
          fee        = t.Strings "fee"
          expense    = t.Strings "expense"
          payout     = t.Strings "payout" }

    let private matching (t: Table) : Matching =
        { days_before          = t.Int "days_before"
          days_after           = t.Int "days_after"
          max_subset           = t.Int "max_subset"
          tolerance            = t.Decimal "tolerance"
          weight_unmatched     = t.Decimal "weight_unmatched"
          weight_near_miss     = t.Decimal "weight_near_miss"
          weight_date_distance = t.Decimal "weight_date_distance"
          weight_subset_size   = t.Decimal "weight_subset_size"
          weight_coherence     = t.Decimal "weight_coherence" }

    let private rule (t: Table) : Rule =
        let rule =
            { account     = t.String "account"
              name        = t.String "name"
              keywords    = t.OptionalStrings "keywords"
              no_text     = t.OptionalBool "no_text"
              from_date   = t.OptionalDate "from"
              to_date     = t.OptionalDate "to"
              max_amount  = t.OptionalDecimal "max_amount"
              min_per_day = t.OptionalInt "min_per_day"
              flag        = t.OptionalBool "flag" }
        match rule with
        | { from_date = Some from; to_date = Some until } when from > until -> t.Fail "'from' is after 'to'"
        | { keywords = _ :: _; no_text = true } -> t.Fail "'no_text' excludes 'keywords'"
        | { keywords = []; no_text = false; from_date = None; to_date = None; max_amount = None; min_per_day = None } ->
            t.Fail "a rule needs a predicate: keywords, no_text, from/to, max_amount or min_per_day"
        | _ -> ()
        rule

    let private classification (t: Table) : Classification =
        { majority_min    = t.Int "majority_min"
          same_amount_min = t.Int "same_amount_min"
          income          = t.Tables("income", rule)
          expense         = t.Tables("expense", rule) }

    let private coalesce (t: Table) : Coalesce =
        { max_days        = t.Int "max_days"
          max_gap         = t.Int "max_gap" }

    let private coalescing (t: Table) : Coalescing =
        { cash            = t.Table("cash", coalesce)
          mobilepay       = t.Table("mobilepay", coalesce)
          fees            = t.Table("fees", coalesce) }

    let private posting (t: Table) : Posting =
        { marker          = t.String "marker" }

    let private rules (t: Table) : Rules =
        { accounts        = t.Table("accounts", accounts)
          mobilepay       = t.Table("mobilepay", mobilePayKinds)
          matching        = t.Table("matching", matching)
          classification  = t.Table("classification", classification)
          coalesce        = t.Table("coalesce", coalescing)
          posting         = t.Table("posting", posting) }

    /// Parses a `rules.toml` strictly (V1, see `TomlReader`); a classification rule must also
    /// carry a predicate.
    let parse (text: string) : Result<Rules, string list> = TomlReader.read text rules

    /// Every account the file names, checked against the chart of accounts (V2): a number the
    /// business lacks is an error, a name that differs from NoCFO's is a warning.
    let check (chart: Account list) (rules: Rules) : Finding list =
        let names = chart |> List.map (fun account -> account.number, account.name) |> Map.ofList
        let ofAccount (key: string) (account: LedgerAccount) =
            $"accounts.{key}", account.number, Some account.name
        let ofRules (key: string) (rules: Rule list) =
            rules |> List.mapi (fun index rule -> $"classification.{key} #{index + 1}", rule.account, Some rule.name)
        [ ofAccount "cash"      rules.accounts.cash
          ofAccount "mobilepay" rules.accounts.mobilepay
          ofAccount "fees"      rules.accounts.fees
          yield! ofRules "income" rules.classification.income
          yield! ofRules "expense" rules.classification.expense ]
        |> List.choose (fun (reference, number, name) ->
            match Map.tryFind number names, name with
            | None, _ -> Some (MissingAccount (reference, number))
            | Some actual, Some expected when actual <> expected -> Some (NameDiffers (reference, number, expected, actual))
            | _ -> None)
