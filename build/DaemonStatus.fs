/// What `sagefs status` says about the daemon, read as one of four answers.
///
/// `sagefs status` exits 1 for a daemon that is running but degraded (a component reports a failure), for a wedged daemon, and for no
/// daemon at all. A script that reads the exit code therefore cannot tell them apart, and `scripts/install-local.fsx --status` said
/// "not running" about a daemon that was up and answering on both ports. The answer is in the first line of the output, which the
/// command writes for exactly this purpose, so the script reads that.
///
/// Output that matches none of the three headlines is `Unreadable` and says what it was. It is never reported as "not running", because
/// a status line that guesses is the failure this module exists to prevent. `DaemonStatusTests` runs the real command in every state
/// and feeds its output to `classify`, so a change to the command's wording breaks a test and not a status line.
module SageFs.Build.DaemonStatus

open System.Text.RegularExpressions

type DaemonReport =
  /// The daemon answered. `failing` is how many components report a failure, 0 when it is healthy.
  | Running of version: string * failing: int
  /// Something holds the port and does not answer.
  | Wedged
  /// Nothing is there.
  | NotRunning
  /// The output matched no known headline. Carries the first line as it was.
  | Unreadable of firstLine: string

/// The first line that has anything on it.
let headline (output: string) : string =
  output.Split('\n')
  |> Array.map (fun line -> line.Trim())
  |> Array.tryFind (fun line -> line <> "")
  |> Option.defaultValue ""

/// The words the command's headline uses, one per state.
[<Literal>]
let RunningHeadline = "SageFs daemon running"

[<Literal>]
let WedgedHeadline = "SageFs daemon is wedged"

[<Literal>]
let NotRunningHeadline = "No daemon running"

let versionIn (output: string) : string =
  let m = Regex.Match(output, @"(?m)^\s*Version:\s+(\S+)")
  match m.Success with
  | true -> m.Groups.[1].Value
  | false -> "unknown version"

/// How many components the output says are failing. 0 when it names none.
let failingIn (output: string) : int =
  let m = Regex.Match(output, @"DEGRADED\D*(\d+)\s+component")
  match m.Success with
  | true -> int m.Groups.[1].Value
  | false -> 0

let classify (output: string) : DaemonReport =
  match headline output with
  | RunningHeadline -> Running(versionIn output, failingIn output)
  | WedgedHeadline -> Wedged
  | NotRunningHeadline -> NotRunning
  | other -> Unreadable other

/// The words a status line uses for the daemon.
let describe (report: DaemonReport) : string =
  match report with
  | Running(version, 0) -> version
  | Running(version, 1) -> sprintf "%s (degraded: 1 component failing)" version
  | Running(version, failing) -> sprintf "%s (degraded: %d components failing)" version failing
  | Wedged -> "wedged: it holds the port and does not answer"
  | NotRunning -> "not running"
  | Unreadable first -> sprintf "unknown (`sagefs status` said: %s)" first
