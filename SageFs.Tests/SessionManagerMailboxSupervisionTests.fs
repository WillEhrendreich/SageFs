module SageFs.Tests.SessionManagerMailboxSupervisionTests

open System
open System.Diagnostics
open System.Threading
open System.Threading.Tasks
open Expecto
open Expecto.Flip
open SageFs
open SageFs.SessionManager
open SageFs.WorkerProtocol

// RED→GREEN tests for the SessionManager mailbox supervision work:
//  - a message-handler exception must not kill the mailbox (no orphaned sessions)
//  - the cold-restart `dotnet build` must not block other session operations
//  - the owner must serialise concurrent hard resets while a rebuild is in flight

type private Harness = {
  Mailbox: MailboxProcessor<SessionCommand>
  ReadSnapshot: unit -> QuerySnapshot
  FaultedEvents: ResizeArray<SessionId * string>
  Cancellation: CancellationTokenSource
}

type private Runtime = {
  Runtime: SessionManagerRuntime
  GetStartCalls: unit -> int
}

let private mkRuntime
  (startWorker: int -> Result<Process, SageFsError>)
  (stopWorker: unit -> Async<unit>)
  (runBuild: unit -> Async<Result<string, string>>) =
  let mutable startCalls = 0
  {
    Runtime =
      {
        StartWorkerProcess =
          fun _ _ _ _ _ _ ->
            startCalls <- startCalls + 1
            startWorker startCalls
        AwaitWorkerPort = fun _ _ _ _ -> ()
        StopWorker = fun _ -> stopWorker ()
        RunBuildAsync = fun _ _ -> runBuild ()
      }
    GetStartCalls = fun () -> startCalls
  }

