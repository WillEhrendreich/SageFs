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

// ─── Helpers ───────────────────────────────────────────────────────

let testProjectDir =
  Path.GetFullPath(
    Path.Combine(__SOURCE_DIRECTORY__, "..", "SageFs.Tests"))

let SageFsExe =
  let toolDir =
    Path.Combine(
      Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
      ".dotnet", "tools")
  let exe = Path.Combine(toolDir, "SageFs.exe")
  if File.Exists exe then exe
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
      let info : SessionInfo = {
        Id = "test-1"
        Name = None
        Projects = ["Foo.fsproj"]
        WorkingDirectory = @"C:\test"
        SolutionRoot = None
        CreatedAt = DateTime.UtcNow
        LastActivity = DateTime.UtcNow
        Status = SessionStatus.Ready
        WorkerPid = Some 1234
      }
      let session : SageFs.SessionManager.ManagedSession = {
        Info = info
        Process = new Process()
        Proxy = fun _ -> async { return WorkerResponse.WorkerError (SageFsError.Unexpected (exn "mock")) }
        Projects = ["Foo.fsproj"]
        WorkingDir = @"C:\test"
        RestartState = SageFs.RestartPolicy.emptyState
      }
      let state =
        SageFs.SessionManager.ManagerState.empty
        |> SageFs.SessionManager.ManagerState.addSession "test-1" session
      SageFs.SessionManager.ManagerState.tryGetSession "test-1" state
      |> Expect.isSome "should find session"

    testCase "removeSession then tryGetSession returns None" <| fun _ ->
      let info : SessionInfo = {
        Id = "test-2"
        Name = None
        Projects = []
        WorkingDirectory = @"C:\test"
        SolutionRoot = None
        CreatedAt = DateTime.UtcNow
        LastActivity = DateTime.UtcNow
        Status = SessionStatus.Ready
        WorkerPid = None
      }
      let session : SageFs.SessionManager.ManagedSession = {
        Info = info
        Process = new Process()
        Proxy = fun _ -> async { return WorkerResponse.WorkerError (SageFsError.Unexpected (exn "mock")) }
        Projects = []
        WorkingDir = @"C:\test"
        RestartState = SageFs.RestartPolicy.emptyState
      }
      let state =
        SageFs.SessionManager.ManagerState.empty
        |> SageFs.SessionManager.ManagerState.addSession "test-2" session
        |> SageFs.SessionManager.ManagerState.removeSession "test-2"
      SageFs.SessionManager.ManagerState.tryGetSession "test-2" state
      |> Expect.isNone "should not find removed session"

    testCase "allInfos returns all session infos" <| fun _ ->
      let mkSession id : SageFs.SessionManager.ManagedSession =
        let info : SessionInfo = {
          Id = id
          Name = None
          Projects = []
          WorkingDirectory = @"C:\test"
          SolutionRoot = None
          CreatedAt = DateTime.UtcNow
          LastActivity = DateTime.UtcNow
          Status = SessionStatus.Ready
          WorkerPid = None
        }
        { Info = info
          Process = new Process()
          Proxy = fun _ -> async { return WorkerResponse.WorkerError (SageFsError.Unexpected (exn "mock")) }
          Projects = []
          WorkingDir = @"C:\test"
          RestartState = SageFs.RestartPolicy.emptyState }

      let state =
        SageFs.SessionManager.ManagerState.empty
        |> SageFs.SessionManager.ManagerState.addSession "a" (mkSession "a")
        |> SageFs.SessionManager.ManagerState.addSession "b" (mkSession "b")
        |> SageFs.SessionManager.ManagerState.addSession "c" (mkSession "c")

      SageFs.SessionManager.ManagerState.allInfos state
      |> List.length
      |> Expect.equal "3 sessions" 3
  ]

// ─── DaemonState + CLI subcommand integration ──────────────────────

