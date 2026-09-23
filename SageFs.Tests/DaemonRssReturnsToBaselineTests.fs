/// The outcome test job 4 calls for: "create sessions, stop them, and
/// assert the daemon's own RSS returns near its baseline." Every other test
/// in this effort proves a DECISION is correct (reap the dead, shed idle,
/// refuse admission); this one proves the actual daemon PROCESS behaves the
/// way a user cares about — its own resident memory, measured from the
/// outside by PID, not self-reported.
///
/// Both incidents this whole effort exists for were the DAEMON's own
/// process (not a worker's) growing to 51.7GB and then 55GB — the daemon's
/// `Process.GetCurrentProcess().WorkingSet64` is exactly the number that
/// ran away. Each session's FSI worker is a SEPARATE OS process with its
/// own memory, invisible to this measurement — which is the point: this
/// test is measuring precisely the retained-per-session-state leak Job 2
/// closes (RecentOutput ring buffers, LiveBindingsAdaptive snapshots), not
/// worker-process memory.
module SageFs.Tests.DaemonRssReturnsToBaselineTests

open System
open System.Diagnostics
open System.IO
open Expecto
open Expecto.Flip
open SageFs.Tests.HttpApiIntegrationTests

module Integration = SageFs.Tests.TestInfrastructure.Integration

let private repoRoot = Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, ".."))

let private sampleProjects =
  [ "from-csharp", "SageFs.Samples.FromCSharp"
    "from-python", "SageFs.Samples.FromPython"
    "from-rust", "SageFs.Samples.FromRust"
    "from-java", "SageFs.Samples.FromJava" ]
  |> List.map (fun (folder, name) ->
    let dir = Path.Combine(repoRoot, "samples", folder, name)
    dir, Path.Combine(dir, name + ".fsproj"))

let private rssMB (proc: Process) =
  proc.Refresh()
  proc.WorkingSet64 / 1_048_576L

[<Tests>]
let tests =
  Integration.hostList "Daemon RSS returns to baseline after sessions are created and stopped" [

    testTask "four sessions created, evaluated and stopped: daemon RSS returns near its own baseline" {
      let port = reserveLoopbackPort ()
      let! proc, client = startDaemon port
      try
        // Let warmup settle (module init, JIT of the request pipeline) before
        // taking the baseline — otherwise "baseline" is really "mid-startup."
        do! Threading.Tasks.Task.Delay(2000)
        let baselineMB = rssMB proc

        // Create all four, wait for each to reach Ready, then actually
        // evaluate something in each — real output lines and a real
        // adaptive-bindings snapshot, not an empty session that would make
        // this test pass for a reason that proves nothing.
        for (dir, projFile) in sampleProjects do
          let! status, body = createSession client projFile dir
          status |> Expect.equal (sprintf "create for %s" dir) 200
          let! ready, lastBody = waitForReadySession client dir (TimeSpan.FromSeconds 90.0)
          ready |> Expect.isTrue (sprintf "%s should reach Ready — last sessions body: %s (create body: %s)" dir lastBody body)
          let! _ = postJson client "/exec" {| code = "let x = [ for i in 1 .. 200 -> i * i ];; printfn \"%d\" x.Length"; working_directory = dir |}
          ()

        let peakMB = rssMB proc
        (peakMB > baselineMB) |> Expect.isTrue "four real sessions with real output should have grown the daemon's own RSS at all"

        // Stop all four — collect ids from /api/sessions first (create's own
        // response carries no clean sessionId field; see the existing
        // "POST /api/sessions/stop stops a session" test for the same
        // pattern).
        let! _, sessBody = getJson client "/api/sessions"
        let sessDoc = System.Text.Json.JsonDocument.Parse(sessBody: string)
        let ourDirs = sampleProjects |> List.map (fun (dir, _) -> normalizeDir dir) |> Set.ofList
        let idsToStop =
          sessDoc.RootElement.GetProperty("sessions").EnumerateArray()
          |> Seq.filter (fun s -> ourDirs.Contains(normalizeDir (s.GetProperty("workingDirectory").GetString())))
          |> Seq.map (fun s -> s.GetProperty("id").GetString())
          |> Seq.toList
        sessDoc.Dispose()
        idsToStop |> Expect.hasLength "found all four sessions to stop" 4
        for sid in idsToStop do
          let! stopStatus, _ = postJson client "/api/sessions/stop" {| sessionId = sid |}
          stopStatus |> Expect.equal (sprintf "stop %s" sid) 200

        // The daemon's own periodic sweep runs every 5s (DaemonMode's
        // watcherSyncTimer) and also fires immediately on the ModelChanged
        // a stop dispatches — poll (task-based, no thread-blocking sleep)
        // rather than assume a fixed delay is enough.
        let tolerance = 150L // MB — daemon-internal growth unrelated to sessions (JIT, GC bookkeeping) is expected and NOT the leak this guards against.
        let! returned =
          SageFs.Tests.TestInfrastructure.awaitCondition
            30_000
            (fun () -> rssMB proc <= baselineMB + tolerance)
        let finalMB = rssMB proc
        returned
        |> Expect.isTrue (
          sprintf
            "daemon RSS should return near its baseline after all sessions are stopped — baseline=%dMB peak=%dMB final=%dMB (tolerance %dMB)"
            baselineMB peakMB finalMB tolerance
        )
      finally
        try
          if not proc.HasExited then
            proc.Kill()
            proc.WaitForExit(5000) |> ignore
        with _ -> ()
        client.Dispose()
    }
  ]
