module SageFs.Tests.WorkerHttpGuardTests

open System
open System.Net.Http
open System.Text
open System.Threading.Tasks
open Expecto
open Expecto.Flip
open SageFs
open SageFs.WorkerProtocol

// ─── HTTP wiring tests ─────────────────────────────────────────────
// The worker HTTP server (SageFs.Host, the F#-executing surface) must be
// guarded the same way the daemon's MCP + dashboard servers are: cross-site /
// non-loopback browser requests get 403 (fail closed), the blanket
// `Access-Control-Allow-Origin: *` on /__sagefs__/reload is gone, and the only
// cross-site surface left (the DevReload SSE stream read by the user's local
// dev app) reflects the requesting loopback origin instead of `*`.
//
// These tests are written against the CURRENT (pre-change) server contract —
// they are the RED half of the TDD loop and must fail until WorkerHttpGuard
// lands.

/// A real worker Kestrel server on an OS-assigned port plus a counter of how
/// many EvalCode messages reached the handler (i.e. passed the guard).
let private startTestServer (executed: int ref) : Task<WorkerHttpTransport.HttpWorkerServer> = task {
  let handler (_msg: WorkerMessage) : Async<WorkerResponse> = async {
    match _msg with
    | WorkerMessage.EvalCode(code, rid) ->
      executed.Value <- executed.Value + 1
      return WorkerResponse.EvalResult(rid, Ok (sprintf "val it : string = \"%s\"" code), [], Map.empty)
    | _ ->
      return WorkerResponse.WorkerError (SageFsError.EvalFailed "unexpected")
  }
  let stateRef = ref HotReloadState.empty
  let projectFiles = [ @"C:\proj\src\Lib.fs"; @"C:\proj\src\Main.fs" ]
  let! (server: WorkerHttpTransport.HttpWorkerServer) =
    WorkerHttpTransport.startServer
      handler stateRef projectFiles (fun () -> WarmupContext.empty)
      (fun () -> fun _tc -> async { return Features.LiveTesting.TestResult.NotRun })
      0
  return server
}

let private httpClient = new HttpClient()

/// Issue one request to the worker. Header arguments model a browser or an
/// attacker: hostHeader overrides the Host header (None = loopback default),
/// origin / secFetchSite set the browser headers (None = absent — curl,
/// editors, and the daemon proxy send none).
let private request
    (server: WorkerHttpTransport.HttpWorkerServer)
    (method: string) (path: string)
    (hostHeader: string option) (origin: string option) (secFetchSite: string option)
    (body: string option)
    : Task<HttpResponseMessage> =
  task {
    use req = new HttpRequestMessage(HttpMethod(method), server.BaseUrl + path)
    match body with
    | Some b -> req.Content <- new StringContent(b, Encoding.UTF8, "application/json")
    | None -> ()
    match hostHeader with
    | Some h -> req.Headers.Host <- h
    | None -> ()
    match origin with
    | Some o -> req.Headers.TryAddWithoutValidation("Origin", o) |> ignore
    | None -> ()
    match secFetchSite with
    | Some s -> req.Headers.TryAddWithoutValidation("Sec-Fetch-Site", s) |> ignore
    | None -> ()
    use cts = new Threading.CancellationTokenSource(TimeSpan.FromSeconds(20.0))
    let! resp = httpClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token)
    return resp
  }

let private status (resp: HttpResponseMessage) = int resp.StatusCode

let private headerValue (resp: HttpResponseMessage) (name: string) : string option =
  match resp.Headers.Contains(name) with
  | true ->
    resp.Headers.GetValues(name)
    |> Seq.map string
    |> Seq.tryHead
  | false -> None

let private disposeServer (server: WorkerHttpTransport.HttpWorkerServer) =
  (server :> IDisposable).Dispose()

