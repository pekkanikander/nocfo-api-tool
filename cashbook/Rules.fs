namespace Nocfo.Cashbook

open System
open System.Collections.Generic
open Tomlyn
open Tomlyn.Model

type RoleAccount =
    { number: string
      name: string }

type Roles =
    { cash: RoleAccount
      mobilepay: RoleAccount
      fees: RoleAccount }

/// `Maksutapa` values by meaning (I6).
type MobilePay =
    { payment_in: string list
      fee: string list
      expense: string list
      payout: string list }

/// The matching window and the M8 cost weights.
type Matching =
    { days_before: int
      days_after: int
      max_subset: int
      tolerance: decimal
      weight_unmatched: decimal
      weight_near_miss: decimal
      weight_date_distance: decimal
      weight_subset_size: decimal
      weight_coherence: decimal }

type Cafe =
    { account: string
      max_amount: decimal
      min_transactions_per_day: int }

/// A classification rule (K1). It hits a line dated within its window whose text contains a
/// keyword; a bound left out is open, and a windowed rule without keywords hits every line in
/// its window.
type Rule =
    { account: string
      name: string
      keywords: string list
      from: DateOnly option
      ``to``: DateOnly option }

type Classification =
    { majority_min: int
      same_amount_min: int
      cafe: Cafe
      income: Rule list
      expense: Rule list }

type Coalesce =
    { max_days: int
      max_gap: int }

type Coalescing =
    { cash: Coalesce
      mobilepay: Coalesce
      fees: Coalesce }

type Posting =
    { marker: string }

type Rules =
    { accounts: Roles
      mobilepay: MobilePay
      matching: Matching
      classification: Classification
      coalesce: Coalescing
      posting: Posting }

