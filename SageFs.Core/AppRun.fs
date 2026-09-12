module SageFs.AppRun

open System
open System.IO
open System.Text.Json
open SageFs.ProjectLoading

/// Which executable project the caller asked to run.
[<RequireQualifiedAccess>]
type RunRequest =
  | DefaultTarget
  | Named of string

[<RequireQualifiedAccess>]
type RunTargetError =
  | NoExecutableProject of loadedProjects: string list
  | AmbiguousTarget of candidates: string list
  | UnknownProject of requested: string * candidates: string list
  | NotExecutable of project: string * role: ProjectRole

let projectName (projectPath: string) =
  match Path.GetFileNameWithoutExtension projectPath with
  | null -> projectPath
  | name -> name

let private fileName (projectPath: string) =
  match Path.GetFileName projectPath with
  | null -> projectPath
  | name -> name

module RunTargetError =
  let private roleLabel = function
    | ProjectRole.Executable -> "an executable"
    | ProjectRole.Library -> "a library"
    | ProjectRole.Test -> "a test project"

  let private listOrNone (names: string list) =
    match names with
    | [] -> "none"
    | names -> String.Join(", ", names)

  let describe = function
    | RunTargetError.NoExecutableProject loaded ->
      sprintf "No runnable project in this session (loaded: %s). → Create a session that includes a project with <OutputType>Exe</OutputType>." (listOrNone loaded)
    | RunTargetError.AmbiguousTarget candidates ->
      sprintf "This session has %d runnable projects: %s. → Pick one: pass its name to run_app, or use that project's Run button on the dashboard." candidates.Length (listOrNone candidates)
    | RunTargetError.UnknownProject (requested, candidates) ->
      sprintf "No project named '%s' in this session. → Runnable projects: %s." requested (listOrNone candidates)
    | RunTargetError.NotExecutable (name, role) ->
      sprintf "'%s' is %s, not an executable. → Only projects with <OutputType>Exe</OutputType> can run." name (roleLabel role)

let private isExecutable (p: ClassifiedProject) = p.Role = ProjectRole.Executable

let private matchesName (requested: string) (p: ClassifiedProject) =
  let eq (a: string) (b: string) = String.Equals(a, b, StringComparison.OrdinalIgnoreCase)
  match String.IsNullOrWhiteSpace requested with
  | true -> false
  | false ->
    eq requested p.Path
    || eq requested (fileName p.Path)
    || eq requested (projectName p.Path)
    || eq (Path.GetFullPath requested) (Path.GetFullPath p.Path)

/// An active project wins while it is still a loaded executable; otherwise
/// exactly one executable must exist.
let resolveTarget
  (request: RunRequest)
  (activeProject: string option)
  (projects: ClassifiedProject list)
  : Result<ClassifiedProject, RunTargetError> =
  let executables = projects |> List.filter isExecutable
  let names (ps: ClassifiedProject list) = ps |> List.map (fun p -> projectName p.Path)
  match request with
  | RunRequest.Named requested ->
    match projects |> List.tryFind (matchesName requested) with
    | Some p when isExecutable p -> Ok p
    | Some p -> Error (RunTargetError.NotExecutable (projectName p.Path, p.Role))
    | None -> Error (RunTargetError.UnknownProject (requested, names executables))
  | RunRequest.DefaultTarget ->
    let active = activeProject |> Option.bind (fun a -> executables |> List.tryFind (matchesName a))
    match active, executables with
    | Some p, _ -> Ok p
    | None, [ single ] -> Ok single
    | None, [] -> Error (RunTargetError.NoExecutableProject (names projects))
    | None, many -> Error (RunTargetError.AmbiguousTarget (names many))

type LaunchProfile = {
  Name: string
  ApplicationUrls: string list
  EnvironmentVariables: (string * string) list
}

[<RequireQualifiedAccess>]
type LaunchConfig =
  | NoProfile
  | Profile of LaunchProfile

let private jsonOptions =
  JsonDocumentOptions(CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true)

let private tryProp (name: string) (el: JsonElement) =
  match el.ValueKind with
  | JsonValueKind.Object ->
    match el.TryGetProperty name with
    | true, v -> Some v
    | false, _ -> None
  | _ -> None

let private stringValue (el: JsonElement) =
  match el.ValueKind with
  | JsonValueKind.String ->
    match el.GetString() with
    | null -> ""
    | s -> s
  | _ -> el.GetRawText()

