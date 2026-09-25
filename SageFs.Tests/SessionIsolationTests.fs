module SageFs.Tests.SessionIsolationTests

open System
open System.IO
open System.Threading.Tasks
open Expecto
open Expecto.Flip
open SageFs
open SageFs.McpTools
open SageFs.McpSessionRouting
open SageFs.Tests.TestInfrastructure
open SageFs.Tests.SharedGenerators
open System.Collections.Concurrent

/// Tests that MCP session switch does NOT leak into other clients.
/// These tests define the contract for per-client session isolation.
module McpSessionIsolation =

  /// Create a McpContext with a tracking dispatch that records all messages sent.
  /// Uses inMemoryPersistence so event-append tests work correctly.
  let ctxWithTracking sessionId =
    let dispatched = System.Collections.Generic.List<SageFsMsg>()
    let sessionMap = ConcurrentDictionary<string, string>()
    sessionMap.["test"] <- sessionId
    let ctx =
      { FrictionStore = None
        DiagnosticsChanged = (Event<Features.DiagnosticsStore.T>()).Publish
        StateChanged = None
        SessionOps = {
          CreateSession = fun _ _ _ -> System.Threading.Tasks.Task.FromResult(Ok "test-session")
          ListSessions = fun () -> System.Threading.Tasks.Task.FromResult("No sessions")
          StopSession = fun _ -> System.Threading.Tasks.Task.FromResult(Ok "stopped")
          PurgeSession = fun _ -> System.Threading.Tasks.Task.FromResult(Ok "purged")
          RestartSession = fun _ _ -> System.Threading.Tasks.Task.FromResult(Ok "restarted")
          GetProxy = fun _ -> System.Threading.Tasks.Task.FromResult(None)
          GetSessionInfo = fun id ->
            System.Threading.Tasks.Task.FromResult(
              Some { WorkerProtocol.SessionInfo.Id = id
                     Name = None
                     Projects = []; WorkingDirectory = ""; SolutionRoot = None
                     Status = WorkerProtocol.SessionLifecycleStatus.Ready { Pid = 0; Port = None }
                     Workflow = WorkflowTypes.SessionWorkflow.Interactive
                     CreatedAt = System.DateTime.UtcNow
                     LastActivity = System.DateTime.UtcNow
                     ActiveProject = None
                     ProjectRoles = []
                     App = SageFs.AppRun.AppRunState.NotRunning })
          GetAllSessions = fun () -> System.Threading.Tasks.Task.FromResult([])
          UpdateSessionStatus = fun _ _ -> System.Threading.Tasks.Task.FromResult(())
          NotifyWorkerDied = fun _ -> ()
          ClaimRun = SageFs.SessionManagementOps.stub.ClaimRun
          ClaimStop = SageFs.SessionManagementOps.stub.ClaimStop
          AdvanceRun = SageFs.SessionManagementOps.stub.AdvanceRun
          EndAppRun = SageFs.SessionManagementOps.stub.EndAppRun
          AwaitReady = fun _ _ -> System.Threading.Tasks.Task.FromResult(Result.Error (SageFs.SageFsError.HardResetFailed "Not available"))
          SwitchWorkflow = fun _ _ -> System.Threading.Tasks.Task.FromResult(Result.Error (SageFsError.HardResetFailed "Not available"))
          GetAdoptedCore = fun _ -> System.Threading.Tasks.Task.FromResult(None)
          GetWarmupProgress = fun _ -> System.Threading.Tasks.Task.FromResult(None)
        }
        SessionMap = sessionMap
        McpPort = 0
        Dispatch = Some (fun msg -> dispatched.Add(msg))
        GetElmModel = None
        GetElmRegions = None
        GetWarmupContext = None
        GetFeatureState = None; RecordEval = None
        ActivityTracker = SageFs.AgentActivityTracker.create()
        LiveSnapshotSink = None
        CohortOwner = None
        GetDaemonHealth = fun () -> None
        GetProcessTelemetry = fun () -> None } : McpContext
    ctx, dispatched

  /// Call switchSession and return result, ignoring event store errors.
  /// switchSession dispatches to Elm BEFORE appending to the event store,
  /// so dispatch tracking is valid even if the store throws.
  let switchSessionIgnoringStoreErrors ctx agent sessionId =
    task {
      try
        let! result = switchSession ctx agent sessionId
        return Ok result
      with ex ->
        return Error (ex.Message)
    }

  // Every SessionOps member here is a hand-written stub (GetProxy returns
  // None/a dummyProxy, GetSessionInfo returns a canned record) — the only
  // reason this list ever touched the shared FSI actor was to borrow its
  // DiagnosticsChanged handle, replaced above with a bare Event<_>, so
  // --integration-host no longer pays for a real actor warmup here
  // (fsi-mechanism-extraction.md R7). With no real process left, it runs in
  // the default suite.
  let tests = testSequenced <| testList "MCP session isolation" [

    testTask "switchSession updates only the given context's SessionMap for that agent" {
      let ctx1, _ = ctxWithTracking "aaaaaa01"
      let ctx2, _ = ctxWithTracking "aaaaaa01"

      let! _ = switchSessionIgnoringStoreErrors ctx1 "agent1" "bbbbbb02"

      ctx1.SessionMap.["agent1"]
      |> Expect.equal "ctx1 agent1 should switch to B" "bbbbbb02"

      ctx1.SessionMap.["test"]
      |> Expect.equal "ctx1 test agent should remain on A" "aaaaaa01"

      ctx2.SessionMap.["test"]
      |> Expect.equal "ctx2 should remain on A" "aaaaaa01"
    }

    testCaseAsync "switchSession moves the daemon-global active session via SessionSwitched" <| async {
      // The per-agent SessionMap update alone left session-less calls (e.g.
      // GET /api/live-testing/status with no ?session=) stuck on whatever
      // session was last created. switchSession now also dispatches
      // SessionSwitched so the daemon-global active session follows the switch,
      // and those no-arg calls resolve to the session the user is working in.
      let ctx, dispatched = ctxWithTracking "aaaaaa01"

      let! _ = switchSessionIgnoringStoreErrors ctx "test" "bbbbbb02" |> Async.AwaitTask

      dispatched
      |> Seq.choose (fun msg ->
        match msg with
        | SageFsMsg.Event (TuiEvent.SessionSwitched (_, toId)) -> Some toId
        | _ -> None)
      |> Seq.toList
      |> Expect.equal "switchSession dispatches exactly one SessionSwitched to the target session" [ "bbbbbb02" ]
    }

    testCaseAsync "switchSession does NOT dispatch ListSessions to Elm" <| async {
      let ctx, dispatched = ctxWithTracking "aaaaaa01"

      let! _ = switchSessionIgnoringStoreErrors ctx "test" "bbbbbb02" |> Async.AwaitTask

      dispatched
      |> Seq.filter (fun msg ->
        match msg with
        | SageFsMsg.Editor EditorAction.ListSessions -> true
        | _ -> false)
      |> Seq.length
      |> Expect.equal "switchSession should not dispatch ListSessions to Elm" 0
    }

    testTask "switchSession returns error for nonexistent session" {
      let sessionMap = ConcurrentDictionary<string, string>()
      sessionMap.["test"] <- "aaaaaa01"
      let ctx =
        { FrictionStore = None
          DiagnosticsChanged = (Event<Features.DiagnosticsStore.T>()).Publish
          StateChanged = None
          SessionOps = {
            CreateSession = fun _ _ _ -> System.Threading.Tasks.Task.FromResult(Ok "test")
            ListSessions = fun () -> System.Threading.Tasks.Task.FromResult("No sessions")
            StopSession = fun _ -> System.Threading.Tasks.Task.FromResult(Ok "stopped")
            PurgeSession = fun _ -> System.Threading.Tasks.Task.FromResult(Ok "purged")
            RestartSession = fun _ _ -> System.Threading.Tasks.Task.FromResult(Ok "restarted")
            GetProxy = fun _ -> System.Threading.Tasks.Task.FromResult(None)
            GetSessionInfo = fun _ -> System.Threading.Tasks.Task.FromResult(None)
            GetAllSessions = fun () -> System.Threading.Tasks.Task.FromResult([])
            UpdateSessionStatus = fun _ _ -> System.Threading.Tasks.Task.FromResult(())
            NotifyWorkerDied = fun _ -> ()
            ClaimRun = SageFs.SessionManagementOps.stub.ClaimRun
            ClaimStop = SageFs.SessionManagementOps.stub.ClaimStop
            AdvanceRun = SageFs.SessionManagementOps.stub.AdvanceRun
            EndAppRun = SageFs.SessionManagementOps.stub.EndAppRun
            AwaitReady = fun _ _ -> System.Threading.Tasks.Task.FromResult(Result.Error (SageFs.SageFsError.HardResetFailed "Not available"))
            SwitchWorkflow = fun _ _ -> System.Threading.Tasks.Task.FromResult(Result.Error (SageFsError.HardResetFailed "Not available"))
            GetAdoptedCore = fun _ -> System.Threading.Tasks.Task.FromResult(None)
            GetWarmupProgress = fun _ -> System.Threading.Tasks.Task.FromResult(None) }
          SessionMap = sessionMap
          McpPort = 0
          Dispatch = None
          GetElmModel = None
          GetElmRegions = None
          GetWarmupContext = None
          GetFeatureState = None; RecordEval = None
          ActivityTracker = SageFs.AgentActivityTracker.create()
          LiveSnapshotSink = None
          CohortOwner = None
          GetDaemonHealth = fun () -> None
          GetProcessTelemetry = fun () -> None } : McpContext

      let! result = switchSession ctx "test" "ffff0001"

      result
      |> Expect.stringContains "should contain error message" "not found"
    }

  ]

