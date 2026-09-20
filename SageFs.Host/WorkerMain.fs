module SageFs.Server.WorkerMain

open System
open System.Threading
open SageFs
open SageFs.Utils
open SageFs.WorkerProtocol
open SageFs.AppState
open SageFs.WarmUp

/// Parent-death monitor: workers self-exit when the daemon dies.
/// When the daemon is killed hard (Task Manager, taskkill /F, crash, OS
/// shutdown), no Shutdown message ever arrives — without this, worker
/// processes become orphans that outlive the daemon (issue #126).
///
/// The pid-and-start-time-fenced decision core now lives in Core
/// (`SageFs.OwnerMonitor`) so any owned process — worker, Run App child,
/// `dotnet build`, or an externally-spawned daemon — can share it. This
/// module is a thin, name-compatible wrapper kept for existing callers and
/// tests that only ever had a bare pid (no fence, i.e. pid-reuse is NOT
/// closed for these entry points — see the real worker usage below, which
/// builds a fenced `OwnerMonitor.Owner` directly and does not go through
/// this wrapper).
module ParentMonitor =
  /// Decide whether the given daemon PID is still alive (pid-only, no
  /// start-time fence — see `SageFs.OwnerMonitor.isAlive` for the fenced
  /// version used by the real watchdog below).
  let isDaemonAlive (getProcessById: int -> System.Diagnostics.Process option) (daemonPid: int) : bool =
    SageFs.OwnerMonitor.isAlive getProcessById (SageFs.OwnerMonitor.Owner.ofPid daemonPid)

  /// Poll interval between daemon liveness checks.
  let pollIntervalMs = SageFs.OwnerMonitor.pollIntervalMs

  /// Run the monitor loop (pid-only fence). Cancels the provided CTS when
  /// the daemon dies, which unwinds the worker's main wait (tcs in
  /// WorkerMain.run) and shuts down the HTTP server, file watcher, and actor.
  let run
    (getProcessById: int -> System.Diagnostics.Process option)
    (daemonPid: int)
    (cts: CancellationTokenSource)
    (log: string -> unit)
    : Async<unit> =
    SageFs.OwnerMonitor.run getProcessById (SageFs.OwnerMonitor.Owner.ofPid daemonPid) cts log

  /// Real process lookup for production use.
  let getProcessById (pid: int) : System.Diagnostics.Process option =
    SageFs.OwnerMonitor.getProcessById pid

/// Convert internal Diagnostic to WorkerDiagnostic for transport.
let toWorkerDiagnostic (d: Features.Diagnostics.Diagnostic) : WorkerDiagnostic =
  { Severity = d.Severity
    Message = d.Message
    StartLine = d.Range.StartLine
    StartColumn = d.Range.StartColumn
    EndLine = d.Range.EndLine
    EndColumn = d.Range.EndColumn
    ErrorNumber = d.ErrorNumber }

/// Convert internal SessionState + EvalStats to WorkerStatusSnapshot.
let toStatusSnapshot
  (state: SessionState)
  (stats: Affordances.EvalStats)
  (statusMsg: string option)
  (projects: SageFs.ProjectLoading.ClassifiedProject list)
  : WorkerStatusSnapshot =
  let avg =
    match stats.EvalCount > 0 with
    | true -> stats.TotalDuration.TotalMilliseconds / float stats.EvalCount |> int64
    | false -> 0L
  let status =
    match state with
    | SessionState.Uninitialized
    | SessionState.WarmingUp -> SessionStatus.Starting
    | SessionState.Ready -> SessionStatus.Ready
    | SessionState.Evaluating -> SessionStatus.Evaluating
    | SessionState.Faulted -> SessionStatus.Faulted
  { Status = status
    StatusMessage = statusMsg
    EvalCount = stats.EvalCount
    AvgDurationMs = avg
    MinDurationMs = stats.MinDuration.TotalMilliseconds |> int64
    MaxDurationMs = stats.MaxDuration.TotalMilliseconds |> int64
    Projects = projects }

let mergeInitialDiscoveryResults
  (results: Features.LiveTesting.LiveTestHookResult array)
  : Features.LiveTesting.TestCase array * Features.LiveTesting.ProviderDescription list =
  let tests =
    results
    |> Array.collect (fun r -> r.DiscoveredTests)
    |> Array.distinctBy (fun test -> test.Id)

  let providers =
    results
    |> Array.collect (fun r -> r.DetectedProviders |> List.toArray)
    |> Array.distinctBy (fun provider ->
      match provider with
      | Features.LiveTesting.ProviderDescription.AttributeBased d -> d.Name
      | Features.LiveTesting.ProviderDescription.Custom d -> d.Name)
    |> Array.toList

  tests, providers

let workerRuntimeCriticalAssemblyNames =
  set [
    "fsharp.core.dll"
    "fsharp.systemtextjson.dll"
    "mono.cecil.dll"
    "mono.cecil.rocks.dll"
    "mono.cecil.mdb.dll"
    "mono.cecil.pdb.dll"
  ]

let isWorkerRuntimeCritical (assemblyName: string) =
  workerRuntimeCriticalAssemblyNames
  |> Set.contains (assemblyName.ToLowerInvariant())

let shouldQuarantineAssembly (assemblyName: string) =
  not (assemblyName.EndsWith(".resources.dll", StringComparison.OrdinalIgnoreCase))
  && not (isWorkerRuntimeCritical assemblyName)

/// How the worker runs its session's executable project (see AppRunner).
type AppRunHandlers = {
  Run: string -> AppRun.PreviousAddress -> Async<Result<AppRun.AppRunState, SageFsError>>
  Stop: AppRun.StopScope -> Async<Result<AppRun.AppRunState, SageFsError>>
  AwaitChange: string -> Async<AppRun.AppRunState>
}

/// How a saved source file reaches the worker.
[<RequireQualifiedAccess>]
type private ReloadRoute =
  /// The file belongs to the running app: patch its functions in place, bound to
  /// the compiled types and state, or restart the app.
  | PatchRunningApp of baseline: Features.ReloadPlanning.FileDecls
  | ReevaluateFile

/// For hosts that do not run apps (test harnesses).
let noAppRuns : AppRunHandlers = {
  Run = fun project _ -> async { return Error (SageFsError.AppRunFailed (project, "This host does not run apps.")) }
  Stop = fun _ -> async { return Ok AppRun.AppRunState.NotRunning }
  AwaitChange = fun _ -> async { return AppRun.AppRunState.NotRunning }
}

/// The error a failed eval reports across the process boundary: a structured
/// SageFsError the actor raised (e.g. EvalSupersededByReset) keeps its case;
/// any other exception becomes `fallback` with the full exception text.
let rec private rootCause (e: exn) : exn =
  match box e.InnerException with
  | null -> e
  | _ -> rootCause e.InnerException

