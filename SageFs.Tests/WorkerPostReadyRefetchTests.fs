/// The daemon can ask a live session's worker for its tests and its instrumentation maps again, on demand,
/// without the ready watchdog. A new FSI host instruments a new copy and probe numbering changes under maps
/// the daemon still holds, so a later package that refreshes the REPL in the background needs this call.
/// These tests post `RefetchLiveTestingState` to the real mailbox, with only process spawn faked (the same fake
/// runtime as SessionManagerRebuildOutcomeTests) and a proxy that counts what the worker is asked.
module SageFs.Tests.WorkerPostReadyRefetchTests

open System
open System.Diagnostics
open System.Threading
open System.Threading.Tasks
open Expecto
open Expecto.Flip
open SageFs
open SageFs.SessionManager
open SageFs.WorkerProtocol

/// What the daemon heard from the manager, and what the worker was asked.
type private Harness =
  { Mailbox: MailboxProcessor<SessionCommand>
    /// One release per discovery callback the manager fired.
    Discoveries: SemaphoreSlim
    /// One release per instrumentation-maps callback the manager fired.
    Maps: SemaphoreSlim
    DiscoveryAsks: int ref
    MapAsks: int ref
    /// While set, the worker's replies (tests and maps alike) are held back until it completes.
    HoldReplies: TaskCompletionSource<unit> option ref
    /// One release per request that reached the worker's proxy.
    Asked: SemaphoreSlim }

let private mkRuntime () : SessionManagerRuntime =
  { StartWorkerProcess =
      fun _ _ _ _ _ _ -> Ok ({ Process = Process.GetCurrentProcess(); AdoptedCore = None } : SpawnedWorker)
    AwaitWorkerPort = fun _ _ _ _ _ -> ()
    Ledger = StartLedger.closed
    StopWorker = fun _ -> async { return () }
    RunBuildAsync = fun _ _ -> async { return Ok "build ok" } }

let private withHarness (run: Harness -> (SessionProxy) -> Task) : Task =
  task {
    use cancellation = new CancellationTokenSource()
    let discoveries = new SemaphoreSlim(0)
    let maps = new SemaphoreSlim(0)
    let discoveryAsks = ref 0
    let mapAsks = ref 0
    let hold : TaskCompletionSource<unit> option ref = ref None
    let asked = new SemaphoreSlim(0)
    let callbacks =
      { SessionManagerCallbacks.silent with
          OnTestDiscovery = fun _ _ -> discoveries.Release() |> ignore
          OnInstrumentationMaps = fun _ _ -> maps.Release() |> ignore }
    let mailbox, _ = createWithAlarm (mkRuntime ()) cancellation.Token callbacks
    let proxy : SessionProxy =
      fun (msg: WorkerMessage) ->
        async {
          match msg with
          | WorkerMessage.GetTestDiscovery _ ->
            Interlocked.Increment discoveryAsks |> ignore
            asked.Release() |> ignore
            match hold.Value with
            | Some gate -> do! gate.Task |> Async.AwaitTask
            | None -> ()
            return WorkerResponse.InitialTestDiscovery([||], [])
          | WorkerMessage.GetInstrumentationMaps rid ->
            Interlocked.Increment mapAsks |> ignore
            asked.Release() |> ignore
            match hold.Value with
            | Some gate -> do! gate.Task |> Async.AwaitTask
            | None -> ()
            return WorkerResponse.InstrumentationMapsResult(rid, [| Features.LiveTesting.InstrumentationMap.empty |])
          | WorkerMessage.GetStatus rid ->
            // Never Ready: the tests post WorkerReportedReady themselves, so the watchdog stays out of the way.
            let snap : WorkerStatusSnapshot =
              { Status = SessionStatus.Starting
                StatusMessage = Some "held by the test"
                EvalCount = 0
                AvgDurationMs = FixtureDurations.unmeasuredMs
                MinDurationMs = FixtureDurations.unmeasuredMs
                MaxDurationMs = FixtureDurations.unmeasuredMs
                Projects = []
                CoreVersion = "0.0.0-test" }
            return WorkerResponse.StatusResult(rid, snap)
          | _ -> return WorkerResponse.WorkerError (SageFsError.WorkerSpawnFailed "unexpected message")
        }
    try
      do! run { Mailbox = mailbox; Discoveries = discoveries; Maps = maps; DiscoveryAsks = discoveryAsks; MapAsks = mapAsks; HoldReplies = hold; Asked = asked } proxy
    finally
      try mailbox.PostAndReply(fun reply -> SessionCommand.StopAll reply) with _ -> ()
      cancellation.Cancel()
  }

