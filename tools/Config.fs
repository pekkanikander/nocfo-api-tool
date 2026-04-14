namespace Nocfo.Tools

open System
open System.IO
open Tomlyn
open Tomlyn.Model

/// Values a named profile can supply as defaults.
/// All fields are optional; absent keys fall through to env vars.
type ProfileSettings = {
    Token:         string option
    BaseUrl:       string option
    SourceToken:   string option
    SourceBaseUrl: string option
}

module Config =

    // Environment variable that overrides the base config directory.
    // If unset, defaults to ~/.config/nocfo/
    [<Literal>]
    let ConfigHomeVar = "NOCFO_TOOL_CONFIG_HOME"

    // TOML key names
    [<Literal>]
    let private KeyProfiles      = "profiles"
    [<Literal>]
    let private KeyToken         = "token"
    [<Literal>]
    let private KeyBaseUrl       = "base_url"
    [<Literal>]
    let private KeySourceToken   = "source_token"
    [<Literal>]
    let private KeySourceBaseUrl = "source_base_url"

    let private configFilePath () =
        let base' =
            match Environment.GetEnvironmentVariable ConfigHomeVar with
            | null | "" ->
                Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    ".config",
                    "nocfo")
            | v -> v
        Path.Combine(base', "config.toml")

    let private tryString (table: TomlTable) (key: string) : string option =
        match table.TryGetValue key with
        | true, (:? string as v) -> Some v
        | _ -> None

    /// Load a named profile from the config file.
    /// Returns Error if the file does not exist, is malformed, or the profile is absent.
    let loadProfile (profileName: string) : Result<ProfileSettings, string> =
        let path = configFilePath ()
        if not (File.Exists path) then
            Error $"Config file not found: {path}"
        else
            try
                let doc = Toml.ToModel(File.ReadAllText path)
                match doc.TryGetValue KeyProfiles with
                | true, (:? TomlTable as profiles) ->
                    match profiles.TryGetValue profileName with
                    | true, (:? TomlTable as p) ->
                        Ok {
                            Token         = tryString p KeyToken
                            BaseUrl       = tryString p KeyBaseUrl
                            SourceToken   = tryString p KeySourceToken
                            SourceBaseUrl = tryString p KeySourceBaseUrl
                        }
                    | _ ->
                        Error $"Profile '{profileName}' not found in {path}."
                | _ ->
                    Error $"No [{KeyProfiles}] table in {path}."
            with ex ->
                Error $"Failed to parse {path}: {ex.Message}"
