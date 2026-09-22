module SageFs.Tests.DaemonIntegrationTests

open System
open System.Diagnostics
open System.IO
open System.Net
open System.Net.Sockets
open System.Threading
open Expecto
open Expecto.Flip
open SageFs
open SageFs.Server
open SageFs.WorkerProtocol
open SageFs.Tests.SharedGenerators

module Integration = SageFs.Tests.TestInfrastructure.Integration

// ─── Helpers ───────────────────────────────────────────────────────

let testProjectDir =
  Path.GetFullPath(
    Path.Combine(__SOURCE_DIRECTORY__, "..", "SageFs.Tests"))

/// A small, standalone, CI-built sample — NOT this repo's own SageFs.Tests.fsproj
/// (300+ files, the whole solution's package closure). `sessionManagerLifecycleTests`
/// below sessions on this instead of `testProjectDir`: with no explicit `projects`
/// list, `ProjectLoading.loadSolution` auto-discovers every `*.fsproj` in the given
/// working directory and runs it through Ionide's real MSBuild `WorkspaceLoader` —
/// pointed at `testProjectDir` (= SageFs.Tests itself), that was a full MSBuild
/// evaluation of the repo's largest project on every session create, dominating
/// both tests' wall clock (~65s for two sessions, measured) even though the claim
/// under test (worker routing / restart-onto-a-new-pid) does not depend on which
/// project is loaded. Switching to this sample cut it to ~23s. Duplicated here
/// rather than shared with `HttpApiIntegrationTests.smokeSampleProjectDir` (same
/// fsproj) because this file compiles earlier in SageFs.Tests.fsproj — the
/// isolation this repo's own integration fixtures already duplicate rather than
/// share (see CohortLandingGate's former header). CI builds it in "build samples
/// for integration suites" (ci-pipeline.fsx), same as the HttpApi suite's copy.
let sampleProjectDir =
  Path.GetFullPath(
    Path.Combine(__SOURCE_DIRECTORY__, "..", "samples", "from-csharp", "SageFs.Samples.FromCSharp"))

let SageFsExe = SageFs.Tests.TestInfrastructure.SageFsBinary.path ()

/// A fresh, throwaway SAGEFS_DATA_DIR so CLI subcommands and daemons spawned
/// by these tests never read or write the real ~/.SageFs.
let isolatedDataDir () =
  Path.Combine(Path.GetTempPath(), "sagefs-test", Guid.NewGuid().ToString("N"))

/// Kill a process by PID, swallowing errors.
let tryKill (pid: int) =
  try
    let p = Process.GetProcessById(pid)
    p.Kill()
    p.WaitForExit(3000) |> ignore
  with _ -> ()

// ─── SessionManager: ManagerState pure functions ───────────────────

