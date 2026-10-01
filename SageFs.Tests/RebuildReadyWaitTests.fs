module SageFs.Tests.RebuildReadyWaitTests

// After a rebuild restart the live-testing pipeline waits for the session to be
// Ready with a streaming proxy. It used to poll ListSessions and
// GetStreamingTestProxy on a timer. These tests pin the event-driven wait that
// replaced it: one await on the session manager's AwaitReady, bounded by a
// deadline, abandoned on cancel, with no timer between.

open System
open System.Diagnostics
open System.Threading
open System.Threading.Tasks
open Expecto
open Expecto.Flip
open SageFs
open SageFs.SessionManager
open SageFs.SessionBuild
open SageFs.WorkerProtocol
open SageFs.Tests.SharedGenerators
open SageFs.Tests.SageFsEffectHandlerTests

// ---- the helper, on its own ----

/// Wait for a task with a ceiling, so a hang fails the test instead of the run.
let private within (ceiling: TimeSpan) (what: string) (pending: Task<'a>) : Task<'a> =
  task {
    let! winner = Task.WhenAny(pending, Task.Delay ceiling)
    match obj.ReferenceEquals(winner, pending) with
    | true -> return! pending
    | false -> return failtestf "%s did not happen within %O" what ceiling
  }

let private newSignal<'a> () = TaskCompletionSource<'a>(TaskCreationOptions.RunContinuationsAsynchronously)

[<Tests>]
let helperTests =
  testList "RebuildReadyWait.await" [
    testTask "an answer of Ok is Ready" {
      let! outcome =
        RebuildReadyWait.await (async { return Result.Ok () }) RebuildWaitTimeouts.deadlineNotHit CancellationToken.None
        |> Async.StartAsTask
      outcome |> Expect.equal "a session that turned Ready is Ready" (RebuildReadyWait.Outcome<string>.Ready)
    }

    testTask "an answer of Error is Failed with that error" {
      let! outcome =
        RebuildReadyWait.await (async { return Result.Error "worker faulted" }) RebuildWaitTimeouts.deadlineNotHit CancellationToken.None
        |> Async.StartAsTask
      outcome |> Expect.equal "the manager's reason is carried through" (RebuildReadyWait.Outcome.Failed "worker faulted")
    }

    testTask "no answer by the deadline is DeadlineReached" {
      let never = newSignal<Result<unit, string>> ()
      let! outcome =
        RebuildReadyWait.await (Async.AwaitTask never.Task) RebuildWaitTimeouts.deadlineToHit CancellationToken.None
        |> Async.StartAsTask
        |> within TestTimeouts.patienceBrief "the deadline outcome"
      outcome |> Expect.equal "a wait nobody answers ends at its deadline" (RebuildReadyWait.Outcome<string>.DeadlineReached)
    }

    testTask "cancelling the token ends a parked wait at once, well before the deadline" {
      let never = newSignal<Result<unit, string>> ()
      let cancellation = new CancellationTokenSource()
      let waiting =
        RebuildReadyWait.await (Async.AwaitTask never.Task) RebuildWaitTimeouts.deadlineNotHit cancellation.Token
        |> Async.StartAsTask
      cancellation.Cancel()
      let! outcome = waiting |> within TestTimeouts.patienceBrief "the cancelled outcome"
      outcome |> Expect.equal "a cancelled wait reports Cancelled, not a deadline" (RebuildReadyWait.Outcome<string>.Cancelled)
    }

    testTask "a token that is already cancelled is Cancelled without waiting for the answer" {
      let never = newSignal<Result<unit, string>> ()
      let cancellation = new CancellationTokenSource()
      cancellation.Cancel()
      let! outcome =
        RebuildReadyWait.await (Async.AwaitTask never.Task) RebuildWaitTimeouts.deadlineNotHit cancellation.Token
        |> Async.StartAsTask
        |> within TestTimeouts.patienceBrief "the cancelled outcome"
      outcome |> Expect.equal "already cancelled" (RebuildReadyWait.Outcome<string>.Cancelled)
    }
  ]

// ---- the effect, against the EffectDeps fakes ----

let private sid = testSessionId "a1b2c3d4"

let private testCase' : Features.LiveTesting.TestCase = {
  Id = Features.LiveTesting.TestId.TestId "t-rebuild-wait"
  FullName = "Sample.Tests.rebuild wait"
  DisplayName = "rebuild wait"
  Origin = Features.LiveTesting.TestOrigin.ReflectionOnly
  Labels = []
  Framework = Features.LiveTesting.TestFramework.Expecto
  Category = Features.LiveTesting.TestCategory.Unit
}

