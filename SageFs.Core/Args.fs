module SageFs.Args

open System
open System.IO

// === New types: the real daemon-worker contract ===

/// What the daemon CLI actually cares about — 3 flags, nothing more.
type DaemonFlags = {
  NoResume: bool
  Prune: bool
  NoWatch: bool
  Projects: string list
}

module DaemonFlags =
  let defaults = {
    NoResume = false
    Prune = false
    NoWatch = false
    Projects = []
  }

  let parse (args: string list) =
    let finish acc =
      { acc with Projects = List.rev acc.Projects }

    let rec loop acc remaining =
      match remaining with
      | [] -> finish acc
      | "--no-resume" :: rest -> loop { acc with NoResume = true } rest
      | "--prune" :: rest -> loop { acc with Prune = true } rest
      | "--no-watch" :: rest -> loop { acc with NoWatch = true } rest
      | ("--proj" | "--sln") :: path :: rest ->
        loop { acc with Projects = path :: acc.Projects } rest
      | ("--proj" | "--sln") :: [] ->
        finish acc
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
  /// When true, installs the Harmony JIT hook for DevReload browser auto-refresh
  /// and method detouring. Requires FSI single-assembly mode (--multiemit-).
  /// WARNING: type redefinition is disabled in single-assembly mode.
  /// Set via SAGEFS_HOT_RELOAD=1. Default: false.
  HotReloadEnabled: bool
}

module WorkerConfig =
  let envVar = "SAGEFS_SESSION_PROJECTS"
  let bareEnvVar = "SAGEFS_BARE_SESSION"
  let noWatchEnvVar = "SAGEFS_NO_WATCH"
  let autoOpenNamespacesEnvVar = "SAGEFS_AUTO_OPEN_NAMESPACES"
  let hotReloadEnvVar = "SAGEFS_HOT_RELOAD"

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
    { SessionId = sessionId
      HttpPort = httpPort
      Projects = projects
      WorkingDir = Environment.CurrentDirectory
      IsBare = isBare
      NoWatch = noWatch
      AutoOpenNamespaces = autoOpenNamespaces
      HotReloadEnabled = hotReloadEnabled }

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
let buildWorkerSpawnConfig
  (sessionId: string)
  (projects: string list)
  (isBare: bool)
  (noWatch: bool)
  (autoOpenNamespaces: bool)
  (hotReloadEnabled: bool)
  : string * (string * string) list =
  let args = sprintf "worker --session-id %s --http-port 0" sessionId
  let envVars = [
    WorkerConfig.envVar, (projects |> String.concat ";")
    if isBare then WorkerConfig.bareEnvVar, "1"
    if noWatch then WorkerConfig.noWatchEnvVar, "1"
    if not autoOpenNamespaces then WorkerConfig.autoOpenNamespacesEnvVar, "0"
    if hotReloadEnabled then WorkerConfig.hotReloadEnvVar, "1"
  ]
  args, envVars




