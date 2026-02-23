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
    WorkingDirectory: string }

/// Status of the SageFs daemon connection.
[<RequireQualifiedAccess>]
type DaemonStatus =
  | Offline
  | Connecting
  | Connected of sessionCount: int
