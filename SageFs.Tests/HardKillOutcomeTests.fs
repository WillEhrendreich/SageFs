/// OUTCOME gate for the hard-kill promise (roast R-TESTS-7): when the daemon dies the
/// way a crash, `kill -9`, Task Manager or an OS shutdown kills it, nothing it started
/// is left behind. No worker, no FSI host, no listener on a port nobody can reach.
///
/// WHAT WAS COVERED, AND WHY IT WAS NOT ENOUGH. The decision cores are well tested:
/// `OwnerMonitorTests` and `ParentMonitorTests` inject the pid lookup,
/// `ShutdownLifecycleTests` kills real children through the daemon's own
/// `killWorkerPids` sweep. Every one of them runs in the test's own process, against a
/// pid lookup it supplies. The promise is about a DIFFERENT thing: a real daemon, with a
/// real session, killed from outside by a signal that gives it no chance to run a single
/// line of cleanup. A watchdog that is never started, started with the wrong pid, or
/// started and then cancelled by an unrelated token ships green under all of them.
///
/// SO THIS GATE KILLS A REAL DAEMON. The layers it exercises, each with its own case:
///   1. SIGKILL the daemon. The worker's owner monitor (SageFs.Host/WorkerMain.fs, the
///      watchdog on SAGEFS_DAEMON_PID) must notice and exit the worker, and the FSI host
///      must follow when the worker's pipe closes. Run with a `run_app` app live in the
///      worker (it is a thread of the worker, not a process of its own, so "the app is
///      gone" is "the worker is gone and the ports it held are free").
///   2. The daemon's owner (`--owner-pid`) dies. The daemon's own owner monitor must make
///      the daemon exit, and it takes its worker and FSI host with it.
///   3. SIGKILL the worker alone. The FSI host must exit on its own, because the worker's
///      pipe to it closed (SageFs.FsiHost/Program.fs, the reader loop ends on EOF). The
///      daemon stays up and restarts the worker, so this case tells the FSI host's layer
///      apart from the worker's.
///   4. `--supervised`: the watchdog (SageFs/WatchdogRunner.fs) restarts a daemon that was
///      killed hard.
///
/// THE ORACLE IS THE PROCESS TABLE, NOT A SLEEP. Before the kill the test reads every
/// descendant of the daemon from /proc (pid AND start time, so a recycled pid is never
/// mistaken for the original), and afterwards waits on each of them exiting
/// (`WaitForExitAsync` against a bound from TestTimeouts, which ends the moment the last
/// one is gone). Port listeners are read from /proc/net/tcp joined to the process's
/// socket inodes before, and from the kernel's listener table after.
///
/// LINUX ONLY, ON PURPOSE: the oracle is /proc. The host tier runs on Linux in CI; on a
/// platform without /proc the cases report pending with that reason rather than passing.
module SageFs.Tests.HardKillOutcomeTests

open System
open System.Diagnostics
open System.IO
open System.Net.Http
open System.Net.NetworkInformation
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Expecto
open Expecto.Flip
open ModelContextProtocol.Client
open ModelContextProtocol.Protocol
open SageFs

module Integration = SageFs.Tests.TestInfrastructure.Integration
module Infra = SageFs.Tests.TestInfrastructure
module Http = SageFs.Tests.HttpApiIntegrationTests

// ─── The process table ──────────────────────────────────────────────────────

/// A process as /proc names it. The start time is in clock ticks since boot, so a pid the
/// OS recycles is a different `ProcId` and can never be taken for the original.
type ProcId = { Pid: int; StartTicks: int64 }

type ProcRow = { Id: ProcId; Parent: int; Running: bool }

/// A process the daemon owned when it was snapshotted, with the command that started it
/// (so a survivor is named in the failure, not just numbered).
type Owned = { Proc: ProcId; Command: string }