/// The profile `dotnet run` would use: the first whose commandName is "Project".
let parseLaunchSettings (json: string) : Result<LaunchConfig, string> =
  try
    use doc = JsonDocument.Parse(json, jsonOptions)
    let profiles =
      match tryProp "profiles" doc.RootElement with
      | Some p -> p.EnumerateObject() |> Seq.toList
      | None -> []
    let isProjectProfile (p: JsonProperty) =
      match tryProp "commandName" p.Value with
      | Some c -> stringValue c = "Project"
      | None -> false
    match profiles |> List.tryFind isProjectProfile with
    | None -> Ok LaunchConfig.NoProfile
    | Some p ->
      let urls =
        match tryProp "applicationUrl" p.Value with
        | Some u ->
          (stringValue u).Split(';', StringSplitOptions.RemoveEmptyEntries ||| StringSplitOptions.TrimEntries)
          |> Array.toList
        | None -> []
      let env =
        match tryProp "environmentVariables" p.Value with
        | Some e -> e.EnumerateObject() |> Seq.map (fun kv -> kv.Name, stringValue kv.Value) |> Seq.toList
        | None -> []
      Ok (LaunchConfig.Profile { Name = p.Name; ApplicationUrls = urls; EnvironmentVariables = env })
  with :? JsonException as ex ->
    Error (sprintf "launchSettings.json is not valid JSON: %s" ex.Message)

[<RequireQualifiedAccess>]
type UrlPolicy =
  | ProjectConfigured
  | FreeLoopbackPortIfUnset
  /// A restarted run listens where the previous one did, so open browser tabs keep working.
  | ReusedAddress

/// Where the previous run of this app listened, if it is being restarted.
[<RequireQualifiedAccess>]
type PreviousAddress =
  | NoPreviousAddress
  | ReuseAddress of url: string

type LaunchPlan = {
  ContentRoot: string
  EnvironmentVariables: (string * string) list
  UrlPolicy: UrlPolicy
}

let freeLoopbackUrl = "http://127.0.0.1:0"
let contentRootVar = "ASPNETCORE_CONTENTROOT"
let urlsVar = "ASPNETCORE_URLS"

let private lastWins (pairs: (string * string) list) =
  pairs
  |> List.map fst
  |> List.distinct
  |> List.map (fun key -> key, pairs |> List.findBack (fun (k, _) -> k = key) |> snd)

/// Run a project the way `dotnet run` would: content root at the project dir,
/// launch-profile env vars, and profile urls as ASPNETCORE_URLS.
let planLaunch (projectPath: string) (config: LaunchConfig) (previous: PreviousAddress) : LaunchPlan =
  let projectDir =
    match Path.GetDirectoryName(Path.GetFullPath projectPath) with
    | null -> Path.GetFullPath "."
    | dir -> dir
  let profileEnv, urls =
    match config with
    | LaunchConfig.NoProfile -> [], []
    | LaunchConfig.Profile p -> p.EnvironmentVariables, p.ApplicationUrls
  // The project's own address wins; a restart otherwise keeps the previous one.
  let urlVars, policy =
    match urls, previous with
    | _ :: _, _ -> [ (urlsVar, String.Join(";", urls)) ], UrlPolicy.ProjectConfigured
    | [], PreviousAddress.ReuseAddress url -> [ (urlsVar, url) ], UrlPolicy.ReusedAddress
    | [], PreviousAddress.NoPreviousAddress -> [], UrlPolicy.FreeLoopbackPortIfUnset
  let env = lastWins ([ (contentRootVar, projectDir) ] @ profileEnv @ urlVars)
  { ContentRoot = env |> List.find (fun (k, _) -> k = contentRootVar) |> snd
    EnvironmentVariables = env
    UrlPolicy = policy }

[<RequireQualifiedAccess>]
type AppEndpoint =
  | Http of primaryUrl: string * otherUrls: string list
  | NoServer

/// Kestrel reports wildcard binds as [::], 0.0.0.0, * or +; a browser needs a real host.
let browsableUrl (address: string) =
  match address.IndexOf("://", StringComparison.Ordinal) with
  | -1 -> address
  | i ->
    let scheme = address.Substring(0, i + 3)
    let rest = address.Substring(i + 3)
    let host, tail =
      match rest.StartsWith("[", StringComparison.Ordinal), rest.IndexOf(']') with
      | true, close when close > 0 -> rest.Substring(0, close + 1), rest.Substring(close + 1)
      | _ ->
        match rest.IndexOfAny([| ':'; '/' |]) with
        | -1 -> rest, ""
        | cut -> rest.Substring(0, cut), rest.Substring(cut)
    match host with
    | "[::]" | "0.0.0.0" | "*" | "+" -> scheme + "localhost" + tail
    | _ -> address

