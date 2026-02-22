namespace SageFs

open System
open System.IO
open System.Text.Json

type DaemonInfo = {
  Pid: int
  Port: int
  StartedAt: DateTime
  WorkingDirectory: string
  Version: string
}

module DaemonState =

  let SageFsDir =
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

  let private httpClient = new System.Net.Http.HttpClient(Timeout = TimeSpan.FromSeconds(2.0))

  /// Probe the daemon's /api/daemon-info endpoint on the dashboard port.
  /// Falls back to probing /dashboard if /api/daemon-info isn't available
  /// (e.g. older daemon versions).
  let probeDaemonHttpAsync (mcpPort: int) : Async<DaemonInfo option> = async {
    let dashboardPort = mcpPort + 1
    try
      let! resp =
        httpClient.GetAsync(sprintf "http://localhost:%d/api/daemon-info" dashboardPort)
        |> Async.AwaitTask
      if resp.IsSuccessStatusCode then
        let! json = resp.Content.ReadAsStringAsync() |> Async.AwaitTask
        let doc = JsonDocument.Parse(json)
        let root = doc.RootElement
        let pid = root.GetProperty("pid").GetInt32()
        let version =
          match root.TryGetProperty("version") with
          | true, v -> v.GetString()
          | _ -> "unknown"
        let startedAt =
          match root.TryGetProperty("startedAt") with
          | true, v ->
            match DateTime.TryParse(v.GetString()) with
            | true, dt -> dt.ToUniversalTime()
            | _ -> DateTime.UtcNow
          | _ -> DateTime.UtcNow
        let workingDir =
          match root.TryGetProperty("workingDirectory") with
          | true, v -> v.GetString()
          | _ -> Environment.CurrentDirectory
        return Some {
          Pid = pid
          Port = mcpPort
          StartedAt = startedAt
          WorkingDirectory = workingDir
          Version = version
        }
      else
        let! fallbackResp =
          httpClient.GetAsync(sprintf "http://localhost:%d/dashboard" dashboardPort)
          |> Async.AwaitTask
        if fallbackResp.IsSuccessStatusCode then
          return Some {
            Pid = 0
            Port = mcpPort
            StartedAt = DateTime.UtcNow
            WorkingDirectory = Environment.CurrentDirectory
            Version = "unknown"
          }
        else return None
    with _ ->
      try
        let! fallbackResp =
          httpClient.GetAsync(sprintf "http://localhost:%d/dashboard" dashboardPort)
          |> Async.AwaitTask
        if fallbackResp.IsSuccessStatusCode then
          return Some {
            Pid = 0
            Port = mcpPort
            StartedAt = DateTime.UtcNow
            WorkingDirectory = Environment.CurrentDirectory
            Version = "unknown"
          }
        else return None
      with _ -> return None
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
  let private shutdownClient = new System.Net.Http.HttpClient(Timeout = TimeSpan.FromSeconds(5.0))

  let requestShutdownAsync (mcpPort: int) = async {
    let dashboardPort = mcpPort + 1
    try
      let! resp =
        shutdownClient.PostAsync(sprintf "http://localhost:%d/api/shutdown" dashboardPort, null)
        |> Async.AwaitTask
      return resp.IsSuccessStatusCode
    with _ -> return false
  }

  let requestShutdown (mcpPort: int) =
    requestShutdownAsync mcpPort |> Async.RunSynchronously
