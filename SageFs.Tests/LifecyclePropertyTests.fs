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
    return {
      RestartPolicy.Policy.MaxRestarts = maxRestarts
      RestartPolicy.Policy.BackoffBase = TimeSpan.FromMilliseconds(float backoffBaseMs)
      RestartPolicy.Policy.BackoffMax = TimeSpan.FromMilliseconds(float backoffMaxMs)
      RestartPolicy.Policy.ResetWindow = TimeSpan.FromMinutes(float resetWindowMin)
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

let genStandbyState =
  Gen.elements [
    StandbyState.Warming
    StandbyState.Ready
    StandbyState.Invalidated
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

// ── StandbyPool decision property tests ──

let standbyDecisionTests = testList "StandbyPool.decideRestart properties" [

  testPropertyWithConfig propConfig "rebuild=true always yields ColdRestart" <|
    fun () ->
      let standby = pick (Gen.optionOf (Gen.constant {
          StandbySession.Process = null
          Proxy = Some (fun _ -> async { return WorkerResponse.WorkerReady })
          BaseUrl = "http://localhost:1234"
          State = StandbyState.Ready
          WarmupProgress = None
          Projects = ["test.fsproj"]
          WorkingDir = "C:\\test"
          CreatedAt = DateTime.UtcNow
        }))
      match StandbyPool.decideRestart true standby with
      | RestartDecision.ColdRestart -> ()
      | RestartDecision.SwapStandby _ -> failtest "rebuild=true must cold restart"

  testPropertyWithConfig propConfig "None standby always yields ColdRestart" <|
    fun () ->
      let rebuild = pick (ArbMap.defaults |> ArbMap.generate<bool>)
      match StandbyPool.decideRestart rebuild None with
      | RestartDecision.ColdRestart -> ()
      | RestartDecision.SwapStandby _ -> failtest "None standby must cold restart"

  test "Ready standby with proxy and rebuild=false yields SwapStandby" {
    let standby = Some {
      StandbySession.Process = null
      Proxy = Some (fun _ -> async { return WorkerResponse.WorkerReady })
      BaseUrl = "http://localhost:1234"
      State = StandbyState.Ready
      WarmupProgress = None
      Projects = ["test.fsproj"]
      WorkingDir = "C:\\test"
      CreatedAt = DateTime.UtcNow
    }
    match StandbyPool.decideRestart false standby with
    | RestartDecision.SwapStandby _ -> ()
    | RestartDecision.ColdRestart -> failtest "ready standby should swap"
  }

  test "Warming standby yields ColdRestart" {
    let standby = Some {
      StandbySession.Process = null
      Proxy = None
      BaseUrl = ""
      State = StandbyState.Warming
      WarmupProgress = Some "Loading..."
      Projects = ["test.fsproj"]
      WorkingDir = "C:\\test"
      CreatedAt = DateTime.UtcNow
    }
    match StandbyPool.decideRestart false standby with
    | RestartDecision.ColdRestart -> ()
    | RestartDecision.SwapStandby _ -> failtest "warming standby should cold restart"
  }

  test "Ready standby without proxy yields ColdRestart" {
    let standby = Some {
      StandbySession.Process = null
      Proxy = None
      BaseUrl = "http://localhost:1234"
      State = StandbyState.Ready
      WarmupProgress = None
      Projects = ["test.fsproj"]
      WorkingDir = "C:\\test"
      CreatedAt = DateTime.UtcNow
    }
    match StandbyPool.decideRestart false standby with
    | RestartDecision.ColdRestart -> ()
    | RestartDecision.SwapStandby _ -> failtest "no proxy should cold restart"
  }
]

// ── shouldWarmStandby property tests ──

let warmupDecisionTests = testList "StandbyPool.shouldWarmStandby properties" [

  testPropertyWithConfig propConfig "disabled pool never warms" <|
    fun () ->
      let status = pick genSessionStatus
      StandbyPool.shouldWarmStandby status None false
      |> Expect.isFalse "disabled pool should not warm"

  testPropertyWithConfig propConfig "existing standby prevents warming" <|
    fun () ->
      let standby = Some {
        StandbySession.Process = null
        Proxy = None; BaseUrl = ""; State = StandbyState.Warming
        WarmupProgress = None; Projects = []; WorkingDir = ""; CreatedAt = DateTime.UtcNow
      }
      let status = pick genSessionStatus
      StandbyPool.shouldWarmStandby status standby true
      |> Expect.isFalse "existing standby should prevent warming"

  testPropertyWithConfig propConfig "healthy status + no standby + enabled => warm" <|
    fun () ->
      let healthyStatuses = [
        SessionStatus.Ready
        SessionStatus.Evaluating
        SessionStatus.Building "test"
      ]
      for status in healthyStatuses do
        StandbyPool.shouldWarmStandby status None true
        |> Expect.isTrue (sprintf "healthy status %A should warm" status)

  testPropertyWithConfig propConfig "unhealthy status never warms" <|
    fun () ->
      let unhealthyStatuses = [
        SessionStatus.Starting
        SessionStatus.Faulted
        SessionStatus.Restarting
        SessionStatus.Stopped
      ]
      for status in unhealthyStatuses do
        StandbyPool.shouldWarmStandby status None true
        |> Expect.isFalse (sprintf "unhealthy status %A should not warm" status)
]

// ── QuerySnapshot pure projection tests ──

let querySnapshotTests = testList "QuerySnapshot projection properties" [

  test "empty state produces empty snapshot" {
    let snap = QuerySnapshot.fromManagerState ManagerState.empty
    snap.Sessions |> Map.isEmpty |> Expect.isTrue "no sessions"
    snap.WarmupProgress |> Map.isEmpty |> Expect.isTrue "no warmup"
    snap.WorkerBaseUrls |> Map.isEmpty |> Expect.isTrue "no urls"
    snap.StandbyInfo |> Expect.equal "no pool" StandbyInfo.NoPool
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

// ── computeStandbyInfo property tests ──

let standbyInfoTests = testList "computeStandbyInfo properties" [

  test "empty pool yields NoPool" {
    computeStandbyInfo PoolState.empty
    |> Expect.equal "empty = NoPool" StandbyInfo.NoPool
  }

  test "disabled pool yields NoPool even with standbys" {
    let key = StandbyKey.fromSession ["p.fsproj"] "C:\\test" false WorkflowTypes.SessionWorkflow.Interactive
    let standby = {
      StandbySession.Process = null; Proxy = None; BaseUrl = ""
      State = StandbyState.Ready; WarmupProgress = None
      Projects = ["p.fsproj"]; WorkingDir = "C:\\test"; CreatedAt = DateTime.UtcNow
    }
    let pool = { PoolState.Standbys = Map.ofList [key, standby]; Enabled = false }
    computeStandbyInfo pool
    |> Expect.equal "disabled = NoPool" StandbyInfo.NoPool
  }

  test "all ready yields Ready" {
    let key = StandbyKey.fromSession ["p.fsproj"] "C:\\test" false WorkflowTypes.SessionWorkflow.Interactive
    let standby = {
      StandbySession.Process = null; Proxy = None; BaseUrl = ""
      State = StandbyState.Ready; WarmupProgress = None
      Projects = ["p.fsproj"]; WorkingDir = "C:\\test"; CreatedAt = DateTime.UtcNow
    }
    let pool = { PoolState.Standbys = Map.ofList [key, standby]; Enabled = true }
    computeStandbyInfo pool
    |> Expect.equal "all ready = Ready" StandbyInfo.Ready
  }

  test "any invalidated yields Invalidated" {
    let key = StandbyKey.fromSession ["p.fsproj"] "C:\\test" false WorkflowTypes.SessionWorkflow.Interactive
    let standby = {
      StandbySession.Process = null; Proxy = None; BaseUrl = ""
      State = StandbyState.Invalidated; WarmupProgress = None
      Projects = ["p.fsproj"]; WorkingDir = "C:\\test"; CreatedAt = DateTime.UtcNow
    }
    let pool = { PoolState.Standbys = Map.ofList [key, standby]; Enabled = true }
    computeStandbyInfo pool
    |> Expect.equal "invalidated" StandbyInfo.Invalidated
  }

  test "warming with progress shows progress" {
    let key = StandbyKey.fromSession ["p.fsproj"] "C:\\test" false WorkflowTypes.SessionWorkflow.Interactive
    let standby = {
      StandbySession.Process = null; Proxy = None; BaseUrl = ""
      State = StandbyState.Warming; WarmupProgress = Some "2/4 files"
      Projects = ["p.fsproj"]; WorkingDir = "C:\\test"; CreatedAt = DateTime.UtcNow
    }
    let pool = { PoolState.Standbys = Map.ofList [key, standby]; Enabled = true }
    match computeStandbyInfo pool with
    | StandbyInfo.Warming msg ->
      msg |> Expect.equal "progress message" "2/4 files"
    | other -> failtestf "expected Warming, got %A" other
  }
]

// ── PoolState algebraic tests ──

let poolStateTests = testList "PoolState algebra" [

  testPropertyWithConfig propConfig "setStandby then getStandby roundtrips" <|
    fun () ->
      let key = StandbyKey.fromSession ["p.fsproj"] "C:\\test" false WorkflowTypes.SessionWorkflow.Interactive
      let standby = {
        StandbySession.Process = null; Proxy = None; BaseUrl = ""
        State = StandbyState.Ready; WarmupProgress = None
        Projects = ["p.fsproj"]; WorkingDir = "C:\\test"; CreatedAt = DateTime.UtcNow
      }
      let pool = PoolState.setStandby key standby PoolState.empty
      PoolState.getStandby key pool
      |> Option.isSome
      |> Expect.isTrue "roundtrip"

  testPropertyWithConfig propConfig "removeStandby makes getStandby return None" <|
    fun () ->
      let key = StandbyKey.fromSession ["p.fsproj"] "C:\\test" false WorkflowTypes.SessionWorkflow.Interactive
      let standby = {
        StandbySession.Process = null; Proxy = None; BaseUrl = ""
        State = StandbyState.Ready; WarmupProgress = None
        Projects = ["p.fsproj"]; WorkingDir = "C:\\test"; CreatedAt = DateTime.UtcNow
      }
      let pool =
        PoolState.empty
        |> PoolState.setStandby key standby
        |> PoolState.removeStandby key
      PoolState.getStandby key pool
      |> Option.isNone
      |> Expect.isTrue "removed"

  test "removeStandby on empty pool is no-op" {
    let key = StandbyKey.fromSession ["p.fsproj"] "C:\\test" false WorkflowTypes.SessionWorkflow.Interactive
    let pool = PoolState.removeStandby key PoolState.empty
    pool.Standbys |> Map.isEmpty |> Expect.isTrue "still empty"
  }
]

// ── StandbyKey ordering tests ──

let standbyKeyTests = testList "StandbyKey" [

  test "fromSession sorts projects for deterministic keys" {
    let key1 = StandbyKey.fromSession ["b.fsproj"; "a.fsproj"] "C:\\test" false WorkflowTypes.SessionWorkflow.Interactive
    let key2 = StandbyKey.fromSession ["a.fsproj"; "b.fsproj"] "C:\\test" false WorkflowTypes.SessionWorkflow.Interactive
    key1 |> Expect.equal "sorted projects" key2
  }

  test "different autoOpen yields different keys" {
    let key1 = StandbyKey.fromSession ["p.fsproj"] "C:\\test" true WorkflowTypes.SessionWorkflow.Interactive
    let key2 = StandbyKey.fromSession ["p.fsproj"] "C:\\test" false WorkflowTypes.SessionWorkflow.Interactive
    (key1 = key2) |> Expect.isFalse "autoOpen matters"
  }
]

[<Tests>]
let tests = testList "Lifecycle properties" [
  lifecyclePropertyTests
  standbyDecisionTests
  warmupDecisionTests
  querySnapshotTests
  standbyInfoTests
  poolStateTests
  standbyKeyTests
]

