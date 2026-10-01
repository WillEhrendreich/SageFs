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

  let exe = SageFs.Tests.TestInfrastructure.SageFsBinary.path ()

  // SageFs binds the MCP port it is given AND the dashboard at that port + 1
  // — TestPorts.reservePair proves both are free right now, scanning only
  // this tier's assigned SAGEFS_TEST_PORT_RANGE when one is set, so a
  // concurrently-running tier's daemon can never win the reserve-then-bind
  // race for the same pair.
  let mcpPort, dashboardPort = SageFs.Tests.TestInfrastructure.TestPorts.reservePair ()

  let dataDir = SageFs.Tests.RunnerDirs.create SageFs.Tests.RunnerDirs.Family.BrowserRuns

  let psi = Diagnostics.ProcessStartInfo()
  psi.FileName <- exe
  psi.UseShellExecute <- false
  psi.CreateNoWindow <- true
  psi.WorkingDirectory <- repoRoot
  psi.ArgumentList.Add("--mcp-port")
  psi.ArgumentList.Add(string mcpPort)
  psi.ArgumentList.Add("--owner-pid")
  psi.ArgumentList.Add(string (System.Diagnostics.Process.GetCurrentProcess().Id))
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
  client.Timeout <- TestTimeouts.httpProbe

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
    try daemon.WaitForExit(TestTimeouts.childExit) |> ignore with _ -> ()
    daemon.Dispose()

  let syncGetString (path: string) =
    client.GetStringAsync(path).GetAwaiter().GetResult()

  let syncPost (path: string) (json: string) =
    use content = new StringContent(json, Text.Encoding.UTF8, "application/json")
    let resp = client.PostAsync(path, content).GetAwaiter().GetResult()
    let status = int resp.StatusCode
    // The body says WHY: a refused session create was reported as a bare status.
    let body =
      use reader = new StreamReader(resp.Content.ReadAsStream())
      reader.ReadToEnd()
    resp.Dispose()
    status, body

  let exitWith (code: int) =
    stopDaemon ()
    SageFs.Tests.RunnerDirs.remove dataDir
    code

  try
    // Wait for /health (up to 60s).
    let mutable healthy = false
    let healthDeadline = DateTime.UtcNow.Add TestTimeouts.readyBudget
    while not healthy && DateTime.UtcNow < healthDeadline do
      try
        use _resp = client.GetAsync("/health").GetAwaiter().GetResult()
        healthy <- true
      with _ ->
        Threading.Thread.Sleep(TestTimeouts.pollService)

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

      let createStatus, createBody = syncPost "/api/sessions/create" payload
      if createStatus <> 200 then
        eprintfn "Browser runner: session create failed (HTTP %d): %s" createStatus createBody
        exitWith 1
      else
        let mutable ready = false
        // Cold CI runners can take several minutes for the first FSI project
        // load; budget generously (the job's overall timeout is 20 min).
        let warmupDeadline = DateTime.UtcNow.Add SageFs.Timeouts.browserJourneyWarmup
        while not ready && DateTime.UtcNow < warmupDeadline do
          try
            let body = syncGetString "/api/sessions"
            use doc = System.Text.Json.JsonDocument.Parse(body)
            ready <-
              doc.RootElement.GetProperty("sessions").EnumerateArray()
              |> Seq.exists (fun s ->
                s.GetProperty("status").GetString() = "Ready")
          with _ ->
            Threading.Thread.Sleep(TestTimeouts.pollSlow)

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
          Environment.SetEnvironmentVariable("SAGEFS_BROWSER_MCP_PORT", string mcpPort)
          Environment.SetEnvironmentVariable(
            "SAGEFS_FRICTION_DB", Path.Combine(dataDir, "friction.db"))
          let browserArgv =
            cliArgs
            |> Array.filter (fun a -> a <> "--integration-browser")
          let result =
            SageFs.Tests.TestInfrastructure.TrustSignal.run "--integration-browser" browserArgv (testList "browser journeys" [ DashboardBrowserTests.tests; LiveBindingsBrowserTests.tests; HygieneBrowserTests.tests; ReplFreshnessDashboardTests.browserTests ])
          exitWith result
  with ex ->
    eprintfn "Browser runner: %s" (ex.ToString())
    exitWith 1

