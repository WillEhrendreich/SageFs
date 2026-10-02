/// The `sagefs` command on PATH inside the VS Code sandbox. The extension (and a terminal
/// opened in that window) call `sagefs`; a real user has the CLI installed, so the window
/// gets one. But the CLI can stop the shared daemon, sweep it, or start a second one, and
/// the daemon is Will's, so this shim runs the read-only verbs and refuses the rest.
/// Pure parsing, so the split is proven in the REPL.
module LemDrive.Shim

open System
open System.Diagnostics

/// What a `sagefs ...` command line asks for. Closed: anything not recognised as
/// read-only is `StartsDaemon`, which is refused.
type SagefsVerb =
  | ShowsVersion
  | ShowsHelp
  | ChecksEnvironment
  | ShowsStatus
  | StopsDaemon
  | SweepsDaemons
  | PrunesSessions
  | SpeaksMcp
  | RunsJupyterKernel
  | ReplaysLedger
  | StartsDaemon

let verbOf (args: string list) : SagefsVerb =
  match args with
  | [ "--version" ]
  | [ "-v" ] -> ShowsVersion
  | [ "--help" ]
  | [ "-h" ] -> ShowsHelp
  | "check" :: _ -> ChecksEnvironment
  | "status" :: _ -> ShowsStatus
  | "stop" :: _ -> StopsDaemon
  | "sweep" :: _ -> SweepsDaemons
  | "mcp" :: _ -> SpeaksMcp
  | "play" :: _ -> ReplaysLedger
  | _ when args |> List.contains "--prune" -> PrunesSessions
  | _ when args |> List.contains "--jupyter" -> RunsJupyterKernel
  | _ -> StartsDaemon

/// The word the refusal uses for what was asked.
let describe (v: SagefsVerb) : string =
  match v with
  | ShowsVersion -> "show the version"
  | ShowsHelp -> "show help"
  | ChecksEnvironment -> "check the environment"
  | ShowsStatus -> "show the daemon's status"
  | StopsDaemon -> "stop the daemon"
  | SweepsDaemons -> "sweep daemons"
  | PrunesSessions -> "prune sessions"
  | SpeaksMcp -> "speak MCP (it can spawn a daemon)"
  | RunsJupyterKernel -> "run a Jupyter kernel"
  | ReplaysLedger -> "replay a ledger"
  | StartsDaemon -> "start a daemon"

/// Read-only verbs run; everything that changes a daemon is refused.
let allowed (v: SagefsVerb) : Result<unit, string> =
  match v with
  | ShowsVersion
  | ShowsHelp
  | ChecksEnvironment
  | ShowsStatus
  | ReplaysLedger -> Ok()
  | StopsDaemon
  | SweepsDaemons
  | PrunesSessions
  | SpeaksMcp
  | RunsJupyterKernel
  | StartsDaemon ->
    Result.Error(
      sprintf
        "sagefs: this machine's SageFs daemon is shared with other people, so this shell will not %s. The daemon is already running; use the editor or the dashboard."
        (describe v))

/// The environment variable that names the real SageFs command line the shim runs.
[<Literal>]
let RealCommandVar = "LEM_SAGEFS_REAL"

/// The exit code of a refused call, the same as the driver's refusal.
[<Literal>]
let RefusedExit = 3

/// Runs the shim: `sagefs <args>` against the real build, or a refusal.
let run (args: string list) : int =
  match allowed (verbOf args) with
  | Result.Error why ->
    eprintfn "%s" why
    RefusedExit
  | Ok() ->
    match Environment.GetEnvironmentVariable RealCommandVar with
    | null
    | "" ->
      eprintfn "sagefs shim: %s is not set" RealCommandVar
      1
    | real ->
      let parts = real.Split([| ' ' |], StringSplitOptions.RemoveEmptyEntries) |> List.ofArray
      match parts with
      | [] -> 1
      | exe :: lead ->
        let psi = ProcessStartInfo(exe, UseShellExecute = false)
        for a in lead @ args do
          psi.ArgumentList.Add a
        use p = Process.Start psi
        p.WaitForExit()
        p.ExitCode
