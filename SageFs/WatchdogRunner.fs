module SageFs.Server.WatchdogRunner

open System
open System.Diagnostics
open System.Threading
open SageFs
open SageFs.Utils
open SageFs.Watchdog

/// The MCP port the supervised daemon will bind — `DaemonPort`'s own answer for these args, which is
/// the daemon's answer too: `--mcp-port <n>` in its args, else the environment, else the port the
/// Settings panel persisted, else the default. The watchdog does not bind it, it only uses it to tell
/// daemons apart (the jitter seed below), so a second reading of the rule here would be a second rule:
/// two daemons the panel put on different ports would draw the same seed and restart in lockstep.
let daemonPort (daemonArgs: string list) : int = DaemonPort.resolve daemonArgs

/// The jitter seed for one crash of one daemon: the working directory and port identify the daemon,
/// the crash time makes each crash of it different. Two daemons in different directories (or on
/// different ports) get different seeds for the same crash, which is what breaks the herd.
let jitterSeed (workingDirectory: string) (port: int) (crashedAt: DateTime) : RestartPolicy.JitterSeed =
  SessionLifecycle.jitterSeedFor (sprintf "%s:%d" workingDirectory port) crashedAt

/// What the watchdog launches on a start or restart: the executable, the daemon's argv as separate
/// elements, and the directory it runs in.
type DaemonLaunch = {
  ExePath: string
  Args: string list
  WorkingDirectory: string
}

/// Something that starts a new daemon after `sagefs stop` ends this one, so a stop that reported success would be
/// true for about five seconds.
[<RequireQualifiedAccess>]
type Restarter =
  /// A systemd user unit runs `sagefs --supervised`; the watchdog it runs starts a new daemon, and the unit restarts
  /// the watchdog.
  | SystemdUnit of unitName: string
  /// A launchd agent runs `sagefs --supervised` and keeps it alive.
  | LaunchdAgent of label: string
  /// A `sagefs --supervised` started by hand: its watchdog starts a new daemon whenever the daemon is gone.
  | Watchdog of pid: int
  /// An agent's `sagefs mcp` stdio bridge starts a daemon on its next request when none answers.
  | Bridge of pid: int

module Restarter =
  /// The launchd label the agent in `contrib/launchd` is installed under.
  let launchdLabel = "io.github.willehrendreich.sagefs"

  /// What it will do, and the one command that keeps the daemon stopped.
  let describe (restarter: Restarter) : string =
    match restarter with
    | Restarter.SystemdUnit unitName ->
      let name = unitName.Replace(".service", "")
      sprintf
        "The systemd user unit %s runs it under a watchdog, which starts a new daemon within seconds. To stop it for good: systemctl --user stop %s (and systemctl --user disable %s to keep it from starting at login)."
        unitName name name
    | Restarter.LaunchdAgent label ->
      sprintf
        "The launchd agent %s runs it under a watchdog, which starts a new daemon within seconds. To stop it for good: launchctl bootout gui/$(id -u)/%s"
        label label
    | Restarter.Watchdog pid ->
      sprintf
        "A `sagefs --supervised` watchdog (PID %d) starts a new daemon within seconds. Stop the watchdog to keep it stopped: Ctrl+C in its terminal, or kill %d."
        pid pid
    | Restarter.Bridge pid ->
      sprintf
        "An agent's `sagefs mcp` bridge (PID %d) starts a new daemon on its next request. Close that agent, or remove SageFs from its MCP servers, to keep the daemon stopped."
        pid

