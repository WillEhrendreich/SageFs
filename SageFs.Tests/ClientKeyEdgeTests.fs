module SageFs.Tests.ClientKeyEdgeTests

open System
open System.Net.Http
open System.Threading
open System.Threading.Channels
open System.Threading.Tasks
open Expecto
open Expecto.Flip
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Hosting
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.Logging
open SageFs
open SageFs.MemberTable

/// The HTTP edge of P13 (`McpServer.useClientKey`), in a real Kestrel on a port of its own: a valid client key becomes the
/// member id and nothing more, a wrong one is a 400 with the reason, and a GET that carries an `Mcp-Session-Id` is a push
/// stream, which makes its member live for exactly as long as it is open.

type private Edge =
  { App: WebApplication
    Liveness: MemberLiveness.Table
    /// One entry per request that finished crossing the edge, written outside the middleware under test, so a stream's
    /// close has been recorded by the time the test reads it.
    Finished: Channel<unit>
    Client: HttpClient }

let private sessionHeader = "Mcp-Session-Id"

let private startEdge () : Task<Edge> =
  task {
    let builder = WebApplication.CreateBuilder [||]
    builder.WebHost.UseUrls "http://127.0.0.1:0" |> ignore
    builder.Logging.ClearProviders() |> ignore
    let app = builder.Build()
    let liveness = MemberLiveness.Table()
    let finished = Channel.CreateUnbounded<unit>()
    app.Use(Func<HttpContext, Func<Task>, Task>(fun _ next ->
      task {
        try
          do! next.Invoke()
        finally
          finished.Writer.TryWrite(()) |> ignore
      }
      :> Task)) |> ignore
    SageFs.Server.McpServer.useClientKey liveness app
    // Behind the edge: a GET is a stream that stays open until the client goes, anything else answers at once.
    app.Run(RequestDelegate(fun ctx ->
      task {
        match HttpMethods.IsGet ctx.Request.Method with
        | false -> ctx.Response.StatusCode <- StatusCodes.Status200OK
        | true ->
          // An SSE comment line, flushed: the headers and the first bytes are on the wire before the client reads them.
          ctx.Response.ContentType <- "text/event-stream"
          do! ctx.Response.WriteAsync ":\n"
          do! ctx.Response.Body.FlushAsync()
          try
            do! Task.Delay(Timeout.Infinite, ctx.RequestAborted)
          with :? OperationCanceledException -> ()
      }
      :> Task))
    do! app.StartAsync()
    let address = app.Urls |> Seq.head
    return { App = app; Liveness = liveness; Finished = finished; Client = new HttpClient(BaseAddress = Uri address) }
  }

let private stopEdge (edge: Edge) : Task =
  task {
    edge.Client.Dispose()
    do! edge.App.StopAsync()
    do! edge.App.DisposeAsync()
  }

let private withEdge (body: Edge -> Task<unit>) : Task<unit> =
  task {
    let! edge = startEdge ()
    let! failure =
      task {
        try
          do! body edge
          return None
        with ex ->
          return Some ex
      }
    do! stopEdge edge
    match failure with
    | Some ex -> System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex).Throw()
    | None -> ()
  }

/// A GET to the edge with these headers; it returns once the headers are back, with the stream still open.
let private openStream (edge: Edge) (headers: (string * string) list) : Task<HttpResponseMessage> =
  task {
    use request = new HttpRequestMessage(HttpMethod.Get, "/")
    for (name, value) in headers do
      request.Headers.Add(name, value)
    return! edge.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead)
  }

/// Close a stream the way a client that goes away does, and wait until the edge has recorded it.
let private closeStream (edge: Edge) (response: HttpResponseMessage) : Task =
  task {
    response.Dispose()
    use cts = new CancellationTokenSource(TestTimeouts.patience)
    let! _ = edge.Finished.Reader.ReadAsync cts.Token
    ()
  }

