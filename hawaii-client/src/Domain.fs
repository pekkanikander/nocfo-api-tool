namespace Nocfo.Domain

open System
open System.Net
open FSharp.Control
open Nocfo
open NocfoApi.Types
open NocfoClient
open NocfoClient.Http
open NocfoClient.Streams
open NocfoClient

/// Domain-level generics

/// Domain-level error channel (extend as needed)
type DomainError =
  | Http of Http.HttpError
  | Unexpected of string

module DomainError =
  let inline asDomain r = Result.mapError DomainError.Http r

/// Generic hydratable wrapper carrying a partial payload and a fetch that upgrades it
///
/// In this domain, we use Full and Partial only to represent the lifecycle.
/// Hence, we do not use RQA for them, even though LLMs suggest it.
type Hydratable<'Full,'Partial> =
  | Partial of partial: 'Partial * fetch: (unit -> Async<Result<Hydratable<'Full,'Partial>, DomainError>>)
  | Full    of full:    'Full

/// ------------------------------------------------------------
/// Accounting model: Businesses, Accounts, and Reports
/// ------------------------------------------------------------

/// Businesses

/// Businesses are identified by their VAT ID or other similar identifier, and a slug.
/// This identifier is assumed to be serializable, immutable and stable.
/// The slug is fetched lazily from the API on demand.
type BusinessKey = {
  id: NocfoApi.Types.BusinessIdentifier
  slug: string
}

type BusinessMeta = {
  name: string
  country: string option
}

type BusinessFull = {
  key:  BusinessKey
  meta: BusinessMeta
  raw:  NocfoApi.Types.Business
}

/// Business is a hydratable of its full form, with BusinessKey as the partial
type Business = Hydratable<BusinessFull, BusinessKey>
type BusinessDelta = NocfoApi.Types.PatchedBusinessRequest // XXX: Not implemented yet

/// ------------------------------------------------------------
/// Accounts
/// ------------------------------------------------------------

type AccountClass = Asset | Liability | Equity | Income | Expense

type AccountClassTotals = Map<AccountClass, decimal>

/// Accounts are identified by their ID. Each account is associated with a business,
/// but we don't model that relationship yet, as we don't need it yet.
type AccountFull  = NocfoApi.Types.Account
type AccountRow   = NocfoApi.Types.AccountList

[<CLIMutable>]
type AccountDelta =
  { id: int; patch: NocfoApi.Types.PatchedAccountRequest }
  with
    static member Create (id: int, patch: NocfoApi.Types.PatchedAccountRequest) =
      { id = id; patch = patch }
/// Account is a hydratable of its full form with AccountRow as the partial
type Account  = Hydratable<AccountFull, AccountRow>

[<CLIMutable>]
type AccountCreatePayload =
  { number: string
    name: string
    ``type``: NocfoApi.Types.Type92dEnum option
    description: string option
    opening_balance: float32 option }

/// Domain-level account commands expressing intent before hitting HTTP.
type AccountCommand =
  | CreateAccount of AccountCreatePayload
  | UpdateAccount of AccountDelta
  | DeleteAccount of accountId:int

/// Result of executing an account command.
type AccountResult =
  | AccountCreated of AccountFull
  | AccountUpdated of AccountFull
  | AccountDeleted of int

/// ------------------------------------------------------------
/// Documents
/// ------------------------------------------------------------

/// Documents now have separate list/detail DTOs in generated types.
/// For current flows we keep list payloads as both row and full values.
type DocumentFull = NocfoApi.Types.DocumentList
type DocumentRow  = NocfoApi.Types.DocumentList
type Document     = Hydratable<DocumentFull, DocumentRow>

[<CLIMutable>]
type DocumentCreatePayload =
  { date: string option
    description: string option
    is_draft: bool option
    ``type``: Newtonsoft.Json.Linq.JToken option
    blueprint: Newtonsoft.Json.Linq.JToken option }

type DocumentCommand =
  | CreateDocument of DocumentCreatePayload
  | DeleteDocument of documentId:int