let private toWorkerError (fallback: string -> SageFsError) (ex: exn) : SageFsError =
  match ex with
  | :? SageFsErrorException as e -> e.Error
  | _ ->
    // Lead with the real (root-cause) exception type + message and the first
    // line of the user's own code in the stack, then the full text — so a
    // runtime failure reads like an actionable location, not a raw stack dump
    // (roast UX-3). The full ex.ToString() (with inner stacks) is kept below.
    let root = rootCause ex
    fallback (ErrorMessages.runtimeExceptionSummary (root.GetType().Name) root.Message (ex.ToString()))

/// Handle a single WorkerMessage by dispatching to the actor.
let handleMessage
  (actor: AppActor)
  (getState: unit -> SessionState)
  (getStats: unit -> Affordances.EvalStats)
  (getStatusMessage: unit -> string option)
  (projects: SageFs.ProjectLoading.ClassifiedProject list)
  (getRunTest: unit -> (Features.LiveTesting.TestCase -> Async<Features.LiveTesting.TestResult>))
  (setRunTest: (Features.LiveTesting.TestCase -> Async<Features.LiveTesting.TestResult>) -> unit)
  (getInitialDiscovery: unit -> Features.LiveTesting.TestCase array * Features.LiveTesting.ProviderDescription list)
  (evalLiveTestFile: string -> string -> Async<Result<Features.LiveTesting.TestCase array * Features.LiveTesting.ProviderDescription list, SageFsError>>)
  (appRuns: AppRunHandlers)
  (msg: WorkerMessage)
  : Async<WorkerResponse> =
  async {
    match msg with
    | WorkerMessage.EvalCode(code, replyId) ->
      let request = { Code = code; Args = Map.empty }
      // Bound the eval the same way the daemon's HTTP client does — a wedged
      // actor must not hang the eval forever even if the client disconnects
      // without cancelling. (Roast-2 item 11 worker side.)
      use cts = new CancellationTokenSource(Timeouts.workerHttpRequest)
      let! response =
        actor.PostAndAsyncReply(fun rc -> Eval(request, cts.Token, rc))
        |> Instrumentation.tracedActorPost Instrumentation.EvalCategory.Repl
      let diags = response.Diagnostics |> Array.map toWorkerDiagnostic |> Array.toList
      let result = response.EvaluationResult |> Result.mapError (toWorkerError SageFsError.EvalFailed)
      let metadata =
        response.Metadata
        |> Map.fold (fun acc k v ->
          match v with
          | :? SageFs.Features.LiveTesting.LiveTestHookResultDto as dto ->
            acc |> Map.add k (WorkerProtocol.Serialization.serialize dto)
          | _ -> acc) Map.empty
      // Capture RunTest closure from the latest discovery
      let metaKeys = response.Metadata |> Map.toList |> List.map fst |> String.concat ", "
      Log.debug "[WorkerMain] Metadata keys after eval: [%s]" metaKeys
      match response.Metadata |> Map.tryFind "liveTestRunTest" with
      | Some (:? (Features.LiveTesting.TestCase -> Async<Features.LiveTesting.TestResult>) as runTest) ->
        Log.debug "[WorkerMain] RunTest captured from eval metadata"
        setRunTest runTest
      | Some v ->
        Log.warn "[WorkerMain] liveTestRunTest found but wrong type: %s" (v.GetType().FullName)
      | None ->
        Log.debug "[WorkerMain] liveTestRunTest NOT found in metadata"
      return WorkerResponse.EvalResult(replyId, result, diags, metadata)

    | WorkerMessage.CheckCode(code, replyId) ->
      let! diags =
        actor.PostAndAsyncReply(fun rc -> GetDiagnostics(code, rc))
        |> Instrumentation.tracedActorPost Instrumentation.EvalCategory.Check
      let workerDiags = diags |> Array.map toWorkerDiagnostic |> Array.toList
      return WorkerResponse.CheckResult(replyId, workerDiags)

    | WorkerMessage.TypeCheckWithSymbols(code, filePath, replyId) ->
      let! result =
        actor.PostAndAsyncReply(fun rc -> GetTypeCheckWithSymbols(code, filePath, rc))
        |> Instrumentation.tracedActorPost Instrumentation.EvalCategory.Check
      let workerDiags = result.Diagnostics |> Array.map toWorkerDiagnostic |> Array.toList
      let workerSymRefs = result.SymbolRefs |> List.map WorkerProtocol.WorkerSymbolRef.fromDomain
      return WorkerResponse.TypeCheckWithSymbolsResult(replyId, workerDiags, workerSymRefs)

    | WorkerMessage.GetCompletions(code, cursorPos, replyId) ->
      let word = ""
      let! completions =
        actor.PostAndAsyncReply(fun rc -> Autocomplete(code, cursorPos, word, rc))
        |> Instrumentation.tracedActorPost Instrumentation.EvalCategory.Completion
      let names = completions |> List.map (fun c -> c.DisplayText)
      return WorkerResponse.CompletionResult(replyId, names)

    | WorkerMessage.CancelEval ->
      let! cancelled = actor.PostAndAsyncReply(fun rc -> CancelEval rc)
      return WorkerResponse.EvalCancelled cancelled

    | WorkerMessage.LoadScript(filePath, replyId) ->
      let code = sprintf "#load @\"%s\"" filePath
      let request = { Code = code; Args = Map.ofList ["hotReload", box true] }
      use cts = new CancellationTokenSource()
      let! response =
        actor.PostAndAsyncReply(fun rc -> Eval(request, cts.Token, rc))
        |> Instrumentation.tracedActorPost Instrumentation.EvalCategory.HotReload
      let result = response.EvaluationResult |> Result.mapError (toWorkerError SageFsError.ScriptLoadFailed)
      return WorkerResponse.ScriptLoaded(replyId, result)

    | WorkerMessage.ResetSession replyId ->
      let! result =
        actor.PostAndAsyncReply(fun rc -> ResetSession rc)
        |> Instrumentation.tracedActorPost Instrumentation.EvalCategory.Warmup
      return WorkerResponse.ResetResult(replyId, result)

    | WorkerMessage.HardResetSession(rebuild, replyId) ->
      let! result =
        actor.PostAndAsyncReply(fun rc -> HardResetSession(rebuild, rc))
        |> Instrumentation.tracedActorPost Instrumentation.EvalCategory.Warmup
      return WorkerResponse.HardResetResult(replyId, result)

    | WorkerMessage.GetStatus replyId ->
      let state = getState ()
      let stats = getStats ()
      return WorkerResponse.StatusResult(replyId, toStatusSnapshot state stats (getStatusMessage()) projects)

    | WorkerMessage.GetLiveValues replyId ->
      let! json = actor.PostAndAsyncReply(fun rc -> GetLiveValues rc)
      return WorkerResponse.LiveValuesResult(replyId, json)

    | WorkerMessage.RunTests(tests, maxParallelism, replyId) ->
      let runTest = getRunTest()
      let results = System.Collections.Concurrent.ConcurrentBag<Features.LiveTesting.TestRunResult>()
      use cts = new CancellationTokenSource(TimeSpan.FromSeconds(float (30 + tests.Length / 10)))
      do!
        Features.LiveTesting.TestOrchestrator.executeFiltered
          runTest (fun r -> results.Add r) maxParallelism tests cts.Token
      return WorkerResponse.TestRunResults(replyId, results.ToArray())

    | WorkerMessage.GetTestDiscovery(replyId) ->
      let tests, providers = getInitialDiscovery()
      return WorkerResponse.InitialTestDiscovery(tests, providers)

    | WorkerMessage.EvalLiveTestFile(filePath, content, replyId) ->
      let! result = evalLiveTestFile filePath content
      return WorkerResponse.EvalLiveTestFileResult(replyId, result)

    | WorkerMessage.GetInstrumentationMaps _ ->
      return WorkerResponse.InstrumentationMapsResult("", [||])

    | WorkerMessage.RunApp(project, previous, replyId) ->
      let! result = appRuns.Run project previous
      return WorkerResponse.AppRunResult(replyId, result)

    | WorkerMessage.StopApp(scope, replyId) ->
      let! result = appRuns.Stop scope
      return WorkerResponse.AppRunResult(replyId, result)

    | WorkerMessage.AwaitAppChange(runId, replyId) ->
      let! state = appRuns.AwaitChange runId
      return WorkerResponse.AppRunResult(replyId, Ok state)

    | WorkerMessage.Shutdown ->
      return WorkerResponse.WorkerShuttingDown
  }

