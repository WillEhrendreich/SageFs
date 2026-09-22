/// How the worker reaches the agent of the session it drives: what the agent starts from, and a handle on whichever
/// session is active now.
module SageFs.SessionAgent

open SageFs.Features
open SageFs.ProjectLoading
open SageFs.Middleware.ValueReads

/// Rule 2's reflection read mode, as a setting: the values are exactly the
/// modes' names (`ReflectionReadMode.name`), so config, the MCP tool and the
/// dashboard spell it one way. It's read when a hot reload session starts; a
/// running session switches from its Hot Reload panel or with the
/// set_reflection_read_mode MCP tool, without a restart.
let reflectionReadModeSetting : SettingDescriptor =
  let names = ReflectionReadMode.all |> List.map ReflectionReadMode.name
  let enumOf (raw: string) = EnumValue.create names raw |> Result.map VEnum
  { Key = "hotreload.reflectionReadMode"
    Name = "Reflection reads"
    Description =
      ReflectionReadMode.all
      |> List.map (fun mode -> sprintf "%s: %s." (ReflectionReadMode.name mode) (ReflectionReadMode.consequence mode))
      |> String.concat " "
      |> sprintf "How hot reload watches values read through reflection. New sessions start in it; a running one switches from its Hot Reload panel. %s"
    Category = SessionSettings
    Scope = RepoOverridable
    Applicability = RestartRequired
    Default =
      match enumOf (ReflectionReadMode.name ReflectionReadMode.standard) with
      | Result.Ok v -> v
      | Result.Error _ -> failwith "the default reflection read mode must be one of the modes"
    Parse = enumOf
    Render =
      fun v ->
        match v with
        | VEnum e -> EnumValue.value e
        | _ -> ""
    Apply = ignore }

/// The reflection settings a new session starts with, from the config layers
/// at `paths`. A value that doesn't parse falls back to the default and says
/// so in the log: a broken config line must not stop a session starting.
let reflectionSettingsAt (paths: ConfigPaths) : ReflectionReadSettings =
  let mode =
    match SettingsCatalog.resolve paths reflectionReadModeSetting with
    | Result.Ok provenance ->
      match ReflectionReadMode.parse (reflectionReadModeSetting.Render provenance.Effective) with
      | Result.Ok mode -> mode
      | Result.Error _ -> ReflectionReadMode.standard
    | Result.Error why ->
      Utils.Log.warn "[ValueReads] the reflection read mode setting can't be read (%s); using %s" (ConfigError.describe why) (ReflectionReadMode.name ReflectionReadMode.standard)
      ReflectionReadMode.standard
  { Mode = mode; HotLoop = HotLoopThreshold.standard }

/// The reflection settings for a session started in `workingDir`.
let reflectionSettingsFor (workingDir: string) : ReflectionReadSettings =
  reflectionSettingsAt { GlobalDir = DaemonState.SageFsDir; Repo = RepoRootAt workingDir }

/// What the session's agent starts from, derived from the solution: every project's output is loaded, and dependencies
/// resolve from the projects' outputs, their package directories and the reference assemblies FSI was given.
let agentInitOf (sln: Solution) (hotReload: bool) (reflection: ReflectionReadSettings) : HostAgent.AgentInit =
  let referenced (options: string list) =
    options
    |> List.filter (fun s -> s.StartsWith("-r:", System.StringComparison.Ordinal) && s.EndsWith(".dll", System.StringComparison.Ordinal))
    |> List.map (fun s -> s.Substring 3)
  { Projects = sln.Projects |> List.map (fun p -> p.TargetPath)
    ResolveFrom =
      sln.Projects
      |> List.collect (fun p -> (p.PackageReferences |> List.map (fun pr -> pr.FullPath)) @ referenced p.OtherOptions)
    // Only hot reload redefines values, so only hot reload pays for watching them.
    ValueReads =
      match hotReload with
      | true -> SageFs.Middleware.ValueReadTracking.ValueReadWatch.WatchValueReads reflection
      | false -> SageFs.Middleware.ValueReadTracking.ValueReadWatch.IgnoreValueReads }

/// Reaches the agent of whichever session is active now, without a round-trip through the eval actor. A session that is
/// not active has no agent, and says so.
type SessionAgent =
  { DiscoverLoaded: unit -> HostAgent.AgentReply<HostAgent.Discovery>
    TakeCoverage: unit -> HostAgent.AgentReply<HostAgent.CoverageReading>
    RunTest: LiveTesting.TestCase -> Async<HostAgent.AgentReply<LiveTesting.TestResult>>
    ValueReads: string list -> HostAgent.AgentReply<SageFs.Middleware.ValueReads.ValueEvidence list>
    ReflectionReads: unit -> HostAgent.AgentReply<ReflectionReadsReport>
    SetReflectionMode: ReflectionReadMode -> HostAgent.AgentReply<ReflectionReadsReport> }

/// The agent accessors for whichever session `current` returns right now (null when none is active): the caller reads the
/// live session on every call, so a hard reset that swaps the session is followed without re-wiring.
let ofCurrentSession (current: unit -> FsiSession.IFsiSession) : SessionAgent =
  let inactive = HostAgent.AgentUnavailable "the session is not active"
  let withSession (ask: FsiSession.IFsiSession -> 'a) (whenInactive: 'a) : 'a =
    match current () with
    | null -> whenInactive
    | session -> ask session
  { DiscoverLoaded = fun () -> withSession (fun s -> s.DiscoverLoaded()) inactive
    TakeCoverage = fun () -> withSession (fun s -> s.TakeCoverage()) inactive
    RunTest = fun test -> withSession (fun s -> s.RunTest test) (async { return inactive })
    ValueReads = fun values -> withSession (fun s -> s.ValueReads values) (HostAgent.AgentUnavailable "the session is not active")
    ReflectionReads = fun () -> withSession (fun s -> s.ReflectionReads()) (HostAgent.AgentUnavailable "the session is not active")
    SetReflectionMode = fun mode -> withSession (fun s -> s.SetReflectionMode mode) (HostAgent.AgentUnavailable "the session is not active") }
