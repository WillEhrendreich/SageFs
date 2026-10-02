/// Why a run stopped, as a closed set. One exit code per kind, so a caller (run-matrix, a person
/// reading the shell) can tell them apart. These are the documented codes in README.md.
module LemRun.Failure

type Failure =
  /// A required argument is missing (1).
  | MissingArgument of string
  /// The request is refused: not a FREE model, no such task or fixture, the run id exists, not a git checkout (2).
  | Refused of string
  /// The shared daemon will not do: unreachable, unhealthy, short of room, sessions unreadable (3).
  | DaemonUnavailable of string
  /// The build or toolchain is missing: no SageFs.dll, cmdc not under mise, a skewed bridge when it is refused,
  /// a driver that will not build, an editor that is not installed (4).
  | ToolchainMissing of string
  /// A window appeared on the real desktop, or a session was left on the dashboard (5).
  | DesktopLeak of string
  /// The shared daemon's pid changed during the run: something in the run stopped it (6).
  | DaemonRestarted of string

exception Stop of Failure

let fail (f: Failure) = raise (Stop f)

let exitCodeOf (f: Failure) : int =
  match f with
  | MissingArgument _ -> 1
  | Refused _ -> 2
  | DaemonUnavailable _ -> 3
  | ToolchainMissing _ -> 4
  | DesktopLeak _ -> 5
  | DaemonRestarted _ -> 6

let describe (f: Failure) : string =
  match f with
  | MissingArgument m | Refused m | DaemonUnavailable m | ToolchainMissing m | DesktopLeak m | DaemonRestarted m -> m

/// The words every message of the harness starts with.
let say (message: string) : unit = eprintfn "lem: %s" message