// ============================================================================
// HR-DASH: hot-reload browser journeys (real save -> changed running app).
//
// Phase 2 entry point. Boots an isolated daemon, creates a HotReload session on
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
  // WHY match the running FSI worker's OWN runtime major version rather than
  // just taking the newest shared-framework dir: a plain descending SORT
  // BY NAME picks a co-installed preview ahead of the stable release the
  // project actually targets — e.g. "11.0.0-rc.1.26425.128" string-sorts
  // ABOVE "10.0.12" — so a machine with both a stable and a preview SDK
  // installed silently loads the wrong major version's ASP.NET Core refs
  // into a net10.0 fixture. Prefer the dir whose version starts with this
  // process's own runtime major (matching the fixture's TargetFramework,
  // since the isolated FSI host is built for the project's own TFM); fall
  // back to the newest-by-name only when nothing matches, so a single-SDK
  // machine keeps its previous behavior exactly.
  let dirs =
    Directory.EnumerateDirectories(Path.Combine(dotnetRoot, "shared", "Microsoft.AspNetCore.App"))
    |> Seq.toList
  let currentMajorPrefix = sprintf "%d." Environment.Version.Major
  let matchesRuntime (dir: string) =
    Path.GetFileName(dir).StartsWith(currentMajorPrefix)
  match dirs |> List.filter matchesRuntime |> List.sortDescending with
  | best :: _ -> best
  | [] ->
    match dirs |> List.sortDescending with
    | best :: _ -> best
    | [] -> failwithf "No Microsoft.AspNetCore.App shared framework found under %s" dotnetRoot

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

