namespace Nocfo.Tools.Arguments

open Argu

type NoPrefixAttribute() = inherit CliPrefixAttribute(CliPrefix.None)

type BusinessesArgs =
    | [< Hidden >]             Dummy
    interface IArgParserTemplate with
        member this.Usage =
            match this with
            | Dummy       -> "Dummy argument for BusinessesArgs."

type BusinessScopedArgs =
    | [< AltCommandLine("-b"); Mandatory >]       BusinessId of string
    interface IArgParserTemplate with
        member this.Usage =
            match this with
            | BusinessId _ -> "Business identifier (Y-tunnus|VAT-code)."

[<RequireSubcommand>]
type EntitiesArgs =
    | [< AltCommandLine("-f"); Inherit >]         Fields of fields: string list
    | [< NoPrefix; SubCommand >]                  Accounts of ParseResults<BusinessScopedArgs>
    | [< NoPrefix; SubCommand >]                  Contacts of ParseResults<BusinessScopedArgs>
    | [< NoPrefix; SubCommand >]                  Documents of ParseResults<BusinessScopedArgs>
    | [< NoPrefix; SubCommand >]                  Businesses of ParseResults<BusinessesArgs>
    interface IArgParserTemplate with
        member this.Usage =
            match this with
            | Fields _     -> "Comma-separated list of fields to list/update/... (default: all)."
            | Accounts _   -> "Accounts of a business."
            | Contacts _   -> "Contacts of a business."
            | Documents _  -> "Documents of a business."
            | Businesses _ -> "Businesses."

[<RequireSubcommand>]
type MapEntitiesArgs =
    | [< NoPrefix; SubCommand >]                  Accounts of ParseResults<BusinessScopedArgs>
    interface IArgParserTemplate with
        member this.Usage =
            match this with
            | Accounts _ -> "Map account identifiers between source and target environments."

[<RequireSubcommand>]
type DocumentCreateArgs =
    | [< AltCommandLine("-b"); Mandatory >]       BusinessId of string
    | [< AltCommandLine("-m") >]                  AccountIdMap of string
    | [< AltCommandLine("-s") >]                  Strict
    interface IArgParserTemplate with
        member this.Usage =
            match this with
            | BusinessId _   -> "Target business identifier (Y-tunnus|VAT-code)."
            | AccountIdMap _ -> "Optional CSV path with source_id,target_id,number mappings."
            | Strict         -> "Fail a row if blueprint account IDs are not fully mapped."

[<RequireSubcommand>]
type CreateEntitiesArgs =
    | [< AltCommandLine("-f"); Inherit >]         Fields of fields: string list
    | [< NoPrefix; SubCommand >]                  Accounts  of ParseResults<BusinessScopedArgs>
    | [< NoPrefix; SubCommand >]                  Contacts  of ParseResults<BusinessScopedArgs>
    | [< NoPrefix; SubCommand >]                  Documents of ParseResults<DocumentCreateArgs>
    | [< NoPrefix; SubCommand >]                  Businesses of ParseResults<BusinessesArgs>
    interface IArgParserTemplate with
        member this.Usage =
            match this with
            | Fields _     -> "Comma-separated list of fields to read from CSV (default: all create fields)."
            | Accounts _   -> "Create accounts from CSV input."
            | Contacts _   -> "Create contacts from CSV input."
            | Documents _  -> "Create documents from CSV input."
            | Businesses _ -> "Create businesses from CSV input."

type BalanceArgs =
    | [< AltCommandLine("-b"); Mandatory >]       BusinessId of string
    | [< CustomCommandLine("--from"); Mandatory >] DateFrom of string
    | [< CustomCommandLine("--to"); Mandatory >]  DateTo of string
    | [< AltCommandLine("-a") >]                  Account of numbers: string list
    | [< CustomCommandLine("--account-type") >]   AccountType of accountType: string
    |                                             Daily
    | [< AltCommandLine("-f") >]                  Fields of fields: string list
    interface IArgParserTemplate with
        member this.Usage =
            match this with
            | BusinessId _  -> "Business identifier (Y-tunnus|VAT-code)."
            | DateFrom _    -> "First day of the period (YYYY-MM-DD)."
            | DateTo _      -> "Last day of the period (YYYY-MM-DD)."
            | Account _     -> "Account numbers to report (default: every account with entries)."
            | AccountType _ -> "Account type to report, e.g. ASS or ASS_DEP; a prefix covers its subtypes."
            | Daily         -> "Emit one closing balance per day instead of one row per entry."
            | Fields _      -> "Comma-separated list of columns to emit (default: all)."

