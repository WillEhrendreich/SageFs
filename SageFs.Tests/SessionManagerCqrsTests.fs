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

// See SessionManager.fs module header for the CQRS rationale.
// Problem: MailboxProcessor reads block behind writes — p99 > 200ms during slow commands.
// Fix: Immutable QuerySnapshot published after each command. Reads bypass the actor.

// ── Test helpers ──────────────────────────────────────────────

let mkSessionInfo id status =
  {
    Id = id
    Name = None
    Projects = [ "Test.fsproj" ]
    WorkingDirectory = "/test"
    SolutionRoot = None
    CreatedAt = DateTime.MinValue
    LastActivity = DateTime.MinValue
    Status = status
    WorkerPid = None
  }

let mkManagedSession id status =
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
    RestartState = RestartPolicy.emptyState
  }

// ── Simulated actor for pattern testing ──────────────────────

type SimCommand =
  | SlowWrite of id: string * AsyncReplyChannel<string>
  | MailboxRead of id: string * AsyncReplyChannel<string option>

type SimState = { Items: Map<string, string> }

let createSimActor (slowMs: int) (initial: SimState) =
  let snapshotRef = ref initial
  let agent = MailboxProcessor<SimCommand>.Start(fun inbox ->
    let rec loop (state: SimState) = async {
      let! cmd = inbox.Receive()
      match cmd with
      | SlowWrite(id, reply) ->
        do! Async.Sleep slowMs
        let newState = { Items = state.Items |> Map.add id "written" }
        snapshotRef.Value <- newState
        reply.Reply "ok"
        return! loop newState
      | MailboxRead(id, reply) ->
        reply.Reply(state.Items |> Map.tryFind id)
        return! loop state
    }
    loop initial)
  let mailboxRead id =
    agent.PostAndAsyncReply(fun reply -> MailboxRead(id, reply))
    |> Async.StartAsTask
  let snapshotRead id =
    snapshotRef.Value.Items |> Map.tryFind id
  agent, mailboxRead, snapshotRead

// ── CQRS pattern tests (isolated simulation) ────────────────

let cqrsPatternTests = testList "CQRS pattern" [

  test "mailbox read blocks behind slow write (demonstrates the problem)" {
    let initial = { Items = Map.ofList [ "s1", "ready" ] }
    let agent, mailboxRead, _snapshotRead = createSimActor 3000 initial

    let _slowTask =
      agent.PostAndAsyncReply(fun reply -> SlowWrite("s2", reply))
      |> Async.StartAsTask

    Thread.Sleep(50)

    let readTask = mailboxRead "s1"
    let completed = readTask.Wait(500)

    completed
    |> Expect.isFalse "mailbox read should be blocked behind slow write (see SessionManager.fs header)"
  }

  test "snapshot read completes instantly during slow write" {
    let initial = { Items = Map.ofList [ "s1", "ready" ] }
    let agent, _mailboxRead, snapshotRead = createSimActor 3000 initial

    let _slowTask =
      agent.PostAndAsyncReply(fun reply -> SlowWrite("s2", reply))
      |> Async.StartAsTask

    Thread.Sleep(50)

    let sw = Stopwatch.StartNew()
    let result = snapshotRead "s1"
    sw.Stop()

    result |> Expect.equal "snapshot read instant during slow write — CQRS bypass (see SessionManager.fs header)" (Some "ready")
    (sw.ElapsedMilliseconds, 10L)
    |> Expect.isLessThan "snapshot read < 10ms — lock-free CQRS (see SessionManager.fs header)"
  }

  test "snapshot eventually updates after write completes" {
    let initial = { Items = Map.ofList [ "s1", "ready" ] }
    let agent, _mailboxRead, snapshotRead = createSimActor 100 initial

    agent.PostAndAsyncReply(fun reply -> SlowWrite("s2", reply))
    |> Async.RunSynchronously
    |> ignore

    snapshotRead "s2"
    |> Expect.equal "new item visible after write" (Some "written")
  }

  test "snapshot is stale during write but returns old data" {
    let initial = { Items = Map.ofList [ "x", "original" ] }
    let agent, _mailboxRead, snapshotRead = createSimActor 3000 initial

    let _slowTask =
      agent.PostAndAsyncReply(fun reply -> SlowWrite("y", reply))
      |> Async.StartAsTask

    Thread.Sleep(50)

    snapshotRead "x"
    |> Expect.equal "existing item visible" (Some "original")

    snapshotRead "y"
    |> Expect.equal "new item not yet visible" None
  }

  test "100 concurrent snapshot reads complete instantly" {
    let items = [ for i in 1..100 -> sprintf "s%d" i, sprintf "val%d" i ] |> Map.ofList
    let initial = { Items = items }
    let agent, _mailboxRead, snapshotRead = createSimActor 5000 initial

    let _slowTask =
      agent.PostAndAsyncReply(fun reply -> SlowWrite("new", reply))
      |> Async.StartAsTask

    Thread.Sleep(50)

    let sw = Stopwatch.StartNew()
    let results =
      [| for i in 1..100 ->
          Task.Run(fun () -> snapshotRead (sprintf "s%d" i)) |]
      |> Task.WhenAll
      |> fun t -> t.Result
    sw.Stop()

    results |> Array.iter (fun r -> r |> Expect.isSome "concurrent snapshot reads must succeed (see SessionManager.fs header)")
    (sw.ElapsedMilliseconds, 100L)
    |> Expect.isLessThan "100 concurrent reads < 100ms — lock-free CQRS (see SessionManager.fs header)"
  }
]

