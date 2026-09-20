module SageFs.Tests.OriginGuardOutcomeTests

/// OUTCOME gate for the loopback origin/CSRF guard (outcome-gate-sweep.md Gap H).
///
/// WHAT WAS ALREADY COVERED, AND WHY IT IS NOT ENOUGH.
/// `HttpOriginGuardTests.fs` proves the pure decision function
/// `HttpOriginGuard.decide` classifies every request shape correctly. It runs
/// in the default suite and it is a good test. It cannot fail when the
/// middleware is not INSTALLED — and installation is the part that carries the
/// risk: `useOriginGuard` is called from exactly two places
/// (`McpServer.fs` for the MCP listener, `DaemonMode.fs` for the dashboard
/// listener), each one line, each easy to drop or to move after `UseRouting`
/// where it can no longer stop a route from running. Today a regression that
/// deletes either line ships green, and the standing audit finding is that any
/// web page the user has open could then create a session rooted at a path it
/// chooses and evaluate arbitrary F# as the logged-in user.
///
/// SO THIS GATE ASSERTS THE OUTCOME: a real cross-origin HTTP request, sent by
/// a real client to a real daemon, does not reach a mutating endpoint — and
/// the identical request WITHOUT browser headers does. Header-only difference,
/// same path, same body, so a 403/404 split isolates the middleware from the
/// route and neither result can be produced by the other.
///
/// COST: one daemon, no session, no warmup, no project load — the daemon is
/// started with `--no-resume` on an isolated SAGEFS_DATA_DIR and every case
/// shares it. Measured wall clock for the whole list: ~4s.

open System
open System.Diagnostics
open System.Net
open System.Net.Http
open System.Net.Sockets
open System.Text
open System.Text.Json
open Expecto
open Expecto.Flip

module Harness = SageFs.Tests.HttpApiIntegrationTests
module Integration = SageFs.Tests.TestInfrastructure.Integration
module Infra = SageFs.Tests.TestInfrastructure

// ─── The daemon under test ──────────────────────────────────────────────────

/// The daemon binds the MCP port AND the dashboard on port+1, so a port is
/// only usable when BOTH are free — `reserveLoopbackPort` proves one.
let private bothFree (port: int) =
  try
    use a = new TcpListener(IPAddress.Loopback, port)
    a.Start()
    use b = new TcpListener(IPAddress.Loopback, port + 1)
    b.Start()
    true
  with :? SocketException -> false

let private reserveDaemonPort () =
  let rec attempt (left: int) =
    let candidate = Harness.reserveLoopbackPort (Some (39100 + Random.Shared.Next 300))
    match bothFree candidate, left with
    | true, _ -> candidate
    | false, 0 -> failwith "OriginGuardOutcomeTests: no free (port, port+1) pair on loopback"
    | false, n -> attempt (n - 1)
  attempt 20

let private mcpPort = reserveDaemonPort ()
let private dashboardPort = mcpPort + 1

/// A directory the "attacker" would point a session at. Real and readable, so
/// a create that got through would genuinely succeed — the rejection cannot be
/// mistaken for a path-validation failure.
let private attackerTargetDir =
  IO.Directory.CreateTempSubdirectory("sagefs-origin-guard-").FullName

let private daemon =
  lazy (
    // --no-resume + a fresh SAGEFS_DATA_DIR (set by startDaemonWithArgs) mean
    // this daemon starts with exactly zero sessions, so "no session was
    // created" is an exact assertion rather than a delta.
    Harness.startDaemonWithArgs mcpPort Harness.repoRoot [ "--no-resume" ]
    |> Async.AwaitTask
    |> Async.RunSynchronously)

let private daemonClient () = snd daemon.Value
let private daemonProc () = fst daemon.Value

do AppDomain.CurrentDomain.ProcessExit.Add(fun _ ->
  if daemon.IsValueCreated then
    let proc, client = daemon.Value
    client.Dispose()
    Harness.killDaemon proc
  try IO.Directory.Delete(attackerTargetDir, true) with _ -> ())

// ─── Sending a request with chosen browser headers ──────────────────────────

/// A request shaped the way a browser would shape it. Header fields are
/// `string option` because an HTTP header is genuinely present-or-absent at
/// the wire boundary — this mirrors `HttpOriginGuard.Request` exactly.
type private Probe = {
  Method: HttpMethod
  Path: string
  Origin: string option
  SecFetchSite: string option
  HostHeader: string option
  ContentType: string option
  Body: string option
}

let private probe (httpMethod: HttpMethod) (path: string) = {
  Method = httpMethod
  Path = path
  Origin = None
  SecFetchSite = None
  HostHeader = None
  ContentType = None
  Body = None
}

let private jsonBody (payload: obj) (p: Probe) =
  { p with Body = Some (JsonSerializer.Serialize payload); ContentType = Some "application/json" }

