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
}

module DaemonState =

  let SageFsDir =
    // SAGEFS_DATA_DIR isolates the daemon's persisted state (manifest, test
    // cache, themes, friction store). Tests use it to avoid polluting and
    // being polluted by the real ~/.SageFs state.
    match Environment.GetEnvironmentVariable("SAGEFS_DATA_DIR") with
    | value when not (String.IsNullOrWhiteSpace value) -> Path.GetFullPath value
    | _ ->
      let home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
      Path.Combine(home, ".SageFs")

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
      SessionCount = None }

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
      }
    with _ ->
      None

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
      Utils.Log.warn "[DaemonState] MCP status probe failed on port %d: %s" mcpPort ex.Message
      try
        let! fallbackResp =
          httpClient.GetAsync(sprintf "http://localhost:%d/dashboard" dashboardPort)
          |> Async.AwaitTask
        match fallbackResp.IsSuccessStatusCode with
        | true ->
          return Some (fallbackInfo mcpPort dashboardPort)
        | false -> return None
      with ex2 ->
        Utils.Log.warn "[DaemonState] Dashboard fallback also failed on port %d: %s" dashboardPort ex2.Message
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
