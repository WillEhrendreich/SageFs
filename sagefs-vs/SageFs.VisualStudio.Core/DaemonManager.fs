namespace SageFs.VisualStudio.Core

open System
open System.Diagnostics
open System.Net.Http

/// Manages the SageFs daemon process lifecycle.
module DaemonManager =

  let defaultMcpPort = 37749

  /// Check if a SageFs daemon is already running on the given port.
  let isDaemonRunning (mcpPort: int) =
    use client = new HttpClient(Timeout = TimeSpan.FromSeconds(2.0))
    let dashboardPort = mcpPort + 1
    try
      let resp =
        client.GetAsync(sprintf "http://localhost:%d/api/daemon-info" dashboardPort).Result
      resp.IsSuccessStatusCode
    with _ ->
      try
        let resp =
          client.GetAsync(sprintf "http://localhost:%d/dashboard" dashboardPort).Result
        resp.IsSuccessStatusCode
      with _ -> false

  /// Find the SageFs executable on PATH.
  let findSageFs () =
    let psi =
      ProcessStartInfo(
        "where", "SageFs",
        RedirectStandardOutput = true,
        UseShellExecute = false,
        CreateNoWindow = true)
    try
      use p = Process.Start(psi)
      let line = p.StandardOutput.ReadLine()
      p.WaitForExit(3000) |> ignore
      if String.IsNullOrEmpty line then None
      else Some line
    with _ -> None

  /// Start the SageFs daemon with a project or solution.
  /// Returns Error if daemon is already running or SageFs is not found.
  let startDaemon (projectOrSln: string) =
    if isDaemonRunning defaultMcpPort then
      Error "SageFs daemon is already running"
    else
      match findSageFs () with
      | None -> Error "SageFs not found on PATH. Install with: dotnet tool install --global SageFs"
      | Some exe ->
        let flag =
          if projectOrSln.EndsWith(".sln", StringComparison.OrdinalIgnoreCase)
             || projectOrSln.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase) then
            "--sln"
          else
            "--proj"
        let psi =
          ProcessStartInfo(
            exe,
            sprintf "%s \"%s\"" flag projectOrSln,
            UseShellExecute = true)
        try
          let proc = Process.Start(psi)
          Ok proc.Id
        with ex ->
          Error (sprintf "Failed to start SageFs: %s" ex.Message)

  /// Start the SageFs daemon on a specific port.
  let startDaemonOnPort (projectOrSln: string) (mcpPort: int) =
    if isDaemonRunning mcpPort then
      Error "SageFs daemon is already running"
    else
      match findSageFs () with
      | None -> Error "SageFs not found on PATH. Install with: dotnet tool install --global SageFs"
      | Some exe ->
        let flag =
          if projectOrSln.EndsWith(".sln", StringComparison.OrdinalIgnoreCase)
             || projectOrSln.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase) then
            "--sln"
          else
            "--proj"
        let psi =
          ProcessStartInfo(
            exe,
            sprintf "%s \"%s\" --mcp-port %d" flag projectOrSln mcpPort,
            UseShellExecute = true)
        try
          let proc = Process.Start(psi)
          Ok proc.Id
        with ex ->
          Error (sprintf "Failed to start SageFs: %s" ex.Message)

  /// Open the SageFs dashboard in the default browser.
  let openDashboard (port: int) =
    let url = sprintf "http://localhost:%d/dashboard" port
    Process.Start(ProcessStartInfo(url, UseShellExecute = true)) |> ignore