let endpointFromAddresses (addresses: string list) : AppEndpoint =
  let urls = addresses |> List.map browsableUrl |> List.distinct
  let isHttp (u: string) = u.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
  match urls |> List.tryFind isHttp, urls with
  | _, [] -> AppEndpoint.NoServer
  | Some primary, all -> AppEndpoint.Http (primary, all |> List.filter (fun u -> u <> primary))
  | None, first :: rest -> AppEndpoint.Http (first, rest)

type RunningApp = {
  RunId: string
  Project: string
  EntryPoint: string
  Endpoint: AppEndpoint
  StartedAt: DateTime
}

[<RequireQualifiedAccess>]
type StartPhase =
  | RestartingIntoWebLive
  /// The session has no worker (its last build failed), so Run rebuilds it first.
  | RebuildingSession
  | LaunchingEntryPoint
  | RebuildingForChanges of first: SageFs.Features.ReloadPlanning.ReloadChange * rest: SageFs.Features.ReloadPlanning.ReloadChange list

/// The one app a session may run, as the user should see it.
[<RequireQualifiedAccess>]
type AppRunState =
  | NotRunning
  | Starting of project: string * phase: StartPhase * since: DateTime
  | Running of RunningApp
  | Exited of project: string * exitCode: int * at: DateTime
  | Crashed of project: string * reason: string * at: DateTime
  /// The app never ran: the worker didn't come up, or refused the launch —
  /// not a crash. A crash implies something ran and then died; this is a
  /// start that could not happen at all.
  | CouldNotStart of project: string * reason: SageFsError * at: DateTime
  /// The run was stopped because a save changed something that only takes effect at startup.
  | RestartRequired of project: string * first: SageFs.Features.ReloadPlanning.ReloadChange * rest: SageFs.Features.ReloadPlanning.ReloadChange list * at: DateTime
  /// A rebuild of the app failed: the code did not compile, the app did not crash.
  | BuildFailed of project: string * reason: string * at: DateTime * lastAddress: PreviousAddress
  /// Watching the run failed, so what the app is doing is no longer known: it
  /// may still be serving, or it may be gone. Says why, and what to do about it.
  | LostTrack of project: string * reason: SageFsError * at: DateTime

/// What the app state becomes when the session's worker is replaced.
/// An app being started survives (the card keeps saying what is happening), and
/// how the last run ended is kept; a running app died with the old worker.
let acrossWorkerRestart (state: AppRunState) : AppRunState =
  match state with
  | AppRunState.Starting _
  | AppRunState.Exited _
  | AppRunState.Crashed _
  | AppRunState.CouldNotStart _
  | AppRunState.RestartRequired _
  | AppRunState.BuildFailed _ -> state
  | AppRunState.Running _
  // Whatever the lost run was doing, it died with the worker that hosted it.
  | AppRunState.LostTrack _
  | AppRunState.NotRunning -> AppRunState.NotRunning

/// A worker's report that a run ended applies only while that run is current:
/// a stale report must not clobber a newer run or a stop the user already made.
let applyEnd (current: AppRunState) (runId: string) (final: AppRunState) : AppRunState =
  match current with
  | AppRunState.Running app when app.RunId = runId -> final
  | _ -> current

/// Which run a stop is for.
[<RequireQualifiedAccess>]
type StopScope =
  /// Whatever the worker is running or starting: the user pressed Stop.
  | CurrentApp
  /// Only this run: stopping an app no Run owns any more must not end a newer one.
  | OnlyRun of runId: string

/// Where a running app listened, so its relaunch can come back there.
let addressOf (app: RunningApp) : PreviousAddress =
  match app.Endpoint with
  | AppEndpoint.Http (url, _) -> PreviousAddress.ReuseAddress url
  | AppEndpoint.NoServer -> PreviousAddress.NoPreviousAddress

/// The owner's count of accepted Run and Stop presses for one session. Every
/// step of a run carries the generation its Run was given; once a later Run or
/// Stop takes a newer one, the older run's steps are stale and are dropped.
[<Struct>]
type RunGeneration = RunGeneration of int64

