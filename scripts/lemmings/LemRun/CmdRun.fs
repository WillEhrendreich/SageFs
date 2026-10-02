/// One disposable trial: Command Code (cmdc) on a FREE model, given only what a new user has (the
/// project, the SageFs skill where cmdc discovers skills, the SageFs MCP server registered the way
/// the README says), does a task with SageFs. The harness records what happened, runs the task's
/// oracle itself, writes <run-dir>/out/summary.json, and then prunes the run down to its evidence.
///
/// Every lemming is a client of the ONE shared SageFs daemon on 37749 (never started, stopped,
/// restarted or configured here), so the person running the harness can watch them all in its
/// dashboard. Residue is read from the dashboard API first and stopped by exact session id, only for
/// sessions under this run.
///
/// The pieces after `run` (the daemon gate, the sandboxed cmdc, the cleanup, the oracle, the score,
/// the prune) are shared with the editor runners, which differ only in what surrounds the lemming.
module LemRun.CmdRun

open System
open System.Diagnostics
open System.IO
open System.Threading.Tasks
open LemRun.Failure
open LemScore
open LemScore.Types
open LemScore.SharedDaemon

// ---- the shared daemon ------------------------------------------------------------------------

/// What the harness read from the daemon at the start: where it is, which build, how much room.
type Shared =
  { Port: int
    DashPort: int
    DaemonVersion: string
    MemPressure: string
    MemAvail: int64
    Leases: int }

/// Checks the shared daemon and never starts one. Refuses when it is unreachable, unhealthy or
/// short of room after a bounded wait.
let useSharedDaemon () : Shared =
  match useShared defaultMcpPort true with
  | Error why ->
    eprintfn "refused: %s" why
    fail (DaemonUnavailable "refusing to run without a healthy shared daemon")
  | Ok ready ->
    { Port = defaultMcpPort
      DashPort = dashboardPortFor defaultMcpPort
      DaemonVersion = ready.Status.CoreVersion
      MemPressure = ready.Status.MemoryPressure
      MemAvail = ready.Status.AvailableBytes
      Leases = ready.Status.ActiveLeases }

/// The pid listening on the shared daemon's port, or nothing. Read before and after a run: the
/// harness never stops the daemon, so a different pid afterwards means something in the run did.
let daemonPid (port: int) : string option =
  let c = Proc.run (Proc.spec "ss" [ "-ltnpH"; sprintf "( sport = :%d )" port ]) None
  let m = Text.RegularExpressions.Regex.Match(c.Stdout, @"pid=(\d+)")
  match m.Success with
  | true -> Some m.Groups[1].Value
  | false -> None

// ---- the model, the run id ---------------------------------------------------------------------

/// Nothing is touched, and no model is called, until the model is confirmed FREE.
let assertFreeModel (model: string) : unit =
  let catalog =
    match Env.var "LEM_CATALOG_FILE" with
    | Some path -> Ok (File.ReadAllText path)
    | None -> Catalog.liveText ()
  match catalog |> Result.bind (fun text -> Catalog.checkFree (Catalog.parse text) model) with
  | Ok entry -> eprintfn "%s is FREE: %s" entry.Id entry.Description
  | Error why ->
    eprintfn "refused: %s" why
    fail (Refused (sprintf "%s refused. Lemmings run on FREE models only." model))

/// A run: its id, its directory, the lemming's working directory and the evidence directory.
type Run =
  { Id: string
    Dir: string
    Work: string
    Out: string }

let runOf (id: string) : Run =
  let dir = Path.Combine(Env.lemRoot, id)
  { Id = id; Dir = dir; Work = Path.Combine(dir, "w"); Out = Path.Combine(dir, "out") }

/// `auto` picks the next free `<model-short>-<task>-<nn>`. A run that exists is never reused.
let resolveRun (idArg: string) (model: string) (task: string) : Run =
  let id = match idArg with "auto" -> Catalog.nextRunId Env.lemRoot model task | explicit -> explicit
  let run = runOf id
  match Directory.Exists run.Dir || File.Exists run.Dir with
  | true -> fail (Refused (sprintf "run %s already exists: pick a new id" id))
  | false -> run

// ---- the sandbox of a run -----------------------------------------------------------------------

