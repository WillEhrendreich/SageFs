// For more information see https://aka.ms/fsharp-console-apps
open System
open System.IO
open System.Text
open System.Reflection
open SageFs
open SageFs.Server

/// Wraps a TextWriter to normalize lone LF to CRLF.
/// Some console modes on Windows cause \n alone to not carriage-return.
/// This wrapper ensures all output uses \r\n.
type NewlineNormalizingWriter(inner: TextWriter) =
  inherit TextWriter()
  let mutable lastCharWasCR = false
  override _.Encoding = inner.Encoding
  override _.FormatProvider = inner.FormatProvider
  override _.NewLine
    with get () = inner.NewLine
    and set v = inner.NewLine <- v
  override _.Write(value: char) =
    match value with
    | '\n' ->
      match lastCharWasCR with
      | false -> inner.Write '\r'
      | true -> ()
      inner.Write '\n'
      lastCharWasCR <- false
    | '\r' ->
      lastCharWasCR <- true
      inner.Write value
    | _ ->
      lastCharWasCR <- false
      inner.Write value
  override _.Write(value: string) =
    match isNull value with
    | true -> ()
    | false ->
      let normalized = value.Replace("\r\n", "\n").Replace("\n", "\r\n")
      inner.Write normalized
  override _.Write(buffer: char[], index: int, count: int) =
    let s = new string(buffer, index, count)
    let normalized = s.Replace("\r\n", "\n").Replace("\n", "\r\n")
    inner.Write normalized
  override _.Flush() = inner.Flush()
  override _.FlushAsync() = inner.FlushAsync()

/// Parse --mcp-port from args, falling back to env var or default SageFsConfig.DefaultMcpPort.
let parseMcpPort (args: string array) =
  let mcpPortIndex = args |> Array.tryFindIndex (fun a -> a = "--mcp-port")
  let defaultPort = SageFsConfig.McpPortFromEnv
  match mcpPortIndex with
  | Some i when i + 1 < args.Length ->
    match Int32.TryParse(args.[i + 1]) with
    | true, p -> p
    | _ -> defaultPort
  | _ -> defaultPort

let explicitDaemonInvocationUsesAlternatePort (args: string array) =
  let requestedPort = parseMcpPort args
  let defaultPort = SageFsConfig.McpPortFromEnv
  requestedPort <> defaultPort

let deprecatedClientMessage name =
  sprintf "The '%s' client is deprecated and no longer shipped. Use http://localhost:37750/dashboard." name

let waitForDaemonReady
  (sleep: int -> unit)
  (readOnPort: int -> DaemonInfo option)
  (mcpPort: int)
  =
  let mutable attempts = 0
  let mutable info = None
  while attempts < 30 && Option.isNone info do
    sleep 500
    info <- readOnPort mcpPort
    attempts <- attempts + 1
  match info with
  | Some daemon -> Ok daemon
  | None -> Error (SageFsError.DaemonStartFailed "Daemon started but did not become ready in 15s")


/// CLI command parsed from arguments — replaces if/elif chain with pattern matching.
type CliCommand =
  | ShowHelp
  | ShowVersion
  | Stop
  | Status
  | Check
  | Sweep of kill: bool
  | DeprecatedClient of name: string
  | Daemon of args: string array
  | Jupyter of connectionFile: string

module CliCommand =
  let parse (args: string array) =
    let hasFlag flag = args |> Array.exists (fun a -> a = flag)
    match () with
    | _ when hasFlag "--help" || hasFlag "-h" -> ShowHelp
    | _ when hasFlag "--version" || hasFlag "-v" -> ShowVersion
    | _ when args.Length > 0 && args.[0] = "stop" -> Stop
    | _ when args.Length > 0 && args.[0] = "status" -> Status
    | _ when args.Length > 0 && args.[0] = "check" -> Check
    | _ when args.Length > 0 && args.[0] = "sweep" -> Sweep (hasFlag "--kill")
    | _ when args.Length > 0 && args.[0] = "tui" -> DeprecatedClient "tui"
    | _ when args.Length > 0 && args.[0] = "gui" -> DeprecatedClient "gui"
    | _ when hasFlag "--jupyter" ->
      let idx = args |> Array.findIndex (fun a -> a = "--jupyter")
      match idx + 1 < args.Length with
      | true -> Jupyter args.[idx + 1]
      | false -> ShowHelp
    | _ -> Daemon args