let private createSession (harness: Harness) : SessionInfo =
  match harness.Mailbox.PostAndReply(fun reply ->
    SessionCommand.CreateSession([ SessionProjectTarget.Project "Foo.fsproj" ], "/nonexistent/does-not-matter", true, WorkflowTypes.SessionWorkflow.Interactive, reply)) with
  | Ok info -> info
  | Error err -> failtestf "create session failed: %s" (SageFsError.describe err)

let private sessionOf (harness: Harness) (id: SessionId) : ManagedSession =
  match harness.Mailbox.PostAndReply(fun reply -> SessionCommand.GetSession(id, reply)) with
  | Some session -> session
  | None -> failtestf "expected session %s to exist" (SessionId.value id)

let private resolvedRole : ProjectLoading.ClassifiedProject =
  { Path = "/repo/Foo.fsproj"; Role = ProjectLoading.ProjectRole.Library; PackageRefs = []; LoadMode = ProjectLoading.LoadMode.Evaluated; Build = SageFs.BuildOptimization.Unoptimized }

/// A session whose worker is installed and has reported Ready. Returns after the first (post-ready) discovery and
/// maps fetches have both been published, so a case counts only what it asked for afterwards.
let private readySession (harness: Harness) (proxy: SessionProxy) : Task<SessionInfo * int> =
  task {
    let info = createSession harness
    let pid = SessionLifecycleStatus.workerPid (sessionOf harness info.Id).Info.Status |> Option.defaultWith (fun () -> failtest "expected a worker pid after CreateSession")
    harness.Mailbox.Post(SessionCommand.WorkerReady(info.Id, pid, "http://localhost:4123", proxy))
    harness.Mailbox.Post(SessionCommand.WorkerReportedReady(info.Id, pid, [ resolvedRole ]))
    let! _ = harness.Discoveries.WaitAsync TestTimeouts.patienceInProcess
    let! _ = harness.Maps.WaitAsync TestTimeouts.patienceInProcess
    // Take the ready's own two asks off the tally, so a case waiting on `Asked` waits for its own.
    let! _ = harness.Asked.WaitAsync TestTimeouts.patienceInProcess
    let! _ = harness.Asked.WaitAsync TestTimeouts.patienceInProcess
    match (sessionOf harness info.Id).Info.Status with
    | SessionLifecycleStatus.Ready _ -> ()
    | other -> failtestf "expected Ready, got %A" other
    return info, pid
  }

let private refetch (harness: Harness) (id: SessionId) : Result<unit, WorkerPostReady.RefetchRefusal> =
  harness.Mailbox.PostAndReply(fun reply -> SessionCommand.RefetchLiveTestingState(id, reply))

let private behindApp : SessionReload =
  SessionReload.Finished
    { Case = ReloadCase.PatchPending; Patched = 1; Considered = 1; Message = "m"; SuggestedAction = ""
      Mechanism = SageFs.Features.ReloadOutcome.PatchMechanism.MetadataDelta; Declarations = [ "Handlers.describe" ]
      Callers = SageFs.Features.CallerState.CallersState.CallersCurrent }

