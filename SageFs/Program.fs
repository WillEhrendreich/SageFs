// For more information see https://aka.ms/fsharp-console-apps
open System
open System.IO
open System.Text
open System.Reflection
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
    if value = '\n' then
      if not lastCharWasCR then
        inner.Write '\r'
      inner.Write '\n'
      lastCharWasCR <- false
    else
      lastCharWasCR <- (value = '\r')
      inner.Write value
  override _.Write(value: string) =
    if not (isNull value) then
      let normalized = value.Replace("\r\n", "\n").Replace("\n", "\r\n")
      inner.Write normalized
  override _.Write(buffer: char[], index: int, count: int) =
    let s = new string(buffer, index, count)
    let normalized = s.Replace("\r\n", "\n").Replace("\n", "\r\n")
    inner.Write normalized
  override _.Flush() = inner.Flush()
  override _.FlushAsync() = inner.FlushAsync()

/// Parse --mcp-port from args, falling back to env var or default 37749.
let parseMcpPort (args: string array) =
  let mcpPortIndex = args |> Array.tryFindIndex (fun a -> a = "--mcp-port")
  let defaultPort =
    match Environment.GetEnvironmentVariable("SageFs_MCP_PORT") with
    | null | "" -> 37749
    | portStr ->
      match Int32.TryParse(portStr) with
      | true, p -> p
      | _ -> 37749
  match mcpPortIndex with
  | Some i when i + 1 < args.Length ->
    match Int32.TryParse(args.[i + 1]) with
    | true, p -> p
    | _ -> defaultPort
  | _ -> defaultPort

/// Filter out SageFs-specific flags, keep FSI-passthrough args.
let filterArgs (args: string array) (extraFlags: string list) =
  let SageFsFlags =
    set ([ "--mcp-port"; "--help"; "-h";
           "--version"; "-v" ] @ extraFlags)
  let mcpPortIndex = args |> Array.tryFindIndex (fun a -> a = "--mcp-port")
  let ionideFlags =
    args |> Array.filter (fun a -> a.StartsWith("--fsi-server-"))
  let regularArgs =
    args
    |> Array.filter (fun a ->
      not (SageFsFlags.Contains(a))
      && not (Array.contains a ionideFlags))
    |> Array.filter (fun a ->
      match mcpPortIndex with
      | Some i when i + 1 < args.Length && a = args.[i + 1] -> false
      | _ -> true)
  if ionideFlags.Length > 0 then
    Array.concat [regularArgs; [|"--other"|]; ionideFlags]
  else
    regularArgs

/// Run daemon mode (default behavior).
let runDaemon (args: string array) (extraFlags: string list) =
  let mcpPort = parseMcpPort args
  let filteredArgs = filterArgs args extraFlags
  let parsedArgs =
    try SageFs.Args.parser.ParseCommandLine(filteredArgs).GetAllResults()
    with _ -> []
  if args |> Array.exists (fun a -> a = "--supervised") then
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
  else
    DaemonMode.run mcpPort parsedArgs
    |> _.GetAwaiter() |> _.GetResult()
    0