/// Send `p` to one of the daemon's two listeners and return (status, body).
/// A fresh HttpClient per probe: HttpClient stamps its own Host from the
/// BaseAddress, and these probes must control that header.
let private sendTo (port: int) (p: Probe) = task {
  use client = new HttpClient()
  client.Timeout <- TimeSpan.FromSeconds 20.0
  use request = new HttpRequestMessage(p.Method, Uri(sprintf "http://127.0.0.1:%d%s" port p.Path))
  match p.Body with
  | Some body ->
    let content = new StringContent(body, Encoding.UTF8)
    content.Headers.ContentType <-
      Headers.MediaTypeHeaderValue.Parse(defaultArg p.ContentType "application/json")
    request.Content <- content
  | None -> ()
  p.Origin |> Option.iter (fun o -> request.Headers.TryAddWithoutValidation("Origin", o) |> ignore)
  p.SecFetchSite |> Option.iter (fun s -> request.Headers.TryAddWithoutValidation("Sec-Fetch-Site", s) |> ignore)
  p.HostHeader |> Option.iter (fun h -> request.Headers.Host <- h)
  let! response = client.SendAsync request
  let! body = response.Content.ReadAsStringAsync()
  return int response.StatusCode, body
}

let private sendToMcp (p: Probe) = sendTo mcpPort p
let private sendToDashboard (p: Probe) = sendTo dashboardPort p

/// The harness hands the daemon back once `/health` answers on the MCP port —
/// which says nothing about the dashboard. The daemon binds the two listeners
/// independently (`startDashboardServer` is a separate WebApplication started
/// after the MCP host, and it SWALLOWS a bind failure with a log line), so a
/// dashboard probe sent on MCP readiness alone can land on a port that is not
/// listening yet, or never will. Wait for the dashboard's own listener,
/// bounded, so a genuine "the dashboard never came up" failure reports itself
/// as that instead of as a bare connection-refused inside an assertion.
let private awaitDashboardListener () = task {
  let! up =
    Infra.waitForAsync 60_000 (fun () -> task {
      try
        let! status, _ = sendToDashboard (probe HttpMethod.Get "/dashboard")
        return status > 0
      with _ -> return false })
  up
  |> Expect.isTrue
       (sprintf "the daemon's dashboard listener must come up on port %d" dashboardPort)
}

let private sessionCount (client: HttpClient) = task {
  let! status, body = Harness.getJson client "/api/sessions"
  status |> Expect.equal "/api/sessions answers" 200
  use doc = JsonDocument.Parse(body: string)
  return doc.RootElement.GetProperty("sessions").EnumerateArray() |> Seq.length
}

/// A rejection must say what was refused and why — an agent or a developer
/// reading the 403 should not have to read the daemon log to understand it.
let private expectRejection (expectedStatus: int) (mustMention: string) (status: int, body: string) =
  status |> Expect.equal (sprintf "must be refused by the origin guard (body: %s)" body) expectedStatus
  use doc = JsonDocument.Parse(body: string)
  doc.RootElement.GetProperty("success").GetBoolean()
  |> Expect.isFalse "a refused request must not report success"
  doc.RootElement.GetProperty("error").GetString()
  |> Expect.stringContains "the refusal must name what was refused" mustMention

// ─── The gate ───────────────────────────────────────────────────────────────