module ProcessTable =
  /// Parse /proc/<pid>/stat. The command name sits in parentheses and may hold spaces and
  /// parentheses of its own, so the fields are read after the LAST ')'. After it the tail
  /// starts at the state (field 3), so the parent pid is tail[1] and the start time, field
  /// 22, is tail[19]. A zombie or dead process is not Running.
  let parseStat (pid: int) (stat: string) : ProcRow option =
    match stat.LastIndexOf ')' with
    | -1 -> None
    | close ->
      let tail = stat.Substring(close + 1).Split([| ' ' |], StringSplitOptions.RemoveEmptyEntries)
      match tail.Length >= 20 with
      | false -> None
      | true ->
        match Int32.TryParse tail.[1], Int64.TryParse tail.[19] with
        | (true, parent), (true, start) ->
          Some { Id = { Pid = pid; StartTicks = start }; Parent = parent; Running = (tail.[0] <> "Z" && tail.[0] <> "X") }
        | _ -> None

  let read () : ProcRow list =
    Directory.EnumerateDirectories "/proc"
    |> Seq.choose (fun dir ->
      match Int32.TryParse(Path.GetFileName dir) with
      | true, pid ->
        try parseStat pid (File.ReadAllText(Path.Combine(dir, "stat"))) with _ -> None
      | false, _ -> None)
    |> Seq.toList

  /// Every process below `root`, however deep, in the order the walk found them.
  let descendantsOf (root: int) (table: ProcRow list) : ProcRow list =
    let rec walk (frontier: int list) (seen: ProcRow list) =
      match frontier with
      | [] -> seen
      | parents ->
        let children =
          table
          |> List.filter (fun p -> List.contains p.Parent parents && not (List.exists (fun s -> s.Id.Pid = p.Id.Pid) seen))
        walk (children |> List.map (fun c -> c.Id.Pid)) (seen @ children)
    walk [ root ] []

  let commandOf (pid: int) : string =
    try (File.ReadAllText(sprintf "/proc/%d/cmdline" pid)).Replace('\000', ' ').Trim() with _ -> ""

  /// The TCP ports a process is LISTENING on: its socket inodes (/proc/<pid>/fd) joined
  /// with the LISTEN rows (state 0A) of /proc/net/tcp and tcp6.
  let listeningPortsOf (pid: int) : int list =
    let inodes =
      try
        Directory.EnumerateFileSystemEntries(sprintf "/proc/%d/fd" pid)
        |> Seq.choose (fun fd ->
          try
            match (FileInfo fd).LinkTarget with
            | null -> None
            | target when target.StartsWith "socket:[" -> Some (target.Substring(8).TrimEnd(']'))
            | _ -> None
          with _ -> None)
        |> Set.ofSeq
      with _ -> Set.empty
    [ "/proc/net/tcp"; "/proc/net/tcp6" ]
    |> List.collect (fun file -> try File.ReadAllLines file |> Array.skip 1 |> Array.toList with _ -> [])
    |> List.choose (fun line ->
      let cols = line.Split([| ' ' |], StringSplitOptions.RemoveEmptyEntries)
      match cols.Length > 9 && cols.[3] = "0A" && inodes.Contains cols.[9] with
      | true -> Some (Convert.ToInt32(cols.[1].Substring(cols.[1].LastIndexOf ':' + 1), 16))
      | false -> None)
    |> List.distinct

  /// Is anything in this machine's network namespace listening on the port right now?
  let isListening (port: int) : bool =
    IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners()
    |> Array.exists (fun endpoint -> endpoint.Port = port)

  /// Gone means: no such pid, a different process now holds the pid, or it is a zombie
  /// (exited, waiting only for its parent to collect it).
  let isGone (id: ProcId) : bool =
    match read () |> List.tryFind (fun row -> row.Id.Pid = id.Pid) with
    | None -> true
    | Some row when row.Id.StartTicks <> id.StartTicks -> true
    | Some row -> not row.Running

/// Build tooling a session's build can leave running by design (MSBuild node reuse, the
/// compiler server). They outlive the build on purpose, so they are not an orphaned
/// worker and are not what this gate is about.
let isBuildInfrastructure (command: string) : bool =
  [ "MSBuild.dll"; "VBCSCompiler"; "build-server" ] |> List.exists (fun marker -> command.Contains marker)

let ownedOf (row: ProcRow) : Owned =
  { Proc = row.Id; Command = ProcessTable.commandOf row.Id.Pid }

/// Everything below `root` that is the product's own: the workers, the FSI hosts and
/// anything else it started, with the build servers set apart.
let snapshotOwned (root: int) : Owned list * Owned list =
  ProcessTable.read ()
  |> ProcessTable.descendantsOf root
  |> List.map ownedOf
  |> List.partition (fun o -> not (isBuildInfrastructure o.Command))

let describe (o: Owned) = sprintf "pid %d: %s" o.Proc.Pid o.Command

