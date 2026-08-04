module ExitCodeTests

open System
open System.IO
open Argu
open Xunit
open Swensen.Unquote
open Nocfo.Domain
open Nocfo.Tools.Arguments
open NocfoClient

let private exitCodeFor (ex: exn) = Program.exitCodeForException ex

let private parseFailure (argv: string[]) =
    let parser = ArgumentParser.Create<CliArgs>(programName = "nocfo")
    try
        parser.ParseCommandLine(argv, raiseOnUsage = true) |> ignore
        None
    with :? ArguParseException as ex -> Some (exitCodeFor ex)

[<Fact>]
let ``Help request exits successfully`` () =
    let code = parseFailure [| "--help" |]
    test <@ code = Some 0 @>

[<Fact>]
let ``Missing subcommand is a usage error`` () =
    let code = parseFailure [||]
    test <@ code = Some 64 @>

[<Fact>]
let ``Unrecognised argument is a usage error`` () =
    let code = parseFailure [| "frobnicate" |]
    test <@ code = Some 64 @>

[<Fact>]
let ``Unauthorized maps to EX_NOPERM`` () =
    let code = exitCodeFor (DomainStreamException (Http (Http.HttpError.Unauthorized (Uri "https://example.test/"))))
    test <@ code = 77 @>

[<Fact>]
let ``Transport failure maps to EX_UNAVAILABLE`` () =
    let code = exitCodeFor (DomainStreamException (Http (Http.HttpError.Transport (Uri "https://example.test/", "refused"))))
    test <@ code = 69 @>

[<Fact>]
let ``Unknown business maps to EX_NOINPUT`` () =
    let code = exitCodeFor (DomainStreamException (Unexpected "No matching business: acme"))
    test <@ code = 66 @>

[<Fact>]
let ``Bad CSV header maps to EX_DATAERR`` () =
    let code = exitCodeFor (Nocfo.CsvFormatException "Unknown column(s) for X: y")
    test <@ code = 65 @>

[<Fact>]
let ``Missing input file maps to EX_NOINPUT`` () =
    let code = exitCodeFor (FileNotFoundException "no such file")
    test <@ code = 66 @>

[<Fact>]
let ``Configuration failure maps to EX_CONFIG`` () =
    let code = exitCodeFor (Exception "Tool configuration failed: missing token")
    test <@ code = 78 @>

[<Fact>]
let ``Anything else maps to EX_SOFTWARE`` () =
    let code = exitCodeFor (InvalidOperationException "boom")
    test <@ code = 70 @>
