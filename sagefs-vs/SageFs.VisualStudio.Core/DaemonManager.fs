namespace SageFs.VisualStudio.Core

open System
open System.Diagnostics
open System.Net.Http
open System.IO

/// Manages the SageFs daemon process lifecycle.
module DaemonManager =

  let defaultMcpPort = Constants.DefaultMcpPort

  let private daemonJsonPath () =
    Path.Combine(
      Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
      "SageFs",
      "daemon.json")

  let tryReadConfiguredDaemonUrlFromContent (json: string) =
    match String.IsNullOrWhiteSpace json with
    | true -> None
    | false ->
      let marker = "\"Url\":\""
      let start = json.IndexOf(marker, StringComparison.OrdinalIgnoreCase)
      match start >= 0 with
      | false -> None
      | true ->
        let valueStart = start + marker.Length
        let valueEnd = json.IndexOf('"', valueStart)
        match valueEnd > valueStart with
        | false -> None
        | true ->
          let url = json.Substring(valueStart, valueEnd - valueStart)
          match String.IsNullOrWhiteSpace url with
          | true -> None
          | false -> Some url

  let tryReadConfiguredDaemonUrl () =
    let path = daemonJsonPath ()
    match File.Exists path with
    | false -> None
    | true ->
      try
        File.ReadAllText(path)
        |> tryReadConfiguredDaemonUrlFromContent
      with _ ->
        None

  let private tryParsePort (url: string) =
    match Uri.TryCreate(url, UriKind.Absolute) with
    | true, uri when uri.Port > 0 -> Some uri.Port
    | _ -> None

  let resolveConfiguredMcpPort (daemonUrl: string option) =
    daemonUrl
    |> Option.bind tryParsePort
    |> Option.defaultValue defaultMcpPort

  let buildDaemonArguments (projectOrSln: string) (mcpPort: int) =
    let flag =
      if projectOrSln.EndsWith(".sln", StringComparison.OrdinalIgnoreCase)
         || projectOrSln.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase) then
        "--sln"
      else
        "--proj"

    sprintf "%s \"%s\" --mcp-port %d" flag projectOrSln mcpPort

  /// Check if a SageFs daemon is already running on the given port.
  let isDaemonRunning (mcpPort: int) =
    use handler = new HttpClientHandler(AutomaticDecompression = System.Net.DecompressionMethods.All)
    use client = new HttpClient(handler, Timeout = TimeSpan.FromSeconds(2.0))
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

  let resolveDiscoveryMcpPortWith (isPortRunning: int -> bool) (daemonUrl: string option) =
    let configuredPort =
      daemonUrl
      |> Option.bind tryParsePort

    match configuredPort with
    | Some port when isPortRunning port -> port
    | Some port when port <> defaultMcpPort && isPortRunning defaultMcpPort -> defaultMcpPort
    | Some port -> port
    | None -> defaultMcpPort

  /// Resolve the MCP port for client discovery.
  /// Prefers the persisted port when a daemon is actually running there, otherwise
  /// falls back to a live daemon on the default port before reusing the configured port.
  let resolveDiscoveryMcpPort (daemonUrl: string option) =
    resolveDiscoveryMcpPortWith isDaemonRunning daemonUrl

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

  /// Start the SageFs daemon on a specific port.
  let startDaemonOnPort (projectOrSln: string) (mcpPort: int) =
    if isDaemonRunning mcpPort then
      Error "SageFs daemon is already running"
    else
      match findSageFs () with
      | None -> Error "SageFs not found on PATH. Install with: dotnet tool install --global SageFs"
      | Some exe ->
        let psi =
          ProcessStartInfo(
            exe,
            buildDaemonArguments projectOrSln mcpPort,
            UseShellExecute = false,
            RedirectStandardError = true,
            CreateNoWindow = true)
        try
          let proc = Process.Start(psi)
          Ok proc
        with ex ->
          Error (sprintf "Failed to start SageFs: %s" ex.Message)

  /// Start the SageFs daemon with a project or solution.
  /// Uses the persisted daemon URL port when one has already been configured.
  let startDaemon (projectOrSln: string) =
    tryReadConfiguredDaemonUrl ()
    |> resolveDiscoveryMcpPort
    |> startDaemonOnPort projectOrSln

  /// Read captured stderr from a daemon process (non-blocking snapshot).
  let readStderr (proc: Process) =
    try
      if proc.HasExited then
        proc.StandardError.ReadToEnd()
      else
        // Non-blocking: read whatever is available
        let sb = System.Text.StringBuilder()
        while proc.StandardError.Peek() >= 0 do
          sb.Append(char (proc.StandardError.Read())) |> ignore
        sb.ToString()
    with _ -> ""

  /// Open the SageFs dashboard in the default browser.
  let openDashboard (port: int) =
    let url = sprintf "http://localhost:%d/dashboard" port
    Process.Start(ProcessStartInfo(url, UseShellExecute = true)) |> ignore
