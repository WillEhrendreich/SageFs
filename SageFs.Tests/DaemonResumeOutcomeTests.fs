module SageFs.Tests.DaemonResumeOutcomeTests

/// OUTCOME gate for `Readme.md:471-479` — the daemon manifest is "replayed on
/// startup to rebuild your sessions" (outcome-gate-sweep.md Gap E).
///
/// WHAT WAS ALREADY COVERED, AND WHY IT IS NOT ENOUGH.
/// Below this line the coverage is genuinely excellent: `BinaryFormatTests`
/// with corruption properties, `ManifestPersistenceTests`, the `ManifestSim`
/// DST triple (concurrent mutation, fault injection, single-owner discipline)
/// and `DaemonResumeDecisionTests` for the pure Resume/Forget decision. Every
/// one of them runs in ONE process against in-memory state or a file that the
/// same process wrote and read back.
///
/// The claim itself is about a DIFFERENT process: the daemon you have now
/// rebuilds sessions that a daemon that no longer exists wrote down. Nothing
/// crosses that boundary, so the whole class of failures that live at it —
/// the manifest never reaching disk, being written somewhere the next daemon
/// does not look, resume not being wired into startup at all, the records
/// round-tripping but arriving with a working directory or project list the
/// resume path cannot use — ships green.
///
/// SO THIS GATE CROSSES IT: daemon 1 creates a session and dies; daemon 2 is
/// started on the same data directory and a different port, and the session
/// comes back with the same working directory and the same project list.
///
/// WHY THE FIRST DAEMON IS KILLED, NOT ASKED TO STOP: a graceful shutdown
/// stamps every live session STOPPED in the manifest
/// (`performGracefulShutdown` commits a `LiveSync.ShuttingDown` sync), and
/// `aliveSessions` resumes only records with no StoppedAt — so `sagefs stop`
/// followed by `sagefs start` deliberately restores nothing. The promise this
/// gate is named for is the crash/kill case, and that is what it reproduces.
///
/// WHAT THE COST BUYS, HONESTLY STATED: the daemon writes the manifest from a
/// 60-second periodic timer (`DaemonMode.fs` — `cacheSaveTimer`, first tick at
/// start+60s); session creation itself commits nothing. So the observable
/// durability boundary is up to a minute wide, and this gate waits on the real
/// file through the real `DaemonPersistence.loadManifest` rather than pretending
/// otherwise. That wait IS the measurement: if a future change makes creation
/// durable immediately, this gate gets faster and stays correct.
///
/// COST: two sequential daemons (never concurrent), one bare session each
/// side, no project load. Measured wall clock: ~90s, dominated by the 60s
/// manifest timer. This is the most expensive gate in Island E and nothing
/// cheaper crosses a process boundary.

open System
open System.Diagnostics
open System.Net.Http
open System.Text.Json
open Expecto
open Expecto.Flip
open SageFs.Features

module Harness = SageFs.Tests.HttpApiIntegrationTests
module Integration = SageFs.Tests.TestInfrastructure.Integration
module Infra = SageFs.Tests.TestInfrastructure

/// How long to wait for the daemon's periodic save to make the session
/// durable. The timer is wall-clock (60s from daemon start) and does not get
/// faster on a faster runner, so the ceiling is a generous multiple of it.
let private manifestDurabilityCeiling = TimeSpan.FromSeconds 150.0

/// How long the second daemon may take to rebuild the session.
let private resumeCeiling = TimeSpan.FromSeconds 150.0

