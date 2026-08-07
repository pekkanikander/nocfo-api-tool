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

let headerPeriod (account: string) (periodFrom: string) (periodTo: string) (openingBalance: decimal) =
    record "00" 322
        [ 9,   account
          23,  "001"
          26,  periodFrom
          32,  periodTo
          65,  "251231"
          71,  cents openingBalance
          96,  "EUR"
          292, "FI1234500000" + account.Substring(account.Length - 6) ]

let private header (account: string) (openingBalance: decimal) =
    headerPeriod account "260101" "260131" openingBalance

let private transactionAt (number: string) (receiptCode: string)
                          (bookingDate: string) (valueDate: string) (amount: decimal) (text: string) =
    record "10" 188
        [ 6,   number
          12,  "260101000000000001"
          30,  bookingDate
          36,  valueDate
          49,  "700"
          52,  text
          87,  cents amount
          106, receiptCode
          108, "Yritys Oy"
          144, "FI9876500000123"
          159, "00000000000000012345" ]

let private transaction = transactionAt "000001" ""

let dayBalance (date: string) (balance: decimal) =
    record "40" 50 [ 6, date; 12, cents balance; 31, cents balance ]

let file (records: string list) = String.Join("\r\n", records) + "\r\n"

let bytes (text: string) = Encoding.ASCII.GetBytes text

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
let ``An available balance the bank left blank decodes as None`` () =
    // The spec (§3.4.5) marks the available balance as optional.
    let text = file [ header "10963000000001" 0M; record "40" 50 [ 6, "260102"; 12, cents 11M ] ]
    let result = Tito.read TitoCharset.Auto (bytes text) |> Result.map (fun s -> s.Head.day_balances)
    test <@ result = Ok [ { date = DateOnly(2026, 1, 2); balance = 11M; available = None } ] @>

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
    test <@ result = Ok [ { date = DateOnly(2026, 1, 2); balance = 11M; available = Some 11M } ] @>

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

// ── Statement order ───────────────────────────────────────────────────────────

[<Fact>]
let ``Statements in reverse chronological order yield day balances in date order`` () =
    // An annual Nordea download is one statement per month, latest month first.
    let text =
        file [ headerPeriod "10963000000001" "260201" "260228" 20M
               dayBalance "260203" 30M
               headerPeriod "10963000000001" "260101" "260131" 10M
               dayBalance "260102" 15M
               dayBalance "260105" 20M ]
    let result =
        Tito.read TitoCharset.Auto (bytes text)
        |> Result.bind (Tito.dayBalances None)
        |> Result.map (List.map (fun b -> b.date, b.balance))
    test <@ result = Ok [ DateOnly(2026, 1, 2), 15M
                          DateOnly(2026, 1, 5), 20M
                          DateOnly(2026, 2, 3), 30M ] @>

[<Fact>]
let ``Statements in reverse chronological order yield transactions in date order`` () =
    let text =
        file [ headerPeriod "10963000000001" "260201" "260228" 20M
               transaction "260203" "260203" 10M "Helmikuu"
               headerPeriod "10963000000001" "260101" "260131" 10M
               transaction "260102" "260102" 5M "Tammikuu" ]
    let result =
        Tito.read TitoCharset.Auto (bytes text)
        |> Result.bind (Tito.transactions None)
        |> Result.map (List.map (fun t -> t.entry_text))
    test <@ result = Ok [ "Tammikuu"; "Helmikuu" ] @>

// ── Transactions and statement rows ───────────────────────────────────────────

[<Fact>]
let ``Records itemising a service-charge aggregate are dropped from transactions`` () =
    // Nordea itemises the monthly service charge in records that repeat the aggregate's
    // transaction number; only the aggregate (kuittikoodi 'E') moves the balance.
    let text =
        file [ header "10963000000001" 0M
               transactionAt "000001" "E" "260703" "260703" -12.97M "Palvelumaksu"
               transactionAt "000001" ""  "260703" "000000" -0.99M  "Palvelumaksu"
               transactionAt "000001" ""  "260703" "000000" -4.48M  "Palvelumaksu"
               transactionAt "000001" ""  "260703" "000000" -7.50M  "Palvelumaksu"
               transactionAt "000002" ""  "260703" "260703" 95M     "Viitemaksu" ]
    let result =
        Tito.read TitoCharset.Auto (bytes text)
        |> Result.bind (Tito.transactions None)
        |> Result.map (List.map (fun t -> t.amount))
    test <@ result = Ok [ -12.97M; 95M ] @>

[<Fact>]
let ``Statement rows keep only the period asked for`` () =
    let text =
        file [ header "10963000000001" 0M
               transactionAt "000001" "" "260102" "260102" 5M  "Tammikuu"
               transactionAt "000002" "" "260203" "260203" 10M "Helmikuu" ]
    let result =
        Tito.read TitoCharset.Auto (bytes text)
        |> Result.bind (Tito.transactions None)
        |> Result.map (Statement.rows (Some (DateOnly(2026, 2, 1))) None)
    test <@ result = Ok [ { booking_date = "2026-02-03"; value_date = Some "2026-02-03"
                            amount = 10M; entry_text = "Helmikuu"; counterparty = "Yritys Oy" } ] @>
