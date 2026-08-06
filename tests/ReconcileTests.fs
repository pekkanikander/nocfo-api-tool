module ReconcileTests

open Xunit
open Swensen.Unquote
open FSharp.Control
open NocfoClient
open Nocfo.Domain

let private days (balances: (string * decimal) list) =
    balances |> List.map (fun (date, balance) -> { date = date; balance = balance }) |> AsyncSeq.ofSeq

let private reconcile tolerance left right =
    Reconcile.daily tolerance (days left) (days right) |> AsyncSeq.toListSynchronously

let private statuses tolerance left right =
    reconcile tolerance left right |> List.map (fun row -> row.date, row.status)

[<Fact>]
let ``Identical balances reconcile`` () =
    let result = statuses 0M [ "2025-01-01", 10M; "2025-01-02", 20M ] [ "2025-01-01", 10M; "2025-01-02", 20M ]
    test <@ result = [ "2025-01-01", "ok"; "2025-01-02", "ok" ] @>

[<Fact>]
let ``A difference on a shared day is reported`` () =
    let result = reconcile 0M [ "2025-01-01", 10M ] [ "2025-01-01", 12M ]
    test <@ result = [ { date = "2025-01-01"; left_balance = 10M; right_balance = 12M
                         difference = -2M; status = "differs" } ] @>

[<Fact>]
let ``A day only the left side has is left-only, never a difference`` () =
    let result = statuses 0M [ "2025-01-01", 10M; "2025-01-02", 15M ] [ "2025-01-01", 10M ]
    test <@ result = [ "2025-01-01", "ok"; "2025-01-02", "left-only" ] @>

[<Fact>]
let ``A day only the right side has is right-only, never a difference`` () =
    let result = statuses 0M [ "2025-01-01", 10M ] [ "2025-01-01", 10M; "2025-01-02", 11M ]
    test <@ result = [ "2025-01-01", "ok"; "2025-01-02", "right-only" ] @>

[<Fact>]
let ``A difference within the tolerance reconciles`` () =
    let result = statuses 0.01M [ "2025-01-01", 10M ] [ "2025-01-01", 10.01M ]
    test <@ result = [ "2025-01-01", "ok" ] @>

[<Fact>]
let ``Balances out of date order are rejected`` () =
    raises<StreamOrderException> <@ reconcile 0M [ "2025-01-02", 10M; "2025-01-01", 10M ] [] @>
