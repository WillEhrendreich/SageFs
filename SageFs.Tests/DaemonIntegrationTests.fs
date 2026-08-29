module SageFs.Tests.DaemonIntegrationTests

open System
open System.Diagnostics
open System.IO
open System.Threading
open Expecto
open Expecto.Flip
open SageFs
open SageFs.Server
open SageFs.WorkerProtocol
open SageFs.Tests.SharedGenerators

// ─── Helpers ───────────────────────────────────────────────────────

let testProjectDir =
  Path.GetFullPath(
    Path.Combine(__SOURCE_DIRECTORY__, "..", "SageFs.Tests"))

let SageFsExe =
  let localExe =
    Path.Combine(
      __SOURCE_DIRECTORY__, "..", "SageFs", "bin", "Debug", "net11.0", "SageFs.exe")
  let toolDir =
    Path.Combine(
      Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
      ".dotnet", "tools")
  let exe = Path.Combine(toolDir, "SageFs.exe")
  if File.Exists localExe then localExe
  elif File.Exists exe then exe
  else "SageFs" // fall back to PATH

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
        Status = SessionStatus.Ready
        FaultReason = None
        WorkerPid = Some 1234
        WorkerPort = None
        Workflow = WorkflowTypes.SessionWorkflow.Interactive
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
        Status = SessionStatus.Ready
        FaultReason = None
        WorkerPid = None
        WorkerPort = None
        Workflow = WorkflowTypes.SessionWorkflow.Interactive
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
          Status = SessionStatus.Ready
          FaultReason = None
          WorkerPid = None
          WorkerPort = None
          Workflow = WorkflowTypes.SessionWorkflow.Interactive
        }
        { Info = info
          Process = new Process()
          Proxy = fun _ -> async { return WorkerResponse.WorkerError (SageFsError.Unexpected (exn "mock")) }
          WorkerBaseUrl = ""
          Projects = []
          WorkingDir = @"C:\test"
          AutoOpenNamespaces = true
          Workflow = WorkflowTypes.SessionWorkflow.Interactive
          RestartState = SageFs.RestartPolicy.emptyState }

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

[<Tests>]
let daemonCliTests =
  ptestList "[Integration] Daemon CLI subcommands" [

    testCase "SageFs status returns 1 when no daemon running" <| fun _ ->
      let psi = ProcessStartInfo()
      psi.FileName <- SageFsExe
      psi.Arguments <- "status --mcp-port 39990"
      psi.UseShellExecute <- false
      psi.RedirectStandardOutput <- true
      psi.CreateNoWindow <- true

      use proc = Process.Start(psi)
      let output = proc.StandardOutput.ReadToEnd()
      proc.WaitForExit(5000) |> ignore

      proc.ExitCode |> Expect.equal "exit code 1" 1
      output |> Expect.stringContains "says no daemon" "No daemon running"

    testCase "SageFs stop returns 0 when no daemon running" <| fun _ ->
      let psi = ProcessStartInfo()
      psi.FileName <- SageFsExe
      psi.Arguments <- "stop --mcp-port 39990"
      psi.UseShellExecute <- false
      psi.RedirectStandardOutput <- true
      psi.CreateNoWindow <- true

      use proc = Process.Start(psi)
      let output = proc.StandardOutput.ReadToEnd()
      proc.WaitForExit(5000) |> ignore

      proc.ExitCode |> Expect.equal "exit code 0" 0
      output |> Expect.stringContains "says no daemon" "No daemon running"

    testCase "SageFs --help mentions daemon subcommands" <| fun _ ->
      let psi = ProcessStartInfo()
      psi.FileName <- SageFsExe
      psi.Arguments <- "--help"
      psi.UseShellExecute <- false
      psi.RedirectStandardOutput <- true
      psi.CreateNoWindow <- true

      use proc = Process.Start(psi)
      let output = proc.StandardOutput.ReadToEnd()
      proc.WaitForExit(5000) |> ignore

      proc.ExitCode |> Expect.equal "exit code 0" 0
      output |> Expect.stringContains "mentions daemon" "daemon"
      output |> Expect.stringContains "mentions stop" "stop"
      output |> Expect.stringContains "mentions status" "status"
      (output.Contains "--proj")
      |> Expect.isFalse "help should not advertise legacy startup project flags"
      (output.Contains "--sln")
      |> Expect.isFalse "help should not advertise legacy startup solution flags"
  ]

// ─── Daemon lifecycle: start, status, stop ─────────────────────────