let private sessionInfo status : SessionInfo = {
  Id = sid
  Name = None
  Projects = ["Test.fsproj"]
  WorkingDirectory = "."
  SolutionRoot = None
  CreatedAt = DateTime.UtcNow
  LastActivity = DateTime.UtcNow
  Status = status
  Workflow = WorkflowTypes.SessionWorkflow.Interactive
  ActiveProject = None
  ProjectRoles = []
  App = SageFs.AppRun.AppRunState.NotRunning
  Rebuild = LastRebuild.NeverRebuilt
  Reload = SessionReload.NoReloadYet; Freshness = SageFs.ReplFreshness.InSync
}

let private aProxy =
  Some (fun _ _ _ _ _ -> async { return HttpWorkerClient.StreamOutcome.Completed })

/// What the fakes saw, so a test can say how often the effect looked.
type private Calls = {
  mutable ListSessions: int
  mutable StreamingProxy: int
  mutable AwaitReady: int
  /// Completed once the effect has asked the manager to tell it when the session is Ready.
  Awaiting: TaskCompletionSource<unit>
}

let private newCalls () = {
  ListSessions = 0
  StreamingProxy = 0
  AwaitReady = 0
  Awaiting = newSignal<unit> ()
}

/// EffectDeps for one session. `awaitReady` is the manager's answer, called once per rebuild.
let private depsFor
  (calls: Calls)
  (deadline: TimeSpan)
  (proxy: unit -> _ option)
  (awaitReady: Async<Result<unit, SageFsError>>)
  : EffectDeps =
  { TestDeps.noSessions () with
      ResolveSession = fun _ -> Result.Ok (SessionOperations.SessionResolution.DefaultSingle sid)
      GetStreamingTestProxy = fun _ ->
        Interlocked.Increment(&calls.StreamingProxy) |> ignore
        proxy ()
      RestartSession = fun _ _ -> async { return Result.Ok "restarted" }
      ListSessions = fun () ->
        async {
          Interlocked.Increment(&calls.ListSessions) |> ignore
          return [ sessionInfo (SessionLifecycleStatus.Starting { Pid = 0; Port = None }) ]
        }
      AwaitReady = fun _ ->
        async {
          Interlocked.Increment(&calls.AwaitReady) |> ignore
          calls.Awaiting.TrySetResult () |> ignore
          return! awaitReady
        }
      ReadyDeadline = deadline }