/// Who will restart a daemon, read from the process table. The decisions are pure over `ProcessFacts`; `restartersOf`
/// is the edge that reads `/proc` (Linux) or looks for the installed launchd agent (macOS).
module Supervision =
  /// One process as `/proc` shows it: its parent, its argv, and its cgroup path (which names the systemd unit).
  [<RequireQualifiedAccess>]
  type ParentProcess =
    | Pid of int
    /// `/proc/<pid>/stat` could not be read or parsed.
    | Unknown

  type ProcessFacts = { Pid: int; Parent: ParentProcess; Argv: string list; Cgroup: string }

  /// The systemd unit a process runs in, read off its cgroup path. The user manager's own `user@<uid>.service` is
  /// the parent of every user unit and is no unit of ours.
  let unitOfCgroup (cgroup: string) : string option =
    cgroup.Split([| '\n'; '\r' |], StringSplitOptions.RemoveEmptyEntries)
    |> Array.tryPick (fun line ->
      line.Split('/')
      |> Array.tryFindBack (fun segment -> segment.EndsWith ".service" && not (segment.StartsWith "user@")))

  /// The parent pid in a `/proc/<pid>/stat` line: `pid (comm) state ppid ...`. The command name can hold spaces and
  /// parentheses, so the fields are read after the last `)`.
  let parentPidOfStat (stat: string) : int option =
    match stat.LastIndexOf ')' with
    | -1 -> None
    | close ->
      match stat.Substring(close + 1).Split(' ', StringSplitOptions.RemoveEmptyEntries) |> List.ofArray with
      | _state :: ppid :: _ ->
        match Int32.TryParse ppid with
        | true, value -> Some value
        | false, _ -> None
      | _ -> None

  let private isSageFsProgram (word: string) : bool =
    match (IO.Path.GetFileName word).ToLowerInvariant() with
    | "sagefs"
    | "sagefs.exe"
    | "sagefs.dll" -> true
    | _ -> false

  /// `sagefs mcp`, from the tool's shim or through the `dotnet` muxer.
  let isBridgeArgv (argv: string list) : bool =
    match argv with
    | program :: "mcp" :: _ when isSageFsProgram program -> true
    | muxer :: dll :: "mcp" :: _ when IO.Path.GetFileNameWithoutExtension muxer = "dotnet" && isSageFsProgram dll -> true
    | _ -> false

  let isWatchdogArgv (argv: string list) : bool =
    (argv |> List.exists isSageFsProgram) && List.contains "--supervised" argv

  /// The port of the daemon being stopped, and the configured port a bridge with no `--mcp-port` of its own asks for.
  type Ports = { Stopped: int; Home: int }

  /// Who restarts `daemon`: a `--supervised` watchdog parent (named by the systemd unit it runs in, when it runs in
  /// one), and every live bridge that asks for the stopped daemon's port (a bridge starts a daemon only on its own
  /// port). A unit with no watchdog is no restarter: a daemon that exits cleanly is not restarted by
  /// `Restart=on-failure`, and a daemon a test or a CI runner's own service started is in that service's cgroup too.
  let restarters (ports: Ports) (daemon: ProcessFacts) (parent: ProcessFacts option) (others: ProcessFacts list) : Restarter list =
    let supervisor =
      match parent with
      | Some p when isWatchdogArgv p.Argv ->
        match unitOfCgroup p.Cgroup with
        | Some unitName -> [ Restarter.SystemdUnit unitName ]
        | None -> [ Restarter.Watchdog p.Pid ]
      | Some _
      | None -> []
    let bridges =
      others
      |> List.filter (fun p ->
        p.Pid <> daemon.Pid && isBridgeArgv p.Argv && DaemonPort.resolveWith ports.Home p.Argv = ports.Stopped)
      |> List.map (fun p -> Restarter.Bridge p.Pid)
    supervisor @ bridges

  /// One `/proc` file's text. A process can end between listing and reading, and another user's files can be closed
  /// to us; both read as nothing.
  let private readProcFile (path: string) : string option =
    try Some(IO.File.ReadAllText path)
    with
    | :? IO.IOException
    | :? UnauthorizedAccessException -> None

  let private readProc (pid: int) : ProcessFacts option =
    let dir = sprintf "/proc/%d" pid
    readProcFile (dir + "/cmdline")
    |> Option.map (fun cmdline ->
      { Pid = pid
        Parent =
          match readProcFile (dir + "/stat") |> Option.bind parentPidOfStat with
          | Some ppid -> ParentProcess.Pid ppid
          | None -> ParentProcess.Unknown
        Argv = cmdline.Split('\000', StringSplitOptions.RemoveEmptyEntries) |> List.ofArray
        Cgroup = readProcFile (dir + "/cgroup") |> Option.defaultValue "" })

  /// The daemon `sagefs stop` is about to stop: the port it was asked about and the process that holds it.
  type Stopping = { Port: int; Pid: int }

  /// The restarters of the daemon `sagefs stop` is about to stop, read before it stops (its `/proc` entry goes with
  /// it). Where there is no `/proc` (macOS, Windows), only a loaded launchd agent can be seen.
  let restartersOf (stopping: Stopping) : Restarter list =
    match IO.Directory.Exists "/proc/self" with
    | true ->
      match readProc stopping.Pid with
      | None -> []
      | Some daemon ->
        let parent =
          match daemon.Parent with
          | ParentProcess.Pid ppid -> readProc ppid
          | ParentProcess.Unknown -> None
        let others =
          IO.Directory.GetDirectories "/proc"
          |> Array.choose (fun dir ->
            match Int32.TryParse(IO.Path.GetFileName dir) with
            | true, pid when pid <> Environment.ProcessId -> readProc pid
            | _ -> None)
          |> List.ofArray
        restarters { Stopped = stopping.Port; Home = DaemonPort.configured () } daemon parent others
    | false ->
      // `launchctl list <label>` exits 0 only when the agent is loaded in this user's domain.
      let loaded () =
        try
          let psi = ProcessStartInfo("launchctl", UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true)
          psi.ArgumentList.Add "list"
          psi.ArgumentList.Add Restarter.launchdLabel
          use proc = Process.Start psi
          proc.StandardOutput.ReadToEnd() |> ignore
          proc.WaitForExit()
          proc.ExitCode = 0
        with :? ComponentModel.Win32Exception -> false
      match OperatingSystem.IsMacOS() && loaded () with
      | true -> [ Restarter.LaunchdAgent Restarter.launchdLabel ]
      | false -> []

