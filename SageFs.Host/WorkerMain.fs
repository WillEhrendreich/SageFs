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

/// Make this process's stdout line-unbuffered for the rest of its life.
///
/// Chesterton's fence: .NET block-buffers stdout as soon as it is REDIRECTED
/// (which it always is — the daemon spawns the worker with a redirected pipe,
/// and anyone diagnosing a worker by hand redirects it to a file). The single
/// explicit `Console.Out.Flush()` after the `WORKER_PORT=` handshake therefore
/// gets the port out, and then the log goes silent mid-stream for kilobytes at
/// a time. That silence has twice been read as "the file watcher is dead" and
/// cost real debugging time on a worker that was working fine. Autoflush pays
/// for itself the next time anyone looks at a worker log.
///
/// Called before anything writes, so no writer captures the buffered stream.
let enableStdoutAutoFlush () =
  try
    let stdout = new IO.StreamWriter(Console.OpenStandardOutput())
    stdout.AutoFlush <- true
    Console.SetOut stdout
  with ex ->
    // A worker that cannot reconfigure its own stdout still has to run; the
    // consequence is only a lazier log.
    Log.warn "[WorkerMain] Could not enable stdout autoflush: %s" ex.Message

/// What a save still needs after the in-place patch route has had its turn.
/// A DU rather than a bare flag because the fallback has to carry WHY it is
/// falling back: the reasons are what the whole-file route reports when its
/// re-evaluation cannot reach the running app either.
[<RequireQualifiedAccess>]
type SaveHandling =
  /// The outcome was decided and broadcast. Nothing further to do.
  | Reported
  /// Patching in place was not possible and SageFs does not own the app's
  /// lifetime, so the whole file is re-evaluated — carrying the reasons the
  /// patch route refused, because they are still true afterwards.
  | FallBackWholeFile of reasons: Features.ReloadOutcome.RestartReason list

/// What one eval's mutable-binding detours mean for the save as a whole — not
/// just for `confirmPatchAsOutcome`'s function-only accounting, which never
/// sees a binding at all. A named DU (not a bare bool or a `Choice`) because
/// the two outcomes are handled by entirely different routes below.
[<RequireQualifiedAccess>]
type BindingEscalation =
  /// At least one binding TORE (one accessor re-pointed, the other not): the
  /// running process is now silently wrong, and nothing else in this eval's
  /// missed set is dangerous enough to be reported as a mere "did not land"
  /// — the save must restart the app, the same route a source-level
  /// `MutableStateChanged` already takes.
  | ForcesRestart of first: Features.ReloadPlanning.ReloadChange * rest: Features.ReloadPlanning.ReloadChange list
  /// Nothing tore. A declined orphan leg, or an accessor pair whose
  /// preflight/Harmony application failed on BOTH legs, moved nothing — safe
  /// — and is reported as an extra `RestartReason` alongside whatever the
  /// function-only patch accounting already found.
  | ExtraReasons of Features.ReloadOutcome.RestartReason list

/// What this eval's mutable-binding detours mean for the save, from the
/// `BindingOutcome`/`DeclinedBinding` values `HotReloading.fs`'s middleware
/// forwarded on the eval response. Pure — no I/O, no logging — so the danger
/// judgement (does this force a restart?) is testable on its own, apart from
/// the async plumbing that acts on it.
///
/// A TORN binding is not a missed patch: reads see the new field, writes
/// still land in the old one (or the reverse), and nothing throws or logs
/// past this point to say so. That is categorically different from "this
/// edit needs a restart to take effect", which is safe to sit in until the
/// user gets to it — so Torn always wins the whole verdict when it appears
/// at all, regardless of what else this eval also managed to patch cleanly.
let escalationOf
  (bindings: Middleware.HotReloadCore.BindingOutcome list)
  (declined: Middleware.HotReloadCore.DeclinedBinding list)
  : BindingEscalation =
  let torn =
    bindings
    |> List.choose (function
      | Middleware.HotReloadCore.BindingOutcome.Torn(binding, _) -> Some binding
      | Middleware.HotReloadCore.BindingOutcome.BothLegsRedirected _
      | Middleware.HotReloadCore.BindingOutcome.NeitherLegRedirected _ -> None)
  match torn with
  | first :: rest ->
    BindingEscalation.ForcesRestart(
      Features.ReloadPlanning.ReloadChange.MutableBindingTorn first,
      rest |> List.map Features.ReloadPlanning.ReloadChange.MutableBindingTorn)
  | [] ->
    let fromNeither =
      bindings
      |> List.choose (function
        | Middleware.HotReloadCore.BindingOutcome.NeitherLegRedirected(binding, reason) ->
          Some(
            Features.ReloadOutcome.RestartReason.NotYetSupported(
              sprintf "re-pointing the mutable binding '%s' (%s)" binding reason))
        | Middleware.HotReloadCore.BindingOutcome.BothLegsRedirected _
        | Middleware.HotReloadCore.BindingOutcome.Torn _ -> None)
    let fromDeclined =
      declined |> List.map (fun d -> Features.ReloadOutcome.RestartReason.MutableModuleState d.Binding)
    BindingEscalation.ExtraReasons(fromNeither @ fromDeclined)