[<Tests>]
let daemonLifecycleTests =
  ptestList "[Integration] Daemon lifecycle" [

    testCase "start daemon, check status, stop" <| fun _ ->
      // Start daemon in background with a unique port to avoid conflicts
      let port = 37800 + (Random().Next(100))
      let psi = ProcessStartInfo()
      psi.FileName <- SageFsExe
      psi.Arguments <- sprintf "--mcp-port %d" port
      psi.UseShellExecute <- false
      psi.CreateNoWindow <- true
      psi.WorkingDirectory <- testProjectDir
      // Isolate persisted state so the daemon never resumes real ~/.SageFs sessions.
      psi.Environment.["SAGEFS_DATA_DIR"] <-
        Path.Combine(Path.GetTempPath(), "sagefs-test", Guid.NewGuid().ToString("N"))

      let daemonProc = Process.Start(psi)
      try
        // Wait for daemon to respond on HTTP
        let mutable attempts = 0
        let mutable info : DaemonInfo option = None
        while attempts < 60 && info.IsNone do
          Thread.Sleep(100)
          info <- DaemonState.readOnPort port
          attempts <- attempts + 1

        info |> Expect.isSome "daemon should respond within 30s"
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

        use stopProc = Process.Start(stopPsi)
        let stopOutput = stopProc.StandardOutput.ReadToEnd()
        stopProc.WaitForExit(5000) |> ignore

        stopProc.ExitCode |> Expect.equal "stop exits 0" 0
        stopOutput |> Expect.stringContains "says shutting down" "hutting down"

        // Verify daemon process actually exited (poll with timeout)
        let mutable exited = false
        let sw = System.Diagnostics.Stopwatch.StartNew()
        while not exited && sw.ElapsedMilliseconds < 5000L do
          exited <- try daemonProc.HasExited with _ -> true
          if not exited then Thread.Sleep(100)
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
  ptestList "[Integration] SessionManager lifecycle" [

    testTask "create session, eval code, stop session" {
      let cts = new CancellationTokenSource(120_000)
      let mgr, _ = SageFs.SessionManager.create cts.Token ignore (fun _ _ _ -> ()) (fun _ _ -> ()) ignore (fun _ _ -> ()) (fun _ _ -> ())

      let! createResult =
        mgr.PostAndAsyncReply(fun reply ->
          SageFs.SessionManager.SessionCommand.CreateSession(
            [], testProjectDir, true, WorkflowTypes.SessionWorkflow.Interactive, reply))
        |> Async.StartAsTask

      match createResult with
      | Error err -> failwithf "create failed: %s" (SageFsError.describe err)
      | Ok info ->
      try
        SessionId.value info.Id
        |> Expect.isNotNull "has session id"
        info.WorkerPid
        |> Expect.isSome "has worker PID"

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

    testTask "worker crash is detected and session cleaned up" {
      let cts = new CancellationTokenSource(120_000)
      let mgr, _ = SageFs.SessionManager.create cts.Token ignore (fun _ _ _ -> ()) (fun _ _ -> ()) ignore (fun _ _ -> ()) (fun _ _ -> ())

      let! createResult =
        mgr.PostAndAsyncReply(fun reply ->
          SageFs.SessionManager.SessionCommand.CreateSession(
            [], testProjectDir, true, WorkflowTypes.SessionWorkflow.Interactive, reply))
        |> Async.StartAsTask

      match createResult with
      | Error err -> failwithf "create failed: %s" (SageFsError.describe err)
      | Ok info ->
      try
        info.WorkerPid |> Expect.isSome "has worker PID"
        let pid = info.WorkerPid.Value

        // Kill the worker process externally
        try
          let p = Process.GetProcessById(pid)
          p.Kill()
          p.WaitForExit(5000) |> ignore
        with _ -> ()

        // Give the WorkerExited message time to propagate (async poll)
        let mutable cleaned = false
        let sw = System.Diagnostics.Stopwatch.StartNew()
        while not cleaned && sw.ElapsedMilliseconds < 2000L do
          let! (sessions: SageFs.WorkerProtocol.SessionInfo list) =
            mgr.PostAndAsyncReply(fun reply ->
              SageFs.SessionManager.SessionCommand.ListSessions reply)
            |> Async.StartAsTask
          cleaned <-
            sessions |> List.forall (fun s -> s.Id <> info.Id)
          if not cleaned then do! System.Threading.Tasks.Task.Delay 100

        cleaned
        |> Expect.isTrue
          "session should be removed after worker crash"
      finally
        cleanupSession mgr info.Id
        cts.Dispose()
    }

    testTask "multiple sessions are independent" {
      let cts = new CancellationTokenSource(120_000)
      let mgr, _ = SageFs.SessionManager.create cts.Token ignore (fun _ _ _ -> ()) (fun _ _ -> ()) ignore (fun _ _ -> ()) (fun _ _ -> ())

      let create () =
        mgr.PostAndAsyncReply(fun reply ->
          SageFs.SessionManager.SessionCommand.CreateSession(
            [], testProjectDir, true, WorkflowTypes.SessionWorkflow.Interactive, reply))
        |> Async.StartAsTask

      let result1 = create ()
      let result2 = create ()

      let! result1 = result1
      let! result2 = result2

      match result1, result2 with
      | Ok info1, Ok info2 ->
        try
          // Sessions have different IDs
          info1.Id
          |> Expect.notEqual "different session ids" info2.Id

          // Both have different worker PIDs
          info1.WorkerPid
          |> Expect.isSome "session 1 has PID"
          info2.WorkerPid
          |> Expect.isSome "session 2 has PID"
          info1.WorkerPid.Value
          |> Expect.notEqual "different PIDs"
            info2.WorkerPid.Value

          // Get proxies
          let getProxy id =
            mgr.PostAndAsyncReply(fun reply ->
              SageFs.SessionManager.SessionCommand.GetSession(
                id, reply))
            |> Async.StartAsTask

          let! (s1: SageFs.SessionManager.ManagedSession option) = getProxy info1.Id
          let! (s2: SageFs.SessionManager.ManagedSession option) = getProxy info2.Id
          let proxy1 = s1.Value.Proxy
          let proxy2 = s2.Value.Proxy

          // Eval different code in each session
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

          // List sessions — should have 2
          let! (sessions: SageFs.WorkerProtocol.SessionInfo list) =
            mgr.PostAndAsyncReply(fun reply ->
              SageFs.SessionManager.SessionCommand.ListSessions
                reply)
            |> Async.StartAsTask
          sessions.Length |> Expect.equal "2 sessions" 2

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
    }
  ]
