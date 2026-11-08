namespace SageFs

open System

/// The catalog glue (unified-settings-design.md Phase A4): it ties a
/// `SettingDescriptor` to the layer store, so one call resolves a setting's
/// effective value across layers, and one call edits it — parse → persist to
/// the chosen layer → apply-if-live → re-resolve. The only place a raw string
/// becomes a typed `SettingValue` is the descriptor's `Parse`, which returns
/// *why* on failure; everything past it is legal by construction.
///
/// This module also holds the Phase-A pilot descriptors: test timeouts
/// (Live — wires the previously-dead `setTestTimeouts` path), MCP port
/// (RestartRequired), and bind host (Guarded — the loopback RCE guard is the
/// type, `LoopbackHost`, not a runtime check). Theme and auto-open are
/// existing-subsystem migrations and belong to Phase C.

/// Whether a session has a git checkout that can carry a repo-override layer.
/// "No repo" is a first-class domain case (a bare/scratch session with only the
/// global layer), modelled as a DU case rather than a null string — absence
/// here means something specific, so it says so by its name.
type RepoLayerLocation =
  | NoRepoCheckout
  | RepoRootAt of path: string

/// Where the file layers live for a session.
type ConfigPaths = {
  GlobalDir: string
  Repo: RepoLayerLocation
}