module SessionResolutionByWorkingDir =

  let mkInfo id workDir : WorkerProtocol.SessionInfo =
    { Id = id; Name = None; Projects = []
      WorkingDirectory = workDir; SolutionRoot = None
      Status = WorkerProtocol.SessionLifecycleStatus.Ready { Pid = 0; Port = None }
      Workflow = WorkflowTypes.SessionWorkflow.Interactive
      CreatedAt = System.DateTime.UtcNow
      LastActivity = System.DateTime.UtcNow
      ActiveProject = None
      ProjectRoles = []
      App = SageFs.AppRun.AppRunState.NotRunning }

  let tests = testList "resolveSessionByWorkingDir" [
    test "returns None for empty session list" {
      resolveSessionByWorkingDir [] @"C:\Code\Repos\SageFs"
      |> Expect.isNone "empty list yields None"
    }

    test "returns None when no session matches" {
      let sessions = [ mkInfo (testSessionId "aa000001") @"C:\Code\Repos\Other" ]
      resolveSessionByWorkingDir sessions @"C:\Code\Repos\SageFs"
      |> Expect.isNone "no match yields None"
    }

    test "finds exact match" {
      let sessions = [
        mkInfo (testSessionId "aa000001") @"C:\Code\Repos\Other"
        mkInfo (testSessionId "bb000002") @"C:\Code\Repos\SageFs"
      ]
      let result = resolveSessionByWorkingDir sessions @"C:\Code\Repos\SageFs"
      result |> Expect.isSome "should find matching session"
      result.Value.Id
      |> Expect.equal "should return s2" (testSessionId "bb000002")
    }

    test "matches with trailing separator on query" {
      let sessions = [ mkInfo (testSessionId "aa000001") @"C:\Code\Repos\SageFs" ]
      let result = resolveSessionByWorkingDir sessions @"C:\Code\Repos\SageFs\"
      result |> Expect.isSome "trailing sep should match"
      result.Value.Id
      |> Expect.equal "should return s1" (testSessionId "aa000001")
    }

    test "matches case-insensitively on Windows" {
      if Environment.OSVersion.Platform <> PlatformID.Win32NT then
        ()
      else
        let sessions = [ mkInfo (testSessionId "aa000001") @"C:\Code\Repos\SageFs" ]
        let result = resolveSessionByWorkingDir sessions @"c:\code\repos\sagefs"
        result |> Expect.isSome "case-insensitive match"
        result.Value.Id
        |> Expect.equal "should return s1" (testSessionId "aa000001")
    }

    test "returns first match when multiple sessions share dir" {
      let sessions = [
        mkInfo (testSessionId "aa000001") @"C:\Code\Repos\SageFs"
        mkInfo (testSessionId "bb000002") @"C:\Code\Repos\SageFs"
      ]
      let result = resolveSessionByWorkingDir sessions @"C:\Code\Repos\SageFs"
      result |> Expect.isSome "should find a match"
      result.Value.Id
      |> Expect.equal "should return first" (testSessionId "aa000001")
    }

    test "session trailing separator matches clean input" {
      let sessions = [ mkInfo (testSessionId "aa000001") @"C:\Code\Repos\SageFs\" ]
      let result = resolveSessionByWorkingDir sessions @"C:\Code\Repos\SageFs"
      result |> Expect.isSome "session trailing sep should match"
      result.Value.Id
      |> Expect.equal "should return s1" (testSessionId "aa000001")
    }
  ]

module WorkingDirDeepMatching =
  /// WHY — friction report 2026-08: an agent working in a subdirectory of a
  /// registered session got "No sessions match" from get_fsi_status while
  /// list_sessions showed the session. Deep matching routes subdirectory
  /// requests to the owning session instead of reporting it missing.

  open SageFs.McpTools
  open SageFs.McpSessionRouting

  let mkInfo id workDir : WorkerProtocol.SessionInfo =
    { Id = id; Name = None; Projects = []
      WorkingDirectory = workDir; SolutionRoot = None
      Status = WorkerProtocol.SessionLifecycleStatus.Ready { Pid = 0; Port = None }
      Workflow = WorkflowTypes.SessionWorkflow.Interactive
      CreatedAt = System.DateTime.UtcNow
      LastActivity = System.DateTime.UtcNow
      ActiveProject = None
      ProjectRoles = []
      App = SageFs.AppRun.AppRunState.NotRunning }

  let tests = testList "sessionsMatchingWorkingDirDeep" [

    testCase "WHY — subdirectory routing — an agent calling from repo\\tests reaches the session rooted at repo because exact-only matching reported existing sessions as missing"
    <| fun _ ->
      let sessions = [ mkInfo (testSessionId "aa000001") @"C:\Code\Repos\SageFs" ]
      sessionsMatchingWorkingDirDeep sessions @"C:\Code\Repos\SageFs\tests"
      |> List.map (fun s -> s.Id)
      |> Expect.equal "subdirectory of session root should match the session" [ testSessionId "aa000001" ]

    testCase "WHY — path-boundary safety — 'SageFsExtra' is NOT inside 'SageFs' so prefix matching must not cross directory-name boundaries"
    <| fun _ ->
      let sessions = [ mkInfo (testSessionId "aa000001") @"C:\Code\Repos\SageFs" ]
      sessionsMatchingWorkingDirDeep sessions @"C:\Code\Repos\SageFsExtra"
      |> Expect.isEmpty "sibling with shared name prefix must not match"

    testCase "WHY — sibling isolation — a different repo's agent must not silently route into another repo's session"
    <| fun _ ->
      let sessions = [ mkInfo (testSessionId "aa000001") @"C:\Code\Repos\SageFs" ]
      sessionsMatchingWorkingDirDeep sessions @"C:\Code\Repos\SageTech"
      |> Expect.isEmpty "unrelated sibling directory must not match"

    testCase "exact match still wins and is not duplicated by ancestor fallback"
    <| fun _ ->
      let sessions = [
        mkInfo (testSessionId "aa000001") @"C:\Code\Repos\SageFs"
        mkInfo (testSessionId "bb000002") @"C:\Code\Repos"
      ]
      sessionsMatchingWorkingDirDeep sessions @"C:\Code\Repos\SageFs"
      |> List.map (fun s -> s.Id)
      |> Expect.equal "exact match should be the only result" [ testSessionId "aa000001" ]

    testCase "sessions with empty WorkingDirectory never match by ancestor"
    <| fun _ ->
      let sessions = [ mkInfo (testSessionId "aa000001") "" ]
      sessionsMatchingWorkingDirDeep sessions @"C:\Code\Repos\SageFs\tests"
      |> Expect.isEmpty "empty base dir must not act as root-of-everything"

    // ── Checkout-boundary RED tests (sagefs-multiagent-vision.md §3.2,
    // Phase 0 item 3): a nested git worktree is a ROUTING BOUNDARY —
    // ancestor matching must stop at it, not silently route a worktree
    // agent's request into the main checkout's session.

    testCase "WHY — a request from a git worktree nested under a session's root does NOT match that session"
    <| fun _ ->
      let sessions = [ mkInfo (testSessionId "aa000001") @"C:\Code\Repos\SageFs" ]
      let hasMarker (dir: string) =
        dir = @"C:\Code\Repos\SageFs\.claude\worktrees\agent-x"
      sessionsMatchingWorkingDirDeepWith hasMarker sessions @"C:\Code\Repos\SageFs\.claude\worktrees\agent-x\SageFs"
      |> Expect.isEmpty "a nested worktree must not resolve into the main checkout's session"

    testCase "WHY — a plain subdirectory with no checkout marker between root and target still matches"
    <| fun _ ->
      let sessions = [ mkInfo (testSessionId "aa000001") @"C:\Code\Repos\SageFs" ]
      sessionsMatchingWorkingDirDeepWith (fun _ -> false) sessions @"C:\Code\Repos\SageFs\SageFs.Tests"
      |> List.map (fun s -> s.Id)
      |> Expect.equal "no marker anywhere means the ancestor match still applies" [ testSessionId "aa000001" ]

    testCase "WHY — the session's OWN root marker does not count as a boundary"
    <| fun _ ->
      let sessions = [ mkInfo (testSessionId "aa000001") @"C:\Code\Repos\SageFs" ]
      let hasMarker (dir: string) = dir = @"C:\Code\Repos\SageFs"
      sessionsMatchingWorkingDirDeepWith hasMarker sessions @"C:\Code\Repos\SageFs\SageFs.Tests"
      |> List.map (fun s -> s.Id)
      |> Expect.equal "a session's repository root is where its files live" [ testSessionId "aa000001" ]

    testCase "WHY — findGitRoot from inside a REAL git worktree returns the worktree root, not the main checkout"
    <| fun _ ->
      // End-to-end proof against the real Checkout.hasCheckoutMarker (real
      // disk), mirroring this repo's own on-disk worktree layout: a plain
      // ".git" DIRECTORY at the main root, a ".git" FILE inside the worktree.
      let tmp = System.IO.Path.Combine(System.IO.Path.GetTempPath(), System.IO.Path.GetRandomFileName())
      let mainGit = System.IO.Path.Combine(tmp, ".git")
      let worktreeRoot = System.IO.Path.Combine(tmp, "wt")
      let worktreeSrc = System.IO.Path.Combine(worktreeRoot, "src")
      System.IO.Directory.CreateDirectory(mainGit) |> ignore
      System.IO.Directory.CreateDirectory(worktreeSrc) |> ignore
      System.IO.File.WriteAllText(System.IO.Path.Combine(worktreeRoot, ".git"), sprintf "gitdir: %s" (System.IO.Path.Combine(mainGit, "worktrees", "wt")))
      try
        WorkerProtocol.SessionInfo.findGitRoot worktreeSrc
        |> Expect.equal "must return the WORKTREE root, not the main checkout further up" (Some worktreeRoot)
      finally
        System.IO.Directory.Delete(tmp, true)

    testCase "WHY — sessionsMatchingWorkingDirDeep, against the real filesystem, excludes a worktree nested in a REAL session root"
    <| fun _ ->
      let tmp = System.IO.Path.Combine(System.IO.Path.GetTempPath(), System.IO.Path.GetRandomFileName())
      let repoRoot = System.IO.Path.Combine(tmp, "repo")
      let worktreeRoot = System.IO.Path.Combine(repoRoot, ".claude", "worktrees", "agent-x")
      let worktreeSrc = System.IO.Path.Combine(worktreeRoot, "SageFs")
      System.IO.Directory.CreateDirectory(System.IO.Path.Combine(repoRoot, ".git")) |> ignore
      System.IO.Directory.CreateDirectory(worktreeSrc) |> ignore
      System.IO.File.WriteAllText(System.IO.Path.Combine(worktreeRoot, ".git"), "gitdir: /elsewhere/.git/worktrees/agent-x")
      try
        let sessions = [ mkInfo (testSessionId "aa000001") repoRoot ]
        sessionsMatchingWorkingDirDeep sessions worktreeSrc
        |> Expect.isEmpty "a request from inside the real nested worktree must not resolve to the main checkout's session"
      finally
        System.IO.Directory.Delete(tmp, true)
  ]

module WorkingDirRoutingPriority =

  open System.Threading.Tasks

  let mkInfo id workDir : WorkerProtocol.SessionInfo =
    { Id = id; Name = Some (WorkerProtocol.SessionId.value id); Projects = []
      WorkingDirectory = workDir; SolutionRoot = None
      Status = WorkerProtocol.SessionLifecycleStatus.Ready { Pid = 1234; Port = None }
      Workflow = WorkflowTypes.SessionWorkflow.Interactive
      CreatedAt = System.DateTime.UtcNow
      LastActivity = System.DateTime.UtcNow
      ActiveProject = None
      ProjectRoles = []
      App = SageFs.AppRun.AppRunState.NotRunning }

  let dummyProxy : WorkerProtocol.SessionProxy =
    fun _msg -> async { return WorkerProtocol.WorkerResponse.WorkerReady }

  let mkCtx (sessions: WorkerProtocol.SessionInfo list) (proxies: Map<string, WorkerProtocol.SessionProxy>) : McpContext =
    let sessionMap = ConcurrentDictionary<string, string>()
    { FrictionStore = None; DiagnosticsChanged = Unchecked.defaultof<_>
      StateChanged = None
      SessionOps =
        { CreateSession = fun _ _ _ -> Task.FromResult(Error(SageFsError.SessionCreationFailed "n/a"))
          ListSessions = fun () -> Task.FromResult("")
          StopSession = fun _ -> Task.FromResult(Error(SageFsError.SessionNotFound "n/a"))
          PurgeSession = fun _ -> Task.FromResult(Error(SageFsError.SessionNotFound "n/a"))
          RestartSession = fun _ _ -> Task.FromResult(Error(SageFsError.SessionNotFound "n/a"))
          GetProxy = fun sid -> Task.FromResult(Map.tryFind (WorkerProtocol.SessionId.value sid) proxies)
          GetSessionInfo = fun sid -> Task.FromResult(sessions |> List.tryFind (fun s -> s.Id = sid))
          GetAllSessions = fun () -> Task.FromResult(sessions)
          UpdateSessionStatus = fun _ _ -> Task.FromResult(())
          NotifyWorkerDied = fun _ -> ()
          ClaimRun = SageFs.SessionManagementOps.stub.ClaimRun
          ClaimStop = SageFs.SessionManagementOps.stub.ClaimStop
          AdvanceRun = SageFs.SessionManagementOps.stub.AdvanceRun
          EndAppRun = SageFs.SessionManagementOps.stub.EndAppRun
          AwaitReady = fun _ _ -> Task.FromResult(Result.Error (SageFs.SageFsError.HardResetFailed "Not available"))
          SwitchWorkflow = fun _ _ -> Task.FromResult(Error(SageFsError.HardResetFailed "Not available"))
          GetAdoptedCore = fun _ -> Task.FromResult(None)
          GetWarmupProgress = fun _ -> Task.FromResult(None) }
      SessionMap = sessionMap; McpPort = 0; Dispatch = None
      GetElmModel = None; GetElmRegions = None; GetWarmupContext = None
      GetFeatureState = None; RecordEval = None; ActivityTracker = SageFs.AgentActivityTracker.create()
      LiveSnapshotSink = None; CohortOwner = None
      GetDaemonHealth = fun () -> None
      GetProcessTelemetry = fun () -> None }

  /// Same shape as mkCtx, but sessions/proxies are read through thunks each
  /// call instead of frozen at construction — lets a test simulate a
  /// session's warmup completing (status flips, proxy comes online) between
  /// two resolveSessionId calls on the SAME context/SessionMap.
  let mkCtxDynamic (sessions: unit -> WorkerProtocol.SessionInfo list) (proxies: unit -> Map<string, WorkerProtocol.SessionProxy>) : McpContext =
    let sessionMap = ConcurrentDictionary<string, string>()
    { FrictionStore = None; DiagnosticsChanged = Unchecked.defaultof<_>
      StateChanged = None
      SessionOps =
        { CreateSession = fun _ _ _ -> Task.FromResult(Error(SageFsError.SessionCreationFailed "n/a"))
          ListSessions = fun () -> Task.FromResult("")
          StopSession = fun _ -> Task.FromResult(Error(SageFsError.SessionNotFound "n/a"))
          PurgeSession = fun _ -> Task.FromResult(Error(SageFsError.SessionNotFound "n/a"))
          RestartSession = fun _ _ -> Task.FromResult(Error(SageFsError.SessionNotFound "n/a"))
          GetProxy = fun sid -> Task.FromResult(Map.tryFind (WorkerProtocol.SessionId.value sid) (proxies ()))
          GetSessionInfo = fun sid -> Task.FromResult(sessions () |> List.tryFind (fun s -> s.Id = sid))
          GetAllSessions = fun () -> Task.FromResult(sessions ())
          UpdateSessionStatus = fun _ _ -> Task.FromResult(())
          NotifyWorkerDied = fun _ -> ()
          ClaimRun = SageFs.SessionManagementOps.stub.ClaimRun
          ClaimStop = SageFs.SessionManagementOps.stub.ClaimStop
          AdvanceRun = SageFs.SessionManagementOps.stub.AdvanceRun
          EndAppRun = SageFs.SessionManagementOps.stub.EndAppRun
          AwaitReady = fun _ _ -> Task.FromResult(Result.Error (SageFs.SageFsError.HardResetFailed "Not available"))
          SwitchWorkflow = fun _ _ -> Task.FromResult(Error(SageFsError.HardResetFailed "Not available"))
          GetAdoptedCore = fun _ -> Task.FromResult(None)
          GetWarmupProgress = fun _ -> Task.FromResult(None) }
      SessionMap = sessionMap; McpPort = 0; Dispatch = None
      GetElmModel = None; GetElmRegions = None; GetWarmupContext = None
      GetFeatureState = None; RecordEval = None; ActivityTracker = SageFs.AgentActivityTracker.create()
      LiveSnapshotSink = None; CohortOwner = None
      GetDaemonHealth = fun () -> None
      GetProcessTelemetry = fun () -> None }

  let tests = testSequenced <| testList "workingDirectory routing priority" [
    testTask "workingDirectory should override cached session" {
      let s1 = mkInfo (testSessionId "5a6e0001") @"C:\Code\Repos\SageFs"
      let s2 = mkInfo (testSessionId "4a120002") @"C:\Code\Repos\Harmony"
      let ctx = mkCtx [s1;s2] (Map.ofList ["5a6e0001",dummyProxy;"4a120002",dummyProxy])
      setActiveSessionId ctx "mcp" "5a6e0001"
      let! resolved = resolveSessionId ctx "mcp" None (Some @"C:\Code\Repos\Harmony")
      resolved |> Expect.equal "should route to Harmony based on workingDirectory" (Routable "4a120002")
    }
    testTask "workingDirectory routes correctly when no cached session" {
      let s1 = mkInfo (testSessionId "5a6e0001") @"C:\Code\Repos\SageFs"
      let s2 = mkInfo (testSessionId "4a120002") @"C:\Code\Repos\Harmony"
      let ctx = mkCtx [s1;s2] (Map.ofList ["5a6e0001",dummyProxy;"4a120002",dummyProxy])
      let! resolved = resolveSessionId ctx "mcp" None (Some @"C:\Code\Repos\Harmony")
      resolved |> Expect.equal "should route to Harmony via workingDirectory" (Routable "4a120002")
    }
    testTask "workingDirectory returns an ambiguity error when multiple sessions share the same directory" {
      let s1 = mkInfo (testSessionId "5a6e0001") @"C:\Code\Repos\SageFs"
      let s2 = mkInfo (testSessionId "4a120002") @"C:\Code\Repos\SageFs"
      let ctx = mkCtx [s1; s2] (Map.ofList ["5a6e0001",dummyProxy; "4a120002",dummyProxy])
      setActiveSessionId ctx "mcp" "5a6e0001"
      let! resolved = resolveSessionId ctx "mcp" None (Some @"C:\Code\Repos\SageFs")
      match resolved with
      | Routable sid ->
        failtestf "expected workingDirectory ambiguity error but resolved '%s'" sid
      | Gone msg ->
        msg |> Expect.stringContains "should describe the ambiguity" "Multiple sessions match workingDirectory"
        msg |> Expect.stringContains "should list the first matching session" "5a6e0001"
        msg |> Expect.stringContains "should list the second matching session" "4a120002"
        activeSessionId ctx "mcp"
        |> Expect.equal "cached session should remain unchanged after ambiguity" "5a6e0001"
      | other ->
        failtestf "expected Gone ambiguity error but got %A" other
    }
    testTask "explicit sessionId always wins over workingDirectory" {
      let s1 = mkInfo (testSessionId "5a6e0001") @"C:\Code\Repos\SageFs"
      let s2 = mkInfo (testSessionId "4a120002") @"C:\Code\Repos\Harmony"
      let ctx = mkCtx [s1;s2] (Map.ofList ["5a6e0001",dummyProxy;"4a120002",dummyProxy])
      let! resolved = resolveSessionId ctx "mcp" (Some "5a6e0001") (Some @"C:\Code\Repos\Harmony")
      resolved |> Expect.equal "explicit sessionId takes priority" (Routable "5a6e0001")
    }
    testTask "workingDirectory updates the cached session" {
      let s1 = mkInfo (testSessionId "5a6e0001") @"C:\Code\Repos\SageFs"
      let s2 = mkInfo (testSessionId "4a120002") @"C:\Code\Repos\Harmony"
      let ctx = mkCtx [s1;s2] (Map.ofList ["5a6e0001",dummyProxy;"4a120002",dummyProxy])
      setActiveSessionId ctx "mcp" "5a6e0001"
      let! _ = resolveSessionId ctx "mcp" None (Some @"C:\Code\Repos\Harmony")
      activeSessionId ctx "mcp" |> Expect.equal "cached session should update" "4a120002"
    }
    testTask "falls back to cached session when workingDirectory is None" {
      let s1 = mkInfo (testSessionId "5a6e0001") @"C:\Code\Repos\SageFs"
      let ctx = mkCtx [s1] (Map.ofList ["5a6e0001",dummyProxy])
      setActiveSessionId ctx "mcp" "5a6e0001"
      let! resolved = resolveSessionId ctx "mcp" None None
      resolved |> Expect.equal "should fall back to cached when no workingDirectory" (Routable "5a6e0001")
    }
    testTask "falls back to the only session when no active session is cached" {
      let s1 = mkInfo (testSessionId "5a6e0001") @"C:\Code\Repos\SageFs"
      let ctx = mkCtx [s1] (Map.ofList ["5a6e0001",dummyProxy])
      let! resolved = resolveSessionId ctx "mcp" None None
      resolved |> Expect.equal "single session should be used automatically" (Routable "5a6e0001")
      activeSessionId ctx "mcp" |> Expect.equal "single session should become cached" "5a6e0001"
    }
    testTask "returns an explicit error when workingDirectory does not match any session" {
      let s1 = mkInfo (testSessionId "5a6e0001") @"C:\Code\Repos\SageFs"
      let ctx = mkCtx [s1] (Map.ofList ["5a6e0001",dummyProxy])
      let! resolved = resolveSessionId ctx "mcp" None (Some @"C:\Code\Repos\Other")
      match resolved with
      | Routable sid -> failtestf "expected workingDirectory mismatch error, got session '%s'" sid
      | Gone msg ->
        msg |> Expect.stringContains "should explain mismatch" "No sessions match workingDirectory"
        activeSessionId ctx "mcp" |> Expect.equal "cached session should remain unchanged" ""
      | other ->
        failtestf "expected Gone mismatch error but got %A" other
    }
    testTask "falls back to the session matching the daemon current directory" {
      let originalDir = Environment.CurrentDirectory
      let root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))
      let currentDir = Path.Combine(root, "current")
      let otherDir = Path.Combine(root, "other")
      Directory.CreateDirectory(currentDir) |> ignore
      Directory.CreateDirectory(otherDir) |> ignore
      Environment.CurrentDirectory <- currentDir
      try
        let currentSession = mkInfo (testSessionId "5a6e0001") currentDir
        let otherSession = mkInfo (testSessionId "4a120002") otherDir
        let ctx =
          mkCtx [currentSession; otherSession] (Map.ofList ["5a6e0001",dummyProxy; "4a120002",dummyProxy])
        let! resolved = resolveSessionId ctx "mcp" None None
        resolved |> Expect.equal "current directory session should be selected" (Routable "5a6e0001")
        activeSessionId ctx "mcp" |> Expect.equal "current directory session should become cached" "5a6e0001"
      finally
        Environment.CurrentDirectory <- originalDir
        try Directory.Delete(root, true) with _ -> ()
    }
    testTask "current directory fallback returns an ambiguity error when multiple sessions share the daemon directory" {
      let originalDir = Environment.CurrentDirectory
      let root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))
      let currentDir = Path.Combine(root, "current")
      let otherDir = Path.Combine(root, "other")
      Directory.CreateDirectory(currentDir) |> ignore
      Directory.CreateDirectory(otherDir) |> ignore
      Environment.CurrentDirectory <- currentDir
      try
        let currentA = mkInfo (testSessionId "5a6e0001") currentDir
        let currentB = mkInfo (testSessionId "4a120002") currentDir
        let otherSession = mkInfo (testSessionId "8c340003") otherDir
        let ctx =
          mkCtx [currentA; currentB; otherSession] (Map.ofList ["5a6e0001",dummyProxy; "4a120002",dummyProxy; "8c340003",dummyProxy])
        let! resolved = resolveSessionId ctx "mcp" None None
        match resolved with
        | Routable sid ->
          failtestf "expected current directory ambiguity error but resolved '%s'" sid
        | Gone msg ->
          msg |> Expect.stringContains "should describe the ambiguity" "Multiple sessions match the current working directory"
          msg |> Expect.stringContains "should list the first matching session" "5a6e0001"
          msg |> Expect.stringContains "should list the second matching session" "4a120002"
          activeSessionId ctx "mcp"
          |> Expect.equal "cache should remain empty after ambiguity" ""
        | other ->
          failtestf "expected Gone ambiguity error but got %A" other
      finally
        Environment.CurrentDirectory <- originalDir
        try Directory.Delete(root, true) with _ -> ()
    }
    testTask "WHY — resolveSessionId — an unrouted status read during warmup must not undo a deliberate switch_session (issue #140)" {
      // Repro shape from #140: two sessions share a working directory.
      // switch_session targets the one still warming up. A later, unrouted
      // get_fsi_status (no session_id, no workingDirectory) resolves through
      // the SAME "candidate <> ''" branch resolveSessionId used to clear the
      // cached mapping in — that side effect is the bug: it silently undid
      // the switch, so once the target became Ready, the next unrouted call
      // fell back to workingDirectory matching and hit "Multiple sessions
      // match" instead of routing to the session the agent switched to.
      let wd = @"C:\Code\Repos\Proj"
      let ready = mkInfo (testSessionId "aaaaaaa1") wd
      let mutable warmingStatus = WorkerProtocol.SessionLifecycleStatus.Starting { Pid = 999; Port = None }
      let mutable warmingProxy : Map<string, WorkerProtocol.SessionProxy> = Map.ofList [ "aaaaaaa1", dummyProxy ]
      let warming () = { mkInfo (testSessionId "bbbbbbb2") wd with Status = warmingStatus }
      let ctx = mkCtxDynamic (fun () -> [ ready; warming () ]) (fun () -> warmingProxy)

      // The agent deliberately switches to the still-warming session.
      setActiveSessionId ctx "mcp" "bbbbbbb2"

      // An unrelated, unrouted status read while it is still warming.
      let! duringWarmup = resolveSessionId ctx "mcp" None None
      match duringWarmup with
      | WarmingUp ("bbbbbbb2", _) -> ()
      | other -> failtestf "expected WarmingUp('bbbbbbb2', _), got %A" other

      activeSessionId ctx "mcp"
      |> Expect.equal "the switch must survive a status read taken mid-warmup" "bbbbbbb2"

      // Warmup completes: status flips to Ready and the worker proxy comes online.
      warmingStatus <- WorkerProtocol.SessionLifecycleStatus.Ready { Pid = 999; Port = Some 6000 }
      warmingProxy <- Map.add "bbbbbbb2" dummyProxy warmingProxy

      let! afterReady = resolveSessionId ctx "mcp" None None
      afterReady
      |> Expect.equal "once Ready, the unrouted call must route to the session switched to, not fall back to an ambiguous workingDirectory match" (Routable "bbbbbbb2")
    }
  ]