[<Tests>]
let managerStateTests =
  testList "SessionManager.ManagerState" [
    testCase "empty has no sessions" <| fun _ ->
      let state = SageFs.SessionManager.ManagerState.empty
      SageFs.SessionManager.ManagerState.allInfos state
      |> List.length
      |> Expect.equal "no sessions" 0

    testCase "addSession then tryGetSession finds it" <| fun _ ->
      let sid = testSessionId "aaaa0001"
      let info : SessionInfo = {
        Id = sid
        Name = None
        Projects = ["Foo.fsproj"]
        WorkingDirectory = @"C:\test"
        SolutionRoot = None
        CreatedAt = DateTime.UtcNow
        LastActivity = DateTime.UtcNow
        Status = SessionLifecycleStatus.Ready { Pid = 1234; Port = None }
        Workflow = WorkflowTypes.SessionWorkflow.Interactive
        ActiveProject = None

        ProjectRoles = []

        App = SageFs.AppRun.AppRunState.NotRunning

      }
      let session : SageFs.SessionManager.ManagedSession = {
        Info = info
        Process = new Process()
        Proxy = fun _ -> async { return WorkerResponse.WorkerError (SageFsError.Unexpected (exn "mock")) }
        WorkerBaseUrl = ""
        Projects = ["Foo.fsproj"]
        WorkingDir = @"C:\test"
        AutoOpenNamespaces = true
        Workflow = WorkflowTypes.SessionWorkflow.Interactive
        RestartState = SageFs.RestartPolicy.emptyState
        AppGeneration = SageFs.AppRun.AppSlot.initial.Generation
        AdoptedCore = None

        ProjectRoles = []


      }
      let state =
        SageFs.SessionManager.ManagerState.empty
        |> SageFs.SessionManager.ManagerState.addSession sid session
      SageFs.SessionManager.ManagerState.tryGetSession sid state
      |> Expect.isSome "should find session"

    testCase "removeSession then tryGetSession returns None" <| fun _ ->
      let sid = testSessionId "aaaa0002"
      let info : SessionInfo = {
        Id = sid
        Name = None
        Projects = []
        WorkingDirectory = @"C:\test"
        SolutionRoot = None
        CreatedAt = DateTime.UtcNow
        LastActivity = DateTime.UtcNow
        Status = SessionLifecycleStatus.Ready { Pid = 1; Port = None }
        Workflow = WorkflowTypes.SessionWorkflow.Interactive
        ActiveProject = None

        ProjectRoles = []

        App = SageFs.AppRun.AppRunState.NotRunning

      }
      let session : SageFs.SessionManager.ManagedSession = {
        Info = info
        Process = new Process()
        Proxy = fun _ -> async { return WorkerResponse.WorkerError (SageFsError.Unexpected (exn "mock")) }
        WorkerBaseUrl = ""
        Projects = []
        WorkingDir = @"C:\test"
        AutoOpenNamespaces = true
        Workflow = WorkflowTypes.SessionWorkflow.Interactive
        RestartState = SageFs.RestartPolicy.emptyState
        AppGeneration = SageFs.AppRun.AppSlot.initial.Generation
        AdoptedCore = None

        ProjectRoles = []


      }
      let state =
        SageFs.SessionManager.ManagerState.empty
        |> SageFs.SessionManager.ManagerState.addSession sid session
        |> SageFs.SessionManager.ManagerState.removeSession sid
      SageFs.SessionManager.ManagerState.tryGetSession sid state
      |> Expect.isNone "should not find removed session"

    testCase "allInfos returns all session infos" <| fun _ ->
      let mkSession (sid: SessionId) : SageFs.SessionManager.ManagedSession =
        let info : SessionInfo = {
          Id = sid
          Name = None
          Projects = []
          WorkingDirectory = @"C:\test"
          SolutionRoot = None
          CreatedAt = DateTime.UtcNow
          LastActivity = DateTime.UtcNow
          Status = SessionLifecycleStatus.Ready { Pid = 1; Port = None }
          Workflow = WorkflowTypes.SessionWorkflow.Interactive
          ActiveProject = None

          ProjectRoles = []

          App = SageFs.AppRun.AppRunState.NotRunning

        }
        { Info = info
          Process = new Process()
          Proxy = fun _ -> async { return WorkerResponse.WorkerError (SageFsError.Unexpected (exn "mock")) }
          WorkerBaseUrl = ""
          Projects = []
          WorkingDir = @"C:\test"
          AutoOpenNamespaces = true
          Workflow = WorkflowTypes.SessionWorkflow.Interactive
          RestartState = SageFs.RestartPolicy.emptyState
          AppGeneration = SageFs.AppRun.AppSlot.initial.Generation
          ProjectRoles = []
          AdoptedCore = None }

      let sidA = testSessionId "aa000011"
      let sidB = testSessionId "bb000011"
      let sidC = testSessionId "cc000011"
      let state =
        SageFs.SessionManager.ManagerState.empty
        |> SageFs.SessionManager.ManagerState.addSession sidA (mkSession sidA)
        |> SageFs.SessionManager.ManagerState.addSession sidB (mkSession sidB)
        |> SageFs.SessionManager.ManagerState.addSession sidC (mkSession sidC)

      SageFs.SessionManager.ManagerState.allInfos state
      |> List.length
      |> Expect.equal "3 sessions" 3
  ]

