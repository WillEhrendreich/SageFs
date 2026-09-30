namespace SageFs

open System
open System.IO
open System.Text.Json

type DaemonInfo = {
  Pid: int
  Port: int
  DashboardPort: int
  StartedAt: DateTime
  WorkingDirectory: string
  Version: string
  ApiVersion: int option
  SessionCount: int option
  // Mirrors DaemonInfoContract.ComponentFailures (the field `/api/daemon-info`
  // actually sends): non-empty means a component the daemon depends on
  // (most commonly the MCP server itself) failed, even though this probe
  // succeeded — see that type's doc comment for why the split matters.
  // Defaults to `[]` for daemons predating this field.
  ComponentFailures: string list
}

/// The three states a port can be in, not two. An HTTP probe that gets no
/// answer used to mean "no daemon running" unconditionally — but a daemon
/// whose process is alive and holding the port, just not answering (stuck
/// warmup, a deadlocked request pipeline, whatever), reads identically over
/// HTTP to nobody being there at all. `sagefs stop` then printed "No daemon
/// running" while the old process kept the port, and starting a fresh
/// daemon failed to bind right after — the incident this type exists to
/// stop reading as a mystery. `Wedged` carries the pid because that pid is
/// the recovery: it is what `sagefs stop` kills directly once it can no
/// longer expect a graceful HTTP shutdown to work.
[<RequireQualifiedAccess>]
type DaemonPresence =
  | NotRunning
  | Running of DaemonInfo
  | Wedged of pid: int

module DaemonPresence =
  /// A successful HTTP probe always wins. Otherwise, a locally-recorded pid
  /// for this exact port (see `DaemonOwnership.DaemonInfoFile`) — valid only
  /// while that process is still alive — means "wedged", never "not
  /// running". No local pid at all means nobody is here.
  let classify (httpProbe: DaemonInfo option) (wedgedPid: int option) : DaemonPresence =
    match httpProbe, wedgedPid with
    | Some info, _ -> DaemonPresence.Running info
    | None, Some pid -> DaemonPresence.Wedged pid
    | None, None -> DaemonPresence.NotRunning

  let describe =
    function
    | DaemonPresence.NotRunning -> "no daemon running"
    | DaemonPresence.Running info -> sprintf "daemon running (PID %d, port %d)" info.Pid info.Port
    | DaemonPresence.Wedged pid -> sprintf "daemon process %d is holding the port but not answering — it is wedged" pid

/// Whose state a daemon owns: the user's own, or an isolated directory named by
/// SAGEFS_DATA_DIR (tests, throwaway daemons). The manifest dir and the log dir
/// both read this one decision, so a daemon that is isolated for one is isolated
/// for the other.
[<RequireQualifiedAccess>]
type DataDirChoice =
  | Isolated of dir: string
  | UserDefault

module DataDirChoice =
  /// The environment variable that requests isolation.
  [<Literal>]
  let envVar = "SAGEFS_DATA_DIR"

  /// Unset, empty and blank all mean "not isolated".
  let ofEnvValue (value: string | null) : DataDirChoice =
    match value with
    | null -> DataDirChoice.UserDefault
    | v when String.IsNullOrWhiteSpace v -> DataDirChoice.UserDefault
    | v -> DataDirChoice.Isolated (Path.GetFullPath v)

  let current () : DataDirChoice =
    ofEnvValue (Environment.GetEnvironmentVariable envVar)

/// How much log a daemon may keep. Finite by construction: a chatty day costs
/// at most `MaxFileBytes` per file and `RetainedFiles` files in total.
type LogBounds = { MaxFileBytes: int64; RetainedFiles: int }