/// Run the worker process: create actor, start HTTP server, handle messages.
let run (sessionId: string) (port: int) = async {
  enableStdoutAutoFlush ()
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
  // Initializers a save KEPT the live value for (rule 3 of the state spec),
  // keyed by qualified binding, waiting for someone to reset them.
  let keptPending = System.Collections.Concurrent.ConcurrentDictionary<string, Features.KeptState.Pending>()
  // Re-run ONE kept binding's new initializer and write it into the app's
  // own field. Nothing else in the file runs.
  let resetKept (binding: string) : Async<Features.KeptState.ResetOutcome> = async {
    match keptPending.TryGetValue binding with
    | false, _ -> return Features.KeptState.ResetOutcome.NothingPending binding
    | true, pending ->
      match Features.LiveStateEmit.resetCode pending.Decls pending.Decl with
      | Error reason -> return Features.KeptState.ResetOutcome.ResetFailed(binding, Features.LiveStateEmit.LiveStateError.describe reason)
      | Ok code ->
        let budget = DevReload.DevReloadConfig.defaults.CompileBudgetMs
        let request = { Code = code; Args = Map.ofList [ "hotReload", box true ] }
        match! actor.PostAndTryAsyncReply((fun rc -> Eval(request, CancellationToken.None, rc)), budget) with
        | None -> return Features.KeptState.ResetOutcome.ResetFailed(binding, sprintf "the reset didn't finish within %dms" budget)
        | Some response ->
          match response.EvaluationResult with
          | Error ex -> return Features.KeptState.ResetOutcome.ResetFailed(binding, ex.Message)
          | Ok output ->
            match Features.LiveStateEmit.parseReset output with
            | Error reason -> return Features.KeptState.ResetOutcome.ResetFailed(binding, Features.LiveStateEmit.LiveStateError.describe reason)
            | Ok value ->
              keptPending.TryRemove binding |> ignore
              Log.info "Hot reload: reset '%s' to its new initializer, it's %s now" binding value
              return Features.KeptState.ResetOutcome.Reset(binding, value) }
  let keptStateAccess : Features.KeptState.Access =
    { Pending = fun () -> keptPending.Values |> Seq.map _.Value |> Seq.sortBy _.Binding |> Seq.toList
      Reset = resetKept
      ReflectionReads =
        fun () ->
          match result.Agent.ReflectionReads () with
          | HostAgent.AgentAnswered report -> Result.Ok report
          | HostAgent.AgentUnavailable reason -> Result.Error(Features.KeptState.ReflectionReadsError.NoAgent reason)
      SetReflectionMode =
        fun mode ->
          match result.Agent.SetReflectionMode mode with
          | HostAgent.AgentAnswered report ->
            Log.info "Hot reload: reflection reads are %s now" (Middleware.ValueReads.ReflectionReadMode.name mode)
            Result.Ok report
          | HostAgent.AgentUnavailable reason -> Result.Error(Features.KeptState.ReflectionReadsError.NoAgent reason) }

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
      /// The TRUE evidence, straight off `DetourReport.ReachedRunningProcess`:
      /// the subset of `reloadedMethodsOf` whose re-pointed OLD entry point is
      /// the exact copy `HotReloadCore.State.AppHolds` recorded the app as
      /// holding. This is what `confirmPatchAsOutcome` must be given as its
      /// reached-set — passing `reloadedMethodsOf response` again here (as if
      /// every redirect reaches the app) is the exact bug that let a save
      /// report "Hot reloaded 1 of 1" while the app kept serving the old body
      /// (see FsiEmitSimTests.fs).
      let reachedRunningProcessOf (response: EvalResponse) =
        response.Metadata
        |> Map.tryFind "hotReloadReachedRunningProcess"
        |> Option.bind (fun v ->
          match v with
          | :? (string list) as methods -> Some methods
          | _ -> None)
        |> Option.defaultValue []
      // What HotReloading.fs's middleware forwarded from this eval's
      // DetourReport, straight off the metadata bag it wrote — see
      // `reloadedMethodsOf` above for the same pattern.
      /// Methods Harmony accepted a patch for and whose JIT-compiled bytes the
      /// canary then found unchanged. These are NOT in `reloadedMethods`: the
      /// running process provably still executes the old body, so they are
      /// misses that must reach the user with a remedy, not silent successes.
      let ineffectiveMethodsOf (response: EvalResponse) =
        response.Metadata
        |> Map.tryFind "hotReloadIneffectiveMethods"
        |> Option.bind (fun v ->
          match v with
          | :? (string list) as methods -> Some methods
          | _ -> None)
        |> Option.defaultValue []
      /// Of the redirects that landed, those whose OLD entry point lived in a
      /// COMPILED assembly. An app running from the project's build output
      /// calls those; a redirect that only re-pointed a previous FSI copy
      /// leaves it running the old body, and the two are indistinguishable by
      /// name because FSI's `FSI_NNNN` wrapper is stripped on both sides.
      let redirectedFromCompiledOf (response: EvalResponse) =
        response.Metadata
        |> Map.tryFind "hotReloadRedirectedFromCompiled"
        |> Option.bind (fun v ->
          match v with
          | :? (string list) as methods -> Some methods
          | _ -> None)
        |> Option.defaultValue []
      /// Names that HAD a compiled copy among the detour candidates. When a name
      /// is absent there is no compiled copy to reach — every copy is an FSI one
      /// (a `#load`ed file), so the running app holds an FSI copy and an
      /// FSI-to-FSI redirect IS what reaches it.
      let compiledCandidatesOf (response: EvalResponse) =
        response.Metadata
        |> Map.tryFind "hotReloadCompiledCandidates"
        |> Option.bind (fun v ->
          match v with
          | :? (string list) as methods -> Some methods
          | _ -> None)
        |> Option.defaultValue []
      let bindingOutcomesOf (response: EvalResponse) : Middleware.HotReloadCore.BindingOutcome list =
        response.Metadata
        |> Map.tryFind "hotReloadBindingOutcomes"
        |> Option.bind (fun v ->
          match v with
          | :? (Middleware.HotReloadCore.BindingOutcome list) as outcomes -> Some outcomes
          | _ -> None)
        |> Option.defaultValue []
      let declinedBindingsOf (response: EvalResponse) : Middleware.HotReloadCore.DeclinedBinding list =
        response.Metadata
        |> Map.tryFind "hotReloadDeclinedBindings"
        |> Option.bind (fun v ->
          match v with
          | :? (Middleware.HotReloadCore.DeclinedBinding list) as declined -> Some declined
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
          |> Array.filter (fun d -> d.Severity = Features.Diagnostics.DiagnosticSeverity.Blocking || d.Severity = Features.Diagnostics.DiagnosticSeverity.Warning)
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
        Features.ReloadBroadcast.broadcastCompileFailure summary diagnostics
        Log.warn "Reload failed for %s: %s\n%s" fileName ex.Message (ex.StackTrace |> Option.ofObj |> Option.defaultValue "")
      let routeFor (filePath: string) =
        Features.ReloadPlanning.routeFor
          (fun path ->
            match reloadBaselines.TryGetValue(IO.Path.GetFullPath path) with
            | true, baseline -> Some baseline
            | _ -> None)
          filePath
      /// One save's re-evaluation, bounded.
      ///
      /// Chesterton's fence: this budget is what guarantees `compilationLock`
      /// is handed back. The eval is posted with a token nothing else cancels,
      /// so without a deadline a single wedged submission owned the compiler
      /// for the rest of the session and every later save queued behind it
      /// forever. `PostAndTryAsyncReply` bounds the WAIT (so this workflow
      /// always reaches its `finally` and releases) and the linked CTS asks the
      /// eval itself to stop. Reports the budget it blew rather than an
      /// `Option`, so the caller has something to tell the user.
      let evalWithinBudget (request: EvalRequest) : Async<Result<EvalResponse, TimeSpan>> = async {
        let budget = TimeSpan.FromMilliseconds(float DevReload.DevReloadConfig.defaults.CompileBudgetMs)
        let localCts = new CancellationTokenSource(budget)
        match! actor.PostAndTryAsyncReply((fun rc -> Eval(request, localCts.Token, rc)), int budget.TotalMilliseconds) with
        | Some response ->
          localCts.Dispose()
          return Ok response
        | None ->
          // Deliberately NOT disposed: the abandoned eval still holds this
          // token, and disposing a source another thread is observing throws.
          // It releases itself when the budget fires.
          Log.warn "Hot reload: an eval did not finish within %.0fs — abandoning it so the next save can compile"
            budget.TotalSeconds
          return Error budget }
      // A change that only takes effect at startup restarts the app when SageFs
      // is the one running it. When the app was started some other way (an init
      // script, or by hand in the REPL) there is nothing SageFs can restart, so
      // the save falls back to the whole-file re-evaluation.
      //
      // The distinction is the honesty requirement, not an implementation
      // detail: taking credit for a restart that did not happen, and asking a
      // user to restart something SageFs already restarted, are both lies.
      let restartOrFallBack (fileName: string) first rest = async {
        // The planner owns the change → reason translation
        // (`ReloadChange.restartReasons`); this pipeline only decides who acts
        // on it.
        let reasons = Features.ReloadPlanning.ReloadChange.restartReasons first rest
        match AppRunner.state appRunner with
        | AppRun.AppRunState.Running _ ->
          Log.info "Run App: %s — %s; restarting the app" fileName (Features.ReloadPlanning.ReloadChange.describeAll first rest)
          // `requireRestart` ends the run with AppRunState.RestartRequired,
          // which the daemon's run-ended handler turns straight into a rebuild
          // and relaunch (AppRun.endRun → RunEnd.RebuildForChanges). SageFs
          // started this app, so SageFs brings it back: the user is told what
          // happened, not asked to do anything.
          let! _ = AppRunner.requireRestart appRunner first rest |> Async.AwaitTask
          Features.ReloadBroadcast.broadcastOutcome (Features.ReloadOutcome.ReloadOutcome.Restarted reasons)
          return SaveHandling.Reported
        | _ ->
          Log.info "Hot reload: %s — %s; no app is running under SageFs, so the whole file is re-evaluated instead"
            fileName (Features.ReloadPlanning.ReloadChange.describeAll first rest)
          return SaveHandling.FallBackWholeFile reasons }
      // What the running app holds for a kept binding, and whether its edited
      // initializer is still the same type. Reads only: never runs the
      // initializer, never writes the field.
      let probeKept (current: Features.ReloadPlanning.FileDecls) (decl: Features.ReloadPlanning.SourceDecl) = async {
        match Features.LiveStateEmit.probeCode current decl with
        | Error reason -> return Error reason
        | Ok code ->
          match! evalWithinBudget { Code = code; Args = Map.ofList [ "hotReload", box true ] } with
          | Error budget -> return Error (Features.LiveStateEmit.LiveStateError.TimedOut budget.TotalSeconds)
          | Ok response ->
            match response.EvaluationResult with
            | Error ex -> return Error (Features.LiveStateEmit.LiveStateError.EvalFailed ex.Message)
            | Ok output -> return Features.LiveStateEmit.parseProbe output }
      // Rule 2: where did each redefined value's copies go? Asked of the
      // agent in the process the app runs in. Every value that isn't provably
      // safe to patch comes back as the reason to restart, naming who kept it.
      let redefinitionRefusals
        (current: Features.ReloadPlanning.FileDecls)
        (redefined: Features.ReloadPlanning.SourceDecl list)
        : Features.ReloadPlanning.ReloadChange list =
        match redefined with
        | [] -> []
        | _ ->
          let named = redefined |> List.map (fun d -> Features.KeptState.Pending.bindingName current d, d)
          let shortName (binding: string) =
            named |> List.tryFind (fun (b, _) -> b = binding) |> Option.map (fun (_, d) -> d.Name) |> Option.defaultValue binding
          match result.Agent.ValueReads (named |> List.map fst) with
          | HostAgent.AgentUnavailable reason ->
            named
            |> List.map (fun (_, d) ->
              Features.ReloadPlanning.ReloadChange.ValueUntraceable(d.Name, sprintf "the app's agent didn't answer (%s)" reason))
          | HostAgent.AgentAnswered evidence ->
            let verdicts =
              evidence |> List.map (fun e -> Middleware.ValueReads.valueOf e, Middleware.ValueReads.verdictOf e)
            match Middleware.ValueReads.checkSave verdicts with
            | Middleware.ValueReads.SaveCheck.AllSafe -> []
            | Middleware.ValueReads.SaveCheck.Refused(first, rest) ->
              first :: rest
              |> List.choose (fun (binding, verdict) ->
                match verdict with
                | Middleware.ValueReads.ValueVerdict.SafeToPatch -> None
                | Middleware.ValueReads.ValueVerdict.HeldBy(site, seen) ->
                  Some(Features.ReloadPlanning.ReloadChange.ValueCopied(shortName binding, Middleware.ValueReads.describeHolder site seen))
                | Middleware.ValueReads.ValueVerdict.CannotTell why ->
                  Some(Features.ReloadPlanning.ReloadChange.ValueUntraceable(shortName binding, why)))
      // Re-emit the changed functions against the compiled module and report
      // what reached the running process. `carried` are the unedited private
      // `let mutable`s those functions use; they get stand-ins bound to the
      // app's own storage instead of being re-declared (LiveStateEmit).
      let patchInPlace
        (fileName: string)
        (filePath: string)
        (baseline: Features.ReloadPlanning.FileDecls)
        (current: Features.ReloadPlanning.FileDecls)
        (functions: Features.ReloadPlanning.SourceDecl list)
        (carried: Features.ReloadPlanning.SourceDecl list)
        (kept: Features.KeptState.Pending list)
        (recheck: unit -> Features.ReloadOutcome.RestartReason list)
        : Async<SaveHandling> = async {
            let keptValues = kept |> List.map _.Value
            let recordKept () =
              for k in kept do
                keptPending.[k.Value.Binding] <- k
            match functions with
            | [] ->
              // Only initializers of live state changed. Nothing to compile:
              // the app keeps its values and the save says so.
              reloadBaselines.[IO.Path.GetFullPath filePath] <- current
              recordKept ()
              let outcome =
                Features.ReloadOutcome.ReloadOutcome.ofPatchCounts 0 0 []
                |> Features.ReloadOutcome.ReloadOutcome.withKept keptValues
              Features.ReloadBroadcast.broadcastOutcome outcome
              Log.info "Hot reload: %s — %s" fileName (Features.ReloadOutcome.ReloadOutcome.describe outcome)
              return SaveHandling.Reported
            | _ ->
            match Middleware.CompilationContext.emitPatchCarrying filePath current functions carried with
            | Error (unreachable, reason) ->
              Log.info "Hot reload: %s can't carry '%s' into the patch: %s" fileName unreachable.Name (Features.LiveStateEmit.LiveStateError.describe reason)
              let user = functions |> List.tryHead |> Option.map _.Name |> Option.defaultValue fileName
              return! restartOrFallBack fileName (Features.ReloadPlanning.ReloadChange.UsesNonPublicMember (user, unreachable.Name)) []
            | Ok patch ->
            DevReload.broadcastCompiling (Some fileName)
            let request = { Code = patch.Code; Args = Map.ofList ["hotReload", box true] }
            match! evalWithinBudget request with
            | Error budget ->
              Features.ReloadBroadcast.broadcastEvent (Features.ReloadBroadcast.evalTimedOut fileName budget)
              return SaveHandling.Reported
            | Ok response ->
              match response.EvaluationResult with
              | Error ex ->
                broadcastEvalFailure filePath patch.LineOffset response ex
                return SaveHandling.Reported
              | Ok _ ->
                let reloaded = reloadedMethodsOf response
                match escalationOf (bindingOutcomesOf response) (declinedBindingsOf response) with
                | BindingEscalation.ForcesRestart (first, rest) ->
                  // A mutable binding TORE: the running process is already
                  // silently wrong (reads and writes now disagree about
                  // which field is live), no matter what else this eval
                  // patched cleanly. Restarting is the only outcome that
                  // clears that danger, so this takes the SAME forced-
                  // restart route a source-level MutableStateChanged does —
                  // it is never folded into the patch/no-effect reporting
                  // below, where a `Patched(n, m)` success could bury it.
                  return! restartOrFallBack fileName first rest
                | BindingEscalation.ExtraReasons extraReasons ->
                match Features.ReloadPlanning.confirmPatch baseline functions reloaded with
                | Features.ReloadPlanning.PatchOutcome.Applied ->
                  reloadBaselines.[IO.Path.GetFullPath filePath] <- current
                  // The counts come from the planner, not from a tally invented
                  // here: `confirmPatchAsOutcome` pairs the functions the user
                  // actually changed against the methods that were genuinely
                  // re-pointed, and names anything that was planned and did not
                  // land. `withExtraMisses` folds in whatever a mutable
                  // binding's OWN accounting found that confirmPatchAsOutcome
                  // cannot see on its own — a declined orphan leg, or an
                  // accessor pair whose preflight/Harmony application failed
                  // outright — so those no longer die in the log unreported.
                  // A canary-proven ineffective patch is folded in as a MISS
                  // with its own reason. Its declaration is named, because
                  // "nothing changed" is not actionable and "'x' was
                  // re-pointed but the running code did not change — its
                  // caller most likely inlined the old body" is.
                  // NOT folded in as misses: the canary produces false
                  // negatives (see HotReloadCore), so a BytesUnchanged reading
                  // must not turn a working reload into a reported no-op.
                  let ineffectiveReasons : Features.ReloadOutcome.RestartReason list = []
                  // The evidence a NAME cannot carry: which redirects re-pointed
                  // an entry point the RUNNING PROCESS actually calls. Under
                  // `--multiemit-` the single FSI assembly accumulates every
                  // eval, so a same-named older copy is always available to
                  // pair with, and re-pointing the wrong one changes nothing an
                  // app running from a `#load`ed or compiled copy will ever
                  // call. `reachedRunningProcessOf` is the fix: `HotReloadCore`
                  // now tracks, in `State.AppHolds`, the exact `MethodInfo` the
                  // app captured the last time a non-file-save eval (the
                  // startup/init script, or an interactive eval) defined it,
                  // and reports a name as reached only when the SPECIFIC copy
                  // that eval redirected is that recorded value, never by
                  // assembly kind. A name with no `AppHolds` entry (the app has
                  // not been observed to capture anything under it) is simply
                  // absent here, so `confirmPatchAsOutcome` cannot count it as
                  // landed: no evidence means never `Patched`, by construction.
                  let reachedRunningProcess = reachedRunningProcessOf response
                  let patched =
                    Features.ReloadPlanning.confirmPatchAsOutcome
                      baseline
                      functions
                      reloaded
                      reachedRunningProcess
                    |> Features.ReloadOutcome.ReloadOutcome.withExtraMisses
                         (extraReasons @ ineffectiveReasons)
                    |> Features.ReloadOutcome.ReloadOutcome.withKept keptValues
                  // Rule 2's second look: a redefined value was checked before
                  // the patch, and a read that raced the patch (a lazy forced
                  // in between, say) would have kept the OLD value. Ask again
                  // now that every read goes to the new getter; if anything
                  // kept a copy in the meantime, the honest outcome is a
                  // restart, whatever the count says.
                  let outcome =
                    match recheck () with
                    | [] -> patched
                    | raced -> Features.ReloadOutcome.ReloadOutcome.RestartRequired raced
                  recordKept ()
                  Features.ReloadBroadcast.broadcastOutcome outcome
                  Log.info "Hot reload: %s — %s (%s)"
                    fileName
                    (Features.ReloadOutcome.ReloadOutcome.describe outcome)
                    (functions |> List.map _.Name |> String.concat ", ")
                  return SaveHandling.Reported
                | Features.ReloadPlanning.PatchOutcome.RestartNeeded (first, rest) ->
                  return! restartOrFallBack fileName first rest
            }
      // Patch the process in place when only function bodies changed; anything
      // that takes effect at startup restarts the app (see ReloadPlanning).
      // Every exit reports exactly one terminal outcome, so a Compiling overlay
      // can never be left open and a refresh can never be sent for a save the
      // running process did not take.
      let reloadRunningApp (filePath: string) (baseline: Features.ReloadPlanning.FileDecls) : Async<SaveHandling> = async {
        let fileName = IO.Path.GetFileName filePath
        match Features.ReloadPlanning.extractDecls (IO.File.ReadAllText filePath) with
        | Error reason ->
          Features.ReloadBroadcast.broadcastOutcome
            (Features.ReloadOutcome.ReloadOutcome.CompileFailed (sprintf "%s does not parse: %s" fileName reason))
          return SaveHandling.Reported
        | Ok current ->
          match Features.ReloadPlanning.planReload baseline current with
          | Features.ReloadPlanning.ReloadPlan.PatchFunctions [] ->
            // The file's declarations are byte-identical to the running build.
            // Nothing to fetch and nothing to do, so this is reported as the
            // non-event it is — the old code broadcast a browser reload here,
            // which is the byte-identical refresh users read as breakage.
            Log.info "Hot reload: %s saved with no declaration change — the running app is already current" fileName
            Features.ReloadBroadcast.broadcastEvent (Features.ReloadBroadcast.unchanged fileName)
            return SaveHandling.Reported
          | Features.ReloadPlanning.ReloadPlan.PatchFunctions functions ->
            return! patchInPlace fileName filePath baseline current functions [] [] (fun () -> [])
          | Features.ReloadPlanning.ReloadPlan.PatchKeepingState (functions, first, rest) ->
            let state = first :: rest
            let carried =
              state
              |> List.choose (function
                | Features.ReloadPlanning.LiveState.Carried d -> Some d
                | Features.ReloadPlanning.LiveState.Kept _
                | Features.ReloadPlanning.LiveState.Redefined _ -> None)
            let keptDecls =
              state
              |> List.choose (function
                | Features.ReloadPlanning.LiveState.Kept d -> Some d
                | Features.ReloadPlanning.LiveState.Carried _
                | Features.ReloadPlanning.LiveState.Redefined _ -> None)
            let redefined =
              state
              |> List.choose (function
                | Features.ReloadPlanning.LiveState.Redefined d -> Some d
                | Features.ReloadPlanning.LiveState.Carried _
                | Features.ReloadPlanning.LiveState.Kept _ -> None)
            // Ask the running app about each kept binding BEFORE patching
            // anything: its live value for the notice, and whether the edited
            // initializer is still the same type. A retype, or a probe that
            // can't answer, means there's nothing safe to keep.
            let! probed =
              keptDecls
              |> List.map (fun d -> async {
                let! reading = probeKept current d
                return d, reading })
              |> Async.Sequential
            let refusals =
              probed
              |> Array.toList
              |> List.choose (fun (d, reading) ->
                match reading with
                | Ok (Features.LiveStateEmit.ProbeReading.Keeps _) -> None
                | Ok (Features.LiveStateEmit.ProbeReading.Retyped (was, now)) ->
                  Some (Features.ReloadPlanning.ReloadChange.MutableStateRetyped (d.Name, was, now))
                | Error reason ->
                  Log.warn "Hot reload: couldn't check the live value of '%s', so it isn't kept: %s" d.Name (Features.LiveStateEmit.LiveStateError.describe reason)
                  Some (Features.ReloadPlanning.ReloadChange.MutableStateChanged d.Name))
            // Rule 2: redefined values are patched only on the running app's
            // word that nothing kept a copy. SageFs running the app itself
            // (run_app) is a different process from the one the evidence
            // comes from, and a restart picks the value up anyway.
            let valueRefusals =
              match redefined, AppRunner.state appRunner with
              | [], _ -> []
              | _, AppRun.AppRunState.Running _ -> redefined |> List.map (fun d -> Features.ReloadPlanning.ReloadChange.ValueChanged d.Name)
              | _ -> redefinitionRefusals current redefined
            match valueRefusals @ refusals with
            | r :: rs -> return! restartOrFallBack fileName r rs
            | [] ->
              let kept =
                probed
                |> Array.toList
                |> List.choose (fun (d, reading) ->
                  match reading, Features.LiveStateEmit.initializerOf d with
                  | Ok (Features.LiveStateEmit.ProbeReading.Keeps preview), Ok init ->
                    Some
                      ({ Value =
                           { Binding = Features.KeptState.Pending.bindingName current d
                             KeptValue = preview
                             NewInitializer = init }
                         Decls = current
                         Decl = d } : Features.KeptState.Pending)
                  | _ -> None)
              // The redefined values go into the patch with the functions, in
              // file order, so a function that uses one compiles against it.
              let emitted = functions @ redefined |> List.sortBy _.StartLine
              let recheck () =
                redefinitionRefusals current redefined |> List.map Features.ReloadPlanning.ReloadChange.restartReason
              return! patchInPlace fileName filePath baseline current emitted carried kept recheck
          | Features.ReloadPlanning.ReloadPlan.RestartRequired (first, rest) ->
            return! restartOrFallBack fileName first rest }
      let onFileChanged (change: FileWatcher.FileChange) =
        let ext = IO.Path.GetExtension(change.FilePath)
        let kind = match change.Kind with
                   | FileWatcher.FileChangeKind.Changed -> "Modified"
                   | FileWatcher.FileChangeKind.Created -> "Created"
                   | FileWatcher.FileChangeKind.Deleted -> "Deleted"
                   | FileWatcher.FileChangeKind.Renamed -> "Renamed"
                   | FileWatcher.FileChangeKind.Overflow -> "Overflow"
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
          // Chesterton's fence: the wait for the compiler is BOUNDED, and the
          // timeout is REPORTED. An unbounded wait here was the subsystem's
          // worst failure mode: `ct` is cancelled only by a newer change to
          // THIS file, so one wedged eval held the semaphore and silently
          // disabled hot reload for every other file for the rest of the
          // session — no log, no SSE, no signal at all. An agent debugging it
          // concluded the file watcher was dead. The eval itself is now bounded
          // too (evalWithinBudget), which is what makes the holder always hand
          // the compiler back; this bound is what makes the wait visible in the
          // meantime instead of silent.
          let queueWait = TimeSpan.FromMilliseconds(float DevReload.DevReloadConfig.defaults.CompileQueueWaitMs)
          let! acquired = async {
            try return! compilationLock.WaitAsync(queueWait, ct) |> Async.AwaitTask
            with :? OperationCanceledException -> return false }
          match acquired with
          | false ->
            match ct.IsCancellationRequested with
            | true ->
              // Superseded by a newer save of the same file — the normal,
              // healthy case, and the newer save reports for both.
              Log.debug "File change cancelled while queued (superseded by newer save): %s"
                (IO.Path.GetFileName change.FilePath)
            | false ->
              let fileName = IO.Path.GetFileName change.FilePath
              Log.warn
                "Hot reload: %s waited %.0fs for the compiler and gave up — a previous compile is still holding it"
                fileName queueWait.TotalSeconds
              DevReload.DevReloadHealthTracker.transition
                (DevReload.Degraded (sprintf "a hot-reload compile has held the compiler for over %.0fs" queueWait.TotalSeconds))
              Features.ReloadBroadcast.broadcastEvent (Features.ReloadBroadcast.compilerBusy fileName queueWait)
          | true ->
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
                let! handling =
                  match routeFor filePath with
                  | Features.ReloadPlanning.ReloadRoute.PatchInPlace baseline -> reloadRunningApp filePath baseline
                  | Features.ReloadPlanning.ReloadRoute.ReevaluateWholeFile ->
                    async { return SaveHandling.FallBackWholeFile [] }
                match handling with
                | SaveHandling.Reported -> ()
                // A mutable's own value changed. Do NOT re-evaluate the whole
                // file: that re-declares every `let mutable` in it, and the
                // accessor-pair machinery then moves each getter AND setter onto
                // the fresh backing field — coherent (it cannot tear), but the
                // running app's LIVE value is replaced by the edited initializer.
                // Measured against a real host: the shape matrix's `mutable`
                // cell served the new initializer while this path reported
                // "Restart needed" — state silently reset behind a message that
                // said nothing had happened. `RestartReason.MutableModuleState`
                // already promises the opposite ("SageFs will not carry the old
                // value forward or reset it, because both silently lose
                // something"), so the running process is left exactly as it was
                // and the restart it needs is reported. The baseline is NOT
                // advanced, so the change stays pending until that restart.
                | SaveHandling.FallBackWholeFile restartReasons
                    when restartReasons
                         |> List.exists (function
                           | Features.ReloadOutcome.RestartReason.MutableModuleState _
                           | Features.ReloadOutcome.RestartReason.MutableStateTypeChanged _
                           // Rule 2: re-evaluating the whole file can't reach
                           // a copy the app kept either, and it would reset
                           // every `let mutable` in the file on the way.
                           | Features.ReloadOutcome.RestartReason.ValueCopiedByApp _
                           | Features.ReloadOutcome.RestartReason.ValueUntraceable _ -> true
                           | _ -> false) ->
                  let outcome = Features.ReloadOutcome.ReloadOutcome.RestartRequired restartReasons
                  Features.ReloadBroadcast.broadcastOutcome outcome
                  Log.info "Hot reload: %s — %s (live state left untouched)"
                    (IO.Path.GetFileName filePath)
                    (Features.ReloadOutcome.ReloadOutcome.describe outcome)
                | SaveHandling.FallBackWholeFile restartReasons ->
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
                    Features.ReloadBroadcast.broadcastOutcome
                      (Features.ReloadOutcome.ReloadOutcome.CompileFailed
                        (sprintf "%s does not parse: %s" (IO.Path.GetFileName filePath) exn.Message))
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
                match! evalWithinBudget request with
                | Error budget ->
                  Features.ReloadBroadcast.broadcastEvent
                    (Features.ReloadBroadcast.evalTimedOut (IO.Path.GetFileName filePath) budget)
                | Ok response ->
                match response.EvaluationResult with
                | Ok _ ->
                  // The file the session now holds IS the file on disk, so it is
                  // the baseline the NEXT save is diffed against. Without this the
                  // same startup-only change is re-reported on every later save.
                  let declsOnDisk = Features.ReloadPlanning.extractDecls fileContent
                  match declsOnDisk with
                  | Ok decls -> reloadBaselines.[IO.Path.GetFullPath filePath] <- decls
                  | Error _ -> reloadBaselines.TryRemove(IO.Path.GetFullPath filePath) |> ignore
                  // Capture RunTest from hot-reload discovery
                  match response.Metadata |> Map.tryFind "liveTestRunTest" with
                  | Some (:? (Features.LiveTesting.TestCase -> Async<Features.LiveTesting.TestResult>) as runTest) ->
                    setDynamicRunTest runTest
                  | _ -> ()
                  let reloaded = reloadedMethodsOf response
                  let fileName = IO.Path.GetFileName filePath
                  // How many definitions this save put in front of the process —
                  // the denominator of "N of M", so a zero numerator is visible
                  // as the non-event it is rather than reported as success.
                  let considered =
                    match declsOnDisk with
                    | Ok decls -> List.length decls.Decls
                    | Error _ -> List.length reloaded
                  // THE fix. This is where the shipped bug lived: the code here
                  // logged "Hot reloaded <file>" whenever ANY method had been
                  // detoured and left the detour middleware to refresh the
                  // browser. A whole-file re-evaluation re-declares the file's
                  // own types, so the handlers the user edited fail the detour
                  // matcher's parameter-type check while a few incidental
                  // BCL-signature helpers match — "reloaded" was reported, the
                  // page refreshed, and it served the old code.
                  //
                  // `restartReasons` is non-empty exactly when the planner
                  // already established that the user's change takes effect at
                  // startup. No number of incidental detours makes that change
                  // live, so the outcome is RestartRequired regardless: SageFs
                  // did not start this app (that is why this path was taken at
                  // all), so the message carries the remedy instead of claiming
                  // a restart that never happened.
                  let outcome =
                    match escalationOf (bindingOutcomesOf response) (declinedBindingsOf response) with
                    | BindingEscalation.ForcesRestart (first, rest) ->
                      // A mutable binding TORE while re-evaluating the whole
                      // file. This route is already the one SageFs takes
                      // when it cannot patch the running process in place —
                      // the loudest honest outcome is RestartRequired, same
                      // as any other startup-only change on this path, never
                      // a Patched success that buries the danger.
                      Features.ReloadOutcome.ReloadOutcome.RestartRequired(
                        Features.ReloadPlanning.ReloadChange.restartReasons first rest @ restartReasons)
                    | BindingEscalation.ExtraReasons extraReasons ->
                      match restartReasons with
                      | [] ->
                        Features.ReloadOutcome.ReloadOutcome.ofPatchCounts (List.length reloaded) considered []
                        |> Features.ReloadOutcome.ReloadOutcome.withExtraMisses extraReasons
                      | reasons -> Features.ReloadOutcome.ReloadOutcome.RestartRequired (reasons @ extraReasons)
                  Features.ReloadBroadcast.broadcastOutcome outcome
                  Log.info "Hot reload: %s — %s" fileName (Features.ReloadOutcome.ReloadOutcome.describe outcome)
                | Error ex -> broadcastEvalFailure filePath preprocessed.LineOffset response ex
              | FileWatcher.FileChangeAction.SoftReset ->
                Log.info "Project file changed — soft reset needed"
                let! _ = actor.PostAndAsyncReply(fun rc -> ResetSession rc)
                ()
              | FileWatcher.FileChangeAction.RecoverFromOverflow directory ->
                // The watch buffer overflowed: we cannot know which files
                // changed, so we cannot reload just one. Reset the whole
                // session to bring FSI back in sync with disk — but a reset
                // that happens in silence is exactly the bug this case
                // exists to fix. A save that got lost to the overflow must
                // still produce an outcome the user can see.
                Log.warn "File watcher buffer overflowed under %s — resetting session to recover" directory
                let! _ = actor.PostAndAsyncReply(fun rc -> ResetSession rc)
                Features.ReloadBroadcast.broadcastEvent (Features.ReloadBroadcast.watcherOverflow directory)
              | FileWatcher.FileChangeAction.Ignore -> ()
            with
            | :? OperationCanceledException ->
              Log.debug "File change cancelled (superseded by newer save): %s"
                (IO.Path.GetFileName change.FilePath)
            | ex ->
              // Chesterton's fence: if the actor mailbox crashes or the reply
              // throws, we must still close the Compiling→terminal lifecycle.
              // Without this catch-all, an unhandled exception leaves the
              // browser stuck on "⟳ Recompiling..." with no recovery path.
              Features.ReloadBroadcast.broadcastCompileFailure (sprintf "Internal error: %s" ex.Message) []
              Log.error "File watcher async failed: %s" (ex.ToString())
          finally
            compilationLock.Release() |> ignore
        })
      Some (FileWatcher.start config DevReload.DevReloadConfig.defaults onFileChanged)

  /// Record the source each loaded project's assembly was built from. That baseline
  /// is what makes a save patchable in place (see ReloadPlanning.routeFor): without
  /// it the save falls back to re-evaluating the whole file, which re-declares the
  /// file's own types and so can never re-point a handler the app captured at
  /// startup. Seeded for EVERY loaded project when the session starts, not just for
  /// a project `run_app` launched — a user who starts their app from an init script
  /// or by hand in the REPL gets the same hot reload.
  let seedReloadBaselines (projectDir: string) (assemblyPath: string) (sources: string array) =
    // Fail closed: if the assembly's write time can't be read, treat every source
    // file as untrustworthy rather than risk silently baking an unbuilt edit into
    // the baseline (see ReloadPlanning.baselineIsTrustworthy).
    let assemblyWriteTimeUtc =
      try IO.File.GetLastWriteTimeUtc assemblyPath
      with _ -> DateTime.MinValue
    for source in sources do
      let sourceWriteTimeUtc =
        try IO.File.GetLastWriteTimeUtc source
        with _ -> DateTime.MaxValue
      match Features.ReloadPlanning.baselineIsTrustworthy assemblyWriteTimeUtc sourceWriteTimeUtc with
      | false ->
        Log.warn "Hot reload: %s was modified after the build — it will be fully re-evaluated (not diff-patched) on its next save. → Rebuild the project so saves can be patched into the running process." source
      | true ->
        match Features.ReloadPlanning.extractDecls (IO.File.ReadAllText source) with
        | Ok decls -> reloadBaselines.TryAdd(IO.Path.GetFullPath source, decls) |> ignore
        | Error reason -> Log.warn "Hot reload: %s cannot be patched in place (%s)" source reason
    Log.debug "Hot reload: baselined %d source file(s) in %s" sources.Length projectDir

  let projectSources (projectDir: string) =
    IO.Directory.GetFiles(projectDir, "*.fs", IO.SearchOption.AllDirectories)
    |> Array.filter (fun f ->
      let n = f.Replace('\\', '/')
      not (n.Contains("/obj/") || n.Contains("/bin/")))

  let watchForHotReload (projectPath: string) (assemblyPath: string) =
    match workerConfig.Workflow, IO.Path.GetDirectoryName(IO.Path.GetFullPath projectPath) with
    | WorkflowTypes.SessionWorkflow.HotReload _, (NonNull projectDir) ->
      let sources = projectSources projectDir
      result.HotReloadStateRef.Value <- HotReloadState.watchByDirectory projectDir sources result.HotReloadStateRef.Value
      seedReloadBaselines projectDir assemblyPath sources
      Log.info "Run App: watching %d source file(s) in %s for hot reload"
        (HotReloadState.watchedInDirectory projectDir result.HotReloadStateRef.Value).Length projectDir
    | _ -> ()

  // Every loaded project gets its baseline at session start, so a save can be
  // patched in place however the user started their app. `run_app` re-seeds for
  // the project it launched (TryAdd, so this wins) and additionally opts that
  // project's files into the hot-reload watch set.
  match workerConfig.Workflow with
  | WorkflowTypes.SessionWorkflow.HotReload _ ->
    for projectPath, assemblyPath in result.ProjectTargets do
      match IO.Path.GetDirectoryName(IO.Path.GetFullPath projectPath) with
      | NonNull projectDir -> seedReloadBaselines projectDir assemblyPath (projectSources projectDir)
      | _ -> ()
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
      WorkerHttpTransport.startServer readyHandler result.HotReloadStateRef keptStateAccess projectFiles result.GetWarmupContext getRunTest result.Agent.TakeCoverage port
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