module RunGeneration =
  let initial = RunGeneration 0L
  let next (RunGeneration n) = RunGeneration (n + 1L)

/// The owner's answer to Run.
[<RequireQualifiedAccess>]
type RunClaim =
  /// Accepted: the app is Starting under this generation; `previous` is where it last listened.
  | Begun of generation: RunGeneration * phase: StartPhase * previous: PreviousAddress
  | AlreadyRunning of RunningApp
  | AlreadyStarting of project: string * phase: StartPhase

/// The owner's answer to Stop. Either way the stop takes a new generation, so
/// nothing an earlier Run still has in flight can be applied afterwards.
[<RequireQualifiedAccess>]
type StopClaim =
  /// A start in progress was cancelled: the app is Not running from now on.
  | CancelledStart of generation: RunGeneration * phase: StartPhase
  /// The worker must be asked to stop its app; its answer is recorded under this generation.
  | StopWorkerApp of generation: RunGeneration

/// Whether one step of a run was recorded.
[<RequireQualifiedAccess>]
type StepOutcome =
  | Applied
  /// A later Run or Stop owns the app now; this step changed nothing.
  | Stale of current: AppRunState

/// What the owner did with a worker's report that a run ended.
[<RequireQualifiedAccess>]
type RunEnd =
  | Recorded
  /// The run ended for changes and its generation still owns the app: the
  /// owner now says it is rebuilding, and the watcher rebuilds and relaunches.
  | RebuildForChanges of project: string * previous: PreviousAddress
  /// Not the current run (stopped, replaced, or its worker is gone): nothing changed.
  | NotCurrent

/// A session's app as its single owner holds it: the state the user sees and
/// the generation of the Run or Stop that last claimed it.
type AppSlot = {
  Generation: RunGeneration
  State: AppRunState
}

module AppSlot =
  let initial = { Generation = RunGeneration.initial; State = AppRunState.NotRunning }

  /// Run is accepted only when nothing is running or starting; the accepted
  /// run takes the next generation and the app is Starting from that moment.
  let claimRun (project: string) (phase: StartPhase) (at: DateTime) (slot: AppSlot) : RunClaim * AppSlot =
    match slot.State with
    | AppRunState.Running app -> RunClaim.AlreadyRunning app, slot
    | AppRunState.Starting (starting, startingPhase, _) -> RunClaim.AlreadyStarting (starting, startingPhase), slot
    | AppRunState.NotRunning
    | AppRunState.Exited _
    | AppRunState.Crashed _
    | AppRunState.CouldNotStart _
    | AppRunState.RestartRequired _
    | AppRunState.LostTrack _
    | AppRunState.BuildFailed _ ->
      // After a failed rebuild, come back where the app listened so open tabs keep working.
      let previous =
        match slot.State with
        | AppRunState.BuildFailed (_, _, _, address) -> address
        | _ -> PreviousAddress.NoPreviousAddress
      let generation = RunGeneration.next slot.Generation
      RunClaim.Begun (generation, phase, previous),
      { Generation = generation; State = AppRunState.Starting (project, phase, at) }

  /// Stop always takes the next generation. A start in progress is cancelled on
  /// the spot; anything else waits for the worker's answer.
  let claimStop (slot: AppSlot) : StopClaim * AppSlot =
    let generation = RunGeneration.next slot.Generation
    match slot.State with
    | AppRunState.Starting (_, phase, _) ->
      StopClaim.CancelledStart (generation, phase), { Generation = generation; State = AppRunState.NotRunning }
    | AppRunState.NotRunning
    | AppRunState.Running _
    | AppRunState.Exited _
    | AppRunState.Crashed _
    | AppRunState.CouldNotStart _
    | AppRunState.RestartRequired _
    | AppRunState.LostTrack _
    | AppRunState.BuildFailed _ -> StopClaim.StopWorkerApp generation, { slot with Generation = generation }

  /// A step is recorded only while its generation still owns the app.
  let advance (generation: RunGeneration) (next: AppRunState) (slot: AppSlot) : StepOutcome * AppSlot =
    match generation = slot.Generation with
    | true -> StepOutcome.Applied, { slot with State = next }
    | false -> StepOutcome.Stale slot.State, slot

  /// A worker's report that run `runId` ended applies only while that run is
  /// the current one. A run ended for changes whose generation still owns the
  /// app moves straight to rebuilding, so no Run or Stop can slip in between.
  let endRun (generation: RunGeneration) (runId: string) (final: AppRunState) (slot: AppSlot) : RunEnd * AppSlot =
    match slot.State with
    | AppRunState.Running app when app.RunId = runId ->
      match final with
      | AppRunState.RestartRequired (project, first, rest, at) when generation = slot.Generation ->
        RunEnd.RebuildForChanges (project, addressOf app),
        { slot with State = AppRunState.Starting (project, StartPhase.RebuildingForChanges (first, rest), at) }
      | _ -> RunEnd.Recorded, { slot with State = final }
    | _ -> RunEnd.NotCurrent, slot

  /// What the slot becomes when the session's worker is replaced.
  let acrossWorkerRestart (slot: AppSlot) : AppSlot =
    { slot with State = acrossWorkerRestart slot.State }

