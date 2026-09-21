module SageFs.Tests.SessionBindingIsolationOutcomeTests

/// OUTCOME gate for `docs/multi-session.md:27-33` — "two sessions cannot see
/// each other's bindings" (outcome-gate-sweep.md Gap K).
///
/// WHAT WAS ALREADY COVERED, AND WHY IT IS NOT ENOUGH.
/// `DaemonIntegrationTests.fs` proves the POSITIVE half well: two real worker
/// processes with different pids, each evaluating its own binding and getting
/// its own answer back. The NEGATIVE half — the claim the docs actually make —
/// was only inferred from that: no test has ever asked session B for session
/// A's binding. Inference is not a gate. If routing collapsed two working
/// directories onto one worker, or if a future shared-FSI optimisation leaked
/// a binding table between sessions, every existing assertion would still
/// pass: each session would still answer its own binding correctly.
///
/// SO THIS GATE ASSERTS THE NEGATIVE, SYMMETRICALLY, THROUGH THE REAL WIRE:
/// A binds a name, B's eval of that name FAILS to resolve, and the reverse
/// holds too — with a same-process check in between so a routing collapse
/// cannot masquerade as isolation.
///
/// WHY INTEGRATION AND NOT A SIMULATION: `SessionIsolationTests.fs:1135` is
/// labelled "End-to-end proof" and is an in-memory Elm-model fold over mocked
/// workers; it cannot observe a real FSI binding table at all. Isolation here
/// is a property of two OS processes, so the gate has to cross that boundary.
/// It is kept as cheap as that allows: BARE sessions (no projects), so there
/// is no project load, no `dotnet build` and no reference resolution — the
/// binding-table claim has nothing to do with warmup.
///
/// COST: one daemon, two bare sessions. Measured wall clock: ~25s.

open System
open System.Net.Http
open System.Text.Json
open Expecto
open Expecto.Flip

module Harness = SageFs.Tests.HttpApiIntegrationTests
module Integration = SageFs.Tests.TestInfrastructure.Integration

// ─── One daemon, two bare sessions in two directories ───────────────────────

let private daemonPort = Harness.reserveLoopbackPort ()

/// Two sessions for the SAME directory are one session by design (the owner
/// rejects the duplicate), so each gets its own temp directory. Bare: no
/// project, so `create` returns quickly and nothing is compiled.
let private dirA = IO.Directory.CreateTempSubdirectory("sagefs-isolation-a-").FullName
let private dirB = IO.Directory.CreateTempSubdirectory("sagefs-isolation-b-").FullName

let private daemon =
  lazy (
    Harness.startDaemonWithArgs daemonPort Harness.repoRoot [ "--no-resume" ]
    |> Async.AwaitTask
    |> Async.RunSynchronously)

let private client () = snd daemon.Value

do AppDomain.CurrentDomain.ProcessExit.Add(fun _ ->
  if daemon.IsValueCreated then
    let proc, c = daemon.Value
    c.Dispose()
    Harness.killDaemon proc
  for dir in [ dirA; dirB ] do
    try IO.Directory.Delete(dir, true) with _ -> ())

/// The outcome of one `/exec` call, as the endpoint reports it. `/exec`
/// answers 200 whenever the request was PROCESSED; the typed `success` flag
/// carries the eval outcome, so a compile failure is distinguishable from a
/// successful eval without sniffing the result text.
type private EvalOutcome =
  | Evaluated of result: string
  | Refused of result: string

let private execIn (workingDir: string) (code: string) = task {
  let! status, body =
    Harness.postJson (client ()) "/exec" {| code = code; working_directory = workingDir |}
  status |> Expect.equal (sprintf "/exec processes the request (body: %s)" body) 200
  use doc = JsonDocument.Parse(body: string)
  let result = doc.RootElement.GetProperty("result").GetString()
  return
    match doc.RootElement.GetProperty("success").GetBoolean() with
    | true -> Evaluated result
    | false -> Refused result
}

