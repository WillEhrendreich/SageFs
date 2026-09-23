module SageFs.ActorCreation


open SageFs.Middleware
open SageFs.Middleware.Tracing
open SageFs.ProjectLoading
open SageFs.AppState

let commonMiddleware: AppState.Middleware list = [
  // Must run FIRST: it never rewrites code, only ever short-circuits a
  // submission that has nothing for FSI to run (a bare module/namespace
  // header, or blank/comments only) — see EvaluableSubmission.fs. Every
  // Executable submission passes through it unmodified, so ordering it
  // ahead of the rewriting middleware below costs nothing on the normal
  // path and saves wasted rewrite work (plus a guaranteed FSI compile
  // error) on the "nothing to evaluate" path.
  EvaluableSubmission.nothingToEvaluateMiddleware
  FsiCompatibility.fsiCompatibilityMiddleware
  Directives.viBindMiddleware
  Directives.OpenDirective.openDirectiveMiddleware
  ComputationExpression.compExprMiddleware
  NonBlockingRun.nonBlockingRunMiddleware
  HotReloading.hotReloadingMiddleware
]

/// Hot reload used to seed its registry here, loading every project's assemblies into the WORKER. The session's agent owns
/// that now, in whichever process the user's code runs, so nothing is loaded on the worker's behalf.
let commonInitFunctions : (Solution -> string * obj) list = []

open System
open System.IO

/// Project directories for the file watcher. Derives from BOTH the Ionide
/// ProjectOptions list AND the manual-parse FSharpProjectOptions fallback
/// (FsProjects): when Ionide's WorkspaceLoader returns 0 projects (e.g. an
/// MSBuild eval failure in-process), loadSolution falls back to a manual
/// fsproj parse that fills only FsProjects. Without this, ProjectDirectories
/// is empty, the file watcher is skipped, and hot reload silently never fires
/// on file saves — the exact P0 hot-reload gap.
let projectDirectories (sln: Solution) : string list =
  let dirOf (projectFile: string) =
    let dir = Path.GetDirectoryName(projectFile)
    match String.IsNullOrEmpty(dir) with
    | true -> None
    | false -> Some (Path.GetFullPath(dir))
  sln.Projects
  |> List.choose (fun p -> dirOf p.ProjectFileName)
  |> List.append (sln.FsProjects |> List.choose (fun p -> dirOf p.ProjectFileName))
  |> List.distinct

type ActorArgs = {
  Middleware: AppState.Middleware list
  InitFunctions: (Solution -> string * obj) list
  Logger: Utils.ILogger
  OutStream: TextWriter
  UseAsp: bool
  LoadConfig: Args.ProjectLoadConfig
  IsBare: bool
  AutoOpenNamespaces: bool
  OnEvent: Features.Events.SageFsEvent -> unit
  Workflow: WorkflowTypes.SessionWorkflow
  /// Where the sessions' FSI lives. Production reads it from the environment (isolated by default); the test constructor
  /// pins the in-process reference implementation.
  FsiKind: SessionKinds.FsiSessionKind
}
  with
    /// Backward-compatible accessor.
    member this.HotReloadEnabled = WorkflowTypes.SessionWorkflow.isHotReloadActive this.Workflow

type ActorResult = {
  Actor: AppActor
  DiagnosticsChanged: IEvent<Features.DiagnosticsStore.T>
  CancelEval: unit -> System.Threading.Tasks.Task<bool>
  GetSessionState: unit -> SessionState
  GetEvalStats: unit -> Affordances.EvalStats
  GetWarmupFailures: unit -> WarmupFailure list
  GetWarmupContext: unit -> WarmupContext
  GetStartupConfig: unit -> StartupConfig option
  GetStatusMessage: unit -> string option
  /// Scan the session's process for the project's tests (the agent runs where the user's code runs).
  Agent: SessionAgent.SessionAgent
  ProjectDirectories: string list
  /// Shared hot-reload state — file watcher reads, API writes.
  HotReloadStateRef: HotReloadState.T ref
  /// IL coverage instrumentation maps from shadow-copy instrumentation.
  InstrumentationMaps: Features.LiveTesting.InstrumentationMap array
  /// Each project file with the assembly path the session actually loads
  /// (the shadow copy when shadowing), for running its entry point.
  ProjectTargets: (string * string) list
  /// Each loaded project classified as executable, library or test.
  ProjectRoles: SageFs.ProjectLoading.ClassifiedProject list
}

