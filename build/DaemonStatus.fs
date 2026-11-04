/// What `sagefs status` says about the daemon, read as one of four answers, and what to make of the answer after a stop.
///
/// `sagefs status` exits 1 for a daemon that is running but degraded (a component reports a failure), for a wedged daemon, and for no
/// daemon at all. A script that reads the exit code therefore cannot tell them apart, and `scripts/install-local.fsx --status` said
/// "not running" about a daemon that was up and answering on both ports. The answer is in the first line of the output, which the
/// command writes for exactly this purpose, so the script reads that.
///
/// Output that matches none of the three headlines is `Unreadable` and says what it was. It is never reported as "not running", because
/// a status line that guesses is the failure this module exists to prevent. `DaemonStatusTests` runs the real command in every state
/// and feeds its output to `classify`, so a change to the command's wording breaks a test and not a status line.
///
/// A daemon answering on the port is not the daemon that was stopped. An agent's `sagefs mcp` bridge starts one the moment there is
/// none, inside that agent's own sandbox, and `install-local --force` read that replacement as "the old daemon is still answering".
/// `afterStop` compares process ids, so the script can say which it was and act on it.
module SageFs.Build.DaemonStatus

open System.Text.RegularExpressions

/// A daemon that answered, as `sagefs status` describes it.
type RunningDaemon =
  { Version: string
    /// How many components report a failure, 0 when it is healthy.
    Failing: int
    Pid: int
    /// The directory it was started in. A daemon the install script starts is in the install directory.
    Directory: string }

type DaemonReport =
  | Running of RunningDaemon
  /// Something holds the port and does not answer.
  | Wedged
  /// Nothing is there.
  | NotRunning
  /// The output matched no known headline. Carries the first line as it was.
  | Unreadable of firstLine: string

/// What the daemon that answers after a stop is, compared with the one that was stopped.
type AfterStop =
  /// Nothing answers. The port is free.
  | OldGone
  /// The daemon that was stopped is still the one answering, or what answers cannot be told from it.
  | OldStillRunning
  /// A different daemon answers: another process started one as the old one stopped.
  | ReplacedBy of RunningDaemon

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

let field (name: string) (output: string) : string option =
  let m = Regex.Match(output, sprintf @"(?m)^\s*%s:\s+(.+?)\s*$" (Regex.Escape name))
  match m.Success with
  | true -> Some m.Groups.[1].Value
  | false -> None

let versionIn (output: string) : string = field "Version" output |> Option.defaultValue "unknown version"

/// How many components the output says are failing. 0 when it names none.
let failingIn (output: string) : int =
  let m = Regex.Match(output, @"DEGRADED\D*(\d+)\s+component")
  match m.Success with
  | true -> int m.Groups.[1].Value
  | false -> 0

let classify (output: string) : DaemonReport =
  match headline output with
  | RunningHeadline ->
    Running
      { Version = versionIn output
        Failing = failingIn output
        Pid = field "PID" output |> Option.bind (fun text -> match System.Int32.TryParse text with | true, pid -> Some pid | false, _ -> None) |> Option.defaultValue 0
        Directory = field "Directory" output |> Option.defaultValue "" }
  | WedgedHeadline -> Wedged
  | NotRunningHeadline -> NotRunning
  | other -> Unreadable other

/// The words a status line uses for the daemon.
let describe (report: DaemonReport) : string =
  match report with
  | Running d when d.Failing = 0 -> d.Version
  | Running d when d.Failing = 1 -> sprintf "%s (degraded: 1 component failing)" d.Version
  | Running d -> sprintf "%s (degraded: %d components failing)" d.Version d.Failing
  | Wedged -> "wedged: it holds the port and does not answer"
  | NotRunning -> "not running"
  | Unreadable first -> sprintf "unknown (`sagefs status` said: %s)" first

/// What answers now, given the pid of the daemon that was asked to stop. A wedged daemon, or output that cannot be read, is not
/// treated as gone: the port may still be held.
let afterStop (stoppedPid: int) (report: DaemonReport) : AfterStop =
  match report with
  | Running d when d.Pid = stoppedPid -> OldStillRunning
  | Running d -> ReplacedBy d
  | NotRunning -> OldGone
  | Wedged
  | Unreadable _ -> OldStillRunning

/// Whether a daemon was started where the install script starts its own. One that was started somewhere else, such as an agent's
/// worktree, runs in that agent's sandbox and ends when that agent does.
let startedIn (expectedDirectory: string) (daemon: RunningDaemon) : bool =
  let normal (path: string) = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath path)
  normal daemon.Directory = normal expectedDirectory

let describeReplacement (daemon: RunningDaemon) : string =
  sprintf "another process started a daemon (pid %d, started in %s) as the old one stopped" daemon.Pid daemon.Directory