/// Ownership rule 2 (multi-agent vision §3.1/§10 item 2): resolve
/// `--owner-pid`/`--owner-start`/`--ttl` off the raw args, applying the
/// nested-checkout default (a daemon started inside another checkout, with
/// neither flag, defaults to a 30-minute TTL rather than running forever
/// unowned) and logging loudly when that default kicks in.
let resolveOwnership (args: string array) (cwd: string) : DaemonOwnership.EffectiveOwnership =
  let parsed = DaemonOwnership.OwnershipArgs.parse (Array.toList args)
  let nested = DaemonOwnership.isNestedCheckout SageFs.FileWatcher.hasCheckoutMarker cwd
  let effective = DaemonOwnership.applyNestedCheckoutDefault nested parsed
  match effective.DefaultedTtl with
  | true ->
    eprintfn "sagefs: started inside a nested checkout (%s) with no --owner-pid or --ttl — defaulting to --ttl %s so it self-terminates if abandoned"
      cwd (string DaemonOwnership.defaultTtlForNestedCheckout)
  | false -> ()
  effective

/// Run daemon mode (default behavior).
let runDaemon (args: string array) =
  // Fail fast on a non-loopback SAGEFS_BIND_HOST before anything binds. The
  // supervised watchdog's child daemon runs this check again.
  match SageFsConfig.BindHost with
  | Error message ->
    eprintfn "%s" message
    2
  | Ok bindHost ->
  let mcpPort = parseMcpPort args
  let flags = Args.DaemonFlags.parse (Array.toList args)
  let ownership = resolveOwnership args Environment.CurrentDirectory
  let isSupervised = args |> Array.exists (fun a -> a = "--supervised")
  match isSupervised with
  | true ->
    let daemonArgs =
      args
      |> Array.filter (fun a -> a <> "--supervised")
      |> Array.toList
    use cts = new System.Threading.CancellationTokenSource()
    Console.CancelKeyPress.Add(fun e ->
      e.Cancel <- true
      cts.Cancel())
    WatchdogRunner.run
      SageFs.Watchdog.defaultConfig
      daemonArgs
      Environment.CurrentDirectory
      cts.Token
    |> _.GetAwaiter() |> _.GetResult()
    0
  | false ->
    DaemonMode.run bindHost mcpPort flags ownership
    |> _.GetAwaiter() |> _.GetResult()
    0

/// `sagefs sweep [--kill]` — reap daemons whose recorded owner is gone.
/// Read-only by default (report only); `--kill` also terminates the
/// process and removes its stale daemon-info/registry entry. Never matches
/// by process name or PPID — only by the owner (pid, startTime) recorded
/// in the daemon's own info file.
let sweepCommand (kill: bool) =
  let isOwnerAlive (pid: int) (startTicks: int64 option) =
    OwnerMonitor.isAlive OwnerMonitor.getProcessById { Pid = pid; StartTimeTicks = startTicks }
  let realDir = DaemonOwnership.realHomeSageFsDir ()
  let results = DaemonOwnership.sweep isOwnerAlive realDir
  match results with
  | [] ->
    printfn "sweep: no known daemons under %s" realDir
    0
  | _ ->
    let mutable reaped = 0
    for (registryPath, info, verdict) in results do
      match verdict with
      | DaemonOwnership.SweepVerdict.Leave reason ->
        printfn "sweep: leave  pid=%d (%s)" info.Pid reason
      | DaemonOwnership.SweepVerdict.Reap reason ->
        reaped <- reaped + 1
        match kill with
        | false ->
          printfn "sweep: reapable pid=%d (%s) — re-run with --kill to reap it" info.Pid reason
        | true ->
          let killOutcome =
            try
              let proc = System.Diagnostics.Process.GetProcessById(info.Pid)
              match proc.HasExited with
              | true -> "already exited"
              | false ->
                proc.Kill()
                "killed"
            with ex -> sprintf "could not kill: %s" ex.Message
          DaemonOwnership.DaemonInfoFile.delete info.DataDir
          try
            match String.Equals(Path.GetFullPath info.DataDir, Path.GetFullPath realDir, StringComparison.OrdinalIgnoreCase) with
            | true -> ()
            | false -> DaemonOwnership.unregisterSpawned realDir info.Pid
          with _ -> ()
          // The registry entry might be the primary daemon-info.json itself
          // (already handled by `delete info.DataDir` above) or a spawned
          // registration file — remove whichever path this verdict came from.
          try
            match File.Exists registryPath with
            | true -> File.Delete registryPath
            | false -> ()
          with _ -> ()
          printfn "sweep: reaped pid=%d (%s, %s)" info.Pid reason killOutcome
    match kill, reaped with
    | false, 0 -> printfn "sweep: nothing reapable"
    | _ -> ()
    0

