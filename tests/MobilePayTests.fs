module MobilePayTests

open System
open Xunit
open Swensen.Unquote
open Nocfo.Cashbook

let private kinds : MobilePayKinds =
    { payment_in = [ "Maksa Lippaaseen" ]; fee = [ "Palvelumaksu" ]; expense = [ "Osto" ]; payout = [] }

let private header = "Päivämäärä,Nimi,Tyyppi,Numero,Viesti,Summa,Valuutta,Maksutapa,Tapahtumatunnus\n"

/// A payment row as the Box writes it, the columns no test varies fixed.
let private row (viesti: string) (summa: string) (valuutta: string) (maksutapa: string) (id: string) =
    $"18/07/2026 19:14,A A,Henkilö,,{viesti},{summa},{valuutta},{maksutapa},{id}\n"

let private transactions (rows: string) =
    match MobilePay.read kinds (header + rows) with
    | Ok transactions -> transactions
    | Error errors -> failwithf "%A" errors

[<Fact>]
let ``A transaction is read with its date and time split, the amount signed and the kind by Maksutapa`` () =
    test <@ transactions "18/07/2026 19:12,Palvelumaksu,,,,-2,EUR,Palvelumaksu,f-1001\n"
              = [ { id = "f-1001"; date = DateOnly(2026, 7, 18); time = TimeOnly(19, 12); name = "Palvelumaksu"; text = ""
                    amount = -2M; kind = TransactionKind.Fee } ] @>

[<Fact>]
let ``A quoted Viesti may contain a comma`` () =
    let viesti = "'La-su vuokra osissa, loput käteisellä =125€"
    let read = transactions (row ("\"" + viesti + "\"") "38" "EUR" "Maksa Lippaaseen" "3006")
    test <@ read.Head.text = viesti @>

[<Fact>]
let ``A byte order mark is accepted`` () =
    let result = MobilePay.read kinds ("\uFEFF" + header + row "Tanssit" "10" "EUR" "Maksa Lippaaseen" "1001")
    test <@ Result.map List.length result = Ok 1 @>

[<Fact>]
let ``A non-EUR row is an error (I7)`` () =
    test <@ MobilePay.read kinds (header + row "Tanssit" "10" "SEK" "Maksa Lippaaseen" "1001")
              = Error [ "[L2] 'Valuutta' must be EUR, not 'SEK'" ] @>

[<Fact>]
let ``A repeated transaction id is kept once, the first occurrence winning (I5)`` () =
    let twice = row "first" "10" "EUR" "Maksa Lippaaseen" "1001" + row "again" "10" "EUR" "Maksa Lippaaseen" "1001"
    test <@ transactions twice |> List.map (fun transaction -> transaction.text) = [ "first" ] @>

[<Fact>]
let ``A Maksutapa in no list gives the unknown kind (I6)`` () =
    test <@ (transactions (row "" "10" "EUR" "Tilitys" "1001")).Head.kind = TransactionKind.Unknown @>

[<Fact>]
let ``A malformed date or amount is located`` () =
    let badDate = "2026-07-18 19:14,A A,Henkilö,,,10,EUR,Maksa Lippaaseen,1001\n"
    let badAmount = row "" "\"10,00\"" "EUR" "Maksa Lippaaseen" "1002"
    test <@ MobilePay.read kinds (header + badDate + badAmount)
              = Error [ "[L2] 'Päivämäärä' must be a date and time (dd/MM/yyyy HH:mm), not '2026-07-18 19:14'"
                        "[L3] 'Summa' must be an amount (decimal point), not '10,00'" ] @>