[<Tests>]
let daemonCliTests =
  testList "[Integration] Daemon CLI subcommands" [

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
  ]

// ─── Daemon lifecycle: start, status, stop ─────────────────────────

[<Tests>]
let daemonLifecycleTests =
  testList "[Integration] Daemon lifecycle" [

    testCase "start daemon, check status, stop" <| fun _ ->
      // Start daemon in background with a unique port to avoid conflicts
      let port = 37800 + (Random().Next(100))
      let psi = ProcessStartInfo()
      psi.FileName <- SageFsExe
      psi.Arguments <- sprintf "--mcp-port %d" port
      psi.UseShellExecute <- false
      psi.CreateNoWindow <- true
      psi.WorkingDirectory <- testProjectDir

      let daemonProc = Process.Start(psi)
      try
        // Wait for daemon to respond on HTTP
        let mutable attempts = 0
        let mutable info : DaemonInfo option = None
        while attempts < 60 && info.IsNone do
          Thread.Sleep(500)
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

        // Verify daemon process actually exited
        Thread.Sleep(2000)
        let exited =
          try daemonProc.HasExited with _ -> true
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

// ─── ClientMode unit tests ─────────────────────────────────────────

[<Tests>]
let clientModeTests =
  testList "ClientMode" [
    ptestCase "tryConnect returns None when no daemon" <| fun _ ->
      // Skipped: depends on no daemon running, fails when SageFs is active
      ClientMode.tryConnect ()
      |> Expect.isNone "should be None with no daemon"
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
  testList "[Integration] SessionManager lifecycle" [

    testCase "create session, eval code, stop session" <| fun _ ->
      use cts = new CancellationTokenSource(120_000)
      let mgr = SageFs.SessionManager.create cts.Token ignore

      let createResult =
        mgr.PostAndAsyncReply(fun reply ->
          SageFs.SessionManager.SessionCommand.CreateSession(
            [], testProjectDir, reply))
        |> Async.RunSynchronously

      match createResult with
      | Error err -> failwithf "create failed: %s" (SageFsError.describe err)
      | Ok info ->
      try
        info.Id
        |> Expect.isNotNull "has session id"
        info.WorkerPid
        |> Expect.isSome "has worker PID"

        let session =
          mgr.PostAndAsyncReply(fun reply ->
            SageFs.SessionManager.SessionCommand.GetSession(
              info.Id, reply))
          |> Async.RunSynchronously
        session |> Expect.isSome "session exists"

        let proxy = session.Value.Proxy

        // Eval simple code
        let evalResp =
          proxy (WorkerMessage.EvalCode("let x = 42;;", "e1"))
          |> Async.RunSynchronously
        match evalResp with
        | WorkerResponse.EvalResult("e1", Ok output, _) ->
          output |> Expect.stringContains "has 42" "42"
        | WorkerResponse.EvalResult(_, Error e, _) ->
          failwithf "eval error: %s" (SageFsError.describe e)
        | other ->
          failwithf "unexpected eval response: %A" other

        // Get status — should show at least 1 eval
        let statusResp =
          proxy (WorkerMessage.GetStatus "s1")
          |> Async.RunSynchronously
        match statusResp with
        | WorkerResponse.StatusResult(_, snap) ->
          Expect.isTrue "at least 1 eval"
            (snap.EvalCount > 0)
        | other ->
          failwithf "unexpected status response: %A" other

        // Stop session
        let stopResult =
          mgr.PostAndAsyncReply(fun reply ->
            SageFs.SessionManager.SessionCommand.StopSession(
              info.Id, reply))
          |> Async.RunSynchronously
        stopResult |> Expect.isOk "stop succeeded"

        // Verify session removed
        let sessions =
          mgr.PostAndAsyncReply(fun reply ->
            SageFs.SessionManager.SessionCommand.ListSessions reply)
          |> Async.RunSynchronously
        sessions.Length |> Expect.equal "no sessions" 0
      finally
        cleanupSession mgr info.Id

    testCase "worker crash is detected and session cleaned up" <| fun _ ->
      use cts = new CancellationTokenSource(120_000)
      let mgr = SageFs.SessionManager.create cts.Token ignore

      let createResult =
        mgr.PostAndAsyncReply(fun reply ->
          SageFs.SessionManager.SessionCommand.CreateSession(
            [], testProjectDir, reply))
        |> Async.RunSynchronously

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

        // Give the WorkerExited message time to propagate
        let mutable attempts = 0
        let mutable cleaned = false
        while attempts < 20 && not cleaned do
          Thread.Sleep(100)
          let sessions =
            mgr.PostAndAsyncReply(fun reply ->
              SageFs.SessionManager.SessionCommand.ListSessions reply)
            |> Async.RunSynchronously
          cleaned <-
            sessions |> List.forall (fun s -> s.Id <> info.Id)
          attempts <- attempts + 1

        cleaned
        |> Expect.isTrue
          "session should be removed after worker crash"
      finally
        cleanupSession mgr info.Id

    testCase "multiple sessions are independent" <| fun _ ->
      use cts = new CancellationTokenSource(120_000)
      let mgr = SageFs.SessionManager.create cts.Token ignore

      let create () =
        mgr.PostAndAsyncReply(fun reply ->
          SageFs.SessionManager.SessionCommand.CreateSession(
            [], testProjectDir, reply))
        |> Async.RunSynchronously

      let result1 = create ()
      let result2 = create ()

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
            let s =
              mgr.PostAndAsyncReply(fun reply ->
                SageFs.SessionManager.SessionCommand.GetSession(
                  id, reply))
              |> Async.RunSynchronously
            s.Value.Proxy

          let proxy1 = getProxy info1.Id
          let proxy2 = getProxy info2.Id

          // Eval different code in each session
          let resp1 =
            proxy1 (
              WorkerMessage.EvalCode(
                "let session1Val = 111;;", "r1"))
            |> Async.RunSynchronously
          let resp2 =
            proxy2 (
              WorkerMessage.EvalCode(
                "let session2Val = 222;;", "r2"))
            |> Async.RunSynchronously

          match resp1 with
          | WorkerResponse.EvalResult(_, Ok output, _) ->
            output
            |> Expect.stringContains "session 1 has 111" "111"
          | _ -> failwithf "unexpected: %A" resp1

          match resp2 with
          | WorkerResponse.EvalResult(_, Ok output, _) ->
            output
            |> Expect.stringContains "session 2 has 222" "222"
          | _ -> failwithf "unexpected: %A" resp2

          // List sessions — should have 2
          let sessions =
            mgr.PostAndAsyncReply(fun reply ->
              SageFs.SessionManager.SessionCommand.ListSessions
                reply)
            |> Async.RunSynchronously
          sessions.Length |> Expect.equal "2 sessions" 2

          // Stop both
          mgr.PostAndAsyncReply(fun reply ->
            SageFs.SessionManager.SessionCommand.StopSession(
              info1.Id, reply))
          |> Async.RunSynchronously |> ignore
          mgr.PostAndAsyncReply(fun reply ->
            SageFs.SessionManager.SessionCommand.StopSession(
              info2.Id, reply))
          |> Async.RunSynchronously |> ignore

          let afterStop =
            mgr.PostAndAsyncReply(fun reply ->
              SageFs.SessionManager.SessionCommand.ListSessions
                reply)
            |> Async.RunSynchronously
          afterStop.Length |> Expect.equal "no sessions" 0
        finally
          cleanupSession mgr info1.Id
          cleanupSession mgr info2.Id
      | Error err, _ ->
        failwithf "session 1 create failed: %s" (SageFsError.describe err)
      | _, Error err ->
        failwithf "session 2 create failed: %s" (SageFsError.describe err)
  ]
