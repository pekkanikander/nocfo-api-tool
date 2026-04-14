namespace Nocfo.Tools

open NocfoClient
open Nocfo.Domain
open System
open System.IO


/// Resolved configuration shared by all CLI commands.
type ToolConfig =
    {
        BaseUrl: Uri
        Token: string
        SourceToken: string option
        SourceBaseUrl: Uri
    }

/// Shared runtime context derived from configuration.
type ToolContext =
    {
        Config: ToolConfig
        Accounting: AccountingContext
        Input: TextReader
        Output: TextWriter
    }

/// Errors that can occur while materialising `ToolConfig`.
type ToolConfigError =
    | MissingEnvironmentVariable of string
    | InvalidUri of envVar: string * value: string
    | ProfileLoadError of profileName: string * message: string

module Runtime =

    [<Literal>]
    let private DefaultBaseUrl = "https://api-tst.nocfo.io"
    [<Literal>]
    let private DefaultSourceBaseUrl = "https://api-prd.nocfo.io"
    [<Literal>]
    let private TokenVar = "NOCFO_TOKEN"
    [<Literal>]
    let private TargetTokenVar = "NOCFO_TARGET_TOKEN"
    [<Literal>]
    let private SourceTokenVar = "NOCFO_SOURCE_TOKEN"
    [<Literal>]
    let private BaseUrlVar = "NOCFO_BASE_URL"
    [<Literal>]
    let private TargetBaseUrlVar = "NOCFO_TARGET_BASE_URL"
    [<Literal>]
    let private SourceBaseUrlVar = "NOCFO_SOURCE_BASE_URL"

    let private tryEnv name =
        match Environment.GetEnvironmentVariable(name) with
        | null  -> None
        | ""    -> None
        | value -> Some value

    module ToolConfig =

        let describeError =
            function
            | MissingEnvironmentVariable name ->
                $"Missing required environment variable {name}."
            | InvalidUri (name, value) ->
                $"Environment variable {name} must be an absolute URI (value: {value})."
            | ProfileLoadError (name, message) ->
                $"Cannot load profile '{name}': {message}"

        let createContext (cfg: ToolConfig) (input: TextReader) (output: TextWriter) (dryRun: bool) : ToolContext =
            let httpContext = Http.createHttpContext cfg.BaseUrl cfg.Token
            let accounting = Accounting.ofHttp httpContext dryRun
            { Config = cfg; Accounting = accounting; Input = input; Output = output }

        let fromSources (profile: string option) =
            let profileResult =
                match profile with
                | None ->
                    Ok ({ Token = None; BaseUrl = None; SourceToken = None; SourceBaseUrl = None } : ProfileSettings)
                | Some name ->
                    match Config.loadProfile name with
                    | Ok settings -> Ok settings
                    | Error msg -> Error [ ProfileLoadError (name, msg) ]

            match profileResult with
            | Error errs -> Error errs
            | Ok p ->

            let parseUri label value =
                match Uri.TryCreate(value, UriKind.Absolute) with
                | true, uri -> Ok uri
                | false, _ -> Error (InvalidUri (label, value))

            let tryUri envVar =
                tryEnv envVar |> Option.map (parseUri envVar)

            let targetBaseUrlResult =
                match tryUri TargetBaseUrlVar with
                | Some result -> result
                | None ->
                    match tryUri BaseUrlVar with
                    | Some result -> result
                    | None ->
                        match p.BaseUrl with
                        | Some v -> parseUri "profile: base_url" v
                        | None -> Ok (Uri DefaultBaseUrl)

            let sourceBaseUrlResult =
                match tryUri SourceBaseUrlVar with
                | Some result -> result
                | None ->
                    match p.SourceBaseUrl with
                    | Some v -> parseUri "profile: source_base_url" v
                    | None -> Ok (Uri DefaultSourceBaseUrl)

            let targetTokenResult =
                match tryEnv TargetTokenVar with
                | Some token -> Ok token
                | None ->
                    match tryEnv TokenVar with
                    | Some token -> Ok token
                    | None ->
                        match p.Token with
                        | Some token -> Ok token
                        | None -> Error (MissingEnvironmentVariable $"{TargetTokenVar} (fallback: {TokenVar})")

            let sourceToken =
                match tryEnv SourceTokenVar with
                | Some t -> Some t
                | None -> p.SourceToken

            match targetBaseUrlResult, targetTokenResult, sourceBaseUrlResult with
            | Ok baseUrl, Ok token, Ok sourceBaseUrl ->
                Ok { BaseUrl = baseUrl; Token = token; SourceToken = sourceToken; SourceBaseUrl = sourceBaseUrl }
            | _ ->
                let errors =
                    [
                        match targetBaseUrlResult with Error e -> yield e | _ -> ()
                        match targetTokenResult with Error e -> yield e | _ -> ()
                        match sourceBaseUrlResult with Error e -> yield e | _ -> ()
                    ]
                Error errors

        let loadOrFail (profile: string option) (input: TextReader) (output: TextWriter) (dryRun: bool): ToolContext =
            match fromSources profile with
            | Ok cfg -> createContext cfg input output dryRun
            | Error errors ->
                let errorMessages = errors |> List.map describeError |> String.concat "\n"
                failwith $"Tool configuration failed: {errorMessages}"