/// Wait until every one of these processes has exited, or the bound runs out. Returns the
/// survivors. The wait is on the exit itself (not a sleep), so it ends when the last one goes.
let awaitGone (owned: Owned list) (bound: TimeSpan) : Task<Owned list> = task {
  use cts = new CancellationTokenSource(bound)
  let waitFor (o: Owned) : Task =
    task {
      try
        use running = Process.GetProcessById o.Proc.Pid
        do! running.WaitForExitAsync cts.Token
      with
      | :? ArgumentException -> () // no such process: already gone
      | :? InvalidOperationException -> () // exited between the lookup and the wait
      | :? OperationCanceledException -> () // the bound ran out: the survivors are read below
    }
  do! Task.WhenAll(owned |> List.map waitFor)
  return owned |> List.filter (fun o -> not (ProcessTable.isGone o.Proc))
}

/// SIGKILL a process by exact pid, only if it is still the process we snapshotted.
let killExact (id: ProcId) =
  match ProcessTable.isGone id with
  | true -> ()
  | false -> try (Process.GetProcessById id.Pid).Kill() with _ -> ()

/// The exit code .NET reports for a process the kernel ended with SIGKILL (128 + 9).
/// A daemon that ran any cleanup of its own would exit with something else.
let exitCodeOfSigkill = 137

// ─── A real daemon, started the way the other host gates start one ──────────

let consoleTickerDir = Path.Combine(Http.repoRoot, "samples", "demos", "SageFs.Samples.ConsoleTicker")

let consoleTickerProject = Path.Combine(consoleTickerDir, "SageFs.Samples.ConsoleTicker.fsproj")

[<RequireQualifiedAccess>]
type Owner =
  /// The test runner owns the daemon, exactly as `Http.startDaemonWithArgs` does. It stays alive.
  | TestRunner
  /// A stand-in process the test can kill to make the owner die.
  | StandIn of Process

[<RequireQualifiedAccess>]
type Supervision =
  | Plain
  /// `--supervised`: the process we spawn is the watchdog, and the daemon is its child.
  | Supervised

type Daemon =
  { /// The process the test spawned: the daemon, or the watchdog above it.
    Process: Process
    DataDir: string
    McpPort: int
    DashboardPort: int
    Supervision: Supervision
    /// Everything the test snapshotted, so the cleanup can reap it by exact pid whatever happened.
    Tracked: ResizeArray<Owned> }

/// The pid the dashboard's /api/daemon-info says is answering, or -1 while nothing does.
let answeringPid (dashboardPort: int) : Task<int> = task {
  try
    use client = new HttpClient()
    client.Timeout <- TestTimeouts.httpProbe
    let! body = client.GetStringAsync(sprintf "http://localhost:%d/api/daemon-info" dashboardPort)
    use doc = JsonDocument.Parse body
    return doc.RootElement.GetProperty("pid").GetInt32()
  with _ -> return -1
}

/// Is `pid` a live daemon of this daemon handle: the spawned process itself, or for a
/// supervised daemon a child of the watchdog.
let isOurDaemon (spawned: Process) (supervision: Supervision) (pid: int) : bool =
  match supervision with
  | Supervision.Plain -> pid = spawned.Id
  | Supervision.Supervised ->
    ProcessTable.read () |> List.exists (fun row -> row.Id.Pid = pid && row.Parent = spawned.Id && row.Running)