// ─── DaemonState + CLI subcommand integration ──────────────────────
//
// THE CLAIM this used to be three real-process tests for: `status`/`stop`
// exit non-zero with no daemon, and `--help` advertises current flags.
//
// Split:
//   * The DECISION ("no daemon found" -> print "No daemon running", exit 1)
//     is identical machinery for both subcommands (`DaemonState.readOnPort`
//     returning `None`) and is exhaustively pure-tested with every daemon
//     interaction injected: CliStopExitCodeTests.fs (`stopCommand`) and
//     CliStatusExitCodeTests.fs (`statusCommand`).
//   * Which subcommand a bare CLI arg routes to (`"status"`/`"stop"` ->
//     `CliCommand.Status`/`.Stop`) is a pure function of args, unit-tested
//     directly in CliStatusExitCodeTests.fs
//     ("Program.CliCommand.parse routes daemon subcommands").
//   * `--help`'s content (including that it never advertises the legacy
//     `--proj`/`--sln` flags) never touches daemon state, so it is proven
//     in-process via `Program.main` directly in CliFirstRunTests.fs
//     ("sagefs --help documents check and stop exit codes...") — no real
//     process needed to prove pure stdout content.
//
// What NONE of the above proves: that the actual compiled, spawned
// `SageFs.exe`/`SageFs.dll` binary parses real OS argv, reaches
// `DaemonState.readOnPort`'s REAL network probe, and propagates the exit
// code back across a genuine process boundary. That is a wire claim, and
// `status`'s and `stop`'s no-daemon paths exercise IDENTICAL wiring (both
// call only `readOnPort`; `stop`'s extra injected functions are never
// reached when there is no daemon) — so ONE retained smoke proves it for
// both.
[<Tests>]
let daemonCliTests =
  Integration.hostList "Daemon CLI subcommands" [

    testCase "SageFs status returns 1 when no daemon running (real binary, real argv, real exit code)" <| fun _ ->
      let psi = ProcessStartInfo()
      psi.FileName <- SageFsExe
      psi.Arguments <- "status --mcp-port 39990"
      psi.UseShellExecute <- false
      psi.RedirectStandardOutput <- true
      psi.CreateNoWindow <- true
      psi.Environment.["SAGEFS_DATA_DIR"] <- isolatedDataDir ()

      use proc = Process.Start(psi)
      let output = proc.StandardOutput.ReadToEnd()
      proc.WaitForExit(5000) |> ignore

      proc.ExitCode |> Expect.equal "exit code 1" 1
      output |> Expect.stringContains "says no daemon" "No daemon running"
  ]

// ─── Daemon startup fails closed on a bind failure ──────────────────
//
// THE FLAKE THIS PROVES FIXED: ci-pipeline.fsx runs several test tiers
// concurrently, all sharing one network namespace. Every harness reserves a
// daemon's ports by binding them, reading them back, then RELEASING them
// before the daemon itself binds — a window a concurrently-running tier's
// daemon can win. When that happened here, the daemon logged "Failed to bind
// to address ...: address already in use", then "Dashboard failed to start"
// — and then logged "SageFs daemon ready" anyway, before eventually exiting
// 0. Every test that had spawned it burned its whole readiness timeout
// polling a port nobody would ever answer on.
//
// TestPorts.reservePair (see SageFs.Tests.TestInfrastructure) makes the
// cross-tier race structurally impossible (disjoint per-tier port ranges).
// This is the OTHER half of the fix: even when a bind loses a race, the
// daemon itself must fail fast and say why, rather than announce readiness
// over a listener nobody can reach.
let private startupBindFailureCeiling = TimeSpan.FromSeconds 20.0

