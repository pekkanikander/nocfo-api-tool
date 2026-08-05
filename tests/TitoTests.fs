module TitoTests

open System
open System.Text
open Xunit
open Swensen.Unquote
open Nocfo
open Nocfo.Domain

// ── Synthetic fixtures ────────────────────────────────────────────────────────
// The real statements this reader was written against carry names, IBANs and payment
// references of actual people, so every fixture here is built from scratch.

let private put (buffer: char[]) (offset: int) (text: string) =
    text |> Seq.iteri (fun i c -> buffer.[offset + i] <- c)

let private record (recordType: string) (length: int) (fields: (int * string) list) =
    let buffer = Array.create length ' '
    put buffer 0 (sprintf "T%s%03d" recordType length)
    fields |> List.iter (fun (offset, text) -> put buffer offset text)
    String(buffer)

/// Sign plus 18 digits, in cents.
let private cents (amount: decimal) =
    let value = int64 (Math.Round(amount * 100M))
    sprintf "%c%018d" (if value < 0L then '-' else '+') (abs value)

let private header (account: string) (openingBalance: decimal) =
    record "00" 322
        [ 9,   account
          23,  "001"
          26,  "260101"
          32,  "260131"
          65,  "251231"
          71,  cents openingBalance
          96,  "EUR"
          292, "FI1234500000" + account.Substring(account.Length - 6) ]

let private transaction (bookingDate: string) (valueDate: string) (amount: decimal) (text: string) =
    record "10" 188
        [ 6,   "000001"
          12,  "260101000000000001"
          30,  bookingDate
          36,  valueDate
          49,  "700"
          52,  text
          87,  cents amount
          108, "Yritys Oy"
          144, "FI9876500000123"
          159, "00000000000000012345" ]

let private dayBalance (date: string) (balance: decimal) =
    record "40" 50 [ 6, date; 12, cents balance; 31, cents balance ]

let private file (records: string list) = String.Join("\r\n", records) + "\r\n"

let private bytes (text: string) = Encoding.ASCII.GetBytes text

// ── Character set ─────────────────────────────────────────────────────────────

[<Fact>]
let ``ISO 646-FI puts Finnish letters where ASCII has brackets`` () =
    let result = Tito.decode TitoCharset.AsciiFi (bytes "K[YT[NN[ISS[ M[[R[T {|}")
    test <@ result = "KÄYTÄNNÄISSÄ MÄÄRÄT äöå" @>

[<Fact>]
let ``Auto reads a 7-bit file as ISO 646-FI`` () =
    let result = Tito.decode TitoCharset.Auto (bytes "M[[R[")
    test <@ result = "MÄÄRÄ" @>

[<Fact>]
let ``Auto reads a file with 8-bit characters as Latin-1`` () =
    let result = Tito.decode TitoCharset.Auto [| 0x4Duy; 0xC4uy; 0xC4uy; 0x52uy; 0xC4uy |]
    test <@ result = "MÄÄRÄ" @>

// ── Record framing ────────────────────────────────────────────────────────────

[<Fact>]
let ``A statement decodes into its header, transactions and day balances`` () =
    let text =
        file [ header "10963000000001" 1000M
               transaction "260102" "260102" 250M "Palkka"
               transaction "260103" "260103" -75.5M "Vuokra"
               dayBalance "260102" 1250M
               dayBalance "260103" 1174.5M ]
    let result = Tito.read TitoCharset.Auto (bytes text)
    match result with
    | Ok [ statement ] ->
        test <@ statement.header.account = "10963000000001" @>
        test <@ statement.header.opening_balance = 1000M @>
        test <@ statement.header.period_from = DateOnly(2026, 1, 1) @>
        test <@ statement.transactions |> List.map (fun t -> t.amount) = [ 250M; -75.5M ] @>
        test <@ statement.transactions |> List.map (fun t -> t.entry_text) = [ "Palkka"; "Vuokra" ] @>
        test <@ statement.day_balances |> List.map (fun b -> b.balance) = [ 1250M; 1174.5M ] @>
    | other -> failwithf "Expected one statement, got %A" other

[<Fact>]
let ``A file without line breaks frames records by their declared length`` () =
    let text = String.Join("", [ header "10963000000001" 0M; dayBalance "260102" 12M ])
    let result = Tito.read TitoCharset.Auto (bytes text)
    match result with
    | Ok [ statement ] -> test <@ statement.day_balances |> List.map (fun b -> b.balance) = [ 12M ] @>
    | other -> failwithf "Expected one statement, got %A" other