module ResetIsolation =

  /// Create a context with two agents on different sessions, plus tracking stubs.
  let mkTrackingCtx () =
    let sessionMap = ConcurrentDictionary<string, string>()
    sessionMap.["agent1"] <- "aaa00001"
    sessionMap.["agent2"] <- "bbb00002"
    let restartLog = System.Collections.Generic.List<string * bool>()
    let routedSessions = System.Collections.Generic.List<string>()
    let ops : SessionManagementOps = {
      CreateSession = fun _ _ _ -> System.Threading.Tasks.Task.FromResult(Ok "new-session")
      ListSessions = fun () -> System.Threading.Tasks.Task.FromResult("No sessions")
      StopSession = fun _ -> System.Threading.Tasks.Task.FromResult(Ok "stopped")
      PurgeSession = fun _ -> System.Threading.Tasks.Task.FromResult(Ok "purged")
      RestartSession = fun sid rebuild ->
        restartLog.Add((WorkerProtocol.SessionId.value sid, rebuild))
        System.Threading.Tasks.Task.FromResult(Ok "restarted")
      GetProxy = fun sid ->
        routedSessions.Add(WorkerProtocol.SessionId.value sid)
        System.Threading.Tasks.Task.FromResult(None)
      GetSessionInfo = fun id ->
        System.Threading.Tasks.Task.FromResult(
          Some { WorkerProtocol.SessionInfo.Id = id
                 Name = None; Projects = []; WorkingDirectory = ""; SolutionRoot = None
                 Status = WorkerProtocol.SessionLifecycleStatus.Ready { Pid = 0; Port = None }
                 Workflow = WorkflowTypes.SessionWorkflow.Interactive
                 CreatedAt = System.DateTime.UtcNow; LastActivity = System.DateTime.UtcNow
                 ActiveProject = None; ProjectRoles = []; App = SageFs.AppRun.AppRunState.NotRunning })
      GetAllSessions = fun () -> System.Threading.Tasks.Task.FromResult([])
      UpdateSessionStatus = fun _ _ -> System.Threading.Tasks.Task.FromResult(())
      NotifyWorkerDied = fun _ -> ()
      ClaimRun = SageFs.SessionManagementOps.stub.ClaimRun
      ClaimStop = SageFs.SessionManagementOps.stub.ClaimStop
      AdvanceRun = SageFs.SessionManagementOps.stub.AdvanceRun
      EndAppRun = SageFs.SessionManagementOps.stub.EndAppRun
      AwaitReady = fun _ _ -> System.Threading.Tasks.Task.FromResult(Result.Error (SageFs.SageFsError.HardResetFailed "Not available"))
      SwitchWorkflow = fun _ _ -> System.Threading.Tasks.Task.FromResult(Result.Error (SageFsError.HardResetFailed "Not available"))
      GetAdoptedCore = fun _ -> System.Threading.Tasks.Task.FromResult(None)
      GetWarmupProgress = fun _ -> System.Threading.Tasks.Task.FromResult(None)
    }
    let ctx =
      { FrictionStore = None
        DiagnosticsChanged = (Event<Features.DiagnosticsStore.T>()).Publish
        StateChanged = None
        SessionOps = ops
        SessionMap = sessionMap
        McpPort = 0
        Dispatch = None
        GetElmModel = None
        GetElmRegions = None
        GetWarmupContext = None
        GetFeatureState = None; RecordEval = None
        ActivityTracker = SageFs.AgentActivityTracker.create()
        LiveSnapshotSink = None
        CohortOwner = None
        GetDaemonHealth = fun () -> None
        GetProcessTelemetry = fun () -> None } : McpContext
    ctx, restartLog, routedSessions

  let mkStatusSyncCtx () =
    let sid = testSessionId "aaa00001"
    let sidStr = WorkerProtocol.SessionId.value sid
    let sessionMap = ConcurrentDictionary<string, string>()
    sessionMap.["agent1"] <- sidStr
    let registryStatus = ref (WorkerProtocol.SessionLifecycleStatus.Ready { Pid = 1; Port = None })
    let resetStarted = System.Threading.Tasks.TaskCompletionSource<unit>()
    let allowResetFinish = System.Threading.Tasks.TaskCompletionSource<unit>()

    let sessionInfo () : WorkerProtocol.SessionInfo =
      { Id = sid
        Name = None
        Projects = []
        WorkingDirectory = @"C:\Code\Repos\SageFs"
        SolutionRoot = None
        Status = !registryStatus
        Workflow = WorkflowTypes.SessionWorkflow.Interactive
        CreatedAt = DateTime.UtcNow
        LastActivity = DateTime.UtcNow
        ActiveProject = None
        ProjectRoles = []
        App = SageFs.AppRun.AppRunState.NotRunning }

    let statusSnapshot () : WorkerProtocol.WorkerStatusSnapshot =
      let status =
        match resetStarted.Task.IsCompleted, allowResetFinish.Task.IsCompleted with
        | true, false -> WorkerProtocol.SessionStatus.Starting
        | _ -> WorkerProtocol.SessionStatus.Ready

      { Status = status
        StatusMessage = None
        EvalCount = 0
        AvgDurationMs = 0L
        MinDurationMs = 0L
        MaxDurationMs = 0L; Projects = []; CoreVersion = "0.0.0-test" }

    let proxy : WorkerProtocol.SessionProxy =
      fun msg ->
        async {
          match msg with
          | WorkerProtocol.WorkerMessage.ResetSession replyId ->
            resetStarted.TrySetResult(()) |> ignore
            do! allowResetFinish.Task |> Async.AwaitTask
            return WorkerProtocol.WorkerResponse.ResetResult(replyId, Ok ())
          | WorkerProtocol.WorkerMessage.GetStatus replyId ->
            return WorkerProtocol.WorkerResponse.StatusResult(replyId, statusSnapshot ())
          | other ->
            return failwithf "unexpected worker message in reset status sync test: %A" other
        }

    let ops : SessionManagementOps = {
      CreateSession = fun _ _ _ -> Task.FromResult(Ok "new-session")
      ListSessions = fun () -> Task.FromResult("No sessions")
      StopSession = fun _ -> Task.FromResult(Ok "stopped")
      PurgeSession = fun _ -> Task.FromResult(Ok "purged")
      RestartSession = fun _ _ -> Task.FromResult(Ok "restarted")
      GetProxy = fun sessionId ->
        match sessionId = sid with
        | true -> Task.FromResult(Some proxy)
        | false -> Task.FromResult(None)
      GetSessionInfo = fun sessionId ->
        match sessionId = sid with
        | true -> Task.FromResult(Some (sessionInfo ()))
        | false -> Task.FromResult(None)
      GetAllSessions = fun () -> Task.FromResult([ sessionInfo () ])
      UpdateSessionStatus = fun _ status ->
        registryStatus := status
        Task.FromResult(())
      NotifyWorkerDied = fun _ -> ()
      ClaimRun = SageFs.SessionManagementOps.stub.ClaimRun
      ClaimStop = SageFs.SessionManagementOps.stub.ClaimStop
      AdvanceRun = SageFs.SessionManagementOps.stub.AdvanceRun
      EndAppRun = SageFs.SessionManagementOps.stub.EndAppRun
      AwaitReady = fun _ _ -> Task.FromResult(Result.Error (SageFs.SageFsError.HardResetFailed "Not available"))
      SwitchWorkflow = fun _ _ -> Task.FromResult(Result.Error (SageFsError.HardResetFailed "Not available"))
      GetAdoptedCore = fun _ -> Task.FromResult(None)
      GetWarmupProgress = fun _ -> Task.FromResult(None) }

    let ctx =
      { FrictionStore = None
        DiagnosticsChanged = (Event<Features.DiagnosticsStore.T>()).Publish
        StateChanged = None
        SessionOps = ops
        SessionMap = sessionMap
        McpPort = 0
        Dispatch = None
        GetElmModel = None
        GetElmRegions = None
        GetWarmupContext = None
        GetFeatureState = None; RecordEval = None
        ActivityTracker = SageFs.AgentActivityTracker.create()
        LiveSnapshotSink = None
        CohortOwner = None
        GetDaemonHealth = fun () -> None
        GetProcessTelemetry = fun () -> None } : McpContext
    ctx, sidStr, resetStarted, allowResetFinish

  // Every ops record here is hand-written (SessionManagementOps.stub or a
  // fully custom record); GetProxy never reaches the real actor's proxy
  // (fixed responses or None). globalActorResult was forced only to borrow
  // its DiagnosticsChanged handle, replaced above/below with bare Event<_>
  // values, so --integration-host no longer pays for a real actor warmup
  // here (fsi-mechanism-extraction.md R7). With no real process left, it
  // runs in the default suite.
  let tests = testList "Reset isolation" [
    testTask "hardResetSession with rebuild only restarts the targeted session" {
      let ctx, restartLog, _ = mkTrackingCtx ()

      let! _ = hardResetSession ctx "agent1" true (Some "aaa00001") None

      restartLog |> Seq.toList
      |> Expect.equal "only session-AAA restarted" [("aaa00001", true)]

      ctx.SessionMap.["agent2"]
      |> Expect.equal "agent2 session untouched" "bbb00002"
    }

    testTask "hardResetSession with rebuild returns before background restart completes" {
      let sessionMap = ConcurrentDictionary<string, string>()
      sessionMap.["agent1"] <- "aaa00001"
      let restartStarted = TaskCompletionSource<unit>()
      let allowRestartFinish = TaskCompletionSource<unit>()
      let statuses = ResizeArray<WorkerProtocol.SessionLifecycleStatus>()

      let ops : SessionManagementOps = {
        CreateSession = fun _ _ _ -> Task.FromResult(Ok "test-session")
        ListSessions = fun () -> Task.FromResult("No sessions")
        StopSession = fun _ -> Task.FromResult(Ok "stopped")
        PurgeSession = fun _ -> Task.FromResult(Ok "purged")
        RestartSession = fun _ _ ->
          task {
            restartStarted.TrySetResult(()) |> ignore
            do! allowRestartFinish.Task
            return Ok "restarted"
          }
        GetProxy = fun _ -> Task.FromResult(Some (fun _ -> async { return WorkerProtocol.WorkerResponse.WorkerReady }))
        GetSessionInfo = fun id ->
          Task.FromResult(
            Some { WorkerProtocol.SessionInfo.Id = id
                   Name = None
                   Projects = []
                   WorkingDirectory = ""
                   SolutionRoot = None
                   Status = WorkerProtocol.SessionLifecycleStatus.Ready { Pid = 0; Port = None }
                   Workflow = WorkflowTypes.SessionWorkflow.Interactive
                   CreatedAt = DateTime.UtcNow
                   LastActivity = DateTime.UtcNow
                   ActiveProject = None
                   ProjectRoles = []
                   App = SageFs.AppRun.AppRunState.NotRunning })
        GetAllSessions = fun () -> Task.FromResult([])
        UpdateSessionStatus = fun _ status ->
          statuses.Add(status)
          Task.FromResult(())
        NotifyWorkerDied = fun _ -> ()
        ClaimRun = SageFs.SessionManagementOps.stub.ClaimRun
        ClaimStop = SageFs.SessionManagementOps.stub.ClaimStop
        AdvanceRun = SageFs.SessionManagementOps.stub.AdvanceRun
        EndAppRun = SageFs.SessionManagementOps.stub.EndAppRun
        AwaitReady = fun _ _ -> Task.FromResult(Result.Error (SageFs.SageFsError.HardResetFailed "Not available"))
        SwitchWorkflow = fun _ _ -> Task.FromResult(Result.Error (SageFsError.HardResetFailed "Not available"))
        GetAdoptedCore = fun _ -> Task.FromResult(None)
        GetWarmupProgress = fun _ -> Task.FromResult(None)
      }

      let ctx =
        { FrictionStore = None
          DiagnosticsChanged = (Event<Features.DiagnosticsStore.T>()).Publish
          StateChanged = None
          SessionOps = ops
          SessionMap = sessionMap
          McpPort = 0
          Dispatch = None
          GetElmModel = None
          GetElmRegions = None
          GetWarmupContext = None
          GetFeatureState = None; RecordEval = None
          ActivityTracker = SageFs.AgentActivityTracker.create()
          LiveSnapshotSink = None
          CohortOwner = None
          GetDaemonHealth = fun () -> None
          GetProcessTelemetry = fun () -> None } : McpContext

      let hardResetTask = hardResetSession ctx "agent1" true (Some "aaa00001") None
      let! completed = Task.WhenAny(hardResetTask, Task.Delay(1000))

      obj.ReferenceEquals(completed, hardResetTask)
      |> Expect.isTrue "rebuild hard reset should return immediately"

      let! message = hardResetTask
      message
      |> Expect.stringContains "should explain that rebuild continues in background" "Hard reset initiated"

      restartStarted.Task.IsCompleted
      |> Expect.isTrue "background restart should have started"

      // The SessionManager owns the registry: a build-first rebuild keeps the
      // live worker serving, so the tool must not mark the session Restarting.
      statuses |> Seq.toList
      |> Expect.isEmpty "the tool writes no session status"

      allowRestartFinish.TrySetResult(()) |> ignore
    }

    testTask "hardResetSession without rebuild recycles the targeted session's worker process, and only that session" {
      let ctx, restartLog, routedSessions = mkTrackingCtx ()

      let! _ = hardResetSession ctx "agent1" false (Some "aaa00001") None

      // A hard reset without a rebuild still replaces the worker PROCESS (the
      // owner's spawnFirst) so project assemblies already loaded in the old
      // worker's Default load context are actually replaced — an in-process
      // FSI rebuild kept them. Session resolution consults the proxy once;
      // there is no separate worker-proxy route for this call anymore.
      routedSessions |> Seq.toList
      |> Expect.equal "only session-AAA's proxy was consulted, once" ["aaa00001"]

      restartLog |> Seq.toList
      |> Expect.equal "the owner restarts session-AAA without a rebuild" [("aaa00001", false)]

      ctx.SessionMap.["agent2"]
      |> Expect.equal "agent2 session untouched" "bbb00002"
    }

    testTask "resetSession only routes to the targeted session" {
      let ctx, restartLog, routedSessions = mkTrackingCtx ()

      let! _ = resetSession ctx "agent1" (Some "aaa00001") None

      // See above: resolution + routing each consult the proxy, both on the
      // same targeted session.
      routedSessions |> Seq.toList |> Seq.distinct |> Seq.toList
      |> Expect.equal "only session-AAA routed" ["aaa00001"]

      restartLog.Count
      |> Expect.equal "no process restarts for soft reset" 0

      ctx.SessionMap.["agent2"]
      |> Expect.equal "agent2 session untouched" "bbb00002"
    }

    testTask "resetSession updates listSessions while the worker is warming" {
      let ctx, sid, resetStarted, allowResetFinish = mkStatusSyncCtx ()

      let resetTask = resetSession ctx "agent1" (Some sid) None

      let! started =
        waitForAsync 5000 (fun () ->
          Task.FromResult(resetStarted.Task.IsCompleted))

      started
      |> Expect.isTrue "soft reset should reach the worker before assertions"

      let! listed = listSessions ctx
      listed
      |> Expect.stringContains
        "listSessions should reflect that the session is re-warming during a soft reset"
        "Starting"

      let! status = getStatus ctx "agent1" (Some sid) None
      status
      |> Expect.stringContains
        "getStatus should surface the live worker warming state"
        "State: WarmingUp"

      allowResetFinish.TrySetResult(()) |> ignore

      let! resetResult = resetTask
      resetResult
      |> Expect.stringContains
        "soft reset should still complete successfully"
        "reset"
    }

    testTask "WHY — getStatus — synchronizes registry with worker status because agents compare list_sessions and get_fsi_status before trusting REPL readiness" {
      let ctx, _, resetStarted, _ = mkStatusSyncCtx ()

      resetStarted.TrySetResult(()) |> ignore

      let! _ = getStatus ctx "agent1" None (Some @"C:\Code\Repos\SageFs")

      let! listed = listSessions ctx
      listed
      |> Expect.stringContains
        "listSessions should no longer report Ready when live worker status is still warming"
        "Starting"
    }

    testTask "WHY — hardResetSession with rebuild=true — a failed build is recorded as the rebuild outcome and never written to the registry, because the SessionManager owns session status and build-first keeps the worker serving" {
      // Create context where RestartSession can be controlled via TCS
      let sidStr = "bbb00011"
      let sessionMap = ConcurrentDictionary<string, string>()
      sessionMap.["agent1"] <- sidStr
      let statuses = ResizeArray<WorkerProtocol.SessionLifecycleStatus>()
      let restartResult = TaskCompletionSource<Result<string, SageFsError>>()
      let faultedSignal = TaskCompletionSource<unit>()
      let finished = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

      let ops : SessionManagementOps = {
        CreateSession = fun _ _ _ -> Task.FromResult(Ok "new-session")
        ListSessions = fun () -> Task.FromResult("No sessions")
        StopSession = fun _ -> Task.FromResult(Ok "stopped")
        PurgeSession = fun _ -> Task.FromResult(Ok "purged")
        RestartSession = fun _ _ -> restartResult.Task
        GetProxy = fun _ -> Task.FromResult(None)
        GetSessionInfo = fun id ->
          Task.FromResult(Some {
            Id = id
            Name = None; Projects = []; WorkingDirectory = ""; SolutionRoot = None
            Status = WorkerProtocol.SessionLifecycleStatus.Ready { Pid = 0; Port = None }
            Workflow = WorkflowTypes.SessionWorkflow.Interactive
            CreatedAt = DateTime.UtcNow; LastActivity = DateTime.UtcNow
            ActiveProject = None; ProjectRoles = []; App = SageFs.AppRun.AppRunState.NotRunning
          })
        GetAllSessions = fun () -> Task.FromResult([])
        UpdateSessionStatus = fun _ status ->
          statuses.Add(status)
          if (match status with WorkerProtocol.SessionLifecycleStatus.Faulted _ -> true | _ -> false) then
            faultedSignal.TrySetResult(()) |> ignore
          Task.FromResult(())
        NotifyWorkerDied = fun _ -> ()
        ClaimRun = SageFs.SessionManagementOps.stub.ClaimRun
        ClaimStop = SageFs.SessionManagementOps.stub.ClaimStop
        AdvanceRun = SageFs.SessionManagementOps.stub.AdvanceRun
        EndAppRun = SageFs.SessionManagementOps.stub.EndAppRun
        AwaitReady = fun _ _ -> Task.FromResult(Result.Error (SageFs.SageFsError.HardResetFailed "Not available"))
        SwitchWorkflow = fun _ _ -> Task.FromResult(Result.Error (SageFsError.HardResetFailed "Not available"))
        GetAdoptedCore = fun _ -> Task.FromResult(None)
        GetWarmupProgress = fun _ -> Task.FromResult(None)
      }

      let ctx =
        { FrictionStore = None
          DiagnosticsChanged = (Event<Features.DiagnosticsStore.T>()).Publish
          StateChanged = None
          SessionOps = ops
          SessionMap = sessionMap
          McpPort = 0
          Dispatch = None
          GetElmModel = None
          GetElmRegions = None
          GetWarmupContext = None
          GetFeatureState = None; RecordEval = None
          ActivityTracker = SageFs.AgentActivityTracker.create()
          LiveSnapshotSink = None
          CohortOwner = None
          GetDaemonHealth = fun () -> None
          GetProcessTelemetry = fun () -> None } : McpContext

      // The background rebuild reports its outcome as one final status event.
      let ctx =
        { ctx with
            Dispatch = Some (fun msg ->
              match msg with
              | SageFsMsg.Event (TuiEvent.SessionStatusChanged _) -> finished.TrySetResult(()) |> ignore
              | _ -> ()) }

      let! message = hardResetSession ctx "agent1" true (Some sidStr) None

      message
      |> Expect.stringContains "should return immediately with status message" "Hard reset initiated"

      restartResult.TrySetResult(Error (SageFsError.HardResetFailed "build failed"))
      |> Expect.isTrue "should be able to complete restart TCS"

      let! winner = Task.WhenAny(finished.Task :> Task, Task.Delay 5000)
      obj.ReferenceEquals(winner, finished.Task)
      |> Expect.isTrue "the background rebuild must report an outcome"

      statuses |> Seq.toList
      |> Expect.isEmpty "the tool writes no session status — the SessionManager owns it"

      match rebuildOutcomes.TryGetValue sidStr with
      | true, RebuildOutcome.FailedStillServing (SageFsError.HardResetFailed "build failed", _) -> ()
      | _, other -> failtestf "expected FailedStillServing carrying the build error, got %A" other
    }

    testTask "WHY — hardResetSession with rebuild=true — an exception from RestartSession is recorded as a failed rebuild and reported, never swallowed, because a fire-and-forget task must surface its failure" {
      let sidStr = "bbb00012"
      let finished = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
      let sessionMap = ConcurrentDictionary<string, string>()
      sessionMap.["agent1"] <- sidStr
      let statuses = ResizeArray<WorkerProtocol.SessionLifecycleStatus>()
      let restartCalled = TaskCompletionSource<unit>()
      let faultedSignal = TaskCompletionSource<unit>()

      let ops : SessionManagementOps = {
        CreateSession = fun _ _ _ -> Task.FromResult(Ok "new-session")
        ListSessions = fun () -> Task.FromResult("No sessions")
        StopSession = fun _ -> Task.FromResult(Ok "stopped")
        PurgeSession = fun _ -> Task.FromResult(Ok "purged")
        RestartSession = fun _ _ ->
          restartCalled.TrySetResult(()) |> ignore
          // This exception should be caught by the fire-and-forget task
          raise (InvalidOperationException "unexpected crash in RestartSession")
        GetProxy = fun _ -> Task.FromResult(None)
        GetSessionInfo = fun id ->
          Task.FromResult(Some {
            Id = id
            Name = None; Projects = []; WorkingDirectory = ""; SolutionRoot = None
            Status = WorkerProtocol.SessionLifecycleStatus.Ready { Pid = 0; Port = None }
            Workflow = WorkflowTypes.SessionWorkflow.Interactive
            CreatedAt = DateTime.UtcNow; LastActivity = DateTime.UtcNow
            ActiveProject = None; ProjectRoles = []; App = SageFs.AppRun.AppRunState.NotRunning
          })
        GetAllSessions = fun () -> Task.FromResult([])
        UpdateSessionStatus = fun _ status ->
          statuses.Add(status)
          if (match status with WorkerProtocol.SessionLifecycleStatus.Faulted _ -> true | _ -> false) then
            faultedSignal.TrySetResult(()) |> ignore
          Task.FromResult(())
        NotifyWorkerDied = fun _ -> ()
        ClaimRun = SageFs.SessionManagementOps.stub.ClaimRun
        ClaimStop = SageFs.SessionManagementOps.stub.ClaimStop
        AdvanceRun = SageFs.SessionManagementOps.stub.AdvanceRun
        EndAppRun = SageFs.SessionManagementOps.stub.EndAppRun
        AwaitReady = fun _ _ -> Task.FromResult(Result.Error (SageFs.SageFsError.HardResetFailed "Not available"))
        SwitchWorkflow = fun _ _ -> Task.FromResult(Result.Error (SageFsError.HardResetFailed "Not available"))
        GetAdoptedCore = fun _ -> Task.FromResult(None)
        GetWarmupProgress = fun _ -> Task.FromResult(None)
      }

      let ctx =
        { FrictionStore = None
          DiagnosticsChanged = (Event<Features.DiagnosticsStore.T>()).Publish
          StateChanged = None
          SessionOps = ops
          SessionMap = sessionMap
          McpPort = 0
          Dispatch = None
          GetElmModel = None
          GetElmRegions = None
          GetWarmupContext = None
          GetFeatureState = None; RecordEval = None
          ActivityTracker = SageFs.AgentActivityTracker.create()
          LiveSnapshotSink = None
          CohortOwner = None
          GetDaemonHealth = fun () -> None
          GetProcessTelemetry = fun () -> None } : McpContext

      // The background rebuild reports its outcome as one final status event.
      let ctx =
        { ctx with
            Dispatch = Some (fun msg ->
              match msg with
              | SageFsMsg.Event (TuiEvent.SessionStatusChanged _) -> finished.TrySetResult(()) |> ignore
              | _ -> ()) }

      let! message = hardResetSession ctx "agent1" true (Some sidStr) None

      message
      |> Expect.stringContains "should return immediately" "Hard reset initiated"

      let! winner = Task.WhenAny(finished.Task :> Task, Task.Delay 5000)
      obj.ReferenceEquals(winner, finished.Task)
      |> Expect.isTrue "the background rebuild must report an outcome, not swallow the exception"

      restartCalled.Task.IsCompleted |> Expect.isTrue "RestartSession should be invoked"

      statuses |> Seq.toList
      |> Expect.isEmpty "the tool writes no session status — the SessionManager owns it"

      match rebuildOutcomes.TryGetValue sidStr with
      | true, RebuildOutcome.FailedStillServing (SageFsError.Unexpected ex, _) ->
        ex.Message |> Expect.stringContains "the outcome carries the exception" "unexpected crash in RestartSession"
      | _, other -> failtestf "expected a failed rebuild carrying the exception, got %A" other
    }

    testTask "concurrent agents: resetting one never touches the other's session" {
      let ctx, restartLog, routedSessions = mkTrackingCtx ()

      // Agent1 hard resets their session
      let! _ = hardResetSession ctx "agent1" true (Some "aaa00001") None
      // Agent2 soft resets their session
      let! _ = resetSession ctx "agent2" (Some "bbb00002") None

      restartLog |> Seq.toList
      |> Expect.equal "only AAA was restarted" [("aaa00001", true)]

      // Lookup pattern per operation, with the typed resolver in play:
      // agent1's rebuild hard reset consults the proxy once during session
      // resolution (no proxy routing — rebuild goes via SessionManager ops).
      // agent2's soft reset consults the proxy once during resolution and once
      // during routing. Both operations touch ONLY their own session — any
      // cross-session lookup would change this exact list.
      routedSessions |> Seq.toList
      |> Expect.equal "only AAA and BBB touched, each by its own operation" ["aaa00001"; "bbb00002"; "bbb00002"]

      ctx.SessionMap.["agent1"]
      |> Expect.equal "agent1 still on AAA" "aaa00001"

      ctx.SessionMap.["agent2"]
      |> Expect.equal "agent2 still on BBB" "bbb00002"
    }
  ]