/// PostAndReply bounded by a timeout — returns None when the mailbox is dead or
/// blocked (the failure mode this suite exists to prevent).
let private tryPostAndReply (timeoutMs: int) (mailbox: MailboxProcessor<SessionCommand>) (build: AsyncReplyChannel<'a> -> SessionCommand) : 'a option =
  let task = mailbox.PostAndAsyncReply(build) |> Async.StartAsTask
  match task.Wait(timeoutMs) with
  | true -> Some task.Result
  | false -> None

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
    // Bounded teardown: a mailbox killed by the scenario under test must not
    // hang the suite forever.
    tryPostAndReply 2000 mailbox (fun reply -> SessionCommand.StopAll reply)
    |> ignore
    cancellation.Cancel()

let private createSessionFor (projects: string list) (workingDir: string) (harness: Harness) =
  match harness.Mailbox.PostAndReply(fun reply ->
    SessionCommand.CreateSession(projects, workingDir, true, WorkflowTypes.SessionWorkflow.Interactive, reply)) with
  | Ok info -> info
  | Error err -> failtestf "create session failed: %s" (SageFsError.describe err)

let private createSession (harness: Harness) =
  createSessionFor [ "Test.fsproj" ] @"C:\Test" harness

let private okStart (_call: int) : Result<Process, SageFsError> =
  Ok (Process.GetCurrentProcess())

[<Tests>]
let sessionManagerMailboxSupervisionTests =
  testList "SessionManager mailbox supervision" [

    testCase "unexpected StopWorker exception does not kill the mailbox" <| fun _ ->
      let stopFailure = ref false
      let runtime =
        mkRuntime
          okStart
          (fun () -> async {
            if stopFailure.Value then
              return failwith "stop worker boom"
            else
              return ()
          })
          (fun () -> async { return Ok "build ok" })

      withHarness runtime.Runtime <| fun harness ->
        let info = createSession harness

        // Make the next stop throw inside the mailbox handler.
        stopFailure.Value <- true
        match tryPostAndReply 1500 harness.Mailbox (fun reply -> SessionCommand.StopSession(info.Id, reply)) with
        | Some (Error (SageFsError.SessionStopFailed _)) -> ()
        | Some other -> failtestf "expected fail-closed SessionStopFailed, got %A" other
        | None ->
          failtest "StopSession hung — the handler exception killed the mailbox"

        stopFailure.Value <- false

        // The mailbox must still process commands and the session must not be
        // silently orphaned.
        let sessions = harness.Mailbox.PostAndReply(fun reply -> SessionCommand.ListSessions reply)
        sessions
        |> List.map (fun s -> s.Id)
        |> Expect.contains "session survives a handler exception (no silent orphan)" info.Id

        match harness.Mailbox.PostAndReply(fun reply -> SessionCommand.StopSession(info.Id, reply)) with
        | Ok () -> ()
        | Error err -> failtestf "clean stop after handler exception failed: %s" (SageFsError.describe err)

    testCase "handler exception leaves the CQRS snapshot consistent (fail-closed)" <| fun _ ->
      let stopFailure = ref false
      let runtime =
        mkRuntime
          okStart
          (fun () -> async {
            if stopFailure.Value then
              return failwith "stop worker boom"
            else
              return ()
          })
          (fun () -> async { return Ok "build ok" })

      withHarness runtime.Runtime <| fun harness ->
        let infoA = createSessionFor [ "A.fsproj" ] @"C:\A" harness
        let infoB = createSessionFor [ "B.fsproj" ] @"C:\B" harness

        stopFailure.Value <- true
        match tryPostAndReply 1500 harness.Mailbox (fun reply -> SessionCommand.StopSession(infoA.Id, reply)) with
        | Some (Error (SageFsError.SessionStopFailed _)) -> ()
        | _ -> failtest "expected fail-closed SessionStopFailed on the throwing stop"
        stopFailure.Value <- false

        // Snapshot still lists both sessions — nothing was silently dropped.
        let snap = harness.ReadSnapshot()
        QuerySnapshot.tryGetSession infoA.Id snap |> Expect.isSome "session A still visible after failed stop"
        QuerySnapshot.tryGetSession infoB.Id snap |> Expect.isSome "session B still visible after failed stop"

        // And the unaffected session can still be stopped normally.
        match harness.Mailbox.PostAndReply(fun reply -> SessionCommand.StopSession(infoB.Id, reply)) with
        | Ok () -> ()
        | Error err -> failtestf "clean stop of unaffected session failed: %s" (SageFsError.describe err)
  ]

[<Tests>]
let sessionManagerOffMailboxBuildTests =
  testList "SessionManager off-mailbox cold build" [

    testCase "list_sessions stays responsive while a cold-restart build runs" <| fun _ ->
      let buildStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously)
      let releaseBuild = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously)
      let runtime =
        mkRuntime
          okStart
          (fun () -> async { return () })
          (fun () -> async {
            buildStarted.TrySetResult(true) |> ignore
            let! _ = releaseBuild.Task |> Async.AwaitTask
            return Ok "build ok"
          })

      withHarness runtime.Runtime <| fun harness ->
        let info = createSession harness
        let restartTask =
          harness.Mailbox.PostAndAsyncReply(fun reply -> SessionCommand.RestartSession(info.Id, true, reply))
          |> Async.StartAsTask

        try
          buildStarted.Task.Wait(2000) |> Expect.isTrue "cold build should have started"

          // While the build is in flight, list_sessions must still answer promptly.
          let listTask =
            harness.Mailbox.PostAndAsyncReply(fun reply -> SessionCommand.ListSessions reply)
            |> Async.StartAsTask
          match listTask.Wait(1000) with
          | true ->
            listTask.Result
            |> List.map (fun s -> s.Id)
            |> Expect.contains "session stays registered while the build runs" info.Id
          | false ->
            failtest "list_sessions blocked behind the cold build (mailbox serialized by dotnet build)"

          releaseBuild.TrySetResult(true) |> ignore
          match restartTask.Wait(5000) with
          | true ->
            match restartTask.Result with
            | Ok msg -> msg |> Expect.stringContains "completion should report respawn" "Hard reset complete"
            | Error err -> failtestf "cold restart failed: %s" (SageFsError.describe err)
          | false -> failtest "cold restart did not complete after the build finished"
        finally
          releaseBuild.TrySetResult(true) |> ignore

    testCase "create_session stays responsive while a cold-restart build runs" <| fun _ ->
      let buildStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously)
      let releaseBuild = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously)
      let runtime =
        mkRuntime
          okStart
          (fun () -> async { return () })
          (fun () -> async {
            buildStarted.TrySetResult(true) |> ignore
            let! _ = releaseBuild.Task |> Async.AwaitTask
            return Ok "build ok"
          })

      withHarness runtime.Runtime <| fun harness ->
        let info = createSession harness
        let restartTask =
          harness.Mailbox.PostAndAsyncReply(fun reply -> SessionCommand.RestartSession(info.Id, true, reply))
          |> Async.StartAsTask

        try
          buildStarted.Task.Wait(2000) |> Expect.isTrue "cold build should have started"

          // A brand-new session (different dir) must be creatable during the build.
          match tryPostAndReply 1500 harness.Mailbox (fun reply ->
            SessionCommand.CreateSession([ "C.fsproj" ], @"C:\C", true, WorkflowTypes.SessionWorkflow.Interactive, reply)) with
          | Some (Ok second) -> second.Id |> Expect.notEqual "second session has its own id" info.Id
          | Some (Error err) -> failtestf "create_session during build failed: %s" (SageFsError.describe err)
          | None -> failtest "create_session blocked behind the cold build"

          releaseBuild.TrySetResult(true) |> ignore
          match restartTask.Wait(5000) with
          | true ->
            match restartTask.Result with
            | Ok _ -> ()
            | Error err -> failtestf "cold restart failed: %s" (SageFsError.describe err)
          | false -> failtest "cold restart did not complete after the build finished"
        finally
          releaseBuild.TrySetResult(true) |> ignore

    testCase "second hard reset while a rebuild is in flight is rejected at the owner" <| fun _ ->
      let buildStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously)
      let releaseBuild = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously)
      let runtime =
        mkRuntime
          okStart
          (fun () -> async { return () })
          (fun () -> async {
            buildStarted.TrySetResult(true) |> ignore
            let! _ = releaseBuild.Task |> Async.AwaitTask
            return Ok "build ok"
          })

      withHarness runtime.Runtime <| fun harness ->
        let info = createSession harness
        let firstRestart =
          harness.Mailbox.PostAndAsyncReply(fun reply -> SessionCommand.RestartSession(info.Id, true, reply))
          |> Async.StartAsTask

        try
          buildStarted.Task.Wait(2000) |> Expect.isTrue "first cold build should have started"

          // A concurrent hard reset of the same session must be rejected, not
          // queued behind the build and not double-spawned.
          match tryPostAndReply 1500 harness.Mailbox (fun reply -> SessionCommand.RestartSession(info.Id, true, reply)) with
          | Some (Error (SageFsError.HardResetFailed msg)) ->
            msg |> Expect.stringContains "rejection should explain the in-flight rebuild" "already in progress"
          | Some (Error otherErr) ->
            failtestf "second hard reset returned an unexpected error: %s" (SageFsError.describe otherErr)
          | Some (Ok _) -> failtest "second hard reset unexpectedly accepted while a rebuild was in flight"
          | None -> failtest "second hard reset hung instead of being rejected"

          releaseBuild.TrySetResult(true) |> ignore
          match firstRestart.Wait(5000) with
          | true ->
            match firstRestart.Result with
            | Ok _ -> ()
            | Error err -> failtestf "first cold restart failed: %s" (SageFsError.describe err)
          | false -> failtest "first cold restart did not complete"

          // create (1) + respawn (1) — the rejected second reset must not spawn.
          runtime.GetStartCalls()
          |> Expect.equal "exactly one replacement worker spawned" 2
        finally
          releaseBuild.TrySetResult(true) |> ignore

    testCase "crash-recovery ScheduleRestart does not double-spawn during an in-flight cold rebuild" <| fun _ ->
      let buildStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously)
      let releaseBuild = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously)
      let runtime =
        mkRuntime
          okStart
          (fun () -> async { return () })
          (fun () -> async {
            buildStarted.TrySetResult(true) |> ignore
            let! _ = releaseBuild.Task |> Async.AwaitTask
            return Ok "build ok"
          })

      withHarness runtime.Runtime <| fun harness ->
        let info = createSession harness
        let restartTask =
          harness.Mailbox.PostAndAsyncReply(fun reply -> SessionCommand.RestartSession(info.Id, true, reply))
          |> Async.StartAsTask

        try
          buildStarted.Task.Wait(2000) |> Expect.isTrue "cold build should have started"

          // Simulate the old worker's crash-recovery timer firing mid-build.
          harness.Mailbox.Post(SessionCommand.ScheduleRestart info.Id)

          // Round-trip proves the mailbox was free to dequeue the ScheduleRestart.
          match tryPostAndReply 1500 harness.Mailbox (fun reply -> SessionCommand.GetSession(info.Id, reply)) with
          | Some _ -> ()
          | None -> failtest "mailbox unresponsive while the cold build runs"

          // The crash-recovery respawn must NOT fire while the cold rebuild owns
          // the session: the build completion is the single respawn point.
          runtime.GetStartCalls()
          |> Expect.equal "ScheduleRestart must not spawn during an in-flight cold rebuild" 1

          releaseBuild.TrySetResult(true) |> ignore
          match restartTask.Wait(5000) with
          | true ->
            match restartTask.Result with
            | Ok _ -> ()
            | Error err -> failtestf "cold restart failed: %s" (SageFsError.describe err)
          | false -> failtest "cold restart did not complete after the build finished"
        finally
          releaseBuild.TrySetResult(true) |> ignore
  ]
