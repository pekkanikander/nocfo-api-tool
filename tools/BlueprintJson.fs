module Nocfo.Tools.BlueprintJson

open System.Text.Json
open System.Text.Json.Nodes
open Nocfo.Domain
open Nocfo.JsonHelpers

let private emptyBlueprintJson =
    """{"debet_account_id":null,"debet_entries":[],"credit_account_id":null,"credit_entries":[],"expense_entries":[]}"""

let private emptyBlueprint : JsonElement =
    JsonDocument.Parse(emptyBlueprintJson).RootElement.Clone()

let private tryGetInt (node: JsonNode) : int option =
    match node with
    | :? JsonValue as jv ->
        let mutable v = 0
        if jv.TryGetValue<int>(&v) then Some v else None
    | _ -> None

let private remapObjectAccountId (accountIdMap: Map<int, int>) (key: string) (container: JsonObject) : Result<unit, DomainError> =
    if not (container.ContainsKey(key)) then Ok ()
    else
        let value = container[key]
        if isNull value then Ok ()  // JSON null → no-op
        else
            match tryGetInt value with
            | Some sourceId ->
                match Map.tryFind sourceId accountIdMap with
                | Some targetId ->
                    container[key] <- JsonValue.Create(targetId)
                    Ok ()
                | None ->
                    Error (DomainError.Unexpected $"Missing account-id mapping for {key}={sourceId}.")
            | None ->
                Error (DomainError.Unexpected $"Expected integer for {key}, got {value.GetValueKind()}.")

let private remapEntryArrayAccountIds (accountIdMap: Map<int, int>) (key: string) (container: JsonObject) : Result<unit, DomainError> =
    if not (container.ContainsKey(key)) then Ok ()
    else
        match container[key] with
        | null ->
            Error (DomainError.Unexpected $"Expected array for {key}, got Null.")
        | :? JsonArray as entries ->
            entries
            |> Seq.fold (fun state entry ->
                match state with
                | Error _ -> state
                | Ok () ->
                    match entry with
                    | :? JsonObject as entryObj ->
                        if not (entryObj.ContainsKey("account_id")) then Ok ()
                        else
                            let value = entryObj["account_id"]
                            if isNull value then Ok ()  // JSON null → no-op
                            else
                                match tryGetInt value with
                                | Some sourceId ->
                                    match Map.tryFind sourceId accountIdMap with
                                    | Some targetId ->
                                        entryObj["account_id"] <- JsonValue.Create(targetId)
                                        Ok ()
                                    | None ->
                                        Error (DomainError.Unexpected $"Missing account-id mapping for {key}.account_id={sourceId}.")
                                | None ->
                                    Error (DomainError.Unexpected $"Expected integer for {key}.account_id, got {value.GetValueKind()}.")
                    | _ ->
                        Error (DomainError.Unexpected $"Expected object entries under {key}."))
                (Ok ())
        | value ->
            Error (DomainError.Unexpected $"Expected array for {key}, got {value.GetValueKind()}.")

/// Remap account IDs inside a document blueprint from source to target using the supplied map.
/// JSON null and missing fields are no-ops. Integer account IDs are remapped.
/// Non-integer values or missing mappings are errors in strict mode; non-strict mode logs a warning
/// and substitutes the predefined empty blueprint.
let remapBlueprint (strict: bool) (accountIdMap: Map<int, int>) (row: DocumentCreatePayload) : Result<DocumentCreatePayload, DomainError> =
    match row.blueprint with
    | None -> Ok row
    | Some blueprintElement ->
        if blueprintElement.ValueKind = JsonValueKind.Null then Ok row
        else
            match elementToNode blueprintElement with
            | :? JsonObject as blueprintObj ->
                let mutable remapError : DomainError option = None
                let apply result =
                    match remapError, result with
                    | None, Error err -> remapError <- Some err
                    | _ -> ()

                apply (remapObjectAccountId    accountIdMap "debet_account_id"  blueprintObj)
                apply (remapObjectAccountId    accountIdMap "credit_account_id" blueprintObj)
                apply (remapEntryArrayAccountIds accountIdMap "debet_entries"   blueprintObj)
                apply (remapEntryArrayAccountIds accountIdMap "credit_entries"  blueprintObj)
                apply (remapEntryArrayAccountIds accountIdMap "expense_entries" blueprintObj)

                match remapError with
                | None ->
                    Ok { row with blueprint = Some (nodeToElement blueprintObj) }
                | Some err ->
                    if strict then
                        Error err
                    else
                        eprintfn "Warning: %A; replacing blueprint with empty blueprint." err
                        Ok { row with blueprint = Some (cloneElement emptyBlueprint) }
            | _ ->
                if strict then
                    Error (DomainError.Unexpected $"Expected blueprint JSON object, got {blueprintElement.ValueKind}.")
                else
                    eprintfn "Warning: invalid blueprint shape (%A); replacing blueprint with empty blueprint." blueprintElement.ValueKind
                    Ok { row with blueprint = Some (cloneElement emptyBlueprint) }