[<Tests>]
let daemonStartupFailsClosedTests =
  Integration.hostList "Daemon startup fails closed" [

    testCase "a taken dashboard port makes the daemon exit non-zero instead of announcing ready" <| fun _ ->
      let mcpPort, dashboardPort =
        SageFs.Tests.TestInfrastructure.TestPorts.reservePair ()

      // Hold the dashboard's own port for the whole test — exactly what a
      // losing tier's daemon does to the winner in the real flake.
      use occupyDashboard = new TcpListener(IPAddress.Loopback, dashboardPort)
      occupyDashboard.Start()

      let psi = ProcessStartInfo()
      psi.FileName <- SageFsExe
      psi.UseShellExecute <- false
      psi.CreateNoWindow <- true
      psi.WorkingDirectory <- testProjectDir
      psi.ArgumentList.Add "--mcp-port"
      psi.ArgumentList.Add(string mcpPort)
      psi.ArgumentList.Add "--no-resume"
      psi.Environment["SAGEFS_DATA_DIR"] <- isolatedDataDir ()
      // Redirected to FILES (never undrained pipes — see DashboardBrowserRunner
      // for why a pipe deadlocks the child before it can even log the failure).
      let dataDirForLogs = Path.Combine(Path.GetTempPath(), "sagefs-test", Guid.NewGuid().ToString("N"))
      Directory.CreateDirectory dataDirForLogs |> ignore
      let outLog = Path.Combine(dataDirForLogs, "stdout.log")
      psi.RedirectStandardOutput <- true
      psi.RedirectStandardError <- true

      use daemonProc = new Process(StartInfo = psi)
      use logWriter = new StreamWriter(outLog, append = false)
      let logLock = obj ()
      let writeLine (line: string) =
        if not (isNull line) then lock logLock (fun () -> logWriter.WriteLine line)
      daemonProc.OutputDataReceived.Add(fun e -> writeLine e.Data)
      daemonProc.ErrorDataReceived.Add(fun e -> writeLine e.Data)
      daemonProc.Start() |> ignore
      daemonProc.BeginOutputReadLine()
      daemonProc.BeginErrorReadLine()

      try
        let exited =
          SageFs.Tests.TestInfrastructure.waitFor
            (int startupBindFailureCeiling.TotalMilliseconds)
            (fun () -> daemonProc.HasExited)
        exited
        |> Expect.isTrue
             "the daemon must exit on its own once its dashboard bind fails, instead of hanging around claiming to be ready"

        daemonProc.WaitForExit(1000) |> ignore // flush the async readers
        lock logLock (fun () -> logWriter.Flush())

        daemonProc.ExitCode
        |> Expect.notEqual "a failed required bind must be a non-zero exit, not a quiet success" 0

        let logged = File.ReadAllText outLog
        logged
        |> Expect.stringContains "the failure is explained, not silent" "did not stay up"
        logged.Contains "SageFs daemon ready"
        |> Expect.isFalse "the daemon must never announce ready once a required listener failed to bind"
      finally
        try
          if not daemonProc.HasExited then
            daemonProc.Kill()
            daemonProc.WaitForExit(3000) |> ignore
        with _ -> ()
        try Directory.Delete(dataDirForLogs, true) with _ -> ()
  ]

// ─── Daemon lifecycle: start, status, stop ─────────────────────────

[<Tests>]
let daemonLifecycleTests =
  Integration.hostList "Daemon lifecycle" [

    testCase "start daemon, check status, stop" <| fun _ ->
      // Reserved via TestPorts (disjoint per-tier ranges) instead of a fixed
      // 37800+rand(100) span, which could collide with another
      // concurrently-running tier's daemon on the self-hosted runner.
      let port, _dashboardPort = SageFs.Tests.TestInfrastructure.TestPorts.reservePair ()
      let psi = ProcessStartInfo()
      psi.FileName <- SageFsExe
      psi.Arguments <- sprintf "--mcp-port %d" port
      psi.UseShellExecute <- false
      psi.CreateNoWindow <- true
      psi.WorkingDirectory <- testProjectDir
      // Isolate persisted state so the daemon never resumes real ~/.SageFs
      // sessions; status/stop below share the same data dir.
      let dataDir = isolatedDataDir ()
      psi.Environment.["SAGEFS_DATA_DIR"] <- dataDir

      let daemonProc = Process.Start(psi)
      try
        // Wait for daemon to respond on HTTP — event-driven, no sleep-poll.
        let ready =
          SageFs.Tests.TestInfrastructure.waitFor
            (int Timeouts.integrationDaemonReady.TotalMilliseconds)
            (fun () -> (DaemonState.readOnPort port) |> Option.isSome)
        let info = if ready then DaemonState.readOnPort port else None

        info |> Expect.isSome "daemon should respond within the readiness ceiling"
        let di = info.Value
        di.Port |> Expect.equal "port matches" port
        di.Pid |> Expect.equal "PID matches" daemonProc.Id

        // Run SageFs status
        let statusPsi = ProcessStartInfo()
        statusPsi.FileName <- SageFsExe
        statusPsi.Arguments <- sprintf "status --mcp-port %d" port
        statusPsi.UseShellExecute <- false
        statusPsi.RedirectStandardOutput <- true
        statusPsi.CreateNoWindow <- true
        statusPsi.Environment.["SAGEFS_DATA_DIR"] <- dataDir

        use statusProc = Process.Start(statusPsi)
        let statusOutput = statusProc.StandardOutput.ReadToEnd()
        statusProc.WaitForExit(5000) |> ignore

        statusProc.ExitCode |> Expect.equal "status exits 0" 0
        statusOutput |> Expect.stringContains "shows running" "running"
        statusOutput
        |> Expect.stringContains "shows PID" (string daemonProc.Id)

        // Run SageFs stop
        let stopPsi = ProcessStartInfo()
        stopPsi.FileName <- SageFsExe
        stopPsi.Arguments <- sprintf "stop --mcp-port %d" port
        stopPsi.UseShellExecute <- false
        stopPsi.RedirectStandardOutput <- true
        stopPsi.CreateNoWindow <- true
        stopPsi.Environment.["SAGEFS_DATA_DIR"] <- dataDir

        use stopProc = Process.Start(stopPsi)
        let stopOutput = stopProc.StandardOutput.ReadToEnd()
        stopProc.WaitForExit(5000) |> ignore

        stopProc.ExitCode |> Expect.equal "stop exits 0" 0
        stopOutput |> Expect.stringContains "reports the stopped daemon's pid" (sprintf "Daemon stopped (PID %d)" daemonProc.Id)

        // Verify daemon process actually exited — event-driven, no sleep-poll.
        let exited =
          SageFs.Tests.TestInfrastructure.waitFor
            5000
            (fun () -> try daemonProc.HasExited with _ -> true)
        exited |> Expect.isTrue "daemon process should have exited"

        // Verify daemon is no longer responding
        DaemonState.readOnPort port
        |> Expect.isNone "daemon should no longer respond after stop"
      finally
        // Ensure cleanup even if test fails
        try
          if not daemonProc.HasExited then
            daemonProc.Kill()
            daemonProc.WaitForExit(3000) |> ignore
        with _ -> ()
  ]