/// Pure unit tests for LiveTestState.statusEntriesForSession — no daemon, no FSI, no I/O.
/// These define the session-isolation contract at the state layer.
///
/// Session isolation moved layers: it used to be `TestSessionMap` filtering
/// WITHIN one shared `LiveTestState` (a `Map<TestId, string>` — which,
/// keyed only by TestId, could hold at most ONE session's attribution per
/// TestId, so two sessions whose TestIds collide — e.g. two checkouts of one
/// repo — had one overwrite the other's entry: last-discovery-wins). Now
/// every session gets its OWN `LiveTestState` (routed per session by
/// `SageFsModel.cycleForSession` — see `SessionCycleIsolation` below for the
/// real end-to-end proof), so `statusEntriesForSession` no longer filters at
/// all: the state handed to it already belongs wholly to one session.
module LiveTestStateIsolation =
  open SageFs.Features.LiveTesting

  let private mkEntry (tid: string) : TestStatusEntry =
    { TestId = TestId.TestId tid
      DisplayName = tid
      FullName = tid
      Origin = TestOrigin.ReflectionOnly
      Framework = TestFramework.Expecto
      Category = TestCategory.Unit
      CurrentPolicy = RunPolicy.OnEveryChange
      Status = TestRunStatus.Passed System.TimeSpan.Zero
      PreviousStatus = TestRunStatus.Detected }

  let private mkState (entries: TestStatusEntry array) =
    { LiveTestState.empty with
        StatusIndex = TestStatusIndex.fromEntries entries }

  let tests = testList "LiveTestState session result isolation" [

    test "statusEntriesForSession returns every entry in a session-scoped state, regardless of the sessionId argument" {
      let entryA = mkEntry "testA"
      let state = mkState [| entryA |]
      [ "session-A"; "session-B"; "" ]
      |> List.iter (fun sid ->
        LiveTestState.statusEntriesForSession sid state
        |> Array.map (fun e -> e.DisplayName)
        |> Expect.equal (sprintf "sessionId=%s should not change what a session-scoped state returns" sid) [| "testA" |])
    }

    test "an empty state returns zero entries for any sessionId" {
      let state = mkState [||]
      LiveTestState.statusEntriesForSession "any-session" state
      |> Array.length
      |> Expect.equal "no entries means no entries, regardless of which session asked" 0
    }
  ]

