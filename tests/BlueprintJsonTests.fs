module BlueprintJsonTests

open System.Text.Json
open Xunit
open Swensen.Unquote
open Nocfo.Domain
open Nocfo.JsonHelpers
open Nocfo.Tools.BlueprintJson

// ── Helpers ───────────────────────────────────────────────────────────────────

let private row (blueprintJson: string option) : DocumentCreatePayload =
    { date        = None
      description = None
      is_draft    = None
      ``type``    = None
      blueprint   =
        blueprintJson
        |> Option.map (fun s -> JsonDocument.Parse(s).RootElement.Clone()) }

let private blueprintOf (result: Result<DocumentCreatePayload, DomainError>) : JsonElement option =
    match result with
    | Ok r  -> r.blueprint
    | Error e -> failwith $"Expected Ok, got Error %A{e}"

let private errorOf (result: Result<DocumentCreatePayload, DomainError>) : string =
    match result with
    | Error (DomainError.Unexpected msg) -> msg
    | other -> failwith $"Expected DomainError.Unexpected, got %A{other}"

let private idMap pairs = pairs |> List.map (fun (k, v) -> (k, v)) |> Map.ofList

// ── No blueprint ──────────────────────────────────────────────────────────────

[<Fact>]
let ``None blueprint is unchanged`` () =
    let r = row None
    let result = remapBlueprint true Map.empty r
    test <@ result = Ok r @>

// ── JSON null blueprint ───────────────────────────────────────────────────────

[<Fact>]
let ``JSON null blueprint is a no-op`` () =
    let r = row (Some "null")
    let result = remapBlueprint true Map.empty r
    // null ValueKind means no-op; blueprint stays as-is
    match result with
    | Ok row ->
        let kind = row.blueprint.Value.ValueKind
        test <@ kind = JsonValueKind.Null @>
    | Error e -> Assert.Fail $"%A{e}"

// ── Full remap success ────────────────────────────────────────────────────────

[<Fact>]
let ``Full remap: top-level account IDs and entry arrays remapped`` () =
    let blueprint = """
      { "debet_account_id": 1,
        "credit_account_id": 2,
        "debet_entries":   [{"account_id": 3, "amount": "10.00"}],
        "credit_entries":  [{"account_id": 4, "amount": "10.00"}],
        "expense_entries": [] }"""

    let m = idMap [1, 101; 2, 202; 3, 303; 4, 404]
    let result = remapBlueprint true m (row (Some blueprint))

    let bp = blueprintOf result
    let obj = bp.Value
    let debetAccId  = obj.GetProperty("debet_account_id").GetInt32()
    let creditAccId = obj.GetProperty("credit_account_id").GetInt32()
    let debetEntry  = obj.GetProperty("debet_entries").[0].GetProperty("account_id").GetInt32()
    let creditEntry = obj.GetProperty("credit_entries").[0].GetProperty("account_id").GetInt32()
    test <@ debetAccId  = 101 @>
    test <@ creditAccId = 202 @>
    test <@ debetEntry  = 303 @>
    test <@ creditEntry = 404 @>

// ── Null account IDs are no-ops ───────────────────────────────────────────────

[<Fact>]
let ``Null account_id fields are left unchanged`` () =
    let blueprint = """{"debet_account_id":null,"debet_entries":[{"account_id":null}],"credit_account_id":null,"credit_entries":[],"expense_entries":[]}"""
    let result = remapBlueprint true (idMap []) (row (Some blueprint))
    let bp = (blueprintOf result).Value
    let kind = bp.GetProperty("debet_account_id").ValueKind
    test <@ kind = JsonValueKind.Null @>

// ── Missing mapping in strict mode ───────────────────────────────────────────

[<Fact>]
let ``Missing mapping in strict mode returns error`` () =
    let blueprint = """{"debet_account_id":99,"debet_entries":[],"credit_account_id":null,"credit_entries":[],"expense_entries":[]}"""
    let result = remapBlueprint true Map.empty (row (Some blueprint))
    let msg = errorOf result
    test <@ msg.Contains "99" @>

// ── Missing mapping in non-strict mode ───────────────────────────────────────

[<Fact>]
let ``Missing mapping in non-strict mode substitutes empty blueprint`` () =
    let blueprint = """{"debet_account_id":99,"debet_entries":[],"credit_account_id":null,"credit_entries":[],"expense_entries":[]}"""
    let result = remapBlueprint false Map.empty (row (Some blueprint))
    let bp = (blueprintOf result).Value
    // empty blueprint has these five keys and no account IDs set
    let kind         = bp.ValueKind
    let debetLen     = bp.GetProperty("debet_entries").GetArrayLength()
    let creditLen    = bp.GetProperty("credit_entries").GetArrayLength()
    let expenseLen   = bp.GetProperty("expense_entries").GetArrayLength()
    test <@ kind       = JsonValueKind.Object @>
    test <@ debetLen   = 0 @>
    test <@ creditLen  = 0 @>
    test <@ expenseLen = 0 @>

// ── Invalid top-level shape ───────────────────────────────────────────────────

[<Fact>]
let ``Non-object blueprint in strict mode returns error`` () =
    let result = remapBlueprint true Map.empty (row (Some "[1,2,3]"))
    let msg = errorOf result
    test <@ msg.Contains "Expected blueprint JSON object" @>

[<Fact>]
let ``Non-object blueprint in non-strict mode substitutes empty blueprint`` () =
    let result = remapBlueprint false Map.empty (row (Some "[1,2,3]"))
    let bp = (blueprintOf result).Value
    let kind  = bp.ValueKind
    let dLen  = bp.GetProperty("debet_entries").GetArrayLength()
    test <@ kind = JsonValueKind.Object @>
    test <@ dLen = 0 @>
