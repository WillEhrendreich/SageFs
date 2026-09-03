module SageFs.Tests.LifecyclePropertyTests

open System
open Expecto
open Expecto.Flip
open FsCheck
open FsCheck.FSharp
open SageFs
open SageFs.WorkerProtocol
open SageFs.SessionManager
open SageFs.Tests.SharedGenerators

let private pick gen = (Gen.sample 1 gen).[0]

// ── Generators ──

let genPolicy =
  gen {
    let! maxRestarts = Gen.choose (1, 10)
    let! backoffBaseMs = Gen.choose (100, 5000)
    let! backoffMaxMs = Gen.choose (backoffBaseMs, 60000)
    let! resetWindowMin = Gen.choose (1, 30)
    let! startupCrashWindowMs = Gen.choose (100, 10000)
    return {
      RestartPolicy.Policy.MaxRestarts = maxRestarts
      RestartPolicy.Policy.BackoffBase = TimeSpan.FromMilliseconds(float backoffBaseMs)
      RestartPolicy.Policy.BackoffMax = TimeSpan.FromMilliseconds(float backoffMaxMs)
      RestartPolicy.Policy.ResetWindow = TimeSpan.FromMinutes(float resetWindowMin)
      RestartPolicy.Policy.StartupCrashWindow = TimeSpan.FromMilliseconds(float startupCrashWindowMs)
      RestartPolicy.Policy.StartupCrashMaxRestarts = 3
    }
  }

let genRestartState =
  gen {
    let! count = Gen.choose (0, 10)
    let! hasLast = ArbMap.defaults |> ArbMap.generate<bool>
    let! hasWindow = ArbMap.defaults |> ArbMap.generate<bool>
    let baseTime = DateTime(2026, 1, 1, 12, 0, 0)
    let! offsetSec = Gen.choose (0, 3600)
    let time = baseTime.AddSeconds(float offsetSec)
    return {
      RestartPolicy.State.RestartCount = count
      RestartPolicy.State.LastRestartAt = match hasLast with true -> Some time | false -> None
      RestartPolicy.State.WindowStart = match hasWindow with true -> Some time | false -> None
    }
  }

let genExitCode =
  Gen.frequency [
    3, Gen.constant 0
    5, Gen.choose (1, 255)
    1, Gen.constant -1
    1, Gen.constant 137
  ]

// ── SessionLifecycle property tests ──

let lifecyclePropertyTests = testList "SessionLifecycle properties" [

  testPropertyWithConfig propConfig "exit code 0 always yields Graceful" <|
    fun () ->
      let policy = pick genPolicy
      let state = pick genRestartState
      let now = DateTime.UtcNow
      match SessionLifecycle.onWorkerExited policy state 0 now with
      | SessionLifecycle.ExitOutcome.Graceful -> ()
      | other -> failtestf "expected Graceful for exit code 0, got %A" other

  testPropertyWithConfig propConfig "non-zero exit never yields Graceful" <|
    fun (PositiveInt code) ->
      let policy = RestartPolicy.defaultPolicy
      let now = DateTime.UtcNow
      match SessionLifecycle.onWorkerExited policy RestartPolicy.emptyState code now with
      | SessionLifecycle.ExitOutcome.Graceful ->
        failtestf "non-zero exit code %d should not be Graceful" code
      | _ -> ()

  testPropertyWithConfig propConfig "statusAfterExit is total over ExitOutcome" <|
    fun () ->
      let outcomes = [
        SessionLifecycle.ExitOutcome.Graceful
        SessionLifecycle.ExitOutcome.RestartAfter(TimeSpan.FromSeconds 1.0, RestartPolicy.emptyState)
        SessionLifecycle.ExitOutcome.Abandoned SageFsError.PipeClosed
      ]
      for outcome in outcomes do
        let status = SessionLifecycle.statusAfterExit outcome
        match outcome with
        | SessionLifecycle.ExitOutcome.Graceful ->
          status |> Expect.equal "Graceful→Stopped" SessionStatus.Stopped
        | SessionLifecycle.ExitOutcome.RestartAfter _ ->
          status |> Expect.equal "RestartAfter→Restarting" SessionStatus.Restarting
        | SessionLifecycle.ExitOutcome.Abandoned _ ->
          status |> Expect.equal "Abandoned→Faulted" SessionStatus.Faulted

  testPropertyWithConfig propConfig "exhausted policy always gives up" <|
    fun () ->
      let policy = pick genPolicy
      let exhaustedState = {
        RestartPolicy.State.RestartCount = policy.MaxRestarts
        RestartPolicy.State.LastRestartAt = Some DateTime.UtcNow
        RestartPolicy.State.WindowStart = Some DateTime.UtcNow
      }
      match SessionLifecycle.onWorkerExited policy exhaustedState 1 DateTime.UtcNow with
      | SessionLifecycle.ExitOutcome.Abandoned _ -> ()
      | other -> failtestf "expected Abandoned after max restarts, got %A" other

  testPropertyWithConfig propConfig "fresh state + non-zero exit always restarts" <|
    fun (PositiveInt code) ->
      let policy = pick genPolicy
      match SessionLifecycle.onWorkerExited policy RestartPolicy.emptyState code DateTime.UtcNow with
      | SessionLifecycle.ExitOutcome.RestartAfter(delay, newState) ->
        (delay.TotalMilliseconds, 0.0) |> Expect.isGreaterThan "positive delay"
        newState.RestartCount |> Expect.equal "count is 1" 1
      | other -> failtestf "fresh state should restart, got %A" other
]

// ── QuerySnapshot pure projection tests ──

