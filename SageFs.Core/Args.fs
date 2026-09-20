module SageFs.Args

open System
open System.IO
open System.IO.Compression

// === New types: the real daemon-worker contract ===

/// What the daemon CLI actually cares about — 3 flags, nothing more.
type DaemonFlags = {
  NoResume: bool
  Prune: bool
  NoWatch: bool
}

module DaemonFlags =
  let defaults = {
    NoResume = false
    Prune = false
    NoWatch = false
  }

  let parse (args: string list) =
    let rec loop acc remaining =
      match remaining with
      | [] -> acc
      | "--no-resume" :: rest -> loop { acc with NoResume = true } rest
      | "--prune" :: rest -> loop { acc with Prune = true } rest
      | "--no-watch" :: rest -> loop { acc with NoWatch = true } rest
      | ("--proj" | "--sln") :: [] ->
        acc
      | ("--proj" | "--sln") :: _ :: rest ->
        loop acc rest
      | _ :: rest -> loop acc rest
    loop defaults args

/// A daemon-startup flag that `DaemonFlags.parse` still recognizes (for
/// backward-compatible argument shapes) but that has NO effect on the
/// running daemon:
///   - `--proj`/`--sln` — the daemon no longer auto-loads a project at
///     startup (it is always bare); these flags used to seed
///     `DaemonFlags.Projects` and now just fall on the floor.
///   - `--no-watch` — parsed into `DaemonFlags.NoWatch`, but nothing reads
///     that field: `SessionManager.startWorkerProcess` hardcodes `noWatch =
///     false` for every worker it spawns, and no session-creation surface
///     (MCP, dashboard, editors) exposes a way to request it either.
/// Accepting one of these silently would let a user believe their flag did
/// something. The CLI must refuse with guidance instead of starting a
/// daemon that quietly ignored what was asked of it.
[<RequireQualifiedAccess>]
type UnimplementedFlag =
  | Proj
  | Sln
  | NoWatch

module UnimplementedFlag =
  /// The exact flag text as a user would type it.
  let text =
    function
    | UnimplementedFlag.Proj -> "--proj"
    | UnimplementedFlag.Sln -> "--sln"
    | UnimplementedFlag.NoWatch -> "--no-watch"

  /// Detect every unimplemented flag present in the raw argument list, in a
  /// stable order. Presence-only (position- and value-independent) — this
  /// mirrors how `DaemonFlags.parse` itself recognizes these flags.
  let detectAll (args: string list) : UnimplementedFlag list =
    [ if List.contains "--proj" args then UnimplementedFlag.Proj
      if List.contains "--sln" args then UnimplementedFlag.Sln
      if List.contains "--no-watch" args then UnimplementedFlag.NoWatch ]

/// What a worker process needs — comes entirely from env vars.
/// Uses dependency rejection: pure core reads from a function,
/// impure shell passes Environment.GetEnvironmentVariable.
type WorkerConfig = {
  SessionId: string
  HttpPort: int
  Projects: string list
  WorkingDir: string
  IsBare: bool
  NoWatch: bool
  AutoOpenNamespaces: bool
  /// The session workflow — determines FSI flags, REPL capability, and hot reload.
  /// Interactive = full REPL (default). HotReload = save-driven hot reload.
  /// Derived from SAGEFS_HOT_RELOAD env var for backward compat.
  Workflow: WorkflowTypes.SessionWorkflow
  /// PID of the daemon that spawned this worker (None when run standalone,
  /// e.g. tests or manual worker invocation).
  DaemonPid: int option
  /// UTC ticks of the daemon's own `Process.StartTime` at the moment it
  /// spawned this worker. Paired with `DaemonPid` as an `OwnerMonitor.Owner`
  /// fence so a recycled daemon pid (heavy spawn churn) can never be
  /// mistaken for the same daemon and keep a worker alive forever.
  DaemonStartTicks: int64 option
}
  with
    /// Backward-compatible accessor.
    member this.HotReloadEnabled = WorkflowTypes.SessionWorkflow.isHotReloadActive this.Workflow

module WorkerConfig =
  let envVar = "SAGEFS_SESSION_PROJECTS"
  let bareEnvVar = "SAGEFS_BARE_SESSION"
  let noWatchEnvVar = "SAGEFS_NO_WATCH"
  let autoOpenNamespacesEnvVar = "SAGEFS_AUTO_OPEN_NAMESPACES"
  let hotReloadEnvVar = "SAGEFS_HOT_RELOAD"
  /// PID of the daemon process that spawned this worker. Workers monitor this
  /// so they can exit when the daemon is killed hard (Task Manager, taskkill /F,
  /// crash) instead of becoming orphans (issue #126).
  let daemonPidEnvVar = "SAGEFS_DAEMON_PID"
  /// UTC ticks of the daemon's own start time, recorded alongside
  /// `daemonPidEnvVar` so the worker's parent-death watchdog can fence on
  /// (pid, startTime) instead of pid alone — see `SageFs.OwnerMonitor`.
  let daemonStartTicksEnvVar = "SAGEFS_DAEMON_START_TICKS"

  /// Pure core — reads config via an injected env reader.
  let fromEnvironmentWith
    (getEnv: string -> string)
    (sessionId: string)
    (httpPort: int)
    =
    let projects =
      match getEnv envVar with
      | null | "" -> []
      | s -> s.Split(';', StringSplitOptions.RemoveEmptyEntries) |> Array.toList
    let isBare =
      match getEnv bareEnvVar with
      | "1" | "true" -> true
      | _ -> false
    let noWatch =
      match getEnv noWatchEnvVar with
      | "1" | "true" -> true
      | _ -> false
    let autoOpenNamespaces =
      match getEnv autoOpenNamespacesEnvVar with
      | "0" | "false" -> false
      | _ -> true
    let hotReloadEnabled =
      match getEnv hotReloadEnvVar with
      | "1" | "true" -> true
      | _ -> false
    let daemonPid =
      match getEnv daemonPidEnvVar with
      | null | "" -> None
      | s ->
        match Int32.TryParse(s) with
        | true, pid when pid > 0 -> Some pid
        | _ -> None
    let daemonStartTicks =
      match getEnv daemonStartTicksEnvVar with
      | null | "" -> None
      | s ->
        match Int64.TryParse(s) with
        | true, ticks -> Some ticks
        | _ -> None
    { SessionId = sessionId
      HttpPort = httpPort
      Projects = projects
      WorkingDir = Environment.CurrentDirectory
      IsBare = isBare
      NoWatch = noWatch
      AutoOpenNamespaces = autoOpenNamespaces
      Workflow = WorkflowTypes.SessionWorkflow.fromHotReloadBool hotReloadEnabled
      DaemonPid = daemonPid
      DaemonStartTicks = daemonStartTicks }

  /// Impure shell — reads from real environment.
  let fromEnvironment sessionId httpPort =
    fromEnvironmentWith Environment.GetEnvironmentVariable sessionId httpPort