/// Start a daemon bound to an EXPLICIT data directory — the one thing the
/// shared harness cannot do, because it allocates a fresh isolated
/// SAGEFS_DATA_DIR per call and resume needs two daemons to share one.
/// Everything else is `HttpApiIntegrationTests.startDaemonWithArgs` verbatim:
/// reserved loopback port, `--owner-pid`/`--owner-start` watchdog so a killed
/// test runner can never leak this daemon, `--ttl 10m` on top of that, and a
/// bounded health poll before the client is handed back.
let private startDaemonOnDataDir (port: int) (dataDir: string) (args: string list) = task {
  let psi = ProcessStartInfo()
  psi.FileName <- Infra.SageFsBinary.path ()
  psi.UseShellExecute <- false
  psi.CreateNoWindow <- true
  psi.WorkingDirectory <- Harness.repoRoot
  psi.ArgumentList.Add("--mcp-port")
  psi.ArgumentList.Add(string port)
  for arg in args do
    psi.ArgumentList.Add(arg)
  let self = Process.GetCurrentProcess()
  psi.ArgumentList.Add("--owner-pid")
  psi.ArgumentList.Add(string self.Id)
  psi.ArgumentList.Add("--owner-start")
  psi.ArgumentList.Add(string (self.StartTime.ToUniversalTime().Ticks))
  psi.ArgumentList.Add("--ttl")
  psi.ArgumentList.Add("10m")
  psi.Environment["SAGEFS_DATA_DIR"] <- dataDir

  let proc = Process.Start(psi)
  let client = new HttpClient()
  client.BaseAddress <- Uri(sprintf "http://localhost:%d" port)
  client.Timeout <- TimeSpan.FromSeconds 30.0

  let! healthy =
    Infra.waitForAsync 90_000 (fun () -> task {
      try
        let! resp = client.GetAsync("/health")
        return int resp.StatusCode > 0
      with _ -> return false })

  match healthy with
  | false ->
    try proc.Kill(entireProcessTree = true) with _ -> ()
    proc.Dispose()
    client.Dispose()
    return failwithf "daemon on port %d (data dir %s) never answered /health" port dataDir
  | true -> return proc, client
}

/// The sessions the manifest ON DISK currently records as alive, read through
/// the daemon's own loader — not a hand-rolled parser, so a format change
/// cannot make this gate pass while the daemon cannot read its own file.
let private aliveInManifest (dataDir: string) =
  match DaemonPersistence.loadManifest dataDir with
  | Ok state -> DaemonManifest.DaemonManifestState.aliveSessions state
  | Error _ -> []

let private sessionsIn (workingDir: string) (body: string) : (string * string) list =
  use doc = JsonDocument.Parse body
  doc.RootElement.GetProperty("sessions").EnumerateArray()
  |> Seq.filter (fun (s: JsonElement) ->
    let dir = Harness.normalizeDir (s.GetProperty("workingDirectory").GetString())
    dir = Harness.normalizeDir workingDir)
  |> Seq.map (fun (s: JsonElement) ->
    s.GetProperty("id").GetString(), s.GetProperty("status").GetString())
  |> Seq.toList

let private sessionsFor (client: HttpClient) (workingDir: string) = task {
  try
    let! status, body = Harness.getJson client "/api/sessions"
    match status with
    | 200 -> return sessionsIn workingDir body
    | _ -> return []
  with _ -> return []
}

/// (success, result) from an `/exec` response body.
let private execOutcome (body: string) : bool * string =
  use doc = JsonDocument.Parse body
  doc.RootElement.GetProperty("success").GetBoolean(),
  doc.RootElement.GetProperty("result").GetString()

// ─── The gate ───────────────────────────────────────────────────────────────

