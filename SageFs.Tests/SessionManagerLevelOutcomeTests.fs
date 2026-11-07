/// A save that puts a session's REPL behind its app makes the manager ask the worker to bring its FSI host up to the patched
/// build, and the session says `Leveling` until the worker has answered and the daemon holds the new host's tests and maps.
///
/// The real mailbox, with only process spawn faked (the runtime of SwapRefetchTests) and a proxy the test scripts: it answers
/// `LevelRepl`, the two fetches and a hard reset, counts what it was asked, and can hold an answer back.
module SageFs.Tests.SessionManagerLevelOutcomeTests

open System
open System.Diagnostics
open System.Threading
open System.Threading.Tasks
open Expecto
open Expecto.Flip
open SageFs
open SageFs.SessionManager
open SageFs.WorkerProtocol
open SageFs.Features.ReloadOutcome

type private Harness =
  { Mailbox: MailboxProcessor<SessionCommand>
    /// One release each time the manager says something changed for a surface to redraw.
    Progress: SemaphoreSlim
    LevelAsks: int ref
    DiscoveryAsks: int ref
    MapAsks: int ref
    /// One release per fetch that reached the worker.
    FetchAsked: SemaphoreSlim
    /// What the worker answers a `LevelRepl` with.
    LevelAnswer: LevelAnswer ref
    /// While set, a `LevelRepl` is held until it completes.
    HoldLevel: TaskCompletionSource<unit> option ref
    /// Released when a `LevelRepl` reaches the worker.
    LevelAsked: SemaphoreSlim
    /// Makes the discovery fetch throw.
    FailDiscovery: bool ref
    /// Whether the landing gate drives the session.
    GateDriven: bool ref }

let private mkRuntime () : SessionManagerRuntime =
  { StartWorkerProcess =
      fun _ _ _ _ _ _ -> Ok ({ Process = Process.GetCurrentProcess(); AdoptedCore = None } : SpawnedWorker)
    AwaitWorkerPort = fun _ _ _ _ _ -> ()
    Ledger = StartLedger.closed
    StopWorker = fun _ -> async { return () }
    RunBuildAsync = fun _ _ -> async { return Ok "build ok" } }

let private resolvedRole : ProjectLoading.ClassifiedProject =
  { Path = "/repo/Foo.fsproj"; Role = ProjectLoading.ProjectRole.Library; PackageRefs = []; LoadMode = ProjectLoading.LoadMode.Evaluated; Build = SageFs.BuildOptimization.Unoptimized }

let private withHarness (run: Harness -> Task) : Task =
  task {
    use cancellation = new CancellationTokenSource()
    let progress = new SemaphoreSlim(0)
    let gateDriven = ref false
    let callbacks =
      { SessionManagerCallbacks.silent with
          OnSessionProgressChanged = fun () -> progress.Release() |> ignore
          WhoDrives = fun _ -> (match gateDriven.Value with | true -> SessionDriver.LandingGate | false -> SessionDriver.Everyone) }
    let mailbox, _ = createWithAlarm (mkRuntime ()) cancellation.Token callbacks
    let harness =
      { Mailbox = mailbox
        Progress = progress
        LevelAsks = ref 0
        DiscoveryAsks = ref 0
        MapAsks = ref 0
        FetchAsked = new SemaphoreSlim(0)
        LevelAnswer = ref LevelAnswer.Leveled
        HoldLevel = ref None
        LevelAsked = new SemaphoreSlim(0)
        FailDiscovery = ref false
        GateDriven = gateDriven }
    try
      do! run harness
    finally
      try mailbox.PostAndReply(fun reply -> SessionCommand.StopAll reply) with _ -> ()
      cancellation.Cancel()
  }

