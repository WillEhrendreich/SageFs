module SageFs.Tests.SessionManagerCqrsTests

open System
open System.Threading
open System.Threading.Tasks
open System.Diagnostics
open Expecto
open Expecto.Flip
open SageFs
open SageFs.WorkerProtocol
open SageFs.SessionManager
open SageFs.Tests.SharedGenerators

// See SessionManager.fs module header for the CQRS rationale.
// Problem: MailboxProcessor reads block behind writes — p99 > 200ms during slow commands.
// Fix: Immutable QuerySnapshot published after each command. Reads bypass the actor.

// ── Test helpers ──────────────────────────────────────────────

let mkSessionInfo (id: SessionId) status =
  {
    Id = id
    Name = None
    Projects = [ "Test.fsproj" ]
    WorkingDirectory = "/test"
    SolutionRoot = None
    CreatedAt = DateTime.MinValue
    LastActivity = DateTime.MinValue
    Status = SessionLifecycleStatus.ofWorkerReport (SessionLifecycleStatus.Ready { Pid = 100; Port = None }) status
    Workflow = WorkflowTypes.SessionWorkflow.Interactive
    ActiveProject = None

    ProjectRoles = []

    App = SageFs.AppRun.AppRunState.NotRunning

  }

let mkManagedSession (id: SessionId) status =
  let proxy : SessionProxy =
    fun _ -> async {
      return WorkerResponse.WorkerError (SageFsError.WorkerSpawnFailed "test")
    }
  {
    Info = mkSessionInfo id status
    Process = Process.GetCurrentProcess()
    Proxy = proxy
    WorkerBaseUrl = "http://localhost:0"
    Projects = [ "Test.fsproj" ]
    WorkingDir = "/test"
    AutoOpenNamespaces = true
    Workflow = WorkflowTypes.SessionWorkflow.Interactive
    RestartState = RestartPolicy.emptyState
    AppGeneration = SageFs.AppRun.AppSlot.initial.Generation

    ProjectRoles = []


  }

// ── QuerySnapshot projection tests ──────────────────────────

let querySnapshotTests = testList "QuerySnapshot projection" [

  test "fromState projects empty state" {
    let snap = QuerySnapshot.fromState ManagerState.empty
    snap.Sessions |> Expect.isEmpty "empty state has no sessions (see SessionManager.fs header)"
  }

  test "fromState projects multiple sessions" {
    let idA = testSessionId "aa000001"
    let idB = testSessionId "bb000001"
    let s1 = mkManagedSession idA SessionStatus.Ready
    let s2 = mkManagedSession idB SessionStatus.Starting
    let state =
      ManagerState.empty
      |> ManagerState.addSession idA s1
      |> ManagerState.addSession idB s2
    let snap = QuerySnapshot.fromState state

    snap.Sessions |> Map.count |> Expect.equal "two sessions" 2
  }

  test "tryGetSession returns existing session info" {
    let idX = testSessionId "ee000001"
    let s = mkManagedSession idX SessionStatus.Ready
    let state = ManagerState.empty |> ManagerState.addSession idX s
    let snap = QuerySnapshot.fromState state

    let result = QuerySnapshot.tryGetSession idX snap
    result |> Expect.isSome "should find session"
    (result |> Option.get).Status
    |> Expect.equal "status is Ready" (SessionLifecycleStatus.ofWorkerReport (SessionLifecycleStatus.Ready { Pid = 100; Port = None }) SessionStatus.Ready)
  }

  test "tryGetSession returns None for missing session" {
    let snap = QuerySnapshot.fromState ManagerState.empty
    QuerySnapshot.tryGetSession (testSessionId "dead0001") snap
    |> Expect.isNone "missing session returns None"
  }

  test "allSessions returns all session infos" {
    let idA = testSessionId "aa000002"
    let idB = testSessionId "bb000002"
    let s1 = mkManagedSession idA SessionStatus.Ready
    let s2 = mkManagedSession idB SessionStatus.Starting
    let state =
      ManagerState.empty
      |> ManagerState.addSession idA s1
      |> ManagerState.addSession idB s2
    let snap = QuerySnapshot.fromState state

    let all = QuerySnapshot.allSessions snap
    all |> List.length |> Expect.equal "two sessions" 2
  }

  test "snapshot is immutable — adding to state doesn't affect existing snapshot" {
    let idA = testSessionId "aa000003"
    let idB = testSessionId "bb000003"
    let s1 = mkManagedSession idA SessionStatus.Ready
    let state1 = ManagerState.empty |> ManagerState.addSession idA s1
    let snap1 = QuerySnapshot.fromState state1

    // Add another session to state
    let s2 = mkManagedSession idB SessionStatus.Starting
    let state2 = state1 |> ManagerState.addSession idB s2
    let snap2 = QuerySnapshot.fromState state2

    // snap1 should NOT see session "b"
    QuerySnapshot.tryGetSession idB snap1
    |> Expect.isNone "old snapshot doesn't see new session"

    // snap2 should see both
    snap2.Sessions |> Map.count |> Expect.equal "new snapshot sees both" 2
  }

  test "snapshot reflects removal in new snapshot" {
    let idA = testSessionId "aa000004"
    let idB = testSessionId "bb000004"
    let s1 = mkManagedSession idA SessionStatus.Ready
    let s2 = mkManagedSession idB SessionStatus.Starting
    let state =
      ManagerState.empty
      |> ManagerState.addSession idA s1
      |> ManagerState.addSession idB s2
    let snap1 = QuerySnapshot.fromState state

    let state2 = state |> ManagerState.removeSession idA
    let snap2 = QuerySnapshot.fromState state2

    snap1.Sessions |> Map.count |> Expect.equal "old snapshot still has 2" 2
    snap2.Sessions |> Map.count |> Expect.equal "new snapshot has 1" 1
    QuerySnapshot.tryGetSession idA snap2
    |> Expect.isNone "removed session not in new snapshot"
  }
]

