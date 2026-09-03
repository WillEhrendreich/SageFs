module SageFs.Tests.SessionManagerSpawnFirstRestartTests

open System.Diagnostics
open System.Threading
open Expecto
open Expecto.Flip
open SageFs
open SageFs.SessionManager
open SageFs.WorkerProtocol

/// Shared verb log so ordering tests can assert spawn-before-stop.
type private Verb =
  | Start
  | Stop

type private Harness = {
  Mailbox: MailboxProcessor<SessionCommand>
  ReadSnapshot: unit -> QuerySnapshot
  FaultedEvents: ResizeArray<SessionId * string>
  Cancellation: CancellationTokenSource
}

type private RuntimeHarness = {
  Runtime: SessionManagerRuntime
  Verbs: ResizeArray<Verb>
  GetBuildCalls: unit -> int
  GetStartCalls: unit -> int
}

let private mkRuntime
  (runBuild: int -> Result<string, string>)
  (startWorker: int -> Result<Process, SageFsError>) =
  let mutable buildCalls = 0
  let mutable startCalls = 0
  let verbs = ResizeArray<Verb>()

  {
    Runtime =
      {
        StartWorkerProcess =
          fun _ _ _ _ _ _ ->
            startCalls <- startCalls + 1
            verbs.Add Verb.Start
            startWorker startCalls
        AwaitWorkerPort = fun _ _ _ _ -> ()
        StopWorker =
          fun _ ->
            verbs.Add Verb.Stop
            async { return () }
        RunBuildAsync =
          fun _ _ -> async {
            buildCalls <- buildCalls + 1
            return runBuild buildCalls
          }
      }
    Verbs = verbs
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

/// A proxy that reports Ready on status probe — simulates a live worker.
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

/// Drive a session to Ready by posting WorkerReady with a valid transport.
let private makeSessionReady (harness: Harness) (info: SessionInfo) =
  let session = getManagedSession harness info.Id
  let pid = getWorkerPid session
  harness.Mailbox.Post(
    SessionCommand.WorkerReady(
      info.Id,
      pid,
      "http://localhost:4123",
      readyProxy))
  // Mirror what the worker ready-poll does in production: flip the session to
  // Ready after the valid transport is installed.
  harness.Mailbox.PostAndReply(fun reply -> SessionCommand.GetSession(info.Id, reply))
  |> ignore
  harness.Mailbox.Post(SessionCommand.UpdateSessionStatus(info.Id, SessionStatus.Ready))
  harness.Mailbox.PostAndReply(fun reply -> SessionCommand.GetSession(info.Id, reply))
  |> ignore

[<Tests>]
let sessionManagerSpawnFirstRestartTests =
  testList "SessionManager spawn-first restart" [
    testCase "T1 — a session that becomes ready spawns exactly one worker (no standby)" <| fun _ ->
      let runtime =
        mkRuntime
          (fun _ -> Ok "build ok")
          (fun _ -> Ok(Process.GetCurrentProcess()))

      withHarness runtime.Runtime <| fun harness ->
        let info = createSession harness
        makeSessionReady harness info

        runtime.GetStartCalls()
        |> Expect.equal "becoming ready must not spawn a standby worker" 1

        let session = getManagedSession harness info.Id
        session.Info.Status |> Expect.equal "session stays ready" SessionStatus.Ready

    testCase "T2 — non-rebuild hard reset spawns the replacement before stopping the old worker" <| fun _ ->
      let runtime =
        mkRuntime
          (fun _ -> Ok "build ok")
          (fun _ -> Ok(Process.GetCurrentProcess()))

      withHarness runtime.Runtime <| fun harness ->
        let info = createSession harness
        makeSessionReady harness info

        match harness.Mailbox.PostAndReply(fun reply -> SessionCommand.RestartSession(info.Id, false, reply)) with
        | Ok _ -> ()
        | Error err -> failtestf "restart failed: %s" (SageFsError.describe err)

        // At accept-time the replacement has been spawned but the old worker is
        // still serving (no Stop yet — the stop happens when the new worker
        // reports Ready, see T7). The order property P1 is: the Start for the
        // replacement precedes any Stop of the old worker.
        runtime.Verbs |> Seq.toList
        |> Expect.equal "restart must spawn the replacement without stopping the old worker yet" [ Verb.Start; Verb.Start ]
        runtime.GetStartCalls()
        |> Expect.equal "restart must spawn exactly one replacement" 2
        // The session is registered as Restarting through the swap (registry
        // continuity — never a missing-session window).
        let session = getManagedSession harness info.Id
        session.Info.Status
        |> Expect.equal "session stays registered as Restarting during the swap" SessionStatus.Restarting

    testCase "T3 — non-rebuild hard reset with a spawn failure leaves the session Ready and serving" <| fun _ ->
      let runtime =
        mkRuntime
          (fun _ -> Ok "build ok")
          (fun call ->
            match call with
            | 1 -> Ok(Process.GetCurrentProcess())
            | _ -> Error(SageFsError.WorkerSpawnFailed "spawn boom"))

      withHarness runtime.Runtime <| fun harness ->
        let info = createSession harness
        makeSessionReady harness info
        let originalPid =
          getManagedSession harness info.Id
          |> getWorkerPid

        match harness.Mailbox.PostAndReply(fun reply -> SessionCommand.RestartSession(info.Id, false, reply)) with
        | Error (SageFsError.WorkerSpawnFailed reason) ->
          reason |> Expect.equal "spawn failure should bubble through" "spawn boom"
        | other ->
          failtestf "expected spawn failure, got %A" other

        let session = getManagedSession harness info.Id
        session.Info.Status
        |> Expect.equal "spawn failure must leave the session Ready (old worker still serving)" SessionStatus.Ready
        session.Info.WorkerPid
        |> Expect.equal "spawn failure must not change the worker pid" (Some originalPid)

        // The failed spawn attempt did call StartWorkerProcess, but the old
        // worker was never stopped — the session is untouched and serving.
        runtime.Verbs |> Seq.toList
        |> Expect.equal "spawn failure must not stop the old worker" [ Verb.Start; Verb.Start ]

    testCase "T4 — rebuild hard reset keeps stop-then-spawn order" <| fun _ ->
      let runtime =
        mkRuntime
          (fun _ -> Ok "build ok")
          (fun _ -> Ok(Process.GetCurrentProcess()))

      withHarness runtime.Runtime <| fun harness ->
        let info = createSession harness
        makeSessionReady harness info

        match harness.Mailbox.PostAndReply(fun reply -> SessionCommand.RestartSession(info.Id, true, reply)) with
        | Ok _ -> ()
        | Error err -> failtestf "rebuild restart failed: %s" (SageFsError.describe err)

        runtime.Verbs |> Seq.toList
        |> Expect.equal "rebuild restart must stop the old worker before spawning" [ Verb.Start; Verb.Stop; Verb.Start ]
        runtime.GetStartCalls()
        |> Expect.equal "rebuild restart spawns one replacement" 2
        runtime.GetBuildCalls()
        |> Expect.equal "rebuild restart runs one build" 1

    testCase "T5 — the retired worker's exit during a swap is ignored" <| fun _ ->
      let runtime =
        mkRuntime
          (fun _ -> Ok "build ok")
          (fun _ -> Ok(Process.GetCurrentProcess()))

      withHarness runtime.Runtime <| fun harness ->
        let info = createSession harness
        makeSessionReady harness info
        let oldPid =
          getManagedSession harness info.Id
          |> getWorkerPid

        // Accept a non-rebuild restart: the new worker is spawning (Start
        // recorded), the old worker still registered.
        match harness.Mailbox.PostAndReply(fun reply -> SessionCommand.RestartSession(info.Id, false, reply)) with
        | Ok _ -> ()
        | Error err -> failtestf "restart failed: %s" (SageFsError.describe err)

        // The old worker exits mid-swap — its exit must NOT be treated as a
        // real exit (no crash-recovery restart, no removal).
        harness.Mailbox.Post(SessionCommand.WorkerExited(info.Id, oldPid, 0))
        harness.Mailbox.PostAndReply(fun reply -> SessionCommand.GetSession(info.Id, reply))
        |> ignore

        let session = getManagedSession harness info.Id
        session.Info.Status
        |> Expect.notEqual "retired worker exit must not tombstone or remove the session" SessionStatus.Faulted

        // The session should still be present and eventually complete when the
        // new worker reports ready.
        harness.Mailbox.PostAndReply(fun reply -> SessionCommand.GetSession(info.Id, reply))
        |> Option.isSome
        |> Expect.isTrue "session must stay registered through the swap"

    testCase "T6 — new-worker spawn failure during a swap reverts to the old worker" <| fun _ ->
      let distinctProcesses =
        Process.GetProcesses()
        |> Array.filter (fun p -> p.Id <> Process.GetCurrentProcess().Id && p.Id > 0)
      if distinctProcesses.Length = 0 then
        skiptest "need a second live process to simulate distinct worker pids"
      let otherProcess = distinctProcesses[0]

      let runtime =
        mkRuntime
          (fun _ -> Ok "build ok")
          (fun call ->
            match call with
            | 1 -> Ok(Process.GetCurrentProcess())
            | _ -> Ok(otherProcess))

      withHarness runtime.Runtime <| fun harness ->
        let info = createSession harness
        makeSessionReady harness info
        let oldPid =
          getManagedSession harness info.Id
          |> getWorkerPid

        // Accept the spawn-first restart (new worker warming).
        match harness.Mailbox.PostAndReply(fun reply -> SessionCommand.RestartSession(info.Id, false, reply)) with
        | Ok _ -> ()
        | Error err -> failtestf "restart failed: %s" (SageFsError.describe err)

        // The NEW worker fails to come up (its pid differs from the registered
        // old pid). The swap must revert: old session restored, still Ready.
        let newPid = otherProcess.Id
        harness.Mailbox.Post(SessionCommand.WorkerSpawnFailed(info.Id, newPid, "replacement failed"))
        harness.Mailbox.PostAndReply(fun reply -> SessionCommand.GetSession(info.Id, reply))
        |> ignore

        let session = getManagedSession harness info.Id
        session.Info.Status
        |> Expect.equal "spawn failure during swap must revert to the old Ready worker" SessionStatus.Ready
        session.Info.WorkerPid
        |> Expect.equal "revert must restore the old worker pid" (Some oldPid)
        harness.FaultedEvents |> Seq.length
        |> Expect.equal "revert must not fire a fault callback" 0

    testCase "T7 — the new worker's ready commits the swap and retires the old worker" <| fun _ ->
      let distinctProcesses =
        Process.GetProcesses()
        |> Array.filter (fun p -> p.Id <> Process.GetCurrentProcess().Id && p.Id > 0)
      if distinctProcesses.Length = 0 then
        skiptest "need a second live process to simulate distinct worker pids"
      let otherProcess = distinctProcesses[0]

      let runtime =
        mkRuntime
          (fun _ -> Ok "build ok")
          (fun call ->
            match call with
            | 1 -> Ok(Process.GetCurrentProcess())
            | _ -> Ok(otherProcess))

      withHarness runtime.Runtime <| fun harness ->
        let info = createSession harness
        makeSessionReady harness info

        // Accept the spawn-first restart.
        match harness.Mailbox.PostAndReply(fun reply -> SessionCommand.RestartSession(info.Id, false, reply)) with
        | Ok _ -> ()
        | Error err -> failtestf "restart failed: %s" (SageFsError.describe err)

        // New worker reports ready → the swap commits: pid updated, old worker
        // retired (Stop verb), session transport installed.
        harness.Mailbox.Post(
          SessionCommand.WorkerReady(
            info.Id,
            otherProcess.Id,
            "http://localhost:4124",
            readyProxy))
        harness.Mailbox.PostAndReply(fun reply -> SessionCommand.GetSession(info.Id, reply))
        |> ignore

        let session = getManagedSession harness info.Id
        session.Info.WorkerPid
        |> Expect.equal "ready must commit the new worker pid" (Some otherProcess.Id)

        runtime.Verbs |> Seq.toList
        |> Expect.equal "ready must retire the old worker (one stop after the two starts)" [ Verb.Start; Verb.Start; Verb.Stop ]
  ]
