module SageFs.VisualStudio.Core.Tests.SageFsClientVersionTests

open System.Net
open System.Net.Http
open System.Text
open System.Threading
open System.Threading.Tasks
open Xunit
open FsUnit.Xunit
open SageFs.VisualStudio.Core

/// A minimal HttpMessageHandler stub that returns a fixed response.
type MockHttpMessageHandler(statusCode: HttpStatusCode, responseBody: string) =
  inherit HttpMessageHandler()
  override _.SendAsync(_, _) =
    let resp = new HttpResponseMessage(statusCode)
    resp.Content <- new StringContent(responseBody, Encoding.UTF8, "application/json")
    Task.FromResult(resp)

/// An HttpMessageHandler stub that always throws, simulating an unreachable daemon.
type ThrowingHttpMessageHandler() =
  inherit HttpMessageHandler()
  override _.SendAsync(_, _) =
    raise (System.Net.Http.HttpRequestException "Connection refused")
    Task.FromResult(Unchecked.defaultof<_>)

type RoutedResponse =
  | Json of HttpStatusCode * string
  | Failure of exn

/// A route-aware HttpMessageHandler stub for testing endpoint fallbacks.
type RoutingHttpMessageHandler(routes: Map<string, RoutedResponse>) =
  inherit HttpMessageHandler()

  override _.SendAsync(request, _) =
    let path = request.RequestUri.AbsolutePath

    match routes |> Map.tryFind path with
    | Some (Json (statusCode, responseBody)) ->
      let resp = new HttpResponseMessage(statusCode)
      resp.Content <- new StringContent(responseBody, Encoding.UTF8, "application/json")
      Task.FromResult(resp)
    | Some (Failure ex) ->
      raise ex
      Task.FromResult(Unchecked.defaultof<_>)
    | None ->
      let resp = new HttpResponseMessage(HttpStatusCode.NotFound)
      resp.Content <- new StringContent("""{"error":"not found"}""", Encoding.UTF8, "application/json")
      Task.FromResult(resp)

let private ct = CancellationToken.None

// ── GetVersionAsync ───────────────────────────────────────────────────────────

[<Fact>]
let ``GetVersionAsync returns Ok with apiVersion from response`` () =
  task {
    let handler = new MockHttpMessageHandler(HttpStatusCode.OK, """{"apiVersion":1,"version":"1.0.0","server":"sagefs"}""")
    use client = new SageFsClient(handler)
    let! result = client.GetVersionAsync(ct)
    match result with
    | Ok v -> v |> should equal 1
    | Error e -> failwith (sprintf "Expected Ok but got Error: %s" e)
  }

[<Fact>]
let ``GetVersionAsync returns Ok with different apiVersion`` () =
  task {
    let handler = new MockHttpMessageHandler(HttpStatusCode.OK, """{"apiVersion":2,"version":"2.0.0","server":"sagefs"}""")
    use client = new SageFsClient(handler)
    let! result = client.GetVersionAsync(ct)
    match result with
    | Ok v -> v |> should equal 2
    | Error e -> failwith (sprintf "Expected Ok but got Error: %s" e)
  }

[<Fact>]
let ``GetVersionAsync returns Ok with -1 when apiVersion field is missing`` () =
  task {
    let handler = new MockHttpMessageHandler(HttpStatusCode.OK, """{"version":"1.0.0","server":"sagefs"}""")
    use client = new SageFsClient(handler)
    let! result = client.GetVersionAsync(ct)
    match result with
    | Ok v -> v |> should equal -1
    | Error e -> failwith (sprintf "Expected Ok but got Error: %s" e)
  }

[<Fact>]
let ``GetVersionAsync returns Error when daemon is unreachable`` () =
  task {
    let handler = new ThrowingHttpMessageHandler()
    use client = new SageFsClient(handler)
    let! result = client.GetVersionAsync(ct)
    match result with
    | Error msg -> msg |> should equal "Could not reach SageFs daemon."
    | Ok _ -> failwith "Expected Error but got Ok"
  }

// ── CheckVersionAsync ─────────────────────────────────────────────────────────