/// Constructs the live-merged test-discovery view and the identity-preserving
/// buffer-eval handler for as-you-type live testing
/// (live-testing-asyoutype-plan.md Brief 3 — the keystone). Factored out of
/// `run` so it is directly testable against a real FSI actor
/// (e.g. TestInfrastructure's `globalActorResult`) without spinning up the
/// whole worker process (project loading, HTTP server, file watcher).
///
/// Returns:
///  - `getInitialDiscovery`: `GetTestDiscovery`'s live view — the compiled
///    baseline merged with whatever the latest FSI buffer eval discovered,
///    dynamic winning by `TestId` (Invariant 2, Brief 1's
///    `TestDiscoveryMerge.merge`). Before any buffer has been eval'd, the
///    dynamic side is empty and this is exactly the old frozen compiled set
///    (`merge x [||] = x`).
///  - `evalLiveTestFile`: eval a (possibly unsaved) editor buffer's content
///    through the SAME `parseFileStructureCached` + `preprocessForFsi
///    ... EvalMode.File` transform the on-disk file-watcher reload uses
///    (`onFileChanged`'s `ReloadRoute.ReevaluateFile` branch below) so the
///    re-eval'd test's `DeclaringType.FullName` — and therefore its
///    `TestId` — matches the compiled test's exactly (Invariant 5 of the
///    plan's §2): a raw `#load` or bare-body eval would break that identity
///    and turn an edit into a duplicate instead of an override. Forces
///    eval-time rediscovery via Brief 2's `liveTestRediscover` flag so a
///    brand-new `[<Tests>]` value — which detours no existing method — is
///    scanned too.
///
///    FAIL-CLOSED (Invariant 4): on a parse failure OR an eval failure,
///    NEITHER the compilation-state cache NOR either dynamic slot
///    (discovery, run-test) is written — the last-good live view is left
///    exactly as it was, and the caller sees `Error`. A broken intermediate
///    keystroke can never wipe or falsely flip prior results.
let mkLiveTestEvalSupport
  (actor: AppActor)
  (initialDiscoveredTests: Features.LiveTesting.TestCase array)
  (initialProviders: Features.LiveTesting.ProviderDescription list)
  (setDynamicRunTest: (Features.LiveTesting.TestCase -> Async<Features.LiveTesting.TestResult>) -> unit)
  : (unit -> Features.LiveTesting.TestCase array * Features.LiveTesting.ProviderDescription list) * (string -> string -> Async<Result<Features.LiveTesting.TestCase array * Features.LiveTesting.ProviderDescription list, SageFsError>>) =

  // Dynamic test DISCOVERY from FSI evals — the live-merged sibling of the
  // worker's `latestDynamicRunTest` slot. Same ref + Volatile/Interlocked
  // discipline: writer is the eval path (HTTP handler thread via
  // `EvalLiveTestFile`), reader is any thread calling `GetTestDiscovery`.
  let dynamicDiscovery : Features.LiveTesting.TestCase array ref = ref [||]
  let setDynamicDiscovery (tests: Features.LiveTesting.TestCase array) =
    System.Threading.Interlocked.Exchange(dynamicDiscovery, tests) |> ignore

  let getInitialDiscovery () =
    let dynamic = System.Threading.Volatile.Read(&dynamicDiscovery.contents)
    SageFs.Features.LiveTesting.TestDiscoveryMerge.merge initialDiscoveredTests dynamic, initialProviders

  // Per-eval-file CompilationContext state for `EvalLiveTestFile`, tracking
  // this handler's own EvaluatedModules/FileCache — deliberately separate
  // from the file watcher's own `compilationState` (that one only exists
  // when a watcher is running; buffer-changed evals must work with the
  // watcher off, e.g. Interactive workflow sessions with live testing on).
  let liveTestFileCompilationState : Middleware.CompilationContext.CompilationState ref =
    ref Middleware.CompilationContext.CompilationState.empty

  let evalLiveTestFile (filePath: string) (content: string)
    : Async<Result<Features.LiveTesting.TestCase array * Features.LiveTesting.ProviderDescription list, SageFsError>> =
    async {
      let cacheState = System.Threading.Volatile.Read(&liveTestFileCompilationState.contents)
      try
        let! fileStructure, updatedCache =
          Middleware.CompilationContext.parseFileStructureCached filePath content cacheState.FileCache
          |> Async.AwaitTask
        let preprocessed, updatedModules =
          Middleware.CompilationContext.preprocessForFsi
            (Some fileStructure)
            Middleware.CompilationContext.EvalMode.File
            None
            cacheState.EvaluatedModules
            content
        let request =
          { Code = preprocessed.Code
            Args = Map.ofList [ "hotReload", box true; "liveTestRediscover", box true ] }
        use cts = new CancellationTokenSource(Timeouts.workerHttpRequest)
        let! response =
          actor.PostAndAsyncReply(fun rc -> Eval(request, cts.Token, rc))
          |> Instrumentation.tracedActorPost Instrumentation.EvalCategory.HotReload
        match response.EvaluationResult with
        | Error ex ->
          // Eval failed (compile error in the buffer) — leave the
          // compilation-state cache AND both dynamic slots untouched.
          return Error (toWorkerError SageFsError.EvalFailed ex)
        | Ok _ ->
          // Only commit identity-tracking state on a CONFIRMED-good eval —
          // a failed submission must never be "remembered" as evaluated.
          System.Threading.Volatile.Write(
            &liveTestFileCompilationState.contents,
            { cacheState with EvaluatedModules = updatedModules; FileCache = updatedCache })
          match response.Metadata |> Map.tryFind "liveTestRunTest" with
          | Some (:? (Features.LiveTesting.TestCase -> Async<Features.LiveTesting.TestResult>) as runTest) ->
            setDynamicRunTest runTest
          | _ -> ()
          let freshDynamic =
            match response.Metadata |> Map.tryFind "liveTestHookResult" with
            | Some (:? Features.LiveTesting.LiveTestHookResultDto as dto) -> dto.DiscoveredTests
            | _ -> System.Threading.Volatile.Read(&dynamicDiscovery.contents)
          setDynamicDiscovery freshDynamic
          let merged = SageFs.Features.LiveTesting.TestDiscoveryMerge.merge initialDiscoveredTests freshDynamic
          return Ok (merged, initialProviders)
      with ex ->
        // Parse-level failure (FCS chokes on malformed syntax before an eval
        // is even attempted) — same fail-closed contract as an eval error.
        return Error (toWorkerError SageFsError.EvalFailed ex)
    }

  getInitialDiscovery, evalLiveTestFile