/// End-to-end proof that two sessions' live-testing cycles are genuinely
/// independent — the real fix for item 13b: two sessions whose TestIds
/// COLLIDE (identical `fullName|framework`, e.g. two checkouts of one repo)
/// keep separate pass/fail outcomes because they never share a
/// `LiveTestState` to begin with. Exercises the same `SessionSwitched`
/// promote/demote mechanism the daemon uses when a viewer moves between
/// sessions — this is where the old `TestSessionMap` (last-writer-wins)
/// bug actually bit.
module SessionCycleIsolation =
  open SageFs.Features.LiveTesting

  let private mkSnap (id: WorkerProtocol.SessionId) (dir: string) : SessionSnapshot =
    { Id = id; Name = None; Projects = [ dir + "/Project.fsproj" ]
      Status = SessionDisplayStatus.Running
      LastActivity = System.DateTime.UtcNow
      EvalCount = 0
      UpSince = System.DateTime.UtcNow
      WorkingDirectory = dir }

  let tests = testList "SessionCycleIsolation" [ test "two sessions on two checkouts of the same repo (colliding TestIds) keep independent pass/fail outcomes" {
    // Identical fullName|framework — the exact TestId-collision scenario a
    // cohort's worktrees produce (two checkouts of one repository).
    let collidingId = TestId.create "MyModule.tests" TestFramework.Expecto
    let tc =
      { TestCase.Id = collidingId; FullName = "MyModule.tests"; DisplayName = "tests"
        Origin = TestOrigin.ReflectionOnly; Labels = []; Framework = TestFramework.Expecto
        Category = TestCategory.Unit }

    let sidA = WorkerProtocol.SessionId.newId ()
    let sidB = WorkerProtocol.SessionId.newId ()
    let sidAStr = WorkerProtocol.SessionId.value sidA
    let sidBStr = WorkerProtocol.SessionId.value sidB

    let m0 = SageFsModel.initial ()
    let m1, _ = SageFsUpdate.update (SageFsMsg.Event (TuiEvent.SessionCreated (mkSnap sidA "/repo/worktree-a"))) m0
    let m2, _ = SageFsUpdate.update (SageFsMsg.Event (TuiEvent.SessionCreated (mkSnap sidB "/repo/worktree-b"))) m1

    // Session A: becomes active, discovers, runs, PASSES.
    let m3, _ = SageFsUpdate.update (SageFsMsg.Event (TuiEvent.SessionSwitched (None, sidAStr))) m2
    let m4, _ = SageFsUpdate.update (SageFsMsg.Event (TuiEvent.TestsDiscovered (sidAStr, [| tc |]))) m3
    let m5, _ = SageFsUpdate.update (SageFsMsg.Event (TuiEvent.TestRunStarted ([| collidingId |], Some sidAStr))) m4
    let passResult =
      { TestRunResult.TestId = collidingId; TestName = "tests"
        Result = TestResult.Passed (System.TimeSpan.FromMilliseconds 3.0)
        Timestamp = System.DateTimeOffset.UtcNow; Output = None }
    let m6, _ = SageFsUpdate.update (SageFsMsg.Event (TuiEvent.TestResultsBatch (Some sidAStr, [| passResult |]))) m5
    let m7, _ = SageFsUpdate.update (SageFsMsg.Event (TuiEvent.TestRunCompleted (Some sidAStr))) m6

    // Session B: becomes active (A is parked), discovers the SAME TestId,
    // runs, FAILS.
    let m8, _ = SageFsUpdate.update (SageFsMsg.Event (TuiEvent.SessionSwitched (Some sidAStr, sidBStr))) m7
    let m9, _ = SageFsUpdate.update (SageFsMsg.Event (TuiEvent.TestsDiscovered (sidBStr, [| tc |]))) m8
    let m10, _ = SageFsUpdate.update (SageFsMsg.Event (TuiEvent.TestRunStarted ([| collidingId |], Some sidBStr))) m9
    let failResult =
      { TestRunResult.TestId = collidingId; TestName = "tests"
        Result = TestResult.Failed (TestFailure.AssertionFailed "diverged", System.TimeSpan.FromMilliseconds 4.0)
        Timestamp = System.DateTimeOffset.UtcNow; Output = None }
    let m11, _ = SageFsUpdate.update (SageFsMsg.Event (TuiEvent.TestResultsBatch (Some sidBStr, [| failResult |]))) m10
    let final, _ = SageFsUpdate.update (SageFsMsg.Event (TuiEvent.TestRunCompleted (Some sidBStr))) m11

    // Session A's view is untouched by session B's later discovery/run of
    // the SAME TestId — the whole point of item 13b.
    let aState = (SageFsModel.cycleForSession sidAStr final).TestState
    aState.LastResults
    |> Map.tryFind collidingId
    |> function
       | Some { Result = TestResult.Passed _ } -> ()
       | other -> failwithf "session A's Passed result should survive session B's colliding run, got %A" other

    let bState = (SageFsModel.cycleForSession sidBStr final).TestState
    bState.LastResults
    |> Map.tryFind collidingId
    |> function
       | Some { Result = TestResult.Failed _ } -> ()
       | other -> failwithf "session B should see its own Failed result, got %A" other
  };

    test "a BACKGROUND session's own discovery+results never touch the ACTIVE session's cycle, even for a colliding TestId" {
      // The literal bug this item fixes: session A stays Primary/active the
      // WHOLE time (a human is viewing it, or it was simply created first) —
      // session B never becomes Primary — yet B's own worker keeps
      // discovering and running its tests in the background. Before this
      // fix, TestsDiscovered/TestResultsBatch always wrote into
      // `model.LiveTesting` unconditionally, so B's events corrupted A's
      // Primary cycle regardless of which session was "active". This is the
      // scenario `TestSessionMap` (single-valued per TestId) could not
      // survive for colliding TestIds.
      let collidingId = TestId.create "MyModule.tests" TestFramework.Expecto
      let tc =
        { TestCase.Id = collidingId; FullName = "MyModule.tests"; DisplayName = "tests"
          Origin = TestOrigin.ReflectionOnly; Labels = []; Framework = TestFramework.Expecto
          Category = TestCategory.Unit }
      let sidA = WorkerProtocol.SessionId.newId ()
      let sidB = WorkerProtocol.SessionId.newId ()
      let sidAStr = WorkerProtocol.SessionId.value sidA
      let sidBStr = WorkerProtocol.SessionId.value sidB

      let m0 = SageFsModel.initial ()
      let m1, _ = SageFsUpdate.update (SageFsMsg.Event (TuiEvent.SessionCreated (mkSnap sidA "/repo/worktree-a"))) m0
      let m2, _ = SageFsUpdate.update (SageFsMsg.Event (TuiEvent.SessionCreated (mkSnap sidB "/repo/worktree-b"))) m1
      // Seed B into PerSessionLiveTesting by briefly switching to it, then
      // back to A — mirroring how a cohort session typically gets a moment
      // of focus (creation/join) before settling into the background while
      // another session stays viewed.
      let m3, _ = SageFsUpdate.update (SageFsMsg.Event (TuiEvent.SessionSwitched (None, sidBStr))) m2
      let m4, _ = SageFsUpdate.update (SageFsMsg.Event (TuiEvent.SessionSwitched (Some sidBStr, sidAStr))) m3

      // A is now Primary/active. A discovers, runs, PASSES — all while active.
      let m5, _ = SageFsUpdate.update (SageFsMsg.Event (TuiEvent.TestsDiscovered (sidAStr, [| tc |]))) m4
      let m6, _ = SageFsUpdate.update (SageFsMsg.Event (TuiEvent.TestRunStarted ([| collidingId |], Some sidAStr))) m5
      let passResult =
        { TestRunResult.TestId = collidingId; TestName = "tests"
          Result = TestResult.Passed (System.TimeSpan.FromMilliseconds 3.0)
          Timestamp = System.DateTimeOffset.UtcNow; Output = None }
      let m7, _ = SageFsUpdate.update (SageFsMsg.Event (TuiEvent.TestResultsBatch (Some sidAStr, [| passResult |]))) m6
      let m8, _ = SageFsUpdate.update (SageFsMsg.Event (TuiEvent.TestRunCompleted (Some sidAStr))) m7

      // A is STILL Primary/active here — no SessionSwitched to B. B's own
      // worker discovers and runs the SAME (colliding) TestId anyway, and
      // FAILS.
      let m9, _ = SageFsUpdate.update (SageFsMsg.Event (TuiEvent.TestsDiscovered (sidBStr, [| tc |]))) m8
      let m10, _ = SageFsUpdate.update (SageFsMsg.Event (TuiEvent.TestRunStarted ([| collidingId |], Some sidBStr))) m9
      let failResult =
        { TestRunResult.TestId = collidingId; TestName = "tests"
          Result = TestResult.Failed (TestFailure.AssertionFailed "diverged", System.TimeSpan.FromMilliseconds 4.0)
          Timestamp = System.DateTimeOffset.UtcNow; Output = None }
      let m11, _ = SageFsUpdate.update (SageFsMsg.Event (TuiEvent.TestResultsBatch (Some sidBStr, [| failResult |]))) m10
      let final, _ = SageFsUpdate.update (SageFsMsg.Event (TuiEvent.TestRunCompleted (Some sidBStr))) m11

      // A (still Primary throughout) must be completely unaffected by B's
      // background discovery/run of the colliding TestId.
      final.LiveTesting.TestState.LastResults
      |> Map.tryFind collidingId
      |> function
         | Some { Result = TestResult.Passed _ } -> ()
         | other -> failwithf "the ACTIVE session's Primary cycle must be untouched by a background session's colliding run, got %A" other

      let bState = (SageFsModel.cycleForSession sidBStr final).TestState
      bState.LastResults
      |> Map.tryFind collidingId
      |> function
         | Some { Result = TestResult.Failed _ } -> ()
         | other -> failwithf "session B should see its own Failed result in its own (background) cycle, got %A" other
    };

    test "a session's warmup discovery lands in its own cycle even though it was NEVER active — no SessionSwitched seed, no enable round-trip (roast UX-6 keystone)" {
      // The exact background-session gap this item closes: B is created
      // while A is already active and B is NEVER switched to (unlike the
      // "seed" test above, which briefly makes B Primary before parking it —
      // that seed step is the workaround this fix removes the need for).
      // Before the fix, `tryResolveLiveTestingTarget` returned `None` for a
      // `Some sid` target that is neither the active session NOR already a
      // key in `PerSessionLiveTesting`, so the daemon's warmup-time
      // `TestsDiscovered` dispatch was silently dropped on the floor — a
      // background session's Total stayed 0 until an `enable` round-trip
      // (which happens to switch-and-seed) gave it a map entry to land in.
      let tc =
        { TestCase.Id = TestId.create "RuntimeBugs.Tests.addTest" TestFramework.Expecto
          FullName = "RuntimeBugs.Tests.addTest"; DisplayName = "addTest"
          Origin = TestOrigin.ReflectionOnly; Labels = []; Framework = TestFramework.Expecto
          Category = TestCategory.Unit }
      let sidA = WorkerProtocol.SessionId.newId ()
      let sidB = WorkerProtocol.SessionId.newId ()
      let sidAStr = WorkerProtocol.SessionId.value sidA
      let sidBStr = WorkerProtocol.SessionId.value sidB

      let m0 = SageFsModel.initial ()
      let m1, _ = SageFsUpdate.update (SageFsMsg.Event (TuiEvent.SessionCreated (mkSnap sidA "/repo/a"))) m0
      // A becomes active (a real session-create flow always ends up with
      // exactly one active session very quickly).
      let m2, _ = SageFsUpdate.update (SageFsMsg.Event (TuiEvent.SessionSwitched (None, sidAStr))) m1
      // B is created as a pure background session — created, but NEVER
      // switched to. This is the "hot-reload cohort + live-testing cohort
      // running side by side" scenario: B's worker warms up and discovers
      // its own tests entirely in the background.
      let m3, _ = SageFsUpdate.update (SageFsMsg.Event (TuiEvent.SessionCreated (mkSnap sidB "/repo/b"))) m2

      // B's worker reports warmup-time discovery. A is still Primary/active
      // throughout — no SessionSwitched to B, ever.
      let m4, _ = SageFsUpdate.update (SageFsMsg.Event (TuiEvent.TestsDiscovered (sidBStr, [| tc |]))) m3

      let bState = (SageFsModel.cycleForSession sidBStr m4).TestState
      bState.DiscoveredTests
      |> Array.exists (fun t -> t.Id = tc.Id)
      |> Expect.isTrue "B's warmup discovery should populate its OWN cycle without ever being active"

      // A's Primary cycle must stay untouched by B's background discovery.
      m4.LiveTesting.TestState.DiscoveredTests
      |> Array.exists (fun t -> t.Id = tc.Id)
      |> Expect.isFalse "A's Primary cycle must not gain B's discovered test"
    };

    test "cycleOwnedBySession finds a session's discovery even when the Primary cycle belongs to a DIFFERENT session (the cohort-landing attribution flake)" {
      // The exact divergence that ERRORED CohortLandingGate on cold CI runners:
      // the landing verifier reads a background session's discovery to attribute
      // and run its tests, but `Sessions.ActiveSessionId` (the session pointer)
      // and the OWNER of the Primary live-testing cycle can diverge. A
      // `SessionStopped` of the active session advances the pointer to the next
      // session while deliberately LEAVING the Primary cycle as the stopped
      // session's stale data (see SageFsApp.fs `SessionStopped`). After that,
      // `cycleForSession B` resolves to Primary (B is now the session-active
      // one) but Primary is owned by A — so `ownerSessionId <> Some B`, and the
      // landing verifier's attribution guard refuses to run ("N of N requested
      // test(s) are not attributed to session"). `cycleOwnedBySession` keys off
      // the real data owner, so it finds B's discovery in B's own cycle.
      let tcA =
        { TestCase.Id = TestId.create "A.tests" TestFramework.Expecto
          FullName = "A.tests"; DisplayName = "tests"; Origin = TestOrigin.ReflectionOnly
          Labels = []; Framework = TestFramework.Expecto; Category = TestCategory.Unit }
      let tcB =
        { TestCase.Id = TestId.create "B.tests" TestFramework.Expecto
          FullName = "B.tests"; DisplayName = "tests"; Origin = TestOrigin.ReflectionOnly
          Labels = []; Framework = TestFramework.Expecto; Category = TestCategory.Unit }
      let sidA = WorkerProtocol.SessionId.newId ()
      let sidB = WorkerProtocol.SessionId.newId ()
      let sidAStr = WorkerProtocol.SessionId.value sidA
      let sidBStr = WorkerProtocol.SessionId.value sidB

      let m0 = SageFsModel.initial ()
      let m1, _ = SageFsUpdate.update (SageFsMsg.Event (TuiEvent.SessionCreated (mkSnap sidA "/repo/a"))) m0
      let m2, _ = SageFsUpdate.update (SageFsMsg.Event (TuiEvent.SessionCreated (mkSnap sidB "/repo/b"))) m1
      // A becomes active and discovers into Primary (owner = A).
      let m3, _ = SageFsUpdate.update (SageFsMsg.Event (TuiEvent.SessionSwitched (None, sidAStr))) m2
      let m4, _ = SageFsUpdate.update (SageFsMsg.Event (TuiEvent.TestsDiscovered (sidAStr, [| tcA |]))) m3
      // B discovers in the background (never active) — its discovery lands in
      // B's own PerSessionLiveTesting cycle.
      let m5, _ = SageFsUpdate.update (SageFsMsg.Event (TuiEvent.TestsDiscovered (sidBStr, [| tcB |]))) m4
      // A (the active session) is stopped: the pointer advances to B, but the
      // Primary cycle is deliberately left as A's stale data.
      let final, _ = SageFsUpdate.update (SageFsMsg.Event (TuiEvent.SessionStopped sidAStr)) m5

      // The divergence is real: Primary is still owned by A even though B is the
      // active session now.
      Features.LiveTesting.LiveTestState.ownerSessionId final.LiveTesting.TestState
      |> Expect.equal "Primary cycle must still be owned by the stopped session A (the divergence)" (Some sidAStr)

      // The OLD resolver (`cycleForSession`) returns the wrong cycle for B — it
      // hands back Primary (A's data) because B is the session-active one, so an
      // attribution check against B fails. This is the flake.
      let viaCycleForSession = SageFsModel.cycleForSession sidBStr final
      Features.LiveTesting.LiveTestState.ownerSessionId viaCycleForSession.TestState
      |> Expect.notEqual "cycleForSession resolves B to the WRONG (A-owned) Primary cycle — the attribution bug" (Some sidBStr)

      // The FIX (`cycleOwnedBySession`) resolves B to B's own cycle regardless of
      // the diverged active pointer, so the landing verifier attributes and runs
      // B's tests correctly.
      let viaOwned = SageFsModel.cycleOwnedBySession sidBStr final
      Features.LiveTesting.LiveTestState.ownerSessionId viaOwned.TestState
      |> Expect.equal "cycleOwnedBySession resolves B to B's OWN cycle despite the diverged active pointer" (Some sidBStr)
      viaOwned.TestState.DiscoveredTests
      |> Array.exists (fun t -> t.Id = tcB.Id)
      |> Expect.isTrue "cycleOwnedBySession must surface B's own discovered test"
    } ]

