module SageFs.Tests.SessionManagerRestartTombstoneTests

open System
open System.Diagnostics
open System.Threading
open Expecto
open Expecto.Flip
open SageFs
open SageFs.SessionManager
open SageFs.WorkerProtocol

let private connectedStatus = {
  Status = SessionStatus.Faulted
  StatusMessage = None
  EvalCount = 0
  AvgDurationMs = 0L
  MinDurationMs = 0L
  MaxDurationMs = 0L
}

let private connectedProxy : SessionProxy =
  fun msg -> async {
    match msg with
    | WorkerMessage.GetStatus rid ->
      return WorkerResponse.StatusResult(rid, connectedStatus)
    | WorkerMessage.GetTestDiscovery _ ->
      return WorkerResponse.InitialTestDiscovery([||], [])
    | WorkerMessage.GetInstrumentationMaps rid ->
      return WorkerResponse.InstrumentationMapsResult(rid, [||])
    | WorkerMessage.Shutdown ->
      return WorkerResponse.WorkerShuttingDown
    | _ ->
      return WorkerResponse.WorkerReady
  }

type private Harness = {
  Mailbox: MailboxProcessor<SessionCommand>
  ReadSnapshot: unit -> QuerySnapshot
  FaultedEvents: ResizeArray<SessionId * string>
  Cancellation: CancellationTokenSource
}

let private waitUntil message condition =
  SpinWait.SpinUntil(Func<bool>(condition), 1000)
  |> Expect.isTrue message

let private pendingProxyLooksPending (proxy: SessionProxy) =
  match proxy (WorkerMessage.GetStatus "pending") |> Async.RunSynchronously with
  | WorkerResponse.WorkerError (SageFsError.WorkerSpawnFailed _) -> true
  | _ -> false

let private mkRuntime
  (runBuild: int -> Result<string, string>)
  (startWorker: int -> Result<Process, SageFsError>) =
  let mutable buildCalls = 0
  let mutable startCalls = 0

  {
    StartWorkerProcess =
      fun _ _ _ _ _ ->
        startCalls <- startCalls + 1
        startWorker startCalls
    AwaitWorkerPort = fun _ _ _ _ -> ()
    AwaitStandbyPort = fun _ _ _ _ -> ()
    StopWorker = fun _ -> async { return () }
    StopStandbyWorker = fun _ -> async { return () }
    RunBuildAsync =
      fun _ _ -> async {
        buildCalls <- buildCalls + 1
        return runBuild buildCalls
      }
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
    SessionCommand.CreateSession(["Test.fsproj"], @"C:\Test", true, reply)) with
  | Ok info -> info
  | Error err -> failtestf "create session failed: %s" (SageFsError.describe err)

let private getManagedSession (harness: Harness) sessionId =
  match harness.Mailbox.PostAndReply(fun reply -> SessionCommand.GetSession(sessionId, reply)) with
  | Some session -> session
  | None -> failtestf "expected session %s to exist" (SessionId.value sessionId)

let private markWorkerConnected (harness: Harness) (info: SessionInfo) =
  let workerPid =
    info.WorkerPid
    |> Option.defaultWith (fun () -> failtest "expected worker pid")

  harness.Mailbox.Post(SessionCommand.WorkerReady(info.Id, workerPid, "http://worker", connectedProxy))

  waitUntil "worker should appear connected in the snapshot" (fun () ->
    harness.ReadSnapshot().WorkerBaseUrls |> Map.containsKey info.Id)

  workerPid

[<Tests>]
let sessionManagerRestartTombstoneTests =
  testList "SessionManager restart tombstones" [
    testCase "cold restart build failure keeps a faulted tombstone session" <| fun _ ->
      let runtime =
        mkRuntime
          (fun _ -> Error "build boom")
          (fun _ -> Ok(Process.GetCurrentProcess()))

      withHarness runtime <| fun harness ->
        let info = createSession harness
        markWorkerConnected harness info |> ignore

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

      withHarness runtime <| fun harness ->
        let info = createSession harness
        markWorkerConnected harness info |> ignore

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

    testCase "late WorkerExited from old worker does not erase tombstone" <| fun _ ->
      let runtime =
        mkRuntime
          (fun _ -> Error "build boom")
          (fun _ -> Ok(Process.GetCurrentProcess()))

      withHarness runtime <| fun harness ->
        let info = createSession harness
        let originalPid = markWorkerConnected harness info

        harness.Mailbox.PostAndReply(fun reply -> SessionCommand.RestartSession(info.Id, true, reply))
        |> ignore

        harness.Mailbox.Post(SessionCommand.WorkerExited(info.Id, originalPid, 1))

        let snapshot = harness.ReadSnapshot()
        QuerySnapshot.tryGetSession info.Id snapshot
        |> Expect.isSome "late exit should not remove the tombstone"
        (QuerySnapshot.tryGetSession info.Id snapshot |> Option.get).Status
        |> Expect.equal "late exit leaves the session faulted" SessionStatus.Faulted

    testCase "second restart after tombstone can recover the session" <| fun _ ->
      let runtime =
        mkRuntime
          (fun call ->
            match call with
            | 1 -> Error "build boom"
            | _ -> Ok "build ok")
          (fun _ -> Ok(Process.GetCurrentProcess()))

      withHarness runtime <| fun harness ->
        let info = createSession harness
        markWorkerConnected harness info |> ignore

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
  ]