type DocumentResult =
  | DocumentCreated of DocumentFull
  | DocumentDeleted of int

/// ------------------------------------------------------------
/// Contacts
/// ------------------------------------------------------------

type ContactFull  = NocfoApi.Types.Contact
type ContactRow   = NocfoApi.Types.Contact

[<CLIMutable>]
type ContactDelta =
  { id: int; patch: NocfoApi.Types.PatchedContactRequest }
  with
    static member Create (id: int, patch: NocfoApi.Types.PatchedContactRequest) =
      { id = id; patch = patch }

type Contact      = Hydratable<ContactFull, ContactRow>

[<CLIMutable>]
type ContactCreatePayload =
  { name: string
    ``type``: NocfoApi.Types.ContactTypeEnum option
    customer_id: string option
    contact_business_id: string option
    notes: string option
    phone_number: string option }

type ContactCommand =
  | CreateContact of ContactCreatePayload
  | UpdateContact of ContactDelta
  | DeleteContact of contactId:int

type ContactResult =
  | ContactCreated of ContactFull
  | ContactUpdated of ContactFull
  | ContactDeleted of int

/// ------------------------------------------------------------
/// Mapping output DTOs
/// ------------------------------------------------------------

module Mapping =
  [<CLIMutable>]
  type IDMap =
    { source_id: int
      target_id: int
      number: string }

/// ------------------------------------------------------------
/// Domain-level error channel (extend as needed)
/// ------------------------------------------------------------
exception DomainStreamException of DomainError

