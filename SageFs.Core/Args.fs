module SageFs.Args

open System
open System.IO

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
  /// Interactive = full REPL (default). WebLive = save-driven hot reload.
  /// Derived from SAGEFS_HOT_RELOAD env var for backward compat.
  Workflow: WorkflowTypes.SessionWorkflow
  /// PID of the daemon that spawned this worker (None when run standalone,
  /// e.g. tests or manual worker invocation).
  DaemonPid: int option
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
    { SessionId = sessionId
      HttpPort = httpPort
      Projects = projects
      WorkingDir = Environment.CurrentDirectory
      IsBare = isBare
      NoWatch = noWatch
      AutoOpenNamespaces = autoOpenNamespaces
      Workflow = WorkflowTypes.SessionWorkflow.fromHotReloadBool hotReloadEnabled
      DaemonPid = daemonPid }

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
    if isBare then WorkerConfig.bareEnvVar, "1"
    if noWatch then WorkerConfig.noWatchEnvVar, "1"
    if not autoOpenNamespaces then WorkerConfig.autoOpenNamespacesEnvVar, "0"
    if WorkflowTypes.SessionWorkflow.isHotReloadActive workflow then WorkerConfig.hotReloadEnvVar, "1"
  ]
  args, envVars

/// Resolve the FSI host exe path relative to the daemon's own location.
/// Dev layout: <daemon>/host/SageFs.Host.exe (copied post-build).
/// Tool layout: <tool store>/tools/<tfm>/any/host/SageFs.Host.exe (packaged).
/// Pure: takes the daemon's base directory so tests can pin the resolution.
let hostExePath (daemonBaseDir: string) : string =
  Path.Combine(daemonBaseDir, "host", "SageFs.Host.exe")




