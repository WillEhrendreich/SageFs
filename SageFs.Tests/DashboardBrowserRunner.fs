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

// ============================================================================
// HR-DASH: hot-reload browser journeys (real save -> changed running app).
//
// Phase 2 entry point. Boots an isolated daemon, creates a WebLive session on
// a TEMP COPY of the WebAppFixture (one-session-per-workingDir rule), writes
// a .SageFs/init.fsx into the copy that bootstraps ASP.NET Core refs (a bare
// Web SDK project's Ionide FSI args omit them), starts the fixture app on a
// free port and records app-url.txt. The journeys then drive the DASHBOARD
// page: Watch All -> edit Greeting.fs on disk -> the SAME running app serves
// value B and the dashboard hot-reload panel reflects the watched/reloaded
// state.
//
// CI invokes this via `SageFs.Tests.dll --integration-hr` after a Release
// build (same shape as --integration-host / --integration-browser).
// ============================================================================

/// The init profile written into the temp fixture copy. FSI directives accept
/// literal strings only and cannot appear in loops, so the ASP.NET Core refs
/// are generated into a sibling .fsx with literal #r lines, then #load-ed by
/// a literal path relative to the worker CWD (the fixture dir).
let hotReloadInitProfile = """// Auto-generated HR-DASH init profile.
open System
open System.IO

let dotnetRoot =
  match Environment.GetEnvironmentVariable("DOTNET_ROOT") with
  | null | "" ->
    Path.GetFullPath(Path.Combine(Path.GetDirectoryName(typeof<obj>.Assembly.Location), "..", "..", ".."))
  | root -> root

let aspVerDir =
  Directory.EnumerateDirectories(Path.Combine(dotnetRoot, "shared", "Microsoft.AspNetCore.App"))
  |> Seq.sortDescending
  |> Seq.head

let refsPath = Path.Combine(Environment.CurrentDirectory, "asp-refs.generated.fsx")
let refsLines =
  Directory.EnumerateFiles(aspVerDir, "*.dll", SearchOption.TopDirectoryOnly)
  |> Seq.filter (fun dll ->
    let name = Path.GetFileName dll
    not (name.EndsWith(".resources.dll", StringComparison.OrdinalIgnoreCase))
    && not (name.Contains("aspnetcorev2", StringComparison.OrdinalIgnoreCase))
    && not (name.EndsWith(".ni.dll", StringComparison.OrdinalIgnoreCase)))
  |> Seq.map (fun dll -> sprintf "#r @\"%s\"" dll)
  |> Seq.toList
File.WriteAllLines(refsPath, refsLines)

#load "asp-refs.generated.fsx"
#load "Greeting.fs"
#load "App.fs"

let port =
  let listener = Net.Sockets.TcpListener(Net.IPAddress.Loopback, 0)
  listener.Start()
  let p = (listener.LocalEndpoint :?> Net.IPEndPoint).Port
  listener.Stop()
  p

WebAppFixture.App.run port |> ignore
File.WriteAllText(
  Path.Combine(Environment.CurrentDirectory, "app-url.txt"),
  sprintf "http://127.0.0.1:%d" port)
"""

/// Copy the WebAppFixture into a fresh temp dir and drop in the init profile.
/// Returns the fixture dir.
let prepareHotReloadFixture (repoRoot: string) : string =
  let fixtureSrc =
    Path.Combine(repoRoot, "SageFs.Tests", "fixtures", "WebAppFixture")
  let dest =
    Path.Combine(Path.GetTempPath(), "sagefs-hr", Guid.NewGuid().ToString("N"))
  Directory.CreateDirectory(Path.Combine(dest, ".SageFs")) |> ignore
  for file in [ "Greeting.fs"; "App.fs"; "Program.fs"; "WebAppFixture.fsproj" ] do
    File.Copy(Path.Combine(fixtureSrc, file), Path.Combine(dest, file))
  File.WriteAllText(Path.Combine(dest, ".SageFs", "init.fsx"), hotReloadInitProfile)
  dest