let querySnapshotTests = testList "QuerySnapshot projection properties" [

  test "empty state produces empty snapshot" {
    let snap = QuerySnapshot.fromManagerState ManagerState.empty
    snap.Sessions |> Map.isEmpty |> Expect.isTrue "no sessions"
    snap.WarmupProgress |> Map.isEmpty |> Expect.isTrue "no warmup"
    snap.WorkerBaseUrls |> Map.isEmpty |> Expect.isTrue "no urls"
  }

  testPropertyWithConfig propConfig "snapshot session count equals state session count" <|
    fun () ->
      let n = pick (Gen.choose (0, 5))
      let mutable state = ManagerState.empty
      for _ in 1..n do
        let id = SessionId.newId()
        let managed = {
          ManagedSession.Info = {
            Id = id; Name = None; Projects = ["p.fsproj"]
            WorkingDirectory = "C:\\test"
            SolutionRoot = None; CreatedAt = DateTime.UtcNow
            LastActivity = DateTime.UtcNow
            Status = SessionStatus.Ready; WorkerPid = Some 1234
            WorkerPort = None
            FaultReason = None
            Workflow = WorkflowTypes.SessionWorkflow.Interactive
          }
          Process = null; Proxy = pendingProxy; WorkerBaseUrl = ""
          Projects = ["p.fsproj"]; WorkingDir = "C:\\test"
          AutoOpenNamespaces = false
          Workflow = WorkflowTypes.SessionWorkflow.Interactive
          RestartState = RestartPolicy.emptyState
        }
        state <- ManagerState.addSession id managed state
      let snap = QuerySnapshot.fromManagerState state
      snap.Sessions.Count |> Expect.equal "session count matches" state.Sessions.Count

  testPropertyWithConfig propConfig "snapshot preserves session status" <|
    fun () ->
      let status = pick genSessionStatus
      let id = SessionId.newId()
      let managed = {
        ManagedSession.Info = {
          Id = id; Name = None; Projects = ["p.fsproj"]
          WorkingDirectory = "C:\\test"
          SolutionRoot = None; CreatedAt = DateTime.UtcNow
          LastActivity = DateTime.UtcNow
          Status = status; WorkerPid = Some 1234
          WorkerPort = None
          FaultReason = None
          Workflow = WorkflowTypes.SessionWorkflow.Interactive
        }
        Process = null; Proxy = pendingProxy; WorkerBaseUrl = ""
        Projects = ["p.fsproj"]; WorkingDir = "C:\\test"
        AutoOpenNamespaces = false
        Workflow = WorkflowTypes.SessionWorkflow.Interactive
        RestartState = RestartPolicy.emptyState
      }
      let state = ManagerState.addSession id managed ManagerState.empty
      let snap = QuerySnapshot.fromManagerState state
      let snapInfo = snap.Sessions |> Map.find id
      snapInfo.Status |> Expect.equal "status preserved" status

  test "snapshot includes WorkerBaseUrl only when non-empty" {
    let id1 = testSessionId "aa000001"
    let id2 = testSessionId "bb000002"
    let mkManaged url = {
      ManagedSession.Info = {
        Id = id1; Name = None; Projects = ["p.fsproj"]
        WorkingDirectory = "C:\\test"
        SolutionRoot = None; CreatedAt = DateTime.UtcNow
        LastActivity = DateTime.UtcNow
        Status = SessionStatus.Ready; WorkerPid = Some 1
        WorkerPort = None
        FaultReason = None
        Workflow = WorkflowTypes.SessionWorkflow.Interactive
      }
      Process = null; Proxy = pendingProxy; WorkerBaseUrl = url
      Projects = ["p.fsproj"]; WorkingDir = "C:\\test"
      AutoOpenNamespaces = false
      Workflow = WorkflowTypes.SessionWorkflow.Interactive
      RestartState = RestartPolicy.emptyState
    }
    let state =
      ManagerState.empty
      |> ManagerState.addSession id1 (mkManaged "http://localhost:5000")
      |> ManagerState.addSession id2 { mkManaged "" with Info = { (mkManaged "").Info with Id = id2 } }
    let snap = QuerySnapshot.fromManagerState state
    snap.WorkerBaseUrls |> Map.containsKey id1 |> Expect.isTrue "has url for id1"
    snap.WorkerBaseUrls |> Map.containsKey id2 |> Expect.isFalse "no url for id2"
  }

  test "addSession then removeSession yields original state sessions" {
    let id = SessionId.newId()
    let managed = {
      ManagedSession.Info = {
        Id = id; Name = None; Projects = ["p.fsproj"]
        WorkingDirectory = "C:\\test"
        SolutionRoot = None; CreatedAt = DateTime.UtcNow
        LastActivity = DateTime.UtcNow
        Status = SessionStatus.Ready; WorkerPid = Some 1
        WorkerPort = None
        FaultReason = None
        Workflow = WorkflowTypes.SessionWorkflow.Interactive
      }
      Process = null; Proxy = pendingProxy; WorkerBaseUrl = ""
      Projects = ["p.fsproj"]; WorkingDir = "C:\\test"
      AutoOpenNamespaces = false
      Workflow = WorkflowTypes.SessionWorkflow.Interactive
      RestartState = RestartPolicy.emptyState
    }
    let afterAdd = ManagerState.addSession id managed ManagerState.empty
    let afterRemove = ManagerState.removeSession id afterAdd
    afterRemove.Sessions |> Map.isEmpty |> Expect.isTrue "sessions empty after remove"
    afterRemove.WarmupProgress |> Map.isEmpty |> Expect.isTrue "progress empty after remove"
  }
]

[<Tests>]
let tests = testList "Lifecycle properties" [
  lifecyclePropertyTests
  querySnapshotTests
]