// ── QuerySnapshot projection tests ──────────────────────────

let querySnapshotTests = testList "QuerySnapshot projection" [

  test "fromState projects empty state" {
    let snap = QuerySnapshot.fromState ManagerState.empty StandbyInfo.NoPool
    snap.Sessions |> Expect.isEmpty "empty state has no sessions (see SessionManager.fs header)"
    snap.StandbyInfo |> Expect.equal "standby is NoPool" StandbyInfo.NoPool
  }

  test "fromState projects multiple sessions" {
    let s1 = mkManagedSession "a" SessionStatus.Ready
    let s2 = mkManagedSession "b" SessionStatus.Starting
    let state =
      ManagerState.empty
      |> ManagerState.addSession "a" s1
      |> ManagerState.addSession "b" s2
    let snap = QuerySnapshot.fromState state (StandbyInfo.Warming "building")

    snap.Sessions |> Map.count |> Expect.equal "two sessions" 2
    snap.StandbyInfo |> Expect.equal "standby is Warming" (StandbyInfo.Warming "building")
  }

  test "tryGetSession returns existing session info" {
    let s = mkManagedSession "x" SessionStatus.Ready
    let state = ManagerState.empty |> ManagerState.addSession "x" s
    let snap = QuerySnapshot.fromState state StandbyInfo.NoPool

    let result = QuerySnapshot.tryGetSession "x" snap
    result |> Expect.isSome "should find session"
    (result |> Option.get).Status
    |> Expect.equal "status is Ready" SessionStatus.Ready
  }

  test "tryGetSession returns None for missing session" {
    let snap = QuerySnapshot.fromState ManagerState.empty StandbyInfo.NoPool
    QuerySnapshot.tryGetSession "nonexistent" snap
    |> Expect.isNone "missing session returns None"
  }

  test "allSessions returns all session infos" {
    let s1 = mkManagedSession "a" SessionStatus.Ready
    let s2 = mkManagedSession "b" SessionStatus.Starting
    let state =
      ManagerState.empty
      |> ManagerState.addSession "a" s1
      |> ManagerState.addSession "b" s2
    let snap = QuerySnapshot.fromState state StandbyInfo.NoPool

    let all = QuerySnapshot.allSessions snap
    all |> List.length |> Expect.equal "two sessions" 2
  }

  test "snapshot is immutable — adding to state doesn't affect existing snapshot" {
    let s1 = mkManagedSession "a" SessionStatus.Ready
    let state1 = ManagerState.empty |> ManagerState.addSession "a" s1
    let snap1 = QuerySnapshot.fromState state1 StandbyInfo.NoPool

    // Add another session to state
    let s2 = mkManagedSession "b" SessionStatus.Starting
    let state2 = state1 |> ManagerState.addSession "b" s2
    let snap2 = QuerySnapshot.fromState state2 StandbyInfo.NoPool

    // snap1 should NOT see session "b"
    QuerySnapshot.tryGetSession "b" snap1
    |> Expect.isNone "old snapshot doesn't see new session"

    // snap2 should see both
    snap2.Sessions |> Map.count |> Expect.equal "new snapshot sees both" 2
  }

  test "snapshot reflects removal in new snapshot" {
    let s1 = mkManagedSession "a" SessionStatus.Ready
    let s2 = mkManagedSession "b" SessionStatus.Starting
    let state =
      ManagerState.empty
      |> ManagerState.addSession "a" s1
      |> ManagerState.addSession "b" s2
    let snap1 = QuerySnapshot.fromState state StandbyInfo.NoPool

    let state2 = state |> ManagerState.removeSession "a"
    let snap2 = QuerySnapshot.fromState state2 StandbyInfo.NoPool

    snap1.Sessions |> Map.count |> Expect.equal "old snapshot still has 2" 2
    snap2.Sessions |> Map.count |> Expect.equal "new snapshot has 1" 1
    QuerySnapshot.tryGetSession "a" snap2
    |> Expect.isNone "removed session not in new snapshot"
  }
]

