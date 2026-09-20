/// Where a session's FSI lives. Its own early file because both the daemon side (which decides how to launch a
/// worker) and the worker side (which decides how to create the FSI session) need it.
module SageFs.SessionKinds

/// In this process (today's default), or in an isolated host process that shares no assembly with SageFs.
/// Isolated is opt-in until the host agent (hot reload, live testing) exists.
type FsiSessionKind =
  | InProcess
  | Isolated

/// The environment variable that opts a worker's sessions into the isolated FSI host.
[<Literal>]
let EnvironmentVariable = "SAGEFS_ISOLATED_FSI"

let fromEnvironmentWith (getEnv: string -> string | null) : FsiSessionKind =
  match getEnv EnvironmentVariable with
  | "1"
  | "true" -> Isolated
  | _ -> InProcess