/// One wording for the app's state, wherever the user reads it.
let describeState (state: AppRunState) : string =
  match state with
  | AppRunState.NotRunning -> "Not running"
  | AppRunState.Starting (project, StartPhase.RestartingIntoWebLive, _) ->
    sprintf "Restarting the session with hot reload before starting %s…" (projectName project)
  | AppRunState.Starting (project, StartPhase.RebuildingSession, _) ->
    sprintf "Rebuilding %s…" (projectName project)
  | AppRunState.Starting (project, StartPhase.LaunchingEntryPoint, _) ->
    sprintf "Starting %s…" (projectName project)
  | AppRunState.Starting (project, StartPhase.RebuildingForChanges (first, rest), _) ->
    sprintf "Rebuilding %s: %s…" (projectName project) (SageFs.Features.ReloadPlanning.ReloadChange.describeAll first rest)
  | AppRunState.Running { Project = project; Endpoint = AppEndpoint.Http (url, _) } ->
    sprintf "%s is running at %s" (projectName project) url
  | AppRunState.Running { Project = project; Endpoint = AppEndpoint.NoServer } ->
    sprintf "%s is running (no web server)" (projectName project)
  | AppRunState.Exited (project, code, _) -> sprintf "%s exited with code %d" (projectName project) code
  | AppRunState.Crashed (project, reason, _) -> sprintf "%s crashed: %s" (projectName project) reason
  | AppRunState.CouldNotStart (project, reason, _) ->
    sprintf "%s could not start: %s" (projectName project) (SageFsError.describe reason)
  | AppRunState.BuildFailed (project, reason, _, _) ->
    sprintf "%s could not be rebuilt: %s\n→ Fix the build errors, then press ▶ Run to rebuild and start the app."
      (projectName project) reason
  | AppRunState.LostTrack (project, reason, _) ->
    sprintf "Lost track of %s: %s. → It may still be serving: press Run to take it over again, or Stop to end it."
      (projectName project) (SageFsError.describe reason)
  | AppRunState.RestartRequired (project, first, rest, _) ->
    sprintf "%s must restart: %s" (projectName project) (SageFs.Features.ReloadPlanning.ReloadChange.describeAll first rest)

/// The app state as HTTP and MCP clients read it.
type AppStateView = {
  State: string
  Message: string
  Urls: string list
  EntryPoint: string
  RunId: string
}

let toView (state: AppRunState) : AppStateView =
  let name =
    match state with
    | AppRunState.NotRunning -> "NotRunning"
    | AppRunState.Starting _ -> "Starting"
    | AppRunState.Running _ -> "Running"
    | AppRunState.Exited _ -> "Exited"
    | AppRunState.Crashed _ -> "Crashed"
    | AppRunState.CouldNotStart _ -> "CouldNotStart"
    | AppRunState.RestartRequired _ -> "RestartRequired"
    | AppRunState.BuildFailed _ -> "BuildFailed"
    | AppRunState.LostTrack _ -> "LostTrack"
  let urls, entryPoint, runId =
    match state with
    | AppRunState.Running app ->
      let urls =
        match app.Endpoint with
        | AppEndpoint.Http (primary, others) -> primary :: others
        | AppEndpoint.NoServer -> []
      urls, app.EntryPoint, app.RunId
    | AppRunState.NotRunning
    | AppRunState.Starting _
    | AppRunState.Exited _
    | AppRunState.Crashed _
    | AppRunState.CouldNotStart _
    | AppRunState.RestartRequired _
    | AppRunState.LostTrack _
    | AppRunState.BuildFailed _ -> [], "", ""
  { State = name
    Message = describeState state
    Urls = urls
    EntryPoint = entryPoint
    RunId = runId }
