// For more information see https://aka.ms/fsharp-console-apps
open System
open System.IO
open System.Text
open System.Reflection
open SageFs.Server

/// Wraps a TextWriter to normalize lone LF to CRLF.
/// PrettyPrompt sets DISABLE_NEWLINE_AUTO_RETURN on the console,
/// which means \n alone won't carriage-return. This wrapper ensures
/// all output uses \r\n.
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
    set ([ "--no-web"; "--no-mcp"; "--mcp-port"; "--help"; "-h";
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

[<EntryPoint>]
let main args =
  // Wrap Console.Out to normalize \n to \r\n.
  // PrettyPrompt sets DISABLE_NEWLINE_AUTO_RETURN on Windows console,
  // which causes staircase output when \n is written without \r.
  Console.SetOut(new NewlineNormalizingWriter(Console.Out))

  // Check for --help or -h flag
  if args |> Array.exists (fun arg -> arg = "--help" || arg = "-h") then
    printfn "SageFs - Enhanced F# Interactive with hot reloading and MCP support"
    printfn ""
    printfn "Usage: SageFs [options] [fsi-args]"
    printfn "       SageFs -d|--daemon [options]    Start as background daemon"
    printfn "       SageFs worker [options]          Internal: worker process"
    printfn "       SageFs stop                      Stop running daemon"
    printfn "       SageFs status                    Show daemon info"
    printfn ""
    printfn "Options:"
    printfn "  --version, -v          Show version information"
    printfn "  --help, -h             Show this help message"
    printfn "  --no-web               Disable ASP.NET features (default: enabled)"
    printfn "  --no-mcp               Disable MCP server (default: enabled on port 37749)"
    printfn "  --mcp-port PORT        Set custom MCP server port (default: 37749)"
    printfn "  --bare                 Start a bare FSI session — no project/solution loading, fast startup"
    printfn ""
    printfn "MCP Server:"
    printfn "  When enabled, SageFs starts an MCP (Model Context Protocol) server for"
    printfn "  AI agent integration. The server provides these endpoints:"
    printfn "    /sse     - SSE transport endpoint (connect here with MCP clients)"
    printfn "    /message - Internal message endpoint (used by MCP protocol)"
    printfn ""
    printfn "  For GitHub Copilot CLI, use: http://localhost:37749/sse"
    printfn ""
    printfn "  Available MCP tools:"
    printfn "    - send_fsharp_code: Execute F# code in the REPL"
    printfn "    - load_fsharp_script: Load and execute .fsx files"
    printfn "    - get_recent_fsi_events: View recent REPL activity"
    printfn "    - get_fsi_status: Get session information"
    printfn ""
    printfn "Examples:"
    printfn "  SageFs                              Start interactive REPL"
    printfn "  SageFs -d                           Start as daemon"
    printfn "  SageFs --proj MyProject.fsproj      Load project and start REPL"
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
  // Subcommand: -d / --daemon
  elif args |> Array.exists (fun a -> a = "-d" || a = "--daemon") then
    let mcpPort = parseMcpPort args
    let filteredArgs = filterArgs args ["-d"; "--daemon"]
    let parsedArgs =
      try SageFs.Args.parser.ParseCommandLine(filteredArgs).GetAllResults()
      with _ -> []
    DaemonMode.run mcpPort parsedArgs
    |> _.GetAwaiter() |> _.GetResult()
    0
  else
    // Default: interactive REPL (existing behavior)
    let disableWeb = args |> Array.exists (fun arg -> arg = "--no-web")
    let useAsp = not disableWeb
    let mcpPort = parseMcpPort args |> Some
    let mcpPort =
      if args |> Array.exists (fun arg -> arg = "--no-mcp") then None
      else mcpPort
    let filteredArgs = filterArgs args []
    CliEventLoop.runCliEventLoop useAsp mcpPort filteredArgs ()
    |> _.GetAwaiter() |> _.GetResult()
    0
