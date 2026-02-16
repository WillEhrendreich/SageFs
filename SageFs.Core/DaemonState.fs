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

  let private defaultMcpPort = 37749

  let private jsonOptions =
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
  let private probeDaemonHttp (mcpPort: int) : DaemonInfo option =
    let dashboardPort = mcpPort + 1
    try
      use client = new System.Net.Http.HttpClient(Timeout = TimeSpan.FromSeconds(2.0))
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
