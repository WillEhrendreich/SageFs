/// Where a session's FSI lives. Its own early file because both the daemon side (which decides how to launch a
/// worker) and the worker side (which decides how to create the FSI session) need it.
module SageFs.SessionKinds

/// In an isolated host process that shares no assembly with SageFs (how sessions normally run: no package or runtime of
/// SageFs's can ever conflict with the user's), or in this process (the reference implementation the tests use, and an
/// explicit escape hatch).
type FsiSessionKind =
  | Isolated
  | InProcess

/// The environment variable that opts a worker's sessions OUT of the isolated FSI host, with `0` or `false`.
[<Literal>]
let EnvironmentVariable = "SAGEFS_ISOLATED_FSI"

/// Isolated unless explicitly switched off: a typo or stray value gets the safe mode, never the unsafe one.
let fromEnvironmentWith (getEnv: string -> string | null) : FsiSessionKind =
  match getEnv EnvironmentVariable with
  | null -> Isolated
  | value when System.String.Equals(value, "0", System.StringComparison.Ordinal) -> InProcess
  | value when System.String.Equals(value, "false", System.StringComparison.OrdinalIgnoreCase) -> InProcess
  | _ -> Isolated
