module ReconcileTests

open System.IO
open Xunit
open Swensen.Unquote
open FSharp.Control
open Nocfo
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

[<Fact>]
let ``A reverse-order bank statement reconciles against a daily-balance CSV`` () =
    // The nda side goes through BalanceSource.read, the same path the CLI takes, so this
    // covers the statement sort end to end without production data or network access.
    let nda =
        TitoTests.file
            [ TitoTests.headerPeriod "10963000000001" "260201" "260228" 20M
              TitoTests.dayBalance "260203" 30M
              TitoTests.headerPeriod "10963000000001" "260101" "260131" 10M
              TitoTests.dayBalance "260105" 20M ]
    let ndaPath = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".nda")
    let csvPath = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".csv")
    try
        File.WriteAllBytes(ndaPath, TitoTests.bytes nda)
        File.WriteAllText(csvPath, "date,balance\n2026-01-05,20\n2026-02-03,30\n")
        let query = { dateFrom = None; dateTo = None; accounts = Set.empty; charset = TitoCharset.Auto }
        let read text =
            BalanceSource.parse text
            |> Result.bind (fun source -> BalanceSource.read None query source |> Async.RunSynchronously)
        let result =
            match read $"nda:{ndaPath}", read $"csv:{csvPath}" with
            | Ok left, Ok right ->
                Reconcile.daily 0M (AsyncSeq.ofSeq left) (AsyncSeq.ofSeq right)
                |> AsyncSeq.toListSynchronously
                |> List.map (fun row -> row.date, row.status)
            | other -> failwithf "Expected two sources, got %A" other
        test <@ result = [ "2026-01-05", "ok"; "2026-02-03", "ok" ] @>
    finally
        File.Delete ndaPath
        File.Delete csvPath