let startDaemon (owner: Owner) (supervision: Supervision) : Task<Daemon> = task {
  let mcpPort = Http.reserveLoopbackPort ()
  let ownerProcess =
    match owner with
    | Owner.TestRunner -> Process.GetCurrentProcess()
    | Owner.StandIn stand -> stand
  let dataDir = Directory.CreateTempSubdirectory("sagefs-hardkill-data-").FullName
  let psi = ProcessStartInfo()
  psi.FileName <- Infra.SageFsBinary.path ()
  psi.UseShellExecute <- false
  psi.CreateNoWindow <- true
  psi.WorkingDirectory <- consoleTickerDir
  let args =
    [ yield "--mcp-port"; yield string mcpPort
      yield "--no-resume"
      yield "--owner-pid"; yield string ownerProcess.Id
      yield "--owner-start"; yield string (ownerProcess.StartTime.ToUniversalTime().Ticks)
      // Belt and braces on top of the owner: a test that dies in a way the owner watch misses still cannot leak this.
      yield "--ttl"; yield "10m"
      match supervision with
      | Supervision.Plain -> ()
      | Supervision.Supervised -> yield "--supervised" ]
  for arg in args do
    psi.ArgumentList.Add arg
  psi.Environment["SAGEFS_DATA_DIR"] <- dataDir
  let spawned = Process.Start psi

  // Ready means a daemon of OURS answers: its own pid, read from its own dashboard.
  let! ready =
    Infra.waitForAsync (TestTimeouts.asMs TestTimeouts.daemonFirstOutput) (fun () -> task {
      match spawned.HasExited with
      | true -> return true
      | false ->
        let! pid = answeringPid (mcpPort + 1)
        return pid > 0 && isOurDaemon spawned supervision pid })
  match ready, spawned.HasExited with
  | true, false ->
    return
      { Process = spawned
        DataDir = dataDir
        McpPort = mcpPort
        DashboardPort = mcpPort + 1
        Supervision = supervision
        Tracked = ResizeArray<Owned>() }
  | true, true ->
    return failwithf "the daemon on port %d exited with code %d before it answered" mcpPort spawned.ExitCode
  | false, _ ->
    try spawned.Kill() with _ -> ()
    return failwithf "the daemon on port %d never answered within %O" mcpPort TestTimeouts.daemonFirstOutput
}

/// Start the daemon, run the body, and always reap what is left, by exact pid and start time.
/// The watchdog (if any) goes first so it cannot restart the daemon under the cleanup.
let withDaemon (owner: Owner) (supervision: Supervision) (body: Daemon -> Task<unit>) : Task<unit> = task {
  let! daemon = startDaemon owner supervision
  try
    do! body daemon
  finally
    // Read what is below the spawned process BEFORE it dies: afterwards the children are reparented and no walk finds them.
    let stillBelow = fst (snapshotOwned daemon.Process.Id)
    (try daemon.Process.Kill() with _ -> ())
    for o in Seq.append daemon.Tracked stillBelow do
      killExact o.Proc
    daemon.Process.Dispose()
    try Directory.Delete(daemon.DataDir, true) with _ -> ()
}

// ─── Talking to it ──────────────────────────────────────────────────────────

let connect (port: int) : Task<McpClient> =
  let opts = HttpClientTransportOptions(Endpoint = Uri(sprintf "http://localhost:%d/" port))
  let transport = HttpClientTransport(opts, (null: Microsoft.Extensions.Logging.ILoggerFactory))
  McpClient.CreateAsync(transport, null, null, CancellationToken.None)

/// The tool's OWN answer: the first text block only (the server appends an events block to
/// results, which would corrupt a JSON payload).
let textOf (result: CallToolResult) : string =
  result.Content
  |> Seq.choose (function :? TextContentBlock as t -> Some t.Text | _ -> None)
  |> Seq.tryHead
  |> Option.defaultValue ""

/// A tool call that may hold the connection open (a held status wait) or restart the session first.
let callTool (client: McpClient) (name: string) (args: (string * obj) list) : Task<string> = task {
  use cts = new CancellationTokenSource(TestTimeouts.toolCallThatRestarts)
  let! result = client.CallToolAsync(name, readOnlyDict args, null, null, cts.Token)
  return textOf result
}

/// What `get_session_status` says about one session once it is Ready.
type SessionFacts = { WorkerPid: int; WorkerPort: int; LoadedProjects: int }

/// Kept out of the task builder so the parse needs no `use` inside a resumable state machine.
let parseFacts (json: string) : SessionFacts =
  use doc = JsonDocument.Parse json
  let root = doc.RootElement
  let number (name: string) =
    match root.TryGetProperty name with
    | true, p when p.ValueKind = JsonValueKind.Number -> p.GetInt32()
    | _ -> failtestf "get_session_status carried no number named %s. Body: %s" name json
  let state = root.GetProperty("state").GetString()
  match state with
  | "Ready" ->
    { WorkerPid = number "workerPid"
      WorkerPort = number "workerPort"
      LoadedProjects = root.GetProperty("loadedProjects").GetArrayLength() }
  | other -> failtestf "the session is %s, not Ready. Body: %s" other json

