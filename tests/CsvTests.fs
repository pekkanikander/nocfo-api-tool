module CsvTests

open System.IO
open System.Text.Json
open Xunit
open Swensen.Unquote
open FSharp.Control
open Nocfo
open Nocfo.JsonHelpers

// ── Test records ──────────────────────────────────────────────────────────────

[<CLIMutable>]
type Row = { id: int; name: string; note: string option }

[<CLIMutable>]
type JsonRow = { id: int; payload: JsonElement }

[<CLIMutable>]
type JsonOptionRow = { id: int; payload: JsonElement option }

// ── Helpers ───────────────────────────────────────────────────────────────────

let private writeToCsv (fields: string list option) (rows: Row list) : string =
    use writer = new StringWriter()
    Csv.writeCsvGeneric<Row> writer fields (AsyncSeq.ofSeq rows)
    |> AsyncSeq.toListSynchronously
    |> ignore
    writer.ToString()

let private readFromCsv (fields: string list option) (csv: string) : Row list =
    use reader = new StringReader(csv)
    Csv.readCsvGeneric<Row> reader fields
    |> AsyncSeq.toListSynchronously

let private roundTrip (fields: string list option) (rows: Row list) : Row list =
    writeToCsv fields rows |> readFromCsv fields

// ── Round-trip: all fields ────────────────────────────────────────────────────

[<Fact>]
let ``Round-trip: multiple rows, all fields`` () =
    let rows = [
        { id = 1; name = "Alice"; note = Some "first" }
        { id = 2; name = "Bob";   note = None }
        { id = 3; name = "Carol"; note = Some "third" }
    ]
    test <@ roundTrip None rows = rows @>

[<Fact>]
let ``Round-trip: empty input yields empty list`` () =
    test <@ roundTrip None [] = [] @>

// ── Round-trip: field selection ───────────────────────────────────────────────

[<Fact>]
let ``Round-trip: selected fields only`` () =
    let rows = [ { id = 1; name = "Alice"; note = Some "x" } ]
    let result = roundTrip (Some ["id"; "name"]) rows
    // 'note' was not in the selection → defaults to None after read-back
    test <@ result = [ { id = 1; name = "Alice"; note = None } ] @>

// ── Option serialisation ──────────────────────────────────────────────────────

[<Fact>]
let ``Optional field None survives round-trip`` () =
    let rows = [ { id = 1; name = "X"; note = None } ]
    test <@ roundTrip None rows = rows @>

[<Fact>]
let ``Optional field Some survives round-trip`` () =
    let rows = [ { id = 1; name = "X"; note = Some "hello" } ]
    test <@ roundTrip None rows = rows @>

// ── Header validation ─────────────────────────────────────────────────────────

[<Fact>]
let ``Unknown column in CSV raises exception`` () =
    let csv = "id,name,note,bogus\n1,Alice,,\n"
    Assert.Throws<exn>(fun () ->
        readFromCsv None csv |> ignore)
    |> ignore

[<Fact>]
let ``Missing required column in field selection raises exception`` () =
    // Requesting a field that does not exist on the type
    Assert.Throws<exn>(fun () ->
        writeToCsv (Some ["id"; "nonexistent"]) [] |> ignore)
    |> ignore

// ── writeCsvGeneric output shape ──────────────────────────────────────────────

[<Fact>]
let ``Written CSV contains header row`` () =
    let csv = writeToCsv None [ { id = 1; name = "A"; note = None } ]
    let lines = csv.Split('\n') |> Array.filter (fun s -> s.Trim() <> "")
    // at least 2 lines: header + one data row
    test <@ lines.Length >= 2 @>

[<Fact>]
let ``Written CSV header contains expected column names`` () =
    let csv = writeToCsv None []
    let header = csv.Split('\n').[0].ToLowerInvariant()
    test <@ header.Contains "id" @>
    test <@ header.Contains "name" @>
    test <@ header.Contains "note" @>

// ── Raw JSON (JsonElement) round-trips ────────────────────────────────────────

let private writeJsonRowsToCsv (rows: JsonRow list) : string =
    use writer = new StringWriter()
    Csv.writeCsvGeneric<JsonRow> writer None (AsyncSeq.ofSeq rows)
    |> AsyncSeq.toListSynchronously
    |> ignore
    writer.ToString()

let private readJsonRowsFromCsv (csv: string) : JsonRow list =
    use reader = new StringReader(csv)
    Csv.readCsvGeneric<JsonRow> reader None
    |> AsyncSeq.toListSynchronously

let private writeJsonOptRowsToCsv (rows: JsonOptionRow list) : string =
    use writer = new StringWriter()
    Csv.writeCsvGeneric<JsonOptionRow> writer None (AsyncSeq.ofSeq rows)
    |> AsyncSeq.toListSynchronously
    |> ignore
    writer.ToString()

let private readJsonOptRowsFromCsv (csv: string) : JsonOptionRow list =
    use reader = new StringReader(csv)
    Csv.readCsvGeneric<JsonOptionRow> reader None
    |> AsyncSeq.toListSynchronously

[<Fact>]
let ``JsonElement object cell survives round-trip`` () =
    let obj = parseCsvJsonElement """{"x":1,"y":2}"""
    let rows : JsonRow list = [ { id = 1; payload = obj } ]
    let result = readJsonRowsFromCsv (writeJsonRowsToCsv rows)
    test <@ result.Length = 1 @>
    let text = elementToCompactString result.[0].payload
    test <@ text = """{"x":1,"y":2}""" @>

[<Fact>]
let ``JsonElement array cell survives round-trip`` () =
    let arr = parseCsvJsonElement """[1,2,3]"""
    let rows : JsonRow list = [ { id = 1; payload = arr } ]
    let result = readJsonRowsFromCsv (writeJsonRowsToCsv rows)
    test <@ result.Length = 1 @>
    let text = elementToCompactString result.[0].payload
    test <@ text = """[1,2,3]""" @>

[<Fact>]
let ``JsonElement option Some object survives round-trip`` () =
    let obj = parseCsvJsonElement """{"a":"b"}"""
    let rows : JsonOptionRow list = [ { id = 1; payload = Some obj } ]
    let result = readJsonOptRowsFromCsv (writeJsonOptRowsToCsv rows)
    test <@ result.Length = 1 @>
    let text = result.[0].payload |> Option.map elementToCompactString
    test <@ text = Some """{"a":"b"}""" @>

[<Fact>]
let ``JsonElement option None survives round-trip`` () =
    let rows : JsonOptionRow list = [ { id = 1; payload = None } ]
    let result = readJsonOptRowsFromCsv (writeJsonOptRowsToCsv rows)
    test <@ result.Length = 1 @>
    test <@ result.[0].payload = None @>

[<Fact>]
let ``parseCsvJsonElement: non-JSON cell becomes a JSON string element`` () =
    let e = parseCsvJsonElement "hello"
    let kind = e.ValueKind
    let str  = e.GetString()
    test <@ kind = JsonValueKind.String @>
    test <@ str  = "hello" @>

[<Fact>]
let ``parseCsvJsonElement: object literal parses as JSON object`` () =
    let e = parseCsvJsonElement """{"k":1}"""
    let kind = e.ValueKind
    test <@ kind = JsonValueKind.Object @>

[<Fact>]
let ``parseCsvJsonElement: array literal parses as JSON array`` () =
    let e = parseCsvJsonElement """[true]"""
    let kind = e.ValueKind
    test <@ kind = JsonValueKind.Array @>
