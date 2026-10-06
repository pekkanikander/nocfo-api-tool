module CashBookTests

open System
open Xunit
open Swensen.Unquote
open Nocfo.Cashbook

let private header = "sivu;pvm;mita;tulo;meno;tapa;kuitti;huom\n"

let private lines (rows: string) =
    match CashBook.read (header + rows) with
    | Ok lines -> lines
    | Error errors -> failwithf "%A" errors

let private errors (text: string) =
    match CashBook.read text with
    | Error errors -> errors
    | Ok _ -> failwith "Expected errors"

[<Fact>]
let ``A line is read with its file line, the amount signed and the decimal comma`` () =
    test <@ lines "1;2026-07-18;Tanssit;44,70;;MP;2;later\n"
              = [ { file_line = 2; page = "1"; date = DateOnly(2026, 7, 18); text = "Tanssit"; amount = 44.70M
                    method = PaymentMethod.MobilePay; tapa = "MP"; receipts = 2; remark = "later" } ] @>
    test <@ (lines "2;2026-09-08;Alkufest;;13,00;KÄT;;\n").Head.amount = -13.00M @>

[<Fact>]
let ``A byte order mark is accepted`` () =
    let result = CashBook.read ("\uFEFF" + header + "1;2026-07-18;Tanssit;10,00;;;;\n")
    test <@ Result.map List.length result = Ok 1 @>

[<Fact>]
let ``tapa is normalised whatever its case and spacing, and kept as written`` () =
    let marked (tapa: string) = $"1;2026-07-18;x;1,00;;{tapa};;\n"
    let read = lines (marked "MP?" + marked " kt " + marked "kä" + marked "")
    test <@ read |> List.map (fun line -> line.method, line.tapa)
              = [ PaymentMethod.MobilePay, "MP?"; PaymentMethod.Cash, " kt "
                  PaymentMethod.Cash, "kä"; PaymentMethod.Unmarked, "" ] @>

[<Fact>]
let ``An unknown tapa is an error naming the line and the value`` () =
    test <@ errors (header + "1;2026-07-18;x;1,00;;MP;;\n1;2026-07-18;x;1,00;;lasku;;\n")
              = [ "[L3] 'tapa' must be MP, MP?, KÄT, KT, KÄ or empty, not 'lasku'" ] @>

[<Fact>]
let ``A line with both or neither of tulo and meno is rejected`` () =
    test <@ errors (header + "1;2026-07-18;x;1,00;2,00;;;\n1;2026-07-18;x;;;;;\n")
              = [ "[L2] exactly one of 'tulo' and 'meno' must be given"
                  "[L3] exactly one of 'tulo' and 'meno' must be given" ] @>

[<Fact>]
let ``A malformed date, amount or receipt count is located`` () =
    test <@ errors (header + "1;18.7.2026;x;1,00;;;;\n1;2026-07-18;x;1.00;;;;\n1;2026-07-18;x;1,00;;;two;\n")
              = [ "[L2] 'pvm' must be a date (YYYY-MM-DD), not '18.7.2026'"
                  "[L3] 'tulo' must be an amount (decimal comma), not '1.00'"
                  "[L4] 'kuitti' must be a count or empty, not 'two'" ] @>

[<Fact>]
let ``The header must be the book's, in its order`` () =
    let swapped = "pvm;sivu;mita;tulo;meno;tapa;kuitti;huom"
    let expected = [ $"[L1] the header must be 'sivu;pvm;mita;tulo;meno;tapa;kuitti;huom', not '{swapped}'" ]
    test <@ errors (swapped + "\n") = expected @>

[<Fact>]
let ``A line with the wrong number of cells is located`` () =
    test <@ errors (header + "1;2026-07-18;x;1,00;;\n") = [ "[L2] expected 8 cells, found 6" ] @>

[<Fact>]
let ``Read leniently, a malformed line is left out and located, the others kept (P9)`` () =
    let result = CashBook.readRows (header + "1;2026-07-18;Tanssit;10,00;;;;\n1;2026-07-18;x;1,00;;lasku;;\n1;2026-07-19;Kahvila;2,00;;;;\n")
    test <@ Result.map (fun (lines, errors) -> List.map (fun line -> line.text) lines, errors) result
              = Ok ([ "Tanssit"; "Kahvila" ], [ "[L3] 'tapa' must be MP, MP?, KÄT, KT, KÄ or empty, not 'lasku'" ]) @>

[<Fact>]
let ``Read leniently, a wrong header still fails the file`` () =
    test <@ CashBook.readRows "pvm;sivu;mita;tulo;meno;tapa;kuitti;huom\n"
              = Error "[L1] the header must be 'sivu;pvm;mita;tulo;meno;tapa;kuitti;huom', not 'pvm;sivu;mita;tulo;meno;tapa;kuitti;huom'" @>
