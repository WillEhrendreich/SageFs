/// Roadmap "Level the REPL after a patch without losing it", the swap-refetch slice. After ANY in-worker FSI
/// host swap the daemon must re-fetch that session's test discovery and its instrumentation maps, and the
/// worker must answer with the maps of the copy its FSI host NOW runs. Neither half existed: a hard reset
/// re-instrumented a fresh shadow copy and then only logged the fresh maps (`AppState.fs`'s EvalHardReset),
/// so `GetInstrumentationMaps` kept serving the array frozen at actor creation; and no production code posted
/// `RefetchLiveTestingState` — only `WorkerPostReadyRefetchTests`' own harness did, which is what its header
/// says it was waiting for. These cases fail against the unfixed tree for exactly those two reasons.
module SageFs.Tests.SwapRefetchTests

open System
open System.IO
open System.Collections.Concurrent
open System.Diagnostics
open System.Threading
open System.Threading.Tasks
open Expecto
open Expecto.Flip
open SageFs
open SageFs.ActorCreation
open SageFs.AppState
open SageFs.SessionManager
open SageFs.WorkerProtocol
open SageFs.Tests
open SageFs.Tests.TestInfrastructure
open SageFs.McpTools

module Integration = SageFs.Tests.TestInfrastructure.Integration