/// The core sandbox for a run, with the stored builds it may read.
let coreFor (run: Run) (mounts: string list) (extraPath: string option) (maskTools: bool) : Sandbox.Core =
  { Home = Env.home
    RunDir = run.Dir
    Workdir = run.Work
    ExtraPath = extraPath
    NugetCache = Directory.Exists(Path.Combine(Env.home, ".nuget", "packages"))
    MaskDotnetTools = maskTools
    ReadOnlyMounts = mounts }

/// Runs a command in the lemming's sandbox for oracles that must execute code the lemming wrote
/// (a test run): no network and no credentials. LEM_ORACLE_NET keeps the network for a restore.
let sandboxExec (core: Sandbox.Core) (network: Sandbox.Network) (extraBwrap: string list) (seconds: int) (command: string list) : Proc.Captured =
  Sandbox.ensureDirs core
  let args = Sandbox.execArgs core network extraBwrap @ Sandbox.withTimeout Env.Limits.killAfterSeconds seconds command
  Proc.run (Proc.spec "bwrap" args) None

let private oracleNetwork () : Sandbox.Network =
  match Env.isSet "LEM_ORACLE_NET" with
  | true -> Sandbox.NetworkOpen
  | false -> Sandbox.NetworkIsolated

// ---- cmdc -----------------------------------------------------------------------------------------

/// How cmdc ended and how long it ran.
type Cmdc = { ExitCode: int; Seconds: int }

/// What a run asks of the cmdc step beyond the defaults. An editor harness adds mounts and
/// environment here, which are appended after the core sandbox's, so they win.
type Extras =
  { Bwrap: string list
    Env: (string * string) list
    /// Stored builds mounted read-only into the sandbox (the bridge, the editor driver).
    Mounts: string list
    MaskDotnetTools: bool }

let noExtras : Extras = { Bwrap = []; Env = []; Mounts = []; MaskDotnetTools = false }

/// cmdc must be on PATH, and installed under ~/.local/share/mise: the sandbox binds only that toolchain.
let locateCmdc () : string * string =
  match Proc.which "cmdc" with
  | None -> fail (ToolchainMissing "cmdc is not on PATH")
  | Some path ->
    let real = Proc.realPath path
    match real.StartsWith(Path.Combine(Env.home, ".local", "share", "mise") + "/") with
    | true -> path, (match Path.GetDirectoryName path with null -> "" | d -> d)
    | false -> fail (ToolchainMissing "cmdc is not under ~/.local/share/mise, which is the only toolchain the sandbox binds")

let private watcherDelay = TimeSpan.FromSeconds 1.0

/// While the lemming runs, a watcher reads the shared daemon's sessions list into
/// out/sessions.seen.json: the proof that this lemming's sessions showed up in the dashboard being
/// watched. Returns the function that stops it: it ends itself on the stop file, which it is given
/// a bounded time to see.
let startWatcher (run: Run) (port: int) : unit -> unit =
  let stopFile = Path.Combine(run.Out, "watch.stop")
  let seenFile = Path.Combine(run.Out, "sessions.seen.json")
  File.Delete stopFile
  match validRunDir run.Work with
  | Error _ -> ignore
  | Ok dir ->
    let watcher =
      Task.Run(fun () ->
        let seen = watch port dir stopFile (LemScore.Program.writeSeen seenFile)
        LemScore.Program.writeSeen seenFile seen)
    fun () ->
      File.WriteAllText(stopFile, "")
      watcher.Wait(Env.Limits.watcherStop) |> ignore

/// Runs cmdc headless under bubblewrap in the run's working directory. The bridge, the workspace
/// files and the sandbox mounts are laid out first.
let runCmdc (run: Run) (promptFile: string) (model: string) (turns: int) (shared: Shared) (bridge: Bridge.Bridge) (extras: Extras) : Cmdc =
  let cmdcPath, cmdcDir = locateCmdc ()
  ignore cmdcPath
  Workspace.install run.Work bridge.Command shared.Port
  Directory.CreateDirectory(Path.Combine(run.Dir, "cmdchome")) |> ignore
  // bubblewrap's mount point; the credential itself is bound over it
  File.WriteAllText(Path.Combine(run.Dir, "cmdchome", "auth.json"), "")
  let mounts = (bridge.Mount |> Option.toList) @ extras.Mounts @ [ AppContext.BaseDirectory.TrimEnd('/') ]
  let core = coreFor run mounts (Some cmdcDir) extras.MaskDotnetTools
  Sandbox.ensureDirs core
  let sandbox = Sandbox.lemmingArgs core extras.Env extras.Bwrap
  let prompt = File.ReadAllText(promptFile).TrimEnd('\n')
  let supervised =
    [ Path.Combine(Env.home, ".dotnet", "dotnet"); Path.Combine(AppContext.BaseDirectory, "LemRun.dll"); "supervise"; "--ps-dir"; Sandbox.sbxDir core; "--" ]
    @ Sandbox.withTimeout Env.Limits.killAfterSeconds (Env.Limits.cmdcSeconds ()) (Sandbox.cmdcCommand prompt model turns)
  let stopWatcher = startWatcher run shared.Port
  let clock = Stopwatch.StartNew()
  let code =
    Proc.runToFiles (Proc.spec "bwrap" (sandbox @ supervised)) (Path.Combine(run.Out, "events.ndjson")) (Path.Combine(run.Out, "cmdc.stderr"))
  clock.Stop()
  stopWatcher ()
  { ExitCode = code; Seconds = int clock.Elapsed.TotalSeconds }