[<Fact>]
let ``CheckVersionAsync returns Ok when apiVersion matches ExpectedApiVersion`` () =
  task {
    let json = sprintf """{"apiVersion":%d}""" Constants.ExpectedApiVersion
    let handler = new MockHttpMessageHandler(HttpStatusCode.OK, json)
    use client = new SageFsClient(handler)
    let! result = client.CheckVersionAsync(ct)
    match result with
    | Ok () -> ()
    | Error e -> failwith (sprintf "Expected Ok but got Error: %s" e)
  }

[<Fact>]
let ``CheckVersionAsync returns Error with message when version mismatches`` () =
  task {
    let handler = new MockHttpMessageHandler(HttpStatusCode.OK, """{"apiVersion":99}""")
    use client = new SageFsClient(handler)
    let! result = client.CheckVersionAsync(ct)
    match result with
    | Error msg ->
      msg |> should haveSubstring "incompatible"
      msg |> should haveSubstring (sprintf "Expected apiVersion=%d" Constants.ExpectedApiVersion)
      msg |> should haveSubstring "got apiVersion=99"
      msg |> should haveSubstring "dotnet tool update --global SageFs"
    | Ok _ -> failwith "Expected Error but got Ok"
  }

[<Fact>]
let ``CheckVersionAsync returns Error when daemon is unreachable`` () =
  task {
    let handler = new ThrowingHttpMessageHandler()
    use client = new SageFsClient(handler)
    let! result = client.CheckVersionAsync(ct)
    match result with
    | Error msg -> msg |> should equal "Could not reach SageFs daemon."
    | Ok _ -> failwith "Expected Error but got Ok"
  }

[<Fact>]
let ``CheckVersionAsync error message mentions restart Visual Studio`` () =
  task {
    let handler = new MockHttpMessageHandler(HttpStatusCode.OK, """{"apiVersion":0}""")
    use client = new SageFsClient(handler)
    let! result = client.CheckVersionAsync(ct)
    match result with
    | Error msg -> msg |> should haveSubstring "restart Visual Studio"
    | Ok _ -> failwith "Expected Error but got Ok"
  }

[<Fact>]
let ``CheckVersionAsync uses health apiVersion when version endpoint is unavailable`` () =
  task {
    let routes =
      Map.ofList [
        "/health",
        Json (
          HttpStatusCode.OK,
          sprintf """{"apiVersion":%d,"features":["live-testing"]}""" Constants.ExpectedApiVersion
        )
        "/version",
        Failure (System.Net.Http.HttpRequestException "version endpoint unavailable")
      ]

    let handler = new RoutingHttpMessageHandler(routes)
    use client = new SageFsClient(handler)
    let! result = client.CheckVersionAsync(ct)

    match result with
    | Ok () -> ()
    | Error e -> failwith (sprintf "Expected Ok but got Error: %s" e)
  }

[<Fact>]
let ``CheckVersionAsync prefers health apiVersion when health and version disagree`` () =
  task {
    let routes =
      Map.ofList [
        "/health", Json (HttpStatusCode.OK, """{"apiVersion":99,"features":["live-testing"]}""")
        "/version", Json (HttpStatusCode.OK, sprintf """{"apiVersion":%d}""" Constants.ExpectedApiVersion)
      ]

    let handler = new RoutingHttpMessageHandler(routes)
    use client = new SageFsClient(handler)
    let! result = client.CheckVersionAsync(ct)

    match result with
    | Error msg ->
      msg |> should haveSubstring (sprintf "Expected apiVersion=%d" Constants.ExpectedApiVersion)
      msg |> should haveSubstring "got apiVersion=99"
    | Ok _ -> failwith "Expected Error but got Ok"
  }

[<Fact>]
let ``CheckVersionAsync falls back to version endpoint when health endpoint is unavailable`` () =
  task {
    let routes =
      Map.ofList [
        "/health", Failure (System.Net.Http.HttpRequestException "health endpoint unavailable")
        "/version", Json (HttpStatusCode.OK, sprintf """{"apiVersion":%d}""" Constants.ExpectedApiVersion)
      ]

    let handler = new RoutingHttpMessageHandler(routes)
    use client = new SageFsClient(handler)
    let! result = client.CheckVersionAsync(ct)

    match result with
    | Ok () -> ()
    | Error e -> failwith (sprintf "Expected Ok but got Error: %s" e)
  }
