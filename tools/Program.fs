open System
open System.IO
open Argu
open FSharp.Control
open Nocfo.Domain
open Nocfo.Tools.Arguments
open Nocfo.Tools
open Nocfo.Tools.BlueprintJson

module ExitCodes =
    [<Literal>]
    let EX_OK = 0
    [<Literal>]
    let EX_USAGE = 64
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
    let EX_CANTCREAT = 73
    [<Literal>]
    let EX_CONFIG = 78

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
        | NocfoClient.Http.HttpError.Transport (url, message) ->
            eprintfn "Cannot reach %O: %s" url message
            ExitCodes.EX_UNAVAILABLE
        | NocfoClient.Http.HttpError.Timeout url ->
            eprintfn "Request timed out: %O" url
            ExitCodes.EX_UNAVAILABLE
    | DomainError.Unexpected message when message.StartsWith("No matching business:", StringComparison.Ordinal) ->
        eprintfn "%s" message
        ExitCodes.EX_NOINPUT
    | DomainError.Unexpected message ->
        eprintfn "Unexpected error: %s" message
        ExitCodes.EX_SOFTWARE

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
                | Error error -> raise (DomainStreamException error))
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

/// Writes the requested fields of a list response as CSV, hydrating each row only when the row
/// type cannot answer the selection on its own. `AccountRow` lacks only `balance` and `is_used`,
/// so `list accounts --fields "id,number,name,type"` costs ⌈N/100⌉ requests rather than
/// ⌈N/100⌉ + N. Document and contact rows are already the full form, so they never hydrate.
let writeEntitiesCsv<'Full, 'Row>
    (output: TextWriter)
    (fields: string list)
    (hydrate: 'Row -> Async<Result<'Full, DomainError>>)
    (rows: AsyncSeq<Result<'Row, DomainError>>)
    : AsyncSeq<unit> =
    let unwrap = function
        | Ok entity -> entity
        | Error error -> raise (DomainStreamException error)
    if Nocfo.Csv.canSupplyFields<'Row> fields then
        rows
        |> AsyncSeq.map unwrap
        |> Nocfo.Csv.writeCsvGeneric<'Row> output (Some fields)
    else
        rows
        |> AsyncSeq.mapAsync (function
            | Error error -> async.Return (Error error)
            | Ok row -> hydrate row)
        |> AsyncSeq.map unwrap
        |> Nocfo.Csv.writeCsvGeneric<'Full> output (Some fields)

let private listEntitiesForBusiness<'Full, 'Row>
    (toolContext: ToolContext)
    (args: ParseResults<BusinessScopedArgs>)
    (fields: string list)
    (streamRows: BusinessContext -> AsyncSeq<Result<'Row, DomainError>>)
    (hydrate: BusinessContext -> 'Row -> Async<Result<'Full, DomainError>>) =
    async {
        let! businessContext = getBusinessContext toolContext args
        match businessContext with
        | Ok businessContext ->
            do!
                streamRows businessContext
                |> writeEntitiesCsv<'Full, 'Row> toolContext.Output fields (hydrate businessContext)
                |> AsyncSeq.iter ignore
            return 0
        | Error error ->
            return mapDomainErrorToExitCode error
    }

let listAccounts (toolContext: ToolContext) (args: ParseResults<BusinessScopedArgs>) (fields: string list) =
    listEntitiesForBusiness<AccountFull, AccountRow>
        toolContext
        args
        fields
        Streams.streamAccountRows
        (fun context row -> Account.fetchFull context row.id)

let listDocuments (toolContext: ToolContext) (args: ParseResults<BusinessScopedArgs>) (fields: string list) =
    listEntitiesForBusiness<DocumentFull, DocumentRow>
        toolContext
        args
        fields
        Streams.streamDocumentRows
        (fun _ row -> async.Return (Ok row))

let listContacts (toolContext: ToolContext) (args: ParseResults<BusinessScopedArgs>) (fields: string list) =
    listEntitiesForBusiness<ContactFull, ContactRow>
        toolContext
        args
        fields
        Streams.streamContactRows
        (fun _ row -> async.Return (Ok row))

let private foldCommandResults (printOk: 'Result -> unit) (results: AsyncSeq<Result<'Result, DomainError>>) : Async<int> =
    async {
        let! n =
            results
            |> AsyncSeq.fold (fun n r ->
                match r with
                | Ok v  -> printOk v; n
                | Error e -> eprintfn "Command failed: %A" e; n + 1) 0
        return if n > 0 then 1 else 0
    }

let foldAccountCommandResults =
    foldCommandResults (function
        | AccountUpdated account   -> eprintfn "Updated account %d (%s)" account.id account.number
        | AccountDeleted id        -> eprintfn "Deleted account %d" id
        | AccountCreated account   -> eprintfn "Created account %d (%s)" account.id account.number)

let foldDocumentCommandResults =
    foldCommandResults (function
        | DocumentCreated document -> eprintfn "Created document %d (%s)" document.id (defaultArg document.number "<none>")
        | DocumentUpdated document -> eprintfn "Updated document %d (%s)" document.id (defaultArg document.number "<none>")
        | DocumentDeleted id       -> eprintfn "Deleted document %d" id)

let foldContactCommandResults =
    foldCommandResults (function
        | ContactUpdated contact   -> eprintfn "Updated contact %d (%s)" contact.id contact.name
        | ContactDeleted id        -> eprintfn "Deleted contact %d" id
        | ContactCreated contact   -> eprintfn "Created contact %d (%s)" contact.id contact.name)

let foldBusinessCommandResults =
    foldCommandResults (function
        | BusinessUpdated b        -> eprintfn "Updated business %s (%s)" b.key.slug b.meta.name
        | BusinessCreated b        -> eprintfn "Created business %s (%s)" b.key.slug b.meta.name)

[<CLIMutable>]
type private DeletePayload =
    { id: int }

let updateBusinesses (toolContext: ToolContext) (args: ParseResults<BusinessesArgs>) (fields: string list) =
    async {
        let csvStream =
            Nocfo.Csv.readDeltas<BusinessDeltaRow, string, NocfoApi.Types.PatchedBusinessRequest> "slug" toolContext.Input (Some fields)
            |> AsyncSeq.map Ok
        return!
            csvStream
            |> Business.executeDeltaUpdates toolContext.Accounting
            |> foldBusinessCommandResults
    }

let updateAccounts (toolContext: ToolContext) (args: ParseResults<BusinessScopedArgs>) (fields: string list) =
    async {
        let input = toolContext.Input
        let! businessContext  = getBusinessContext toolContext args
        match businessContext with
        | Ok ctx ->
            // The desired state of accounts from the CSV file.
            let csvStream =
                Nocfo.Csv.readDeltas<AccountDelta, int, NocfoApi.Types.PatchedAccountRequest> "id" input (Some fields)
                |> AsyncSeq.map Ok
            return!
                Account.executeDeltaUpdates ctx csvStream
                |> foldAccountCommandResults
        | Error error ->
            return mapDomainErrorToExitCode error
    }

let updateContacts (toolContext: ToolContext) (args: ParseResults<BusinessScopedArgs>) (fields: string list) =
    async {
        let input = toolContext.Input
        let! businessContext  = getBusinessContext toolContext args
        match businessContext with
        | Ok ctx ->
            // The desired state of contacts from the CSV file.
            let csvStream =
                Nocfo.Csv.readDeltas<ContactDelta, int, NocfoApi.Types.PatchedContactRequest> "id" input (Some fields)
                |> AsyncSeq.map Ok
            return!
                Contact.executeDeltaUpdates ctx csvStream
                |> foldContactCommandResults
        | Error error ->
            return mapDomainErrorToExitCode error
    }

let updateDocuments (toolContext: ToolContext) (args: ParseResults<BusinessScopedArgs>) (fields: string list) =
    async {
        let input = toolContext.Input
        let! businessContext = getBusinessContext toolContext args
        match businessContext with
        | Ok ctx ->
            let csvStream =
                Nocfo.Csv.readDeltas<DocumentDelta, int, NocfoApi.Types.PatchedDocumentInstanceRequest> "id" input (Some fields)
                |> AsyncSeq.map Ok
            return!
                Document.executeDeltaUpdates ctx csvStream
                |> foldDocumentCommandResults
        | Error error ->
            return mapDomainErrorToExitCode error
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
            return mapDomainErrorToExitCode error
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
            | EntitiesArgs.Businesses args -> updateBusinesses toolContext args fields
            | EntitiesArgs.Accounts args   -> updateAccounts toolContext args fields
            | EntitiesArgs.Contacts args   -> updateContacts toolContext args fields
            | EntitiesArgs.Documents args  -> updateDocuments toolContext args fields
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

let private getSourceAccounting (cfg: ToolConfig) (verbose: bool) =
    match cfg.SourceToken with
    | Some sourceToken ->
        let sourceHttp = NocfoClient.Http.createHttpContext cfg.SourceBaseUrl sourceToken verbose
        Ok (Accounting.ofHttp sourceHttp false)
    | None ->
        Error "Missing required environment variable NOCFO_SOURCE_TOKEN for `map accounts`."

/// Pairs source accounts with target accounts on account number, yielding the mappings in
/// source order together with the source accounts that have no target counterpart.
/// The target chart is indexed rather than merged as an ordered stream: the API does not
/// document the order it lists accounts in, and an ordered merge silently drops rows
/// whenever the two orders disagree (e.g. `999` against `1000`).
let mapAccountRows
    (sourceAccounts: AsyncSeq<Result<AccountRow, DomainError>>)
    (targetAccounts: AsyncSeq<Result<AccountRow, DomainError>>)
    : Async<Result<Mapping.IDMap list * AccountRow list, DomainError>> =

    let collect folder initial (stream: AsyncSeq<Result<'T, DomainError>>) =
        stream
        |> AsyncSeq.takeWhileInclusive Result.isOk
        |> AsyncSeq.fold (fun state result ->
            match state, result with
            | Error _, _ -> state
            | _, Error err -> Error err
            | Ok acc, Ok item -> Ok (folder acc item)) (Ok initial)

    async {
        let! targetIndex =
            targetAccounts
            |> collect (fun index (target: AccountRow) -> Map.add target.number target index) Map.empty

        match targetIndex with
        | Error err -> return Error err
        | Ok index ->
            let! paired =
                sourceAccounts
                |> collect (fun (rows, missing) (source: AccountRow) ->
                    match Map.tryFind source.number index with
                    | Some target ->
                        ({ source_id = source.id
                           target_id = target.id
                           number = source.number } : Mapping.IDMap) :: rows, missing
                    | None -> rows, source :: missing) ([], [])
            return paired |> Result.map (fun (rows, missing) -> List.rev rows, List.rev missing)
    }

let mapAccounts (toolContext: ToolContext) (args: ParseResults<BusinessScopedArgs>) =
    async {
        let businessId = args.GetResult(BusinessScopedArgs.BusinessId, defaultValue = "")

        match getSourceAccounting toolContext.Config toolContext.Accounting.http.verbose with
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
                // Only `id` and `number` are needed, and the list rows carry both.
                let! paired =
                    mapAccountRows
                        (Streams.streamAccountRows sourceContext)
                        (Streams.streamAccountRows targetContext)

                match paired with
                | Error err ->
                    return mapDomainErrorToExitCode err
                | Ok (rows, missing) ->
                    for account in missing do
                        eprintfn "Warning: no target mapping for source account id=%d number=%s" account.id account.number
                    try
                        do!
                            rows
                            |> AsyncSeq.ofSeq
                            |> Nocfo.Csv.writeCsvGeneric<Mapping.IDMap> toolContext.Output None
                            |> AsyncSeq.iter ignore
                        if List.isEmpty missing then
                            return ExitCodes.EX_OK
                        else
                            return ExitCodes.EX_DATAERR
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
            return mapDomainErrorToExitCode error
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
            return mapDomainErrorToExitCode error
        | Ok ctx ->
            let commands =
                Nocfo.Csv.readCsvGeneric<ContactCreatePayload> toolContext.Input (Some fields)
                |> AsyncSeq.map (fun payload -> Ok (ContactCommand.CreateContact payload))
            return!
                commands
                |> Streams.executeContactCommands ctx
                |> foldContactCommandResults
    }

let createBusinesses (toolContext: ToolContext) (args: ParseResults<BusinessesArgs>) (fields: string list) =
    async {
        let commands =
            Nocfo.Csv.readCsvGeneric<BusinessCreatePayload> toolContext.Input (Some fields)
            |> AsyncSeq.map Ok
        return!
            commands
            |> Streams.executeBusinessCommands toolContext.Accounting
            |> foldBusinessCommandResults
    }

let createDocuments (toolContext: ToolContext) (args: ParseResults<DocumentCreateArgs>) (fields: string list) =
    async {
        let businessId = args.GetResult(DocumentCreateArgs.BusinessId, defaultValue = "")
        let strict = args.Contains DocumentCreateArgs.Strict
        let! businessContext = resolveBusinessContext toolContext.Accounting businessId
        match businessContext with
        | Error error ->
            return mapDomainErrorToExitCode error
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
            | CreateEntitiesArgs.Businesses businessArgs -> createBusinesses toolContext businessArgs fields
            | _ -> failwith "Unknown create entity type"
    }

let private run (parser: ArgumentParser<CliArgs>) argv =
    let results: ParseResults<CliArgs> =
        parser.ParseCommandLine(argv, raiseOnUsage = true)

    match results.TryGetSubCommand() with
    | None ->
        eprintfn "%s" (parser.PrintUsage())
        ExitCodes.EX_USAGE
    | Some subcommand ->

    use input : TextReader =
        match results.TryGetResult CliArgs.In with
        | Some path -> upcast new StreamReader(path)
        | None -> Console.In

    use output : TextWriter =
        match results.TryGetResult CliArgs.Out with
        | Some path -> upcast new StreamWriter(path)
        | None -> Console.Out

    let dryRun  = results.Contains(CliArgs.DryRun)
    let verbose = results.Contains(CliArgs.Verbose)
    let profile = results.TryGetResult CliArgs.Profile
    let toolContext = Nocfo.Tools.Runtime.ToolConfig.loadOrFail profile input output dryRun verbose

    match subcommand with
    | CliArgs.List _   -> list   toolContext (results.GetResult List)
    | CliArgs.Update _ -> update toolContext (results.GetResult Update)
    | CliArgs.Delete _ -> delete toolContext (results.GetResult Delete)
    | CliArgs.Map _    -> map    toolContext (results.GetResult Map)
    | CliArgs.Create _ -> create toolContext (results.GetResult Create)
    | _ ->
        eprintfn "%s" (parser.PrintUsage())
        async.Return ExitCodes.EX_USAGE
    |> Async.RunSynchronously

/// Reports the failure on stderr (usage on stdout) and yields the sysexits code for it.
let exitCodeForException (ex: exn) =
    match ex with
    | :? ArguParseException as parseError when parseError.ErrorCode = ErrorCode.HelpText ->
        printfn "%s" parseError.Message
        ExitCodes.EX_OK
    | :? ArguParseException ->
        eprintfn "%s" ex.Message
        ExitCodes.EX_USAGE
    | DomainStreamException err ->
        mapDomainErrorToExitCode err
    | Nocfo.CsvFormatException message
    | NocfoClient.StreamOrderException message ->
        eprintfn "%s" message
        ExitCodes.EX_DATAERR
    | :? FileNotFoundException
    | :? DirectoryNotFoundException ->
        eprintfn "%s" ex.Message
        ExitCodes.EX_NOINPUT
    | :? UnauthorizedAccessException
    | :? IOException ->
        eprintfn "%s" ex.Message
        ExitCodes.EX_CANTCREAT
    | _ when ex.Message.StartsWith("Tool configuration failed:", StringComparison.Ordinal) ->
        eprintfn "%s" ex.Message
        ExitCodes.EX_CONFIG
    | _ ->
        eprintfn "Internal error: %s" ex.Message
        ExitCodes.EX_SOFTWARE

[<EntryPoint>]
let main argv =
    let parser = ArgumentParser.Create<CliArgs>(programName = "nocfo")
    try
        run parser argv
    with ex ->
        exitCodeForException ex