// ---- afterwards, outside the sandbox -----------------------------------------------------------

/// Reads the sessions under the working directory FIRST (residue.json), stops exactly those by id,
/// and verifies. `own` are ids the harness made for the run: stopped with the rest, not residue.
let cleanupSessions (run: Run) (port: int) (own: string list) : Cleanup =
  Directory.CreateDirectory run.Out |> ignore
  try
    let report = cleanup port run.Work own
    LemScore.Program.writeResidue (Path.Combine(run.Out, "residue.json")) report
    report.Verdict
  with ex ->
    eprintfn "cleanup could not complete: %s" ex.Message
    CleanupFailed

/// What the lemming changed, relative to where it started (out/changed.txt).
let writeChanged (run: Run) (git: string -> string list -> Proc.Captured) : unit =
  File.WriteAllLines(Path.Combine(run.Out, "changed.txt"), Workspace.changedFiles git run.Work)

/// The oracle decides, outside the sandbox. Returns what `score` is given: an exit code, or "skip"
/// when the task has no oracle (which makes the run a HarnessError, never a pass). Its output goes
/// to out/oracle.out.
let runOracle (core: Sandbox.Core) (run: Run) (task: string) : string =
  match Oracles.forTask task with
  | None -> "skip"
  | Some oracle ->
    let lines = Collections.Concurrent.ConcurrentQueue<string>()
    let context : Oracles.Context =
      { RunDir = run.Dir
        Say = lines.Enqueue
        Sandboxed = fun seconds extra command -> sandboxExec core (oracleNetwork ()) extra seconds command }
    let finished = Task.Run(fun () -> Oracles.run oracle context)
    let code =
      match finished.Wait Env.Limits.oracle with
      | true -> finished.Result
      | false ->
        lines.Enqueue "the oracle did not finish in time"
        Proc.timedOutExit
    File.WriteAllLines(Path.Combine(run.Out, "oracle.out"), lines)
    string code

/// summary.json, written by LemScore from what the run left in out/. Returns its text.
let score (run: Run) (task: string) (model: string) (harness: Harness) (oracleExit: string) (shared: Shared) (bridge: Bridge.Bridge) (cmdc: Cmdc option) (cleanupResult: Cleanup) : string =
  let readings =
    [ "cmdc-exit", string (cmdc |> Option.map _.ExitCode |> Option.defaultValue -1)
      "seconds", string (cmdc |> Option.map _.Seconds |> Option.defaultValue 0)
      "sagefs-version", bridge.SagefsVersion
      "daemon-version", shared.DaemonVersion
      "bridge-version", bridge.Version
      "cleanup", Cleanup.toString cleanupResult
      "mem-start", shared.MemPressure
      "avail-start", string shared.MemAvail
      "leases-start", string shared.Leases
      "port", string shared.Port ]
    |> Map.ofList
  LemScore.Program.scoreRun readings run.Dir task model (Harness.toString harness) oracleExit

/// Whether a finished run keeps everything, or only its evidence.
type Retention =
  | KeepEverything
  | KeepEvidence

let retentionFromEnv () : Retention =
  match Env.isSet "LEM_KEEP_RUN" with
  | true -> KeepEverything
  | false -> KeepEvidence