/// The process start info for one daemon launch. The argv goes in `ArgumentList`, one element per
/// argument, so a working directory with a space in it reaches the daemon as one argument. A joined
/// `Arguments` string is split again by the child's command-line parser, and `--dir "/my project"`
/// then arrives as two arguments.
let daemonStartInfo (launch: DaemonLaunch) : ProcessStartInfo =
  let psi =
    ProcessStartInfo(
      launch.ExePath,
      WorkingDirectory = launch.WorkingDirectory,
      UseShellExecute = false,
      RedirectStandardOutput = false,
      RedirectStandardError = false)
  launch.Args |> List.iter psi.ArgumentList.Add
  psi

/// How a watchdog ended.
[<RequireQualifiedAccess>]
type WatchdogEnd =
  /// It was asked to stop: Ctrl+C, or the owner it watched is gone.
  | Cancelled
  /// The daemon crashed more often than the restart policy allows.
  | GaveUp of reason: string

/// The exit code of a watchdog that gave up. It is a failure: a supervisor that gave up is not a stop anyone asked
/// for, and exit 0 told systemd's `Restart=on-failure` and launchd's `KeepAlive` that all was well.
let watchdogGaveUpExitCode = 1

module WatchdogEnd =
  let exitCode (ending: WatchdogEnd) : int =
    match ending with
    | WatchdogEnd.Cancelled -> 0
    | WatchdogEnd.GaveUp _ -> watchdogGaveUpExitCode

/// Whether the watchdog's loop ends after this action. Only giving up ends it.
let endAfter (action: Action) : WatchdogEnd option =
  match action with
  | Action.GiveUp reason -> Some(WatchdogEnd.GaveUp reason)
  | Action.StartDaemon
  | Action.RestartDaemon _
  | Action.Wait -> None

