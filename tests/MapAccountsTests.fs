module MapAccountsTests

open Xunit
open Swensen.Unquote
open FSharp.Control
open Nocfo.Domain

let private accounts (items: (int * string) list) =
    items
    |> List.map (fun (id, number) -> Ok (EntityOpsTests.makeAccount id $"Account {number}" number))
    |> AsyncSeq.ofSeq

let private summarise (result: Result<Mapping.IDMap list * AccountFull list, DomainError>) =
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