/// Phase 1: Create the actor and return callbacks immediately.
/// The FSI session init runs in the background — callers can start
/// serving MCP (get_fsi_status etc.) right away while warm-up proceeds.
let createActorImmediate a =
  // Every phase below reports through the SAME `SessionWarmUpProgress` event
  // `createFsiSession`'s own onProgress already uses — one wire
  // (`WARMUP_PROGRESS=`), fed by discovery, shadow-copy, instrumentation AND
  // FSI warm-up in turn, so a newcomer watching a big repo warm up sees
  // motion the whole way through, not just once FSI itself starts.
  let emitWarmupProgress step total message =
    a.OnEvent(Features.Events.SageFsEvent.SessionWarmUpProgress {| Step = step; Total = total; Message = message |})
  let originalSln =
    match a.IsBare with
    | true ->
      a.Logger.LogInfo "Bare session — skipping project discovery"
      ProjectLoading.emptySolution
    | false ->
      a.Logger.LogInfo "Discovering projects..."
      let sln = loadSolution a.Logger a.LoadConfig emitWarmupProgress
      a.Logger.LogInfo "Project loading complete."
      sln

  let shadowDir, sln, instrumentationMaps =
    match List.isEmpty originalSln.Projects && List.isEmpty originalSln.References with
    | true ->
      None, originalSln, ([||] : Features.LiveTesting.InstrumentationMap array)
    | false ->
      a.Logger.LogInfo "Creating shadow copies of assemblies..."
      emitWarmupProgress 1 2 "Creating shadow copies of assemblies"
      let dir = ShadowCopy.createShadowDir ()
      let shadowSln = ShadowCopy.shadowCopySolution dir originalSln
      a.Logger.LogInfo (sprintf "  Shadow copies in %s" dir)
      a.Logger.LogInfo "  Instrumenting assemblies for IL coverage..."
      emitWarmupProgress 2 2 "Instrumenting assemblies for IL coverage"
      let sw = System.Diagnostics.Stopwatch.StartNew()
      let targetPaths = shadowSln.Projects |> List.map (fun po -> po.TargetPath)
      let maps = Features.LiveTesting.CoverageInstrumenter.instrumentShadowSolution targetPaths
      sw.Stop()
      let totalProbes = maps |> Array.sumBy (fun m -> m.TotalProbes)
      a.Logger.LogInfo (sprintf "  IL coverage: %d probes across %d assemblies in %.0fms" totalProbes maps.Length sw.Elapsed.TotalMilliseconds)
      Some dir, shadowSln, maps

  AspireSetup.configureAspireIfNeeded a.Logger sln

  let customData = a.InitFunctions |> Seq.map (fun fn -> fn sln) |> Map.ofSeq
  let tracedBuild: AppState.PipelineBuildFn =
    fun middleware evalFn ->
      let namedMiddleware =
        Tracing.namedCommonMiddleware
        |> List.map (fun nm -> nm.Name, nm.Middleware)
        |> Map.ofList
      let named =
        middleware
        |> List.map (fun mw ->
          let name =
            namedMiddleware
            |> Map.tryFindKey (fun _ v -> obj.ReferenceEquals(v, mw))
            |> Option.defaultValue "Unknown"
          { Tracing.NamedMiddleware.Name = name; Middleware = mw })
      Tracing.buildTracedPipeline named "CoreEval" evalFn
  let appActor, diagnosticsChanged, cancelEval, getSessionState, getEvalStats, getWarmupFailures, getWarmupContext, getStartupConfig, getStatusMessage, sessionAgent =
    mkAppStateActor a.FsiKind a.Logger customData a.OutStream a.UseAsp originalSln shadowDir a.AutoOpenNamespaces a.HotReloadEnabled a.OnEvent tracedBuild sln
  let projDirs = projectDirectories originalSln
  let hotReloadStateRef = ref HotReloadState.empty
  { Actor = appActor; DiagnosticsChanged = diagnosticsChanged; CancelEval = cancelEval; GetSessionState = getSessionState; GetEvalStats = getEvalStats; GetWarmupFailures = getWarmupFailures; GetWarmupContext = getWarmupContext; GetStartupConfig = getStartupConfig; GetStatusMessage = getStatusMessage; Agent = sessionAgent; ProjectDirectories = projDirs; HotReloadStateRef = hotReloadStateRef; InstrumentationMaps = instrumentationMaps; ProjectTargets = SageFs.ProjectLoading.projectTargetsOf sln; ProjectRoles = SageFs.ProjectLoading.classifiedProjectsOf sln }

/// Phase 2: Add middleware — blocks until init() completes and the
/// eval actor is ready to process messages in its main loop.
let addMiddleware (result: ActorResult) (middleware: AppState.Middleware list) =
  result.Actor.PostAndAsyncReply(fun r -> AddMiddleware(middleware, r))

/// Combined for callers that don't need MCP before warm-up.
let createActor a =
  task {
    let result = createActorImmediate a
    do! addMiddleware result a.Middleware
    return result
  }

let mkCommonActorArgs logger useAsp (onEvent: Features.Events.SageFsEvent -> unit) (loadConfig: Args.ProjectLoadConfig) (isBare: bool) = {
  Middleware = commonMiddleware
  InitFunctions = commonInitFunctions
  UseAsp = useAsp
  LoadConfig = loadConfig
  IsBare = isBare
  AutoOpenNamespaces = true
  OutStream = stdout
  Logger = logger
  OnEvent = onEvent
  Workflow = WorkflowTypes.SessionWorkflow.Interactive
  FsiKind = SessionKinds.InProcess
}