[<Tests>]
let originGuardOutcomeTests =
  testSequenced
  <| Integration.hostList "Origin guard outcome" [

    // ── The headline attack: a web page creating a session ──────────────────

    testTask "a cross-origin POST cannot create a session on a real daemon" {
      let client = daemonClient ()
      let! before = sessionCount client
      before |> Expect.equal "the daemon starts with no sessions (--no-resume, fresh data dir)" 0

      let! rejected =
        probe HttpMethod.Post "/api/sessions/create"
        |> jsonBody {| projects = ([||]: string array); workingDirectory = attackerTargetDir |}
        |> fun p -> { p with Origin = Some "http://evil.example"; SecFetchSite = Some "cross-site" }
        |> sendToMcp
      rejected |> expectRejection 403 "http://evil.example"

      // The outcome that matters is not the status code — it is that nothing
      // happened. A guard that answered 403 after the route already ran would
      // pass a status assertion and still have executed the attack.
      let! after = sessionCount client
      after |> Expect.equal "the rejected request must not have created a session" 0
    }

    // ── Guard vs. route, isolated by a header-only difference ───────────────

    testTask "the SAME mutating request is refused with a foreign Origin and reaches the route without one" {
      let payload =
        {| filePath = IO.Path.Combine(attackerTargetDir, "Unsaved.fs")
           content = "module Unsaved\nlet value = 42" |}
      let target = "/api/sessions/deadbeef/buffer-changed"

      let! guarded =
        probe HttpMethod.Post target
        |> jsonBody payload
        |> fun p -> { p with Origin = Some "http://evil.example" }
        |> sendToMcp
      guarded |> expectRejection 403 "http://evil.example"

      // Identical method, path, body and Content-Type — only the Origin header
      // is gone. 404 is the ROUTE's answer for an unknown session id, so it
      // can only be produced by a request the middleware let through.
      let! allowed =
        probe HttpMethod.Post target |> jsonBody payload |> sendToMcp
      fst allowed
      |> Expect.equal
           (sprintf "local tooling (no browser headers) must reach the route: %s" (snd allowed))
           404
    }

    // ── The no-preflight bypass ─────────────────────────────────────────────

    testTask "a text/plain POST — the CORS simple request a page may send with no preflight — is refused" {
      // text/plain, form-urlencoded and multipart are the three content types
      // a page can POST cross-origin WITHOUT a preflight. If one of them
      // reached the route, the browser would never have sent the preflight
      // whose foreign Origin this guard rejects, so the Origin check would
      // never be consulted at all. 415 is the guard's answer for a body that
      // is not labelled application/json.
      let! rejected =
        { probe HttpMethod.Post "/api/sessions/create" with
            Body = Some (JsonSerializer.Serialize {| projects = ([||]: string array); workingDirectory = attackerTargetDir |})
            ContentType = Some "text/plain;charset=UTF-8" }
        |> sendToMcp
      rejected |> expectRejection 415 "text/plain"

      let! after = sessionCount (daemonClient ())
      after |> Expect.equal "the simple-request bypass must not have created a session" 0
    }

    // ── DNS rebinding ───────────────────────────────────────────────────────

    testTask "a request whose Host is not the loopback interface is refused (DNS rebinding)" {
      // A page on sagefs.evil.example whose DNS A record answers 127.0.0.1
      // reaches the daemon with a same-origin Origin and a foreign Host. Only
      // the Host check can see that.
      let! rejected =
        probe HttpMethod.Post "/api/sessions/create"
        |> jsonBody {| projects = ([||]: string array); workingDirectory = attackerTargetDir |}
        |> fun p -> { p with HostHeader = Some "sagefs.evil.example" }
        |> sendToMcp
      rejected |> expectRejection 403 "sagefs.evil.example"
    }

    // ── A safe method is not a loophole ─────────────────────────────────────

    testTask "a foreign Origin cannot read the daemon's state even with GET" {
      let! rejected =
        { probe HttpMethod.Get "/api/sessions" with
            Origin = Some "http://evil.example"
            SecFetchSite = Some "cross-site" }
        |> sendToMcp
      rejected |> expectRejection 403 "http://evil.example"

      // ...while the same GET from local tooling still answers, so the guard
      // has not simply broken the endpoint.
      let! allowed = probe HttpMethod.Get "/api/sessions" |> sendToMcp
      fst allowed |> Expect.equal "local tooling still reads /api/sessions" 200
    }

    // ── The SECOND listener ─────────────────────────────────────────────────

    testTask "the dashboard listener is guarded too, and shutdown survives the attack" {
      // The dashboard is a separate WebApplication with its own one-line
      // `useOriginGuard` call. Dropping either line leaves the other green, so
      // both listeners are asserted.
      do! awaitDashboardListener ()
      let! rejectedEval =
        probe HttpMethod.Post "/dashboard/eval"
        |> jsonBody {| code = "1 + 1;;" |}
        |> fun p -> { p with Origin = Some "http://evil.example"; SecFetchSite = Some "cross-site" }
        |> sendToDashboard
      rejectedEval |> expectRejection 403 "http://evil.example"

      let! rejectedShutdown =
        { probe HttpMethod.Post "/api/shutdown" with
            Origin = Some "http://evil.example"
            SecFetchSite = Some "cross-site" }
        |> sendToDashboard
      rejectedShutdown |> expectRejection 403 "http://evil.example"

      // The outcome: the daemon is still alive and still serving. A 403 that
      // arrived after the shutdown handler ran would prove nothing.
      let proc = daemonProc ()
      proc.HasExited
      |> Expect.isFalse "a cross-origin /api/shutdown must not end the daemon"
      let! status, _ = Harness.getJson (daemonClient ()) "/health"
      status |> Expect.equal "the daemon still serves /health after the attack" 200
    }

    // ── The dashboard's own page must still work ────────────────────────────

    testTask "the dashboard's own origin is allowed through to its routes" {
      // Same-origin, own Origin header: this is what Datastar sends from the
      // real dashboard page. A guard that rejected it would be a broken
      // product, so the gate asserts the allow direction on this listener too.
      do! awaitDashboardListener ()
      let! status, body =
        probe HttpMethod.Post "/dashboard/eval"
        |> jsonBody {| code = "1 + 1;;" |}
        |> fun p ->
          { p with
              Origin = Some (sprintf "http://127.0.0.1:%d" dashboardPort)
              SecFetchSite = Some "same-origin" }
        |> sendToDashboard
      status
      |> Expect.notEqual (sprintf "the dashboard's own page must not be refused (body: %s)" body) 403
    }
  ]
