namespace Nocfo.Cashbook

open System.Collections.Generic
open System.Globalization
open System.IO
open CsvHelper
open CsvHelper.Configuration

/// Strict reading of a delimited file into records, one per row: the header must be exactly the one
/// expected, and a cell that does not convert is an error located by its file line. Errors accumulate
/// rather than stop the read, so every error in the file is reported, in file order.
module CsvReader =

    /// One row of the file. A cell that fails yields a placeholder, which goes nowhere once there is an
    /// error.
    type Row internal (errors: ResizeArray<string>, line: int, cells: string[], columns: IDictionary<string, int>) =

        let fail (message: string) = errors.Add $"[L{line}] {message}"

        /// The file line of the row; the header is line 1.
        member _.Line = line

        member _.Fail(message: string) = fail message

        member _.String(column: string) : string = cells.[columns.[column]]

        /// The cell converted by `convert`, which yields None for a value that is not `kind`.
        member this.Parse(column: string, kind: string, convert: string -> 'T option, fallback: 'T) : 'T =
            let value = this.String column
            match convert value with
            | Some converted -> converted
            | None ->
                fail $"'{column}' must be {kind}, not '{value}'"
                fallback

    /// Reads every row of the text with `row`. A byte order mark is accepted; the header must be
    /// `header` exactly, and every row must have as many cells as it.
    let read (delimiter: string) (header: string list) (text: string) (row: Row -> 'T) : Result<'T list, string list> =
        let configuration = CsvConfiguration(CultureInfo.InvariantCulture, Delimiter = delimiter)
        use parser = new CsvParser(new StringReader(text.TrimStart '\uFEFF'), configuration)
        let found = if parser.Read() then List.ofArray parser.Record else []
        if found <> header then
            let expected, actual = String.concat delimiter header, String.concat delimiter found
            Error [ $"[L1] the header must be '{expected}', not '{actual}'" ]
        else
            let errors = ResizeArray<string>()
            let columns = header |> List.mapi (fun index column -> column, index) |> dict
            let rows =
                [ while parser.Read() do
                      if parser.Record.Length = header.Length then
                          yield row (Row(errors, parser.RawRow, parser.Record, columns))
                      else
                          let cells = parser.Record.Length
                          errors.Add $"[L{parser.RawRow}] expected {header.Length} cells, found {cells}" ]
            if errors.Count = 0 then Ok rows else Error (List.ofSeq errors)