[<Tests>]
let tests =
  testList "SessionManager can fetch a live session's discovery and instrumentation maps again" [
    testTask "WHY — a refetch asks the worker for its tests and its maps once each, tells the daemon once each, and leaves the REPL's freshness alone" {
      do! withHarness (fun harness proxy -> task {
        let! info, _ = readySession harness proxy
        harness.Mailbox.Post(SessionCommand.ReloadObserved(info.Id, behindApp))
        let before = (sessionOf harness info.Id).Info.Freshness
        before |> Expect.notEqual "the fixture put the REPL behind its app, so a reset would show" ReplFreshness.InSync
        refetch harness info.Id |> Expect.equal "a Ready session accepts the refetch" (Ok ())
        let! _ = harness.Discoveries.WaitAsync TestTimeouts.patienceInProcess
        let! _ = harness.Maps.WaitAsync TestTimeouts.patienceInProcess
        do! Task.Delay TestTimeouts.settle
        harness.DiscoveryAsks.Value |> Expect.equal "the worker was asked for its tests twice in all: once on ready, once on the refetch" 2
        harness.MapAsks.Value |> Expect.equal "and for its maps twice in all" 2
        harness.Discoveries.CurrentCount |> Expect.equal "no second discovery callback beyond the refetch's one" 0
        harness.Maps.CurrentCount |> Expect.equal "no second maps callback beyond the refetch's one" 0
        (sessionOf harness info.Id).Info.Freshness |> Expect.equal "freshness is untouched" before })
    }

    testTask "WHY — a refetch whose reply arrives after the worker was replaced is dropped, as a stale Ready is" {
      do! withHarness (fun harness proxy -> task {
        let! info, _ = readySession harness proxy
        let gate = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
        harness.HoldReplies.Value <- Some gate
        refetch harness info.Id |> Expect.equal "accepted while the worker is current" (Ok ())
        let! _ = harness.Asked.WaitAsync TestTimeouts.patienceInProcess
        let! _ = harness.Asked.WaitAsync TestTimeouts.patienceInProcess
        // The worker is replaced while the old one still owes its answer.
        let! restarted = harness.Mailbox.PostAndAsyncReply(fun reply -> SessionCommand.RestartSession(info.Id, RestartPlan.RespawnOnly, reply))
        restarted |> Expect.isOk "the replacement was accepted"
        gate.SetResult()
        // Both replies came from the old worker, and both are stale.
        do! Task.Delay TestTimeouts.settle
        harness.Mailbox.PostAndReply(fun reply -> SessionCommand.GetSession(info.Id, reply)) |> ignore
        harness.Discoveries.CurrentCount |> Expect.equal "the old worker's tests never reach the daemon" 0
        harness.Maps.CurrentCount |> Expect.equal "nor do its maps" 0 })
    }

    testTask "WHY — a refetch for a session whose worker is not Ready is refused and says why, not ignored" {
      do! withHarness (fun harness _ -> task {
        let info = createSession harness
        match refetch harness info.Id with
        | Error (WorkerPostReady.RefetchRefusal.WorkerNotReady _) -> ()
        | other -> failtestf "expected a WorkerNotReady refusal for a session still starting, got %A" other
        harness.DiscoveryAsks.Value |> Expect.equal "the worker was not asked anything" 0 })
    }

    testTask "WHY — a refetch while a replacement worker is warming is refused as a swap in progress" {
      do! withHarness (fun harness proxy -> task {
        let! info, _ = readySession harness proxy
        let! restarted = harness.Mailbox.PostAndAsyncReply(fun reply -> SessionCommand.RestartSession(info.Id, RestartPlan.RespawnOnly, reply))
        restarted |> Expect.isOk "the replacement was accepted"
        match refetch harness info.Id with
        | Error WorkerPostReady.RefetchRefusal.SwapInProgress -> ()
        | other -> failtestf "expected SwapInProgress, got %A" other })
    }

    testTask "WHY — a refetch for a session that does not exist is refused as no such session" {
      do! withHarness (fun harness _ -> task {
        match refetch harness (SessionId.newId ()) with
        | Error WorkerPostReady.RefetchRefusal.NoSuchSession -> ()
        | other -> failtestf "expected NoSuchSession, got %A" other })
    }
  ]
