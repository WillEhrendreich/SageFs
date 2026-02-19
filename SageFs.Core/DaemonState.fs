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

  /// Probe the daemon's /api/daemon-info endpoint on the dashboard port.
  /// Falls back to probing /dashboard if /api/daemon-info isn't available
  /// (e.g. older daemon versions).
  let probeDaemonHttp (mcpPort: int) : DaemonInfo option =
    let dashboardPort = mcpPort + 1
    try
      use client = new System.Net.Http.HttpClient(Timeout = TimeSpan.FromSeconds(2.0))
      // Try the structured endpoint first
      let resp = client.GetAsync(sprintf "http://localhost:%d/api/daemon-info" dashboardPort).Result
      if resp.IsSuccessStatusCode then
        let json = resp.Content.ReadAsStringAsync().Result
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
        Some {
          Pid = pid
          Port = mcpPort
          StartedAt = startedAt
          WorkingDirectory = workingDir
          Version = version
        }
      else
        // Fallback: probe /dashboard (always existed) to confirm daemon is alive
        let fallbackResp = client.GetAsync(sprintf "http://localhost:%d/dashboard" dashboardPort).Result
        if fallbackResp.IsSuccessStatusCode then
          Some {
            Pid = 0
            Port = mcpPort
            StartedAt = DateTime.UtcNow
            WorkingDirectory = Environment.CurrentDirectory
            Version = "unknown"
          }
        else None
    with _ ->
      // Last resort: try /dashboard in case /api/daemon-info threw
      try
        use client = new System.Net.Http.HttpClient(Timeout = TimeSpan.FromSeconds(2.0))
        let fallbackResp = client.GetAsync(sprintf "http://localhost:%d/dashboard" dashboardPort).Result
        if fallbackResp.IsSuccessStatusCode then
          Some {
            Pid = 0
            Port = mcpPort
            StartedAt = DateTime.UtcNow
            WorkingDirectory = Environment.CurrentDirectory
            Version = "unknown"
          }
        else None
      with _ -> None

  /// Detect a running daemon by probing the default port via HTTP.
  let read () = probeDaemonHttp defaultMcpPort

  /// Detect a running daemon on a specific MCP port.
  let readOnPort (mcpPort: int) = probeDaemonHttp mcpPort

  /// Request graceful shutdown via the dashboard API.
  let requestShutdown (mcpPort: int) =
    let dashboardPort = mcpPort + 1
    try
      use client = new System.Net.Http.HttpClient(Timeout = TimeSpan.FromSeconds(5.0))
      let resp = client.PostAsync(sprintf "http://localhost:%d/api/shutdown" dashboardPort, null).Result
      resp.IsSuccessStatusCode
    with _ -> false
