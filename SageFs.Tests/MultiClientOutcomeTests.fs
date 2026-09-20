module SageFs.Tests.MultiClientOutcomeTests

/// OUTCOME gate for `Readme.md:74`/`:230` — "multiple clients share live
/// session state" (outcome-gate-sweep.md Gap K).
///
/// WHAT WAS ALREADY COVERED, AND WHY IT IS NOT ENOUGH.
/// `ConnectionTrackerTests.fs` is ten in-memory cases over the connection
/// bookkeeping; `CohortDogfoodIntegrationTests.fs` proves two MCP connections
/// are tracked as distinct cohort MEMBERS. Neither asserts the promise a user
/// reads: that an editor, the dashboard and an agent looking at the same
/// session see the SAME session — one FSI, one binding table, one history —
/// rather than a per-connection view. The sharing claim is the whole point of
/// a daemon; nothing gated it.
///
/// SO THIS GATE ASSERTS THE OUTCOME: two independent HTTP clients, each with
/// its own connection pool and its own cookie-free identity, address the same
/// session. What one client binds, the other evaluates; what one client
/// creates, the other lists; and the shared state outlives a single request
/// pair rather than living per connection.
///
/// WHY INTEGRATION: "shared" is a claim about one process serving two
/// connections. An in-process test has one connection by construction, so the
/// defect class — per-connection state that looks shared to a single caller —
/// is invisible to it. Kept cheap the same way the isolation gate is: a BARE
/// session, no project load.
///
/// COST: one daemon, one bare session, two clients. Measured wall clock: ~15s.

open System
open System.Net.Http
open System.Text.Json
open Expecto
open Expecto.Flip

module Harness = SageFs.Tests.HttpApiIntegrationTests
module Integration = SageFs.Tests.TestInfrastructure.Integration

// ─── One daemon, one session, two independent clients ───────────────────────

let private daemonPort = Harness.reserveLoopbackPort (Some (39800 + Random.Shared.Next 150))

let private sharedDir = IO.Directory.CreateTempSubdirectory("sagefs-multiclient-").FullName

let private daemon =
  lazy (
    Harness.startDaemonWithArgs daemonPort Harness.repoRoot [ "--no-resume" ]
    |> Async.AwaitTask
    |> Async.RunSynchronously)

/// The client the harness owns (used for lifecycle), plus two clients that are
/// each as independent of it as an editor and a dashboard tab are of each
/// other: separate HttpClient instances, therefore separate connection pools.
let private clientA =
  lazy (
    let c = new HttpClient(BaseAddress = Uri(sprintf "http://localhost:%d" daemonPort))
    c.Timeout <- TimeSpan.FromSeconds 60.0
    c)

let private clientB =
  lazy (
    let c = new HttpClient(BaseAddress = Uri(sprintf "http://localhost:%d" daemonPort))
    c.Timeout <- TimeSpan.FromSeconds 60.0
    c)

do AppDomain.CurrentDomain.ProcessExit.Add(fun _ ->
  for c in [ clientA; clientB ] do
    if c.IsValueCreated then (try c.Value.Dispose() with _ -> ())
  if daemon.IsValueCreated then
    let proc, c = daemon.Value
    c.Dispose()
    Harness.killDaemon proc
  try IO.Directory.Delete(sharedDir, true) with _ -> ())

/// The outcome of one `/exec` call as the endpoint reports it (`/exec` answers
/// 200 whenever the request was processed; `success` carries the eval result).
type private EvalOutcome =
  | Evaluated of result: string
  | Refused of result: string

let private execVia (client: HttpClient) (code: string) = task {
  let! status, body =
    Harness.postJson client "/exec" {| code = code; working_directory = sharedDir |}
  status |> Expect.equal (sprintf "/exec processes the request (body: %s)" body) 200
  use doc = JsonDocument.Parse(body: string)
  let result = doc.RootElement.GetProperty("result").GetString()
  return
    match doc.RootElement.GetProperty("success").GetBoolean() with
    | true -> Evaluated result
    | false -> Refused result
}

