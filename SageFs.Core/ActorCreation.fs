module SageFs.ActorCreation

open SageFs.Middleware
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
let private projectDirectories (sln: Solution) : string list =
  sln.Projects
  |> List.choose (fun p ->
    let dir = Path.GetDirectoryName(p.ProjectFileName)
    if String.IsNullOrEmpty(dir) then None
    else Some (Path.GetFullPath(dir)))
  |> List.distinct

type ActorArgs = {
  Middleware: AppState.Middleware list
  InitFunctions: (Solution -> string * objnull) list
  Logger: Utils.ILogger
  OutStream: TextWriter
  UseAsp: bool
  ParsedArgs: Args.Arguments list
  OnEvent: Features.Events.SageFsEvent -> unit
}

type ActorResult = {
  Actor: AppActor
  DiagnosticsChanged: IEvent<Features.DiagnosticsStore.T>
  CancelEval: unit -> bool
  GetSessionState: unit -> SessionState
  GetEvalStats: unit -> Affordances.EvalStats
  GetWarmupFailures: unit -> WarmupFailure list
  GetStartupConfig: unit -> StartupConfig option
  ProjectDirectories: string list
}

/// Phase 1: Create the actor and return callbacks immediately.
/// The FSI session init runs in the background — callers can start
/// serving MCP (get_fsi_status etc.) right away while warm-up proceeds.
let createActorImmediate a =
  let parsedArgs = a.ParsedArgs
  let isBare = parsedArgs |> List.exists (function Args.Bare -> true | _ -> false)

  let originalSln =
    if isBare then
      a.Logger.LogInfo "Bare session — skipping project discovery"
      ProjectLoading.emptySolution
    else
      a.Logger.LogInfo "Discovering projects..."
      let sln = loadSolution a.Logger parsedArgs
      a.Logger.LogInfo "Project loading complete."
      sln

  let shadowDir, sln =
    if List.isEmpty originalSln.Projects && List.isEmpty originalSln.References then
      None, originalSln
    else
      a.Logger.LogInfo "Creating shadow copies of assemblies..."
      let dir = ShadowCopy.createShadowDir ()
      let shadowSln = ShadowCopy.shadowCopySolution dir originalSln
      a.Logger.LogInfo (sprintf "  Shadow copies in %s" dir)
      Some dir, shadowSln

  AspireSetup.configureAspireIfNeeded a.Logger sln

  let customData = a.InitFunctions |> Seq.map (fun fn -> fn sln) |> Map.ofSeq
  let appActor, diagnosticsChanged, cancelEval, getSessionState, getEvalStats, getWarmupFailures, getStartupConfig =
    mkAppStateActor a.Logger customData a.OutStream a.UseAsp originalSln shadowDir a.OnEvent sln
  let projDirs = projectDirectories originalSln
  { Actor = appActor; DiagnosticsChanged = diagnosticsChanged; CancelEval = cancelEval; GetSessionState = getSessionState; GetEvalStats = getEvalStats; GetWarmupFailures = getWarmupFailures; GetStartupConfig = getStartupConfig; ProjectDirectories = projDirs }

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

let mkCommonActorArgs logger useAsp (onEvent: Features.Events.SageFsEvent -> unit) args = {
  Middleware = commonMiddleware
  InitFunctions = commonInitFunctions
  UseAsp = useAsp
  ParsedArgs = args
  OutStream = stdout
  Logger = logger
  OnEvent = onEvent
}