/// Wait for the session to be Ready with the daemon's own held wait (`wait_seconds`), then read its facts.
let readyFacts (client: McpClient) (sessionId: string) : Task<SessionFacts> = task {
  let! json =
    callTool client "get_session_status"
      [ "session_id", box sessionId; "wait_seconds", box (int TestTimeouts.daemonHeldWait.TotalSeconds) ]
  return parseFacts json
}

/// Create a session through the daemon's HTTP API and return its id (the create message IS the id).
let createSession (daemon: Daemon) (projects: string array) (workingDir: string) : Task<string> = task {
  use http = new HttpClient()
  http.BaseAddress <- Uri(sprintf "http://localhost:%d" daemon.McpPort)
  http.Timeout <- TestTimeouts.httpDaemon
  let! status, body = Http.postJson http "/api/sessions/create" {| projects = projects; workingDirectory = workingDir |}
  status |> Expect.equal (sprintf "session create succeeds. Body: %s" body) 200
  use doc = JsonDocument.Parse body
  return doc.RootElement.GetProperty("message").GetString()
}

/// Wait until the session holds its project, the precondition `run_app` enforces and Ready does not imply.
let awaitProjectLoaded (client: McpClient) (sessionId: string) : Task<SessionFacts> = task {
  let mutable latest = { WorkerPid = 0; WorkerPort = 0; LoadedProjects = 0 }
  let! loaded =
    Infra.waitForAsync (TestTimeouts.asMs TestTimeouts.sessionBackAfterRestart) (fun () -> task {
      let! facts = readyFacts client sessionId
      latest <- facts
      return facts.LoadedProjects > 0 })
  loaded |> Expect.isTrue (sprintf "the session holds its project (last facts %A)" latest)
  return latest
}

/// The ticker app is running in the worker once `run_app` answers Running.
let startTicker (client: McpClient) : Task<unit> = task {
  let! raw = callTool client "run_app" [ "project", box "" ]
  match raw.TrimStart().StartsWith "{" with
  | false -> failtestf "run_app refused in prose, so there is no app to kill with the daemon: %s" raw
  | true ->
    use doc = JsonDocument.Parse raw
    doc.RootElement.GetProperty("State").GetString() |> Expect.equal "run_app reports the ticker Running" "Running"
}

/// The oracle is /proc, which only Linux has, so these cases are registered only there (`onlyOnLinux`): on another
/// platform they do not exist, which is different from existing and skipping. If one is ever run elsewhere anyway,
/// it fails and says why, rather than passing without having watched anything.
let linuxOnly (body: unit -> Task<unit>) : Task<unit> = task {
  match OperatingSystem.IsLinux() with
  | true -> do! body ()
  | false -> failtest "this case watches /proc, which this platform does not have; it is registered on Linux only"
}

/// The cases themselves, on a platform that has the oracle; none elsewhere.
let onlyOnLinux (cases: Test list) : Test list =
  match OperatingSystem.IsLinux() with
  | true -> cases
  | false -> []

/// What was alive below the daemon at the moment of the kill, printed so a CI log shows what the gate actually watched.
let reportSnapshot (what: string) (owned: Owned list) (setApart: Owned list) (ports: int list) =
  printfn "hard-kill gate (%s): watching %d processes %A on ports %A; %d build servers set apart %A"
    what owned.Length (List.map describe owned) ports setApart.Length (List.map describe setApart)

let expectNoSurvivors (what: string) (survivors: Owned list) =
  survivors |> List.map describe |> Expect.isEmpty (sprintf "%s: these are still running" what)

let expectPortsFree (what: string) (ports: int list) =
  ports
  |> List.filter ProcessTable.isListening
  |> Expect.isEmpty (sprintf "%s: something still listens on these ports" what)

let expectPortsListening (what: string) (ports: int list) =
  ports
  |> List.filter (ProcessTable.isListening >> not)
  |> Expect.isEmpty (sprintf "%s: these ports should be listening before the kill, or the test proves nothing" what)

[<RequireQualifiedAccess>]
type HostBeforeKill =
  | Running
  /// SIGSTOP: the host cannot read, so it cannot see its worker's pipe close. From outside that is what a host with a
  /// broken exit path looks like.
  | Frozen

/// Send a signal to a process by exact pid, as `kill -<signal> <pid>` would.
let signalExact (signal: string) (id: ProcId) : Task<unit> = task {
  match ProcessTable.isGone id with
  | true -> ()
  | false ->
    let psi = ProcessStartInfo("kill", [ sprintf "-%s" signal; string id.Pid ])
    psi.UseShellExecute <- false
    use sender = Process.Start psi
    do! sender.WaitForExitAsync()
}