/// Where the daemon's log file lives, and what it is called. Pure: the sink
/// inserts the date before the extension, and `fileOn` says what that yields,
/// so the path we print is the file that exists.
module DaemonLog =
  [<Literal>]
  let private fileStem = "mcp-server"

  [<Literal>]
  let private fileExtension = ".log"

  /// 50 MB a file, 7 files: at most 350 MB, where the library default was
  /// 1 GiB a day for 31 days.
  let defaultBounds : LogBounds =
    { MaxFileBytes = 50L * 1024L * 1024L
      RetainedFiles = 7 }

  /// Where a non-isolated daemon has always logged, kept so existing users'
  /// logs stay where they look for them.
  let userLogDirectory () : string =
    Path.Combine(Environment.GetFolderPath Environment.SpecialFolder.LocalApplicationData, "SageFs")

  /// An isolated daemon logs inside its own data dir, never the user's.
  let directory (choice: DataDirChoice) (userLogDir: string) : string =
    match choice with
    | DataDirChoice.Isolated dir -> dir
    | DataDirChoice.UserDefault -> userLogDir

  /// The path handed to the file sink.
  let sinkPath (dir: string) : string =
    Path.Combine(dir, fileStem + fileExtension)

  /// The file the sink writes on `day`. A day that outgrows `MaxFileBytes`
  /// rolls to `_001`, `_002`, ... beside it.
  let fileOn (dir: string) (day: DateOnly) : string =
    Path.Combine(dir, fileStem + day.ToString("yyyyMMdd") + fileExtension)

  /// The roll sequence `name` has on `day`: 0 for the day's first file, N for
  /// `_NNN`, and None for anything that is not one of that day's log files.
  let private rollOf (day: DateOnly) (name: string) : int option =
    let prefix = fileStem + day.ToString("yyyyMMdd")
    match name = prefix + fileExtension with
    | true -> Some 0
    | false ->
      match name.StartsWith(prefix + "_", StringComparison.Ordinal) && name.EndsWith(fileExtension, StringComparison.Ordinal) with
      | false -> None
      | true ->
        let digits = name.Substring(prefix.Length + 1, name.Length - prefix.Length - 1 - fileExtension.Length)
        match Int32.TryParse digits with
        | true, n when digits.Length > 0 && Seq.forall Char.IsDigit digits -> Some n
        | _ -> None

  /// The file the sink is writing on `day`, given the file names in `dir`: the
  /// highest roll. A day whose first file is already over `MaxFileBytes` (an
  /// upgrade from the unbounded logger leaves exactly that) rolls at startup,
  /// so `fileOn` alone names a file nothing is writing to. With no file for the
  /// day yet, the day's first, which is where the sink starts.
  let activeFileOn (dir: string) (day: DateOnly) (existing: string list) : string =
    match existing |> List.choose (fun name -> rollOf day name |> Option.map (fun roll -> roll, name)) with
    | [] -> fileOn dir day
    | rolls -> Path.Combine(dir, rolls |> List.maxBy fst |> snd)

  /// This process's log directory, from SAGEFS_DATA_DIR or the user default.
  let currentDirectory () : string =
    directory (DataDirChoice.current ()) (userLogDirectory ())

  /// The file this process is writing today, as it is on disk right now.
  let currentFile () : string =
    let dir = currentDirectory ()
    let names =
      match Directory.Exists dir with
      | true -> Directory.GetFiles dir |> Array.map Path.GetFileName |> Array.toList
      | false -> []
    activeFileOn dir (DateOnly.FromDateTime DateTime.Now) names