// ── Warmup progress tests ─────────────────────────────────────

let warmupProgressTests = testList "Warmup progress in QuerySnapshot" [
  test "fromManagerState propagates warmup progress" {
    let idS1 = testSessionId "aa000010"
    let state = {
      ManagerState.empty with
        WarmupProgress = Map.ofList [ idS1, "2/4 Scanned 12 files" ]
    }
    let snap = QuerySnapshot.fromManagerState state
    snap.WarmupProgress
    |> Map.tryFind idS1
    |> Expect.isSome "should have warmup progress for s1"
  }

  test "empty state has no warmup progress" {
    let snap = QuerySnapshot.fromManagerState ManagerState.empty
    snap.WarmupProgress
    |> Map.isEmpty
    |> Expect.isTrue "should be empty"
  }

  test "warmup progress cleared when session removed" {
    let idS1 = testSessionId "aa000011"
    let state = {
      ManagerState.empty with
        WarmupProgress = Map.ofList [ idS1, "2/4 Scanned 12 files" ]
    }
    let afterRemove = ManagerState.removeSession idS1 state
    let snap = QuerySnapshot.fromManagerState afterRemove
    snap.WarmupProgress
    |> Map.containsKey idS1
    |> Expect.isFalse "should not have warmup progress after session removed"
  }

  test "warmup progress for multiple sessions" {
    let idS1 = testSessionId "aa000012"
    let idS2 = testSessionId "bb000012"
    let state = {
      ManagerState.empty with
        WarmupProgress = Map.ofList [
          idS1, "1/4 FSI session created"
          idS2, "3/4 Opening namespaces"
        ]
    }
    let snap = QuerySnapshot.fromManagerState state
    snap.WarmupProgress |> Map.count
    |> Expect.equal "should have 2 entries" 2
    snap.WarmupProgress |> Map.find idS1
    |> Expect.equal "s1 progress" "1/4 FSI session created"
    snap.WarmupProgress |> Map.find idS2
    |> Expect.equal "s2 progress" "3/4 Opening namespaces"
  }

  test "QuerySnapshot.empty has no warmup progress" {
    QuerySnapshot.empty.WarmupProgress
    |> Map.isEmpty
    |> Expect.isTrue "empty snapshot should have no warmup progress"
  }
]

// ── WorkerBaseUrls projection tests ──────────────────────────

