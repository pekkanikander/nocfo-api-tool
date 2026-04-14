module EntityOpsTests

open System
open System.Net
open System.Net.Http
open System.Threading
open System.Threading.Tasks
open Xunit
open Swensen.Unquote
open FSharp.Control
open Nocfo.Domain
open NocfoApi.Types
open NocfoApi.Http
open NocfoClient

type private StubHandler(respond: HttpRequestMessage -> HttpResponseMessage) =
    inherit HttpMessageHandler()

    override _.SendAsync(request: HttpRequestMessage, _cancellationToken: CancellationToken) =
        Task.FromResult(respond request)

let private httpContext (handler: HttpMessageHandler) =
    let client = new HttpClient(handler)
    client.BaseAddress <- Uri("https://example.test/v1")
    Http.ofHttpClient client "test-token"

let private businessContext dryRun handler =
    { ctx =
        { http = httpContext handler
          options =
            { pageSize = 100
              retryPolicy = None
              loggingPolicy = None
              cachingPolicy = None
              dryRun = dryRun } }
      key =
        { id = BusinessIdentifier.Create(0, Newtonsoft.Json.Linq.JToken.FromObject("y_tunnus"), "1234567-8")
          slug = "acme" } }

let private jsonResponse statusCode body =
    let response = new HttpResponseMessage(statusCode)
    response.Content <- new StringContent(body)
    response

let private makeAccount (id: int) (name: string) (number: string) : AccountFull =
    Account.Create(
        id = id,
        created_at = DateTimeOffset.UtcNow,
        updated_at = DateTimeOffset.UtcNow,
        number = number,
        padded_number = int number,
        name = name,
        name_translations = [],
        header_path = [],
        default_vat_rate = 0.0,
        is_shown = true,
        balance = 0.0f,
        is_used = false
    )

let private emptyPatch : PatchedAccountRequest = PatchedAccountRequest.Create()

[<CLIMutable>]
type private TestFull =
    { id: int
      value: string }

[<CLIMutable>]
type private TestDelta =
    { id: int
      value: string }

[<Fact>]
let ``fetchById returns decoded entity on success`` () =
    let account = makeAccount 42 "Revenue" "3000"
    let handler =
        new StubHandler(fun request ->
            test <@ request.Method = HttpMethod.Get @>
            test <@ request.RequestUri.AbsoluteUri = "https://example.test/v1/businesses/acme/accounts/42" @>
            jsonResponse HttpStatusCode.OK (Serializer.serialize account))

    let context = businessContext false handler
    let result =
        EntityOps.fetchById<AccountFull>
            (fun slug id -> $"/businesses/{slug}/accounts/{id}")
            (fun id -> DomainError.Unexpected $"missing {id}")
            context
            42
        |> Async.RunSynchronously

    match result with
    | Ok fetched -> test <@ fetched.id = 42 @>
    | Error err -> Assert.Fail $"Expected Ok, got %A{err}"

[<Fact>]
let ``fetchById maps 404 to the provided domain error`` () =
    let handler =
        new StubHandler(fun _ ->
            jsonResponse HttpStatusCode.NotFound """{"detail":"not found"}""")

    let context = businessContext false handler
    let result =
        EntityOps.fetchById<AccountFull>
            (fun slug id -> $"/businesses/{slug}/accounts/{id}")
            (fun id -> DomainError.Unexpected $"missing {id}")
            context
            7
        |> Async.RunSynchronously

    test <@ result = Error (DomainError.Unexpected "missing 7") @>

[<Fact>]
let ``diffToPatch normalizes unchanged fields away`` () =
    let full = makeAccount 1 "Revenue" "1000"
    let delta = AccountDelta.Create(1, { emptyPatch with number = Some "1000" })

    let result =
        EntityOps.diffToPatch "account" (fun (a: AccountFull) -> a.id) (fun (d: AccountDelta) -> d.id) (fun d -> d.patch) full delta

    test <@ result = Ok None @>

[<Fact>]
let ``diffToPatch rejects mismatched ids`` () =
    let full = makeAccount 1 "Revenue" "1000"
    let delta = AccountDelta.Create(99, emptyPatch)

    match EntityOps.diffToPatch "account" (fun (a: AccountFull) -> a.id) (fun (d: AccountDelta) -> d.id) (fun d -> d.patch) full delta with
    | Error (DomainError.Unexpected message) ->
        test <@ message.Contains("Patched account id 99") @>
    | other ->
        Assert.Fail $"Expected mismatched-id error, got %A{other}"

[<Fact>]
let ``deltasToCommands emits commands for matches and skips API-only rows`` () =
    let diff (full: TestFull) (delta: TestDelta) =
        if full.value = delta.value then Ok None
        else Ok (Some $"{full.id}:{delta.value}")

    let results =
        EntityOps.deltasToCommands
            (fun (full: TestFull) -> full.id)
            (fun (delta: TestDelta) -> delta.id)
            "test entity"
            diff
            (AsyncSeq.ofSeq [ Ok { id = 1; value = "old" }; Ok { id = 2; value = "skip" } ])
            (AsyncSeq.ofSeq [ Ok { id = 1; value = "new" } ])
        |> AsyncSeq.toListSynchronously

    test <@ results = [ Ok "1:new" ] @>

[<Fact>]
let ``deltasToCommands reports CSV-only rows as errors`` () =
    let diff (_: TestFull) (_: TestDelta) = Ok (Some "unused")

    let results =
        EntityOps.deltasToCommands
            (fun (full: TestFull) -> full.id)
            (fun (delta: TestDelta) -> delta.id)
            "test entity"
            diff
            (AsyncSeq.ofSeq [ Ok { id = 1; value = "left" } ])
            (AsyncSeq.ofSeq [ Ok { id = 2; value = "right" } ])
        |> AsyncSeq.toListSynchronously

    match results with
    | [ Error (DomainError.Unexpected message) ] ->
        test <@ message = "Alignment failure: missing test entity for CSV id 2." @>
    | other ->
        Assert.Fail $"Expected one alignment error, got %A{other}"

[<Fact>]
let ``executeDeltaUpdates fetches diffs patches and wraps the updated entity`` () =
    let full = makeAccount 5 "Revenue" "1000"
    let updated = makeAccount 5 "Revenue" "9999"
    let delta = AccountDelta.Create(5, { emptyPatch with number = Some "9999" })
    let mutable patchRequests = 0

    let handler =
        new StubHandler(fun request ->
            patchRequests <- patchRequests + 1
            test <@ request.Method = HttpMethod.Patch @>
            test <@ request.RequestUri.AbsoluteUri = "https://example.test/v1/businesses/acme/accounts/5" @>
            jsonResponse HttpStatusCode.OK (Serializer.serialize updated))

    let context = businessContext false handler
    let results =
        EntityOps.executeDeltaUpdates
            (fun slug id -> $"/businesses/{slug}/accounts/{id}")
            (fun (account: AccountFull) -> account.number)
            context
            (fun _ id -> async { return Ok (if id = 5 then full else failwith "unexpected id") })
            (EntityOps.diffToPatch "account" (fun (a: AccountFull) -> a.id) (fun (d: AccountDelta) -> d.id) (fun d -> d.patch))
            (fun (d: AccountDelta) -> d.id)
            (AsyncSeq.singleton (Ok delta))
        |> AsyncSeq.toListSynchronously

    test <@ patchRequests = 1 @>
    test <@ results = [ Ok "9999" ] @>