module DaemonState =

  let SageFsDir =
    // SAGEFS_DATA_DIR isolates the daemon's persisted state (manifest, test
    // cache, themes, friction store). Tests use it to avoid polluting and
    // being polluted by the real ~/.SageFs state.
    match DataDirChoice.current () with
    | DataDirChoice.Isolated dir -> dir
    | DataDirChoice.UserDefault ->
      let home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
      Path.Combine(home, ".SageFs")

  /// Ensure the data dir exists and is owner-only on Unix (roast-9 §8: it held
  /// manifests, the friction db, and cohort ledger under the default umask,
  /// world-readable on a shared machine). Chmods an existing dir too, so a dir
  /// created loosely by any writer is tightened. Best-effort: a failed chmod
  /// never blocks startup.
  let ensureDataDir () =
    let dir = SageFsDir
    Directory.CreateDirectory dir |> ignore
    if not (OperatingSystem.IsWindows()) then
      try
        File.SetUnixFileMode(dir, UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute)
      with _ -> ()

  let defaultMcpPort = 37749

  let jsonOptions =
    JsonSerializerOptions(
      PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
      WriteIndented = true
    )

  let isProcessAlive (pid: int) =
    try
      let p = System.Diagnostics.Process.GetProcessById(pid)
      not p.HasExited
    with
    | :? ArgumentException -> false
    | :? InvalidOperationException -> false

  let httpClient = new System.Net.Http.HttpClient(Timeout = Timeouts.healthCheck)

  let private tryGetIntProperty (name: string) (root: JsonElement) =
    match root.TryGetProperty(name) with
    | true, value when value.ValueKind = JsonValueKind.Number -> Some (value.GetInt32())
    | _ -> None

  let private tryGetStringProperty (name: string) (root: JsonElement) =
    match root.TryGetProperty(name) with
    | true, value when value.ValueKind = JsonValueKind.String -> Some (value.GetString())
    | _ -> None

  let private parseStartedAt (root: JsonElement) =
    match tryGetStringProperty "startedAt" root with
    | Some value ->
      match DateTime.TryParse value with
      | true, dt -> dt.ToUniversalTime()
      | _ -> DateTime.UtcNow
    | None -> DateTime.UtcNow

  let private fallbackInfo mcpPort dashboardPort =
    { Pid = 0
      Port = mcpPort
      DashboardPort = dashboardPort
      StartedAt = DateTime.UtcNow
      WorkingDirectory = Environment.CurrentDirectory
      Version = "unknown"
      ApiVersion = None
      SessionCount = None
      ComponentFailures = [] }

  let private tryGetStringArrayProperty (name: string) (root: JsonElement) : string list =
    match root.TryGetProperty(name) with
    | true, value when value.ValueKind = JsonValueKind.Array ->
      value.EnumerateArray()
      |> Seq.choose (fun el -> if el.ValueKind = JsonValueKind.String then Some (el.GetString()) else None)
      |> List.ofSeq
    | _ -> []

  let tryParseDaemonInfoJson (mcpPort: int) (json: string) : DaemonInfo option =
    try
      use doc = JsonDocument.Parse(json)
      let root = doc.RootElement
      let port =
        tryGetIntProperty "mcpPort" root
        |> Option.orElseWith (fun () -> tryGetIntProperty "port" root)
        |> Option.defaultValue mcpPort
      let dashboardPort =
        tryGetIntProperty "dashboardPort" root
        |> Option.defaultValue (port + 1)
      Some {
        Pid = tryGetIntProperty "pid" root |> Option.defaultValue 0
        Port = port
        DashboardPort = dashboardPort
        StartedAt = parseStartedAt root
        WorkingDirectory = tryGetStringProperty "workingDirectory" root |> Option.defaultValue Environment.CurrentDirectory
        Version = tryGetStringProperty "version" root |> Option.defaultValue "unknown"
        ApiVersion = tryGetIntProperty "apiVersion" root
        SessionCount = tryGetIntProperty "sessionCount" root
        ComponentFailures = tryGetStringArrayProperty "componentFailures" root
      }
    with _ ->
      None

  /// Why a probe of the daemon's port threw. A refused connection is the
  /// expected answer to "is it running" when it is not, so it is a case of its
  /// own rather than a string to sniff.
  [<RequireQualifiedAccess>]
  type ProbeFailure =
    | Refused
    | Unexpected of message: string

  let classifyProbeFailure (ex: exn) : ProbeFailure =
    let rec refused (e: exn) =
      match isNull e with
      | true -> false
      | false ->
        match e with
        | :? System.Net.Sockets.SocketException as s ->
          s.SocketErrorCode = System.Net.Sockets.SocketError.ConnectionRefused
        | :? AggregateException as a -> a.InnerExceptions |> Seq.exists refused
        | _ -> refused e.InnerException
    match refused ex with
    | true -> ProbeFailure.Refused
    | false -> ProbeFailure.Unexpected ex.Message

  /// Probe the daemon's /api/daemon-info endpoint on the dashboard port.
  /// Falls back to probing /dashboard if /api/daemon-info isn't available
  /// (e.g. older daemon versions).
  let probeDaemonHttpAsync (mcpPort: int) : Async<DaemonInfo option> = async {
    let dashboardPort = mcpPort + 1
    try
      let! resp =
        httpClient.GetAsync(sprintf "http://localhost:%d/api/daemon-info" dashboardPort)
        |> Async.AwaitTask
      match resp.IsSuccessStatusCode with
      | true ->
        let! json = resp.Content.ReadAsStringAsync() |> Async.AwaitTask
        return tryParseDaemonInfoJson mcpPort json
      | false ->
        let! fallbackResp =
          httpClient.GetAsync(sprintf "http://localhost:%d/dashboard" dashboardPort)
          |> Async.AwaitTask
        match fallbackResp.IsSuccessStatusCode with
        | true ->
          return Some (fallbackInfo mcpPort dashboardPort)
        | false -> return None
    with ex ->
      match classifyProbeFailure ex with
      | ProbeFailure.Refused ->
        // Nothing is listening. That is the answer, not a fault, and the
        // fallback would knock on the same port and get the same answer.
        return None
      | ProbeFailure.Unexpected message ->
        Utils.Log.warn "[DaemonState] MCP status probe failed on port %d: %s" mcpPort message
        try
          let! fallbackResp =
            httpClient.GetAsync(sprintf "http://localhost:%d/dashboard" dashboardPort)
            |> Async.AwaitTask
          match fallbackResp.IsSuccessStatusCode with
          | true ->
            return Some (fallbackInfo mcpPort dashboardPort)
          | false -> return None
        with ex2 ->
          match classifyProbeFailure ex2 with
          | ProbeFailure.Refused -> return None
          | ProbeFailure.Unexpected message2 ->
            Utils.Log.warn "[DaemonState] Dashboard fallback also failed on port %d: %s" dashboardPort message2
            return None
  }

  /// Synchronous wrapper for callers that can't be async yet.
  let probeDaemonHttp (mcpPort: int) : DaemonInfo option =
    probeDaemonHttpAsync mcpPort |> Async.RunSynchronously

  /// Detect a running daemon by probing the default port via HTTP.
  let readAsync () = probeDaemonHttpAsync defaultMcpPort
  let read () = probeDaemonHttp defaultMcpPort

  /// Detect a running daemon on a specific MCP port.
  let readOnPortAsync (mcpPort: int) = probeDaemonHttpAsync mcpPort
  let readOnPort (mcpPort: int) = probeDaemonHttp mcpPort

  /// Request graceful shutdown via the dashboard API.
  let shutdownClient = new System.Net.Http.HttpClient(Timeout = Timeouts.shutdownHttpClient)

  let requestShutdownAsync (mcpPort: int) = async {
    let dashboardPort = mcpPort + 1
    try
      let! resp =
        shutdownClient.PostAsync(sprintf "http://localhost:%d/api/shutdown" dashboardPort, null)
        |> Async.AwaitTask
      return resp.IsSuccessStatusCode
    with ex ->
      Utils.Log.warn "[DaemonState] Shutdown request to port %d failed: %s" mcpPort ex.Message
      return false
  }

  let requestShutdown (mcpPort: int) =
    requestShutdownAsync mcpPort |> Async.RunSynchronously