type DaemonLaunchDecision =
  | AttachToExistingDaemon of DaemonInfo
  | StartNewDaemon

let decideDaemonLaunch
  (readOnPort: int -> DaemonInfo option)
  (mcpPort: int)
  =
  match readOnPort mcpPort with
  | Some info -> AttachToExistingDaemon info
  | None -> StartNewDaemon

/// Result of the fallback force-kill performed when graceful shutdown fails.
type StopKillResult =
  /// Process was found and terminated.
  | StopKilled
  /// Process is gone (stale PID from a dead daemon's state file).
  | StopProcessGone of message: string
  /// Process exists but could not be killed for another reason.
  | StopKillFailed of message: string

/// The `sagefs stop` command with every daemon interaction injected so exit
/// codes are testable without touching a real daemon:
///   readOnPort     - locate the daemon state (stale-pid / no-daemon cases)
///   requestShutdown - graceful HTTP shutdown request
///   killProcess    - fallback force-kill of the recorded PID
///   waitForExit    - whether the daemon's process actually exited after the request
/// A stop that did nothing is NOT success: "No daemon running" and
/// "Daemon was not running (stale PID N)" both exit NON-zero so automation can
/// tell a successful stop from a no-op.
/// Whether the daemon's process exited after a graceful shutdown request.
[<RequireQualifiedAccess>]
type StopWait =
  | Exited
  | StillRunning

let stopCommand
  (readOnPort: int -> DaemonInfo option)
  (requestShutdown: int -> bool)
  (killProcess: int -> StopKillResult)
  (waitForExit: int -> StopWait)
  (mcpPort: int)
  =
  match readOnPort mcpPort with
  | Some info ->
    let forceKill () =
      match killProcess info.Pid with
      | StopKilled ->
        printfn "Daemon stopped (PID %d)" info.Pid
        0
      | StopProcessGone message ->
        eprintfn "Stop daemon error for PID %d: %s" info.Pid message
        printfn "Daemon was not running (stale PID %d)" info.Pid
        1
      | StopKillFailed message ->
        eprintfn "Stop daemon error for PID %d: %s" info.Pid message
        printfn "Daemon was not running (stale PID %d)" info.Pid
        1
    match requestShutdown mcpPort with
    | true ->
      // Accepting the request is not stopping: report success once the process is gone.
      match waitForExit info.Pid with
      | StopWait.Exited ->
        printfn "Daemon stopped (PID %d)" info.Pid
        0
      | StopWait.StillRunning ->
        eprintfn "Daemon PID %d did not exit after the shutdown request; killing it" info.Pid
        forceKill ()
    | false -> forceKill ()
  | None ->
    printfn "No daemon running"
    1

let private stopKillProcess (pid: int) =
  try
    let proc = System.Diagnostics.Process.GetProcessById(pid)
    if proc.HasExited then
      StopProcessGone (sprintf "process %d has already exited" pid)
    else
      proc.Kill()
      proc.WaitForExit(3000) |> ignore
      StopKilled
  with ex ->
    StopProcessGone ex.Message

/// Waits for the daemon's process to exit after a shutdown request.
let private stopWaitForExit (pid: int) : StopWait =
  try
    use proc = System.Diagnostics.Process.GetProcessById(pid)
    // Room for a normal graceful shutdown (manifest save, stopping workers) before the fallback kill.
    match proc.WaitForExit(30_000) with
    | true -> StopWait.Exited
    | false -> StopWait.StillRunning
  with
  | :? ArgumentException -> StopWait.Exited
  | :? InvalidOperationException -> StopWait.Exited

