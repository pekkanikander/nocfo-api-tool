namespace Nocfo.Cashbook

open System
open System.Collections.Generic
open Tomlyn
open Tomlyn.Model

/// Strict reading of a TOML document into records: every table and key must be known, present
/// and of its type. Errors accumulate rather than stop the read, so every error in the document
/// is reported, in document order.
module TomlReader =

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

    let private asBool (raw: obj) =
        match raw with
        | :? bool as value -> Some value
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

    /// One table of the document. Every key read is remembered, so the keys left over are the
    /// unknown ones. A key that fails yields a placeholder, which goes nowhere once there is an
    /// error.
    type Table private (errors: ResizeArray<string>, path: string, toml: TomlTable) =
        let read = HashSet<string>()

        let fail (message: string) =
            errors.Add(if path = "" then message else $"[{path}] {message}")

        let child (key: string) =
            if path = "" then key else $"{path}.{key}"

        static member internal Read(errors, path, toml, read: Table -> 'T) : 'T =
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

        member this.String(key)          = this.Required(key, "a string",   asString, "")
        member this.Int(key)             = this.Required(key, "an integer", asInt, 0)
        member this.Decimal(key)         = this.Required(key, "a number",   asDecimal, 0M)
        member this.Strings(key)         = this.Required(key, "a list of strings", asStrings, [])
        member this.OptionalBool(key)    = this.Optional(key, "a boolean",  asBool) |> Option.defaultValue false
        member this.OptionalInt(key)     = this.Optional(key, "an integer", asInt)
        member this.OptionalDecimal(key) = this.Optional(key, "a number",   asDecimal)
        member this.OptionalStrings(key) = this.Optional(key, "a list of strings", asStrings) |> Option.defaultValue []
        member this.OptionalDate(key)    = this.Optional(key, "a date (YYYY-MM-DD, unquoted)", asDate)

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

    /// Reads the document's root table with `root`; a syntax error is reported with its position.
    let read (text: string) (root: Table -> 'T) : Result<'T, string list> =
        let document = Toml.Parse text
        if document.HasErrors then
            Error [ for diagnostic in document.Diagnostics -> string diagnostic ]
        else
            let errors = ResizeArray<string>()
            let value = Table.Read(errors, "", Toml.ToModel document, root)
            if errors.Count = 0 then Ok value else Error (List.ofSeq errors)
