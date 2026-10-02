/// What the real SessionManager tells a caller parked on AwaitReady while the session rebuilds.
///
/// A build-first rebuild keeps the old worker serving, so the session reads Ready for the whole
/// build. A caller who asked for the rebuild and then waits for Ready is waiting for the NEW build.
/// The manager used to answer that caller from the old worker at once. These tests hold the build
/// open, park a caller, and look at when and how it is answered.
module SageFs.Tests.ReadyWaitManagerTests

open System
open System.Diagnostics
open System.Threading
open System.Threading.Tasks
open Expecto
open Expecto.Flip
open SageFs
open SageFs.SessionManager
open SageFs.WorkerProtocol

type private Harness =
  { Mailbox: MailboxProcessor<SessionCommand> }

/// A runtime whose build the test holds open until it decides how the build ends.
let private heldBuild () : TaskCompletionSource<Result<string, SageFsError>> * SessionManagerRuntime =
  let gate = TaskCompletionSource<Result<string, SageFsError>>(TaskCreationOptions.RunContinuationsAsynchronously)
  let self = Process.GetCurrentProcess()
  let runtime : SessionManagerRuntime =
    { StartWorkerProcess =
        fun _ _ _ _ _ _ -> Ok ({ Process = self; AdoptedCore = None } : SessionManager.SpawnedWorker)
      AwaitWorkerPort = fun _ _ _ _ _ -> ()
      Ledger = StartLedger.closed
      StopWorker = fun _ -> async { return () }
      RunBuildAsync = fun _ _ -> async { return! Async.AwaitTask gate.Task } }
  gate, runtime

/// Runs a body against a real manager and stops it afterwards, awaiting the body first.
let private withManager (runtime: SessionManagerRuntime) (body: Harness -> Task<unit>) : Task<unit> =
  task {
    use cancellation = new CancellationTokenSource()
    let mailbox, _ =
      createWith
        runtime
        cancellation.Token
        ignore
        (fun _ _ -> ())
        (fun _ _ -> ())
        ignore
        (fun _ _ -> ())
        (fun _ _ -> ())
        (fun _ _ -> ())
    let! outcome =
      (body { Mailbox = mailbox })
        .ContinueWith(fun (t: Task<unit>) ->
          match t.IsFaulted with
          | true -> Error (t.Exception :> exn)
          | false -> Ok ())
    try mailbox.PostAndReply(fun reply -> SessionCommand.StopAll reply) with _ -> ()
    cancellation.Cancel()
    match outcome with
    | Error e -> raise e
    | Ok () -> ()
  }

let private readyProxy : SessionProxy =
  fun msg ->
    async {
      match msg with
      | WorkerMessage.GetStatus rid ->
        return
          WorkerResponse.StatusResult(
            rid,
            { Status = SessionStatus.Ready
              StatusMessage = None
              EvalCount = 0
              AvgDurationMs = FixtureDurations.unmeasuredMs
              MinDurationMs = FixtureDurations.unmeasuredMs
              MaxDurationMs = FixtureDurations.unmeasuredMs
              Projects = []
              CoreVersion = "0.0.0-test" })
      | WorkerMessage.GetTestDiscovery _ -> return WorkerResponse.InitialTestDiscovery([||], [])
      | _ -> return WorkerResponse.WorkerError (SageFsError.WorkerSpawnFailed "unexpected message")
    }

/// Waits for the mailbox to have handled everything posted before this call.
let private flush (harness: Harness) (id: SessionId) : unit =
  harness.Mailbox.PostAndReply(fun reply -> SessionCommand.GetSession(id, reply)) |> ignore

let private createReady (harness: Harness) : SessionId =
  let info =
    match harness.Mailbox.PostAndReply(fun reply ->
      SessionCommand.CreateSession([ SageFs.SessionProjectTarget.Project "Test.fsproj" ], @"C:\Test", true, WorkflowTypes.SessionWorkflow.Interactive, reply)) with
    | Ok info -> info
    | Error err -> failtestf "create session failed: %s" (SageFsError.describe err)
  let pid =
    match harness.Mailbox.PostAndReply(fun reply -> SessionCommand.GetSession(info.Id, reply)) with
    | Some session -> SessionLifecycleStatus.workerPid session.Info.Status |> Option.defaultWith (fun () -> failtest "expected a worker pid")
    | None -> failtest "expected the session"
  harness.Mailbox.Post(SessionCommand.WorkerReady(info.Id, pid, "http://localhost:4123", readyProxy))
  flush harness info.Id
  harness.Mailbox.Post(SessionCommand.UpdateSessionStatus(info.Id, SessionLifecycleStatus.Ready { Pid = pid; Port = Some 4123 }))
  flush harness info.Id
  info.Id

/// Asks for a rebuild. The command is posted before this returns, so the mailbox sees it before
/// anything posted after it. The task is the rebuild's own answer.
let private startRebuild (harness: Harness) (id: SessionId) : Task<Result<string, SageFsError>> =
  harness.Mailbox.PostAndAsyncReply(fun reply ->
    SessionCommand.RestartSession(id, SageFs.RestartPlan.Rebuild SageFs.GranularRestart.RestartSubject.Worker, reply))
  |> Async.StartAsTask