/// Start a session, kill its worker ALONE (SIGKILL, the daemon untouched), and return which of the worker's FSI hosts
/// are still running once `bound` has run out or they have all gone.
let killWorkerAlone (daemon: Daemon) (hostBeforeKill: HostBeforeKill) (bound: TimeSpan) : Task<Owned list> = task {
  let! sessionId = createSession daemon [||] consoleTickerDir
  use! client = connect daemon.McpPort
  let! facts = readyFacts client sessionId

  let owned, _ = snapshotOwned daemon.Process.Id
  daemon.Tracked.AddRange owned
  let worker = owned |> List.find (fun o -> o.Proc.Pid = facts.WorkerPid)
  let fsiHost =
    ProcessTable.read ()
    |> ProcessTable.descendantsOf worker.Proc.Pid
    |> List.map ownedOf
    |> List.filter (fun o -> o.Command.Contains "FsiHost")
  fsiHost
  |> List.isEmpty
  |> Expect.isFalse (sprintf "the worker has an FSI host to lose. Worker: %s" (describe worker))

  match hostBeforeKill with
  | HostBeforeKill.Running -> ()
  | HostBeforeKill.Frozen ->
    for host in fsiHost do
      do! signalExact "STOP" host.Proc

  killExact worker.Proc
  return! awaitGone fsiHost bound
}

// ─── The pure part: who a monitor watches ───────────────────────────────────

/// What the watchdog and the daemon below it start their owner monitors from. Proved in a REPL session first (see the
/// commit); kept here as the unit gate beside the outcome gate it serves.
[<Tests>]
let monitoredOwnerTests =
  let ttl = TestTimeouts.ownershipIdleLimit
  let fencedTicks = 639279879983051346L
  testList "Hard kill: the owner a monitor watches" [
    test "an owned daemon is watched, fenced by the start time it was given" {
      let ownership =
        DaemonOwnership.Ownership.OwnedByPid { Pid = 4242; Fence = DaemonOwnership.OwnerFence.StartedAtTicks fencedTicks }
      DaemonOwnership.Ownership.monitoredOwner ownership
      |> Expect.equal "the owner is the pid with its fence" (Some { OwnerMonitor.Owner.Pid = 4242; StartTimeTicks = Some fencedTicks })
    }
    test "an owner given without a start time is watched by pid alone, also when the daemon expires too" {
      let ownership =
        DaemonOwnership.Ownership.OwnedAndExpiring ({ Pid = 7; Fence = DaemonOwnership.OwnerFence.Unfenced }, ttl)
      DaemonOwnership.Ownership.monitoredOwner ownership
      |> Expect.equal "the owner is the pid with no fence" (Some { OwnerMonitor.Owner.Pid = 7; StartTimeTicks = None })
    }
    test "a daemon with only a time limit, or nobody, has no owner to watch" {
      DaemonOwnership.Ownership.monitoredOwner (DaemonOwnership.Ownership.ExpiresAfter (ttl, DaemonOwnership.TtlSource.Explicit))
      |> Expect.isNone "a time limit is not an owner"
      DaemonOwnership.Ownership.monitoredOwner DaemonOwnership.Ownership.Unowned
      |> Expect.isNone "an unowned daemon has no owner"
    }
  ]

// ─── The gates ──────────────────────────────────────────────────────────────

