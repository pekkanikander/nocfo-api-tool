module MapAccountsTests

open System
open Xunit
open Swensen.Unquote
open FSharp.Control
open Nocfo.Domain

let private makeRow (id: int) (number: string) : AccountRow =
    NocfoApi.Types.AccountList.Create(
        id = id,
        created_at = DateTimeOffset.UtcNow,
        updated_at = DateTimeOffset.UtcNow,
        number = number,
        padded_number = int (number.PadRight(7, '0')),
        name = $"Account {number}",
        name_translations = [],
        header_path = [],
        default_vat_rate = 0.0,
        is_shown = true
    )

let private accounts (items: (int * string) list) =
    items
    |> List.map (fun (id, number) -> Ok (makeRow id number))
    |> AsyncSeq.ofSeq

let private summarise (result: Result<Mapping.IDMap list * AccountRow list, DomainError>) =
    result
    |> Result.map (fun (rows, missing) ->
        rows |> List.map (fun row -> row.source_id, row.target_id, row.number),
        missing |> List.map (fun account -> account.number))

let private pair source target =
    Program.mapAccountRows (accounts source) (accounts target)
    |> Async.RunSynchronously
    |> summarise

[<Fact>]
let ``Accounts of unequal digit counts pair up whatever order the API lists them in`` () =
    // Numeric order lists 999 before 1000, lexicographic order the other way round.
    let result = pair [ 1, "999"; 2, "1000" ] [ 11, "1000"; 12, "999" ]
    test <@ result = Ok ([ 1, 12, "999"; 2, 11, "1000" ], []) @>

[<Fact>]
let ``Source accounts with no target counterpart are reported, not dropped`` () =
    let result = pair [ 1, "999"; 2, "1000" ] [ 11, "1000" ]
    test <@ result = Ok ([ 2, 11, "1000" ], [ "999" ]) @>

[<Fact>]
let ``Target-only accounts are ignored`` () =
    let result = pair [ 1, "1000" ] [ 11, "1000"; 12, "2000" ]
    test <@ result = Ok ([ 1, 11, "1000" ], []) @>

[<Fact>]
let ``An error in either stream propagates`` () =
    let failing = AsyncSeq.ofSeq [ Error (DomainError.Unexpected "boom") ]
    let fromTarget =
        Program.mapAccountRows (accounts [ 1, "1000" ]) failing
        |> Async.RunSynchronously
        |> summarise
    let fromSource =
        Program.mapAccountRows failing (accounts [ 11, "1000" ])
        |> Async.RunSynchronously
        |> summarise
    test <@ fromTarget = Error (DomainError.Unexpected "boom") @>
    test <@ fromSource = Error (DomainError.Unexpected "boom") @>

// ── Hydration avoidance (D6) ──────────────────────────────────────────────────

let private listCsv (fields: string list) (rows: (int * string) list) =
    let mutable hydrations = 0
    let hydrate (row: AccountRow) =
        hydrations <- hydrations + 1
        async.Return (Ok (EntityOpsTests.makeAccount row.id row.name row.number))
    use writer = new IO.StringWriter()
    accounts rows
    |> Program.writeEntitiesCsv<AccountFull, AccountRow> writer fields hydrate
    |> AsyncSeq.iter ignore
    |> Async.RunSynchronously
    hydrations, writer.ToString().Replace("\r\n", "\n")

[<Fact>]
let ``Fields the list response already carries cost no per-row request`` () =
    let hydrations, csv = listCsv [ "id"; "number"; "name" ] [ 1, "1000"; 2, "2000" ]
    test <@ hydrations = 0 @>
    test <@ csv = "id,number,name\n1,1000,Account 1000\n2,2000,Account 2000\n" @>

[<Fact>]
let ``A field only the full form carries still hydrates`` () =
    let hydrations, _ = listCsv [ "id"; "balance" ] [ 1, "1000"; 2, "2000" ]
    test <@ hydrations = 2 @>

[<Fact>]
let ``An unrestricted field selection hydrates, since it means every field of the full form`` () =
    let hydrations, _ = listCsv [] [ 1, "1000" ]
    test <@ hydrations = 1 @>