/// The end of every run: everything except out/ is removed, because /tmp is tmpfs and a run's working
/// copy, NuGet folder and sandbox scratch are RAM nobody gets back. Done only for a run that has a
/// summary.json, and last, after every step that reads the working directory.
let finishRun (run: Run) (retention: Retention) : unit =
  match retention with
  | KeepEverything -> say (sprintf "kept the whole run directory %s (LEM_KEEP_RUN is set)" run.Dir)
  | KeepEvidence ->
    match Prune.slim run.Dir with
    | Ok freed -> say (sprintf "pruned %s to out/ (%s freed)" run.Dir (Prune.humanBytes freed))
    | Error why -> say (sprintf "could not prune %s: %s" run.Dir why)

// ---- run-lemming-cmd ------------------------------------------------------------------------------

/// What `run-lemming-cmd` is asked for.
type Request =
  { Fixture: Workspace.Fixture
    Task: string
    Model: string
    /// An id, or "auto".
    IdArg: string
    Turns: int }

/// A run that builds the fixture and stops, so a fixture or a task setup can be checked without
/// calling a model (LEM_PREPARE_ONLY).
type Mode =
  | FullRun
  | PrepareOnly

let modeFromEnv () : Mode =
  match Env.isSet "LEM_PREPARE_ONLY" with
  | true -> PrepareOnly
  | false -> FullRun

let defaultTurns = 60

let harnessFromEnv () : Harness =
  match Harness.tryParse (Env.varOr "LEM_HARNESS" "cmdc") with
  | Ok h -> h
  | Error why -> fail (Refused why)

/// `run-lemming-cmd <fixture> <task> <free-model> <run-id|auto> [max-turns]`. Returns the exit code.
let run (request: Request) (mode: Mode) (retention: Retention) : int =
  let taskFile = Path.Combine(Env.lemDir, "tasks", request.Task + ".md")
  match File.Exists taskFile with
  | false -> fail (Refused (sprintf "no task: %s" taskFile))
  | true -> ()
  let harness = harnessFromEnv ()
  assertFreeModel request.Model
  // A fixture that does not exist is refused before anything is created.
  match request.Fixture with
  | Workspace.Named name when not (Directory.Exists(Path.Combine(Env.lemDir, "fixtures", name))) ->
    fail (Refused (sprintf "no fixture: %s" (Path.Combine(Env.lemDir, "fixtures", name))))
  | _ -> ()
  let run = resolveRun request.IdArg request.Model request.Task
  let shared = useSharedDaemon ()
  // The bridge the lemming will run, checked against the daemon before anything is copied. A skewed
  // bridge is reported now (and refused under LEM_REQUIRE_SAME_VERSION), not discovered in the summary.
  let bridge = Bridge.prepare (Bridge.sourceFromEnv ()) shared.DaemonVersion
  Directory.CreateDirectory run.Work |> ignore
  Directory.CreateDirectory run.Out |> ignore
  // If this is interrupted, the sandbox dies with its parent; the run's sessions are stopped by id here.
  use _interrupt = Proc.onInterrupt (fun () -> cleanupSessions run shared.Port [] |> ignore)
  say (sprintf "run %s: %s on %s/%s; dashboard http://localhost:%d/dashboard (working directory %s)" run.Id request.Model (match request.Fixture with Workspace.Named n -> n | Workspace.SagefsCopy p -> "sagefs-copy:" + p) request.Task shared.DashPort run.Work)
  Workspace.copyFixture request.Fixture run.Work
  // Task setup (a seeded bug, say) happens BEFORE the baseline commit, so the diff is the lemming's.
  Workspace.setupFor request.Task |> Option.iter (fun setup -> setup run.Work)
  Workspace.initRepository run.Work Workspace.cmdBaseline
  File.Copy(taskFile, Path.Combine(run.Out, "prompt.md"))
  match mode with
  | PrepareOnly ->
    say (sprintf "prepared %s (no model was called)" run.Work)
    0
  | FullRun ->
    let cmdc = runCmdc run (Path.Combine(run.Out, "prompt.md")) request.Model request.Turns shared bridge { noExtras with Mounts = [] }
    // Residue first (read before anything is stopped), then exactly those sessions are stopped.
    let cleanupResult = cleanupSessions run shared.Port []
    writeChanged run Workspace.gitIn
    let core = coreFor run (bridge.Mount |> Option.toList) None false
    let oracleExit = runOracle core run request.Task
    let json = score run request.Task request.Model harness oracleExit shared bridge (Some cmdc) cleanupResult
    printfn "%s" json
    finishRun run retention
    0