// ── Warmup progress tests ─────────────────────────────────────

let warmupProgressTests = testList "Warmup progress in QuerySnapshot" [
  test "fromManagerState propagates warmup progress" {
    let state = {
      ManagerState.empty with
        WarmupProgress = Map.ofList [ "s1", "2/4 Scanned 12 files" ]
    }
    let snap = QuerySnapshot.fromManagerState state
    snap.WarmupProgress
    |> Map.tryFind "s1"
    |> Expect.isSome "should have warmup progress for s1"
  }

  test "empty state has no warmup progress" {
    let snap = QuerySnapshot.fromManagerState ManagerState.empty
    snap.WarmupProgress
    |> Map.isEmpty
    |> Expect.isTrue "should be empty"
  }

  test "warmup progress cleared when session removed" {
    let state = {
      ManagerState.empty with
        WarmupProgress = Map.ofList [ "s1", "2/4 Scanned 12 files" ]
    }
    let afterRemove = ManagerState.removeSession "s1" state
    let snap = QuerySnapshot.fromManagerState afterRemove
    snap.WarmupProgress
    |> Map.containsKey "s1"
    |> Expect.isFalse "should not have warmup progress after session removed"
  }

  test "warmup progress for multiple sessions" {
    let state = {
      ManagerState.empty with
        WarmupProgress = Map.ofList [
          "s1", "1/4 FSI session created"
          "s2", "3/4 Opening namespaces"
        ]
    }
    let snap = QuerySnapshot.fromManagerState state
    snap.WarmupProgress |> Map.count
    |> Expect.equal "should have 2 entries" 2
    snap.WarmupProgress |> Map.find "s1"
    |> Expect.equal "s1 progress" "1/4 FSI session created"
    snap.WarmupProgress |> Map.find "s2"
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
    let s1 = { mkManagedSession "s1" SessionStatus.Ready with WorkerBaseUrl = "http://localhost:5001" }
    let s2 = { mkManagedSession "s2" SessionStatus.Starting with WorkerBaseUrl = "http://localhost:5002" }
    let state =
      ManagerState.empty
      |> ManagerState.addSession "s1" s1
      |> ManagerState.addSession "s2" s2
    let snap = QuerySnapshot.fromState state StandbyInfo.NoPool
    snap.WorkerBaseUrls |> Map.count |> Expect.equal "should have 2 URLs" 2
    snap.WorkerBaseUrls |> Map.find "s1" |> Expect.equal "s1 URL" "http://localhost:5001"
    snap.WorkerBaseUrls |> Map.find "s2" |> Expect.equal "s2 URL" "http://localhost:5002"
  }

  test "empty state has empty WorkerBaseUrls" {
    let snap = QuerySnapshot.fromState ManagerState.empty StandbyInfo.NoPool
    snap.WorkerBaseUrls |> Map.isEmpty |> Expect.isTrue "should be empty"
  }

  test "WorkerBaseUrls updates when session removed" {
    let s1 = { mkManagedSession "s1" SessionStatus.Ready with WorkerBaseUrl = "http://localhost:5001" }
    let state = ManagerState.empty |> ManagerState.addSession "s1" s1
    let snap1 = QuerySnapshot.fromState state StandbyInfo.NoPool
    snap1.WorkerBaseUrls |> Map.containsKey "s1" |> Expect.isTrue "should have s1"

    let state2 = state |> ManagerState.removeSession "s1"
    let snap2 = QuerySnapshot.fromState state2 StandbyInfo.NoPool
    snap2.WorkerBaseUrls |> Map.containsKey "s1" |> Expect.isFalse "should not have s1 after removal"
  }

  test "QuerySnapshot.empty has empty WorkerBaseUrls" {
    QuerySnapshot.empty.WorkerBaseUrls |> Map.isEmpty |> Expect.isTrue "should be empty"
  }
]