let workerBaseUrlTests = testList "WorkerBaseUrls in QuerySnapshot" [

  test "fromState projects WorkerBaseUrls from managed sessions" {
    let idS1 = testSessionId "aa000020"
    let idS2 = testSessionId "bb000020"
    let s1 = { mkManagedSession idS1 SessionStatus.Ready with WorkerBaseUrl = "http://localhost:5001" }
    let s2 = { mkManagedSession idS2 SessionStatus.Starting with WorkerBaseUrl = "http://localhost:5002" }
    let state =
      ManagerState.empty
      |> ManagerState.addSession idS1 s1
      |> ManagerState.addSession idS2 s2
    let snap = QuerySnapshot.fromState state
    snap.WorkerBaseUrls |> Map.count |> Expect.equal "should have 2 URLs" 2
    snap.WorkerBaseUrls |> Map.find idS1 |> Expect.equal "s1 URL" "http://localhost:5001"
    snap.WorkerBaseUrls |> Map.find idS2 |> Expect.equal "s2 URL" "http://localhost:5002"
  }

  test "empty state has empty WorkerBaseUrls" {
    let snap = QuerySnapshot.fromState ManagerState.empty
    snap.WorkerBaseUrls |> Map.isEmpty |> Expect.isTrue "should be empty"
  }

  test "WorkerBaseUrls updates when session removed" {
    let idS1 = testSessionId "aa000021"
    let s1 = { mkManagedSession idS1 SessionStatus.Ready with WorkerBaseUrl = "http://localhost:5001" }
    let state = ManagerState.empty |> ManagerState.addSession idS1 s1
    let snap1 = QuerySnapshot.fromState state
    snap1.WorkerBaseUrls |> Map.containsKey idS1 |> Expect.isTrue "should have s1"

    let state2 = state |> ManagerState.removeSession idS1
    let snap2 = QuerySnapshot.fromState state2
    snap2.WorkerBaseUrls |> Map.containsKey idS1 |> Expect.isFalse "should not have s1 after removal"
  }

  test "QuerySnapshot.empty has empty WorkerBaseUrls" {
    QuerySnapshot.empty.WorkerBaseUrls |> Map.isEmpty |> Expect.isTrue "should be empty"
  }
]

// ── Snapshot-based dashboard helper tests ────────────────────

let snapshotDashboardTests = testList "Snapshot dashboard helpers" [

  test "getSessionState from snapshot returns correct state" {
    let idS1 = testSessionId "aa000030"
    let s1 = mkManagedSession idS1 SessionStatus.Ready
    let state = ManagerState.empty |> ManagerState.addSession idS1 s1
    let snap = QuerySnapshot.fromState state
    let info = QuerySnapshot.tryGetSession idS1 snap
    let sessionState =
      info
      |> Option.map (fun i -> SessionLifecycleStatus.toSessionState i.Status)
      |> Option.defaultValue SessionState.Uninitialized
    sessionState |> Expect.equal "should be Ready" SessionState.Ready
  }

  test "getSessionWorkingDir from snapshot returns correct dir" {
    let idS1 = testSessionId "aa000031"
    let s1 = { mkManagedSession idS1 SessionStatus.Ready with
                 Info = { (mkSessionInfo idS1 SessionStatus.Ready) with WorkingDirectory = "/my/project" } }
    let state = ManagerState.empty |> ManagerState.addSession idS1 s1
    let snap = QuerySnapshot.fromState state
    let dir =
      QuerySnapshot.tryGetSession idS1 snap
      |> Option.map (fun i -> i.WorkingDirectory)
      |> Option.defaultValue ""
    dir |> Expect.equal "should be /my/project" "/my/project"
  }

  test "getStatusMsg from snapshot returns warmup progress" {
    let idS1 = testSessionId "aa000032"
    let state = {
      ManagerState.empty with
        WarmupProgress = Map.ofList [ idS1, "2/4 Loading assemblies" ]
    }
    let snap = QuerySnapshot.fromManagerState state
    let msg = snap.WarmupProgress |> Map.tryFind idS1
    msg |> Expect.equal "should have warmup msg" (Some "2/4 Loading assemblies")
  }

  test "snapshot read is non-blocking (performance)" {
    let idS1 = testSessionId "aa000033"
    let s1 = mkManagedSession idS1 SessionStatus.Ready
    let state = ManagerState.empty |> ManagerState.addSession idS1 s1
    let snap = QuerySnapshot.fromState state
    let sw = Stopwatch.StartNew()
    for _ in 1..1000 do
      QuerySnapshot.tryGetSession idS1 snap |> ignore
    sw.Stop()
    (sw.ElapsedMilliseconds, 50L)
    |> Expect.isLessThan "1000 snapshot reads < 50ms — CQRS scalability (see SessionManager.fs header)"
  }
]

// ── Combined test list ───────────────────────────────────────

[<Tests>]
let allCqrsTests = testList "SessionManager CQRS" [
  querySnapshotTests
  warmupProgressTests
  workerBaseUrlTests
  snapshotDashboardTests
]