/// Run the worker process: create actor, start HTTP server, handle messages.
let run (sessionId: string) (port: int) = async {
  let workerConfig = Args.WorkerConfig.fromEnvironment sessionId port
  // Tell DevReload Harmony patches which port to inject into user scripts.
  // Set BEFORE warmup/init: init scripts may start the user's WebApplication,
  // and the Harmony RunAsync prefix consults workerPort — if it's still 0 the
  // middleware injection is skipped. The host binds exactly to `port`, so it's
  // known before the server even starts.
  DevReloadInjector.setWorkerPort port
  let loadConfig = Args.ProjectLoadConfig.fromWorkerConfig workerConfig

  // The worker process bundles SageFs's OWN dependency versions (e.g. Falco
  // 5.2.0 for the dashboard). When the user's project uses a DIFFERENT version
  // of the same library, FSI's runtime probe would load SageFs's copy and #load
  // of project sources fails with 0x80131040 (assembly manifest mismatch).
  // Pre-load the project's bin assemblies BEFORE FSI starts so the project's
  // versions bind first. The worker itself doesn't use Falco/Npgsql/etc. — the
  // bundled copies exist only because the daemon does — so pre-loading the
  // project's versions is safe.
  let projectBinDirs =
    loadConfig.Projects
    |> List.map (fun projPath ->
      let binDir = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath projPath), "bin")
      if System.IO.Directory.Exists binDir then
        System.IO.Directory.EnumerateDirectories binDir
        |> Seq.sortByDescending (fun d -> System.IO.Directory.GetLastWriteTimeUtc d)
        |> Seq.tryHead
        |> Option.map (fun d -> System.IO.Path.GetFullPath d)
      else None)
    |> List.choose id

  // Make the hosted project's NATIVE dependencies resolvable. The build copies
  // native libs (e.g. Raylib-cs's libraylib.so) into the project output's
  // `runtimes/<rid>/native/`, which CoreCLR's default P/Invoke probe never
  // searches when the managed assembly is loaded from elsewhere. Without this,
  // a P/Invoke (Raylib.InitWindow) throws DllNotFoundException; if that throw is
  // on a user-spawned background thread the runtime FailFasts the whole worker.
  // Registering the resolver removes the throw at its source. Fail-closed: a miss
  // defers to the default behavior, so nothing regresses for projects without
  // native deps. See NativeResolution.fs for the full rationale.
  //
  // Probe EVERY <bin>/<config>/<tfm> output dir, not just the newest: a stale or
  // empty sibling config dir must never hide the config that actually deposited
  // the native lib (an empty Debug/ newer than a built Release/ was exactly this
  // trap). candidatePaths appends `runtimes/<rid>/native/` to each root.
  let nativeSearchRoots =
    loadConfig.Projects
    |> List.collect (fun projPath ->
      let binDir =
        System.IO.Path.Combine(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath projPath), "bin")
      match System.IO.Directory.Exists binDir with
      | false -> []
      | true ->
        System.IO.Directory.EnumerateDirectories binDir
        |> Seq.collect (fun cfg -> System.IO.Directory.EnumerateDirectories cfg)
        |> Seq.map System.IO.Path.GetFullPath
        |> Seq.toList)
  SageFs.NativeResolution.install
    (fun m -> Log.info "%s" m)
    (nativeSearchRoots @ projectBinDirs @ [ System.AppContext.BaseDirectory ])

  match projectBinDirs with
  | [] -> ()
  | dirs ->
    // ALC-equivalent isolation: the worker process's own directory bundles
    // SageFs's dashboard-only dependencies (Falco 5.2.0, Falco.Datastar, ...).
    // Those assemblies are NEVER used by the worker code path (only the daemon
    // renders the dashboard), but their presence in the base dir makes FSI's
    // runtime probe bind them FIRST — so a user project using a DIFFERENT
    // version (e.g. Falco 6.0.0-beta1) fails with 0x80131040 on #load.
    // Quarantine: before FSI init, move any worker-bundled DLL that the
    // project's bin also provides into a subdir. The project's versions then
    // resolve cleanly — exactly the isolation a separate ALC would give.
    let projectBinNames =
      dirs
      |> List.collect (fun dir ->
        System.IO.Directory.EnumerateFiles(dir, "*.dll", System.IO.SearchOption.TopDirectoryOnly)
        |> Seq.map System.IO.Path.GetFileName
        |> Seq.toList)
      |> Set.ofList
    let baseDir = System.AppContext.BaseDirectory
    let quarantineDir = System.IO.Path.Combine(baseDir, "_sagefs_quarantine")
    // NEVER quarantine runtime-critical assemblies the worker itself needs to
    // function (FSharp.Core is the prime example — the FSI session loads it
    // from the worker's base dir and must get the worker's own version).
    let quarantined =
      projectBinNames
      |> Seq.filter shouldQuarantineAssembly
      |> Seq.choose (fun name ->
        let own = System.IO.Path.Combine(baseDir, name)
        match System.IO.File.Exists own with
        | false -> None
        | true ->
          try
            System.IO.Directory.CreateDirectory quarantineDir |> ignore
            let dest = System.IO.Path.Combine(quarantineDir, name)
            if System.IO.File.Exists dest then System.IO.File.Delete dest
            System.IO.File.Move(own, dest)
            Some name
          with _ -> None)
      |> Seq.toList
    if not (List.isEmpty quarantined) then
      Log.info "Worker %s: quarantined %d worker-bundled assembly(ies) that collide with project deps: %s"
        sessionId quarantined.Length (String.concat ", " quarantined)

  let logger =
    { new Utils.ILogger with
        member _.LogInfo msg = Log.info "%s" msg
        member _.LogDebug msg = Log.debug "%s" msg
        member _.LogWarning msg = Log.warn "%s" msg
        member _.LogError msg = Log.error "%s" msg }
  let onEvent (evt: Features.Events.SageFsEvent) =
    match evt with
    | Features.Events.SageFsEvent.SessionWarmUpProgress p ->
      match WarmupProgressLine.tryFormatLine p.Step p.Total p.Message with
      | Some line ->
        Console.Out.WriteLine line
        Console.Out.Flush()
      | None ->
        Log.warn "[WorkerMain] Skipping invalid warmup progress %d/%d %s" p.Step p.Total p.Message
    | _ -> ()

  let actorArgs : ActorCreation.ActorArgs = {
    Middleware = ActorCreation.commonMiddleware
    InitFunctions = ActorCreation.commonInitFunctions
    FsiKind = SessionKinds.fromEnvironmentWith System.Environment.GetEnvironmentVariable
    Logger = logger
    OutStream = IO.TextWriter.Null
    UseAsp = false
    LoadConfig = loadConfig
    IsBare = workerConfig.IsBare
    AutoOpenNamespaces = workerConfig.AutoOpenNamespaces
    OnEvent = onEvent
    Workflow = workerConfig.Workflow
  }

  let! result =
    ActorCreation.createActor actorArgs |> Async.AwaitTask
  let actor = result.Actor

  // Install the web DevReload middleware (the WebApplication.Run/RunAsync Harmony
  // patch) only for a hot-reload session on a WEB project. Console and game
  // hot-reload use the separate FSI method-detour engine and need no web patch —
  // so the reload strategy, derived from the workflow AND the project kind,
  // decides. Interactive never reloads at all.
  let projectKind =
    result.ProjectRoles
    |> List.collect (fun p -> p.PackageRefs)
    |> WorkflowTypes.ProjectKind.classify
  let reloadStrategy = WorkflowTypes.SessionWorkflow.reloadStrategy workerConfig.Workflow projectKind
  if WorkflowTypes.ReloadStrategy.installsWebDevReload reloadStrategy then
    DevReloadInjector.install()

  // The agent lives where the user's code lives (this process, or the isolated FSI host). It scans what that process has
  // loaded for tests, and it runs them: interactively defined tests first, then the project's.
  let initialDiscoveredTests, initialProviders =
    match result.Agent.DiscoverLoaded() with
    | HostAgent.AgentAnswered discovery -> discovery.Tests, discovery.Providers
    | HostAgent.AgentUnavailable reason ->
      Log.warn "[WorkerMain] no initial test discovery: %s" reason
      [||], []

  let projectRunTest : Features.LiveTesting.TestCase -> Async<Features.LiveTesting.TestResult> =
    fun test ->
      async {
        match! result.Agent.RunTest test with
        | HostAgent.AgentAnswered outcome -> return outcome
        | HostAgent.AgentUnavailable _ -> return Features.LiveTesting.TestResult.NotRun
      }


  // Dynamic RunTest from FSI evals (updated on each eval via handleMessage.EvalCode).
  // Chesterton's fence: ref + Volatile.Read/Interlocked.Exchange instead of mutable.
  // Writer (file watcher async on ThreadPool) and reader (HTTP handler thread) are
  // on different threads. Plain mutable has no memory barrier — on ARM64 .NET the
  // store may not be visible to the reader without volatile semantics.
  let latestDynamicRunTest : (Features.LiveTesting.TestCase -> Async<Features.LiveTesting.TestResult>) option ref =
    ref None

  // Composed RunTest: try dynamic first (for interactively defined tests), fall back to project
  let getRunTest () =
    match System.Threading.Volatile.Read(&latestDynamicRunTest.contents) with
    | Some dynamicRt ->
      fun (tc: Features.LiveTesting.TestCase) -> async {
        let! result = dynamicRt tc
        match result with
        | Features.LiveTesting.TestResult.NotRun -> return! projectRunTest tc
        | found -> return found }
    | None -> projectRunTest
  let setDynamicRunTest v = System.Threading.Interlocked.Exchange(latestDynamicRunTest, Some v) |> ignore

  // Live-testing-asyoutype-plan.md Brief 3 (the keystone): the live-merged
  // discovery view + identity-preserving buffer-eval handler. Factored out
  // as `mkLiveTestEvalSupport` (below `handleMessage`) so it is directly
  // testable against a real FSI actor without spinning up the whole worker
  // process (project loading, HTTP server, file watcher).
  let getInitialDiscovery, evalLiveTestFile =
    mkLiveTestEvalSupport actor initialDiscoveredTests initialProviders setDynamicRunTest

  let appRunner = AppRunner.create AppRunner.defaultTimeouts AppRunner.processEnv
  // The source each running app's DLL was built from, advanced after every
  // applied patch: what a save is compared with to decide patch vs restart.
  let reloadBaselines = System.Collections.Concurrent.ConcurrentDictionary<string, Features.ReloadPlanning.FileDecls>()

  // Start file watcher unless no-watch was set
  let fileWatcher =
    match workerConfig.NoWatch || List.isEmpty result.ProjectDirectories with
    | true ->
      match workerConfig.NoWatch with
      | true -> Log.info "File watcher disabled (SAGEFS_NO_WATCH=1)"
      | false -> Log.warn "File watcher skipped: no project directories found"
      None
    | false ->
      Log.info "File watcher starting for %d directories: %s"
        result.ProjectDirectories.Length
        (String.Join(", ", result.ProjectDirectories))
      let config = FileWatcher.defaultWatchConfig result.ProjectDirectories
      // Chesterton's fence: per-watcher CompilationState tracks module context
      // across hot-reload cycles. Without this, each reload is context-free —
      // preprocessForFsi can't determine which modules are already `open`'d,
      // leading to duplicate module errors or missing opens.
      let mutable compilationState = Middleware.CompilationContext.CompilationState.empty
      // Chesterton's fence: SemaphoreSlim(1,1) serializes file change processing.
      // Without this, two different files changing within the debounce window spawn
      // two async workflows that race on `compilationState` — the mutable
      // EvaluatedModules set could lose an entry from a concurrent read-modify-write.
      let compilationLock = new Threading.SemaphoreSlim(1, 1)
      // Chesterton's fence: per-file CancellationTokenSource enables cancel-and-restart.
      // When a user rapid-saves, the new change cancels the previous eval for the same
      // file (which may be compiling an intermediate broken state), so only the latest
      // content is evaluated. Without this, rapid saves queue up multiple evals that
      // flash red errors before the final green.
      let perFileCts = System.Collections.Concurrent.ConcurrentDictionary<string, CancellationTokenSource>()
      let reloadedMethodsOf (response: EvalResponse) =
        response.Metadata
        |> Map.tryFind "reloadedMethods"
        |> Option.bind (fun v ->
          match v with
          | :? (string list) as methods -> Some methods
          | _ -> None)
        |> Option.defaultValue []
      // Chesterton's fence: broadcastCompilationFailed ensures the browser
      // overlay transitions from "Recompiling..." to the error message.
      // Without this, compilation errors leave the overlay stuck on blue
      // "Recompiling..." forever — the #1 reported DX issue.
      let broadcastEvalFailure (filePath: string) (lineOffset: int) (response: EvalResponse) (ex: exn) =
        let fileName = IO.Path.GetFileName filePath
        let summary = sprintf "%s: %s" fileName ex.Message
        // Extract structured diagnostics with source-mapped line numbers.
        // Chesterton's fence: lineOffset compensates for lines added/removed by
        // CompilationContext preprocessing (module wrapper, #load directives).
        // Without applying this offset, browser error overlay shows FSI-internal
        // line numbers that don't match the user's source file — the #1 DX
        // complaint from the expert panel.
        let diagnostics =
          response.Diagnostics
          |> Array.filter (fun d -> d.Severity = Features.Diagnostics.DiagnosticSeverity.Error || d.Severity = Features.Diagnostics.DiagnosticSeverity.Warning)
          |> Array.map (fun d ->
            ({ File = fileName
               Line = Middleware.CompilationContext.mapDiagnosticLine lineOffset d.Range.StartLine
               EndLine = Middleware.CompilationContext.mapDiagnosticLine lineOffset d.Range.EndLine
               Column = d.Range.StartColumn
               EndColumn = d.Range.EndColumn
               Severity = Features.Diagnostics.DiagnosticSeverity.label d.Severity
               DiagCode =
                 match d.Subcategory with
                 | s when String.IsNullOrWhiteSpace s -> None
                 | s -> Some s
               Message = d.Message
               SourceContext = None
               SourceContextStartLine = None } : DevReload.DevReloadDiagnostic)
            |> DevReload.DevReloadDiagnostic.addSourceContext)
          |> Array.toList
        DevReload.broadcastCompilationFailed summary diagnostics
        Log.warn "Reload failed for %s: %s\n%s" fileName ex.Message (ex.StackTrace |> Option.ofObj |> Option.defaultValue "")
      let routeFor (filePath: string) =
        match AppRunner.state appRunner, reloadBaselines.TryGetValue(IO.Path.GetFullPath filePath) with
        | AppRun.AppRunState.Running _, (true, baseline) -> ReloadRoute.PatchRunningApp baseline
        | _ -> ReloadRoute.ReevaluateFile
      let requireRestart (fileName: string) first rest = async {
        Log.info "Run App: %s — %s; restarting the app" fileName (Features.ReloadPlanning.ReloadChange.describeAll first rest)
        let! _ = AppRunner.requireRestart appRunner first rest |> Async.AwaitTask
        () }
      // Patch the running app in place when only function bodies changed; anything
      // that takes effect at startup restarts it (see ReloadPlanning).
      let reloadRunningApp (filePath: string) (baseline: Features.ReloadPlanning.FileDecls) = async {
        let fileName = IO.Path.GetFileName filePath
        match Features.ReloadPlanning.extractDecls (IO.File.ReadAllText filePath) with
        | Error reason ->
          DevReload.broadcastCompilationFailed (sprintf "Parse failed for %s: %s" fileName reason) []
        | Ok current ->
          match Features.ReloadPlanning.planReload baseline current with
          | Features.ReloadPlanning.ReloadPlan.PatchFunctions [] ->
            Log.info "Run App: %s saved with no function change — nothing to reload" fileName
          | Features.ReloadPlanning.ReloadPlan.PatchFunctions functions ->
            DevReload.broadcastCompiling (Some fileName)
            let patch = Middleware.CompilationContext.emitStableIdentity filePath current functions
            let request = { Code = patch.Code; Args = Map.ofList ["hotReload", box true] }
            use localCts = new CancellationTokenSource()
            let! response = actor.PostAndAsyncReply(fun rc -> Eval(request, localCts.Token, rc))
            match response.EvaluationResult with
            | Error ex -> broadcastEvalFailure filePath patch.LineOffset response ex
            | Ok _ ->
              let reloaded = reloadedMethodsOf response
              match Features.ReloadPlanning.confirmPatch baseline functions reloaded with
              | Features.ReloadPlanning.PatchOutcome.Applied ->
                reloadBaselines.[IO.Path.GetFullPath filePath] <- current
                // The detour middleware already refreshed the browser when a method moved.
                match reloaded with
                | [] -> DevReload.broadcastReload ()
                | _ -> ()
                Log.info "Run App: hot reloaded %s: %s" fileName (functions |> List.map _.Name |> String.concat ", ")
              | Features.ReloadPlanning.PatchOutcome.RestartNeeded (first, rest) ->
                do! requireRestart fileName first rest
          | Features.ReloadPlanning.ReloadPlan.RestartRequired (first, rest) ->
            do! requireRestart fileName first rest }
      let onFileChanged (change: FileWatcher.FileChange) =
        let ext = IO.Path.GetExtension(change.FilePath)
        let kind = match change.Kind with
                   | FileWatcher.FileChangeKind.Changed -> "Modified"
                   | FileWatcher.FileChangeKind.Created -> "Created"
                   | FileWatcher.FileChangeKind.Deleted -> "Deleted"
                   | FileWatcher.FileChangeKind.Renamed -> "Renamed"
        Instrumentation.fileWatcherChanges.Add(
          1L,
          System.Collections.Generic.KeyValuePair("file.extension", ext :> obj),
          System.Collections.Generic.KeyValuePair("change.kind", kind :> obj))
        // Cancel any in-flight eval for this exact file — only latest save matters.
        // Chesterton's fence: AddOrUpdate is atomic — eliminates the TOCTOU race where
        // TryGetValue + manual cancel + indexer assignment could interleave with another
        // thread's update for the same file path.
        let filePath = change.FilePath
        let newCts = new CancellationTokenSource()
        perFileCts.AddOrUpdate(
          filePath,
          newCts,
          fun _key oldCts ->
            try oldCts.Cancel()
            with
            | :? ObjectDisposedException -> ()
            | ex -> Log.warn "[WorkerMain] CTS cancel failed for %s: %s\n%s" filePath ex.Message (ex.StackTrace |> Option.ofObj |> Option.defaultValue "")
            oldCts.Dispose()
            newCts)
        |> ignore
        let ct = newCts.Token
        Async.Start(async {
          do! compilationLock.WaitAsync(ct) |> Async.AwaitTask
          try
            try
              ct.ThrowIfCancellationRequested()
              match FileWatcher.fileChangeAction change with
              | FileWatcher.FileChangeAction.Reload filePath ->
                match HotReloadState.isWatched filePath !result.HotReloadStateRef with
                | false ->
                  Log.debug "File changed but not in hot-reload watch set: %s (watched: %d files)"
                    (IO.Path.GetFileName filePath) (HotReloadState.watchedCount !result.HotReloadStateRef)
                | true ->
                match routeFor filePath with
                | ReloadRoute.PatchRunningApp baseline -> do! reloadRunningApp filePath baseline
                | ReloadRoute.ReevaluateFile ->
                Log.debug "[DevReload] Reloading watched file: %s" (IO.Path.GetFileName filePath)
                DevReload.broadcastCompiling (Some (IO.Path.GetFileName filePath))
                // Chesterton's fence: read file and preprocess through CompilationContext
                // instead of using `#load`. `#load` re-executes the entire file including
                // module-level side effects (server startup, DB connections), causing type
                // errors ("unit doesn't match Task") in files with effectful top-level code.
                // CompilationContext strips the module declaration and wraps definitions
                // properly for FSI, preserving only type/function definitions. The existing
                // HotReloading middleware then applies NoInlining + Harmony detours.
                let fileContent = IO.File.ReadAllText(filePath)
                let! fileStructure, updatedCache = async {
                  try
                    let! fs, cache =
                      Middleware.CompilationContext.parseFileStructureCached
                        filePath fileContent compilationState.FileCache
                      |> Async.AwaitTask
                    return Some fs, cache
                  with exn ->
                    // Chesterton's fence: do NOT fall back to #load here. #load re-executes
                    // the entire file including module-level side effects (app.RunAsync(),
                    // DB connections), which is the exact bug CompilationContext was built
                    // to fix. Instead, broadcast the parse failure to the browser and skip
                    // the reload. The user sees the error, fixes the file, saves again.
                    Log.warn "CompilationContext parse failed for %s — file not reloaded: %s"
                      filePath exn.Message
                    DevReload.broadcastCompilationFailed
                      (sprintf "Parse failed for %s: %s" (IO.Path.GetFileName filePath) exn.Message) []
                    return None, compilationState.FileCache
                }
                match fileStructure with
                | None -> () // parse failed — error already broadcast, skip reload
                | Some _ ->
                Log.debug "[DevReload] Parse succeeded for %s — preprocessing" (IO.Path.GetFileName filePath)
                let preprocessed, updatedModules =
                  Middleware.CompilationContext.preprocessForFsi
                    fileStructure
                    Middleware.CompilationContext.EvalMode.File
                    None
                    compilationState.EvaluatedModules
                    fileContent
                compilationState <-
                  { compilationState with
                      EvaluatedModules = updatedModules
                      FileCache = updatedCache }
                let code = preprocessed.Code
                let request = { Code = code; Args = Map.ofList ["hotReload", box true] }
                use localCts = new CancellationTokenSource()
                let! response =
                  actor.PostAndAsyncReply(fun rc -> Eval(request, localCts.Token, rc))
                match response.EvaluationResult with
                | Ok _ ->
                  // Capture RunTest from hot-reload discovery
                  match response.Metadata |> Map.tryFind "liveTestRunTest" with
                  | Some (:? (Features.LiveTesting.TestCase -> Async<Features.LiveTesting.TestResult>) as runTest) ->
                    setDynamicRunTest runTest
                  | _ -> ()
                  let reloaded = reloadedMethodsOf response
                  let fileName = IO.Path.GetFileName filePath
                  match List.isEmpty reloaded with
                  | false ->
                    Log.info "Hot reloaded %s: %s" fileName (String.Join(", ", reloaded))
                  | true ->
                    // Chesterton's fence: broadcastReload even when no methods were detouring.
                    // When a file adds NEW types/functions (not modifying existing ones),
                    // Harmony finds no methods to detour, so triggerReload() in HotReloading.fs
                    // is never called. Without this, the browser stays stuck on "⟳ Recompiling..."
                    // forever — violating the Compiling→(Reload|CompilationFailed) contract.
                    DevReload.broadcastReload ()
                    Log.info "Reloaded %s (new types/functions, no methods detouring)" fileName
                | Error ex -> broadcastEvalFailure filePath preprocessed.LineOffset response ex
              | FileWatcher.FileChangeAction.SoftReset ->
                Log.info "Project file changed — soft reset needed"
                let! _ = actor.PostAndAsyncReply(fun rc -> ResetSession rc)
                ()
              | FileWatcher.FileChangeAction.Ignore -> ()
            with
            | :? OperationCanceledException ->
              Log.debug "File change cancelled (superseded by newer save): %s"
                (IO.Path.GetFileName change.FilePath)
            | ex ->
              // Chesterton's fence: if the actor mailbox crashes or PostAndAsyncReply
              // throws, we must still close the Compiling→(Reload|CompilationFailed)
              // lifecycle. Without this catch-all, an unhandled exception leaves the
              // browser stuck on "⟳ Recompiling..." with no recovery path.
              DevReload.broadcastCompilationFailed (sprintf "Internal error: %s" ex.Message) []
              Log.error "File watcher async failed: %s" (ex.ToString())
          finally
            compilationLock.Release() |> ignore
        })
      Some (FileWatcher.start config DevReload.DevReloadConfig.defaults onFileChanged)

  let watchForHotReload (projectPath: string) (assemblyPath: string) =
    match workerConfig.Workflow, IO.Path.GetDirectoryName(IO.Path.GetFullPath projectPath) with
    | WorkflowTypes.SessionWorkflow.HotReload _, (NonNull projectDir) ->
      let sources =
        IO.Directory.GetFiles(projectDir, "*.fs", IO.SearchOption.AllDirectories)
        |> Array.filter (fun f ->
          let n = f.Replace('\\', '/')
          not (n.Contains("/obj/") || n.Contains("/bin/")))
      result.HotReloadStateRef.Value <- HotReloadState.watchByDirectory projectDir sources result.HotReloadStateRef.Value
      // The baseline must be the source the loaded assembly was actually built
      // from. Fail closed: if the assembly's write time can't be read, treat
      // every source file as untrustworthy rather than risk silently baking
      // an unbuild edit into the baseline (see ReloadPlanning.baselineIsTrustworthy).
      let assemblyWriteTimeUtc =
        try IO.File.GetLastWriteTimeUtc assemblyPath
        with _ -> DateTime.MinValue
      for source in sources do
        let sourceWriteTimeUtc =
          try IO.File.GetLastWriteTimeUtc source
          with _ -> DateTime.MaxValue
        match Features.ReloadPlanning.baselineIsTrustworthy assemblyWriteTimeUtc sourceWriteTimeUtc with
        | false ->
          Log.warn "Run App: %s was modified after the build — it will be fully re-evaluated (not diff-patched) on its next save" source
        | true ->
          match Features.ReloadPlanning.extractDecls (IO.File.ReadAllText source) with
          | Ok decls -> reloadBaselines.TryAdd(IO.Path.GetFullPath source, decls) |> ignore
          | Error reason -> Log.warn "Run App: %s cannot be patched in place (%s)" source reason
      Log.info "Run App: watching %d source file(s) in %s for hot reload"
        (HotReloadState.watchedInDirectory projectDir result.HotReloadStateRef.Value).Length projectDir
    | _ -> ()
  let appRuns : AppRunHandlers = {
    Run = fun project previous -> async {
      let prepared =
        AppRunner.resolveProjectAssembly result.ProjectTargets project
        |> Result.bind (fun asm -> AppRunner.entryPointOf asm |> Result.map (fun entry -> asm, entry))
        |> Result.bind (fun (asm, entry) -> AppRunner.readLaunchConfig project |> Result.map (fun config -> asm, entry, config))
      match prepared with
      | Error reason -> return Error (SageFsError.AppRunFailed (project, reason))
      | Ok (asm, entry, config) ->
        let! state = AppRunner.start appRunner project entry (AppRun.planLaunch project config previous) |> Async.AwaitTask
        match state with
        | AppRun.AppRunState.Running _ -> watchForHotReload project asm.Location
        | _ -> ()
        return Ok state }
    Stop = fun scope -> async {
      let project =
        match AppRunner.state appRunner with
        | AppRun.AppRunState.Running app -> app.Project
        | _ -> ""
      let! stopped = AppRunner.stop appRunner scope |> Async.AwaitTask
      return stopped |> Result.mapError (fun reason -> SageFsError.AppRunFailed (project, reason)) }
    AwaitChange = fun runId -> async {
      use cts = new CancellationTokenSource(TimeSpan.FromMinutes 5.0)
      return! AppRunner.awaitChange appRunner runId cts.Token |> Async.AwaitTask } }

  // Signal readiness over the pipe
  let handler =
    handleMessage actor result.GetSessionState result.GetEvalStats result.GetStatusMessage result.ProjectRoles
      getRunTest setDynamicRunTest getInitialDiscovery evalLiveTestFile appRuns

  let readyHandler (msg: WorkerMessage) = async {
    match msg with
    | WorkerMessage.GetInstrumentationMaps(replyId) ->
      return WorkerResponse.InstrumentationMapsResult(replyId, result.InstrumentationMaps)
    | _ -> return! handler msg
  }

  use cts = new CancellationTokenSource()

  // Handle process signals — guard against ObjectDisposedException
  // if the CTS is disposed before the event fires (e.g. daemon kills worker)
  Console.CancelKeyPress.Add(fun e ->
    e.Cancel <- true
    try cts.Cancel() with :? ObjectDisposedException -> ())

  AppDomain.CurrentDomain.ProcessExit.Add(fun _ ->
    try cts.Cancel() with :? ObjectDisposedException -> ())

  // Parent-death monitor: when the daemon is killed hard (Task Manager,
  // taskkill /F, crash, OS shutdown), no Shutdown message ever arrives.
  // Watching the daemon PID lets the worker self-exit instead of becoming
  // an orphan (issue #126).
  match workerConfig.DaemonPid with
  | Some daemonPid ->
    // Fenced on (pid, startTime) whenever the daemon told us its own start
    // time (SAGEFS_DAEMON_START_TICKS) — closing the pid-reuse race where a
    // recycled daemon pid would otherwise keep this worker alive forever.
    let owner : SageFs.OwnerMonitor.Owner =
      { Pid = daemonPid; StartTimeTicks = workerConfig.DaemonStartTicks }
    Log.info "Worker %s monitoring daemon PID %d (parent-death watchdog, fenced=%b)"
      sessionId daemonPid (Option.isSome workerConfig.DaemonStartTicks)
    Async.Start(
      SageFs.OwnerMonitor.run SageFs.OwnerMonitor.getProcessById owner cts (fun msg ->
        Log.warn "%s" msg))
  | None ->
    Log.debug "Worker %s has no daemon PID — parent-death watchdog disabled" sessionId

  try
    // Start HTTP server on requested port (0 = OS-assigned)
    // Collect all .fs/.fsx files from project directories for hot-reload UI
    let projectFiles =
      result.ProjectDirectories
      |> List.collect (fun dir ->
        match IO.Directory.Exists(dir) with
        | true ->
          IO.Directory.GetFiles(dir, "*.fs", IO.SearchOption.AllDirectories)
          |> Array.append (IO.Directory.GetFiles(dir, "*.fsx", IO.SearchOption.AllDirectories))
          |> Array.toList
          |> List.filter (fun f ->
            let n = f.Replace('\\', '/')
            not (n.Contains("/obj/") || n.Contains("/bin/")))
        | false -> [])

    // Hot-reload is OFF by default — nothing watched until the user explicitly
    // opts in via the dashboard or SAGEFS_HOT_RELOAD=all env var. The file
    // watcher still runs (it's cheap OS infrastructure shared with live testing),
    // but no files are marked for hot-reload detouring.
    match Environment.GetEnvironmentVariable("SAGEFS_HOT_RELOAD") with
    | "all" ->
      result.HotReloadStateRef.Value <-
        HotReloadState.watchAll projectFiles HotReloadState.empty
      Log.info "Hot reload: watching %d project files (SAGEFS_HOT_RELOAD=all)" projectFiles.Length
    | _ ->
      Log.info "Hot reload: off by default (0 files watched)"
    let! server =
      WorkerHttpTransport.startServer readyHandler result.HotReloadStateRef projectFiles result.GetWarmupContext getRunTest result.Agent.TakeCoverage port
      |> Async.AwaitTask
    // Print actual port to stdout so daemon can discover it
    printfn "WORKER_PORT=%s" server.BaseUrl
    Console.Out.Flush()

    // Tell DevReload Harmony patches which port to inject into user scripts
    let uri = Uri(server.BaseUrl)
    DevReloadInjector.setWorkerPort uri.Port

    // Block until cancellation
    let tcs = Threading.Tasks.TaskCompletionSource<unit>()
    use _reg = cts.Token.Register(fun () -> tcs.TrySetResult() |> ignore)
    do! tcs.Task |> Async.AwaitTask

    // Graceful shutdown
    (server :> IDisposable).Dispose()
  with
  | :? OperationCanceledException -> ()
  | ex ->
    Log.error "Worker %s error: %s" sessionId (ex.ToString())

  // Clean up file watcher
  fileWatcher |> Option.iter (fun w -> w.Dispose())
}
