module SageFs.ActorCreation


open SageFs.Middleware
open SageFs.Middleware.Tracing
open SageFs.ProjectLoading
open SageFs.AppState

let commonMiddleware: AppState.Middleware list = [
  FsiCompatibility.fsiCompatibilityMiddleware
  Directives.viBindMiddleware
  Directives.OpenDirective.openDirectiveMiddleware
  ComputationExpression.compExprMiddleware
  NonBlockingRun.nonBlockingRunMiddleware
  HotReloading.hotReloadingMiddleware
]

let commonInitFunctions = [ HotReloading.hotReloadingInitFunction ]

open System
open System.IO

/// Extract unique project directories from a Solution.
let projectDirectories (sln: Solution) : string list =
  sln.Projects
  |> List.choose (fun p ->
    let dir = Path.GetDirectoryName(p.ProjectFileName)
    match String.IsNullOrEmpty(dir) with
    | true -> None
    | false -> Some (Path.GetFullPath(dir)))
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
  HotReloadEnabled: bool
}

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
  ProjectDirectories: string list
  /// Shared hot-reload state — file watcher reads, API writes.
  HotReloadStateRef: HotReloadState.T ref
  /// IL coverage instrumentation maps from shadow-copy instrumentation.
  InstrumentationMaps: Features.LiveTesting.InstrumentationMap array
}

/// Phase 1: Create the actor and return callbacks immediately.
/// The FSI session init runs in the background — callers can start
/// serving MCP (get_fsi_status etc.) right away while warm-up proceeds.
let createActorImmediate a =
  let originalSln =
    match a.IsBare with
    | true ->
      a.Logger.LogInfo "Bare session — skipping project discovery"
      ProjectLoading.emptySolution
    | false ->
      a.Logger.LogInfo "Discovering projects..."
      let sln = loadSolution a.Logger a.LoadConfig
      a.Logger.LogInfo "Project loading complete."
      sln

  let shadowDir, sln, instrumentationMaps =
    match List.isEmpty originalSln.Projects && List.isEmpty originalSln.References with
    | true ->
      None, originalSln, ([||] : Features.LiveTesting.InstrumentationMap array)
    | false ->
      a.Logger.LogInfo "Creating shadow copies of assemblies..."
      let dir = ShadowCopy.createShadowDir ()
      let shadowSln = ShadowCopy.shadowCopySolution dir originalSln
      a.Logger.LogInfo (sprintf "  Shadow copies in %s" dir)
      a.Logger.LogInfo "  Instrumenting assemblies for IL coverage..."
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
  let appActor, diagnosticsChanged, cancelEval, getSessionState, getEvalStats, getWarmupFailures, getWarmupContext, getStartupConfig, getStatusMessage =
    mkAppStateActor a.Logger customData a.OutStream a.UseAsp originalSln shadowDir a.AutoOpenNamespaces a.HotReloadEnabled a.OnEvent tracedBuild sln
  let projDirs = projectDirectories originalSln
  let hotReloadStateRef = ref HotReloadState.empty
  { Actor = appActor; DiagnosticsChanged = diagnosticsChanged; CancelEval = cancelEval; GetSessionState = getSessionState; GetEvalStats = getEvalStats; GetWarmupFailures = getWarmupFailures; GetWarmupContext = getWarmupContext; GetStartupConfig = getStartupConfig; GetStatusMessage = getStatusMessage; ProjectDirectories = projDirs; HotReloadStateRef = hotReloadStateRef; InstrumentationMaps = instrumentationMaps }

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
  HotReloadEnabled = false
}
