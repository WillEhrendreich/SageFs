namespace SageFs

open System
open System.IO
open SageFs.Utils

[<RequireQualifiedAccess>]
type AutoOpenNamespacesOptOutResult =
  | Created of path: string
  | AlreadyDisabled of path: string
  | RequiresManualEdit of path: string

[<RequireQualifiedAccess>]
type AutoOpenNamespacesOptInResult =
  | Enabled of path: string
  | AlreadyEnabled
  | RequiresManualEdit of path: string

/// File operations for .SageFs/config.fsx. The config TYPES live in SageFs.Core/DirectoryConfigTypes.fs (they are
/// also compiled into the isolated FSI host, where a user's config.fsx is actually evaluated).
[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module DirectoryConfig =
  let empty = DirectoryConfigDefaults.empty

  let configDir (workingDir: string) =
    Path.Combine(workingDir, ".SageFs")

  let configPath (workingDir: string) =
    Path.Combine(configDir workingDir, "config.fsx")

  /// Must be a SINGLE-LINE expression: the config is evaluated by an FSI
  /// session via EvalExpressionNonThrowing, and a multi-line record update
  /// starting at column 1 is an offside error ("this token is offside of
  /// context started at position (1:3)"). A single line avoids that entirely.
  let autoOpenNamespacesOptOutTemplate =
    "{ DirectoryConfig.empty with AutoOpenNamespaces = false }"

  /// Template for re-enabling — the plain default config. Note: this must NOT
  /// be wrapped in braces (`{ DirectoryConfig.empty }` parses as a computation
  /// expression, not a record — "Invalid record, sequence or computation
  /// expression"). The bare expression is the default value itself.
  let autoOpenNamespacesOptInTemplate =
    "DirectoryConfig.empty"

  /// Evaluate a config.fsx expression as F# code, returning a DirectoryConfig. The script is arbitrary user code,
  /// so it runs in an isolated FSI host (SageFs.ConfigHost), never in this process; `workingDir` picks the SDK.
  /// The config file should contain a DirectoryConfig expression, e.g.:
  ///   { DirectoryConfig.empty with Load = Solution "MyApp.slnx" }
  let evaluateIn (DirPath workingDir) (content: string) : Result<DirectoryConfig, string> =
    ConfigHost.evaluate workingDir content |> Result.mapError ConfigHost.describeError

  /// `evaluateIn` for the current directory.
  let evaluate (content: string) : Result<DirectoryConfig, string> =
    evaluateIn (DirPath Environment.CurrentDirectory) content

  /// What a config file's evaluation means for the directory: its config, or the defaults with the reason logged.
  let configOfEvaluation (path: string) (evaluated: Result<DirectoryConfig, string>) : DirectoryConfig =
    match evaluated with
    | Ok cfg -> cfg
    | Error msg ->
      Log.warn "Failed to load %s: %s (using defaults)" path msg
      empty

  let load (workingDir: string) : DirectoryConfig option =
    let path = configPath workingDir
    match File.Exists path with
    | true -> Some(configOfEvaluation path (evaluateIn (DirPath workingDir) (File.ReadAllText path)))
    | false -> None

  /// `load`, awaiting the evaluation of the config instead of blocking on it. The answer lands in the same cache, so a
  /// synchronous `load` of the same config straight after returns at once.
  let loadAsync (workingDir: string) : Async<DirectoryConfig option> =
    async {
      let path = configPath workingDir
      match File.Exists path with
      | true ->
        let! evaluated = ConfigHost.evaluateAsync workingDir (File.ReadAllText path)
        return Some(configOfEvaluation path (evaluated |> Result.mapError ConfigHost.describeError))
      | false -> return None
    }

  let autoOpenNamespacesForDirectory (workingDir: string) =
    load workingDir
    |> Option.map (fun cfg -> cfg.AutoOpenNamespaces)
    |> Option.defaultValue true

  /// Resolve the options for a session that is being created in a directory, and answer whether warmup auto-opens namespaces.
  /// The options are what `config.fsx` sets, option by option, over what the settings layers hold (the directory's own
  /// `.SageFs/settings.json` over the global one, as `paths` says). When the options ask for an artifacts path, `askClaim` is
  /// asked whether the projects already set one, and the project's choice stays (`SessionOptions.reconcileArtifacts`).
  /// Everything refused on the way is logged and kept with the resolution, so the Settings page can say it.
  ///
  /// This is the one place the options a session's builds and worker use are recorded (`SessionOptionsRegistry`); reading a
  /// config (`load`, `loadAsync`, a page that shows it) never records anything. A directory that asks for nothing and refused
  /// nothing is forgotten. The record is the PENDING one: a session fixes its own options when its worker is first spawned,
  /// so creating another session here later cannot change one that is running.
  let prepareSession
    (paths: ConfigPaths)
    (askClaim: string list -> SessionOptions.ProjectClaim * string list)
    (projects: string list)
    (workingDir: string)
    : bool =
    let config = load workingDir
    let fromFile, fileRefusals =
      match config with
      | Some cfg -> SessionOptions.ofConfig cfg
      | None -> SessionOptions.none, []
    let fromSettings, settingRefusals = SessionOptionSettings.optionsAt paths
    let overlaid = SessionOptions.overlay fromSettings fromFile (fileRefusals @ settingRefusals)
    let options, claimRefusals =
      match overlaid.Options.Artifacts with
      | ProjectChooses -> overlaid.Options, []
      | ArtifactsIn _ ->
        let claim, couldNotAsk = askClaim projects
        couldNotAsk |> List.iter (fun why -> Log.warn "[session options] %s" why)
        SessionOptions.reconcileArtifacts claim overlaid.Options
    let resolution = { overlaid with Options = options; Refusals = overlaid.Refusals @ claimRefusals }
    resolution.Refusals |> List.iter (fun refusal -> Log.warn "[session options] %s: %s" workingDir (SessionOptions.describe refusal))
    SessionOptionsRegistry.rememberResolution workingDir resolution
    config |> Option.map (fun cfg -> cfg.AutoOpenNamespaces) |> Option.defaultValue true

  /// `prepareSession` against the daemon's own settings folder and MSBuild, for the projects of the session being created.
  let prepareForCreateOf (projects: string list) (workingDir: string) : bool =
    prepareSession (SettingsCatalog.pathsFor DaemonState.SageFsDir workingDir) (ProjectArtifactsProbe.claimOf ProjectArtifactsProbe.ask) projects workingDir

  /// `prepareForCreateOf` for a session whose projects are not known to the caller (no artifacts conflict is looked for).
  let prepareForCreate (workingDir: string) : bool =
    prepareForCreateOf [] workingDir

  let ensureAutoOpenNamespacesOptOut (workingDir: string) =
    try
      let path = configPath workingDir
      match File.Exists path with
      | false ->
        Directory.CreateDirectory(configDir workingDir) |> ignore
        File.WriteAllText(path, autoOpenNamespacesOptOutTemplate)
        Ok (AutoOpenNamespacesOptOutResult.Created path)
      | true ->
        match load workingDir with
        | Some cfg when not cfg.AutoOpenNamespaces ->
          Ok (AutoOpenNamespacesOptOutResult.AlreadyDisabled path)
        | Some _ ->
          Ok (AutoOpenNamespacesOptOutResult.RequiresManualEdit path)
        | None ->
          Directory.CreateDirectory(configDir workingDir) |> ignore
          File.WriteAllText(path, autoOpenNamespacesOptOutTemplate)
          Ok (AutoOpenNamespacesOptOutResult.Created path)
    with ex ->
      Error (sprintf "Failed to configure %s: %s" (configPath workingDir) ex.Message)

  /// Re-enable warmup auto-open for a directory. If the existing config is a
  /// plain opt-out (only AutoOpenNamespaces = false), rewrite it to the
  /// default; otherwise ask the user to edit manually (their config may have
  /// other customizations we must not clobber).
  let ensureAutoOpenNamespacesOptIn (workingDir: string) =
    try
      let path = configPath workingDir
      match File.Exists path with
      | false ->
        // No config file means the default (auto-open enabled) already applies.
        Ok (AutoOpenNamespacesOptInResult.AlreadyEnabled)
      | true ->
        match load workingDir with
        | Some cfg when cfg.AutoOpenNamespaces ->
          Ok (AutoOpenNamespacesOptInResult.AlreadyEnabled)
        | Some cfg ->
          // Only rewrite when the config is exactly the opt-out template
          // (no other customizations). Detect via structural equality with
          // the default except for AutoOpenNamespaces.
          let isBareOptOut =
            cfg.Load = empty.Load
            && cfg.DefaultArgs = empty.DefaultArgs
            && cfg.Gc = empty.Gc
            && cfg.SessionEnv = empty.SessionEnv
            && cfg.Artifacts = empty.Artifacts
            && cfg.ValueWalk = empty.ValueWalk
          match isBareOptOut with
          | true ->
            File.WriteAllText(path, autoOpenNamespacesOptInTemplate)
            Ok (AutoOpenNamespacesOptInResult.Enabled path)
          | false ->
            Ok (AutoOpenNamespacesOptInResult.RequiresManualEdit path)
        | None ->
          Ok (AutoOpenNamespacesOptInResult.AlreadyEnabled)
    with ex ->
      Error (sprintf "Failed to configure %s: %s" (configPath workingDir) ex.Message)