module Rules =

    let private asString (raw: obj) =
        match raw with
        | :? string as text -> Some text
        | _ -> None

    let private asInt (raw: obj) =
        match raw with
        | :? int64 as value -> Some (int value)
        | _ -> None

    let private asDecimal (raw: obj) =
        match raw with
        | :? int64 as value -> Some (decimal value)
        | :? double as value -> Some (decimal value)
        | _ -> None

    let private asDate (raw: obj) =
        match raw with
        | :? TomlDateTime as value when value.Kind = TomlDateTimeKind.LocalDate ->
            Some (DateOnly.FromDateTime value.DateTime.DateTime)
        | _ -> None

    let private asStrings (raw: obj) =
        match raw with
        | :? TomlArray as items when items |> Seq.forall (fun item -> item :? string) ->
            Some (items |> Seq.cast<string> |> List.ofSeq)
        | _ -> None

    let private asTable (raw: obj) =
        match raw with
        | :? TomlTable as table -> Some table
        | _ -> None

    let private asTables (raw: obj) =
        match raw with
        | :? TomlTableArray as tables -> Some (List.ofSeq tables)
        | _ -> None

    /// One table of the file, read strictly. Every key read is remembered, so the keys left over
    /// are the unknown ones. Errors accumulate rather than stop the read: a key that fails yields
    /// a placeholder, which goes nowhere once there is an error.
    type private Table(errors: ResizeArray<string>, path: string, toml: TomlTable) =
        let read = HashSet<string>()

        let fail (message: string) =
            errors.Add(if path = "" then message else $"[{path}] {message}")

        let child (key: string) =
            if path = "" then key else $"{path}.{key}"

        static member Read(errors, path, toml, read: Table -> 'T) : 'T =
            let table = Table(errors, path, toml)
            let value = read table
            table.RejectUnread()
            value

        member _.Fail(message: string) = fail message

        member private _.RejectUnread() =
            for KeyValue (key, value) in toml do
                if not (read.Contains key) then
                    let kind =
                        match value with
                        | :? TomlTable | :? TomlTableArray -> "table"
                        | _ -> "key"
                    fail $"unknown {kind} '{key}'"

        member private _.Optional(key: string, kind: string, convert: obj -> 'T option) : 'T option =
            read.Add key |> ignore
            match toml.TryGetValue key with
            | true, raw ->
                let value = convert raw
                if value.IsNone then fail $"'{key}' must be {kind}"
                value
            | _ -> None

        member private this.Required(key: string, kind: string, convert: obj -> 'T option, fallback: 'T) : 'T =
            match this.Optional(key, kind, convert) with
            | Some value -> value
            | None ->
                if not (toml.ContainsKey key) then fail $"missing key '{key}'"
                fallback

        member this.String(key) = this.Required(key, "a string", asString, "")
        member this.Int(key) = this.Required(key, "an integer", asInt, 0)
        member this.Decimal(key) = this.Required(key, "a number", asDecimal, 0M)
        member this.Strings(key) = this.Required(key, "a list of strings", asStrings, [])
        member this.OptionalStrings(key) = this.Optional(key, "a list of strings", asStrings) |> Option.defaultValue []
        member this.OptionalDate(key) = this.Optional(key, "a date (YYYY-MM-DD, unquoted)", asDate)

        member this.Table(key: string, read: Table -> 'T) : 'T =
            match this.Optional(key, "a table", asTable) with
            | Some table -> Table.Read(errors, child key, table, read)
            | None ->
                if not (toml.ContainsKey key) then fail $"missing table '{key}'"
                // The table is absent or not a table; whatever its keys would say is moot.
                read (Table(ResizeArray(), "", TomlTable()))

        member this.Tables(key: string, read: Table -> 'T) : 'T list =
            match this.Optional(key, "a list of tables", asTables) with
            | Some tables ->
                tables |> List.mapi (fun index table -> Table.Read(errors, $"{child key} #{index + 1}", table, read))
            | None -> []

    let private role (t: Table) : RoleAccount =
        { number = t.String "number"
          name = t.String "name" }

    let private roles (t: Table) : Roles =
        { cash = t.Table("cash", role)
          mobilepay = t.Table("mobilepay", role)
          fees = t.Table("fees", role) }

    let private mobilePay (t: Table) : MobilePay =
        { payment_in = t.Strings "payment_in"
          fee = t.Strings "fee"
          expense = t.Strings "expense"
          payout = t.Strings "payout" }

    let private matching (t: Table) : Matching =
        { days_before = t.Int "days_before"
          days_after = t.Int "days_after"
          max_subset = t.Int "max_subset"
          tolerance = t.Decimal "tolerance"
          weight_unmatched = t.Decimal "weight_unmatched"
          weight_near_miss = t.Decimal "weight_near_miss"
          weight_date_distance = t.Decimal "weight_date_distance"
          weight_subset_size = t.Decimal "weight_subset_size"
          weight_coherence = t.Decimal "weight_coherence" }

    let private cafe (t: Table) : Cafe =
        { account = t.String "account"
          max_amount = t.Decimal "max_amount"
          min_transactions_per_day = t.Int "min_transactions_per_day" }

    let private rule (t: Table) : Rule =
        let rule =
            { account = t.String "account"
              name = t.String "name"
              keywords = t.OptionalStrings "keywords"
              from = t.OptionalDate "from"
              ``to`` = t.OptionalDate "to" }
        match rule.from, rule.``to`` with
        | Some from, Some until when from > until -> t.Fail "'from' is after 'to'"
        | None, None when rule.keywords.IsEmpty -> t.Fail "a rule needs keywords or a date window (from/to)"
        | _ -> ()
        rule

    let private classification (t: Table) : Classification =
        { majority_min = t.Int "majority_min"
          same_amount_min = t.Int "same_amount_min"
          cafe = t.Table("cafe", cafe)
          income = t.Tables("income", rule)
          expense = t.Tables("expense", rule) }

    let private coalesce (t: Table) : Coalesce =
        { max_days = t.Int "max_days"
          max_gap = t.Int "max_gap" }

    let private coalescing (t: Table) : Coalescing =
        { cash = t.Table("cash", coalesce)
          mobilepay = t.Table("mobilepay", coalesce)
          fees = t.Table("fees", coalesce) }

    let private posting (t: Table) : Posting =
        { marker = t.String "marker" }

    let private rules (t: Table) : Rules =
        { accounts = t.Table("accounts", roles)
          mobilepay = t.Table("mobilepay", mobilePay)
          matching = t.Table("matching", matching)
          classification = t.Table("classification", classification)
          coalesce = t.Table("coalesce", coalescing)
          posting = t.Table("posting", posting) }

    /// Parses a `rules.toml` strictly (V1): every table and key must be known, present and of
    /// its type, and a classification rule must have keywords or a date window. Every error in
    /// the file is reported, in file order.
    let parse (text: string) : Result<Rules, string list> =
        let document = Toml.Parse text
        if document.HasErrors then
            Error [ for diagnostic in document.Diagnostics -> string diagnostic ]
        else
            let errors = ResizeArray<string>()
            let rules = Table.Read(errors, "", Toml.ToModel document, rules)
            if errors.Count = 0 then Ok rules else Error (List.ofSeq errors)