[<EntryPoint>]
let main args =
  // Wrap Console.Out to normalize \n to \r\n on Windows console.
  Console.SetOut(new NewlineNormalizingWriter(Console.Out))

  // Check for --help or -h flag
  if args |> Array.exists (fun arg -> arg = "--help" || arg = "-h") then
    printfn "SageFs - F# Interactive daemon with MCP, hot reloading, and live dashboard"
    printfn ""
    printfn "Usage: SageFs [options]                Start daemon (default mode)"
    printfn "       SageFs --supervised [options]   Start with watchdog auto-restart"
    printfn "       SageFs connect                  Connect to running daemon"
    printfn "       SageFs stop                     Stop running daemon"
    printfn "       SageFs status                   Show daemon info"
    printfn "       SageFs worker [options]         Internal: worker process"
    printfn ""
    printfn "Options:"
    printfn "  --version, -v          Show version information"
    printfn "  --help, -h             Show this help message"
    printfn "  --mcp-port PORT        Set custom MCP server port (default: 37749)"
    printfn "  --bare                 Start a bare FSI session — no project/solution loading"
    printfn "  --supervised           Run under watchdog supervisor (auto-restart on crash)"
    printfn ""
    printfn "Daemon:"
    printfn "  SageFs runs as a daemon by default. The daemon provides:"
    printfn "    MCP server      http://localhost:37749/sse  (AI agent integration)"
    printfn "    Dashboard       http://localhost:37750/dashboard  (live web UI)"
    printfn "    File watcher    Auto-reload .fs/.fsx changes via #load"
    printfn "    Hot reload      Runtime function redefinition"
    printfn ""
    printfn "  All frontends (terminal, browser, MCP, Neovim) are clients of the daemon."
    printfn ""
    printfn "Examples:"
    printfn "  SageFs                              Start daemon"
    printfn "  SageFs --proj MyProject.fsproj      Start daemon with project"
    printfn "  SageFs --supervised                 Start with auto-restart"
    printfn "  SageFs connect                      Connect REPL to running daemon"
    printfn "  SageFs status                       Show daemon status"
    printfn ""
    0
  // Check for --version or -v flag
  elif args |> Array.exists (fun arg -> arg = "--version" || arg = "-v") then
    let assembly = Assembly.GetExecutingAssembly()
    let version = assembly.GetName().Version
    printfn $"SageFs version {version}"
    0
  // Subcommand: stop
  elif args.Length > 0 && args.[0] = "stop" then
    match DaemonState.read () with
    | Some info ->
      try
        let proc = System.Diagnostics.Process.GetProcessById(info.Pid)
        proc.Kill()
        proc.WaitForExit(3000) |> ignore
        DaemonState.clear ()
        printfn "Daemon stopped (PID %d)" info.Pid
        0
      with ex ->
        DaemonState.clear ()
        printfn "Daemon was not running (stale PID %d)" info.Pid
        0
    | None ->
      printfn "No daemon running"
      0
  // Subcommand: status
  elif args.Length > 0 && args.[0] = "status" then
    match DaemonState.read () with
    | Some info ->
      printfn "SageFs daemon running"
      printfn "  PID:        %d" info.Pid
      printfn "  Port:       %d" info.Port
      printfn "  Started:    %s" (info.StartedAt.ToString("o"))
      printfn "  Directory:  %s" info.WorkingDirectory
      printfn "  Version:    %s" info.Version
      0
    | None ->
      printfn "No daemon running"
      1
  // Subcommand: worker (internal)
  elif args.Length > 0 && args.[0] = "worker" then
    let workerArgs = args.[1..] |> Array.toList
    let sessionId =
      workerArgs
      |> List.tryFindIndex (fun a -> a = "--session-id")
      |> Option.bind (fun i ->
        if i + 1 < workerArgs.Length then Some workerArgs.[i + 1] else None)
      |> Option.defaultValue (System.Guid.NewGuid().ToString("N").[..7])
    let pipeName =
      workerArgs
      |> List.tryFindIndex (fun a -> a = "--pipe-name")
      |> Option.bind (fun i ->
        if i + 1 < workerArgs.Length then Some workerArgs.[i + 1] else None)
      |> Option.defaultWith (fun () -> SageFs.NamedPipeTransport.pipeName sessionId)
    // Filter out worker-specific flags, pass rest to Argu
    let SageFsArgs =
      workerArgs
      |> List.filter (fun a ->
        a <> "--session-id" && a <> "--pipe-name"
        && not (workerArgs
                |> List.pairwise
                |> List.exists (fun (prev, cur) ->
                  cur = a && (prev = "--session-id" || prev = "--pipe-name"))))
    let parsedArgs =
      try SageFs.Args.parser.ParseCommandLine(SageFsArgs |> List.toArray)
            .GetAllResults()
      with _ -> []
    WorkerMain.run sessionId pipeName parsedArgs
    |> Async.RunSynchronously
    0
  // Subcommand: connect (connects to running daemon)
  elif args.Length > 0 && args.[0] = "connect" then
    match DaemonState.read () with
    | Some info ->
      ClientMode.run info
      |> _.GetAwaiter() |> _.GetResult()
    | None ->
      printfn "No SageFs daemon running. Starting one..."
      let daemonArgs =
        args.[1..]
        |> Array.filter (fun a -> a <> "connect")
        |> String.concat " "
      match ClientMode.startDaemonInBackground daemonArgs with
      | Ok () ->
        match DaemonState.read () with
        | Some info ->
          ClientMode.run info
          |> _.GetAwaiter() |> _.GetResult()
        | None ->
          printfn "Daemon started but connection failed."
          1
      | Error err ->
        printfn "Failed to start daemon: %A" err
        1
  // Subcommand: tui (terminal UI client for running daemon)
  elif args.Length > 0 && args.[0] = "tui" then
    match DaemonState.read () with
    | Some info ->
      TuiClient.run info
      |> _.GetAwaiter() |> _.GetResult()
    | None ->
      printfn "No SageFs daemon running. Starting one..."
      let daemonArgs =
        args.[1..]
        |> Array.filter (fun a -> a <> "tui")
        |> String.concat " "
      match ClientMode.startDaemonInBackground daemonArgs with
      | Ok () ->
        match DaemonState.read () with
        | Some info ->
          TuiClient.run info
          |> _.GetAwaiter() |> _.GetResult()
        | None ->
          printfn "Daemon started but connection failed."
          1
      | Error err ->
        printfn "Failed to start daemon: %A" err
        1
  // Subcommand: gui (launch Raylib GUI client for running daemon)
  elif args.Length > 0 && args.[0] = "gui" then
    let launchGui () =
      let guiExe =
        let exeDir = System.IO.Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location)
        let candidate = System.IO.Path.Combine(exeDir, "SageFs.Gui.exe")
        if System.IO.File.Exists(candidate) then Some candidate
        else
          let candidate2 = System.IO.Path.Combine(exeDir, "SageFs.Gui")
          if System.IO.File.Exists(candidate2) then Some candidate2
          else None
      match guiExe with
      | Some path ->
        let proc = System.Diagnostics.Process.Start(path)
        proc.WaitForExit()
        proc.ExitCode
      | None ->
        printfn "SageFs.Gui executable not found. Build it with: dotnet build SageFs.Gui"
        printfn "Then run: dotnet run --project SageFs.Gui"
        1
    match DaemonState.read () with
    | Some _ -> launchGui ()
    | None ->
      printfn "No SageFs daemon running. Starting one..."
      let daemonArgs =
        args.[1..]
        |> Array.filter (fun a -> a <> "gui")
        |> String.concat " "
      match ClientMode.startDaemonInBackground daemonArgs with
      | Ok () -> launchGui ()
      | Error err ->
        printfn "Failed to start daemon: %A" err
        1
  // Default: daemon mode (or TUI if daemon already running)
  else
    match DaemonState.read () with
    | Some info ->
      printfn "SageFs daemon already running (PID %d, port %d). Launching TUI..." info.Pid info.Port
      TuiClient.run info
      |> _.GetAwaiter() |> _.GetResult()
    | None ->
      runDaemon args []