/// SessionMap (agent→session) eviction contract. The map previously had no
/// TryRemove anywhere: setActiveSessionId was the only write, empty-string
/// writes left keys present, stop_session never pruned, and stale agents kept
/// claiming "OccupiedBy: X" on live sessions. These tests pin the eviction.
module SessionMapEviction =

  let mkCtx (live: WorkerProtocol.SessionInfo list) : McpContext =
    let result = globalActorResult.Value
    { FrictionStore = None
      DiagnosticsChanged = result.DiagnosticsChanged
      StateChanged = None
      SessionOps = {
        CreateSession = fun _ _ _ -> System.Threading.Tasks.Task.FromResult(Ok "new-session")
        ListSessions = fun () -> System.Threading.Tasks.Task.FromResult("No sessions")
        StopSession = fun _ -> System.Threading.Tasks.Task.FromResult(Ok "stopped")
        PurgeSession = fun _ -> System.Threading.Tasks.Task.FromResult(Ok "purged")
        RestartSession = fun _ _ -> System.Threading.Tasks.Task.FromResult(Ok "restarted")
        GetProxy = fun _ -> System.Threading.Tasks.Task.FromResult(None)
        GetSessionInfo = fun _ -> System.Threading.Tasks.Task.FromResult(None)
        GetAllSessions = fun () -> System.Threading.Tasks.Task.FromResult(live)
        UpdateSessionStatus = fun _ _ -> System.Threading.Tasks.Task.FromResult(())
        NotifyWorkerDied = fun _ -> ()
        ClaimRun = SageFs.SessionManagementOps.stub.ClaimRun
        ClaimStop = SageFs.SessionManagementOps.stub.ClaimStop
        AdvanceRun = SageFs.SessionManagementOps.stub.AdvanceRun
        EndAppRun = SageFs.SessionManagementOps.stub.EndAppRun
        AwaitReady = fun _ _ -> System.Threading.Tasks.Task.FromResult(Result.Error (SageFs.SageFsError.HardResetFailed "Not available"))
        SwitchWorkflow = fun _ _ -> System.Threading.Tasks.Task.FromResult(Result.Error (SageFsError.HardResetFailed "Not available"))
        GetAdoptedCore = fun _ -> System.Threading.Tasks.Task.FromResult(None)
        GetWarmupProgress = fun _ -> System.Threading.Tasks.Task.FromResult(None) }
      SessionMap = ConcurrentDictionary<string, string>()
      McpPort = 0
      Dispatch = None
      GetElmModel = None
      GetElmRegions = None
      GetWarmupContext = None
      GetFeatureState = None; RecordEval = None
      ActivityTracker = SageFs.AgentActivityTracker.create()
      LiveSnapshotSink = None
      CohortOwner = None
      GetDaemonHealth = fun () -> None
      GetProcessTelemetry = fun () -> None } : McpContext

  let mkInfo id workDir : WorkerProtocol.SessionInfo =
    { Id = id; Name = None; Projects = []
      WorkingDirectory = workDir; SolutionRoot = None
      Status = WorkerProtocol.SessionLifecycleStatus.Ready { Pid = 0; Port = None }
      Workflow = WorkflowTypes.SessionWorkflow.Interactive
      CreatedAt = System.DateTime.UtcNow
      LastActivity = System.DateTime.UtcNow
      ActiveProject = None
      ProjectRoles = []
      App = SageFs.AppRun.AppRunState.NotRunning }

  let tests = testList "SessionMap eviction" [
    test "setActiveSessionId with empty id removes the agent entry" {
      let ctx = mkCtx []
      setActiveSessionId ctx "mcp" "5a6e0001"
      setActiveSessionId ctx "mcp" ""
      activeSessionId ctx "mcp" |> Expect.equal "empty set should clear the mapping" ""
      ctx.SessionMap.ContainsKey "mcp"
      |> Expect.isFalse "empty set must not leave an empty-string key present"
    }

    test "setActiveSessionId with a new session overwrites the old mapping" {
      let ctx = mkCtx []
      setActiveSessionId ctx "mcp" "5a6e0001"
      setActiveSessionId ctx "mcp" "4a120002"
      activeSessionId ctx "mcp" |> Expect.equal "new session should overwrite" "4a120002"
      ctx.SessionMap.Count |> Expect.equal "exactly one entry remains" 1
    }

    testTask "stopSession removes every agent routed to the stopped session" {
      let live = [ mkInfo (testSessionId "5a6e0001") @"C:\Code\Repos\SageFs" ]
      let ctx = mkCtx live
      setActiveSessionId ctx "claude" "5a6e0001"
      setActiveSessionId ctx "copilot" "5a6e0001"
      setActiveSessionId ctx "cursor" "4a120002"   // another (already-stopped) session
      let! _ = stopSession ctx "5a6e0001"
      ctx.SessionMap.TryGetValue "claude" |> fst
      |> Expect.equal "claude mapping removed" false
      ctx.SessionMap.TryGetValue "copilot" |> fst
      |> Expect.equal "copilot mapping removed" false
      ctx.SessionMap.TryGetValue "cursor" |> fst
      |> Expect.equal "unrelated agent mapping untouched" true
      ctx.SessionMap.TryGetValue "cursor" |> snd
      |> Expect.equal "unrelated agent still on its session" "4a120002"
    }

    testTask "listSessions prunes entries pointing at sessions absent from the registry" {
      let live = [ mkInfo (testSessionId "5a6e0001") @"C:\Code\Repos\SageFs" ]
      let ctx = mkCtx live
      // agent1 points at a session the registry no longer has (stopped by any
      // path — HTTP route, dashboard, worker death); agent2 points at the live one.
      ctx.SessionMap.["agent1"] <- "dead0000"
      ctx.SessionMap.["agent2"] <- "5a6e0001"
      let! _ = listSessions ctx
      ctx.SessionMap.ContainsKey "agent1"
      |> Expect.isFalse "dead-target entry pruned by listSessions"
      ctx.SessionMap.TryGetValue "agent2" |> fst
      |> Expect.equal "live-target entry kept" true
      ctx.SessionMap.TryGetValue "agent2" |> snd
      |> Expect.equal "live-target value intact" "5a6e0001"
    }

    testTask "listSessions keeps routing-only agents with no recorded presence" {
      let live = [ mkInfo (testSessionId "5a6e0001") @"C:\Code\Repos\SageFs" ]
      let ctx = mkCtx live
      // "http" / "cli-integrated" are set by McpServer HTTP routes and never
      // fire a presence-recording MCP tool call — they must survive pruning.
      ctx.SessionMap.["http"] <- "5a6e0001"
      let! _ = listSessions ctx
      ctx.SessionMap.ContainsKey "http"
      |> Expect.isTrue "routing-only agent with no presence must be kept"
    }
  ]

