module SageFs.Tests.SessionManagerRestartTombstoneTests

open System.Diagnostics
open System.Threading
open Expecto
open Expecto.Flip
open SageFs
open SageFs.SessionManager
open SageFs.WorkerProtocol

type private Harness = {
  Mailbox: MailboxProcessor<SessionCommand>
  ReadSnapshot: unit -> QuerySnapshot
  FaultedEvents: ResizeArray<SessionId * string>
  Cancellation: CancellationTokenSource
}

type private RuntimeHarness = {
  Runtime: SessionManagerRuntime
  GetBuildCalls: unit -> int
  GetStartCalls: unit -> int
}

let private pendingProxyLooksPending (proxy: SessionProxy) =
  match proxy (WorkerMessage.GetStatus "pending") |> Async.RunSynchronously with
  | WorkerResponse.WorkerError (SageFsError.WorkerSpawnFailed _) -> true
  | _ -> false

/// A proxy that reports a Ready status snapshot — simulates a live worker.
let private readyProxy =
  fun (msg: WorkerMessage) ->
    async {
      match msg with
      | WorkerMessage.GetStatus rid ->
        let snap : WorkerStatusSnapshot = {
          Status = SessionStatus.Ready
          StatusMessage = None
          EvalCount = 0
          AvgDurationMs = 0L
          MinDurationMs = 0L
          MaxDurationMs = 0L; Projects = []
        }
        return WorkerResponse.StatusResult(rid, snap)
      | WorkerMessage.GetTestDiscovery rid ->
        return WorkerResponse.InitialTestDiscovery([||], [])
      | _ ->
        return WorkerResponse.WorkerError (SageFsError.WorkerSpawnFailed "unexpected message")
    }

let private mkRuntime
  (runBuild: int -> Result<string, SageFsError>)
  (startWorker: int -> Result<Process, SageFsError>) =
  let mutable buildCalls = 0
  let mutable startCalls = 0

  {
    Runtime =
      {
        StartWorkerProcess =
          fun _ _ _ _ _ _ ->
            startCalls <- startCalls + 1
            startWorker startCalls
        AwaitWorkerPort = fun _ _ _ _ -> ()
        StopWorker = fun _ -> async { return () }
        RunBuildAsync =
          fun _ _ -> async {
            buildCalls <- buildCalls + 1
            return runBuild buildCalls
          }
      }
    GetBuildCalls = fun () -> buildCalls
    GetStartCalls = fun () -> startCalls
  }

let private withHarness runtime run =
  use cancellation = new CancellationTokenSource()
  let faultedEvents = ResizeArray<SessionId * string>()
  let mailbox, readSnapshot =
    createWith
      runtime
      cancellation.Token
      ignore
      (fun _ _ -> ())
      (fun _ _ -> ())
      ignore
      (fun _ _ -> ())
      (fun sid msg -> faultedEvents.Add(sid, msg))

  let harness = {
    Mailbox = mailbox
    ReadSnapshot = readSnapshot
    FaultedEvents = faultedEvents
    Cancellation = cancellation
  }

  try
    run harness
  finally
    try
      mailbox.PostAndReply(fun reply -> SessionCommand.StopAll reply)
    with _ ->
      ()
    cancellation.Cancel()

let private createSession (harness: Harness) =
  match harness.Mailbox.PostAndReply(fun reply ->
    SessionCommand.CreateSession(["Test.fsproj"], @"C:\Test", true, WorkflowTypes.SessionWorkflow.Interactive, reply)) with
  | Ok info -> info
  | Error err -> failtestf "create session failed: %s" (SageFsError.describe err)

let private getManagedSession (harness: Harness) sessionId =
  match harness.Mailbox.PostAndReply(fun reply -> SessionCommand.GetSession(sessionId, reply)) with
  | Some session -> session
  | None -> failtestf "expected session %s to exist" (SessionId.value sessionId)