[<Tests>]
let daemonResumeOutcomeTests =
  testSequenced
  <| Integration.hostList "Daemon resume outcome" [

    testTask "a session created in one daemon comes back in the next daemon started on the same data dir" {
      // One data directory shared by both daemons; one working directory for
      // the session. Both are fresh temp dirs, so the real ~/.SageFs is never
      // read or written.
      let dataDir = IO.Directory.CreateTempSubdirectory("sagefs-resume-data-").FullName
      let workingDir = IO.Directory.CreateTempSubdirectory("sagefs-resume-work-").FullName
      let mutable first : Process = null
      let mutable firstClient : HttpClient = null
      let mutable second : Process = null
      let mutable secondClient : HttpClient = null
      let wall = Stopwatch.StartNew()

      try
        // ── Daemon 1: create a session ──────────────────────────────────────
        let portOne = Harness.reserveLoopbackPort (Some (40100 + Random.Shared.Next 150))
        let! proc1, client1 = startDaemonOnDataDir portOne dataDir []
        first <- proc1
        firstClient <- client1

        let! createStatus, createBody =
          Harness.postJson client1 "/api/sessions/create"
            {| projects = ([||]: string array); workingDirectory = workingDir |}
        createStatus |> Expect.equal (sprintf "session create succeeds (%s)" createBody) 200

        let! ready, sessionsBody =
          Harness.waitForReadySession client1 workingDir (TimeSpan.FromSeconds 120.0)
        ready |> Expect.isTrue (sprintf "the session reaches Ready in daemon 1. Sessions: %s" sessionsBody)

        // ── The durability boundary ─────────────────────────────────────────
        // Wait for the daemon's own writer to put an ALIVE record for this
        // working directory on disk. Read through the production loader.
        let durability = Stopwatch.StartNew()
        let! durable =
          Infra.waitForAsync (int manifestDurabilityCeiling.TotalMilliseconds) (fun () -> task {
            return
              aliveInManifest dataDir
              |> List.exists (fun r ->
                Harness.normalizeDir r.WorkingDir = Harness.normalizeDir workingDir) })
        durability.Stop()
        durable
        |> Expect.isTrue
             (sprintf
                "the daemon must make the session durable within %O (waited %O; manifest dir %s)"
                manifestDurabilityCeiling durability.Elapsed dataDir)

        let recorded =
          aliveInManifest dataDir
          |> List.find (fun r -> Harness.normalizeDir r.WorkingDir = Harness.normalizeDir workingDir)

        // ── Daemon 1 dies the way a crash kills it ──────────────────────────
        // entireProcessTree so the session's worker goes with it: a surviving
        // worker would make "the session came back" ambiguous.
        proc1.Kill(entireProcessTree = true)
        proc1.WaitForExit(15_000) |> ignore
        proc1.HasExited |> Expect.isTrue "daemon 1 is gone before daemon 2 starts"
        client1.Dispose()
        firstClient <- null

        // ── Daemon 2: a DIFFERENT process, a DIFFERENT port, the same dir ───
        let portTwo = Harness.reserveLoopbackPort (Some (40300 + Random.Shared.Next 150))
        portTwo |> Expect.notEqual "the second daemon must not reuse the first port" portOne
        let! proc2, client2 = startDaemonOnDataDir portTwo dataDir []
        second <- proc2
        secondClient <- client2

        let! resumed =
          Infra.waitForAsync (int resumeCeiling.TotalMilliseconds) (fun () -> task {
            let! found = sessionsFor client2 workingDir
            return not found.IsEmpty })

        let! (found: (string * string) list) = sessionsFor client2 workingDir
        resumed
        |> Expect.isTrue
             (sprintf
                "daemon 2 must rebuild the session recorded as %s for %s within %O"
                recorded.SessionId workingDir resumeCeiling)

        // The rebuilt session must be USABLE, not just listed: same working
        // directory (asserted by the filter above), same project list, and
        // exactly one of it — a resume that duplicated the record would break
        // /exec routing with "multiple sessions match workingDirectory".
        found.Length
        |> Expect.equal
             (sprintf "exactly one session is rebuilt for %s (got %A)" workingDir found)
             1

        recorded.Projects
        |> Expect.equal "the manifest recorded the session's project list verbatim" []

        // And it really is a live FSI in the new daemon, not a placeholder row.
        let! readyAgain, afterBody =
          Harness.waitForReadySession client2 workingDir (TimeSpan.FromSeconds 120.0)
        readyAgain
        |> Expect.isTrue (sprintf "the rebuilt session reaches Ready. Sessions: %s" afterBody)

        let! execStatus, execBody =
          Harness.postJson client2 "/exec"
            {| code = "40 + 2;;"; working_directory = workingDir |}
        execStatus |> Expect.equal (sprintf "/exec reaches the rebuilt session (%s)" execBody) 200
        let evalSucceeded, evalResult = execOutcome execBody
        evalSucceeded
        |> Expect.isTrue (sprintf "the rebuilt session evaluates code (%s)" execBody)
        evalResult
        |> Expect.stringContains "the rebuilt session is a real FSI" "42"

        wall.Stop()
        // Not an assertion about speed — a tripwire. If this ever approaches
        // the ceilings above, the gate is about to start flaking and the
        // reason will be in this message rather than a bare timeout.
        (wall.Elapsed < manifestDurabilityCeiling + resumeCeiling)
        |> Expect.isTrue (sprintf "resume round trip stayed inside its budget (took %O)" wall.Elapsed)
      finally
        for c in [ firstClient; secondClient ] do
          match isNull c with
          | true -> ()
          | false -> try c.Dispose() with _ -> ()
        for p in [ first; second ] do
          match isNull p with
          | true -> ()
          | false -> Harness.killDaemon p
        try IO.Directory.Delete(dataDir, true) with _ -> ()
        try IO.Directory.Delete(workingDir, true) with _ -> ()
    }
  ]
