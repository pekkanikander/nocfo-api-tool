open System
open System.IO
open Argu
open FSharp.Control
open Newtonsoft.Json.Linq
open Nocfo.Domain
open Nocfo.Tools.Arguments
open Nocfo.Tools

module ExitCodes =
    [<Literal>]
    let EX_OK = 0
    [<Literal>]
    let EX_DATAERR = 65
    [<Literal>]
    let EX_NOINPUT = 66
    [<Literal>]
    let EX_NOPERM = 77
    [<Literal>]
    let EX_UNAVAILABLE = 69
    [<Literal>]
    let EX_SOFTWARE = 70
    [<Literal>]
    let EX_CONFIG = 78

let handleEntitiesArgs (args: ParseResults<EntitiesArgs>) =
    let entityTypeAndArgs = args.GetSubCommand()
    let fields = args.GetResult(EntitiesArgs.Fields, defaultValue = [])
    (entityTypeAndArgs, fields)

let handleCreateEntitiesArgs (args: ParseResults<CreateEntitiesArgs>) =
    let entityTypeAndArgs = args.GetSubCommand()
    let fields = args.GetResult(CreateEntitiesArgs.Fields, defaultValue = [])
    (entityTypeAndArgs, fields)

let private resolveBusinessContext (accounting: AccountingContext) (businessId: string) =
    async {
        let! businessContext = BusinessResolver.resolve accounting businessId
        return businessContext
    }

let listBusinesses (toolContext: ToolContext) (args: ParseResults<BusinessesArgs>) (fields: string list) =
    async {
        let output = toolContext.Output
        let rows =
            Streams.streamBusinesses toolContext.Accounting
            |> Streams.hydrateAndUnwrap
            |> AsyncSeq.map (function
                | Ok business -> business.raw
                | Error error -> failwithf "Failed to get business: %A" error)
        let writeCsv =
            Nocfo.Csv.writeCsvGeneric<NocfoApi.Types.Business> output (Some fields) rows
        do! writeCsv |> AsyncSeq.iter ignore
        return 0
    }

let private getBusinessContext (toolContext: ToolContext) (args: ParseResults<BusinessScopedArgs>) =
    async {
        let businessId = args.GetResult(BusinessScopedArgs.BusinessId, defaultValue = "")
        let! businessContext  = resolveBusinessContext toolContext.Accounting businessId
        return businessContext
    }

let private listEntitiesForBusiness<'Full, 'Partial>
    (toolContext: ToolContext)
    (args: ParseResults<BusinessScopedArgs>)
    (fields: string list)
    (streamEntities: BusinessContext -> AsyncSeq<Result<Hydratable<'Full, 'Partial>, DomainError>>)
    (hydrateFailureLabel: string) =
    async {
        let output = toolContext.Output
        let! businessContext = getBusinessContext toolContext args
        match businessContext with
        | Ok businessContext ->
            let rows =
                streamEntities businessContext
                |> Streams.hydrateAndUnwrap
                |> AsyncSeq.map (function
                    | Ok entity -> entity
                    | Error error -> failwithf "Failed to hydrate %s: %A" hydrateFailureLabel error)
            let writeCsv =
                Nocfo.Csv.writeCsvGeneric<'Full> output (Some fields) rows
            do! writeCsv |> AsyncSeq.iter ignore
            return 0
        | Error error ->
            eprintfn "Failed to get business context: %A" error
            return 1
    }

let listAccounts (toolContext: ToolContext) (args: ParseResults<BusinessScopedArgs>) (fields: string list) =
    listEntitiesForBusiness<AccountFull, AccountRow>
        toolContext
        args
        fields
        Streams.streamAccounts
        "account"

let listDocuments (toolContext: ToolContext) (args: ParseResults<BusinessScopedArgs>) (fields: string list) =
    listEntitiesForBusiness<DocumentFull, DocumentRow>
        toolContext
        args
        fields
        Streams.streamDocuments
        "document"

let listContacts (toolContext: ToolContext) (args: ParseResults<BusinessScopedArgs>) (fields: string list) =
    listEntitiesForBusiness<ContactFull, ContactRow>
        toolContext
        args
        fields
        Streams.streamContacts
        "contact"

