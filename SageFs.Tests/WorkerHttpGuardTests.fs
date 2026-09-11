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

/// A worker server whose handler counts EVERY message that reaches it —
/// i.e. every request the guard let through to a WorkerMessage route.
let private startCountingServer (reached: int ref) : Task<WorkerHttpTransport.HttpWorkerServer> = task {
  let handler (msg: WorkerMessage) : Async<WorkerResponse> = async {
    reached.Value <- reached.Value + 1
    match msg with
    | WorkerMessage.EvalCode(code, rid) ->
      return WorkerResponse.EvalResult(rid, Ok code, [], Map.empty)
    | _ ->
      return WorkerResponse.WorkerError (SageFsError.EvalFailed "reached handler")
  }
  let! (server: WorkerHttpTransport.HttpWorkerServer) =
    WorkerHttpTransport.startServer
      handler (ref HotReloadState.empty) [] (fun () -> WarmupContext.empty)
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

// ─── Route table + pure classification ─────────────────────────────

let private upperFirstHalf (path: string) =
  let n = path.Length / 2
  path.Substring(0, n).ToUpperInvariant() + path.Substring(n)

let private sampleMessages : WorkerMessage list = [
  WorkerMessage.GetStatus "r"
  WorkerMessage.EvalCode("1", "r")
  WorkerMessage.CheckCode("1", "r")
  WorkerMessage.TypeCheckWithSymbols("1", "a.fs", "r")
  WorkerMessage.GetCompletions("S", 1, "r")
  WorkerMessage.CancelEval
  WorkerMessage.LoadScript("a.fsx", "r")
  WorkerMessage.ResetSession "r"
  WorkerMessage.HardResetSession(false, "r")
  WorkerMessage.RunTests([||], 1, "r")
  WorkerMessage.GetTestDiscovery "r"
  WorkerMessage.GetInstrumentationMaps "r"
  WorkerMessage.Shutdown
]

[<Tests>]
let workerRouteTableTests =
  testList "WorkerHttpGuard.routeTable" [

    testCase "every POST route in the table classifies as mutating (any casing)" <| fun _ ->
      let posts =
        WorkerHttpTransport.routes
        |> List.filter (fun r -> WorkerHttpTransport.WorkerRoute.httpMethod r = "POST")
      posts |> Expect.isNonEmpty "the worker declares POST routes"
      for r in posts do
        let path = WorkerHttpTransport.WorkerRoute.path r
        for p in [ path; path.ToUpperInvariant(); upperFirstHalf path ] do
          WorkerHttpTransport.classify "POST" p
          |> Expect.equal (sprintf "POST %s must be mutating" p) WorkerHttpTransport.RouteAccess.Mutating

    testCase "the route table declares the eval/reset/test/completion routes as mutating" <| fun _ ->
      for path in [ "/eval"; "/hard-reset"; "/run-tests"; "/run-tests-stream"; "/completions"; "/shutdown"; "/load-script" ] do
        WorkerHttpTransport.routes
        |> List.tryFind (fun r -> WorkerHttpTransport.WorkerRoute.path r = path)
        |> Option.map WorkerHttpTransport.WorkerRoute.access
        |> Expect.equal (sprintf "%s must be declared mutating" path) (Some WorkerHttpTransport.RouteAccess.Mutating)

    testProperty "any non-GET/HEAD method is mutating on any path" <| fun (m: string) (p: string) ->
      let m = match String.IsNullOrWhiteSpace m with | true -> "POST" | false -> m.Trim()
      let p = match String.IsNullOrEmpty p with | true -> "/" | false -> p
      match m.ToUpperInvariant() with
      | "GET" | "HEAD" -> ()
      | _ ->
        WorkerHttpTransport.classify m p
        |> Expect.equal (sprintf "%s %s must be mutating" m p) WorkerHttpTransport.RouteAccess.Mutating

    testCase "GET routes keep their declared access under any casing; unknown GETs are read-only" <| fun _ ->
      for r in WorkerHttpTransport.routes do
        match WorkerHttpTransport.WorkerRoute.httpMethod r with
        | "GET" ->
          let path = WorkerHttpTransport.WorkerRoute.path r
          for p in [ path; path.ToUpperInvariant(); path + "/" ] do
            WorkerHttpTransport.classify "GET" p
            |> Expect.equal (sprintf "GET %s keeps its declared access" p) (WorkerHttpTransport.WorkerRoute.access r)
        | _ -> ()
      WorkerHttpTransport.classify "GET" "/no-such-route"
      |> Expect.equal "an unknown GET is read-only (it 404s)" WorkerHttpTransport.RouteAccess.ReadOnly

    testCase "HttpWorkerClient only targets routes declared in the worker table" <| fun _ ->
      let declared =
        WorkerHttpTransport.routes
        |> List.map (fun r -> WorkerHttpTransport.WorkerRoute.httpMethod r, WorkerHttpTransport.WorkerRoute.path r)
        |> Set.ofList
      for msg in sampleMessages do
        let m, pathWithQuery, _ = HttpWorkerClient.toRoute msg
        let path = pathWithQuery.Split('?').[0]
        declared |> Set.contains (m, path)
        |> Expect.isTrue (sprintf "client route %s %s must be declared" m path)
      declared |> Set.contains ("POST", "/run-tests-stream")
      |> Expect.isTrue "the streaming test route the client posts to must be declared"

    testCase "decide rejects every POST route cross-site, even from a loopback origin" <| fun _ ->
      for r in WorkerHttpTransport.routes do
        match WorkerHttpTransport.WorkerRoute.httpMethod r with
        | "POST" ->
          let path = WorkerHttpTransport.WorkerRoute.path r
          match WorkerHttpTransport.decide "POST" path None (Some "cross-site") (Some "http://localhost:5173") with
          | WorkerHttpTransport.GuardVerdict.Reject _ -> ()
          | other -> failtestf "cross-site POST %s must be rejected, got %A" path other
        | _ -> ()

    testCase "decide allows the daemon proxy (no browser headers) on every route" <| fun _ ->
      for r in WorkerHttpTransport.routes do
        WorkerHttpTransport.decide (WorkerHttpTransport.WorkerRoute.httpMethod r) (WorkerHttpTransport.WorkerRoute.path r) (Some "127.0.0.1:5000") None None
        |> Expect.equal "no-header loopback requests pass" WorkerHttpTransport.GuardVerdict.Allow

    testCase "decide reflects a loopback origin on the DevReload stream and rejects remote ones" <| fun _ ->
      WorkerHttpTransport.decide "GET" "/__sagefs__/reload" None (Some "cross-site") (Some "http://localhost:5173")
      |> Expect.equal "loopback dev app may read the stream" (WorkerHttpTransport.GuardVerdict.AllowCrossOrigin "http://localhost:5173")
      match WorkerHttpTransport.decide "GET" "/__sagefs__/reload" None (Some "cross-site") (Some "http://evil.example.com") with
      | WorkerHttpTransport.GuardVerdict.Reject _ -> ()
      | other -> failtestf "remote origin on the stream must be rejected, got %A" other
  ]

// ─── Mutating-route classification ─────────────────────────────────
// The strong check must be driven by the HTTP METHOD, not a hand-kept path
// prefix list: every non-GET/HEAD request is mutating. The discriminating
// attacker is a page on a LOOPBACK origin (the user's own dev app, or any
// localhost page) sending Sec-Fetch-Site: cross-site — the read-only branch
// lets loopback origins through, so any POST that lands there can execute.

let private crossSitePosts = [
  "/eval", """{"code":"1+1","replyId":"r1"}"""
  "/hard-reset", """{"rebuild":false,"replyId":"r1"}"""
  "/run-tests", """{"tests":[],"maxParallelism":1,"replyId":"r1"}"""
  "/run-tests-stream", """{"tests":[],"maxParallelism":1}"""
  "/completions", """{"code":"Sys","cursorPos":3,"replyId":"r1"}"""
  // ASP.NET routing is case-insensitive: /EVAL reaches the /eval handler.
  "/EVAL", """{"code":"1+1","replyId":"r1"}"""
  "/Hard-Reset", """{"rebuild":false,"replyId":"r1"}"""
  // Not worker routes today (run/stop-app are daemon-side EvalCode calls) —
  // a POST the table does not know must still get the strong check.
  "/run-app", "{}"
  "/stop-app", "{}"
]

[<Tests>]
let workerHttpGuardMutatingTests =
  testList "WorkerHttpGuard.mutating" [
    for (path, body) in crossSitePosts do
      testTask (sprintf "POST %s cross-site from a loopback origin is rejected 403 and never reaches the handler" path) {
        let reached = ref 0
        let! server = startCountingServer reached
        try
          let! resp =
            request server "POST" path None (Some "http://localhost:5173") (Some "cross-site") (Some body)
          try
            status resp |> Expect.equal (sprintf "cross-site POST %s must be rejected 403" path) 403
            reached.Value |> Expect.equal (sprintf "rejected POST %s must never reach the handler" path) 0
          finally
            resp.Dispose()
        finally
          disposeServer server
      }

    testTask "POST /eval with Sec-Fetch-Site cross-site and no Origin is rejected 403" {
      let reached = ref 0
      let! server = startCountingServer reached
      try
        let! resp =
          request server "POST" "/EVAL" None None (Some "cross-site") (Some """{"code":"1+1","replyId":"r1"}""")
        try
          status resp |> Expect.equal "cross-site POST without Origin must be rejected 403" 403
          reached.Value |> Expect.equal "rejected eval must never reach the handler" 0
        finally
          resp.Dispose()
      finally
        disposeServer server
    }

    testTask "the server maps exactly the declared route table (no route bypasses classification)" {
      let reached = ref 0
      let! (server: WorkerHttpTransport.HttpWorkerServer) = startCountingServer reached
      try
        let mapped = server.MappedRoutes |> Set.ofList
        let declared =
          WorkerHttpTransport.routes
          |> List.map (fun r -> WorkerHttpTransport.WorkerRoute.httpMethod r, WorkerHttpTransport.WorkerRoute.path r)
          |> Set.ofList
        Set.difference mapped declared
        |> Expect.isEmpty "every mapped endpoint must be declared in WorkerHttpTransport.routes"
        Set.difference declared mapped
        |> Expect.isEmpty "every declared route must actually be mapped"
      finally
        disposeServer server
    }

    testTask "same-origin loopback POST /eval still succeeds" {
      let reached = ref 0
      let! server = startCountingServer reached
      try
        let! resp =
          request server "POST" "/eval" None (Some server.BaseUrl) (Some "same-origin")
            (Some """{"code":"1+1","replyId":"r1"}""")
        try
          status resp |> Expect.equal "same-origin loopback eval must pass" 200
          reached.Value |> Expect.equal "allowed eval must reach the handler" 1
        finally
          resp.Dispose()
      finally
        disposeServer server
    }
  ]