/// Copy the WebAppFixture into a fresh temp dir, drop in the init profile,
/// and pre-build the copy so the daemon's warmup loads an already-built
/// project (a cold ionide/FSI build of a temp copy on a clean CI runner can
/// fault warmup before Ready). Returns the fixture dir.
///
/// `runtime` is the runtime the copy's app runs on. The checked-in fixture
/// targets net11.0; a net10 copy gets its target framework rewritten and a
/// global.json pinning a .NET 10 SDK, so the isolated FSI host the daemon builds
/// for it (it uses the PROJECT's SDK) is a .NET 10 process too.
let prepareHotReloadFixture (repoRoot: string) (runtime: HotReloadStateHarness.HostRuntime) : string =
  let fixtureSrc =
    Path.Combine(repoRoot, "SageFs.Tests", "fixtures", "WebAppFixture")
  let dest = SageFs.Tests.RunnerDirs.create SageFs.Tests.RunnerDirs.Family.HotReloadRuns
  Directory.CreateDirectory(Path.Combine(dest, ".SageFs")) |> ignore
  // WHY every top-level *.fs/*.fsproj rather than a hardcoded list: a
  // hardcoded [ "Greeting.fs"; "App.fs"; "Program.fs"; "WebAppFixture.fsproj" ]
  // silently stopped copying the fixture the moment a new source file (e.g.
  // Shapes.fs, the shape-matrix fixture) was added and referenced by the
  // .fsproj — the temp copy's build then failed with "Source file ... could
  // not be found" for a file that plainly exists in fixtureSrc. Copying every
  // file the directory actually has means adding a fixture file never
  // requires touching this runner again.
  // Filter by an exact extension match (not a "*.fs" glob, which can also
  // match "*.fsproj"/"*.fsx" on some globbing implementations) so the
  // .fsproj is enumerated exactly once.
  let fixtureFiles =
    Directory.EnumerateFiles(fixtureSrc, "*", SearchOption.TopDirectoryOnly)
    |> Seq.filter (fun f -> let e = Path.GetExtension f in e = ".fs" || e = ".fsproj")
  for file in fixtureFiles do
    File.Copy(file, Path.Combine(dest, Path.GetFileName file))
  File.WriteAllText(Path.Combine(dest, ".SageFs", "init.fsx"), hotReloadInitProfile)
  match runtime with
  | HotReloadStateHarness.HostRuntime.Net11 -> ()
  | HotReloadStateHarness.HostRuntime.Net10 ->
    let project = Path.Combine(dest, "WebAppFixture.fsproj")
    let net11Target = "<TargetFramework>net11.0</TargetFramework>"
    let text = File.ReadAllText project
    if not (text.Contains net11Target) then
      failwithf "HR runner: the WebAppFixture project no longer says %s, so the net10 copy cannot be derived from it" net11Target
    File.WriteAllText(project, text.Replace(net11Target, "<TargetFramework>net10.0</TargetFramework>"))
    match HotReloadStateHarness.sdkPin runtime with
    | Some sdk ->
      File.WriteAllText(
        Path.Combine(dest, "global.json"),
        sprintf """{"sdk":{"version":"%s","rollForward":"latestPatch","allowPrerelease":false}}""" sdk)
    | None -> failwith "HR runner: a net10 fixture copy needs an SDK pin, and none was produced"
  // Pre-build the temp copy (Debug is fine — the daemon's config fallback
  // resolves Debug<->Release at the same TFM). Fail loudly with the build log
  // if the fixture itself cannot build on this machine.
  let psi = Diagnostics.ProcessStartInfo()
  psi.FileName <- "dotnet"
  psi.UseShellExecute <- false
  psi.CreateNoWindow <- true
  psi.WorkingDirectory <- dest
  psi.ArgumentList.Add("build")
  psi.ArgumentList.Add("WebAppFixture.fsproj")
  psi.ArgumentList.Add("-v")
  psi.ArgumentList.Add("q")
  psi.RedirectStandardOutput <- true
  psi.RedirectStandardError <- true
  use build = Diagnostics.Process.Start(psi)
  // Drain to files (never undrained pipes): files can't deadlock the child.
  let buildOut = Path.Combine(dest, "build.stdout.log")
  let buildErr = Path.Combine(dest, "build.stderr.log")
  let outWriter = new System.IO.StreamWriter(buildOut)
  let errWriter = new System.IO.StreamWriter(buildErr)
  let drain (stream: System.IO.StreamReader) (writer: System.IO.StreamWriter) =
    async {
      try
        let mutable line = stream.ReadLine()
        while not (isNull line) do
          writer.WriteLine(line)
          line <- stream.ReadLine()
      with _ -> ()
      writer.Dispose()
    }
  let outDrain = drain build.StandardOutput outWriter |> Async.StartAsTask
  let errDrain = drain build.StandardError errWriter |> Async.StartAsTask
  if not (build.WaitForExit(SageFs.Timeouts.webAppHotReloadBuild)) then
    failwithf "HR runner: pre-build of the WebAppFixture copy timed out after %O" SageFs.Timeouts.webAppHotReloadBuild
  try outDrain.Wait(TestTimeouts.childExit) |> ignore with _ -> ()
  try errDrain.Wait(TestTimeouts.childExit) |> ignore with _ -> ()
  if build.ExitCode <> 0 then
    let out = if File.Exists buildOut then File.ReadAllText(buildOut) else ""
    let err = if File.Exists buildErr then File.ReadAllText(buildErr) else ""
    failwithf "HR runner: pre-build of the WebAppFixture copy failed (exit %d).\n%s\n%s"
      build.ExitCode out err
  dest