let updateBusinesses (toolContext: ToolContext) (args: ParseResults<BusinessesArgs>) =
    async {
        eprintfn "update businesses: not yet implemented"
        return ExitCodes.EX_SOFTWARE
    }

let private foldCommandResults (printOk: 'Result -> unit) (results: AsyncSeq<Result<'Result, DomainError>>) : Async<int> =
    async {
        let! n =
            results
            |> AsyncSeq.fold (fun n r ->
                match r with
                | Ok v  -> printOk v; n
                | Error e -> printfn "Command failed: %A" e; n + 1) 0
        return if n > 0 then 1 else 0
    }

let foldAccountCommandResults =
    foldCommandResults (function
        | AccountUpdated account  -> printfn "Updated account %d (%s)" account.id account.number
        | AccountDeleted id       -> printfn "Deleted account %d" id
        | AccountCreated account  -> printfn "Created account %d (%s)" account.id account.number)

let foldDocumentCommandResults =
    foldCommandResults (function
        | DocumentCreated document -> printfn "Created document %d (%s)" document.id (defaultArg document.number "<none>")
        | DocumentDeleted id       -> printfn "Deleted document %d" id)

let foldContactCommandResults =
    foldCommandResults (function
        | ContactUpdated contact -> printfn "Updated contact %d (%s)" contact.id contact.name
        | ContactDeleted id      -> printfn "Deleted contact %d" id
        | ContactCreated contact -> printfn "Created contact %d (%s)" contact.id contact.name)

[<CLIMutable>]
type private DeletePayload =
    { id: int }

let updateAccounts (toolContext: ToolContext) (args: ParseResults<BusinessScopedArgs>) (fields: string list) =
    async {
        let input = toolContext.Input
        let! businessContext  = getBusinessContext toolContext args
        match businessContext with
        | Ok ctx ->
            // The desired state of accounts from the CSV file.
            let csvStream =
                Nocfo.Csv.readDeltas<AccountDelta, NocfoApi.Types.PatchedAccountRequest> input (Some fields)
                |> AsyncSeq.map Ok
            return!
                Account.executeDeltaUpdates ctx csvStream
                |> foldAccountCommandResults
        | Error error ->
            eprintfn "Failed to get business context: %A" error
            return 1
    }

let updateContacts (toolContext: ToolContext) (args: ParseResults<BusinessScopedArgs>) (fields: string list) =
    async {
        let input = toolContext.Input
        let! businessContext  = getBusinessContext toolContext args
        match businessContext with
        | Ok ctx ->
            // The desired state of contacts from the CSV file.
            let csvStream =
                Nocfo.Csv.readDeltas<ContactDelta, NocfoApi.Types.PatchedContactRequest> input (Some fields)
                |> AsyncSeq.map Ok
            return!
                Contact.executeDeltaUpdates ctx csvStream
                |> foldContactCommandResults
        | Error error ->
            eprintfn "Failed to get business context: %A" error
            return 1
    }

