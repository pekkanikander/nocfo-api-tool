module HttpErrorTests

open System
open System.Net
open System.Net.Http
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Xunit
open Swensen.Unquote
open NocfoClient
open NocfoClient.Http

/// Fails the n:th request with the given exception; succeeds otherwise.
type private FlakyHandler(failWith: int -> exn option) =
    inherit HttpMessageHandler()

    let mutable calls = 0

    member _.Calls = calls

    override _.SendAsync(_request: HttpRequestMessage, _cancellationToken: CancellationToken) =
        calls <- calls + 1
        match failWith calls with
        | Some ex -> Task.FromException<HttpResponseMessage>(ex)
        | None ->
            let response = new HttpResponseMessage(HttpStatusCode.OK)
            response.Content <- new StringContent("{}")
            Task.FromResult response

let private httpContext (handler: HttpMessageHandler) =
    let client = new HttpClient(handler)
    client.BaseAddress <- Uri("https://example.test/v1")
    Http.ofHttpClient client "test-token" false

let private request () =
    new HttpRequestMessage(HttpMethod.Get, Uri "https://example.test/v1/business/")

let private alwaysFail (ex: exn) = new FlakyHandler(fun _ -> Some ex)

[<Fact>]
let ``Connection failure becomes a Transport error`` () =
    let handler = alwaysFail (HttpRequestException("Connection refused") :> exn)
    let result = Http.send (httpContext handler) (request ()) |> Async.RunSynchronously
    let classified =
        match result with
        | Error (Transport (_, message)) -> message
        | other -> sprintf "%A" other
    test <@ classified = "Connection refused" @>

[<Fact>]
let ``Timeout becomes a Timeout error`` () =
    let handler = alwaysFail (TaskCanceledException() :> exn)
    let result = Http.send (httpContext handler) (request ()) |> Async.RunSynchronously
    let classified =
        match result with
        | Error (Timeout url) -> url.AbsolutePath
        | other -> sprintf "%A" other
    test <@ classified = "/v1/business/" @>

[<Fact>]
let ``Transport failures are retried`` () =
    let handler = new FlakyHandler(fun n -> if n = 1 then Some (HttpRequestException("Connection refused") :> exn) else None)
    let result = Http.getJson<JsonElement> (httpContext handler) "/business/" |> Async.RunSynchronously
    let succeeded = Result.isOk result
    let calls = handler.Calls
    test <@ succeeded @>
    test <@ calls = 2 @>