let private isLive (edge: Edge) (who: MemberId) : bool =
  edge.Liveness.IsLive(MemberId.display who, DateTime.UtcNow, Timeouts.agentActivityFresh)

let private memberOf (key: string) : MemberId =
  match ClientKey.toMemberId key with
  | Result.Ok who -> who
  | Result.Error refusal -> failtestf "the key was refused: %s" (ClientKey.describe refusal)

[<Tests>]
let tests =
  testList "Client key at the HTTP edge (P13)" [

    testTask "WHY - a stream with a client key makes the key's member live while it is open and not after it closes" {
      do!
        withEdge (fun edge ->
          task {
            let key = ClientKey.generate ()
            let who = memberOf key
            let! stream = openStream edge [ ClientKey.headerName, key; sessionHeader, "session-one-0000000000000000" ]
            isLive edge who |> Expect.isTrue "live while the stream is open"
            isLive edge (MemberId.ofConnectionHandle "session-one-0000000000000000")
            |> Expect.isFalse "the session id's own member is not the one that is live"
            do! closeStream edge stream
            isLive edge who |> Expect.isFalse "not live once the stream closed and nothing else was heard"
          })
    }

    testTask "WHY - a bridge that re-initializes is the same live member on its new session" {
      do!
        withEdge (fun edge ->
          task {
            let key = ClientKey.generate ()
            let who = memberOf key
            let! first = openStream edge [ ClientKey.headerName, key; sessionHeader, "session-one-0000000000000000" ]
            let! second = openStream edge [ ClientKey.headerName, key; sessionHeader, "session-two-0000000000000000" ]
            do! closeStream edge first
            isLive edge who |> Expect.isTrue "the old stream is gone and the new one keeps the member live"
            do! closeStream edge second
            isLive edge who |> Expect.isFalse "both closed"
          })
    }

    testTask "WHY - without a client key the member is the session's own, as for any HTTP client" {
      do!
        withEdge (fun edge ->
          task {
            let session = "session-plain-000000000000000"
            let! stream = openStream edge [ sessionHeader, session ]
            isLive edge (MemberId.ofConnectionHandle session) |> Expect.isTrue "live as the session's member"
            do! closeStream edge stream
          })
    }

    testTask "WHY - a request that presents a member token is not counted: the token is the member and has its own lease" {
      do!
        withEdge (fun edge ->
          task {
            let key = ClientKey.generate ()
            let! stream = openStream edge [ ClientKey.headerName, key; sessionHeader, "session-token-00000000000000"; Capability.CapabilityTransport.headerName, "sfmt_whatever" ]
            isLive edge (memberOf key) |> Expect.isFalse "not live through a stream that carries a token"
            do! closeStream edge stream
          })
    }

    testTask "WHY - a POST with a session id is a call, not a stream" {
      do!
        withEdge (fun edge ->
          task {
            let key = ClientKey.generate ()
            use request = new HttpRequestMessage(HttpMethod.Post, "/")
            request.Headers.Add(ClientKey.headerName, key)
            request.Headers.Add(sessionHeader, "session-post-000000000000000")
            use! response = edge.Client.SendAsync request
            int response.StatusCode |> Expect.equal "answered" 200
            isLive edge (memberOf key) |> Expect.isFalse "a call opens no stream"
          })
    }

    testTask "WHY - a wrong client key is refused with a 400 that says why, and counts for nothing" {
      do!
        withEdge (fun edge ->
          task {
            use request = new HttpRequestMessage(HttpMethod.Post, "/")
            request.Headers.Add(ClientKey.headerName, "not-a-key")
            use! response = edge.Client.SendAsync request
            int response.StatusCode |> Expect.equal "refused" 400
            let! body = response.Content.ReadAsStringAsync()
            body |> Expect.stringContains "the answer names the cause" "invalid_client_key"
            body |> Expect.stringContains "and says what is wrong with it" "9 characters"
          })
    }
  ]
