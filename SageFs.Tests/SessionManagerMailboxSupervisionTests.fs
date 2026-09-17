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
  (runBuild: unit -> Async<Result<string, SageFsError>>) =
  let mutable startCalls = 0
  {
    Runtime =
      {
        StartWorkerProcess =
          fun _ _ _ _ _ _ ->
            startCalls <- startCalls + 1
            startWorker startCalls |> Result.map (fun p -> ({ Process = p; AdoptedCore = None } : SessionManager.SpawnedWorker))
        AwaitWorkerPort = fun _ _ _ _ -> ()
        StopWorker = fun _ -> stopWorker ()
        RunBuildAsync = fun _ _ -> runBuild ()
      }
    GetStartCalls = fun () -> startCalls
  }

/// The task's result if it finishes within the timeout, None otherwise.
let private completesWithin (timeoutMs: int) (work: Task<'a>) : Task<'a option> =
  task {
    let! winner = Task.WhenAny(work :> Task, Task.Delay timeoutMs)
    match Object.ReferenceEquals(winner, work) with
    | true ->
      let! result = work
      return Some result
    | false -> return None
  }

/// PostAndReply bounded by a timeout — returns None when the mailbox is dead or
/// blocked (the failure mode this suite exists to prevent).
let private tryPostAndReply (timeoutMs: int) (mailbox: MailboxProcessor<SessionCommand>) (build: AsyncReplyChannel<'a> -> SessionCommand) : Task<'a option> =
  mailbox.PostAndAsyncReply(build) |> Async.StartAsTask |> completesWithin timeoutMs

let private postAndReply (mailbox: MailboxProcessor<SessionCommand>) (build: AsyncReplyChannel<'a> -> SessionCommand) : Task<'a> =
  mailbox.PostAndAsyncReply(build) |> Async.StartAsTask

let private withHarness runtime (run: Harness -> Task<unit>) : Task<unit> =
  task {
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
        (fun _ _ -> ())

    let harness = {
      Mailbox = mailbox
      ReadSnapshot = readSnapshot
      FaultedEvents = faultedEvents
      Cancellation = cancellation
    }

    let! failure =
      task {
        try
          do! run harness
          return None
        with ex -> return Some (Runtime.ExceptionServices.ExceptionDispatchInfo.Capture ex)
      }
    // Bounded teardown: a mailbox killed by the scenario under test must not
    // hang the suite forever.
    let! _ = tryPostAndReply 2000 mailbox (fun reply -> SessionCommand.StopAll reply)
    cancellation.Cancel()
    match failure with
    | Some captured -> captured.Throw()
    | None -> ()
  }

let private createSessionFor (projects: string list) (workingDir: string) (harness: Harness) : Task<SessionInfo> =
  task {
    let! created =
      postAndReply harness.Mailbox (fun reply ->
        SessionCommand.CreateSession(projects, workingDir, true, WorkflowTypes.SessionWorkflow.Interactive, reply))
    match created with
    | Ok info -> return info
    | Error err -> return failtestf "create session failed: %s" (SageFsError.describe err)
  }

let private createSession (harness: Harness) =
  createSessionFor [ "Test.fsproj" ] @"C:\Test" harness

let private okStart (_call: int) : Result<Process, SageFsError> =
  Ok (Process.GetCurrentProcess())

[<Tests>]
let sessionManagerMailboxSupervisionTests =
  testList "SessionManager mailbox supervision" [

    testTask "unexpected StopWorker exception does not kill the mailbox" {
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

      do! withHarness runtime.Runtime (fun harness -> task {
        let! info = createSession harness

        // Make the next stop throw inside the mailbox handler.
        stopFailure.Value <- true
        match! tryPostAndReply 1500 harness.Mailbox (fun reply -> SessionCommand.StopSession(info.Id, reply)) with
        | Some (Error (SageFsError.SessionStopFailed _)) -> ()
        | Some other -> failtestf "expected fail-closed SessionStopFailed, got %A" other
        | None ->
          failtest "StopSession hung — the handler exception killed the mailbox"

        stopFailure.Value <- false

        // The mailbox must still process commands and the session must not be
        // silently orphaned.
        let! sessions = postAndReply harness.Mailbox (fun reply -> SessionCommand.ListSessions reply)
        sessions
        |> List.map (fun s -> s.Id)
        |> Expect.contains "session survives a handler exception (no silent orphan)" info.Id

        match! postAndReply harness.Mailbox (fun reply -> SessionCommand.StopSession(info.Id, reply)) with
        | Ok () -> ()
        | Error err -> failtestf "clean stop after handler exception failed: %s" (SageFsError.describe err)
      })
    }

    testTask "handler exception leaves the CQRS snapshot consistent (fail-closed)" {
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

      do! withHarness runtime.Runtime (fun harness -> task {
        let! infoA = createSessionFor [ "A.fsproj" ] @"C:\A" harness
        let! infoB = createSessionFor [ "B.fsproj" ] @"C:\B" harness

        stopFailure.Value <- true
        match! tryPostAndReply 1500 harness.Mailbox (fun reply -> SessionCommand.StopSession(infoA.Id, reply)) with
        | Some (Error (SageFsError.SessionStopFailed _)) -> ()
        | _ -> failtest "expected fail-closed SessionStopFailed on the throwing stop"
        stopFailure.Value <- false

        // Snapshot still lists both sessions — nothing was silently dropped.
        let snap = harness.ReadSnapshot()
        QuerySnapshot.tryGetSession infoA.Id snap |> Expect.isSome "session A still visible after failed stop"
        QuerySnapshot.tryGetSession infoB.Id snap |> Expect.isSome "session B still visible after failed stop"

        // And the unaffected session can still be stopped normally.
        match! postAndReply harness.Mailbox (fun reply -> SessionCommand.StopSession(infoB.Id, reply)) with
        | Ok () -> ()
        | Error err -> failtestf "clean stop of unaffected session failed: %s" (SageFsError.describe err)
      })
    }

    testTask "SwitchWorkflow answers its channel on a handler exception instead of hanging (roast-7 §5 follow-up)" {
      // SwitchWorkflow carries a reply channel and spawn-first restarts into the
      // new workflow. If that spawn throws, the supervision backstop must answer
      // the channel (fail-closed) — previously SwitchWorkflow was the ONE
      // reply-carrying command left in the no-op group, so its caller hung
      // forever. Force the switch's spawn (the 2nd StartWorkerProcess call) to
      // throw and assert the caller gets an Error, not a hang.
      let switchFault = ref false
      let runtime =
        mkRuntime
          (fun call ->
            if switchFault.Value && call >= 2 then failwith "spawn boom during workflow switch"
            else okStart call)
          (fun () -> async { return () })
          (fun () -> async { return Ok "build ok" })

      do! withHarness runtime.Runtime (fun harness -> task {
        let! info = createSession harness

        switchFault.Value <- true
        match! tryPostAndReply 1500 harness.Mailbox (fun reply ->
          SessionCommand.SwitchWorkflow(info.Id, WorkflowTypes.SessionWorkflow.HotReload WorkflowTypes.BrowserRefreshConfig.defaults, reply)) with
        | Some (Error (SageFsError.HardResetFailed _)) -> ()
        | Some other -> failtestf "expected fail-closed HardResetFailed, got %A" other
        | None -> failtest "SwitchWorkflow hung — the handler exception left the reply channel unanswered"
        switchFault.Value <- false

        // The mailbox must still be alive and the session not silently orphaned.
        let! sessions = postAndReply harness.Mailbox (fun reply -> SessionCommand.ListSessions reply)
        sessions
        |> List.map (fun s -> s.Id)
        |> Expect.contains "session survives the SwitchWorkflow exception (mailbox not killed)" info.Id
      })
    }
  ]

[<Tests>]
let sessionManagerOffMailboxBuildTests =
  testList "SessionManager off-mailbox cold build" [

    testTask "list_sessions stays responsive while a cold-restart build runs" {
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

      do! withHarness runtime.Runtime (fun harness -> task {
        let! info = createSession harness
        let restartTask = postAndReply harness.Mailbox (fun reply -> SessionCommand.RestartSession(info.Id, true, reply))

        try
          let! started = completesWithin 2000 buildStarted.Task
          started |> Expect.isSome "cold build should have started"

          // While the build is in flight, list_sessions must still answer promptly.
          match! tryPostAndReply 1000 harness.Mailbox (fun reply -> SessionCommand.ListSessions reply) with
          | Some sessions ->
            sessions
            |> List.map (fun s -> s.Id)
            |> Expect.contains "session stays registered while the build runs" info.Id
          | None ->
            failtest "list_sessions blocked behind the cold build (mailbox serialized by dotnet build)"

          releaseBuild.TrySetResult(true) |> ignore
          match! completesWithin 5000 restartTask with
          | Some (Ok msg) -> msg |> Expect.stringContains "completion should report respawn" "Hard reset complete"
          | Some (Error err) -> failtestf "cold restart failed: %s" (SageFsError.describe err)
          | None -> failtest "cold restart did not complete after the build finished"
        finally
          releaseBuild.TrySetResult(true) |> ignore
      })
    }

    testTask "create_session stays responsive while a cold-restart build runs" {
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

      do! withHarness runtime.Runtime (fun harness -> task {
        let! info = createSession harness
        let restartTask = postAndReply harness.Mailbox (fun reply -> SessionCommand.RestartSession(info.Id, true, reply))

        try
          let! started = completesWithin 2000 buildStarted.Task
          started |> Expect.isSome "cold build should have started"

          // A brand-new session (different dir) must be creatable during the build.
          match! tryPostAndReply 1500 harness.Mailbox (fun reply ->
            SessionCommand.CreateSession([ "C.fsproj" ], @"C:\C", true, WorkflowTypes.SessionWorkflow.Interactive, reply)) with
          | Some (Ok second) -> second.Id |> Expect.notEqual "second session has its own id" info.Id
          | Some (Error err) -> failtestf "create_session during build failed: %s" (SageFsError.describe err)
          | None -> failtest "create_session blocked behind the cold build"

          releaseBuild.TrySetResult(true) |> ignore
          match! completesWithin 5000 restartTask with
          | Some (Ok _) -> ()
          | Some (Error err) -> failtestf "cold restart failed: %s" (SageFsError.describe err)
          | None -> failtest "cold restart did not complete after the build finished"
        finally
          releaseBuild.TrySetResult(true) |> ignore
      })
    }

    testTask "second hard reset while a rebuild is in flight is rejected at the owner" {
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

      do! withHarness runtime.Runtime (fun harness -> task {
        let! info = createSession harness
        let firstRestart = postAndReply harness.Mailbox (fun reply -> SessionCommand.RestartSession(info.Id, true, reply))

        try
          let! started = completesWithin 2000 buildStarted.Task
          started |> Expect.isSome "first cold build should have started"

          // A concurrent hard reset of the same session must be rejected, not
          // queued behind the build and not double-spawned.
          match! tryPostAndReply 1500 harness.Mailbox (fun reply -> SessionCommand.RestartSession(info.Id, true, reply)) with
          | Some (Error (SageFsError.HardResetFailed msg)) ->
            msg |> Expect.stringContains "rejection should explain the in-flight rebuild" "already in progress"
          | Some (Error otherErr) ->
            failtestf "second hard reset returned an unexpected error: %s" (SageFsError.describe otherErr)
          | Some (Ok _) -> failtest "second hard reset unexpectedly accepted while a rebuild was in flight"
          | None -> failtest "second hard reset hung instead of being rejected"

          releaseBuild.TrySetResult(true) |> ignore
          match! completesWithin 5000 firstRestart with
          | Some (Ok _) -> ()
          | Some (Error err) -> failtestf "first cold restart failed: %s" (SageFsError.describe err)
          | None -> failtest "first cold restart did not complete"

          // create (1) + respawn (1) — the rejected second reset must not spawn.
          runtime.GetStartCalls()
          |> Expect.equal "exactly one replacement worker spawned" 2
        finally
          releaseBuild.TrySetResult(true) |> ignore
      })
    }

    testTask "crash-recovery ScheduleRestart does not double-spawn during an in-flight cold rebuild" {
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

      do! withHarness runtime.Runtime (fun harness -> task {
        let! info = createSession harness
        let restartTask = postAndReply harness.Mailbox (fun reply -> SessionCommand.RestartSession(info.Id, true, reply))

        try
          let! started = completesWithin 2000 buildStarted.Task
          started |> Expect.isSome "cold build should have started"

          // Simulate the old worker's crash-recovery timer firing mid-build.
          harness.Mailbox.Post(SessionCommand.ScheduleRestart info.Id)

          // Round-trip proves the mailbox was free to dequeue the ScheduleRestart.
          match! tryPostAndReply 1500 harness.Mailbox (fun reply -> SessionCommand.GetSession(info.Id, reply)) with
          | Some _ -> ()
          | None -> failtest "mailbox unresponsive while the cold build runs"

          // The crash-recovery respawn must NOT fire while the cold rebuild owns
          // the session: the build completion is the single respawn point.
          runtime.GetStartCalls()
          |> Expect.equal "ScheduleRestart must not spawn during an in-flight cold rebuild" 1

          releaseBuild.TrySetResult(true) |> ignore
          match! completesWithin 5000 restartTask with
          | Some (Ok _) -> ()
          | Some (Error err) -> failtestf "cold restart failed: %s" (SageFsError.describe err)
          | None -> failtest "cold restart did not complete after the build finished"
        finally
          releaseBuild.TrySetResult(true) |> ignore
      })
    }
  ]