/// Run the HR-DASH browser journeys end to end, owning the daemon lifecycle.
let runHotReloadBrowserJourneys (cliArgs: string array) : int =
  let repoRoot =
    Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, ".."))

  let exe =
    let debugExe = Path.Combine(repoRoot, "SageFs", "bin", "Debug", "net10.0", "SageFs.exe")
    let releaseExe = Path.Combine(repoRoot, "SageFs", "bin", "Release", "net10.0", "SageFs.exe")
    if File.Exists debugExe then debugExe
    elif File.Exists releaseExe then releaseExe
    else "SageFs"

  let pickFreePort () =
    use l = new TcpListener(IPAddress.Loopback, 0)
    l.Start()
    (l.LocalEndpoint :?> IPEndPoint).Port

  let rec findPortPair attempts =
    let mcp = pickFreePort ()
    let dash = mcp + 1
    try
      use probe = new TcpListener(IPAddress.Loopback, dash)
      probe.Start()
      mcp
    with
    | :? SocketException when attempts > 0 -> findPortPair (attempts - 1)
    | :? SocketException -> failwith "HR runner: could not find a free port pair"

  let mcpPort = findPortPair 5
  let dashboardPort = mcpPort + 1
  let dataDir =
    Path.Combine(Path.GetTempPath(), "sagefs-hr", Guid.NewGuid().ToString("N"))
  Directory.CreateDirectory(dataDir) |> ignore

  let fixtureDir = prepareHotReloadFixture repoRoot

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
  let daemonOutLog = Path.Combine(dataDir, "daemon.stdout.log")
  let daemonErrLog = Path.Combine(dataDir, "daemon.stderr.log")
  psi.RedirectStandardOutput <- true
  psi.RedirectStandardError <- true

  let daemon = Diagnostics.Process.Start(psi)
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
    try Directory.Delete(fixtureDir, true) with _ -> ()
    code

  try
    let mutable healthy = false
    let healthDeadline = DateTime.UtcNow.AddSeconds(60.0)
    while not healthy && DateTime.UtcNow < healthDeadline do
      try
        use _resp = client.GetAsync("/health").GetAwaiter().GetResult()
        healthy <- true
      with _ ->
        Threading.Thread.Sleep(250)

    if not healthy then
      eprintfn "HR runner: daemon did not become healthy on port %d" mcpPort
      dumpDaemonLogs ()
      exitWith 1
    else
      // WebLive session on the temp fixture.
      let fixtureProj = Path.Combine(fixtureDir, "WebAppFixture.fsproj")
      let payload =
        System.Text.Json.JsonSerializer.Serialize(
          {| projects = [| fixtureProj |]
             workingDirectory = fixtureDir
             workflow = "WebLive" |})
      let createStatus = syncPost "/api/sessions/create" payload
      if createStatus <> 200 then
        eprintfn "HR runner: session create failed (HTTP %d)" createStatus
        dumpDaemonLogs ()
        exitWith 1
      else
        let mutable ready = false
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
          eprintfn "HR runner: session never reached Ready within 300s"
          try
            let body = syncGetString "/api/sessions"
            eprintfn "--- /api/sessions ---"
            eprintfn "%s" body
          with _ -> ()
          dumpDaemonLogs ()
          exitWith 1
        else
          // The init profile wrote app-url.txt into the fixture dir.
          let appUrlFile = Path.Combine(fixtureDir, "app-url.txt")
          let mutable appUrl = ""
          let urlDeadline = DateTime.UtcNow.AddSeconds(60.0)
          while appUrl = "" && DateTime.UtcNow < urlDeadline do
            try
              if File.Exists appUrlFile then
                appUrl <- File.ReadAllText(appUrlFile).Trim()
              else
                Threading.Thread.Sleep(500)
            with _ ->
              Threading.Thread.Sleep(500)
          if appUrl = "" then
            eprintfn "HR runner: app-url.txt was not written by the init profile"
            dumpDaemonLogs ()
            exitWith 1
          else
            Environment.SetEnvironmentVariable("SAGEFS_DASHBOARD_PORT", string dashboardPort)
            Environment.SetEnvironmentVariable("SAGEFS_HR_APP_URL", appUrl)
            Environment.SetEnvironmentVariable("SAGEFS_HR_FIXTURE_DIR", fixtureDir)
            let hrArgv =
              cliArgs
              |> Array.filter (fun a -> a <> "--integration-hr")
            let result =
              Tests.runTestsWithCLIArgs [] hrArgv HotReloadBrowserTests.tests
            exitWith result
  with ex ->
    eprintfn "HR runner: %s" (ex.ToString())
    exitWith 1