[<Tests>]
let hardKillOutcomeTests =
  Integration.hostList "Hard kill outcome" <| onlyOnLinux [

    testTask "WHY — SIGKILLing the daemon leaves no worker, no FSI host and no listener behind, with a run_app app live in the worker" {
      do! linuxOnly (fun () ->
        withDaemon Owner.TestRunner Supervision.Plain (fun daemon -> task {
          let! sessionId = createSession daemon [| consoleTickerProject |] consoleTickerDir
          use! client = connect daemon.McpPort
          let! _ = readyFacts client sessionId
          let! _ = awaitProjectLoaded client sessionId
          // run_app moves an Interactive session into HotReload first, which respawns the worker:
          // the pids that matter are the ones AFTER it, so they are read after.
          do! startTicker client
          let! facts = readyFacts client sessionId

          let owned, setApart = snapshotOwned daemon.Process.Id
          daemon.Tracked.AddRange owned
          owned
          |> List.exists (fun o -> o.Proc.Pid = facts.WorkerPid)
          |> Expect.isTrue (sprintf "the snapshot holds the worker (pid %d). Owned: %A Build servers set apart: %A" facts.WorkerPid (List.map describe owned) (List.map describe setApart))
          owned
          |> List.exists (fun o -> o.Command.Contains "FsiHost")
          |> Expect.isTrue (sprintf "the snapshot holds the FSI host. Owned: %A" (List.map describe owned))

          let ports = [ daemon.McpPort; daemon.DashboardPort; facts.WorkerPort ]
          reportSnapshot "SIGKILL the daemon" owned setApart ports
          expectPortsListening "the daemon's MCP, its dashboard and the worker's port" ports

          // The kill: SIGKILL gives the daemon no chance to run a line of cleanup.
          daemon.Process.Kill()
          use killedWithin = new CancellationTokenSource(TestTimeouts.childExit)
          do! daemon.Process.WaitForExitAsync killedWithin.Token
          daemon.Process.ExitCode
          |> Expect.equal "the daemon died to the signal, with no cleanup of its own" exitCodeOfSigkill

          let! survivors = awaitGone owned TestTimeouts.orphanReap
          expectNoSurvivors "after the daemon was SIGKILLed" survivors
          expectPortsFree "after the daemon was SIGKILLed" ports
        }))
    }

    testTask "WHY — the daemon exits, and takes its worker and FSI host with it, when the process that owns it dies" {
      do! linuxOnly (fun () -> task {
        // `exec` so the pid we kill is the sleep itself and not a shell that left it behind.
        let psi = ProcessStartInfo("/bin/sh", [ "-c"; "exec sleep 600" ])
        psi.UseShellExecute <- false
        use standIn = Process.Start psi
        try
          do! withDaemon (Owner.StandIn standIn) Supervision.Plain (fun daemon -> task {
            let! sessionId = createSession daemon [||] consoleTickerDir
            use! client = connect daemon.McpPort
            let! facts = readyFacts client sessionId

            let owned, setApart = snapshotOwned daemon.Process.Id
            daemon.Tracked.AddRange owned
            owned
            |> List.exists (fun o -> o.Proc.Pid = facts.WorkerPid)
            |> Expect.isTrue (sprintf "the snapshot holds the worker. Owned: %A" (List.map describe owned))
            let ports = [ daemon.McpPort; daemon.DashboardPort; facts.WorkerPort ]
            reportSnapshot "the owner dies" owned setApart ports
            expectPortsListening "the daemon's MCP, its dashboard and the worker's port" ports

            // The owner dies. Nothing else happens to the daemon: it has to notice by itself.
            standIn.Kill()
            use ownerGone = new CancellationTokenSource(TestTimeouts.childExit)
            do! standIn.WaitForExitAsync ownerGone.Token

            use daemonExit = new CancellationTokenSource(TestTimeouts.ownerDeathDaemonExit)
            try
              do! daemon.Process.WaitForExitAsync daemonExit.Token
            with :? OperationCanceledException -> ()
            daemon.Process.HasExited
            |> Expect.isTrue (sprintf "the daemon exits within %O of its owner dying" TestTimeouts.ownerDeathDaemonExit)

            let! survivors = awaitGone owned TestTimeouts.orphanReap
            expectNoSurvivors "after the daemon's owner died" survivors
            expectPortsFree "after the daemon's owner died" ports
          })
        finally
          try standIn.Kill() with _ -> ()
      })
    }

    testTask "WHY — SIGKILLing the worker alone ends its FSI host, because the pipe between them closed, and the daemon stays up" {
      do! linuxOnly (fun () ->
        withDaemon Owner.TestRunner Supervision.Plain (fun daemon -> task {
          let! survivors = killWorkerAlone daemon HostBeforeKill.Running TestTimeouts.childExitSlow
          expectNoSurvivors "after its worker was SIGKILLed, the FSI host" survivors
          daemon.Process.HasExited
          |> Expect.isFalse "the daemon is still up: only the worker was killed"
        }))
    }

    // The twin that gives the case above its teeth. The product's layer here is "the FSI host exits when the pipe from
    // its worker closes"; breaking it needs a rebuild of the embedded host. This freezes the host (SIGSTOP) so it cannot
    // see the pipe close, which is what a broken exit path looks like from outside, and requires the SAME oracle to
    // report it as a survivor. If the oracle could not see a host that stayed alive, the case above would pass on anything.
    testTask "WHY — the oracle reports an FSI host that does not exit when its worker dies (the twin that gives the case above its teeth)" {
      do! linuxOnly (fun () ->
        withDaemon Owner.TestRunner Supervision.Plain (fun daemon -> task {
          let! survivors = killWorkerAlone daemon HostBeforeKill.Frozen TestTimeouts.survivorWindow
          survivors
          |> List.exists (fun o -> o.Command.Contains "FsiHost")
          |> Expect.isTrue (sprintf "a frozen FSI host is reported as a survivor. Reported: %A" (List.map describe survivors))
        }))
    }

    testTask "WHY — a supervised daemon whose owner dies ends with it, the watchdog included, and is not restarted into an ownerless loop" {
      do! linuxOnly (fun () -> task {
        let psi = ProcessStartInfo("/bin/sh", [ "-c"; "exec sleep 600" ])
        psi.UseShellExecute <- false
        use standIn = Process.Start psi
        try
          do! withDaemon (Owner.StandIn standIn) Supervision.Supervised (fun daemon -> task {
            let owned, setApart = snapshotOwned daemon.Process.Id
            daemon.Tracked.AddRange owned
            owned
            |> List.isEmpty
            |> Expect.isFalse "the watchdog has a daemon below it to lose"
            let ports = [ daemon.McpPort; daemon.DashboardPort ]
            reportSnapshot "the owner of a supervised daemon dies" owned setApart ports

            standIn.Kill()
            use ownerGone = new CancellationTokenSource(TestTimeouts.childExit)
            do! standIn.WaitForExitAsync ownerGone.Token

            // The daemon notices on its owner poll and exits. The watchdog is the part under test: it has to end too,
            // not start another daemon for an owner that is gone.
            use watchdogExit = new CancellationTokenSource(TestTimeouts.ownerDeathDaemonExit)
            try
              do! daemon.Process.WaitForExitAsync watchdogExit.Token
            with :? OperationCanceledException -> ()
            daemon.Process.HasExited
            |> Expect.isTrue (sprintf "the watchdog ends within %O of its daemon's owner dying" TestTimeouts.ownerDeathDaemonExit)

            let! survivors = awaitGone owned TestTimeouts.orphanReap
            expectNoSurvivors "after the owner of a supervised daemon died" survivors
            expectPortsFree "after the owner of a supervised daemon died" ports
          })
        finally
          try standIn.Kill() with _ -> ()
      })
    }

    testTask "WHY — a supervised daemon killed with SIGKILL comes back under its watchdog, a new process answering on the same ports" {
      do! linuxOnly (fun () ->
        withDaemon Owner.TestRunner Supervision.Supervised (fun daemon -> task {
          let! firstPid = answeringPid daemon.DashboardPort
          (firstPid > 0) |> Expect.isTrue "a daemon answers before the kill"
          firstPid |> Expect.notEqual "the daemon is the watchdog's child, not the watchdog" daemon.Process.Id
          let first = ProcessTable.read () |> List.find (fun row -> row.Id.Pid = firstPid) |> ownedOf
          daemon.Tracked.Add first

          (Process.GetProcessById firstPid).Kill()
          let! survivors = awaitGone [ first ] TestTimeouts.childExitSlow
          expectNoSurvivors "after the supervised daemon was SIGKILLed, the daemon" survivors

          // The watchdog does not act inside its grace period, then checks on its interval and
          // waits out the backoff: the bound is those, and the wait ends when a NEW daemon answers.
          let mutable secondPid = -1
          let! restarted =
            Infra.waitForAsync (TestTimeouts.asMs TestTimeouts.supervisedRestart) (fun () -> task {
              let! pid = answeringPid daemon.DashboardPort
              secondPid <- pid
              return pid > 0 && pid <> firstPid && isOurDaemon daemon.Process daemon.Supervision pid })
          restarted
          |> Expect.isTrue (sprintf "the watchdog restarts the daemon within %O (last answering pid %d, killed pid %d)" TestTimeouts.supervisedRestart secondPid firstPid)
          daemon.Process.HasExited
          |> Expect.isFalse "the watchdog itself is still running"
        }))
    }
  ]
