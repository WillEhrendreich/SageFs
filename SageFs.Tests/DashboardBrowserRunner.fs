module SageFs.Tests.DashboardBrowserRunner

open System
open System.IO
open System.Net
open System.Net.Http
open System.Net.Sockets
open Expecto

/// Run the [Integration] Dashboard browser journeys end to end, owning the
/// daemon lifecycle in-process (no external workflow / runner script).
///
/// CI invokes this via `SageFs.Tests.dll --integration-browser` after a
/// Release build — the same Expecto CLI shape as --integration-host. The
/// journeys need a real daemon with a Ready session; this boots one on an
/// isolated SAGEFS_DATA_DIR + reserved loopback ports, creates a plain
/// session on the small WebappDatastar sample (the dashboard tests eval in
/// the FSI session; they do not need the sample's app running), waits for
/// Ready, points SAGEFS_DASHBOARD_PORT at the dashboard, runs the Expecto
/// list, then tears the daemon down.
let runBrowserJourneys (cliArgs: string array) : int =
  let repoRoot =
    Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, ".."))

  let exe =
    let debugExe = Path.Combine(repoRoot, "SageFs", "bin", "Debug", "net10.0", "SageFs.exe")
    let releaseExe = Path.Combine(repoRoot, "SageFs", "bin", "Release", "net10.0", "SageFs.exe")
    if File.Exists debugExe then debugExe
    elif File.Exists releaseExe then releaseExe
    else "SageFs"

  // Reserve an MCP port up front; the dashboard port is mcp+1 (SageFs binds
  // both). Reserving via port 0 would pick an unrelated port, so instead let
  // the daemon bind --mcp-port on a free port we probe for.
  let pickFreePort () =
    use l = new TcpListener(IPAddress.Loopback, 0)
    l.Start()
    (l.LocalEndpoint :?> IPEndPoint).Port

  let rec findPortPair attempts =
    let mcp = pickFreePort ()
    let dash = mcp + 1
    // Confirm the dashboard port is also free before committing.
    try
      use probe = new TcpListener(IPAddress.Loopback, dash)
      probe.Start()
      mcp
    with
    | :? SocketException when attempts > 0 -> findPortPair (attempts - 1)
    | :? SocketException -> failwith "Browser runner: could not find a free port pair"

  let mcpPort = findPortPair 5
  let dashboardPort = mcpPort + 1

  let dataDir =
    Path.Combine(Path.GetTempPath(), "sagefs-browser", Guid.NewGuid().ToString("N"))
  Directory.CreateDirectory(dataDir) |> ignore

  let psi = Diagnostics.ProcessStartInfo()
  psi.FileName <- exe
  psi.UseShellExecute <- false
  psi.CreateNoWindow <- true
  psi.WorkingDirectory <- repoRoot
  psi.ArgumentList.Add("--mcp-port")
  psi.ArgumentList.Add(string mcpPort)
  psi.ArgumentList.Add("--no-resume")
  psi.Environment["SAGEFS_DATA_DIR"] <- dataDir
  psi.Environment["SAGEFS_HOT_RELOAD"] <- "true"
  // Redirect daemon logs to FILES (never pipes): an undrained pipe deadlocks
  // the daemon once its log buffer fills, freezing warmup before Ready. Files
  // cannot deadlock and are dumped to stderr on failure for CI diagnosis.
  let daemonOutLog = Path.Combine(dataDir, "daemon.stdout.log")
  let daemonErrLog = Path.Combine(dataDir, "daemon.stderr.log")
  psi.RedirectStandardOutput <- true
  psi.RedirectStandardError <- true

  let daemon = Diagnostics.Process.Start(psi)
  // Drain the daemon's stdout/stderr asynchronously into the log files so the
  // pipes never fill regardless of log volume.
  let drain (stream: System.IO.StreamReader) (path: string) =
    let writer = new System.IO.StreamWriter(path, append = true)
    let rec loop () =
      async {
        let! line = stream.ReadLineAsync() |> Async.AwaitTask
        if not (isNull line) then
          do! writer.WriteLineAsync(line) |> Async.AwaitTask
          return! loop ()
      }
    async {
      try
        do! loop ()
      with _ -> ()
      writer.Dispose()
    }
    |> Async.Start
  drain daemon.StandardOutput daemonOutLog
  drain daemon.StandardError daemonErrLog

  use client = new HttpClient(BaseAddress = Uri(sprintf "http://localhost:%d" mcpPort))
  client.Timeout <- TimeSpan.FromSeconds(5.0)

  let dumpDaemonLogs () =
    for path in [ daemonOutLog; daemonErrLog ] do
      try
        if File.Exists path then
          let text = File.ReadAllText(path)
          if not (String.IsNullOrWhiteSpace text) then
            eprintfn "--- %s (tail) ---" (Path.GetFileName path)
            let lines = text.Split('\n')
            let tail = lines |> Array.skip (max 0 (lines.Length - 40))
            tail |> Array.iter (eprintfn "%s")
      with _ -> ()

  let stopDaemon () =
    try
      if not daemon.HasExited then daemon.Kill(entireProcessTree = true)
    with _ -> ()
    try daemon.WaitForExit(5000) |> ignore with _ -> ()
    daemon.Dispose()

  let syncGetString (path: string) =
    client.GetStringAsync(path).GetAwaiter().GetResult()

  let syncPost (path: string) (json: string) =
    use content = new StringContent(json, Text.Encoding.UTF8, "application/json")
    let resp = client.PostAsync(path, content).GetAwaiter().GetResult()
    let status = int resp.StatusCode
    resp.Dispose()
    status

  let exitWith (code: int) =
    stopDaemon ()
    code

  try
    // Wait for /health (up to 60s).
    let mutable healthy = false
    let healthDeadline = DateTime.UtcNow.AddSeconds(60.0)
    while not healthy && DateTime.UtcNow < healthDeadline do
      try
        use _resp = client.GetAsync("/health").GetAwaiter().GetResult()
        healthy <- true
      with _ ->
        Threading.Thread.Sleep(250)

    if not healthy then
      eprintfn "Browser runner: daemon did not become healthy on port %d" mcpPort
      dumpDaemonLogs ()
      exitWith 1
    else
      // Create a session on the WebappDatastar sample; wait for Ready.
      let sampleProject =
        Path.Combine(
          repoRoot, "samples", "demos", "SageFs.Samples.WebappDatastar",
          "SageFs.Samples.WebappDatastar.fsproj")
      let sampleDir = Path.GetDirectoryName(sampleProject)

      let payload =
        System.Text.Json.JsonSerializer.Serialize(
          {| projects = [| sampleProject |]
             workingDirectory = sampleDir |})

      let createStatus = syncPost "/api/sessions/create" payload
      if createStatus <> 200 then
        eprintfn "Browser runner: session create failed (HTTP %d)" createStatus
        exitWith 1
      else
        let mutable ready = false
        // Cold CI runners can take several minutes for the first FSI project
        // load; budget generously (the job's overall timeout is 20 min).
        let warmupDeadline = DateTime.UtcNow.AddSeconds(300.0)
        while not ready && DateTime.UtcNow < warmupDeadline do
          try
            let body = syncGetString "/api/sessions"
            use doc = System.Text.Json.JsonDocument.Parse(body)
            ready <-
              doc.RootElement.GetProperty("sessions").EnumerateArray()
              |> Seq.exists (fun s ->
                s.GetProperty("status").GetString() = "Ready")
          with _ ->
            Threading.Thread.Sleep(1000)

        if not ready then
          eprintfn "Browser runner: session never reached Ready within 300s"
          // Surface the actual session states and the daemon log tail so a CI
          // failure is self-explanatory instead of a bare timeout.
          try
            let body = syncGetString "/api/sessions"
            eprintfn "--- /api/sessions ---"
            eprintfn "%s" body
          with _ -> ()
          dumpDaemonLogs ()
          exitWith 1
        else
          Environment.SetEnvironmentVariable("SAGEFS_DASHBOARD_PORT", string dashboardPort)
          let browserArgv =
            cliArgs
            |> Array.filter (fun a -> a <> "--integration-browser")
          let result =
            Tests.runTestsWithCLIArgs [] browserArgv DashboardBrowserTests.tests
          exitWith result
  with ex ->
    eprintfn "Browser runner: %s" (ex.ToString())
    exitWith 1