// ── Snapshot-based dashboard helper tests ────────────────────

let snapshotDashboardTests = testList "Snapshot dashboard helpers" [

  test "getSessionState from snapshot returns correct state" {
    let s1 = mkManagedSession "s1" SessionStatus.Ready
    let state = ManagerState.empty |> ManagerState.addSession "s1" s1
    let snap = QuerySnapshot.fromState state StandbyInfo.NoPool
    let info = QuerySnapshot.tryGetSession "s1" snap
    let sessionState =
      info
      |> Option.map (fun i -> SessionStatus.toSessionState i.Status)
      |> Option.defaultValue SessionState.Uninitialized
    sessionState |> Expect.equal "should be Ready" SessionState.Ready
  }

  test "getSessionWorkingDir from snapshot returns correct dir" {
    let s1 = { mkManagedSession "s1" SessionStatus.Ready with
                 Info = { (mkSessionInfo "s1" SessionStatus.Ready) with WorkingDirectory = "/my/project" } }
    let state = ManagerState.empty |> ManagerState.addSession "s1" s1
    let snap = QuerySnapshot.fromState state StandbyInfo.NoPool
    let dir =
      QuerySnapshot.tryGetSession "s1" snap
      |> Option.map (fun i -> i.WorkingDirectory)
      |> Option.defaultValue ""
    dir |> Expect.equal "should be /my/project" "/my/project"
  }

  test "getStatusMsg from snapshot returns warmup progress" {
    let state = {
      ManagerState.empty with
        WarmupProgress = Map.ofList [ "s1", "2/4 Loading assemblies" ]
    }
    let snap = QuerySnapshot.fromManagerState state
    let msg = snap.WarmupProgress |> Map.tryFind "s1"
    msg |> Expect.equal "should have warmup msg" (Some "2/4 Loading assemblies")
  }

  test "snapshot read is non-blocking (performance)" {
    let s1 = mkManagedSession "s1" SessionStatus.Ready
    let state = ManagerState.empty |> ManagerState.addSession "s1" s1
    let snap = QuerySnapshot.fromState state StandbyInfo.NoPool
    let sw = Stopwatch.StartNew()
    for _ in 1..1000 do
      QuerySnapshot.tryGetSession "s1" snap |> ignore
    sw.Stop()
    (sw.ElapsedMilliseconds, 50L)
    |> Expect.isLessThan "1000 snapshot reads < 50ms — CQRS scalability (see SessionManager.fs header)"
  }
]

// ── Combined test list ───────────────────────────────────────

[<Tests>]
let allCqrsTests = testList "SessionManager CQRS" [
  cqrsPatternTests
  querySnapshotTests
  warmupProgressTests
  workerBaseUrlTests
  snapshotDashboardTests
]