// ─── SessionManager lifecycle: spawn, eval, stop ───────────────────

/// Helper to clean up a session in a finally block.
let cleanupSession
  (mgr: MailboxProcessor<SageFs.SessionManager.SessionCommand>)
  (sessionId: SessionId)
  =
  try
    mgr.PostAndAsyncReply(fun reply ->
      SageFs.SessionManager.SessionCommand.StopSession(sessionId, reply))
    |> Async.RunSynchronously |> ignore
  with _ -> ()

[<Tests>]
let sessionManagerLifecycleTests =
  Integration.hostList "SessionManager lifecycle" [

    // THE CLAIM: the mailbox actually routes to a REAL worker process — create
    // spawns it, the proxy it hands back reaches the real worker's real HTTP
    // eval endpoint once Ready, status genuinely accumulates eval count, and
    // stop genuinely tears the worker down. This is a WIRE claim (real process,
    // real HTTP), not a decision: nothing here is a candidate for DST.
    testTask "create session, eval code, stop session" {
      let cts = new CancellationTokenSource(int Timeouts.integrationDaemonReady.TotalMilliseconds)
      let mgr, _ = SageFs.SessionManager.create cts.Token ignore (fun _ _ -> ()) (fun _ _ -> ()) ignore (fun _ _ -> ()) (fun _ _ -> ()) (fun _ _ -> ())

      let! createResult =
        mgr.PostAndAsyncReply(fun reply ->
          SageFs.SessionManager.SessionCommand.CreateSession(
            [], sampleProjectDir, true, WorkflowTypes.SessionWorkflow.Interactive, reply))
        |> Async.StartAsTask

      match createResult with
      | Error err -> failwithf "create failed: %s" (SageFsError.describe err)
      | Ok info ->
      try
        SessionId.value info.Id
        |> Expect.isNotNull "has session id"
        SessionLifecycleStatus.workerPid info.Status
        |> Expect.isSome "has worker PID"

        // CreateSession answers at spawn; the proxy is routable only once the
        // worker reports Ready (before that it is the pending proxy).
        let! (ready: Result<unit, SageFsError>) =
          mgr.PostAndAsyncReply(fun reply ->
            SageFs.SessionManager.SessionCommand.AwaitReady(info.Id, reply))
          |> Async.StartAsTask
        ready |> Expect.isOk "the worker reaches Ready before the first eval"

        let! (session: SageFs.SessionManager.ManagedSession option) =
          mgr.PostAndAsyncReply(fun reply ->
            SageFs.SessionManager.SessionCommand.GetSession(
              info.Id, reply))
          |> Async.StartAsTask
        session |> Expect.isSome "session exists"

        let proxy = session.Value.Proxy

        // Eval simple code
        let! (evalResp: WorkerResponse) =
          proxy (WorkerMessage.EvalCode("let x = 42;;", "e1"))
          |> Async.StartAsTask
        match evalResp with
        | WorkerResponse.EvalResult("e1", Ok output, _, _) ->
          output |> Expect.stringContains "has 42" "42"
        | WorkerResponse.EvalResult(_, Error e, _, _) ->
          failwithf "eval error: %s" (SageFsError.describe e)
        | other ->
          failwithf "unexpected eval response: %A" other

        // Get status — should show at least 1 eval
        let! (statusResp: WorkerResponse) =
          proxy (WorkerMessage.GetStatus "s1")
          |> Async.StartAsTask
        match statusResp with
        | WorkerResponse.StatusResult(_, snap) ->
          Expect.isTrue "at least 1 eval"
            (snap.EvalCount > 0)
        | other ->
          failwithf "unexpected status response: %A" other

        // Stop session
        let! stopResult =
          mgr.PostAndAsyncReply(fun reply ->
            SageFs.SessionManager.SessionCommand.StopSession(
              info.Id, reply))
          |> Async.StartAsTask
        stopResult |> Expect.isOk "stop succeeded"

        // Verify session removed
        let! (sessions: SageFs.WorkerProtocol.SessionInfo list) =
          mgr.PostAndAsyncReply(fun reply ->
            SageFs.SessionManager.SessionCommand.ListSessions reply)
          |> Async.StartAsTask
        sessions.Length |> Expect.equal "no sessions" 0
      finally
        cleanupSession mgr info.Id
        cts.Dispose()
    }

    // Collapsed real-process smoke (was two tests: "worker crash is detected
    // and the session is restarted on a new worker" + "multiple sessions are
    // independent"). The DECISIONS those tests asserted — restart-on-crash
    // policy and session-routing independence — are already property-tested
    // by the pure DST sims, folding the REAL extracted decision functions:
    //   * WorkerLifecycleSimTests.fs — "no-stale-pid-applied" holds when
    //     traces are folded through the real `WorkerEventGuard.classify*`
    //     functions (the exact pid-blind-restart-race decision this test's
    //     name refers to), and is VIOLATED under the pre-fix pidBlind twin.
    //   * SimulationTests.fs — `Invariants.all` holds when traces are folded
    //     through the real `RestartPolicy`/`SessionLifecycle` backoff/restart
    //     decision, over 300 generated seeds per property.
    // What only a real-process test can prove is the WIRE: two real OS worker
    // processes are independently routable, and killing one real process is
    // observed as a genuine restart onto a new pid while the other session is
    // undisturbed. This is the minimal union of both.
    testTask "two independent sessions stay routable, and a killed worker restarts on a new pid" {
      let cts = new CancellationTokenSource(int Timeouts.integrationDaemonReady.TotalMilliseconds)
      let mgr, _ = SageFs.SessionManager.create cts.Token ignore (fun _ _ -> ()) (fun _ _ -> ()) ignore (fun _ _ -> ()) (fun _ _ -> ()) (fun _ _ -> ())

      // Two sessions for the SAME directory are one session by design — the
      // owner rejects the duplicate — so the second lives in its own dir.
      let otherDir = System.IO.Directory.CreateTempSubdirectory("sagefs-multi-").FullName
      let create (dir: string) =
        mgr.PostAndAsyncReply(fun reply ->
          SageFs.SessionManager.SessionCommand.CreateSession(
            [], dir, true, WorkflowTypes.SessionWorkflow.Interactive, reply))
        |> Async.StartAsTask

      let result1 = create sampleProjectDir
      let result2 = create otherDir

      let! result1 = result1
      let! result2 = result2

      match result1, result2 with
      | Ok info1, Ok info2 ->
        try
          // Both have different worker PIDs — proves independent wire routing.
          SessionLifecycleStatus.workerPid info1.Status
          |> Expect.isSome "session 1 has PID"
          SessionLifecycleStatus.workerPid info2.Status
          |> Expect.isSome "session 2 has PID"
          (SessionLifecycleStatus.workerPid info1.Status).Value
          |> Expect.notEqual "different PIDs"
            (SessionLifecycleStatus.workerPid info2.Status).Value

          // Proxies are routable only once each worker reports Ready.
          for id in [ info1.Id; info2.Id ] do
            let! (ready: Result<unit, SageFsError>) =
              mgr.PostAndAsyncReply(fun reply ->
                SageFs.SessionManager.SessionCommand.AwaitReady(id, reply))
              |> Async.StartAsTask
            ready |> Expect.isOk "each worker reaches Ready before its eval"

          let getProxy id =
            mgr.PostAndAsyncReply(fun reply ->
              SageFs.SessionManager.SessionCommand.GetSession(
                id, reply))
            |> Async.StartAsTask

          let! (s1: SageFs.SessionManager.ManagedSession option) = getProxy info1.Id
          let! (s2: SageFs.SessionManager.ManagedSession option) = getProxy info2.Id
          let proxy1 = s1.Value.Proxy
          let proxy2 = s2.Value.Proxy

          // Eval different code in each session — proves routing reaches the
          // correct real worker process, not just the correct in-proc record.
          let! (resp1: WorkerResponse) =
            proxy1 (
              WorkerMessage.EvalCode(
                "let session1Val = 111;;", "r1"))
            |> Async.StartAsTask
          let! (resp2: WorkerResponse) =
            proxy2 (
              WorkerMessage.EvalCode(
                "let session2Val = 222;;", "r2"))
            |> Async.StartAsTask

          match resp1 with
          | WorkerResponse.EvalResult(_, Ok output, _, _) ->
            output
            |> Expect.stringContains "session 1 has 111" "111"
          | _ -> failwithf "unexpected: %A" resp1

          match resp2 with
          | WorkerResponse.EvalResult(_, Ok output, _, _) ->
            output
            |> Expect.stringContains "session 2 has 222" "222"
          | _ -> failwithf "unexpected: %A" resp2

          // Kill session 1's real worker process externally.
          let pid1 = (SessionLifecycleStatus.workerPid info1.Status).Value
          try
            let p = Process.GetProcessById(pid1)
            p.Kill()
            p.WaitForExit(5000) |> ignore
          with _ -> ()

          // Supervision restarts the crashed worker with backoff
          // (RestartPolicy): the session stays registered and comes back on
          // a new real process, while session 2 stays undisturbed.
          let! restarted =
            SageFs.Tests.TestInfrastructure.waitForAsync
              (int Timeouts.integrationWorkerRestart.TotalMilliseconds)
              (fun () -> task {
                let! sessions =
                  mgr.PostAndAsyncReply(fun reply ->
                    SageFs.SessionManager.SessionCommand.ListSessions reply)
                  |> Async.StartAsTask
                return
                  sessions
                  |> List.tryFind (fun s -> s.Id = info1.Id)
                  |> Option.bind (fun s -> SessionLifecycleStatus.workerPid s.Status)
                  |> Option.filter (fun p -> p <> pid1)
                  |> Option.isSome })

          restarted
          |> Expect.isTrue "the crashed session is restarted on a new worker process"

          let! (sessionsAfterRestart: SageFs.WorkerProtocol.SessionInfo list) =
            mgr.PostAndAsyncReply(fun reply ->
              SageFs.SessionManager.SessionCommand.ListSessions reply)
            |> Async.StartAsTask
          sessionsAfterRestart
          |> List.tryFind (fun s -> s.Id = info2.Id)
          |> Option.bind (fun s -> SessionLifecycleStatus.workerPid s.Status)
          |> Expect.equal "session 2's worker is untouched by session 1's crash"
            (SessionLifecycleStatus.workerPid info2.Status)

          // Stop both
          let! (_: Result<unit, SageFsError>) =
            mgr.PostAndAsyncReply(fun reply ->
              SageFs.SessionManager.SessionCommand.StopSession(
                info1.Id, reply))
            |> Async.StartAsTask
          let! (_: Result<unit, SageFsError>) =
            mgr.PostAndAsyncReply(fun reply ->
              SageFs.SessionManager.SessionCommand.StopSession(
                info2.Id, reply))
            |> Async.StartAsTask

          let! (afterStop: SageFs.WorkerProtocol.SessionInfo list) =
            mgr.PostAndAsyncReply(fun reply ->
              SageFs.SessionManager.SessionCommand.ListSessions
                reply)
            |> Async.StartAsTask
          afterStop.Length |> Expect.equal "no sessions" 0
        finally
          cleanupSession mgr info1.Id
          cleanupSession mgr info2.Id
      | Error err, _ ->
        failwithf "session 1 create failed: %s" (SageFsError.describe err)
      | _, Error err ->
        failwithf "session 2 create failed: %s" (SageFsError.describe err)
      cts.Dispose()
      try System.IO.Directory.Delete(otherDir, true) with _ -> ()
    }
  ]