/// Bound a pending task: a real hard reset disposes a host, re-shadows and re-warms, so the bound is the
/// cold-build budget — a passing case never spends it (same helper shape as HostCrashRecoveryTests).
let private within (bound: TimeSpan) (pending: Task<'a>) : Task<'a> =
  task {
    let! first = Task.WhenAny(pending :> Task, Task.Delay bound)
    match obj.ReferenceEquals(first, pending) with
    | true -> return! pending
    | false -> return failtestf "did not complete within %A" bound
  }

// ─── Worker half ───────────────────────────────────────────────────

/// A REAL compiled project with no package references, built as a dependency of this test project, so a
/// session on it instruments to non-empty maps — the precondition the worker-half case measures against.
let private fsharpCoreFixtureProject =
  Path.Combine(RepoPaths.repoPathFull [||], "SageFs.Tests", "fixtures", "FSharpCoreIdentityFixture", "FSharpCoreIdentityFixture.fsproj")

[<Tests>]
let workerHalfTests =
  Integration.hostList "Swap refetch (worker half): the worker answers GetInstrumentationMaps for the copy its FSI host NOW runs" [
    testTask "WHY — after WorkerMessage.HardResetSession(false, _) resolves Ok, GetInstrumentationMaps serves the fresh copy's array, not the array captured at actor creation" {
      let loadConfig =
        { SageFs.Args.ProjectLoadConfig.empty with
            Targets = [ SessionProjectTarget.Project fsharpCoreFixtureProject ]
            WorkingDir = Path.GetDirectoryName fsharpCoreFixtureProject }
      // Isolated, like every production worker: the FSI host is a separate process. The
      // instrumentation this case is about happens HERE, in the actor's own process — which is
      // why the case can compare arrays by identity at all (a wire answer serializes them).
      let args =
        { mkCommonActorArgs quietLogger false ignore loadConfig with FsiKind = SessionKinds.Isolated }
      let! session =
        within TestTimeouts.sessionReadyColdBuild (createActor args |> Async.AwaitTask |> Async.StartAsTask)
      // Capture the creation-time array through the live holder — the actor returns a ref, not a bare
      // array, because an in-worker hard reset re-instruments a FRESH shadow copy into the same holder.
      let creationMaps = session.InstrumentationMapsRef.Value
      creationMaps
      |> Expect.isNonEmpty "the fixture must instrument to real maps, or this case measures nothing"
      let! reset =
        within TestTimeouts.sessionReadyColdBuild (
          session.Actor.PostAndAsyncReply(fun reply -> HardResetSession(false, reply)) |> Async.StartAsTask)
      match reset with
      | Ok _ -> ()
      | Error err -> failtestf "the in-worker hard reset must resolve Ok, got: %s" (SageFsError.describe err)
      // What WorkerMain's ready handler answers a GetInstrumentationMaps request from. The hard
      // reset re-instrumented a FRESH shadow copy, so the honest answer is THAT array; serving
      // the creation-time one is the frozen-field bug (probe numbering changed under it).
      Object.ReferenceEquals(session.InstrumentationMapsRef.Value, creationMaps)
      |> Expect.isFalse "after an in-worker hard reset the worker must serve the fresh copy's maps, not the array captured at actor creation"
    }
  ]

// ─── Daemon half ───────────────────────────────────────────────────

/// What the daemon heard from the manager, and what the worker was asked. The same fake runtime as
/// WorkerPostReadyRefetchTests (only process spawn faked), plus what the MCP reset tools need: the real
/// snapshot reader and a reset answer the test chooses.
type private Harness =
  { Mailbox: MailboxProcessor<SessionCommand>
    /// The lock-free CQRS reader the production SessionOps are built on.
    ReadSnapshot: unit -> QuerySnapshot
    Discoveries: SemaphoreSlim
    Maps: SemaphoreSlim
    DiscoveryAsks: int ref
    MapAsks: int ref
    HoldReplies: TaskCompletionSource<unit> option ref
    Asked: SemaphoreSlim
    /// What the worker answers a routed ResetSession with — Ok for the resolving case, an Error for the
    /// one that must post no refetch at all.
    ResetOutcome: Result<unit, SageFsError> ref }

let private mkRuntime () : SessionManagerRuntime =
  { StartWorkerProcess =
      fun _ _ _ _ _ _ -> Ok ({ Process = Process.GetCurrentProcess(); AdoptedCore = None } : SpawnedWorker)
    AwaitWorkerPort = fun _ _ _ _ _ -> ()
    Ledger = StartLedger.closed
    StopWorker = fun _ -> async { return () }
    RunBuildAsync = fun _ _ -> async { return Ok "build ok" } }

let private withHarness (run: Harness -> SessionProxy -> Task) : Task =
  task {
    use cancellation = new Threading.CancellationTokenSource()
    let discoveries = new SemaphoreSlim(0)
    let maps = new SemaphoreSlim(0)
    let discoveryAsks = ref 0
    let mapAsks = ref 0
    let hold : TaskCompletionSource<unit> option ref = ref None
    let asked = new SemaphoreSlim(0)
    let resetOutcome = ref (Ok ())
    let callbacks =
      { SessionManagerCallbacks.silent with
          OnTestDiscovery = fun _ _ -> discoveries.Release() |> ignore
          OnInstrumentationMaps = fun _ _ -> maps.Release() |> ignore }
    let mailbox, readSnapshot = createWithAlarm (mkRuntime ()) cancellation.Token callbacks
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
          | WorkerMessage.ResetSession rid ->
            return WorkerResponse.ResetResult(rid, resetOutcome.Value)
          | WorkerMessage.GetStatus rid ->
            // Never Ready from here: the tests post WorkerReportedReady themselves, so the watchdog
            // stays out of the way.
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
    let harness =
      { Mailbox = mailbox
        ReadSnapshot = readSnapshot
        Discoveries = discoveries
        Maps = maps
        DiscoveryAsks = discoveryAsks
        MapAsks = mapAsks
        HoldReplies = hold
        Asked = asked
        ResetOutcome = resetOutcome }
    try
      do! run harness proxy
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

/// A session whose worker is installed and has reported Ready; returns after the first (post-ready)
/// discovery and maps fetches have both been published and been counted, so a case counts only its own.
let private readySession (harness: Harness) (proxy: SessionProxy) : Task<SessionInfo> =
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
    return info
  }

/// The MCP context the reset tools run in, on the REAL production SessionOps builder
/// (`createSessionOpsWithRecovery` — its GetSessionInfo reads the real snapshot, its status writes post to
/// the real mailbox), with only the proxy pointed at this harness's counting worker.
let private mcpCtxFor (harness: Harness) (proxy: SessionProxy) (sid: SessionId) : McpContext * (unit -> unit) =
  let manifestDir = Path.Combine(Path.GetTempPath(), "sagefs-swap-refetch-" + Guid.NewGuid().ToString("N"))
  let manifest = SageFs.Features.ManifestOwner.start quietLogger manifestDir
  let ops =
    { SageFs.Server.DaemonMode.createSessionOpsWithRecovery harness.Mailbox harness.ReadSnapshot manifest None with
        GetProxy = fun _ -> Task.FromResult(Some proxy) }
  let sessionMap = ConcurrentDictionary<string, string>()
  sessionMap.["agent1"] <- SessionId.value sid
  let ctx : McpContext =
    { FrictionStore = None
      DiagnosticsChanged = Event<Features.DiagnosticsStore.T>().Publish
      StateChanged = None; SessionOps = ops; SessionMap = sessionMap; McpPort = 0
      Dispatch = None
      GetElmModel = None; GetElmRegions = None; GetWarmupContext = None; GetFeatureState = None; RecordEval = None
      ActivityTracker = AgentActivityTracker.create (); LiveBindings = None; CohortSupport = SageFs.Features.CohortOwners.Wiring.Unwired
      GetDaemonHealth = fun () -> None
      GetProcessTelemetry = fun () -> None }
  let cleanup () =
    (manifest :> IDisposable).Dispose()
    try IO.Directory.Delete(manifestDir, true) with _ -> ()
  ctx, cleanup

[<Tests>]
let daemonHalfTests =
  testList "Swap refetch (daemon half): one reset resolving Ok posts exactly one further ask of each kind, and one publish of each" [
    testTask "WHY — reset_session resolving Ok asks the worker for its tests and its maps exactly once more, and publishes each exactly once more" {
      do! withHarness (fun harness proxy -> task {
        let! info = readySession harness proxy
        let ctx, cleanup = mcpCtxFor harness proxy info.Id
        try
          let sidStr = SessionId.value info.Id
          let! reply = within TestTimeouts.patienceInProcess (resetSession ctx "agent1" (Some sidStr) None)
          reply |> Expect.stringContains "the reset resolved Ok" "Session reset successfully"
          // The refetch is posted fire-and-forget; give it the in-process bound to produce its two asks.
          let! _ =
            awaitCondition (TestTimeouts.asMs TestTimeouts.patienceInProcess)
              (fun () -> harness.DiscoveryAsks.Value = 2 && harness.MapAsks.Value = 2)
          do! Task.Delay TestTimeouts.settle
          harness.DiscoveryAsks.Value
          |> Expect.equal "one reset resolving Ok produces exactly one further GetTestDiscovery ask: the ready's one plus the refetch's" 2
          harness.MapAsks.Value
          |> Expect.equal "and exactly one further GetInstrumentationMaps ask: the ready's one plus the refetch's" 2
          harness.Discoveries.CurrentCount
          |> Expect.equal "exactly one further discovery publish, and nothing after it" 1
          harness.Maps.CurrentCount
          |> Expect.equal "exactly one further maps publish, and nothing after it" 1
        finally cleanup () })
    }

    testTask "WHY — a reset that did NOT resolve Ok posts no refetch at all: there is no fresh copy to ask about" {
      do! withHarness (fun harness proxy -> task {
        let! info = readySession harness proxy
        harness.ResetOutcome.Value <- Error (SageFsError.ResetFailed "the reset failed on purpose")
        let ctx, cleanup = mcpCtxFor harness proxy info.Id
        try
          let sidStr = SessionId.value info.Id
          let! reply = within TestTimeouts.patienceInProcess (resetSessionResult ctx "agent1" (Some sidStr) None)
          match reply with
          | Error (SageFsError.ResetFailed _) -> ()
          | other -> failtestf "expected the reset's own typed failure, got %A" other
          // A long settle for an absent event: a post that WOULD come has this long to show up.
          do! Task.Delay TestTimeouts.repeatActionEvery
          harness.DiscoveryAsks.Value
          |> Expect.equal "a failed reset posts no refetch: the worker was asked once, by the ready" 1
          harness.MapAsks.Value
          |> Expect.equal "and no maps refetch either" 1
          harness.Discoveries.CurrentCount
          |> Expect.equal "no discovery publish beyond the ready's, which was drained" 0
          harness.Maps.CurrentCount
          |> Expect.equal "no maps publish beyond the ready's, which was drained" 0
        finally cleanup () })
    }
  ]