[<EntryPoint>]
let main args =
  // Wrap Console.Out to normalize \n to \r\n on Windows console.
  Console.SetOut(new NewlineNormalizingWriter(Console.Out))

  match CliCommand.parse args with
  | ShowHelp ->
    printfn "SageFs - F# Interactive daemon with MCP, hot reloading, and live dashboard"
    printfn ""
    printfn "Usage: SageFs [options]                Start daemon (default mode)"
    printfn "       SageFs check                    Check environment before first run"
    printfn "       SageFs --supervised [options]   Start with watchdog auto-restart"
    printfn "       SageFs --jupyter <conn.json>    Run as Jupyter kernel"
    printfn "       SageFs stop                     Stop running daemon"
    printfn "       SageFs status                   Show daemon info"
    printfn "       SageFs sweep [--kill]           Reap daemons whose owner process is gone"
    printfn ""
    printfn "Options:"
    printfn "  --version, -v          Show version information"
    printfn "  --help, -h             Show this help message"
    printfn "  --mcp-port PORT        Set custom MCP server port (default: 37749)"
    printfn "  --jupyter FILE         Run as Jupyter kernel with given connection file"
    printfn "  --supervised           Run under watchdog supervisor (auto-restart on crash)"
    printfn "  --no-watch             Disable file watching — no automatic #load on changes"
    printfn "  --no-resume            Skip restoring previous sessions on daemon startup"
    printfn "  --prune                Mark all stale sessions as stopped and exit"
    printfn "  --owner-pid PID        Exit when the process at PID exits (fenced by --owner-start"
    printfn "                         when given). For daemons an agent, test, or demo spawns."
    printfn "  --owner-start TICKS    UTC ticks of the owner's own start time, pairs with"
    printfn "                         --owner-pid to close the pid-reuse race."
    printfn "  --ttl DURATION         Self-terminate after DURATION (e.g. 30m, 1h, 90s) with no"
    printfn "                         live sessions and no MCP/SSE clients."
    printfn ""
    printfn "Environment Variables:"
    printfn "  SageFs_MCP_PORT           Override MCP server port (same as --mcp-port)"
    printfn "  SAGEFS_BIND_HOST          Loopback bind address: localhost (default), 127.0.0.1 or ::1."
    printfn "                            Non-loopback addresses are refused — SageFs has no authentication."
    printfn ""
    printfn "Daemon:"
    printfn "  SageFs runs as a daemon by default. The daemon provides:"
    printfn "    MCP server      http://localhost:37749/     (Streamable HTTP)"
    printfn "                    http://localhost:37749/sse (SSE for older clients)"
    printfn "    Dashboard       http://localhost:37750/dashboard  (live web UI)"
    printfn "    File watcher    Auto-reload .fs/.fsx changes via #load"
    printfn "    Hot reload      Runtime function redefinition"
    printfn ""
    printfn "  The dashboard, MCP agents, and editor integrations are clients of the daemon."
    printfn "  If a daemon is already running, `sagefs` reports its dashboard URL."
    printfn ""
    printfn "Quick Start:"
    printfn "  1. sagefs                            Start the bare daemon"
    printfn "  2. Open your editor (VS Code, Neovim, Visual Studio)"
    printfn "  3. Create a session for your project from the editor, MCP client, or dashboard"
    printfn "  4. Edit an F# file and save — live test results appear automatically"
    printfn "  Or visit http://localhost:37750/dashboard in your browser."
    printfn ""
    printfn "Examples:"
    printfn "  SageFs                              Start the bare daemon"
    printfn "  SageFs --mcp-port 47700             Start daemon on custom port"
    printfn "  SageFs --supervised                 Start with auto-restart"
    printfn "  SageFs --jupyter conn.json          Run as Jupyter kernel"
    printfn "  SageFs status                       Show daemon status"
    printfn "  SageFs check                        Check environment before first run"
    printfn ""
    0

  | ShowVersion ->
    let assembly = Assembly.GetExecutingAssembly()
    let version = assembly.GetName().Version
    printfn $"SageFs version %A{version}"
    0

  | Stop ->
    let mcpPort = parseMcpPort args
    stopCommand DaemonState.readOnPort DaemonState.requestShutdown stopKillProcess stopWaitForExit mcpPort

  | Status ->
    let mcpPort = parseMcpPort args
    match DaemonState.readOnPort mcpPort with
    | Some info ->
      printfn "SageFs daemon running"
      printfn "  PID:        %d" info.Pid
      printfn "  Port:       %d" info.Port
      printfn "  Started:    %s" (info.StartedAt.ToString("o"))
      printfn "  Directory:  %s" info.WorkingDirectory
      printfn "  Version:    %s" info.Version
      printfn "  Dashboard:  http://localhost:%d/dashboard" info.DashboardPort
      printfn "  MCP (SSE):  http://localhost:%d/sse" info.Port
      try
        use client = new System.Net.Http.HttpClient(Timeout = TimeSpan.FromSeconds(3.0))
        let resp = client.GetAsync(sprintf "http://localhost:%d/api/sessions" info.Port).Result
        match resp.IsSuccessStatusCode with
        | true ->
          let json = resp.Content.ReadAsStringAsync().Result
          let doc = System.Text.Json.JsonDocument.Parse(json)
          let sessions = doc.RootElement.GetProperty("sessions")
          let count = sessions.GetArrayLength()
          printfn "  Sessions:   %d active" count
        | false -> ()
      with _ -> ()
      0
    | None ->
      printfn "No daemon running"
      1

  | Check ->
    let mcpPort  = parseMcpPort args
    let dashPort = mcpPort + 1
    let dir      = Environment.CurrentDirectory
    let results  = EnvCheck.runAll dir mcpPort dashPort
    let failures = EnvCheck.print results
    match failures with
    | 0 -> 0
    | _ -> 1

  | Sweep kill ->
    sweepCommand kill

  | DeprecatedClient name ->
    eprintfn "%s" (deprecatedClientMessage name)
    2

  | Jupyter connectionFile ->
    match File.Exists connectionFile with
    | false ->
      eprintfn "Connection file not found: %s" connectionFile
      1
    | true ->
      let json = File.ReadAllText connectionFile
      match JupyterKernel.ConnectionInfo.parse json with
      | Error msg ->
        eprintfn "Invalid connection file: %s" msg
        1
      | Ok connInfo ->
        let mcpPort = parseMcpPort args
        match DaemonState.readOnPort mcpPort with
        | None ->
          eprintfn "SageFs daemon is not running on port %d." mcpPort
          eprintfn "Start it first with 'sagefs' (or 'sagefs --mcp-port %d' if you passed a custom port), then reconnect the Jupyter kernel." mcpPort
          1
        | Some _ ->
          printfn "SageFs Jupyter kernel starting (transport=%s, ip=%s)" connInfo.Transport connInfo.Ip
          printfn "  Shell:   %d" connInfo.ShellPort
          printfn "  IOPub:   %d" connInfo.IoPubPort
          printfn "  Stdin:   %d" connInfo.StdinPort
          printfn "  Control: %d" connInfo.ControlPort
          printfn "  HB:      %d" connInfo.HbPort
          printfn "  Daemon:  http://localhost:%d (working directory %s)" mcpPort Environment.CurrentDirectory

          // Route EvalCode to the running daemon over the same /exec
          // contract every editor integration already uses
          // (McpServer.fs's mapExecutionRoutes). See JupyterDaemonBridge
          // for session selection and the no-daemon/no-session error paths.
          let httpClient =
            new System.Net.Http.HttpClient(
              BaseAddress = Uri(sprintf "http://localhost:%d" mcpPort),
              Timeout = Timeouts.workerHttpRequest)
          let proxy =
            JupyterDaemonBridge.makeSessionProxy
              (JupyterDaemonBridge.httpPostJson httpClient "/exec")
              (JupyterDaemonBridge.httpPostJson httpClient "/api/sessions/create")
              Environment.CurrentDirectory
          let exec, complete, isComplete = JupyterKernel.FsiBridge.fromProxy proxy

          use cts = new System.Threading.CancellationTokenSource()
          Console.CancelKeyPress.Add(fun e ->
            e.Cancel <- true
            cts.Cancel())
          printfn "Kernel running. Press Ctrl+C to stop."
          JupyterTransport.run connInfo exec complete isComplete cts.Token
          0

  | Daemon _ ->
    let mcpPort = parseMcpPort args
    let forceDedicatedDaemon = explicitDaemonInvocationUsesAlternatePort args
    match forceDedicatedDaemon, decideDaemonLaunch DaemonState.readOnPort mcpPort with
    | true, _ ->
      runDaemon args
    | false, AttachToExistingDaemon info ->
      printfn "SageFs daemon already running (PID %d, port %d)." info.Pid info.Port
      printfn "Dashboard: http://localhost:%d/dashboard" info.DashboardPort
      0
    | false, StartNewDaemon ->
      runDaemon args