let private deleteEntities<'Command, 'Result>
    (toolContext: ToolContext)
    (args: ParseResults<BusinessScopedArgs>)
    (fields: string list)
    (mkCommand: int -> 'Command)
    (execute: BusinessContext -> AsyncSeq<Result<'Command, DomainError>> -> AsyncSeq<Result<'Result, DomainError>>)
    (foldResults: AsyncSeq<Result<'Result, DomainError>> -> Async<int>) =
    async {
        let csvStream =
            Nocfo.Csv.readCsvGeneric<DeletePayload> toolContext.Input (Some ("id" :: fields))
            |> AsyncSeq.map (fun p -> Ok (mkCommand p.id))
        let! businessContext = getBusinessContext toolContext args
        match businessContext with
        | Ok ctx ->
            return! csvStream |> execute ctx |> foldResults
        | Error error ->
            eprintfn "Failed to get business context: %A" error
            return 1
    }

let deleteAccounts (toolContext: ToolContext) (args: ParseResults<BusinessScopedArgs>) (fields: string list) =
    deleteEntities toolContext args fields AccountCommand.DeleteAccount Streams.executeAccountCommands foldAccountCommandResults

let deleteDocuments (toolContext: ToolContext) (args: ParseResults<BusinessScopedArgs>) (fields: string list) =
    deleteEntities toolContext args fields DocumentCommand.DeleteDocument Streams.executeDocumentCommands foldDocumentCommandResults

let deleteContacts (toolContext: ToolContext) (args: ParseResults<BusinessScopedArgs>) (fields: string list) =
    deleteEntities toolContext args fields ContactCommand.DeleteContact Streams.executeContactCommands foldContactCommandResults

// XXX: TODO: Implement an abstract 'command' type and a map of commands to functions.
let list (toolContext: ToolContext) (args: ParseResults<EntitiesArgs>) =
    async {
        let (entityTypeAndArgs, fields) = handleEntitiesArgs args
        return!
            match entityTypeAndArgs with
            | EntitiesArgs.Businesses args -> listBusinesses toolContext args fields
            | EntitiesArgs.Accounts args   -> listAccounts toolContext args fields
            | EntitiesArgs.Contacts args   -> listContacts toolContext args fields
            | EntitiesArgs.Documents args  -> listDocuments toolContext args fields
            | _ -> failwith "Unknown entity type"
    }

let update  (toolContext: ToolContext) (args: ParseResults<EntitiesArgs>) =
    async {
        let (entityTypeAndArgs, fields) = handleEntitiesArgs args
        return!
            match entityTypeAndArgs with
            | EntitiesArgs.Businesses args -> updateBusinesses toolContext args
            | EntitiesArgs.Accounts args   -> updateAccounts toolContext args fields
            | EntitiesArgs.Contacts args   -> updateContacts toolContext args fields
            | _ -> failwith "Unknown entity type"
    }

let delete (toolContext: ToolContext) (args: ParseResults<EntitiesArgs>) =
    async {
        let (entityTypeAndArgs, fields) = handleEntitiesArgs args
        return!
            match entityTypeAndArgs with
            | EntitiesArgs.Accounts args -> deleteAccounts toolContext args fields
            | EntitiesArgs.Contacts args -> deleteContacts toolContext args fields
            | EntitiesArgs.Documents args -> deleteDocuments toolContext args fields
            | _ -> failwith "Unknown entity type"
    }

let private mapDomainErrorToExitCode (err: DomainError) =
    match err with
    | DomainError.Http httpErr ->
        match httpErr with
        | NocfoClient.Http.HttpError.Unauthorized url ->
            eprintfn "Authentication failed (401 %O). Check your NOCFO_TOKEN." url
            ExitCodes.EX_NOPERM
        | NocfoClient.Http.HttpError.NotFound url ->
            eprintfn "Resource not found (404 %O)." url
            ExitCodes.EX_NOINPUT
        | NocfoClient.Http.HttpError.RateLimited (url, _) ->
            eprintfn "Rate limit exceeded after retries (%O)." url
            ExitCodes.EX_UNAVAILABLE
        | NocfoClient.Http.HttpError.ServerError (url, code, body) ->
            eprintfn "Server error %A at %O: %s" code url (body.Substring(0, min 200 body.Length))
            ExitCodes.EX_UNAVAILABLE
        | NocfoClient.Http.HttpError.ClientError (url, code, body) ->
            eprintfn "Request error %A at %O: %s" code url (body.Substring(0, min 200 body.Length))
            ExitCodes.EX_UNAVAILABLE
        | NocfoClient.Http.HttpError.ParseError (url, message) ->
            eprintfn "Unexpected API response at %O: %s" url message
            ExitCodes.EX_SOFTWARE
    | DomainError.Unexpected message when message.StartsWith("No matching business:", StringComparison.Ordinal) ->
        eprintfn "%s" message
        ExitCodes.EX_NOINPUT
    | DomainError.Unexpected message ->
        eprintfn "Unexpected error: %s" message
        ExitCodes.EX_SOFTWARE

type private MapAccountsOutcome =
    | Mapped of Mapping.IDMap
    | MissingTarget of AccountFull

let private getSourceAccounting (cfg: ToolConfig) =
    match cfg.SourceToken with
    | Some sourceToken ->
        let sourceHttp = NocfoClient.Http.createHttpContext cfg.SourceBaseUrl sourceToken
        Ok (Accounting.ofHttp sourceHttp false)
    | None ->
        Error "Missing required environment variable NOCFO_SOURCE_TOKEN for `map accounts`."

let mapAccounts (toolContext: ToolContext) (args: ParseResults<BusinessScopedArgs>) =
    async {
        let businessId = args.GetResult(BusinessScopedArgs.BusinessId, defaultValue = "")

        match getSourceAccounting toolContext.Config with
        | Error configError ->
            eprintfn "%s" configError
            return ExitCodes.EX_CONFIG
        | Ok sourceAccounting ->
            let targetResolve = getBusinessContext toolContext args
            let sourceResolve = BusinessResolver.resolve sourceAccounting businessId
            let! targetContextResult = targetResolve
            let! sourceContextResult = sourceResolve

            match sourceContextResult, targetContextResult with
            | Error err, _
            | _, Error err ->
                eprintfn "Failed to resolve business context: %A" err
                return mapDomainErrorToExitCode err
            | Ok sourceContext, Ok targetContext ->
                let sourceAccounts =
                    Streams.streamAccounts sourceContext
                    |> Streams.hydrateAndUnwrap

                let targetAccounts =
                    Streams.streamAccounts targetContext
                    |> Streams.hydrateAndUnwrap

                let outcomes =
                    Alignment.alignEntries<AccountFull, AccountFull, string, MapAccountsOutcome option>
                        (fun source -> source.number)
                        (fun target -> target.number)
                        (fun source target ->
                            Ok (Some (Mapped { source_id = source.id; target_id = target.id; number = source.number })))
                        (fun _targetOnly -> Ok None)
                        (fun sourceOnly -> Ok (Some (MissingTarget sourceOnly)))
                        sourceAccounts
                        targetAccounts

                let! warnings, rowsReversed, firstError =
                    outcomes
                    |> AsyncSeq.fold (fun (warnings, rows, firstError) outcome ->
                        match outcome with
                        | Error err ->
                            let updatedFirstError =
                                match firstError with
                                | Some _ -> firstError
                                | None -> Some err
                            warnings, rows, updatedFirstError
                        | Ok None ->
                            warnings, rows, firstError
                        | Ok (Some (Mapped row)) ->
                            warnings, row :: rows, firstError
                        | Ok (Some (MissingTarget sourceAccount)) ->
                            eprintfn "Warning: no target mapping for source account id=%d number=%s" sourceAccount.id sourceAccount.number
                            warnings + 1, rows, firstError) (0, [], None)

                match firstError with
                | Some err ->
                    eprintfn "Mapping failed: %A" err
                    return mapDomainErrorToExitCode err
                | None ->
                    let rows = rowsReversed |> List.rev
                    try
                        do!
                            rows
                            |> AsyncSeq.ofSeq
                            |> Nocfo.Csv.writeCsvGeneric<Mapping.IDMap> toolContext.Output None
                            |> AsyncSeq.iter ignore
                        if warnings > 0 then
                            return ExitCodes.EX_DATAERR
                        else
                            return ExitCodes.EX_OK
                    with ex ->
                        eprintfn "Failed to write CSV output: %s" ex.Message
                        return ExitCodes.EX_SOFTWARE
    }

let map (toolContext: ToolContext) (args: ParseResults<MapEntitiesArgs>) =
    async {
        let entityTypeAndArgs = args.GetSubCommand()
        return!
            match entityTypeAndArgs with
            | MapEntitiesArgs.Accounts accountArgs -> mapAccounts toolContext accountArgs
    }

let private emptyBlueprint : JToken =
    JObject.Parse("""{
  "debet_account_id": null,
  "debet_entries": [],
  "credit_account_id": null,
  "credit_entries": [],
  "expense_entries": []
}""")

let private tryAsInt (token: JToken) =
    match token.Type with
    | JTokenType.Integer -> Some (token.Value<int>())
    | _ -> None

let private remapObjectAccountId (accountIdMap: Map<int, int>) (key: string) (container: JObject) =
    match container.TryGetValue key with
    | true, value when value.Type = JTokenType.Null -> Ok ()
    | true, value ->
        match tryAsInt value with
        | Some sourceId ->
            match Map.tryFind sourceId accountIdMap with
            | Some targetId ->
                container.[key] <- JValue(targetId)
                Ok ()
            | None ->
                Error (DomainError.Unexpected $"Missing account-id mapping for {key}={sourceId}.")
        | None ->
            Error (DomainError.Unexpected $"Expected integer for {key}, got {value.Type}.")
    | false, _ -> Ok ()

let private remapEntryArrayAccountIds (accountIdMap: Map<int, int>) (key: string) (container: JObject) =
    match container.TryGetValue key with
    | true, (:? JArray as entries) ->
        entries
        |> Seq.fold (fun state entry ->
            match state with
            | Error _ -> state
            | Ok () ->
                match entry with
                | :? JObject as entryObj ->
                    match entryObj.TryGetValue "account_id" with
                    | true, value when value.Type = JTokenType.Null -> Ok ()
                    | true, value ->
                        match tryAsInt value with
                        | Some sourceId ->
                            match Map.tryFind sourceId accountIdMap with
                            | Some targetId ->
                                entryObj.["account_id"] <- JValue(targetId)
                                Ok ()
                            | None ->
                                Error (DomainError.Unexpected $"Missing account-id mapping for {key}.account_id={sourceId}.")
                        | None ->
                            Error (DomainError.Unexpected $"Expected integer for {key}.account_id, got {value.Type}.")
                    | false, _ -> Ok ()
                | _ ->
                    Error (DomainError.Unexpected $"Expected object entries under {key}."))
            (Ok ())
    | true, value ->
        Error (DomainError.Unexpected $"Expected array for {key}, got {value.Type}.")
    | false, _ -> Ok ()

let private remapBlueprint (strict: bool) (accountIdMap: Map<int, int>) (row: DocumentCreatePayload) =
    match row.blueprint with
    | None -> Ok row
    | Some blueprint ->
        match blueprint with
        | :? JObject as blueprintObj ->
            let mutable remapError : DomainError option = None
            let apply result =
                match remapError, result with
                | None, Error err -> remapError <- Some err
                | _ -> ()

            apply (remapObjectAccountId accountIdMap "debet_account_id" blueprintObj)
            apply (remapObjectAccountId accountIdMap "credit_account_id" blueprintObj)
            apply (remapEntryArrayAccountIds accountIdMap "debet_entries" blueprintObj)
            apply (remapEntryArrayAccountIds accountIdMap "credit_entries" blueprintObj)
            apply (remapEntryArrayAccountIds accountIdMap "expense_entries" blueprintObj)

            match remapError with
            | None ->
                Ok { row with blueprint = Some blueprintObj }
            | Some err ->
                if strict then
                    Error err
                else
                    eprintfn "Warning: %A; replacing blueprint with empty blueprint." err
                    Ok { row with blueprint = Some (emptyBlueprint.DeepClone()) }
        | _ ->
            if strict then
                Error (DomainError.Unexpected $"Expected blueprint JSON object, got {blueprint.Type}.")
            else
                eprintfn "Warning: invalid blueprint shape (%A); replacing blueprint with empty blueprint." blueprint.Type
                Ok { row with blueprint = Some (emptyBlueprint.DeepClone()) }

let private loadAccountIdMap (mapPath: string) =
    async {
        use reader = new StreamReader(mapPath)
        let! rows =
            Nocfo.Csv.readCsvGeneric<Mapping.IDMap> reader None
            |> AsyncSeq.toListAsync
        return rows |> List.map (fun row -> row.source_id, row.target_id) |> Map.ofList
    }

let createAccounts (toolContext: ToolContext) (args: ParseResults<BusinessScopedArgs>) (fields: string list) =
    async {
        let! businessContext = getBusinessContext toolContext args
        match businessContext with
        | Error error ->
            eprintfn "Failed to get business context: %A" error
            return 1
        | Ok ctx ->
            let commands =
                Nocfo.Csv.readCsvGeneric<AccountCreatePayload> toolContext.Input (Some fields)
                |> AsyncSeq.map (fun payload -> Ok (AccountCommand.CreateAccount payload))
            return!
                commands
                |> Streams.executeAccountCommands ctx
                |> foldAccountCommandResults
    }

let createContacts (toolContext: ToolContext) (args: ParseResults<BusinessScopedArgs>) (fields: string list) =
    async {
        let! businessContext = getBusinessContext toolContext args
        match businessContext with
        | Error error ->
            eprintfn "Failed to get business context: %A" error
            return 1
        | Ok ctx ->
            let commands =
                Nocfo.Csv.readCsvGeneric<ContactCreatePayload> toolContext.Input (Some fields)
                |> AsyncSeq.map (fun payload -> Ok (ContactCommand.CreateContact payload))
            return!
                commands
                |> Streams.executeContactCommands ctx
                |> foldContactCommandResults
    }

let createDocuments (toolContext: ToolContext) (args: ParseResults<DocumentCreateArgs>) (fields: string list) =
    async {
        let businessId = args.GetResult(DocumentCreateArgs.BusinessId, defaultValue = "")
        let strict = args.Contains DocumentCreateArgs.Strict
        let! businessContext = resolveBusinessContext toolContext.Accounting businessId
        match businessContext with
        | Error error ->
            eprintfn "Failed to get business context: %A" error
            return 1
        | Ok ctx ->
            let! accountIdMap =
                match args.TryGetResult DocumentCreateArgs.AccountIdMap with
                | Some mapPath -> loadAccountIdMap mapPath
                | None -> async.Return Map.empty

            let rowStream =
                Nocfo.Csv.readCsvGeneric<DocumentCreatePayload> toolContext.Input (Some fields)
                |> AsyncSeq.map (fun row ->
                    if Map.isEmpty accountIdMap then
                        Ok row
                    else
                        remapBlueprint strict accountIdMap row)

            let commands =
                rowStream
                |> AsyncSeq.map (Result.map DocumentCommand.CreateDocument)

            return!
                commands
                |> Streams.executeDocumentCommands ctx
                |> foldDocumentCommandResults
    }

let create (toolContext: ToolContext) (args: ParseResults<CreateEntitiesArgs>) =
    async {
        let (entityTypeAndArgs, fields) = handleCreateEntitiesArgs args
        return!
            match entityTypeAndArgs with
            | CreateEntitiesArgs.Accounts  accountArgs   -> createAccounts  toolContext accountArgs  fields
            | CreateEntitiesArgs.Contacts  contactArgs   -> createContacts  toolContext contactArgs  fields
            | CreateEntitiesArgs.Documents documentArgs  -> createDocuments toolContext documentArgs fields
            | CreateEntitiesArgs.Businesses _            -> failwith "create businesses: not yet implemented"
            | _ -> failwith "Unknown create entity type"
    }

[<EntryPoint>]
let main argv =
    async {

        let parser = ArgumentParser.Create<CliArgs>(programName = "nocfo")
        let results: ParseResults<CliArgs> =
            parser.ParseCommandLine(argv, raiseOnUsage = false)

        let input : TextReader =
            match results.TryGetResult CliArgs.In with
            | Some path -> upcast new StreamReader(path)
            | None -> Console.In

        let output : TextWriter =
            match results.TryGetResult CliArgs.Out with
            | Some path -> upcast new StreamWriter(path)
            | None -> Console.Out

        try
            let dryRun = results.Contains(CliArgs.DryRun)
            let profile = results.TryGetResult CliArgs.Profile
            let toolContext = Nocfo.Tools.Runtime.ToolConfig.loadOrFail profile input output dryRun

            let subcommand = results.GetSubCommand()
            return!
                match subcommand with
                | CliArgs.List _   -> list   toolContext (results.GetResult List)
                | CliArgs.Update _ -> update toolContext (results.GetResult Update)
                | CliArgs.Delete _ -> delete toolContext (results.GetResult Delete)
                | CliArgs.Map _    -> map    toolContext (results.GetResult Map)
                | CliArgs.Create _ -> create toolContext (results.GetResult Create)
                | _ ->
                    eprintfn "%s" (parser.PrintUsage())
                    async.Return 1
        with ex when ex.Message.StartsWith("Tool configuration failed:", StringComparison.Ordinal) ->
            eprintfn "%s" ex.Message
            return ExitCodes.EX_CONFIG
    } |> Async.RunSynchronously