/// Run the HR-DASH browser journeys end to end, owning the daemon lifecycle.
let runHotReloadBrowserJourneys (cliArgs: string array) : int =
  let repoRoot =
    Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, ".."))

  let exe = SageFs.Tests.TestInfrastructure.SageFsBinary.path ()

  let mcpPort, dashboardPort = SageFs.Tests.TestInfrastructure.TestPorts.reservePair ()
  let dataDir = SageFs.Tests.RunnerDirs.create SageFs.Tests.RunnerDirs.Family.HotReloadRuns

  // The primary copy runs on net11 and carries the dashboard journeys. The net10
  // copy exists for the journeys that have to hold on both runtimes.
  let fixtureDir = prepareHotReloadFixture repoRoot HotReloadStateHarness.HostRuntime.Net11
  let net10FixtureDir = prepareHotReloadFixture repoRoot HotReloadStateHarness.HostRuntime.Net10

  let psi = Diagnostics.ProcessStartInfo()
  psi.FileName <- exe
  psi.UseShellExecute <- false
  psi.CreateNoWindow <- true
  psi.WorkingDirectory <- repoRoot
  psi.ArgumentList.Add("--mcp-port")
  psi.ArgumentList.Add(string mcpPort)
  psi.ArgumentList.Add("--owner-pid")
  psi.ArgumentList.Add(string (System.Diagnostics.Process.GetCurrentProcess().Id))
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
  client.Timeout <- TestTimeouts.httpProbe

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
    try daemon.WaitForExit(TestTimeouts.childExit) |> ignore with _ -> ()
    daemon.Dispose()

  let syncGetString (path: string) =
    client.GetStringAsync(path).GetAwaiter().GetResult()

  let syncPost (path: string) (json: string) =
    use content = new StringContent(json, Text.Encoding.UTF8, "application/json")
    let resp = client.PostAsync(path, content).GetAwaiter().GetResult()
    let status = int resp.StatusCode
    // The body says WHY: a refused session create was reported as a bare status.
    let body =
      use reader = new StreamReader(resp.Content.ReadAsStream())
      reader.ReadToEnd()
    resp.Dispose()
    status, body

  let exitWith (code: int) =
    stopDaemon ()
    try Directory.Delete(fixtureDir, true) with _ -> ()
    try Directory.Delete(net10FixtureDir, true) with _ -> ()
    SageFs.Tests.RunnerDirs.remove dataDir
    code

  /// Create a HotReload session on a prepared fixture copy, wait until THAT
  /// session (found by its working directory, since the daemon now hosts two)
  /// is Ready, then read the app url its init profile wrote. Prints why and
  /// returns None when any step fails.
  let bootHotReloadSession (dir: string) : string option =
    let proj = Path.Combine(dir, "WebAppFixture.fsproj")
    let payload =
      System.Text.Json.JsonSerializer.Serialize(
        {| projects = [| proj |]
           workingDirectory = dir
           workflow = "HotReload" |})
    let createStatus, createBody = syncPost "/api/sessions/create" payload
    if createStatus <> 200 then
      eprintfn "HR runner: session create failed for %s (HTTP %d): %s" dir createStatus createBody
      dumpDaemonLogs ()
      None
    else
      let statusOfThisSession () =
        let body = syncGetString "/api/sessions"
        use doc = System.Text.Json.JsonDocument.Parse(body)
        doc.RootElement.GetProperty("sessions").EnumerateArray()
        |> Seq.tryFind (fun s ->
          String.Equals(Path.GetFullPath(s.GetProperty("workingDirectory").GetString()), Path.GetFullPath dir, StringComparison.Ordinal))
        |> Option.map (fun s -> s.GetProperty("status").GetString())
      let mutable status = ""
      let warmupDeadline = DateTime.UtcNow.Add SageFs.Timeouts.browserJourneyWarmup
      while status <> "Ready" && status <> "Faulted" && DateTime.UtcNow < warmupDeadline do
        try
          status <- statusOfThisSession () |> Option.defaultValue ""
        with _ -> ()
        if status <> "Ready" && status <> "Faulted" then
          Threading.Thread.Sleep(TestTimeouts.pollSlow)
      let dumpSessions () =
        try
          eprintfn "--- /api/sessions ---"
          eprintfn "%s" (syncGetString "/api/sessions")
        with _ -> ()
      if status = "Faulted" then
        eprintfn "HR runner: session on %s Faulted during warmup" dir
        dumpSessions ()
        dumpDaemonLogs ()
        None
      elif status <> "Ready" then
        eprintfn "HR runner: session on %s never reached Ready within %O" dir SageFs.Timeouts.browserJourneyWarmup
        dumpSessions ()
        dumpDaemonLogs ()
        None
      else
        // The init profile wrote app-url.txt into the fixture dir.
        let appUrlFile = Path.Combine(dir, "app-url.txt")
        let mutable appUrl = ""
        let urlDeadline = DateTime.UtcNow.Add TestTimeouts.readyBudget
        while appUrl = "" && DateTime.UtcNow < urlDeadline do
          try
            if File.Exists appUrlFile then
              appUrl <- File.ReadAllText(appUrlFile).Trim()
            else
              Threading.Thread.Sleep(TestTimeouts.pollMedium)
          with _ ->
            Threading.Thread.Sleep(TestTimeouts.pollMedium)
        if appUrl = "" then
          eprintfn "HR runner: app-url.txt was not written by the init profile in %s" dir
          dumpDaemonLogs ()
          None
        else
          Some appUrl

  try
    let mutable healthy = false
    let healthDeadline = DateTime.UtcNow.Add TestTimeouts.readyBudget
    while not healthy && DateTime.UtcNow < healthDeadline do
      try
        use _resp = client.GetAsync("/health").GetAwaiter().GetResult()
        healthy <- true
      with _ ->
        Threading.Thread.Sleep(TestTimeouts.pollService)

    if not healthy then
      eprintfn "HR runner: daemon did not become healthy on port %d" mcpPort
      dumpDaemonLogs ()
      exitWith 1
    else
      // The net10 session first and the primary one last: creating a session
      // makes it the active one, and the dashboard journeys read the active
      // session's panel, so the net11 fixture they edit has to be the last created.
      match bootHotReloadSession net10FixtureDir with
      | None -> exitWith 1
      | Some net10AppUrl ->
      match bootHotReloadSession fixtureDir with
      | None -> exitWith 1
      | Some appUrl ->
        Environment.SetEnvironmentVariable("SAGEFS_DASHBOARD_PORT", string dashboardPort)
        Environment.SetEnvironmentVariable("SAGEFS_HR_APP_URL", appUrl)
        Environment.SetEnvironmentVariable("SAGEFS_HR_FIXTURE_DIR", fixtureDir)
        Environment.SetEnvironmentVariable(HotReloadInlinedCalleeJourneyTests.Env.mcpPort, string mcpPort)
        Environment.SetEnvironmentVariable(HotReloadInlinedCalleeJourneyTests.Env.net10AppUrl, net10AppUrl)
        Environment.SetEnvironmentVariable(HotReloadInlinedCalleeJourneyTests.Env.net10FixtureDir, net10FixtureDir)
        let hrArgv =
          cliArgs
          |> Array.filter (fun a -> a <> "--integration-hr")
        // The inlined-callee journeys go first: the dashboard journeys end with
        // restore saves on the net11 session, and an outcome from one of those
        // arriving during a journey would be read as that journey's save. The latency measurement goes
        // last, from a session the journeys have left at rest, and it saves Greeting.fs and CalledCallee.fs
        // of the net11 copy, so nothing may run beside it.
        let hrJourneys =
          testList
            "hot-reload journeys"
            [ HotReloadInlinedCalleeJourneyTests.tests
              HotReloadBrowserTests.tests
              testSequenced HotReloadLatencyTests.latencyTests ]
        let result =
          SageFs.Tests.TestInfrastructure.TrustSignal.run "--integration-hr" hrArgv hrJourneys
        exitWith result
  with ex ->
    eprintfn "HR runner: %s" (ex.ToString())
    exitWith 1

