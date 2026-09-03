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
          MaxDurationMs = 0L
        }
        return WorkerResponse.StatusResult(rid, snap)
      | WorkerMessage.GetTestDiscovery rid ->
        return WorkerResponse.InitialTestDiscovery([||], [])
      | _ ->
        return WorkerResponse.WorkerError (SageFsError.WorkerSpawnFailed "unexpected message")
    }

let private mkRuntime
  (runBuild: int -> Result<string, string>)
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
      (fun _ _ _ -> ())
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
  session.Info.WorkerPid
  |> Option.defaultWith (fun () -> failtest "expected worker pid")

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
        |> Expect.equal
          "invalid worker ready transport should fault the session instead of installing a broken proxy"
          SessionStatus.Faulted
        session.Info.WorkerPid
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
        |> Expect.equal "session should transition through Restarting under spawn-first" SessionStatus.Restarting
        runtime.GetStartCalls()
        |> Expect.equal "spawn-first restart spawns one replacement worker" 2

    testCase "cold restart build failure keeps a faulted tombstone session" <| fun _ ->
      let runtime =
        mkRuntime
          (fun _ -> Error "build boom")
          (fun _ -> Ok(Process.GetCurrentProcess()))

      withHarness runtime.Runtime <| fun harness ->
        let info = createSession harness

        match harness.Mailbox.PostAndReply(fun reply -> SessionCommand.RestartSession(info.Id, true, reply)) with
        | Error (SageFsError.HardResetFailed reason) ->
          reason |> Expect.equal "build failure should surface the build error" "build boom"
        | other ->
          failtestf "expected hard reset build failure, got %A" other

        let session = getManagedSession harness info.Id
        session.Info.Status |> Expect.equal "session should stay present as faulted" SessionStatus.Faulted
        session.Info.WorkerPid |> Expect.equal "faulted tombstone clears worker pid" None
        session.WorkerBaseUrl |> Expect.equal "faulted tombstone clears base url" ""
        pendingProxyLooksPending session.Proxy |> Expect.isTrue "faulted tombstone should reject worker calls with the pending proxy"

        let snapshot = harness.ReadSnapshot()
        QuerySnapshot.tryGetSession info.Id snapshot
        |> Expect.isSome "CQRS snapshot should still contain the faulted session"
        (QuerySnapshot.tryGetSession info.Id snapshot |> Option.get).Status
        |> Expect.equal "snapshot reports the faulted state" SessionStatus.Faulted

        harness.FaultedEvents |> Seq.toList
        |> Expect.equal "fault callback fires once with the build error"
          [ info.Id, "build boom" ]

    testCase "cold restart spawn failure keeps a faulted tombstone session" <| fun _ ->
      let runtime =
        mkRuntime
          (fun _ -> Ok "build ok")
          (fun call ->
            match call with
            | 1 -> Ok(Process.GetCurrentProcess())
            | _ -> Error(SageFsError.WorkerSpawnFailed "spawn boom"))

      withHarness runtime.Runtime <| fun harness ->
        let info = createSession harness

        match harness.Mailbox.PostAndReply(fun reply -> SessionCommand.RestartSession(info.Id, true, reply)) with
        | Error (SageFsError.WorkerSpawnFailed reason) ->
          reason |> Expect.equal "spawn failure should bubble through" "spawn boom"
        | other ->
          failtestf "expected worker spawn failure, got %A" other

        let session = getManagedSession harness info.Id
        session.Info.Status |> Expect.equal "session should become faulted after spawn failure" SessionStatus.Faulted
        session.Info.WorkerPid |> Expect.equal "faulted tombstone clears worker pid" None
        session.WorkerBaseUrl |> Expect.equal "faulted tombstone clears base url" ""
        pendingProxyLooksPending session.Proxy |> Expect.isTrue "spawn failure should also leave the pending proxy installed"

        harness.FaultedEvents |> Seq.length
        |> Expect.equal "fault callback fires once" 1
        harness.FaultedEvents[0] |> snd
        |> Expect.stringContains "fault message should include the spawn reason" "spawn boom"

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
          info.WorkerPid
          |> Option.defaultWith (fun () -> failtest "expected worker pid")

        // Cold restart registers the replacement worker (new pid).
        harness.Mailbox.PostAndReply(fun reply -> SessionCommand.RestartSession(info.Id, true, reply))
        |> ignore

        let afterRestart = getManagedSession harness info.Id
        let replacementPid =
          afterRestart.Info.WorkerPid
          |> Option.defaultWith (fun () -> failtest "expected replacement worker pid")
        replacementPid
        |> Expect.notEqual "replacement should have a distinct pid from the original" originalPid

        // Late spawn-failure from the OLD worker arrives after the restart.
        harness.Mailbox.Post(SessionCommand.WorkerSpawnFailed(info.Id, originalPid, "old worker died on EOF"))
        harness.Mailbox.PostAndReply(fun reply -> SessionCommand.GetSession(info.Id, reply))
        |> ignore

        let session = getManagedSession harness info.Id
        session.Info.Status
        |> Expect.equal "stale spawn failure must not tombstone the fresh worker" SessionStatus.Starting
        session.Info.WorkerPid
        |> Expect.equal "fresh worker pid survives the stale spawn failure" (Some replacementPid)
        harness.FaultedEvents |> Seq.length
        |> Expect.equal "stale spawn failure should not fire a fault callback" 0
      let runtime =
        mkRuntime
          (fun _ -> Error "build boom")
          (fun _ -> Ok(Process.GetCurrentProcess()))

      withHarness runtime.Runtime <| fun harness ->
        let info = createSession harness
        let originalPid =
          info.WorkerPid
          |> Option.defaultWith (fun () -> failtest "expected worker pid")

        harness.Mailbox.PostAndReply(fun reply -> SessionCommand.RestartSession(info.Id, true, reply))
        |> ignore

        harness.Mailbox.Post(SessionCommand.WorkerExited(info.Id, originalPid, 1))
        harness.Mailbox.PostAndReply(fun reply -> SessionCommand.GetSession(info.Id, reply))
        |> ignore

        let snapshot = harness.ReadSnapshot()
        QuerySnapshot.tryGetSession info.Id snapshot
        |> Expect.isSome "late exit should not remove the tombstone"
        (QuerySnapshot.tryGetSession info.Id snapshot |> Option.get).Status
        |> Expect.equal "late exit leaves the session faulted" SessionStatus.Faulted

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
          |> Option.map (fun session -> session.Info.Status)
          |> Expect.equal
            (sprintf "crash %d should move the session into restarting" attempt)
            (Some SessionStatus.Restarting)

          harness.Mailbox.Post(SessionCommand.ScheduleRestart info.Id)

          let restarted = getManagedSession harness info.Id
          restarted.Info.Status
          |> Expect.equal
            (sprintf "manual restart %d should register the replacement worker" attempt)
            SessionStatus.Starting

        let finalWorkerPid =
          getManagedSession harness info.Id
          |> getWorkerPid

        harness.Mailbox.Post(SessionCommand.WorkerExited(info.Id, finalWorkerPid, 1))
        harness.Mailbox.PostAndReply(fun reply -> SessionCommand.GetSession(info.Id, reply))
        |> ignore

        let session = getManagedSession harness info.Id
        session.Info.Status |> Expect.equal "abandoned worker exit should leave a faulted tombstone" SessionStatus.Faulted
        session.Info.WorkerPid |> Expect.equal "faulted tombstone clears worker pid after abandoned exit" None
        session.WorkerBaseUrl |> Expect.equal "faulted tombstone clears base url after abandoned exit" ""
        pendingProxyLooksPending session.Proxy |> Expect.isTrue "abandoned exit should leave the pending proxy installed"

        let snapshot = harness.ReadSnapshot()
        QuerySnapshot.tryGetSession info.Id snapshot
        |> Expect.isSome "abandoned exit should keep the session in the CQRS snapshot"
        (QuerySnapshot.tryGetSession info.Id snapshot |> Option.get).Status
        |> Expect.equal "snapshot reports the faulted tombstone after abandonment" SessionStatus.Faulted

        harness.FaultedEvents |> Seq.length
        |> Expect.equal "abandoned exit should fire one fault callback" 1
        harness.FaultedEvents[0] |> snd
        |> Expect.stringContains "fault message should describe the abandoned worker exit" "abandoned after max retries"

    testCase "second restart after tombstone can recover the session" <| fun _ ->
      let runtime =
        mkRuntime
          (fun call ->
            match call with
            | 1 -> Error "build boom"
            | _ -> Ok "build ok")
          (fun _ -> Ok(Process.GetCurrentProcess()))

      withHarness runtime.Runtime <| fun harness ->
        let info = createSession harness

        harness.Mailbox.PostAndReply(fun reply -> SessionCommand.RestartSession(info.Id, true, reply))
        |> ignore

        match harness.Mailbox.PostAndReply(fun reply -> SessionCommand.RestartSession(info.Id, true, reply)) with
        | Ok message ->
          message
          |> Expect.stringContains "successful retry should acknowledge respawn"
            "Hard reset complete"
        | Error err ->
          failtestf "expected retry to succeed, got %s" (SageFsError.describe err)

        let session = getManagedSession harness info.Id
        session.Info.Status
        |> Expect.equal "successful retry leaves the session starting again" SessionStatus.Starting
        session.Info.WorkerPid
        |> Expect.isSome "successful retry should register a new worker pid"
        pendingProxyLooksPending session.Proxy
        |> Expect.isTrue "successful retry should go back through the startup proxy until worker ready"

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
          info.WorkerPid
          |> Option.defaultWith (fun () -> failtest "expected worker pid")

        harness.Mailbox.Post(SessionCommand.WorkerExited(info.Id, originalPid, 1))
        harness.Mailbox.PostAndReply(fun reply -> SessionCommand.GetSession(info.Id, reply))
        |> Option.map (fun session -> session.Info.Status)
        |> Expect.equal "worker exit should move the session into restarting state" (Some SessionStatus.Restarting)

        harness.Mailbox.Post(SessionCommand.ScheduleRestart info.Id)

        let session = getManagedSession harness info.Id
        session.Info.Status |> Expect.equal "failed scheduled restart keeps the session registered" SessionStatus.Restarting
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
        |> Option.map (fun session -> session.Info.Status)
        |> Expect.equal "worker exit should move the session into restarting state" (Some SessionStatus.Restarting)

        // Rapid crashes are STARTUP crashes: the circuit breaker faults at
        // the startup ceiling (3), not MaxRestarts (5).
        let startupCeiling = RestartPolicy.defaultPolicy.StartupCrashMaxRestarts

        for attempt in 1 .. (startupCeiling - 1) do
          harness.Mailbox.Post(SessionCommand.ScheduleRestart info.Id)

          let session = getManagedSession harness info.Id
          session.Info.Status
          |> Expect.equal
            (sprintf "spawn failure %d should keep the session restarting until retries are exhausted" attempt)
            SessionStatus.Restarting

        harness.Mailbox.Post(SessionCommand.ScheduleRestart info.Id)
        harness.Mailbox.PostAndReply(fun reply -> SessionCommand.GetSession(info.Id, reply))
        |> ignore

        let session = getManagedSession harness info.Id
        session.Info.Status |> Expect.equal "abandoned crash recovery should leave a faulted tombstone" SessionStatus.Faulted
        session.Info.WorkerPid |> Expect.equal "faulted tombstone clears worker pid after abandoned crash recovery" None
        session.WorkerBaseUrl |> Expect.equal "faulted tombstone clears base url after abandoned crash recovery" ""
        pendingProxyLooksPending session.Proxy |> Expect.isTrue "abandoned crash recovery should leave the pending proxy installed"

        let snapshot = harness.ReadSnapshot()
        QuerySnapshot.tryGetSession info.Id snapshot
        |> Expect.isSome "abandoned crash recovery should keep the session in the CQRS snapshot"
        (QuerySnapshot.tryGetSession info.Id snapshot |> Option.get).Status
        |> Expect.equal "snapshot reports the faulted tombstone after abandoned crash recovery" SessionStatus.Faulted

        harness.FaultedEvents |> Seq.length
        |> Expect.equal "abandoned crash recovery should fire one fault callback" 1
        harness.FaultedEvents[0] |> snd
        |> Expect.stringContains "fault message should describe the scheduled restart failure" "scheduled restart boom"
  ]