/// What ProjectLoading needs — replaces the old Arguments list.
type ProjectLoadConfig = {
  Projects: string list
  Solutions: string list
  WorkingDir: string
}

module ProjectLoadConfig =
  let empty = { Projects = []; Solutions = []; WorkingDir = "." }

  let fromWorkerConfig (wc: WorkerConfig) =
    let solutions, projects =
      wc.Projects
      |> List.partition (fun p ->
        let ext = Path.GetExtension(p).ToLowerInvariant()
        ext = ".sln" || ext = ".slnx")
    { Projects = projects
      Solutions = solutions
      WorkingDir = wc.WorkingDir }

/// Pure function: builds worker spawn arguments + env vars.
/// Extracted from SessionManager for testability.
/// `daemonPid` is the PID of the daemon spawning the worker — workers monitor it
/// so they self-exit when the daemon dies (issue #126: orphaned worker sessions).
///
/// The FSI host takes POSITIONAL args: `<sessionId> <httpPort>` (see
/// SageFs.Host/Program.fs). The port is OS-assigned (`0` = ephemeral); the
/// host prints `WORKER_PORT=<url>` on stdout and the supervisor validates it.
let buildWorkerSpawnConfig
  (sessionId: string)
  (projects: string list)
  (isBare: bool)
  (noWatch: bool)
  (autoOpenNamespaces: bool)
  (workflow: WorkflowTypes.SessionWorkflow)
  : string * (string * string) list =
  let args = sprintf "%s 0" sessionId
  let envVars = [
    WorkerConfig.envVar, (projects |> String.concat ";")
    WorkerConfig.daemonPidEnvVar, string Environment.ProcessId
    WorkerConfig.daemonStartTicksEnvVar, string (System.Diagnostics.Process.GetCurrentProcess().StartTime.ToUniversalTime().Ticks)
    if isBare then WorkerConfig.bareEnvVar, "1"
    if noWatch then WorkerConfig.noWatchEnvVar, "1"
    if not autoOpenNamespaces then WorkerConfig.autoOpenNamespacesEnvVar, "0"
    if WorkflowTypes.SessionWorkflow.isHotReloadActive workflow then WorkerConfig.hotReloadEnvVar, "1"
  ]
  args, envVars

/// How the daemon starts the FSI host. The host runs from its OWN host/ subdir
/// beside the daemon (isolated closure — its fail-closed manifest check refuses
/// a shared dir). Dev: <daemon>/host/. Tool: <store>/.../tools/<tfm>/any/host/.
[<RequireQualifiedAccess>]
type HostLaunch =
  | NativeExecutable of path: string
  | ViaDotnetMuxer of dotnetPath: string * hostDll: string

/// Windows keeps the native SageFs.Host.exe. Unix runs the dll through the
/// daemon's own dotnet: the extensionless apphost can only find a per-user
/// runtime (~/.dotnet) when DOTNET_ROOT is set, which a tool shim does not do.
let resolveHostLaunch
  (daemonBaseDir: string)
  (isWindows: bool)
  (dotnetMuxer: string)
  (fileExists: string -> bool)
  : Result<HostLaunch, string> =
  let hostDir = Path.Combine(daemonBaseDir, "host")
  let exe = Path.Combine(hostDir, "SageFs.Host.exe")
  let dll = Path.Combine(hostDir, "SageFs.Host.dll")
  match isWindows && fileExists exe, fileExists dll with
  | true, _ -> Ok (HostLaunch.NativeExecutable exe)
  | false, true -> Ok (HostLaunch.ViaDotnetMuxer (dotnetMuxer, dll))
  | false, false ->
    Error (sprintf "The SageFs FSI host is missing from %s. → Reinstall the tool (dotnet tool update -g sagefs) or rebuild SageFs (dotnet build) so host/SageFs.Host.dll exists." hostDir)

/// The dotnet executable at the root of a runtime dir
/// (<root>/shared/Microsoft.NETCore.App/<version>/).
let muxerFromRuntimeDir (runtimeDir: string) (isWindows: bool) : string =
  let root = Path.GetFullPath(Path.Combine(runtimeDir, "..", "..", ".."))
  Path.Combine(root, (match isWindows with | true -> "dotnet.exe" | false -> "dotnet"))