module AsyncSeqResult =
  let unwrapOrThrow (stream: AsyncSeq<Result<'T, DomainError>>) : AsyncSeq<'T> =
    stream
    |> AsyncSeq.map (function
        | Ok value -> value
        | Error err -> raise (DomainStreamException err))

module Alignment =

  let alignEntries<'L, 'R, 'K, 'r when 'K : comparison>
    (keyL          : 'L -> 'K)
    (keyR          : 'R -> 'K)
    (onAligned     : 'L -> 'R -> Result<'r, DomainError>)
    (onMissingLeft : 'R -> Result<'r, DomainError>)   // right-only
    (onMissingRight: 'L -> Result<'r, DomainError>)   // left-only
    (left          : AsyncSeq<Result<'L, DomainError>>)
    (right         : AsyncSeq<Result<'R, DomainError>>)
    : AsyncSeq<Result<'r, DomainError>> =

    let computation () =
      let leftPlain  = AsyncSeqResult.unwrapOrThrow left
      let rightPlain = AsyncSeqResult.unwrapOrThrow right

      NocfoClient.Streams.alignByKey
        keyL
        keyR
        leftPlain
        rightPlain
      |> AsyncSeq.map (function
          | StreamAlignment.Aligned (l, r) -> onAligned l r
          | StreamAlignment.MissingLeft  r -> onMissingLeft  r
          | StreamAlignment.MissingRight l -> onMissingRight l)

    asyncSeq {
      try
        yield! computation ()
      with
      | DomainStreamException err ->
          yield Error err
    }


module DeltaUpdate =

  /// Core loop: fetch current state, diff, then call `handlePatch`.
  /// `handlePatch` returns `Ok (Some result)` on success, `Ok None` to skip silently
  /// (used for dry-run), or `Error` to propagate a failure.
  let run<'Delta, 'Full, 'Patch, 'Result>
    (getId        : 'Delta -> int)
    (fetchCurrent : int -> Async<Result<'Full, DomainError>>)
    (diffToPatch  : 'Full -> 'Delta -> Result<'Patch option, DomainError>)
    (handlePatch  : int -> 'Patch -> Async<Result<'Result option, DomainError>>)
    (deltas       : AsyncSeq<Result<'Delta, DomainError>>)
    : AsyncSeq<Result<'Result, DomainError>> =

    asyncSeq {
      for deltaResult in deltas do
        match deltaResult with
        | Error err ->
            yield Error err
        | Ok delta ->
            let id = getId delta
            let! currentResult = fetchCurrent id
            match currentResult with
            | Error err ->
                yield Error err
            | Ok current ->
                match diffToPatch current delta with
                | Error err ->
                    yield Error err
                | Ok None ->
                    ()
                | Ok (Some patch) ->
                    let! result = handlePatch id patch
                    match result with
                    | Error err        -> yield Error err
                    | Ok (Some value)  -> yield Ok value
                    | Ok None          -> ()
    }


/// ------------------------------------------------------------
/// Contexts
/// ------------------------------------------------------------

/// AccountingContext
/// AccountingContext represents the repository, which at this point is just a HTTP client
/// that allows us to fetch the business and its associated data.

type AccountingOptions = {
  pageSize: int
  retryPolicy:   Option<unit> // XXX: Not implemented yet
  loggingPolicy: Option<unit> // XXX: Not implemented yet
  cachingPolicy: Option<unit> // XXX: Not implemented yet
  dryRun: bool
}

type AccountingContext = {
  http: HttpContext
  options: AccountingOptions
}

/// BusinessContext

// At runtime we bind everything to a business context.
type BusinessContext = {
  ctx:  AccountingContext
  key:  BusinessKey
}

/// ------------------------------------------------------------
/// Accounting operations
/// ------------------------------------------------------------

module Accounting =
  let ofHttp (http: HttpContext) (dryRun: bool) : AccountingContext =
    {
      http = http
      options = {
        pageSize      = 100
        retryPolicy   = None
        loggingPolicy = None
        cachingPolicy = None
        dryRun        = dryRun
      }
    }

///
/// Generic entity operation helpers — shared across Account, Contact, etc.
///

module EntityOps =

  /// Fetch a single entity by integer id, mapping NotFound to a domain error.
  let fetchById<'Full>
    (endpoint   : string -> string -> string)
    (missingErr : int -> DomainError)
    (context    : BusinessContext)
    (id         : int)
    : Async<Result<'Full, DomainError>> =
    async {
      let! result = Http.getJson<'Full> context.ctx.http (endpoint context.key.slug (string id))
      match result with
      | Ok full                           -> return Ok full
      | Error (NotFound _) -> return Error (missingErr id)
      | Error err                         -> return Error (DomainError.Http err)
    }

  /// Diff a delta against its current full state, producing a normalised patch or None.
  let diffToPatch<'Full, 'Delta, 'Patch>
    (entityName   : string)
    (getFullId    : 'Full  -> int)
    (getDeltaId   : 'Delta -> int)
    (getDeltaPatch: 'Delta -> 'Patch)
    (full         : 'Full)
    (delta        : 'Delta)
    : Result<'Patch option, DomainError> =
    let id = getDeltaId delta
    if id <> getFullId full then
      Error (DomainError.Unexpected
               $"Patched {entityName} id {id} does not match hydrated {entityName} id {getFullId full}.")
    else
      let normalised = PatchShape<'Full, 'Patch>.Normalize(full, getDeltaPatch delta)
      if PatchShape<'Full, 'Patch>.HasChanges normalised then Ok (Some normalised)
      else Ok None

  /// Align a paginated API stream against a CSV delta stream and produce commands.
  /// Permissive policy: CSV-only row → error; API-only row → skip.
  let deltasToCommands<'Full, 'Delta, 'Command>
    (getFullId   : 'Full  -> int)
    (getDeltaId  : 'Delta -> int)
    (entityLabel : string)
    (diff        : 'Full -> 'Delta -> Result<'Command option, DomainError>)
    (fulls       : AsyncSeq<Result<'Full,  DomainError>>)
    (deltas      : AsyncSeq<Result<'Delta, DomainError>>)
    : AsyncSeq<Result<'Command, DomainError>> =

    Alignment.alignEntries
      getFullId getDeltaId
      (fun full delta -> Ok (Some (full, delta)))
      (fun missing    -> Error (DomainError.Unexpected
                                  $"Alignment failure: missing {entityLabel} for CSV id {getDeltaId missing}."))
      (fun _          -> Ok None)
      fulls deltas
    |> AsyncSeq.collect (function
        | Error err               -> AsyncSeq.singleton (Error err)
        | Ok None                 -> AsyncSeq.empty
        | Ok (Some (full, delta)) ->
            match diff full delta with
            | Ok (Some cmd) -> AsyncSeq.singleton (Ok cmd)
            | Ok None       -> AsyncSeq.empty
            | Error err     -> AsyncSeq.singleton (Error err))

  /// Core delta-update runner: fetch current state, diff, PATCH if changed.
  let executeDeltaUpdates<'Delta, 'Full, 'Patch, 'Result>
    (endpointOf  : string -> string -> string)
    (wrapUpdated : 'Full -> 'Result)
    (context     : BusinessContext)
    (fetchFull   : BusinessContext -> int -> Async<Result<'Full, DomainError>>)
    (diff        : 'Full -> 'Delta -> Result<'Patch option, DomainError>)
    (getDeltaId  : 'Delta -> int)
    (deltas      : AsyncSeq<Result<'Delta, DomainError>>)
    : AsyncSeq<Result<'Result, DomainError>> =

    let url id = endpointOf context.key.slug (string id)
    let handlePatch id patch =
      if context.ctx.options.dryRun then async {
        eprintfn "[dry-run] PATCH %s %s" (url id) (Newtonsoft.Json.JsonConvert.SerializeObject patch)
        return Ok None
      } else
        Http.patchJson<'Patch, 'Full> context.ctx.http (url id) patch
        |> AsyncResult.mapError DomainError.Http
        |> AsyncResult.map (wrapUpdated >> Some)

    DeltaUpdate.run getDeltaId (fetchFull context) diff handlePatch deltas

///
/// Business module operations
///

module Business =
  let ofContext (context: BusinessContext) : Business =
    Hydratable.Partial (context.key, fetch = fun () -> async {
      let! result =
        Http.getJson<NocfoApi.Types.Business> context.ctx.http (Endpoints.businessBySlug context.key.slug)
      match result with
      | Result.Ok business ->
          let full : BusinessFull =
            { key  = context.key
              meta = { name = business.name; country = Some business.country };
              raw  = business }
          return Ok (Business.Full full)
      | Result.Error httpErr ->
          return Error (DomainError.Http httpErr)
    })

  let ofRaw (raw: NocfoApi.Types.Business) : Business =
    let full : BusinessFull =
      {
        // XXX fixme: what if there are no identifiers or no slug
        key  = { id = raw.identifiers.[0]; slug = defaultArg raw.slug "(none)" }
        meta = { name = raw.name; country = Some raw.country };
        raw  = raw
      }
    Business.Full full

  let hydrate (business: Business) : Async<Result<Business, DomainError>> =
    match business with
    | Full _ -> async.Return (Ok business)
    | Partial (key, fetch) -> fetch ()

///
/// Account module operations
///

module Account =
  let private missingAccountError (id: int) =
    DomainError.Unexpected $"Alignment failure: missing account for CSV id {id}."

  let mkFetch (context: BusinessContext) (id) : unit -> Async<Result<Account, DomainError>> =
    fun () ->
      async {
        let! result =
          Http.getJson<AccountFull> context.ctx.http (Endpoints.accountById context.key.slug id)
        return (Result.map (Account.Full) >> DomainError.asDomain) result
      }

  let fetchFull = EntityOps.fetchById Endpoints.accountById missingAccountError

  let ofRow (context: BusinessContext) (row: AccountRow) : Account =
    Hydratable.Partial (row, fetch = mkFetch context (row.id.ToString()))

  let hydrate (acc: Account) : Async<Result<Account, DomainError>> =
    match acc with
    | Full _ -> async.Return (Ok acc)
    | Partial (_row, fetch) -> fetch ()

  let inline classify< ^Account when ^Account : (member ``type`` : Type92dEnum option) >
    (account: ^Account ) : AccountClass option =
    match account.``type`` with
    | Some Type92dEnum.ASS         -> Some Asset
    | Some Type92dEnum.ASS_DEP     -> Some Asset
    | Some Type92dEnum.ASS_VAT     -> Some Asset
    | Some Type92dEnum.ASS_REC     -> Some Asset
    | Some Type92dEnum.ASS_PAY     -> Some Asset
    | Some Type92dEnum.ASS_DUE     -> Some Asset
    | Some Type92dEnum.LIA         -> Some Liability
    | Some Type92dEnum.LIA_EQU     -> Some Liability
    | Some Type92dEnum.LIA_PRE     -> Some Liability
    | Some Type92dEnum.LIA_DUE     -> Some Liability
    | Some Type92dEnum.LIA_DEB     -> Some Liability
    | Some Type92dEnum.LIA_ACC     -> Some Liability
    | Some Type92dEnum.LIA_VAT     -> Some Liability
    | Some Type92dEnum.REV         -> Some Income
    | Some Type92dEnum.REV_SAL     -> Some Income
    | Some Type92dEnum.REV_NO      -> Some Income
    | Some Type92dEnum.EXP         -> Some Expense
    | Some Type92dEnum.EXP_DEP     -> Some Expense
    | Some Type92dEnum.EXP_NO      -> Some Expense
    | Some Type92dEnum.EXP_50      -> Some Expense
    | Some Type92dEnum.EXP_TAX     -> Some Expense
    | Some Type92dEnum.EXP_TAX_PRE -> Some Expense
    | None  -> None

  let diffToPatch : AccountFull -> AccountDelta -> Result<PatchedAccountRequest option, DomainError> =
    EntityOps.diffToPatch "account" (fun a -> a.id) (fun d -> d.id) (fun d -> d.patch)

  let diffAccount (full: AccountFull) (delta: AccountDelta) : Result<AccountCommand option, DomainError> =
    diffToPatch full delta
    |> Result.map (Option.map (fun patch -> UpdateAccount { id = delta.id; patch = patch }))

  let deltasToCommands
    (accounts : AsyncSeq<Result<AccountFull,  DomainError>>)
    (deltas   : AsyncSeq<Result<AccountDelta, DomainError>>)
    : AsyncSeq<Result<AccountCommand, DomainError>> =
    EntityOps.deltasToCommands (fun (a: AccountFull) -> a.id) (fun (d: AccountDelta) -> d.id) "account" diffAccount accounts deltas

  let executeDeltaUpdates (context: BusinessContext) (deltas: AsyncSeq<Result<AccountDelta, DomainError>>) =
    EntityOps.executeDeltaUpdates
      Endpoints.accountById AccountUpdated context fetchFull diffToPatch (fun d -> d.id) deltas

///
/// Document module operations
///

module Document =
  let ofRaw (raw: DocumentFull) : Document =
    Hydratable.Full raw

  let hydrate (doc: Document) : Async<Result<Document, DomainError>> =
    match doc with
    | Full _ -> async.Return (Ok doc)
    | Partial (_row, fetch) -> fetch ()

///
/// Contact module operations
///

module Contact =
  let private missingContactError (id: int) =
    DomainError.Unexpected $"Alignment failure: missing contact for CSV id {id}."

  let ofRaw (raw: ContactFull) : Contact =
    Hydratable.Full raw

  let hydrate (contact: Contact) : Async<Result<Contact, DomainError>> =
    match contact with
    | Full _ -> async.Return (Ok contact)
    | Partial (_row, fetch) -> fetch ()

  let fetchFull = EntityOps.fetchById Endpoints.contactById missingContactError

  let diffToPatch : ContactFull -> ContactDelta -> Result<PatchedContactRequest option, DomainError> =
    EntityOps.diffToPatch "contact" (fun c -> c.id) (fun d -> d.id) (fun d -> d.patch)

  let diffContact (full: ContactFull) (delta: ContactDelta) : Result<ContactCommand option, DomainError> =
    diffToPatch full delta
    |> Result.map (Option.map (fun patch -> UpdateContact { id = delta.id; patch = patch }))

  let deltasToCommands
    (contacts : AsyncSeq<Result<ContactFull,  DomainError>>)
    (deltas   : AsyncSeq<Result<ContactDelta, DomainError>>)
    : AsyncSeq<Result<ContactCommand, DomainError>> =
    EntityOps.deltasToCommands (fun (c: ContactFull) -> c.id) (fun (d: ContactDelta) -> d.id) "contact" diffContact contacts deltas

  let executeDeltaUpdates (context: BusinessContext) (deltas: AsyncSeq<Result<ContactDelta, DomainError>>) =
    EntityOps.executeDeltaUpdates
      Endpoints.contactById ContactUpdated context fetchFull diffToPatch (fun d -> d.id) deltas

///
/// Streams module operations —— maybe to be folded to the previous modules
///

module Streams =

  let private mapHttpError (stream: AsyncSeq<Result<'T, Http.HttpError>>) : AsyncSeq<Result<'T, DomainError>> =
    stream
    |> AsyncSeq.map (function
        | Ok value -> Ok value
        | Error err -> Error (DomainError.Http err))

  /// Dry-run helper for command streams: logs each command and yields nothing.
  /// 'Result is phantom — no Ok value is ever yielded.
  let private dryRunCommands<'Command, 'Result>
    (describe: 'Command -> string)
    (commands: AsyncSeq<Result<'Command, DomainError>>)
    : AsyncSeq<Result<'Result, DomainError>> =
    asyncSeq {
      for result in commands do
        match result with
        | Error err -> yield Error err
        | Ok command -> eprintfn "[dry-run] %s" (describe command)
    }

  /// Convert a raw stream into a domain stream
  let toDomain< 'Dom, 'Raw >
    (ofRaw: 'Raw -> 'Dom)
    (stream: AsyncSeq<Result<'Raw, HttpError>>)
    : AsyncSeq<Result<'Dom, DomainError>> =
    stream
    |> AsyncSeq.map (Result.map ofRaw >> DomainError.asDomain)

  /// Domain-level stream of businesses, yielding directly Full businesses
  let streamBusinesses (context: AccountingContext) : AsyncSeq<Result<Business, DomainError>> =
    Streams.streamPaginated<PaginatedBusinessList, NocfoApi.Types.Business>
       context.http
       (fun page -> Endpoints.businessList page)
    |> toDomain Business.ofRaw

  /// Domain-level stream of accounts for a given business, yielding lazy Partials
  /// that can be hydrated to Full on demand
  let streamAccounts (context: BusinessContext) : AsyncSeq<Result<Account, DomainError>> =
    Streams.streamPaginated<PaginatedAccountListList, NocfoApi.Types.AccountList>
       context.ctx.http
       (fun page -> Endpoints.accountsBySlugPage context.key.slug page)
    |> toDomain (Account.ofRow context)

  /// Domain-level stream of documents for a given business.
  let streamDocuments (context: BusinessContext) : AsyncSeq<Result<Document, DomainError>> =
    Streams.streamPaginated<PaginatedDocumentListList, DocumentFull>
       context.ctx.http
       (fun page -> Endpoints.documentsBySlugPage context.key.slug page)
    |> toDomain Document.ofRaw

  /// Domain-level stream of contacts for a given business.
  let streamContacts (context: BusinessContext) : AsyncSeq<Result<Contact, DomainError>> =
    Streams.streamPaginated<PaginatedContactList, ContactFull>
       context.ctx.http
       (fun page -> Endpoints.contactsBySlugPage context.key.slug page)
    |> toDomain Contact.ofRaw


  let hydrateAndUnwrap<'Full, 'Partial>
    (entity: AsyncSeq<Result<Hydratable<'Full, 'Partial>, DomainError>>)
    : AsyncSeq<Result<'Full, DomainError>> =
    entity
    |> AsyncSeq.mapAsync (fun result ->
      // TODO: replace with a version using higher-order functions
      async {
        match result with
        | Error e -> return Error e
        | Ok (Full full) -> return Ok full
        | Ok (Partial (_, fetch)) ->
            let! hydrated = fetch ()
            match hydrated with
            | Error e -> return Error e
            | Ok (Full full) -> return Ok full
            | Ok (Partial _) -> return Error (DomainError.Unexpected "Entity could not be hydrated")
      })

  let private executeCommands<'Command, 'Result>
    (context        : BusinessContext)
    (formatDryRun   : 'Command -> string)
    (mapToOperation : 'Command -> unit -> Async<Result<'Result, Http.HttpError>>)
    (commands       : AsyncSeq<Result<'Command, DomainError>>)
    : AsyncSeq<Result<'Result, DomainError>> =
    if context.ctx.options.dryRun then
      dryRunCommands formatDryRun commands
    else
      asyncSeq {
        try
          yield!
            commands
            |> AsyncSeqResult.unwrapOrThrow
            |> AsyncSeq.map mapToOperation
            |> NocfoClient.Streams.streamChanges (fun op -> op ())
            |> mapHttpError
        with
        | DomainStreamException err -> yield Error err
      }

  let executeAccountCommands
    (context: BusinessContext)
    (commands: AsyncSeq<Result<AccountCommand, DomainError>>)
    : AsyncSeq<Result<AccountResult, DomainError>> =

    let formatDryRun = function
      | AccountCommand.CreateAccount payload ->
          sprintf "POST %s %s"
            (Endpoints.accountsBySlug context.key.slug)
            (Newtonsoft.Json.JsonConvert.SerializeObject payload)
      | AccountCommand.UpdateAccount delta ->
          sprintf "PATCH %s %s"
            (Endpoints.accountById context.key.slug (string delta.id))
            (Newtonsoft.Json.JsonConvert.SerializeObject delta.patch)
      | AccountCommand.DeleteAccount id ->
          sprintf "DELETE %s" (Endpoints.accountById context.key.slug (string id))

    let mapToOperation = function
      | AccountCommand.CreateAccount payload ->
          fun () ->
            let req =
              { NocfoApi.Types.AccountRequest.Create(payload.number, [{ key = "fi"; value = payload.name }]) with
                  ``type``         = payload.``type``
                  description      = payload.description
                  opening_balance  = payload.opening_balance }
            Http.postJson<NocfoApi.Types.AccountRequest, AccountFull>
              context.ctx.http (Endpoints.accountsBySlug context.key.slug) req
            |> AsyncResult.map AccountCreated
      | AccountCommand.UpdateAccount delta ->
          fun () ->
            Http.patchJson<PatchedAccountRequest, AccountFull>
              context.ctx.http (Endpoints.accountById context.key.slug (string delta.id)) delta.patch
            |> AsyncResult.map AccountUpdated
      | AccountCommand.DeleteAccount id ->
          fun () ->
            Http.deleteJson<unit> context.ctx.http (Endpoints.accountById context.key.slug (string id))
            |> AsyncResult.map (fun () -> AccountDeleted id)

    executeCommands context formatDryRun mapToOperation commands

  let executeDocumentCommands
    (context: BusinessContext)
    (commands: AsyncSeq<Result<DocumentCommand, DomainError>>)
    : AsyncSeq<Result<DocumentResult, DomainError>> =

    let formatDryRun = function
      | DocumentCommand.CreateDocument payload ->
          sprintf "POST %s %s"
            (Endpoints.documentsBySlug context.key.slug)
            (Newtonsoft.Json.JsonConvert.SerializeObject payload)
      | DocumentCommand.DeleteDocument id ->
          sprintf "DELETE %s" (Endpoints.documentById context.key.slug (string id))

    let mapToOperation = function
      | DocumentCommand.CreateDocument payload ->
          fun () ->
            Http.postJson<DocumentCreatePayload, DocumentFull>
              context.ctx.http (Endpoints.documentsBySlug context.key.slug) payload
            |> AsyncResult.map DocumentCreated
      | DocumentCommand.DeleteDocument id ->
          fun () ->
            Http.deleteJson<unit> context.ctx.http (Endpoints.documentById context.key.slug (string id))
            |> AsyncResult.map (fun () -> DocumentDeleted id)

    executeCommands context formatDryRun mapToOperation commands

  let executeContactCommands
    (context: BusinessContext)
    (commands: AsyncSeq<Result<ContactCommand, DomainError>>)
    : AsyncSeq<Result<ContactResult, DomainError>> =

    let formatDryRun = function
      | ContactCommand.CreateContact payload ->
          sprintf "POST %s %s"
            (Endpoints.contactsBySlug context.key.slug)
            (Newtonsoft.Json.JsonConvert.SerializeObject payload)
      | ContactCommand.UpdateContact delta ->
          sprintf "PATCH %s %s"
            (Endpoints.contactById context.key.slug (string delta.id))
            (Newtonsoft.Json.JsonConvert.SerializeObject delta.patch)
      | ContactCommand.DeleteContact id ->
          sprintf "DELETE %s" (Endpoints.contactById context.key.slug (string id))

    let mapToOperation = function
      | ContactCommand.CreateContact payload ->
          fun () ->
            let req =
              { NocfoApi.Types.ContactRequest.Create(payload.name) with
                  ``type``             = payload.``type``
                  customer_id          = payload.customer_id
                  contact_business_id  = payload.contact_business_id
                  notes                = payload.notes
                  phone_number         = payload.phone_number }
            Http.postJson<NocfoApi.Types.ContactRequest, ContactFull>
              context.ctx.http (Endpoints.contactsBySlug context.key.slug) req
            |> AsyncResult.map ContactCreated
      | ContactCommand.UpdateContact delta ->
          fun () ->
            Http.patchJson<PatchedContactRequest, ContactFull>
              context.ctx.http (Endpoints.contactById context.key.slug (string delta.id)) delta.patch
            |> AsyncResult.map ContactUpdated
      | ContactCommand.DeleteContact id ->
          fun () ->
            Http.deleteJson<unit> context.ctx.http (Endpoints.contactById context.key.slug (string id))
            |> AsyncResult.map (fun () -> ContactDeleted id)

    executeCommands context formatDryRun mapToOperation commands

///
/// BusinessResolver module operations
///

module BusinessResolver =
  let private identifierTypeToken (value: string) =
    Newtonsoft.Json.Linq.JToken.FromObject(value)

  /// Build candidate identifiers from a free-form CLI argument.
  /// We try both 'Y_tunnus' and 'Vat_code' identifiers.
  let private formIdentifierCandidates (input: string) : BusinessIdentifier list =
    let trimmed = input.Trim()

    [ BusinessIdentifier.Create(0, identifierTypeToken "y_tunnus", trimmed)
      BusinessIdentifier.Create(0, identifierTypeToken "vat_code", trimmed) ]

  let private identifiersOverlap (candidates: BusinessIdentifier list) (identifier: BusinessIdentifier) =
    candidates
    |> List.exists (fun candidate ->
         String.Equals(candidate.``type``.ToString(), identifier.``type``.ToString(), StringComparison.OrdinalIgnoreCase) &&
         String.Equals(candidate.value, identifier.value, StringComparison.OrdinalIgnoreCase))

  let private businessMatches candidates (full: BusinessFull) =
    full.raw.identifiers |> List.exists (identifiersOverlap candidates)

  /// Resolve a business identifier string to a BusinessContext.
  /// Streams businesses, hydrates them, filters by identifier match, and returns the first hit.
  let resolve (context: AccountingContext) (identifierString: string) : Async<Result<BusinessContext, DomainError>> =
    async {
      let candidates = formIdentifierCandidates identifierString
      let! firstMatch =
        Streams.streamBusinesses context
        |> Streams.hydrateAndUnwrap
        |> AsyncSeq.filter (businessMatches candidates)
        |> AsyncSeq.tryHead

      match firstMatch with
      | None ->            return Error (DomainError.Unexpected $"No matching business: {identifierString}")
      | Some (Error e) ->  return Error e
      | Some (Ok full) ->  return Ok { ctx = context; key = full.key }
    }