let private getWorkerPid (session: ManagedSession) =
  SessionLifecycleStatus.workerPid session.Info.Status
  |> Option.defaultWith (fun () -> failtest "expected worker pid")

let private isRestarting = function SessionLifecycleStatus.Restarting _ -> true | _ -> false
let private isStarting = function SessionLifecycleStatus.Starting _ -> true | _ -> false
let private isFaulted = function SessionLifecycleStatus.Faulted _ -> true | _ -> false

[<Tests>]
let sessionManagerRestartTombstoneTests =
  testList "SessionManager restart tombstones" [
    testCase "worker ready without a proxy faults the session instead of installing a broken transport" <| fun _ ->
      let runtime =
        mkRuntime
          (fun _ -> Ok "build ok")
          (fun _ -> Ok(Process.GetCurrentProcess()))

      withHarness runtime.Runtime <| fun harness ->
        let info = createSession harness
        let workerPid =
          getManagedSession harness info.Id
          |> getWorkerPid

        harness.Mailbox.Post(
          SessionCommand.WorkerReady(
            info.Id,
            workerPid,
            "http://localhost:4123",
            Unchecked.defaultof<SessionProxy>))

        harness.Mailbox.PostAndReply(fun reply -> SessionCommand.GetSession(info.Id, reply))
        |> ignore

        let session = getManagedSession harness info.Id
        session.Info.Status
        |> isFaulted
        |> Expect.isTrue
          "invalid worker ready transport should fault the session instead of installing a broken proxy"
        SessionLifecycleStatus.workerPid session.Info.Status
        |> Expect.equal "faulted tombstone clears worker pid after invalid worker ready" None
        session.WorkerBaseUrl
        |> Expect.equal "faulted tombstone clears base url after invalid worker ready" ""
        pendingProxyLooksPending session.Proxy
        |> Expect.isTrue "invalid worker ready should leave the pending proxy installed"

        harness.FaultedEvents |> Seq.length
        |> Expect.equal "invalid worker ready should fire one fault callback" 1
        harness.FaultedEvents[0] |> snd
        |> Expect.stringContains "fault message should describe the invalid transport" "valid proxy"

    testCase "restart with a ready worker uses spawn-first (session transitions through Restarting)" <| fun _ ->
      let runtime =
        mkRuntime
          (fun _ -> Ok "build ok")
          (fun _ -> Ok(Process.GetCurrentProcess()))

      withHarness runtime.Runtime <| fun harness ->
        let info = createSession harness
        // Drive the session to Ready with a valid transport (spawn-first
        // requires a live worker: the new worker is spawned before the old
        // one is stopped).
        let session = getManagedSession harness info.Id
        let pid = getWorkerPid session
        harness.Mailbox.Post(SessionCommand.WorkerReady(info.Id, pid, "http://localhost:4123", readyProxy))
        harness.Mailbox.PostAndReply(fun reply -> SessionCommand.GetSession(info.Id, reply))
        |> ignore

        match harness.Mailbox.PostAndReply(fun reply -> SessionCommand.RestartSession(info.Id, false, reply)) with
        | Ok message ->
          message
          |> Expect.stringContains
            "non-rebuild restart should acknowledge the spawn-first respawn"
            "replacement worker spawning"
        | Error err ->
          failtestf "expected spawn-first restart, got %s" (SageFsError.describe err)

        // Spawn-first parks the old worker and marks the session Restarting
        // until the replacement reports Ready.
        let restarting = getManagedSession harness info.Id
        restarting.Info.Status
        |> Expect.equal "session should transition through Restarting under spawn-first, keeping the old worker's pid to guard against its late exit" (SessionLifecycleStatus.Restarting (Some pid))
        runtime.GetStartCalls()
        |> Expect.equal "spawn-first restart spawns one replacement worker" 2

    testCase "WHY — a failed rebuild leaves a live session as it was because the build error is the caller's to show, not a reason to kill the worker" <| fun _ ->
      let runtime =
        mkRuntime
          (fun _ -> Error (SageFsError.BuildFailed(1, [ BuildDiagnostic.ofLine "build boom" ])))
          (fun _ -> Ok(Process.GetCurrentProcess()))

      withHarness runtime.Runtime <| fun harness ->
        let info = createSession harness
        let before = getManagedSession harness info.Id

        match harness.Mailbox.PostAndReply(fun reply -> SessionCommand.RestartSession(info.Id, true, reply)) with
        | Error (SageFsError.BuildFailed(_, diagnostics)) ->
          BuildDiagnostic.describe diagnostics |> Expect.equal "build failure should surface the build error" "build boom"
        | other ->
          failtestf "expected a build failure, got %A" other

        let session = getManagedSession harness info.Id
        session.Info.Status |> Expect.equal "the session keeps its status" before.Info.Status
        SessionLifecycleStatus.workerPid session.Info.Status
        |> Expect.equal "the session keeps its worker" (SessionLifecycleStatus.workerPid before.Info.Status)

        let snapshot = harness.ReadSnapshot()
        (QuerySnapshot.tryGetSession info.Id snapshot |> Option.get).Status
        |> Expect.equal "the snapshot agrees" before.Info.Status

        harness.FaultedEvents |> Seq.length
        |> Expect.equal "a failed build is not a session fault" 0

    testCase "WHY — a replacement that fails to spawn after a good rebuild leaves the session serving, because the old worker was never stopped" <| fun _ ->
      let runtime =
        mkRuntime
          (fun _ -> Ok "build ok")
          (fun call ->
            match call with
            | 1 -> Ok(Process.GetCurrentProcess())
            | _ -> Error(SageFsError.WorkerSpawnFailed "spawn boom"))

      withHarness runtime.Runtime <| fun harness ->
        let info = createSession harness
        let before = getManagedSession harness info.Id

        match harness.Mailbox.PostAndReply(fun reply -> SessionCommand.RestartSession(info.Id, true, reply)) with
        | Error (SageFsError.WorkerSpawnFailed reason) ->
          reason |> Expect.equal "spawn failure should bubble through" "spawn boom"
        | other ->
          failtestf "expected worker spawn failure, got %A" other

        let session = getManagedSession harness info.Id
        session.Info.Status |> Expect.equal "the session keeps its status" before.Info.Status
        SessionLifecycleStatus.workerPid session.Info.Status
        |> Expect.equal "the session keeps its worker" (SessionLifecycleStatus.workerPid before.Info.Status)
        harness.FaultedEvents |> Seq.length
        |> Expect.equal "a failed spawn with a live worker is not a session fault" 0

    testCase "late WorkerSpawnFailed from old worker does not tombstone the replacement" <| fun _ ->
      // The stale-pid race: a hard reset kills the old process while its
      // awaitWorkerPort task is still reading stdout; on EOF it posts
      // WorkerSpawnFailed with the OLD pid. The handler must compare the
      // message pid against the session's current worker pid and ignore the
      // stale failure — otherwise the fresh worker is tombstoned to Faulted.
      let distinctProcesses =
        Process.GetProcesses()
        |> Array.filter (fun p -> p.Id <> Process.GetCurrentProcess().Id && p.Id > 0)
      if distinctProcesses.Length = 0 then
        skiptest "need a second live process to simulate distinct worker pids"
      let oldPidProcess = distinctProcesses[0]

      let runtime =
        mkRuntime
          (fun _ -> Ok "build ok")
          (fun call ->
            match call with
            | 1 -> Ok(Process.GetCurrentProcess())   // original worker
            | _ -> Ok(oldPidProcess))                // replacement worker

      withHarness runtime.Runtime <| fun harness ->
        let info = createSession harness
        let originalPid =
          SessionLifecycleStatus.workerPid info.Status
          |> Option.defaultWith (fun () -> failtest "expected worker pid")

        // Fault the session first so the rebuild takes the cold path — with a
        // live worker it builds first and swaps spawn-first (covered by T5/T8),
        // and the old pid stays current until that swap commits.
        harness.Mailbox.Post(
          SessionCommand.WorkerReady(info.Id, originalPid, "http://localhost:4123", Unchecked.defaultof<SessionProxy>))
        harness.Mailbox.PostAndReply(fun reply -> SessionCommand.GetSession(info.Id, reply))
        |> ignore
        let faultsBefore = harness.FaultedEvents.Count

        // Cold restart registers the replacement worker (new pid).
        harness.Mailbox.PostAndReply(fun reply -> SessionCommand.RestartSession(info.Id, true, reply))
        |> ignore

        let afterRestart = getManagedSession harness info.Id
        let replacementPid =
          SessionLifecycleStatus.workerPid afterRestart.Info.Status
          |> Option.defaultWith (fun () -> failtest "expected replacement worker pid")
        replacementPid
        |> Expect.notEqual "replacement should have a distinct pid from the original" originalPid

        // Late spawn-failure from the OLD worker arrives after the restart.
        harness.Mailbox.Post(SessionCommand.WorkerSpawnFailed(info.Id, originalPid, "old worker died on EOF"))
        harness.Mailbox.PostAndReply(fun reply -> SessionCommand.GetSession(info.Id, reply))
        |> ignore

        let session = getManagedSession harness info.Id
        session.Info.Status
        |> Expect.equal "stale spawn failure must not tombstone the fresh worker" (SessionLifecycleStatus.Starting { Pid = replacementPid; Port = None })
        SessionLifecycleStatus.workerPid session.Info.Status
        |> Expect.equal "fresh worker pid survives the stale spawn failure" (Some replacementPid)
        harness.FaultedEvents.Count
        |> Expect.equal "stale spawn failure should not fire a fault callback" faultsBefore

    testCase "WHY — the worker crashing after a failed rebuild is handled as a crash, because the failed build never touched it" <| fun _ ->
      let runtime =
        mkRuntime
          (fun _ -> Error (SageFsError.BuildFailed(1, [ BuildDiagnostic.ofLine "build boom" ])))
          (fun _ -> Ok(Process.GetCurrentProcess()))

      withHarness runtime.Runtime <| fun harness ->
        let info = createSession harness
        let originalPid =
          SessionLifecycleStatus.workerPid info.Status
          |> Option.defaultWith (fun () -> failtest "expected worker pid")

        harness.Mailbox.PostAndReply(fun reply -> SessionCommand.RestartSession(info.Id, true, reply))
        |> ignore

        harness.Mailbox.Post(SessionCommand.WorkerExited(info.Id, originalPid, 1))
        harness.Mailbox.PostAndReply(fun reply -> SessionCommand.GetSession(info.Id, reply))
        |> ignore

        let snapshot = harness.ReadSnapshot()
        QuerySnapshot.tryGetSession info.Id snapshot
        |> Expect.isSome "the session stays registered"
        (QuerySnapshot.tryGetSession info.Id snapshot |> Option.get).Status
        |> isFaulted
        |> Expect.isFalse "a crash is recovered, not left as a tombstone of the earlier failed build"

    testCase "abandoned worker exit keeps a faulted tombstone session" <| fun _ ->
      let runtime =
        mkRuntime
          (fun _ -> Ok "build ok")
          (fun _ -> Ok(Process.GetCurrentProcess()))

      withHarness runtime.Runtime <| fun harness ->
        let info = createSession harness

        // Rapid (back-to-back) crashes are STARTUP crashes: the circuit
        // breaker faults at the startup ceiling (3), not MaxRestarts (5).
        let startupCeiling = RestartPolicy.defaultPolicy.StartupCrashMaxRestarts

        for attempt in 1 .. startupCeiling do
          let sessionBeforeCrash = getManagedSession harness info.Id
          let workerPid = getWorkerPid sessionBeforeCrash

          harness.Mailbox.Post(SessionCommand.WorkerExited(info.Id, workerPid, 1))

          harness.Mailbox.PostAndReply(fun reply -> SessionCommand.GetSession(info.Id, reply))
          |> Option.map (fun session -> isRestarting session.Info.Status)
          |> Expect.equal
            (sprintf "crash %d should move the session into restarting" attempt)
            (Some true)

          harness.Mailbox.Post(SessionCommand.ScheduleRestart info.Id)

          let restarted = getManagedSession harness info.Id
          restarted.Info.Status
          |> isStarting
          |> Expect.isTrue
            (sprintf "manual restart %d should register the replacement worker" attempt)

        let finalWorkerPid =
          getManagedSession harness info.Id
          |> getWorkerPid

        harness.Mailbox.Post(SessionCommand.WorkerExited(info.Id, finalWorkerPid, 1))
        harness.Mailbox.PostAndReply(fun reply -> SessionCommand.GetSession(info.Id, reply))
        |> ignore

        let session = getManagedSession harness info.Id
        session.Info.Status |> isFaulted |> Expect.isTrue "abandoned worker exit should leave a faulted tombstone"
        SessionLifecycleStatus.workerPid session.Info.Status
        |> Expect.equal "faulted tombstone clears worker pid after abandoned exit" None
        session.WorkerBaseUrl |> Expect.equal "faulted tombstone clears base url after abandoned exit" ""
        pendingProxyLooksPending session.Proxy |> Expect.isTrue "abandoned exit should leave the pending proxy installed"

        let snapshot = harness.ReadSnapshot()
        QuerySnapshot.tryGetSession info.Id snapshot
        |> Expect.isSome "abandoned exit should keep the session in the CQRS snapshot"
        (QuerySnapshot.tryGetSession info.Id snapshot |> Option.get).Status
        |> isFaulted
        |> Expect.isTrue "snapshot reports the faulted tombstone after abandonment"

        harness.FaultedEvents |> Seq.length
        |> Expect.equal "abandoned exit should fire one fault callback" 1
        harness.FaultedEvents[0] |> snd
        |> Expect.stringContains "fault message should describe the abandoned worker exit" "abandoned after max retries"

    testCase "WHY — a rebuild after a failed one swaps the replacement in, because the failed build left the session serving" <| fun _ ->
      let runtime =
        mkRuntime
          (fun call ->
            match call with
            | 1 -> Error (SageFsError.BuildFailed(1, [ BuildDiagnostic.ofLine "build boom" ]))
            | _ -> Ok "build ok")
          (fun _ -> Ok(Process.GetCurrentProcess()))

      withHarness runtime.Runtime <| fun harness ->
        let info = createSession harness

        match harness.Mailbox.PostAndReply(fun reply -> SessionCommand.RestartSession(info.Id, true, reply)) with
        | Error (SageFsError.BuildFailed _) -> ()
        | other -> failtestf "expected the first rebuild to fail, got %A" other

        match harness.Mailbox.PostAndReply(fun reply -> SessionCommand.RestartSession(info.Id, true, reply)) with
        | Ok message ->
          message
          |> Expect.stringContains "successful retry should acknowledge respawn"
            "Hard reset complete"
        | Error err ->
          failtestf "expected retry to succeed, got %s" (SageFsError.describe err)

        let session = getManagedSession harness info.Id
        session.Info.Status
        |> isRestarting
        |> Expect.isTrue "the replacement is swapping in"
        SessionLifecycleStatus.workerPid session.Info.Status
        |> Expect.isSome "the serving worker stays registered until the swap commits"
        pendingProxyLooksPending session.Proxy
        |> Expect.isTrue "calls wait for the replacement to become ready"

    testCase "scheduled crash recovery uses the injected runtime instead of spawning a real worker" <| fun _ ->
      let runtime =
        mkRuntime
          (fun _ -> Ok "build ok")
          (fun call ->
            match call with
            | 1 -> Ok(Process.GetCurrentProcess())
            | _ -> Error(SageFsError.WorkerSpawnFailed "scheduled restart boom"))

      withHarness runtime.Runtime <| fun harness ->
        let info = createSession harness
        let originalPid =
          SessionLifecycleStatus.workerPid info.Status
          |> Option.defaultWith (fun () -> failtest "expected worker pid")

        harness.Mailbox.Post(SessionCommand.WorkerExited(info.Id, originalPid, 1))
        harness.Mailbox.PostAndReply(fun reply -> SessionCommand.GetSession(info.Id, reply))
        |> Option.map (fun session -> isRestarting session.Info.Status)
        |> Expect.equal "worker exit should move the session into restarting state" (Some true)

        harness.Mailbox.Post(SessionCommand.ScheduleRestart info.Id)

        let session = getManagedSession harness info.Id
        session.Info.Status |> isRestarting |> Expect.isTrue "failed scheduled restart keeps the session registered"
        runtime.GetStartCalls() |> Expect.equal "all worker spawn attempts should flow through the injected runtime" 2

    testCase "abandoned crash recovery keeps a faulted tombstone session" <| fun _ ->
      let runtime =
        mkRuntime
          (fun _ -> Ok "build ok")
          (fun call ->
            match call with
            | 1 -> Ok(Process.GetCurrentProcess())
            | _ -> Error(SageFsError.WorkerSpawnFailed "scheduled restart boom"))

      withHarness runtime.Runtime <| fun harness ->
        let info = createSession harness
        let originalPid =
          getManagedSession harness info.Id
          |> getWorkerPid

        harness.Mailbox.Post(SessionCommand.WorkerExited(info.Id, originalPid, 1))

        harness.Mailbox.PostAndReply(fun reply -> SessionCommand.GetSession(info.Id, reply))
        |> Option.map (fun session -> isRestarting session.Info.Status)
        |> Expect.equal "worker exit should move the session into restarting state" (Some true)

        // Rapid crashes are STARTUP crashes: the circuit breaker faults at
        // the startup ceiling (3), not MaxRestarts (5).
        let startupCeiling = RestartPolicy.defaultPolicy.StartupCrashMaxRestarts

        for attempt in 1 .. (startupCeiling - 1) do
          harness.Mailbox.Post(SessionCommand.ScheduleRestart info.Id)

          let session = getManagedSession harness info.Id
          session.Info.Status
          |> isRestarting
          |> Expect.isTrue
            (sprintf "spawn failure %d should keep the session restarting until retries are exhausted" attempt)

        harness.Mailbox.Post(SessionCommand.ScheduleRestart info.Id)
        harness.Mailbox.PostAndReply(fun reply -> SessionCommand.GetSession(info.Id, reply))
        |> ignore

        let session = getManagedSession harness info.Id
        session.Info.Status |> isFaulted |> Expect.isTrue "abandoned crash recovery should leave a faulted tombstone"
        SessionLifecycleStatus.workerPid session.Info.Status
        |> Expect.equal "faulted tombstone clears worker pid after abandoned crash recovery" None
        session.WorkerBaseUrl |> Expect.equal "faulted tombstone clears base url after abandoned crash recovery" ""
        pendingProxyLooksPending session.Proxy |> Expect.isTrue "abandoned crash recovery should leave the pending proxy installed"

        let snapshot = harness.ReadSnapshot()
        QuerySnapshot.tryGetSession info.Id snapshot
        |> Expect.isSome "abandoned crash recovery should keep the session in the CQRS snapshot"
        (QuerySnapshot.tryGetSession info.Id snapshot |> Option.get).Status
        |> isFaulted
        |> Expect.isTrue "snapshot reports the faulted tombstone after abandoned crash recovery"

        harness.FaultedEvents |> Seq.length
        |> Expect.equal "abandoned crash recovery should fire one fault callback" 1
        harness.FaultedEvents[0] |> snd
        |> Expect.stringContains "fault message should describe the scheduled restart failure" "scheduled restart boom"
  ]