[<RequireQualifiedAccess>]
module SettingsCatalog =

  /// The raw value present at a file layer for this key, if any. A layer file that cannot be read is an error naming
  /// the file, never an empty layer: an empty one would resolve to the default and say nothing was wrong.
  let private rawAt (layer: ConfigLayer) (path: string) (key: string) : Result<string option, ConfigError> =
    match SettingsStore.readLayerChecked path with
    | SettingsStore.LayerRead.Layer values -> Ok (Map.tryFind key values)
    | SettingsStore.LayerRead.Unreadable reason -> Error (LayerValueInvalid(key, layer, reason))

  /// Resolve one descriptor's effective value + provenance across the file
  /// layers (global, and repo when the session has a checkout). A parse failure
  /// at any present layer is surfaced (with why) rather than silently dropped —
  /// a corrupt persisted value must not resolve to a wrong-but-plausible one.
  /// (Session-scope overrides are transient and not yet wired; the pure
  /// SettingsResolver already carries LSession via its layer list for when they
  /// land.)
  let resolveWith
    (inputs: ExternalInputs)
    (paths: ConfigPaths)
    (descriptor: SettingDescriptor)
    : Result<Provenance, ConfigError> =
    let parseAt (layer: ConfigLayer) (raw: Result<string option, ConfigError>) : Result<(ConfigLayer * SettingValue) option, ConfigError> =
      match raw with
      | Error why -> Error why
      | Ok None -> Ok None
      | Ok (Some r) ->
        match descriptor.Parse r with
        | Ok v -> Ok (Some(layer, v))
        | Error why -> Error (LayerValueInvalid(descriptor.Key, layer, ConfigError.describe why))

    // A read-only setting is never read from a layer file: nothing reads it back, so a stray key there is not a value
    // and a row that showed it as "global" would say something untrue.
    let readsFiles =
      match descriptor.Applicability with
      | ReadOnly _ -> false
      | Live | RestartRequired -> true
    let globalRaw =
      match readsFiles with
      | true -> rawAt LGlobal (SettingsStore.globalPath paths.GlobalDir) descriptor.Key
      | false -> Ok None
    let repoRaw =
      match readsFiles, paths.Repo with
      | true, RepoRootAt root -> rawAt LRepo (SettingsStore.repoPath root) descriptor.Key
      | true, NoRepoCheckout
      | false, _ -> Ok None
    let environmentRaw =
      ExternalSource.variable descriptor.External
      |> Option.bind (fun variable ->
        match inputs.Environment variable with
        | EnvSet raw -> Some raw
        | EnvUnset -> None)
    let flagRaw =
      ExternalSource.flag descriptor.External |> Option.bind (fun flag -> Map.tryFind flag inputs.Flags)

    let layers =
      [ parseAt LGlobal globalRaw
        parseAt LRepo repoRaw
        parseAt LEnvironment (Ok environmentRaw)
        parseAt LCommandLine (Ok flagRaw) ]
    match layers |> List.tryPick (fun parsed -> match parsed with Error why -> Some why | Ok _ -> None) with
    | Some why -> Error why
    | None ->
      let set = layers |> List.collect (fun parsed -> match parsed with Ok present -> Option.toList present | Error _ -> [])
      Ok (SettingsResolver.resolve descriptor.Default set)

  /// `resolveWith` against the saved layers alone: the callers that read a value the files hold (a session
  /// starting, the New Session form) and have no environment or flag to consult.
  let resolve (paths: ConfigPaths) (descriptor: SettingDescriptor) : Result<Provenance, ConfigError> =
    resolveWith ExternalInputs.none paths descriptor

  /// The layer files for a session working in `workingDir`: the global store, plus that directory's own
  /// `.SageFs/settings.json` as the repo layer. A directory that is blank or does not exist has no repo layer, and an
  /// edit aimed at it is refused with `LayerUnavailable` instead of writing somewhere the person did not mean.
  let pathsFor (globalDir: string) (workingDir: string) : ConfigPaths =
    match String.IsNullOrWhiteSpace workingDir || not (IO.Directory.Exists workingDir) with
    | true -> { GlobalDir = globalDir; Repo = NoRepoCheckout }
    | false -> { GlobalDir = globalDir; Repo = RepoRootAt workingDir }

  /// How the value the subsystem is using compares with what the layers resolve to. Read from the consumer,
  /// never from the file. A setting whose consumer reads the layers each time is in use as soon as it is saved.
  let inUse (descriptor: SettingDescriptor) (provenance: Provenance) : InUse =
    match descriptor.Current with
    | InUseReadEachTime -> InUseSameAsSaved
    | InUseFromConsumer read ->
      let value = read ()
      match descriptor.Render value = descriptor.Render provenance.Effective with
      | true -> InUseSameAsSaved
      | false -> InUseDiffers value

  /// When a variable or a flag wins over what the person saved, the row says so and names it.
  type OverrideNote =
    | NotOverridden
    | OverriddenBy of layer: ConfigLayer * name: string * saved: SettingValue

  let overrideNote (descriptor: SettingDescriptor) (provenance: Provenance) : OverrideNote =
    let nameOf (layer: ConfigLayer) : string option =
      match layer with
      | LEnvironment -> ExternalSource.variable descriptor.External
      | LCommandLine -> ExternalSource.flag descriptor.External
      | LDefault | LGlobal | LRepo | LSession -> None
    let savedValue =
      provenance.PerLayer
      |> List.filter (fun (layer, _) ->
        match layer with
        | LGlobal | LRepo | LSession -> true
        | LDefault | LEnvironment | LCommandLine -> false)
      |> List.tryLast
    match nameOf provenance.Source, savedValue with
    | Some name, Some(_, saved) -> OverriddenBy(provenance.Source, name, saved)
    | Some _, None
    | None, Some _
    | None, None -> NotOverridden

  /// Run `Apply` for every `Live` setting whose value some layer above the default set, so a saved per-test timeout
  /// is the one in force after a restart. Nothing else reads the file and pushes it into a running subsystem: before
  /// this the page kept showing the saved number while the daemon used the default.
  let applyAtStart (inputs: ExternalInputs) (paths: ConfigPaths) (descriptors: SettingDescriptor list) : unit =
    for descriptor in descriptors do
      match descriptor.Applicability with
      | Live ->
        match resolveWith inputs paths descriptor with
        | Ok provenance ->
          match provenance.Source with
          | LDefault -> ()
          | LGlobal | LRepo | LSession | LEnvironment | LCommandLine -> descriptor.Apply provenance.Effective
        | Error _ -> ()
      | RestartRequired
      | ReadOnly _ -> ()

  /// Edit a setting at a persisted layer: parse the raw input (the sole
  /// validation), persist the rendered value to that layer's file, apply it
  /// live when the descriptor is `Live`, and return the freshly re-resolved
  /// provenance. `LSession`/`LDefault` are not persisted layers — edits target
  /// `LGlobal` or `LRepo`.
  let edit
    (paths: ConfigPaths)
    (layer: ConfigLayer)
    (raw: string)
    (descriptor: SettingDescriptor)
    : Result<Provenance, ConfigError> =
    match descriptor.Applicability, descriptor.Parse raw with
    | ReadOnly reason, _ -> Error (LayerUnavailable (ReadOnlyReason.describe reason))
    | _, Error why -> Error why
    | _, Ok value ->
      let persisted =
        match layer with
        | LGlobal -> SettingsStore.setKey (SettingsStore.globalPath paths.GlobalDir) descriptor.Key (descriptor.Render value)
        | LRepo ->
          match paths.Repo with
          | RepoRootAt root -> SettingsStore.setKey (SettingsStore.repoPath root) descriptor.Key (descriptor.Render value)
          | NoRepoCheckout -> Error (LayerUnavailable "this session has no repo checkout, so there is no repo layer to write to")
        | LDefault | LSession | LEnvironment | LCommandLine ->
          Error (LayerUnavailable "edits persist to the global or repo layer, not default, session, environment or flag")
      match persisted with
      | Error e -> Error e
      | Ok () ->
        match descriptor.Applicability with
        | Live -> descriptor.Apply value
        | RestartRequired | ReadOnly _ -> ()
        resolve paths descriptor

  /// Clear a persisted override at a layer so the value falls back to the
  /// next-lower layer, then re-resolve.
  let clear
    (paths: ConfigPaths)
    (layer: ConfigLayer)
    (descriptor: SettingDescriptor)
    : Result<Provenance, ConfigError> =
    let cleared =
      match layer with
      | LGlobal -> SettingsStore.clearKey (SettingsStore.globalPath paths.GlobalDir) descriptor.Key
      | LRepo ->
        match paths.Repo with
        | RepoRootAt root -> SettingsStore.clearKey (SettingsStore.repoPath root) descriptor.Key
        | NoRepoCheckout -> Ok ()
      | LDefault | LSession | LEnvironment | LCommandLine -> Error (LayerUnavailable "only the global or repo layer can be cleared")
    match cleared with
    | Error e -> Error e
    | Ok () -> resolve paths descriptor

  // ---------------------------------------------------------------------------
  // Phase-A pilot descriptors
  // ---------------------------------------------------------------------------

  let private secondsToTimeoutValue (raw: string) : Result<SettingValue, ConfigError> =
    match Double.TryParse raw with
    | false, _ -> Error (Malformed (sprintf "expected a number of seconds, got '%s'" raw))
    | true, s when not (Double.IsFinite s) -> Error (OutOfRange (sprintf "expected a finite number of seconds, got '%s'" raw))
    | true, s ->
      // ValidTimeout.create predates the config core and still returns a string
      // error; adapt it to the typed OutOfRange at this boundary.
      match ValidTimeout.create (TimeSpan.FromSeconds s) with
      | Ok t -> Ok (VTimeout t)
      | Error why -> Error (OutOfRange why)

  let private renderTimeout (v: SettingValue) : string =
    match v with
    | VTimeout t -> (ValidTimeout.value t).TotalSeconds |> sprintf "%g"
    | _ -> ""

  /// Default per-test timeout as a legal value (5s is within ValidTimeout's
  /// 1s-10min range, so this never falls through).
  let private defaultTimeout (seconds: float) : SettingValue =
    match ValidTimeout.create (TimeSpan.FromSeconds seconds) with
    | Ok t -> VTimeout t
    | Error _ -> VTimeout (match ValidTimeout.create Timeouts.perTestTimeoutFallback with Ok t -> t | Error _ -> failwith "the per-test timeout fallback must be valid")

  /// The timeout a running subsystem holds, as a value. One outside the legal range (an environment variable can
  /// hold any positive number of seconds) has no `ValidTimeout`, so it is shown as the fallback and the row's
  /// source says where it came from.
  let private timeoutInForce (held: TimeSpan) : SettingValue =
    match ValidTimeout.create held with
    | Ok t -> VTimeout t
    | Error _ -> defaultTimeout held.TotalSeconds

  /// Live: the per-test timeout. Wires the previously-dead setTestTimeouts path
  /// — a validated runtime setter that nothing could reach — to a real edit.
  let perTestTimeout : SettingDescriptor = {
    Key = "livetest.perTestTimeoutSeconds"
    Name = "Per-test timeout"
    Description = "Per-test timeout for live testing (seconds, 1-600)."
    Category = LiveTesting
    Scope = RepoOverridable
    Applicability = Live
    Default = defaultTimeout 5.0
    Parse = secondsToTimeoutValue
    Render = renderTimeout
    Apply = fun v -> match v with VTimeout t -> Timeouts.setPerTestTimeout (ValidTimeout.value t) | _ -> ()
    External = FromEnvironment "SAGEFS_PER_TEST_TIMEOUT_SECONDS"
    Current = InUseFromConsumer (fun () -> timeoutInForce (Timeouts.perTestDefault ()))
  }

  /// Live: the whole-run timeout for a live-test cycle.
  let globalTestRunTimeout : SettingDescriptor = {
    Key = "livetest.globalTestRunTimeoutSeconds"
    Name = "Test-run timeout"
    Description = "Total timeout for one live-test run (seconds, 1-600)."
    Category = LiveTesting
    Scope = RepoOverridable
    Applicability = Live
    Default = defaultTimeout 120.0
    Parse = secondsToTimeoutValue
    Render = renderTimeout
    Apply = fun v -> match v with VTimeout t -> Timeouts.setGlobalTestRunTimeout (ValidTimeout.value t) | _ -> ()
    External = NoExternalSource
    Current = InUseFromConsumer (fun () -> timeoutInForce (Timeouts.globalTestRun ()))
  }

  /// RestartRequired: the MCP server port. Typed as a bounded Port, so an
  /// out-of-range value cannot be persisted; applied on the next daemon start.
  let mcpPort : SettingDescriptor = {
    Key = "daemon.mcpPort"
    Name = "MCP port"
    Description = "MCP server port (1024-65535). Applies on the next daemon restart."
    Category = DaemonSettings
    Scope = Global
    Applicability = RestartRequired
    Default = VPort (match Port.create SageFsConfig.DefaultMcpPort with Ok p -> p | Error _ -> failwith "default MCP port must be valid")
    Parse = fun raw ->
      match Int32.TryParse raw with
      | false, _ -> Error (Malformed (sprintf "expected a port number, got '%s'" raw))
      | true, n -> Port.create n |> Result.map VPort
    Render = fun v -> match v with VPort p -> string (Port.value p) | _ -> ""
    Apply = ignore
    External = FromEnvironmentOrFlag("SAGEFS_MCP_PORT", SettingsBootstrap.McpPortFlag)
    // The port the daemon bound, recorded when it started. A process that is not a daemon has none to report.
    Current =
      InUseFromConsumer (fun () ->
        match SettingsBootstrap.launch () |> Option.map (fun l -> Port.create l.McpPort) with
        | Some (Ok p) -> VPort p
        | Some (Error _) | None -> VPort (match Port.create SageFsConfig.DefaultMcpPort with Ok p -> p | Error _ -> failwith "default MCP port must be valid"))
  }

  /// Read-only: the HTTP bind host. Typed as LoopbackHost, whose parse refuses
  /// any non-loopback value — so a LAN/all-interfaces bind (remote code
  /// execution, since SageFs evals F# with no auth) is structurally
  /// unrepresentable, not merely rejected at runtime. The daemon takes it from
  /// `SAGEFS_BIND_HOST` alone, so the row shows that variable and offers no
  /// control: changing it from a page the daemon serves points the trust the
  /// wrong way.
  let bindHost : SettingDescriptor = {
    Key = "daemon.bindHost"
    Name = "Bind host"
    Description = "Loopback bind address for the HTTP servers (localhost, 127.0.0.1, ::1). Non-loopback is refused."
    Category = DaemonSettings
    Scope = Global
    Applicability = ReadOnly SecurityBoundary
    Default = VBindHost SageFsConfig.LoopbackHost.Localhost
    // LoopbackHost.parse predates the config core and returns a string error;
    // adapt it to the typed NotLoopback at this boundary.
    Parse = fun raw ->
      match SageFsConfig.LoopbackHost.parse raw with
      | Ok h -> Ok (VBindHost h)
      | Error why -> Error (NotLoopback why)
    Render = fun v -> match v with VBindHost h -> SageFsConfig.LoopbackHost.urlHost h | _ -> ""
    Apply = ignore
    External = FromEnvironment "SAGEFS_BIND_HOST"
    Current =
      InUseFromConsumer (fun () ->
        match SageFsConfig.BindHost with
        | Ok h -> VBindHost h
        | Error _ -> VBindHost SageFsConfig.LoopbackHost.Localhost)
  }

  /// Read-only: the directory SageFs keeps its state in. The settings file lives inside it, so a value saved here
  /// could never be read back. Shown with its source so a throwaway daemon's isolated directory is visible.
  let dataDirectory : SettingDescriptor =
    let valueOf (path: string) : SettingValue =
      match DirectoryPath.create path with
      | Ok d -> VDir d
      | Error _ -> VDir (match DirectoryPath.create "" with Ok d -> d | Error _ -> failwith "the empty directory must be valid")
    { Key = "daemon.dataDirectory"
      Name = "Data directory"
      Description = "Where this daemon keeps its settings, sessions, logs and caches."
      Category = DaemonSettings
      Scope = Global
      Applicability = ReadOnly HoldsTheSettingsFile
      Default = valueOf (Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) |> fun home -> IO.Path.Combine(home, ".SageFs"))
      Parse = fun raw -> DirectoryPath.create raw |> Result.map VDir
      Render = fun v -> SettingValue.dir v |> Option.map DirectoryPath.value |> Option.defaultValue ""
      Apply = ignore
      External = FromEnvironment DataDirChoice.envVar
      Current = InUseFromConsumer (fun () -> valueOf (DataDirChoice.dataDirectory ())) }

  /// Read-only: whether a connection with no token may act. Changing it from a page the daemon serves would point the
  /// trust the wrong way, so it is set where the daemon starts. An unreadable value fails closed in the daemon
  /// (`TokenRequired`); the row says the value could not be read.
  let identityPolicy : SettingDescriptor =
    let names = Capability.IdentityPolicy.all |> List.map Capability.IdentityPolicy.toToken
    let valueOf (policy: Capability.IdentityPolicy) : SettingValue =
      match EnumValue.create names (Capability.IdentityPolicy.toToken policy) with
      | Ok e -> VEnum e
      | Error _ -> failwith "every identity policy is one of the names"
    { Key = "daemon.identityPolicy"
      Name = "Identity policy"
      Description = "Whether a connection with no member token may act. TokenRequired lets it read status and nothing more. A value that cannot be read is treated as TokenRequired."
      Category = DaemonSettings
      Scope = Global
      Applicability = ReadOnly SecurityBoundary
      Default = valueOf Capability.IdentityPolicy.defaultPolicy
      Parse =
        fun raw ->
          match Capability.IdentityPolicy.tryParse raw with
          | Ok policy -> Ok (valueOf policy)
          | Error unknown -> Error (NotAMember(unknown.Given, unknown.Expected))
      Render = fun v -> SettingValue.enum v |> Option.map EnumValue.value |> Option.defaultValue ""
      Apply = ignore
      External = FromEnvironment Capability.CapabilityTransport.policyEnvVar
      Current =
        InUseFromConsumer (fun () ->
          valueOf (Capability.CapabilityTransport.policyFromEnvironment (fun name -> match Environment.GetEnvironmentVariable name with | null -> "" | v -> v))) }

  /// A credential shown as set or not set, never as itself: it has no external source for the resolver to read, because
  /// a refused parse of a secret would quote it. The row only asks whether the variable is there.
  let private credential (key: string) (name: string) (help: string) (variable: string) : SettingDescriptor =
    { Key = key
      Name = name
      Description = help
      Category = DaemonSettings
      Scope = Global
      Applicability = ReadOnly Secret
      Default = VToggle Off
      Parse = fun _ -> Error (Malformed "a credential is never read or written here")
      Render = fun v -> match SettingValue.toggle v with Some On -> "set" | Some Off -> "not set" | None -> ""
      Apply = ignore
      External = NoExternalSource
      Current =
        InUseFromConsumer (fun () ->
          match Environment.GetEnvironmentVariable variable with
          | null | "" -> VToggle Off
          | _ -> VToggle On) }

  let memberToken : SettingDescriptor =
    credential "daemon.memberToken" "Member token (SAGEFS_MEMBER_TOKEN)"
      "The token `sagefs mcp` sends so its connection acts as a minted member. Its value is never shown; the row says only whether it is set in this process."
      Capability.CapabilityTransport.bridgeEnvVar

  let operatorToken : SettingDescriptor =
    credential "daemon.operatorToken" "Operator token (SAGEFS_OPERATOR_TOKEN)"
      "The operator credential `sagefs mcp` sends so `tools/list` shows the full list. Its value is never shown; the row says only whether it is set in this process."
      Capability.CapabilityTransport.operatorEnvVar

  /// Read-only: whether the daemon skipped restoring the previous sessions at start. Only the flag sets it.
  let noResume : SettingDescriptor = {
    Key = "daemon.noResume"
    Name = "Skip restoring sessions at start"
    Description = "On when the daemon was started with --no-resume: it did not bring back the sessions that were open when it last stopped."
    Category = DaemonSettings
    Scope = Global
    Applicability = ReadOnly LaunchOnly
    Default = VToggle Off
    Parse = fun raw -> match raw with "on" -> Ok (VToggle On) | "off" -> Ok (VToggle Off) | other -> Error (NotAMember(other, [ "on"; "off" ]))
    Render = fun v -> match SettingValue.toggle v with Some On -> "on" | Some Off -> "off" | None -> ""
    Apply = ignore
    External = FromFlag SettingsBootstrap.NoResumeFlag
    Current =
      InUseFromConsumer (fun () ->
        match SettingsBootstrap.launch () |> Option.bind (fun l -> Map.tryFind SettingsBootstrap.NoResumeFlag l.Flags) with
        | Some _ -> VToggle On
        | None -> VToggle Off) }

  /// Live: the directory a new session defaults to. Typed as DirectoryPath, so
  /// only an absolute path (or the unset empty) can be stored — a relative path
  /// that would resolve against the daemon's cwd is structurally rejected. Read
  /// when the New Session form renders and when a session is created; no live
  /// subsystem to push to, so Apply is a no-op.
  let sessionDefaultWorkingDir : SettingDescriptor = {
    Key = "session.defaultWorkingDirectory"
    Name = "Default working directory"
    Description = "The directory a new session starts in (absolute path). Leave empty for no default. Pre-fills the New Session form."
    Category = SessionSettings
    Scope = RepoOverridable
    Applicability = Live
    Default = (match DirectoryPath.create "" with Ok d -> VDir d | Error _ -> failwith "the empty directory must be valid")
    Parse = fun raw -> DirectoryPath.create raw |> Result.map VDir
    Render = fun v -> match v with VDir d -> DirectoryPath.value d | _ -> ""
    Apply = ignore
    External = NoExternalSource
    Current = InUseReadEachTime
  }

  /// Live: whether the daemon's hygiene sweep removes what is provably safe on its own. Off keeps the sweep's looking and its
  /// salvage (uncommitted worktree files go to a branch either way) and stops the removing. The switch is read each time the
  /// sweep runs, so a change applies at once.
  let hygieneAutoTidy : SettingDescriptor = {
    Key = "hygiene.autoTidy"
    Name = "Tidy the workspace on its own"
    Description = "When on, the daemon removes what agents left behind once it is provably safe: worktrees whose work has reached the base branch and that have been quiet for the grace, expired logs and temp runs, and merged agent branches. Off stops the removing; the daemon still records what it sees and saves uncommitted worktree files to a salvage branch."
    Category = DaemonSettings
    Scope = Global
    Applicability = Live
    Default = VToggle On
    Parse = fun raw -> match raw with "on" -> Ok (VToggle On) | "off" -> Ok (VToggle Off) | other -> Error (NotAMember(other, [ "on"; "off" ]))
    Render = fun v -> match SettingValue.toggle v with Some On -> "on" | Some Off -> "off" | None -> ""
    Apply = fun v -> match SettingValue.toggle v with Some t -> HygieneSwitch.setAutoTidy t | None -> ()
    External = NoExternalSource
    Current = InUseFromConsumer (fun () -> VToggle (HygieneSwitch.autoTidyNow ()))
  }

  /// The Phase-A pilot catalog.
  let pilots : SettingDescriptor list =
    [ sessionDefaultWorkingDir; perTestTimeout; globalTestRunTimeout; mcpPort; bindHost ]

  /// Every setting with a row on the Settings page that lives in Core: the pilots, the settings read from an
  /// environment variable when a process starts, and the read-only rows. The two session-agent settings are added by
  /// the page, because they are declared in `SessionAgent`, which compiles after this file.
  let all : SettingDescriptor list =
    pilots @ EnvBackedRows.descriptors @ [ dataDirectory; noResume; identityPolicy; memberToken; operatorToken; hygieneAutoTidy ]

  /// The Live Bindings view a workflow shows for the app at `paths`: each axis resolved across the layers, and an axis nobody set
  /// follows the workflow's recommended view. An axis whose stored value will not parse (a layer file edited by hand) also falls
  /// back to the recommendation and says its source is the default, because a page that cannot draw its bindings over a typo is
  /// worse than one that draws the recommended view. The Settings panel still surfaces the bad value for the pilots.
  let viewForTab (paths: ConfigPaths) (workflow: ViewWorkflow) (tab: WatchTab) : ResolvedView =
    let provenanceOf (axis: ViewAxis) : Provenance =
      let descriptor = ViewSettings.descriptorIn workflow tab axis
      match resolve paths descriptor with
      | Ok provenance -> provenance
      | Error _ -> { Effective = descriptor.Default; Source = LDefault; PerLayer = [] }
    ViewSettings.resolveViewIn workflow tab provenanceOf

  /// The view of a workflow's All tab.
  let viewFor (paths: ConfigPaths) (workflow: ViewWorkflow) : ResolvedView = viewForTab paths workflow WatchTab.All

  /// Carries out one view edit against the layer files. The layer is the scope's (this app's file, or the global one), except that
  /// resetting one axis clears whichever layer holds it, because a reset that left the value in the other layer would look like it
  /// did nothing. What each write does is `ViewEdit.writes`' to say, measured against the view the layer below gives: for this app
  /// that is the global layer over the recommendation, for everywhere it is the recommendation. Stops at the first write that fails.
  let applyViewEditIn (paths: ConfigPaths) (workflow: ViewWorkflow) (tab: WatchTab) (scope: ViewScope) (change: ViewEdit) : Result<unit, ConfigError> =
    let layer =
      match change, scope with
      | ViewEdit.Reset axis, _ ->
        (viewForTab paths workflow tab).Sources |> Map.tryFind axis |> Option.defaultValue LDefault
      | _, ViewScope.ThisApp -> LRepo
      | _, ViewScope.Everywhere -> LGlobal
    let below =
      match layer with
      | LRepo -> (viewForTab { paths with Repo = NoRepoCheckout } workflow tab).View
      | LGlobal
      | LDefault
      | LSession
      | LEnvironment
      | LCommandLine -> View.recommendedIn workflow tab
    let run (write: AxisWrite) : Result<unit, ConfigError> =
      let descriptor = ViewSettings.descriptorIn workflow tab write.Axis
      match write.Write with
      | Write.Store value -> edit paths layer (AxisValue.token value) descriptor |> Result.map ignore
      | Write.ClearOverride -> clear paths layer descriptor |> Result.map ignore
    match layer with
    | LDefault -> Ok()
    | LGlobal
    | LRepo
    | LSession
    | LEnvironment
    | LCommandLine ->
      ViewEdit.writes below change
      |> List.fold (fun outcome write -> outcome |> Result.bind (fun () -> run write)) (Ok())

  /// The same, for a workflow's All tab.
  let applyViewEdit (paths: ConfigPaths) (workflow: ViewWorkflow) (scope: ViewScope) (change: ViewEdit) : Result<unit, ConfigError> =
    applyViewEditIn paths workflow WatchTab.All scope change

  /// The MCP port the persisted layers hold, if they hold one that parses. The
  /// Settings panel writes this value and its row says it applies on the next
  /// daemon restart; until the daemon read it, nothing did, and the row's promise
  /// was not kept. A layer that holds nothing is not a value (the descriptor's
  /// default is the resolution's answer, not a persisted one), and a corrupt value
  /// is not a reason to refuse to start: the caller falls back to the next source
  /// in the order.
  let mcpPortFromLayers (globalDir: string) : int option =
    let paths : ConfigPaths = { GlobalDir = globalDir; Repo = NoRepoCheckout }
    match resolve paths mcpPort with
    | Ok provenance ->
      match provenance.Source, provenance.Effective with
      | LDefault, _ -> None
      | _, VPort p -> Some(Port.value p)
      | _, _ -> None
    | Error _ -> None
