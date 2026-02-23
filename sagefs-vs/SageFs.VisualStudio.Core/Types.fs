namespace SageFs.VisualStudio.Core

open System

/// Result of evaluating F# code via the daemon.
type EvalResult =
  { Output: string
    Diagnostics: string list
    ExitCode: int }

/// Info about an FSI session from the daemon.
type SessionInfo =
  { Id: string
    ProjectNames: string list
    State: string
    WorkingDirectory: string
    EvalCount: int }

/// Status of the SageFs daemon connection.
[<RequireQualifiedAccess>]
type DaemonStatus =
  | Offline
  | Connecting
  | Connected of sessionCount: int

/// Info about a loaded assembly from warmup context.
type LoadedAssemblyInfo =
  { Name: string
    Path: string
    NamespaceCount: int
    ModuleCount: int }

/// Info about an opened namespace/module from warmup context.
type OpenedBindingInfo =
  { Name: string
    IsModule: bool
    Source: string }

/// Warmup context for a session.
type WarmupContextInfo =
  { SourceFilesScanned: int
    AssembliesLoaded: LoadedAssemblyInfo list
    NamespacesOpened: OpenedBindingInfo list
    FailedOpens: string list list
    WarmupDurationMs: int }

/// Hot reload file info.
type HotReloadFile =
  { Path: string
    Watched: bool }

/// Hot reload state for a session.
type HotReloadState =
  { Files: HotReloadFile list
    WatchedCount: int }