// ============================================================================
// LT-DASH: live-testing browser journeys (real enable -> discover -> edit ->
// failing test -> fix -> green, all through the live dashboard).
//
// Boots an isolated daemon, creates a session on the FromCSharp sample (the
// same small sample the daemon-level live-testing integration tests use — it
// carries 11 Expecto tests in Hello.fs, resolved via the repo's central
// package management, so it must be used IN PLACE, not temp-copied). The
// journeys drive the DASHBOARD page's #live-testing-panel: Enable -> the
// panel shows 11✓ after discovery+baseline -> Hello.fs is edited on disk ->
// the panel shows 10✓ 1✗ -> the edit is reverted -> the panel returns to
// 11✓. The journey restores Hello.fs in a finally, so the checkout is never
// left mutated.
//
// CI invokes this via `SageFs.Tests.dll --integration-lt` after a Release
// build (same shape as --integration-hr / --integration-browser).
// ============================================================================

/// Run the LT-DASH browser journeys end to end, owning the daemon lifecycle.
let runLiveTestingBrowserJourneys (cliArgs: string array) : int =
  let repoRoot =
    Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, ".."))

  let exe = SageFs.Tests.TestInfrastructure.SageFsBinary.path ()

  let mcpPort, dashboardPort = SageFs.Tests.TestInfrastructure.TestPorts.reservePair ()
  let dataDir = SageFs.Tests.RunnerDirs.create SageFs.Tests.RunnerDirs.Family.LiveTestingRuns

  // The FromCSharp sample IN PLACE (central package management; a temp copy
  // outside the repo cannot resolve Expecto's version). Live-testing rebuilds
  // it on edit via dotnet build, which works here because the repo's
  // Directory.Packages.props + nuget.config are in scope.
  let sampleProject =
    Path.Combine(
      repoRoot, "samples", "from-csharp", "SageFs.Samples.FromCSharp",
      "SageFs.Samples.FromCSharp.fsproj")
  let sampleDir = Path.GetDirectoryName(sampleProject)
  let helloPath = Path.Combine(sampleDir, "Hello.fs")

  let psi = Diagnostics.ProcessStartInfo()
  psi.FileName <- exe
  psi.UseShellExecute <- false
  psi.CreateNoWindow <- true
  psi.WorkingDirectory <- repoRoot
  psi.ArgumentList.Add("--mcp-port")
  psi.ArgumentList.Add(string mcpPort)
  psi.ArgumentList.Add("--owner-pid")
  psi.ArgumentList.Add(string (System.Diagnostics.Process.GetCurrentProcess().Id))
  psi.ArgumentList.Add("--no-resume")
  psi.Environment["SAGEFS_DATA_DIR"] <- dataDir
  // Live testing does not need hot reload, and with it on a worker that restarts (a rebuild) replays the
  // session's evals under value-read tracking, whose getter hook throws on the patched `Hello.tests`
  // (`ValueReadTracking.readerIdOf` reads `MetadataToken` of a dynamic method): discovery then finds zero
  // tests. Hot reload has its own tier; this one measures and gates live testing.
  psi.Environment["SAGEFS_HOT_RELOAD"] <- "false"
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
  client.Timeout <- TestTimeouts.httpProbe

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
    try daemon.WaitForExit(TestTimeouts.childExit) |> ignore with _ -> ()
    daemon.Dispose()

  let syncGetString (path: string) =
    client.GetStringAsync(path).GetAwaiter().GetResult()

  let syncPost (path: string) (json: string) =
    use content = new StringContent(json, Text.Encoding.UTF8, "application/json")
    let resp = client.PostAsync(path, content).GetAwaiter().GetResult()
    let status = int resp.StatusCode
    // The body says WHY: a refused session create was reported as a bare status.
    let body =
      use reader = new StreamReader(resp.Content.ReadAsStream())
      reader.ReadToEnd()
    resp.Dispose()
    status, body

  let exitWith (code: int) =
    stopDaemon ()
    SageFs.Tests.RunnerDirs.remove dataDir
    code

  // Always restore Hello.fs if a journey left it mutated (belt and braces on
  // top of the journey's own finally).
  let restoreHello () =
    try
      let git = Diagnostics.ProcessStartInfo("git")
      git.WorkingDirectory <- repoRoot
      git.ArgumentList.Add("checkout")
      git.ArgumentList.Add("--")
      git.ArgumentList.Add(Path.GetRelativePath(repoRoot, helloPath))
      git.UseShellExecute <- false
      git.CreateNoWindow <- true
      git.RedirectStandardOutput <- true
      git.RedirectStandardError <- true
      use p = Diagnostics.Process.Start(git)
      p.WaitForExit(TestTimeouts.childExitSlow) |> ignore
    with _ -> ()

  try
    let mutable healthy = false
    let healthDeadline = DateTime.UtcNow.Add TestTimeouts.readyBudget
    while not healthy && DateTime.UtcNow < healthDeadline do
      try
        use _resp = client.GetAsync("/health").GetAwaiter().GetResult()
        healthy <- true
      with _ ->
        Threading.Thread.Sleep(TestTimeouts.pollService)

    if not healthy then
      eprintfn "LT runner: daemon did not become healthy on port %d" mcpPort
      dumpDaemonLogs ()
      exitWith 1
    else
      // Session on the FromCSharp sample; wait for Ready.
      let payload =
        System.Text.Json.JsonSerializer.Serialize(
          {| projects = [| sampleProject |]
             workingDirectory = sampleDir |})
      let createStatus, createBody = syncPost "/api/sessions/create" payload
      if createStatus <> 200 then
        eprintfn "LT runner: session create failed (HTTP %d): %s" createStatus createBody
        dumpDaemonLogs ()
        exitWith 1
      else
        let mutable ready = false
        let mutable faulted = false
        let warmupDeadline = DateTime.UtcNow.Add SageFs.Timeouts.browserJourneyWarmup
        while not ready && not faulted && DateTime.UtcNow < warmupDeadline do
          try
            let body = syncGetString "/api/sessions"
            use doc = System.Text.Json.JsonDocument.Parse(body)
            let sessionStates =
              doc.RootElement.GetProperty("sessions").EnumerateArray()
              |> Seq.map (fun s -> s.GetProperty("status").GetString())
              |> Seq.toList
            if sessionStates |> List.contains "Faulted" then
              faulted <- true
            ready <- sessionStates |> List.contains "Ready"
          with _ ->
            Threading.Thread.Sleep(TestTimeouts.pollSlow)

        if faulted then
          eprintfn "LT runner: session Faulted during warmup"
          try
            let body = syncGetString "/api/sessions"
            eprintfn "--- /api/sessions ---"
            eprintfn "%s" body
          with _ -> ()
          dumpDaemonLogs ()
          exitWith 1
        elif not ready then
          eprintfn "LT runner: session never reached Ready within 300s"
          try
            let body = syncGetString "/api/sessions"
            eprintfn "--- /api/sessions ---"
            eprintfn "%s" body
          with _ -> ()
          dumpDaemonLogs ()
          exitWith 1
        else
          // Pre-settle live testing over the daemon's own HTTP API BEFORE
          // handing off to the browser journeys — mirroring the settle-then-
          // baseline sequence HttpApiIntegrationTests.fs already proves works
          // (:1020-1090: enable -> policy -> wait ready_with_tests -> wait
          // Running=0 -> run baseline if needed -> wait 11-green). The
          // browser journey's own UI wait for "11✓" previously had to cover
          // enable+discovery+build+baseline in one 60s window and timed out
          // under CI load; by the time Playwright opens the dashboard here,
          // a server-authoritative GET render already reflects the finished
          // baseline with no SSE round-trip needed, so that wait resolves
          // near-instantly instead of racing the whole pipeline. Best-effort:
          // logged, not fatal — the journey's own (generous) waits remain the
          // authority on pass/fail.
          let getLiveTestingSummary () =
            try
              let body = syncGetString "/api/live-testing/status"
              use doc = System.Text.Json.JsonDocument.Parse(body)
              let root = doc.RootElement
              let summary = root.GetProperty("Summary")
              Some
                {| DiscoveryState = root.GetProperty("DiscoveryState").GetString()
                   Total = summary.GetProperty("Total").GetInt32()
                   Passed = summary.GetProperty("Passed").GetInt32()
                   Failed = summary.GetProperty("Failed").GetInt32()
                   Running = summary.GetProperty("Running").GetInt32() |}
            with _ -> None
          let waitForLiveTesting (deadlineSeconds: float) predicate =
            let deadline = DateTime.UtcNow.AddSeconds(deadlineSeconds)
            let mutable matched = false
            while not matched && DateTime.UtcNow < deadline do
              match getLiveTestingSummary () with
              | Some snap when predicate snap -> matched <- true
              | _ -> Threading.Thread.Sleep(TestTimeouts.pollService)
            matched
          syncPost "/api/live-testing/enable" "{}" |> ignore
          syncPost "/api/live-testing/policy" """{"category":"unit","policy":"every"}""" |> ignore
          let discovered =
            waitForLiveTesting 90.0 (fun s -> s.DiscoveryState = "ready_with_tests" && s.Total >= 11)
          let settled =
            discovered && waitForLiveTesting 90.0 (fun s -> s.Total >= 11 && s.Running = 0)
          let baseline =
            settled
            && (match getLiveTestingSummary () with
                | Some s when s.Total >= 11 && s.Passed >= 11 && s.Failed = 0 && s.Running = 0 -> true
                | _ ->
                  syncPost "/api/live-testing/run" """{"pattern":"","category":""}""" |> ignore
                  waitForLiveTesting 90.0 (fun s ->
                    s.Total >= 11 && s.Passed >= 11 && s.Failed = 0 && s.Running = 0))
          if not baseline then
            eprintfn "LT runner: live testing did not settle to an 11-green baseline before the browser journeys started (continuing — the journey's own waits are authoritative)"
            match getLiveTestingSummary () with
            | Some s ->
              eprintfn
                "--- /api/live-testing/status (last) --- Total=%d Passed=%d Failed=%d Running=%d DiscoveryState=%s"
                s.Total s.Passed s.Failed s.Running s.DiscoveryState
            | None -> eprintfn "--- /api/live-testing/status (last) --- unavailable"
            dumpDaemonLogs ()

          try
            Environment.SetEnvironmentVariable("SAGEFS_DASHBOARD_PORT", string dashboardPort)
            Environment.SetEnvironmentVariable("SAGEFS_LT_FIXTURE_DIR", sampleDir)
            Environment.SetEnvironmentVariable("SAGEFS_LT_DATA_DIR", dataDir)
            Environment.SetEnvironmentVariable("SAGEFS_LT_MCP_PORT", string mcpPort)
            let ltArgv =
              cliArgs
              |> Array.filter (fun a -> a <> "--integration-lt")
            // One daemon, one session, one Hello.fs: everything that edits it runs in sequence. The
            // journeys come first (they need the baseline run's coverage untouched by anything else),
            // the browser tests next, and the latency measurement last, from a settled session.
            let ltTests =
              Expecto.Tests.testSequenced (
                Expecto.Tests.testList
                  "Live testing against the FromCSharp sample"
                  [ LiveTestingJourneyTests.journeyTests
                    LiveTestingBrowserTests.tests
                    LiveTestingLatencyTests.latencyTests ])
            let result =
              SageFs.Tests.TestInfrastructure.TrustSignal.run "--integration-lt" ltArgv ltTests
            exitWith result
          finally
            restoreHello ()
  with ex ->
    eprintfn "LT runner: %s" (ex.ToString())
    try restoreHello () with _ -> ()
    exitWith 1