let private createBareSession (workingDir: string) = task {
  let! status, body =
    Harness.postJson (client ()) "/api/sessions/create"
      {| projects = ([||]: string array); workingDirectory = workingDir |}
  status |> Expect.equal (sprintf "session create for %s succeeds (%s)" workingDir body) 200
  let! ready, sessions = Harness.waitForReadySession (client ()) workingDir (TimeSpan.FromSeconds 120.0)
  ready
  |> Expect.isTrue (sprintf "the bare session for %s must reach Ready. Sessions: %s" workingDir sessions)
}

/// The worker process id a session's FSI actually runs in, read from inside
/// the session itself. A JSON field could be reported correctly while the
/// eval was routed elsewhere; this cannot.
let private workerPidOf (workingDir: string) = task {
  let! outcome = execIn workingDir "System.Diagnostics.Process.GetCurrentProcess().Id;;"
  match outcome with
  | Refused result -> return failwithf "could not read the worker pid for %s: %s" workingDir result
  | Evaluated result ->
    let digits = result |> Seq.filter Char.IsDigit |> Seq.toArray |> String
    match Int32.TryParse digits with
    | true, pid -> return pid
    | false, _ -> return failwithf "worker pid not parseable from %s" result
}

// ─── The gate ───────────────────────────────────────────────────────────────

[<Tests>]
let sessionBindingIsolationOutcomeTests =
  testSequenced
  <| Integration.hostList "Session binding isolation outcome" [

    testTask "a binding made in one session is not resolvable in another" {
      do! createBareSession dirA
      do! createBareSession dirB

      // Guard against the failure mode that would make the real assertions
      // vacuous: if both directories routed to ONE worker, every isolation
      // claim below would be about a single FSI session talking to itself.
      let! pidA = workerPidOf dirA
      let! pidB = workerPidOf dirB
      pidB
      |> Expect.notEqual
           (sprintf "the two sessions must run in two worker processes (both reported %d)" pidA)
           pidA

      // A binds. B binds. Each sees its own — the positive control.
      let! boundA = execIn dirA "let sageFsSecretA = 424242;;"
      match boundA with
      | Refused r -> failwithf "session A failed to bind: %s" r
      | Evaluated _ -> ()
      let! boundB = execIn dirB "let sageFsSecretB = 131313;;"
      match boundB with
      | Refused r -> failwithf "session B failed to bind: %s" r
      | Evaluated _ -> ()

      let! ownA = execIn dirA "sageFsSecretA;;"
      match ownA with
      | Refused r -> failwithf "session A must see its own binding, got: %s" r
      | Evaluated r -> r |> Expect.stringContains "session A sees its own value" "424242"

      let! ownB = execIn dirB "sageFsSecretB;;"
      match ownB with
      | Refused r -> failwithf "session B must see its own binding, got: %s" r
      | Evaluated r -> r |> Expect.stringContains "session B sees its own value" "131313"

      // THE CLAIM: neither can resolve the other's name. Asserted in both
      // directions, because a one-way leak is still a leak.
      let! leakedIntoB = execIn dirB "sageFsSecretA;;"
      match leakedIntoB with
      | Evaluated r ->
        failwithf "session B resolved session A's binding — sessions are NOT isolated: %s" r
      | Refused r ->
        r |> Expect.stringContains "the refusal must name the unresolved binding" "sageFsSecretA"
        r |> Expect.stringContains "F# must report the name as undefined" "not defined"

      let! leakedIntoA = execIn dirA "sageFsSecretB;;"
      match leakedIntoA with
      | Evaluated r ->
        failwithf "session A resolved session B's binding — sessions are NOT isolated: %s" r
      | Refused r ->
        r |> Expect.stringContains "the refusal must name the unresolved binding" "sageFsSecretB"

      // And the leak-check did not disturb either session: A still has its own
      // binding after B failed to find it.
      let! stillA = execIn dirA "sageFsSecretA;;"
      match stillA with
      | Refused r -> failwithf "session A lost its binding after the isolation probe: %s" r
      | Evaluated r -> r |> Expect.stringContains "session A still holds its value" "424242"
    }
  ]