let private proxyFor (h: Harness) : SessionProxy =
  fun (msg: WorkerMessage) ->
    async {
      match msg with
      | WorkerMessage.LevelRepl rid ->
        Interlocked.Increment h.LevelAsks |> ignore
        h.LevelAsked.Release() |> ignore
        match h.HoldLevel.Value with
        | Some gate -> do! gate.Task |> Async.AwaitTask
        | None -> ()
        return WorkerResponse.LevelResult(rid, h.LevelAnswer.Value)
      | WorkerMessage.GetTestDiscovery _ ->
        Interlocked.Increment h.DiscoveryAsks |> ignore
        h.FetchAsked.Release() |> ignore
        match h.FailDiscovery.Value with
        | true -> return failwith "the worker's discovery blew up"
        | false -> return WorkerResponse.InitialTestDiscovery([||], [])
      | WorkerMessage.GetInstrumentationMaps rid ->
        Interlocked.Increment h.MapAsks |> ignore
        h.FetchAsked.Release() |> ignore
        return WorkerResponse.InstrumentationMapsResult(rid, [||])
      | WorkerMessage.HardResetSession(_, rid) -> return WorkerResponse.HardResetResult(rid, Result.Ok "Hard reset complete.")
      | WorkerMessage.GetStatus rid ->
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

let private sessionOf (h: Harness) (id: SessionId) : ManagedSession =
  match h.Mailbox.PostAndReply(fun reply -> SessionCommand.GetSession(id, reply)) with
  | Some session -> session
  | None -> failtestf "expected session %s to exist" (SessionId.value id)

let private freshnessOf (h: Harness) (id: SessionId) : ReplFreshness = (sessionOf h id).Info.Freshness

/// A session whose worker is installed and Ready, with the ready's own fetches finished.
let private within (pending: Task<'a>) : Task<'a> =
  task {
    let! first = Task.WhenAny(pending :> Task, Task.Delay TestTimeouts.patienceInProcess)
    match obj.ReferenceEquals(first, pending) with
    | true -> return! pending
    | false -> return failtestf "did not happen within %A" TestTimeouts.patienceInProcess
  }

/// The same bound for a task that has no result (a semaphore's wait).
let private withinSignal (pending: Task) : Task =
  task {
    let! first = Task.WhenAny(pending, Task.Delay TestTimeouts.patienceInProcess)
    match obj.ReferenceEquals(first, pending) with
    | true -> do! pending
    | false -> failtestf "did not happen within %A" TestTimeouts.patienceInProcess
  } :> Task

let private withinSignal2 (pending: Task<WorkerResponse>) : Task<WorkerResponse> = within pending

let private readySession (h: Harness) : Task<SessionId * int> =
 task {
  let info =
    match h.Mailbox.PostAndReply(fun reply ->
      SessionCommand.CreateSession([ SessionProjectTarget.Project "Foo.fsproj" ], "/nonexistent/does-not-matter", true, WorkflowTypes.SessionWorkflow.Interactive, reply)) with
    | Ok info -> info
    | Error err -> failtestf "create session failed: %s" (SageFsError.describe err)
  let pid = SessionLifecycleStatus.workerPid (sessionOf h info.Id).Info.Status |> Option.defaultWith (fun () -> failtest "expected a worker pid")
  h.Mailbox.Post(SessionCommand.WorkerReady(info.Id, pid, "http://localhost:4123", proxyFor h))
  h.Mailbox.Post(SessionCommand.WorkerReportedReady(info.Id, pid, [ resolvedRole ]))
  // The ready's own two fetches, off the tally, so a case counts only its own.
  do! withinSignal (h.FetchAsked.WaitAsync())
  do! withinSignal (h.FetchAsked.WaitAsync())
  return info.Id, pid
 }

let private deltaPending (declaration: string) : SessionReload =
  SessionReload.Finished
    { Case = ReloadCase.PatchPending
      Patched = 1
      Considered = 1
      Message = "m"
      SuggestedAction = ""
      Mechanism = PatchMechanism.MetadataDelta
      Declarations = [ declaration ]
      Callers = SageFs.Features.CallerState.CallersState.CallersCurrent }

/// Wait for the manager to say something changed until the session is in the state wanted.
let private untilFreshness (h: Harness) (id: SessionId) (wanted: ReplFreshness -> bool) : Task<ReplFreshness> =
  task {
    let mutable current = freshnessOf h id
    while not (wanted current) do
      do! withinSignal (h.Progress.WaitAsync())
      current <- freshnessOf h id
    return current
  }

let private isInSync (f: ReplFreshness) = (f = ReplFreshness.InSync)

[<Tests>]
let tests =
  testList "SessionManager brings a session's host level after a delta" [

    testTask "WHY - a delta report on a Ready worker makes the manager ask the worker to level, and the session is InSync only after the tests and maps were fetched again from that worker" {
      do! withHarness (fun h -> task {
        let! id, _ = readySession h
        let gate = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
        h.HoldLevel.Value <- Some gate
        let discoveriesBefore = h.DiscoveryAsks.Value
        let mapsBefore = h.MapAsks.Value
        h.Mailbox.Post(SessionCommand.ReloadObserved(id, deltaPending "Handlers.describe"))
        do! withinSignal (h.LevelAsked.WaitAsync())
        match freshnessOf h id with
        | ReplFreshness.Leveling (1, [ "Handlers.describe" ]) -> ()
        | other -> failtestf "while the worker is leveling the state says so, got %A" other
        h.DiscoveryAsks.Value |> Expect.equal "nothing is fetched until the worker has replaced its host" discoveriesBefore
        gate.SetResult()
        let! final = untilFreshness h id isInSync
        final |> Expect.equal "level" ReplFreshness.InSync
        h.LevelAsks.Value |> Expect.equal "asked once" 1
        h.DiscoveryAsks.Value |> Expect.equal "the tests were fetched again, once" (discoveriesBefore + 1)
        h.MapAsks.Value |> Expect.equal "and the maps, once" (mapsBefore + 1) })
    }

    testTask "WHY - a worker that defers is believed: the state names what stopped it, and nothing is fetched" {
      do! withHarness (fun h -> task {
        let! id, _ = readySession h
        let blockers = [ LevelBlocker.ReplHoldsDefinitions 2; LevelBlocker.InitScriptDefined "init.fsx" ]
        h.LevelAnswer.Value <- LevelAnswer.LevelDeferred blockers
        let discoveriesBefore = h.DiscoveryAsks.Value
        h.Mailbox.Post(SessionCommand.ReloadObserved(id, deltaPending "Handlers.describe"))
        let! final = untilFreshness h id (function ReplFreshness.CannotLevel _ -> true | _ -> false)
        final |> Expect.equal "cannot level" (ReplFreshness.CannotLevel (1, [ "Handlers.describe" ], blockers))
        h.DiscoveryAsks.Value |> Expect.equal "no refetch for a host that was not replaced" discoveriesBefore })
    }

    testTask "WHY - a session the landing gate drives is never asked, and says that is why" {
      do! withHarness (fun h -> task {
        let! id, _ = readySession h
        h.GateDriven.Value <- true
        h.Mailbox.Post(SessionCommand.ReloadObserved(id, deltaPending "Handlers.describe"))
        // The reload is the mailbox's next command, so a round trip proves it was processed.
        freshnessOf h id
        |> Expect.equal "blocked by the gate" (ReplFreshness.CannotLevel (1, [ "Handlers.describe" ], [ LevelBlocker.GateDriven ]))
        h.LevelAsks.Value |> Expect.equal "the worker was never asked" 0 })
    }

    testTask "WHY - a session whose worker is not Ready is not asked, and the state says it is not serving" {
      do! withHarness (fun h -> task {
        let! id, _ = readySession h
        h.Mailbox.Post(SessionCommand.UpdateSessionStatus(id, SessionLifecycleStatus.Faulted (FaultReason.Reported "worker died")))
        h.Mailbox.Post(SessionCommand.ReloadObserved(id, deltaPending "Handlers.describe"))
        freshnessOf h id
        |> Expect.equal "not serving" (ReplFreshness.CannotLevel (1, [ "Handlers.describe" ], [ LevelBlocker.NotServing ]))
        h.LevelAsks.Value |> Expect.equal "not asked" 0 })
    }

    testTask "WHY - a save that lands while the level runs reopens the gap, no second ask goes out meanwhile, and the answer to the first does not clear it: the manager asks again and ends level" {
      do! withHarness (fun h -> task {
        let! id, _ = readySession h
        let gate = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
        h.HoldLevel.Value <- Some gate
        h.Mailbox.Post(SessionCommand.ReloadObserved(id, deltaPending "Handlers.describe"))
        do! withinSignal (h.LevelAsked.WaitAsync())
        h.Mailbox.Post(SessionCommand.ReloadObserved(id, deltaPending "Handlers.makeHeld"))
        freshnessOf h id
        |> Expect.equal "behind again, by two saves" (ReplFreshness.BehindApp (2, [ "Handlers.describe"; "Handlers.makeHeld" ]))
        h.LevelAsks.Value |> Expect.equal "the first ask is still the only one" 1
        // The second ask is answered at once: the held gate is released for both.
        h.HoldLevel.Value <- None
        gate.SetResult()
        let! final = untilFreshness h id isInSync
        final |> Expect.equal "level" ReplFreshness.InSync
        h.LevelAsks.Value |> Expect.equal "asked again for the save that landed meanwhile" 2 })
    }

    testTask "WHY - an answer from a worker the session no longer has is dropped: the state is not moved by it" {
      do! withHarness (fun h -> task {
        let! id, pid = readySession h
        let gate = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
        h.HoldLevel.Value <- Some gate
        h.Mailbox.Post(SessionCommand.ReloadObserved(id, deltaPending "Handlers.describe"))
        do! withinSignal (h.LevelAsked.WaitAsync())
        let before = freshnessOf h id
        before |> Expect.equal "leveling, the worker has not answered" (ReplFreshness.Leveling (1, [ "Handlers.describe" ]))
        h.Mailbox.Post(SessionCommand.LevelAnswered(id, pid + 1000, LevelAnswer.Leveled))
        freshnessOf h id |> Expect.equal "unchanged by a stranger's answer" before
        gate.SetResult() })
    }

    testTask "WHY - a hard reset of the worker's own host that resolves Ok brings a session that was behind to level, once the tests and maps are fetched again, whoever asked for it" {
      do! withHarness (fun h -> task {
        let! id, _ = readySession h
        h.GateDriven.Value <- true
        h.Mailbox.Post(SessionCommand.ReloadObserved(id, deltaPending "Handlers.describe"))
        // The editor's reset goes through the session's own proxy.
        let proxy = (sessionOf h id).Proxy
        let discoveriesBefore = h.DiscoveryAsks.Value
        let! reply = within (proxy (WorkerMessage.HardResetSession(false, "rid")) |> Async.StartAsTask)
        match reply with
        | WorkerResponse.HardResetResult(_, Result.Ok _) -> ()
        | other -> failtestf "the reset should resolve Ok, got %A" other
        let! final = untilFreshness h id isInSync
        final |> Expect.equal "level" ReplFreshness.InSync
        h.DiscoveryAsks.Value |> Expect.equal "the tests were fetched again" (discoveriesBefore + 1) })
    }

    testTask "WHY - a level that answers Leveled is not taken as a rebuilt host by the session's proxy: the manager judges that answer itself, because a save may have landed after the host was built" {
      do! withHarness (fun h -> task {
        let! id, _ = readySession h
        h.GateDriven.Value <- true
        h.Mailbox.Post(SessionCommand.ReloadObserved(id, deltaPending "Handlers.describe"))
        let before = freshnessOf h id
        before |> Expect.equal "behind, and not asked" (ReplFreshness.CannotLevel (1, [ "Handlers.describe" ], [ LevelBlocker.GateDriven ]))
        let! reply = withinSignal2 ((sessionOf h id).Proxy (WorkerMessage.LevelRepl "rid") |> Async.StartAsTask)
        match reply with
        | WorkerResponse.LevelResult(_, LevelAnswer.Leveled) -> ()
        | other -> failtestf "the fake worker should answer Leveled, got %A" other
        // The proxy posts what it saw before the caller hears the answer, so this round trip is after it.
        freshnessOf h id |> Expect.equal "the proxy did not clear it" before })
    }

    testTask "WHY - a fetch that fails after the host was replaced leaves the state Unknown with the reason, never level" {
      do! withHarness (fun h -> task {
        let! id, _ = readySession h
        h.FailDiscovery.Value <- true
        h.Mailbox.Post(SessionCommand.ReloadObserved(id, deltaPending "Handlers.describe"))
        let! final = untilFreshness h id (function ReplFreshness.Unknown _ -> true | _ -> false)
        match final with
        | ReplFreshness.Unknown (FreshnessUnknown.LevelFailed reason) -> reason |> Expect.stringContains "says what failed" "discovery"
        | other -> failtestf "expected Unknown(LevelFailed), got %A" other })
    }

    testTask "WHY - a worker that is replaced is built fresh, so the state is level and an answer to the old worker's ask changes nothing" {
      do! withHarness (fun h -> task {
        let! id, _ = readySession h
        let gate = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
        h.HoldLevel.Value <- Some gate
        h.Mailbox.Post(SessionCommand.ReloadObserved(id, deltaPending "Handlers.describe"))
        do! withinSignal (h.LevelAsked.WaitAsync())
        let! _ = h.Mailbox.PostAndAsyncReply(fun reply -> SessionCommand.RestartSession(id, RestartPlan.RespawnOnly, reply))
        freshnessOf h id |> Expect.equal "the replacement is level" ReplFreshness.InSync
        gate.SetResult()
        // A round trip through the mailbox after the old answer was posted.
        do! Task.Delay TestTimeouts.settle
        freshnessOf h id |> Expect.equal "and the old worker's answer left it so" ReplFreshness.InSync })
    }
  ]