let private sessionIdsIn (body: string) : string list =
  use doc = JsonDocument.Parse body
  doc.RootElement.GetProperty("sessions").EnumerateArray()
  |> Seq.filter (fun (s: JsonElement) ->
    let dir = Harness.normalizeDir (s.GetProperty("workingDirectory").GetString())
    dir = Harness.normalizeDir sharedDir)
  |> Seq.map (fun (s: JsonElement) -> s.GetProperty("id").GetString())
  |> Seq.toList

let private sessionIdsVia (client: HttpClient) = task {
  let! status, body = Harness.getJson client "/api/sessions"
  status |> Expect.equal "/api/sessions answers" 200
  return sessionIdsIn body
}

// ─── The gate ───────────────────────────────────────────────────────────────

[<Tests>]
let multiClientOutcomeTests =
  testSequenced
  <| Integration.hostList "Multi-client shared session outcome" [

    testTask "a session created by one client is the same live session another client evaluates in" {
      let a = clientA.Value
      let b = clientB.Value
      // Force the daemon up through the harness's own health-polled start.
      let _ = daemon.Value

      // CLIENT A creates the session.
      let! createStatus, createBody =
        Harness.postJson a "/api/sessions/create"
          {| projects = ([||]: string array); workingDirectory = sharedDir |}
      createStatus |> Expect.equal (sprintf "client A creates the session (%s)" createBody) 200
      let! ready, sessions = Harness.waitForReadySession a sharedDir (TimeSpan.FromSeconds 120.0)
      ready |> Expect.isTrue (sprintf "the session reaches Ready. Sessions: %s" sessions)

      // CLIENT B sees exactly that session — not none, and not a second one of
      // its own. A per-connection registry would show B zero sessions here.
      let! idsFromA = sessionIdsVia a
      let! (idsFromB: string list) = sessionIdsVia b
      idsFromB
      |> Expect.equal
           "both clients must list the SAME single session for the shared directory"
           idsFromA
      idsFromB.Length
      |> Expect.equal "exactly one session exists for the shared directory" 1

      // CLIENT A binds; CLIENT B reads the binding. This is the promise:
      // one FSI, one binding table, addressed from two connections.
      let! bound = execVia a "let sharedByClientA = 9090;;"
      match bound with
      | Refused r -> failwithf "client A failed to bind: %s" r
      | Evaluated _ -> ()

      let! seenByB = execVia b "sharedByClientA;;"
      match seenByB with
      | Refused r ->
        failwithf "client B could not see client A's binding — session state is NOT shared: %s" r
      | Evaluated r -> r |> Expect.stringContains "client B reads client A's value" "9090"

      // ...and back the other way, so the sharing is not one-directional.
      let! boundByB = execVia b "let sharedByClientB = sharedByClientA + 1;;"
      match boundByB with
      | Refused r -> failwithf "client B failed to bind on top of A's state: %s" r
      | Evaluated _ -> ()

      let! seenByA = execVia a "sharedByClientB;;"
      match seenByA with
      | Refused r ->
        failwithf "client A could not see client B's binding — session state is NOT shared: %s" r
      | Evaluated r -> r |> Expect.stringContains "client A reads client B's value" "9091"
    }

    testTask "state bound through one client is still there for the other across separate later requests" {
      let a = clientA.Value
      let b = clientB.Value

      // Sharing must outlive a single request pair: B binds, both clients make
      // an unrelated round trip, and A can still read the value. A daemon that
      // kept per-connection FSI state would lose it at exactly this seam.
      let! _ = execVia b "let laterProbeFromB = 7777;;"

      let! statusForA, bodyForA = Harness.getJson a "/api/status"
      statusForA |> Expect.equal (sprintf "/api/status answers client A (%s)" bodyForA) 200
      let! statusForB, bodyForB = Harness.getJson b "/api/status"
      statusForB |> Expect.equal (sprintf "/api/status answers client B (%s)" bodyForB) 200

      let! stillThere = execVia a "laterProbeFromB;;"
      match stillThere with
      | Refused r -> failwithf "client A lost B's binding between requests: %s" r
      | Evaluated r ->
        r |> Expect.stringContains "the value B bound is still live for A" "7777"
    }
  ]