[<Tests>]
let workerHttpGuardHttpTests =
  testList "WorkerHttpGuard.http" [

    testTask "POST /eval with cross-site remote browser headers is rejected 403 and does not execute" {
      let executed = ref 0
      let! server = startTestServer executed
      try
        let! resp =
          request server "POST" "/eval" None (Some "http://evil.example.com") (Some "cross-site")
            (Some """{"code":"1+1","replyId":"r1"}""")
        try
          status resp |> Expect.equal "cross-site /eval must be rejected 403" 403
          executed.Value |> Expect.equal "rejected eval must never reach the handler" 0
        finally
          resp.Dispose()
      finally
        disposeServer server
    }

    testTask "POST /eval cross-site from a loopback app origin is rejected 403 (cross-site is SSE-only)" {
      let executed = ref 0
      let! server = startTestServer executed
      try
        let! resp =
          request server "POST" "/eval" None (Some "http://localhost:5173") (Some "cross-site")
            (Some """{"code":"1+1","replyId":"r1"}""")
        try
          status resp |> Expect.equal "even a loopback-origin page must not POST /eval cross-site" 403
          executed.Value |> Expect.equal "rejected eval must never reach the handler" 0
        finally
          resp.Dispose()
      finally
        disposeServer server
    }

    testTask "daemon-proxy POST /eval (loopback Host, no browser headers) still succeeds" {
      let executed = ref 0
      let! server = startTestServer executed
      try
        let! resp =
          request server "POST" "/eval" None None None
            (Some """{"code":"1+1","replyId":"r1"}""")
        try
          status resp |> Expect.equal "the daemon proxy (no Origin/Sec-Fetch-Site) must pass" 200
          executed.Value |> Expect.equal "allowed eval must reach the handler" 1
        finally
          resp.Dispose()
      finally
        disposeServer server
    }

    testTask "POST /eval from a page with a non-loopback Host is rejected 403" {
      let executed = ref 0
      let! server = startTestServer executed
      try
        let! resp =
          request server "POST" "/eval" (Some "sagefs.evil.com:80") (Some "http://localhost:5173") (Some "same-origin")
            (Some """{"code":"1+1","replyId":"r1"}""")
        try
          status resp |> Expect.equal "non-loopback Host must be rejected 403 (DNS rebinding / proxy)" 403
          executed.Value |> Expect.equal "rejected eval must never reach the handler" 0
        finally
          resp.Dispose()
      finally
        disposeServer server
    }

    testTask "GET /__sagefs__/reload no longer emits a wildcard CORS header" {
      let executed = ref 0
      let! server = startTestServer executed
      try
        let! resp = request server "GET" "/__sagefs__/reload" None None None None
        try
          status resp |> Expect.equal "SSE endpoint must stay reachable by local clients" 200
          match headerValue resp "Access-Control-Allow-Origin" with
          | Some v -> v |> Expect.notEqual "wildcard ACAO must be gone" "*"
          | None -> ()  // no Origin sent → no ACAO needed
        finally
          resp.Dispose()
      finally
        disposeServer server
    }

    testTask "GET /__sagefs__/reload from a loopback cross-site origin is allowed and reflects the origin" {
      let executed = ref 0
      let! server = startTestServer executed
      try
        let! resp =
          request server "GET" "/__sagefs__/reload" None (Some "http://localhost:5173") (Some "cross-site") None
        try
          status resp |> Expect.equal "DevReload SSE from the user's local dev app must be allowed" 200
          headerValue resp "Access-Control-Allow-Origin"
          |> Expect.equal "ACAO must echo the specific loopback origin, never *" (Some "http://localhost:5173")
        finally
          resp.Dispose()
      finally
        disposeServer server
    }

    testTask "GET /__sagefs__/reload from a remote (non-loopback) origin is rejected 403" {
      let executed = ref 0
      let! server = startTestServer executed
      try
        let! resp =
          request server "GET" "/__sagefs__/reload" None (Some "http://evil.example.com") (Some "cross-site") None
        try
          status resp |> Expect.equal "remote-origin SSE reads must be rejected 403" 403
        finally
          resp.Dispose()
      finally
        disposeServer server
    }

    testTask "GET /__sagefs__/reload with a non-loopback Host header is rejected 403" {
      let executed = ref 0
      let! server = startTestServer executed
      try
        let! resp =
          request server "GET" "/__sagefs__/reload" (Some "sagefs.evil.com:80") (Some "http://localhost:5173") (Some "cross-site") None
        try
          status resp |> Expect.equal "non-loopback Host on the SSE endpoint must be rejected 403" 403
        finally
          resp.Dispose()
      finally
        disposeServer server
    }

    testTask "POST /hotreload/watch-all from a remote page is rejected and does not mutate state" {
      let executed = ref 0
      let! server = startTestServer executed
      try
        let! resp =
          request server "POST" "/hotreload/watch-all" None (Some "http://evil.example.com") (Some "cross-site")
            (Some "{}")
        try
          status resp |> Expect.equal "cross-site watch-all must be rejected 403" 403
        finally
          resp.Dispose()
        // The state mutation must not have happened: GET /hotreload (daemon-style,
        // no browser headers) reports nothing watched.
        let! check = request server "GET" "/hotreload" None None None None
        try
          status check |> Expect.equal "follow-up GET /hotreload must succeed" 200
          let! body = check.Content.ReadAsStringAsync()
          body |> Expect.stringContains "rejected watch-all must not have mutated watch state" "\"watchedCount\":0"
        finally
          check.Dispose()
      finally
        disposeServer server
    }
  ]