/// Parks a caller the way the status tool does. Posted before this returns.
let private awaitReady (harness: Harness) (id: SessionId) : Task<Result<unit, SageFsError>> =
  harness.Mailbox.PostAndAsyncReply(fun reply -> SessionCommand.AwaitReady(id, reply))
  |> Async.StartAsTask

/// Whether the task answered within the window a wrong answer would have arrived in.
let private answersSoon (window: TimeSpan) (answer: Task<'a>) : Task<bool> =
  task {
    let! winner = Task.WhenAny(answer :> Task, Task.Delay window)
    return Object.ReferenceEquals(winner, answer)
  }

let private buildFailure =
  SageFsError.BuildFailed(1, [ BuildDiagnostic.ofLine "Hello.fs(3,5): error FS0001: expected int" ])

[<Tests>]
let readyWaitManagerTests =
  testList "SessionManager AwaitReady across a rebuild" [

    testTask "WHY — a caller parked while the rebuild builds is not answered by the old worker's Ready, and is answered when the NEW worker is Ready" {
      let others = Process.GetProcesses() |> Array.filter (fun p -> p.Id <> Process.GetCurrentProcess().Id && p.Id > 0)
      match others.Length with
      | 0 -> skiptest "need a second live process to stand in for the replacement worker's pid"
      | _ -> ()
      let replacement = others[0]
      let gate, runtime = heldBuild ()
      // The replacement worker is a different process from the first.
      let spawned = ref 0
      let runtime =
        { runtime with
            StartWorkerProcess =
              fun a b c d e f ->
                spawned.Value <- spawned.Value + 1
                match spawned.Value with
                | 1 -> runtime.StartWorkerProcess a b c d e f
                | _ -> Ok ({ Process = replacement; AdoptedCore = None } : SessionManager.SpawnedWorker) }
      do! withManager runtime (fun harness ->
        task {
          let id = createReady harness
          let rebuilt = startRebuild harness id
          let waiter = awaitReady harness id
          flush harness id
          let! answeredEarly = answersSoon TestTimeouts.absentAnswerWindow waiter
          answeredEarly |> Expect.isFalse "the old worker is Ready, but the build the caller asked about is not done"

          gate.SetResult (Result.Ok "build ok")
          let! _ = rebuilt
          flush harness id
          let! answeredBeforeReady = answersSoon TestTimeouts.absentAnswerWindow waiter
          answeredBeforeReady |> Expect.isFalse "the replacement worker is spawning, so the caller is still parked"

          harness.Mailbox.Post(SessionCommand.WorkerReady(id, replacement.Id, "http://localhost:4124", readyProxy))
          // What the worker's ready poll does in production once the transport is installed.
          harness.Mailbox.Post(SessionCommand.UpdateSessionStatus(id, SessionLifecycleStatus.Ready { Pid = replacement.Id; Port = Some 4124 }))
          let! answer = waiter.WaitAsync TestTimeouts.shortPatience
          answer |> Expect.equal "answered the moment the new worker is Ready" (Result.Ok ())
        })
    }

    testTask "WHY — a caller parked while the rebuild builds is told the build's own error when it fails, not Ready from the worker that kept serving" {
      let gate, runtime = heldBuild ()
      do! withManager runtime (fun harness ->
        task {
          let id = createReady harness
          let rebuilt = startRebuild harness id
          let waiter = awaitReady harness id
          flush harness id
          let! answeredEarly = answersSoon TestTimeouts.absentAnswerWindow waiter
          answeredEarly |> Expect.isFalse "parked while the build runs"

          gate.SetResult (Result.Error buildFailure)
          let! _ = rebuilt
          let! answer = waiter.WaitAsync TestTimeouts.shortPatience
          match answer with
          | Result.Error (SageFsError.BuildFailed _) -> ()
          | other -> failtestf "the wait must fail with the build's error, got %A" other
        })
    }

    testTask "WHY — a caller who asks after a failed rebuild is answered Ready at once, because the session serves and nothing is running" {
      let gate, runtime = heldBuild ()
      do! withManager runtime (fun harness ->
        task {
          let id = createReady harness
          let rebuilt = startRebuild harness id
          flush harness id
          gate.SetResult (Result.Error buildFailure)
          let! _ = rebuilt
          let! answer = (awaitReady harness id).WaitAsync TestTimeouts.shortPatience
          answer |> Expect.equal "the last rebuild is over, so a new caller is not made to wait for it" (Result.Ok ())
        })
    }

    testTask "WHY — a caller on a session nobody is rebuilding is answered Ready at once" {
      let _, runtime = heldBuild ()
      do! withManager runtime (fun harness ->
        task {
          let id = createReady harness
          let! answer = (awaitReady harness id).WaitAsync TestTimeouts.shortPatience
          answer |> Expect.equal "an idle Ready session never makes a caller wait" (Result.Ok ())
        })
    }
  ]