type ReconcileArgs =
    | [< AltCommandLine("-b") >]                  BusinessId of string
    | [< CustomCommandLine("--from") >]           DateFrom of string
    | [< CustomCommandLine("--to") >]             DateTo of string
    | [< AltCommandLine("-a") >]                  Account of number: string
    | [< Mandatory >]                             Left of source: string
    | [< Mandatory >]                             Right of source: string
    |                                             Charset of charset: string
    |                                             Tolerance of amount: decimal
    | [< AltCommandLine("-f") >]                  Fields of fields: string list
    interface IArgParserTemplate with
        member this.Usage =
            match this with
            | BusinessId _ -> "Business identifier (Y-tunnus|VAT-code); required by the 'ledger' source."
            | DateFrom _   -> "First day of the period (YYYY-MM-DD); required by the 'ledger' source."
            | DateTo _     -> "Last day of the period (YYYY-MM-DD); required by the 'ledger' source."
            | Account _    -> "Account number the 'ledger' source reports on."
            | Left _       -> "Left-hand source: 'ledger', 'nda:<path>[#<account>]' or 'csv:<path>'."
            | Right _      -> "Right-hand source: 'ledger', 'nda:<path>[#<account>]' or 'csv:<path>'."
            | Charset _    -> "Character set of a bank statement: auto (default), ascii-fi, latin1 or utf8."
            | Tolerance _  -> "Largest difference still reported as 'ok' (default 0)."
            | Fields _     -> "Comma-separated list of columns to emit (default: all)."

[<RequireSubcommand>]
type CliArgs =
    | [< AltCommandLine("-o"); Inherit >]         Out     of outPath: string
    | [< AltCommandLine("-i"); Inherit >]         In      of inPath: string
    | [< AltCommandLine("-p") >]                  Profile of profileName: string
    | [< AltCommandLine("-n") >]                  DryRun
    | [< AltCommandLine("-v") >]                  Verbose
    | [< NoPrefix; SubCommand >]                  List    of ParseResults<EntitiesArgs>
    | [< NoPrefix; SubCommand >]                  Update  of ParseResults<EntitiesArgs>
    | [< NoPrefix; SubCommand >]                  Delete  of ParseResults<EntitiesArgs>
    | [< NoPrefix; SubCommand >]                  Map     of ParseResults<MapEntitiesArgs>
    | [< NoPrefix; SubCommand >]                  Create  of ParseResults<CreateEntitiesArgs>
    | [< NoPrefix; SubCommand >]                  Balance of ParseResults<BalanceArgs>
    | [< NoPrefix; SubCommand >]                  Reconcile of ParseResults<ReconcileArgs>
    interface IArgParserTemplate with
        member this.Usage =
            match this with
            | Out _        -> "Optional CSV output path (default stdout)."
            | In _         -> "Optional CSV input path (default stdin)."
            | Profile _    -> "Named configuration profile from ~/.config/nocfo/config.toml."
            | DryRun       -> "Print what would be done without executing any mutations."
            | Verbose      -> "Log HTTP requests and responses to stderr."
            | List _       -> "List entities (businesses, accounts, etc.)."
            | Update _     -> "Update an entity (business, account, etc.)."
            | Delete _     -> "Delete entities (accounts, etc.)."
            | Map _        -> "Map entities between source and target environments."
            | Create _     -> "Create entities from CSV input."
            | Balance _    -> "Rolling balance of an account over a period."
            | Reconcile _  -> "Compare the daily balances of two sources."