[<Fact>]
let ``A record type this reader does not decode is skipped`` () =
    let text = file [ header "10963000000001" 0M; record "50" 30 []; dayBalance "260102" 12M ]
    let result = Tito.read TitoCharset.Auto (bytes text)
    match result with
    | Ok [ statement ] -> test <@ statement.day_balances.Length = 1 @>
    | other -> failwithf "Expected one statement, got %A" other

[<Fact>]
let ``A T11 detail attaches to the transaction it follows`` () =
    let text =
        file [ header "10963000000001" 0M
               transaction "260102" "260102" 10M "Palkka"
               record "11" 40 [ 6, "00"; 8, "Lisatieto" ] ]
    let result = Tito.read TitoCharset.Auto (bytes text)
    match result with
    | Ok [ statement ] -> test <@ statement.transactions.Head.details = [ "Lisatieto" ] @>
    | other -> failwithf "Expected one statement, got %A" other

[<Fact>]
let ``A value date the bank left unset decodes as None`` () =
    let text = file [ header "10963000000001" 0M; transaction "260102" "000000" 10M "Palkka" ]
    let result = Tito.read TitoCharset.Auto (bytes text)
    match result with
    | Ok [ statement ] ->
        test <@ statement.transactions.Head.value_date = None @>
        test <@ statement.transactions.Head.booking_date = DateOnly(2026, 1, 2) @>
    | other -> failwithf "Expected one statement, got %A" other

// ── Malformed input ───────────────────────────────────────────────────────────

[<Fact>]
let ``A record that does not declare a length is rejected`` () =
    let result = Tito.read TitoCharset.Auto (bytes "T00abcxxxx")
    test <@ match result with Error (DomainError.BadData _) -> true | _ -> false @>

[<Fact>]
let ``A record whose declared length cannot hold its own header is rejected`` () =
    let result = Tito.read TitoCharset.Auto (bytes "T00001")
    test <@ match result with Error (DomainError.BadData _) -> true | _ -> false @>

[<Fact>]
let ``A record shorter than it declares is rejected`` () =
    let result = Tito.read TitoCharset.Auto (bytes (file [ record "40" 50 [ 6, "260102" ] |> fun r -> r.Substring(0, 20) ]))
    test <@ match result with Error (DomainError.BadData _) -> true | _ -> false @>

[<Fact>]
let ``A transaction before any header is rejected`` () =
    let text = file [ transaction "260102" "260102" 10M "Palkka" ]
    let result = Tito.read TitoCharset.Auto (bytes text)
    test <@ match result with Error (DomainError.BadData _) -> true | _ -> false @>

[<Fact>]
let ``An unparseable amount is rejected`` () =
    let text = file [ header "10963000000001" 0M; record "40" 50 [ 6, "260102"; 12, "+00000000000000000X" ] ]
    let result = Tito.read TitoCharset.Auto (bytes text)
    test <@ match result with Error (DomainError.BadData _) -> true | _ -> false @>

// ── Selecting a bank account ──────────────────────────────────────────────────

let private twoAccounts =
    file [ header "10963000000001" 0M
           dayBalance "260102" 11M
           header "10963000000002" 0M
           dayBalance "260102" 22M ]

[<Fact>]
let ``A file with one account needs no selector`` () =
    let statements = Tito.read TitoCharset.Auto (bytes (file [ header "10963000000001" 0M; dayBalance "260102" 11M ]))
    let result = statements |> Result.bind (Tito.dayBalances None)
    test <@ result = Ok [ { date = DateOnly(2026, 1, 2); balance = 11M; available = 11M } ] @>

[<Fact>]
let ``A file with several accounts requires a selector`` () =
    let result = Tito.read TitoCharset.Auto (bytes twoAccounts) |> Result.bind (Tito.dayBalances None)
    test <@ match result with Error (DomainError.Invalid _) -> true | _ -> false @>

[<Fact>]
let ``The selector matches the tail of the account number`` () =
    let result = Tito.read TitoCharset.Auto (bytes twoAccounts) |> Result.bind (Tito.dayBalances (Some "000002"))
    test <@ result |> Result.map (List.map (fun b -> b.balance)) = Ok [ 22M ] @>

[<Fact>]
let ``A selector matching no account is rejected`` () =
    let result = Tito.read TitoCharset.Auto (bytes twoAccounts) |> Result.bind (Tito.dayBalances (Some "999999"))
    test <@ match result with Error (DomainError.Invalid _) -> true | _ -> false @>