/// RED tests for sagefs-multiagent-vision.md §4.1 / §10 Phase 0 item 4:
/// "identity is bound to the connection, not declared." Before this fix,
/// SessionMap/ActivityTracker were keyed by the tool call's self-declared
/// `agentName` string, so two MCP connections that both called themselves
/// "mcp" collided into ONE presence, and either could clear/evict the
/// other's routing just by matching its name. `memberIdFor` binds the key
/// to `currentTransportSessionId` (the ambient AsyncLocal
/// McpServer.createServerCaptureFilter sets from the real connection's
/// `IMcpServer.SessionId`) instead, falling back to the plain name only when
/// no connection is bound (these tests simulate exactly that binding).
module IdentityBinding =

  /// Run `f` with the ambient transport connection bound to `connId`,
  /// restoring the ambient value afterwards so tests never leak into each
  /// other on a reused thread.
  let withConnection (connId: string) (f: unit -> 'a) : 'a =
    let previous = currentTransportSessionId.Value
    currentTransportSessionId.Value <- Some connId
    try f ()
    finally currentTransportSessionId.Value <- previous

  let tests = testList "Identity is bound to the connection" [

    test "WHY — two connections both naming themselves \"mcp\" are TWO members, not one" {
      let ctx = SessionMapEviction.mkCtx []
      withConnection "conn-A" (fun () -> setActiveSessionId ctx "mcp" "aaaaaaaa")
      withConnection "conn-B" (fun () -> setActiveSessionId ctx "mcp" "bbbbbbbb")
      ctx.SessionMap.Count
      |> Expect.equal "two distinct connection-bound entries, despite the identical self-declared name" 2
      withConnection "conn-A" (fun () -> activeSessionId ctx "mcp")
      |> Expect.equal "conn-A keeps its own session" "aaaaaaaa"
      withConnection "conn-B" (fun () -> activeSessionId ctx "mcp")
      |> Expect.equal "conn-B keeps its own session" "bbbbbbbb"
    }

    test "WHY — a caller cannot evict another connection's presence by naming itself the same" {
      let ctx = SessionMapEviction.mkCtx []
      withConnection "conn-A" (fun () -> setActiveSessionId ctx "mcp" "aaaaaaaa")
      // conn-B declares the SAME name "mcp" and clears ITS OWN active session —
      // this must never reach conn-A's entry.
      withConnection "conn-B" (fun () -> setActiveSessionId ctx "mcp" "")
      withConnection "conn-A" (fun () -> activeSessionId ctx "mcp")
      |> Expect.equal "conn-A's session survives conn-B's same-named clear" "aaaaaaaa"
    }

    test "WHY — an unbound caller (no ambient connection) resolves to its plain name, unchanged" {
      // Direct in-process calls (the shape of nearly every other test in this
      // suite) must see EXACTLY the pre-fix behavior: no ambient connection
      // means the resolved key IS the plain agent name.
      let ctx = SessionMapEviction.mkCtx []
      currentTransportSessionId.Value <- None
      setActiveSessionId ctx "claude" "5a6e0001"
      ctx.SessionMap.ContainsKey "claude"
      |> Expect.isTrue "unbound calls key by the plain name, exactly as before"
    }

    testTask "WHY — the dashboard tab appears as a Browser member in list_sessions occupancy" {
      let sid = "5a6e0001"
      let live = [ SessionMapEviction.mkInfo (testSessionId sid) @"C:\Code\Repos\SageFs" ]
      let ctx = SessionMapEviction.mkCtx live
      let now = System.DateTime.UtcNow
      // Simulates Dashboard.fs's createStreamHandler registering a connected
      // tab into the SAME ActivityTracker instance MCP occupancy reads.
      AgentActivityTracker.recordToolCall
        ctx.ActivityTracker (MemberTable.MemberId.display (MemberTable.MemberId.Browser "tab-1"))
        sid None None now
      let occupants = occupantsForSession ctx sid
      occupants
      |> List.exists (fun o -> o.Role = SessionOperations.OccupantRole.Observer && o.AgentName.StartsWith "browser:")
      |> Expect.isTrue "a dashboard tab shows up as an Observer occupant"
      let! listing = listSessions ctx
      listing
      |> Expect.stringContains "list_sessions occupancy mentions the browser tab" "browser:tab-1"
    }

    test "WHY — a Browser member and an Mcp member with the same display cannot collide" {
      let ctx = SessionMapEviction.mkCtx []
      let now = System.DateTime.UtcNow
      // A dashboard tab and an MCP connection could both be labeled "mcp" by
      // coincidence (agentName is caller-controlled prose); MemberId.display
      // must still keep them apart.
      AgentActivityTracker.recordToolCall
        ctx.ActivityTracker (MemberTable.MemberId.display (MemberTable.MemberId.Browser "mcp")) "aaaaaaaa" None None now
      AgentActivityTracker.recordToolCall
        ctx.ActivityTracker (MemberTable.MemberId.display (MemberTable.MemberId.Mcp "mcp")) "bbbbbbbb" None None now
      AgentActivityTracker.getAllPresences ctx.ActivityTracker None
      |> List.length
      |> Expect.equal "Browser and Mcp members with the same raw id are distinct" 2
    }
  ]

[<Tests>]
let sessionIsolationTests = testList "Session Isolation" [
  McpSessionIsolation.tests
  SessionResolutionByWorkingDir.tests
  WorkingDirDeepMatching.tests
  WorkingDirRoutingPriority.tests
  ResetIsolation.tests
  LiveTestStateIsolation.tests
  SessionCycleIsolation.tests
  SessionMapEviction.tests
  IdentityBinding.tests
]