/// Impure watchdog runner that monitors and restarts the daemon process.
/// Uses the pure Watchdog module for all decisions.
let run
  (config: Config)
  (daemonArgs: string list)
  (workingDirectory: string)
  (ct: CancellationToken)
  : Threading.Tasks.Task<WatchdogEnd> = task {
  let mutable state = emptyState DateTime.UtcNow
  // `return` inside a task's while loop does not leave the loop, so the loop reads this completion instead.
  let ending = Threading.Tasks.TaskCompletionSource<WatchdogEnd>()
  let port = daemonPort daemonArgs

  let checkDaemonStatus (pid: int option) =
    match pid with
    | None -> DaemonStatus.NotRunning
    | Some pid ->
      match DaemonState.isProcessAlive pid with
      | true -> DaemonStatus.Running
      | false -> DaemonStatus.NotRunning

  let startDaemon () =
    // Under the dotnet muxer (`dotnet SageFs.dll --supervised`) the daemon is `dotnet SageFs.dll`, never bare `dotnet`.
    let exePath, leadingArgs =
      match McpBridge.daemonSpawnCommand (McpBridge.currentSpawnFacts ()) with
      | McpBridge.DaemonSpawn.Run (program, leading) -> program, leading
      | McpBridge.DaemonSpawn.MuxerWithoutEntry _ as refused ->
        Log.error "[watchdog] %s" (McpBridge.DaemonSpawn.describe refused)
        Process.GetCurrentProcess().MainModule.FileName, []
    let psi =
      daemonStartInfo { ExePath = exePath; Args = leadingArgs @ daemonArgs; WorkingDirectory = workingDirectory }
    // The daemon inherits this process's environment, which holds only what a person set: it works out its own
    // settings snapshot and tier, from the same files.
    psi.Environment["SAGEFS_SUPERVISED"] <- "1"
    psi.Environment["SAGEFS_RESTART_COUNT"] <- state.RestartState.RestartCount.ToString()
    let proc = Process.Start(psi)
    Log.info "[watchdog] Started daemon PID %d" proc.Id
    proc.Id

  Log.info "[watchdog] Supervisor started (check interval: %gs, grace period: %gs)"
    config.CheckInterval.TotalSeconds config.GracePeriod.TotalSeconds

  while not ct.IsCancellationRequested && not ending.Task.IsCompleted do
    let status = checkDaemonStatus state.DaemonPid
    let checkedAt = DateTime.UtcNow
    let action, newState = decide config (jitterSeed workingDirectory port checkedAt) state status checkedAt
    state <- newState

    match action with
    | Action.StartDaemon ->
      let pid = startDaemon ()
      state <- recordStart pid DateTime.UtcNow state
    | Action.RestartDaemon delay ->
      Log.warn "[watchdog] Daemon crashed. Restarting in %gs..." delay.TotalSeconds
      // The owner dying cancels `ct` while this waits: that ends the watchdog, it is not a crash of the watchdog.
      try
        do! Threading.Tasks.Task.Delay(delay, ct)
        let pid = startDaemon ()
        state <- recordStart pid DateTime.UtcNow state
      with :? OperationCanceledException -> ()
    | Action.Wait -> ()
    | Action.GiveUp reason ->
      Log.error "[watchdog] Giving up: %s. Exiting with code %d so the service manager sees a failure." reason watchdogGaveUpExitCode
    endAfter action |> Option.iter (fun ended -> ending.TrySetResult ended |> ignore)

    match ending.Task.IsCompleted with
    | true -> ()
    | false ->
      try
        do! Threading.Tasks.Task.Delay(config.CheckInterval, ct)
      with
      | :? OperationCanceledException -> ()
  match ending.Task.IsCompleted with
  | true -> return! ending.Task
  | false -> return WatchdogEnd.Cancelled
}