let private rebuildEffect generation =
  SageFsEffect.TestCycle (
    Features.LiveTesting.TestCycleEffect.RequestRebuild(
      generation,
      { Tests = [| testCase' |]
        Trigger = Features.LiveTesting.RunTrigger.FileSave
        TreeSitterElapsed = TimeSpan.Zero
        FcsElapsed = TimeSpan.Zero
        SessionId = Some (SessionId.value sid)
        InstrumentationMaps = [||] }))

let private cancelEffect generation =
  SageFsEffect.TestCycle (Features.LiveTesting.TestCycleEffect.CancelRebuild (Some (SessionId.value sid), generation))

/// Everything the effect dispatched, and a signal for the first RebuildCompleted.
type private Run = {
  Dispatched: ResizeArray<SageFsMsg>
  FirstCompletion: TaskCompletionSource<unit>
}

let private newRun () = { Dispatched = ResizeArray(); FirstCompletion = newSignal<unit> () }

let private dispatchInto (run: Run) (msg: SageFsMsg) =
  lock run.Dispatched (fun () -> run.Dispatched.Add msg)
  match msg with
  | SageFsMsg.RebuildCompleted _ -> run.FirstCompletion.TrySetResult () |> ignore
  | _ -> ()

let private dispatchedOf (run: Run) = lock run.Dispatched (fun () -> run.Dispatched |> Seq.toList)

let private start deps run effect = SageFsEffectHandler.execute deps (dispatchInto run) effect

let private completedOk generation = SageFsMsg.RebuildCompleted (Some (SessionId.value sid), generation, Ok ())

let private errorOf (run: Run) =
  match dispatchedOf run with
  | [ SageFsMsg.RebuildCompleted (_, _, Error message) ] -> message
  | other -> failtestf "expected exactly one failed RebuildCompleted, got %A" other

[<Tests>]
let effectTests =
  testList "RequestRebuild readiness wait" [
    testTask "completes as soon as the session is Ready, and nothing polls while it waits" {
      let calls = newCalls ()
      let ready = newSignal<Result<unit, SageFsError>> ()
      let run = newRun ()
      let deps = depsFor calls RebuildWaitTimeouts.deadlineNotHit (fun () -> aProxy) (Async.AwaitTask ready.Task)
      do! start deps run (rebuildEffect 1L)
      do! calls.Awaiting.Task |> within TestTimeouts.patienceBrief "the effect parking on AwaitReady"
      // The old wait polled every 50 ms in the first second. Let it sit through
      // several of those and see that it looked at nothing.
      do! Task.Delay RebuildWaitTimeouts.quietWindow
      calls.ListSessions |> Expect.equal "no session list while parked" 0
      calls.StreamingProxy |> Expect.equal "no proxy lookup while parked" 0
      dispatchedOf run |> Expect.isEmpty "nothing is reported before the session is Ready"
      ready.SetResult (Result.Ok ())
      do! run.FirstCompletion.Task |> within TestTimeouts.patienceBrief "the rebuild completing"
      dispatchedOf run |> Expect.equal "Ready completes the rebuild" [ completedOk 1L ]
      calls.AwaitReady |> Expect.equal "one await, not one per look" 1
      calls.StreamingProxy |> Expect.equal "the proxy is read once, after Ready" 1
      calls.ListSessions |> Expect.equal "the session list is never read on the success path" 0
    }

    testTask "a session that is already Ready completes without waiting" {
      let calls = newCalls ()
      let run = newRun ()
      let deps = depsFor calls RebuildWaitTimeouts.deadlineNotHit (fun () -> aProxy) (async { return Result.Ok () })
      do! start deps run (rebuildEffect 1L)
      do! run.FirstCompletion.Task |> within TestTimeouts.patienceBrief "the rebuild completing"
      dispatchedOf run |> Expect.equal "Ready completes the rebuild" [ completedOk 1L ]
    }

    testTask "Ready with no streaming proxy fails closed at once instead of polling for one" {
      // The manager installs the worker's URL (WorkerReady) before it can mark the
      // session Ready, so a Ready session always has a proxy. If that ever breaks,
      // the rebuild says so now, rather than waiting out the deadline for a proxy
      // that nothing is going to register.
      let calls = newCalls ()
      let run = newRun ()
      let deps = depsFor calls RebuildWaitTimeouts.deadlineNotHit (fun () -> None) (async { return Result.Ok () })
      do! start deps run (rebuildEffect 1L)
      do! run.FirstCompletion.Task |> within TestTimeouts.patienceBrief "the rebuild failing"
      let message = errorOf run
      message |> Expect.stringContains "names the session" (SessionId.value sid)
      message |> Expect.stringContains "says what is missing" "streaming proxy"
      calls.StreamingProxy |> Expect.equal "the proxy is looked up once, not polled for" 1
    }

    testTask "a session that faults while waiting fails the rebuild with the manager's reason" {
      let calls = newCalls ()
      let run = newRun ()
      let fault = SageFsError.WorkerSpawnFailed "the build broke"
      let deps = depsFor calls RebuildWaitTimeouts.deadlineNotHit (fun () -> None) (async { return Result.Error fault })
      do! start deps run (rebuildEffect 1L)
      do! run.FirstCompletion.Task |> within TestTimeouts.patienceBrief "the rebuild failing"
      errorOf run |> Expect.stringContains "carries the reason" (SageFsError.describe fault)
      calls.StreamingProxy |> Expect.equal "a faulted session is not asked for a proxy" 0
    }

    testTask "the deadline fails closed and names what was observed" {
      let calls = newCalls ()
      let never = newSignal<Result<unit, SageFsError>> ()
      let run = newRun ()
      let deps = depsFor calls RebuildWaitTimeouts.deadlineToHit (fun () -> None) (Async.AwaitTask never.Task)
      do! start deps run (rebuildEffect 1L)
      do! run.FirstCompletion.Task |> within TestTimeouts.patienceBrief "the deadline failure"
      let message = errorOf run
      message |> Expect.stringContains "says it never became ready" "never became ready for test execution"
      message |> Expect.stringContains "names the session" (SessionId.value sid)
      message |> Expect.stringContains "names the status it last saw" "status=Starting"
      message |> Expect.stringContains "says ready was not observed" "ready=not-observed"
      message |> Expect.stringContains "says the proxy was not observed" "proxy=not-observed"
      calls.ListSessions |> Expect.equal "the status is read once, for the message" 1
    }

    testTask "cancelling the rebuild stops the wait and reports nothing" {
      let calls = newCalls ()
      let ready = newSignal<Result<unit, SageFsError>> ()
      let run = newRun ()
      let deps = depsFor calls RebuildWaitTimeouts.deadlineNotHit (fun () -> aProxy) (Async.AwaitTask ready.Task)
      do! start deps run (rebuildEffect 1L)
      do! calls.Awaiting.Task |> within TestTimeouts.patienceBrief "the effect parking on AwaitReady"
      do! start deps run (cancelEffect 1L)
      // Were the wait still alive, this would complete it.
      ready.SetResult (Result.Ok ())
      do! Task.Delay RebuildWaitTimeouts.nothingReportedWindow
      dispatchedOf run |> Expect.isEmpty "a cancelled rebuild reports no completion"
    }

    testTask "a stale cancel does not stop the newer rebuild" {
      let calls = newCalls ()
      let ready = newSignal<Result<unit, SageFsError>> ()
      let run = newRun ()
      let deps = depsFor calls RebuildWaitTimeouts.deadlineNotHit (fun () -> aProxy) (Async.AwaitTask ready.Task)
      do! start deps run (rebuildEffect 2L)
      do! calls.Awaiting.Task |> within TestTimeouts.patienceBrief "the effect parking on AwaitReady"
      do! start deps run (cancelEffect 1L)
      ready.SetResult (Result.Ok ())
      do! run.FirstCompletion.Task |> within TestTimeouts.patienceBrief "the newer rebuild completing"
      dispatchedOf run |> Expect.equal "the newer rebuild still completes" [ completedOk 2L ]
    }

    testTask "a newer rebuild supersedes the older one, and only the newer reports" {
      let calls = newCalls ()
      let ready = newSignal<Result<unit, SageFsError>> ()
      let run = newRun ()
      let deps = depsFor calls RebuildWaitTimeouts.deadlineNotHit (fun () -> aProxy) (Async.AwaitTask ready.Task)
      do! start deps run (rebuildEffect 1L)
      do! calls.Awaiting.Task |> within TestTimeouts.patienceBrief "the first rebuild parking on AwaitReady"
      do! start deps run (rebuildEffect 2L)
      ready.SetResult (Result.Ok ())
      do! run.FirstCompletion.Task |> within TestTimeouts.patienceBrief "the newer rebuild completing"
      do! Task.Delay RebuildWaitTimeouts.nothingReportedWindow
      dispatchedOf run |> Expect.equal "only the latest rebuild reports" [ completedOk 2L ]
    }
  ]

// ---- the claim the effect leans on, against the real session manager ----
//
// One await on AwaitReady is enough only if a session that AwaitReady calls Ready
// already has its worker URL in the published snapshot, because that is what
// GetStreamingTestProxy reads. The manager installs the URL at WorkerReady, in a
// step of its own, and publishes the state after every step before it takes the next
// command, so the URL is out before a Ready step can run. Each trial below issues
// AwaitReady at a seeded point around WorkerReady and Ready, and checks the snapshot
// the moment AwaitReady answers Ok. The twin skips WorkerReady, which is the bad
// rule the claim rules out, and has to be caught.

type private Manager = {
  Mailbox: MailboxProcessor<SessionCommand>
  ReadSnapshot: unit -> QuerySnapshot
}

let private quietProxy : SessionProxy =
  fun _ -> async { return WorkerResponse.WorkerError (SageFsError.WorkerSpawnFailed "no worker here") }

let private withManager run =
  use cancellation = new CancellationTokenSource()
  let runtime : SessionManagerRuntime = {
    StartWorkerProcess =
      fun _ _ _ _ _ _ -> Ok ({ Process = Process.GetCurrentProcess(); AdoptedCore = None } : SessionManager.SpawnedWorker)
    AwaitWorkerPort = fun _ _ _ _ _ -> ()
    Ledger = StartLedger.closed
    StopWorker = fun _ -> async { return () }
    RunBuildAsync = fun _ _ -> async { return Ok "build ok" }
  }
  let mailbox, readSnapshot =
    createWith runtime cancellation.Token ignore (fun _ _ -> ()) (fun _ _ -> ()) ignore (fun _ _ -> ()) (fun _ _ -> ()) (fun _ _ -> ())
  try
    run { Mailbox = mailbox; ReadSnapshot = readSnapshot }
  finally
    try mailbox.PostAndReply(fun reply -> SessionCommand.StopAll reply) with _ -> ()
    cancellation.Cancel()

type private Trial =
  | WorkerReadyThenReady
  /// The twin: the session is marked Ready with no WorkerReady ever installing a URL.
  | ReadyWithoutTransport

/// Run one trial and return what was wrong when AwaitReady answered, if anything.
let private runTrial (manager: Manager) (seed: int) (trial: Trial) : string list =
  let random = Random seed
  let info =
    match manager.Mailbox.PostAndReply(fun reply ->
      SessionCommand.CreateSession([ SageFs.SessionProjectTarget.Project "Test.fsproj" ], sprintf "/test/%d" seed, true, WorkflowTypes.SessionWorkflow.Interactive, reply)) with
    | Ok info -> info
    | Error err -> failtestf "create session failed: %s" (SageFsError.describe err)
  let session =
    match manager.Mailbox.PostAndReply(fun reply -> SessionCommand.GetSession(info.Id, reply)) with
    | Some session -> session
    | None -> failtest "the session should exist"
  let pid = SessionLifecycleStatus.workerPid session.Info.Status |> Option.defaultWith (fun () -> failtest "expected a worker pid")
  let yields () = for _ in 1 .. random.Next 4 do Thread.Yield() |> ignore
  let install () =
    match trial with
    | WorkerReadyThenReady ->
      manager.Mailbox.Post(SessionCommand.WorkerReady(info.Id, pid, "http://localhost:4123", quietProxy))
    | ReadyWithoutTransport -> ()
  let becomeReady () =
    manager.Mailbox.Post(SessionCommand.UpdateSessionStatus(info.Id, SessionLifecycleStatus.Ready { Pid = pid; Port = Some 4123 }))
  let awaitReady () =
    manager.Mailbox.PostAndAsyncReply(fun reply -> SessionCommand.AwaitReady(info.Id, reply)) |> Async.StartAsTask
  // Where AwaitReady is issued: before WorkerReady, between it and Ready, or after both.
  let waiter =
    match random.Next 3 with
    | 0 ->
      let waiter = awaitReady ()
      yields ()
      install ()
      yields ()
      becomeReady ()
      waiter
    | 1 ->
      install ()
      yields ()
      let waiter = awaitReady ()
      yields ()
      becomeReady ()
      waiter
    | _ ->
      install ()
      becomeReady ()
      yields ()
      awaitReady ()
  match waiter.Wait TestTimeouts.patienceBrief with
  | false -> [ sprintf "seed %d: AwaitReady never answered" seed ]
  | true ->
    match waiter.Result with
    | Error err -> [ sprintf "seed %d: AwaitReady answered %s" seed (SageFsError.describe err) ]
    | Ok () ->
      match manager.ReadSnapshot().WorkerBaseUrls |> Map.tryFind info.Id with
      | Some url when url.Length > 0 -> []
      | _ -> [ sprintf "seed %d: AwaitReady answered Ok but the published snapshot has no worker URL" seed ]

let private seeds = [ 1 .. 200 ]

[<Tests>]
let managerTests =
  testList "AwaitReady against the session manager" [
    testCase "when AwaitReady answers Ok, the worker URL is already published, whatever the order of arrival" <| fun _ ->
      withManager <| fun manager ->
        seeds
        |> List.collect (fun seed -> runTrial manager seed WorkerReadyThenReady)
        |> Expect.isEmpty "every seed leaves the URL published by the time AwaitReady answers"

    testCase "twin: a session made Ready with no transport is caught by the same check" <| fun _ ->
      withManager <| fun manager ->
        let violations = seeds |> List.collect (fun seed -> runTrial manager seed ReadyWithoutTransport)
        violations
        |> List.length
        |> Expect.equal "the bad rule is caught on every seed" (List.length seeds)
  ]
