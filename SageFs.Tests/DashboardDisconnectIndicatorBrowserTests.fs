/// Browser coverage for the dashboard disconnect indicator
/// (todo-dashboard-disconnect-indicator.md, cohort item E9).
///
/// Regression: the daemon dying mid-stream (redeploy, crash, SIGTERM) gave no
/// visible sign on an open dashboard tab — the banner element and CSS existed
/// but nothing ever turned them on. The fix is a server SSE heartbeat
/// (`Timeouts.dashboardHeartbeat`) patched into a client-observable Datastar
/// signal, checked client-side on a `Ds.onInterval` against
/// `Timeouts.dashboardStaleAfter`; see Dashboard.fs's `ConnMonitor` module and
/// `connectionStaleCheckExpr`.
///
/// This suite owns its OWN isolated daemon on non-default, freshly-picked
/// ports (never 37749/37750 — must never collide with a shared dogfood
/// daemon) so it can kill and restart that daemon mid-test without disturbing
/// anything else. It sets `SAGEFS_DASHBOARD_HEARTBEAT_SECONDS` /
/// `SAGEFS_DASHBOARD_STALE_AFTER_SECONDS` to small values so the staleness
/// budget in the journey is seconds, not the 15s production default.
///
/// No FSI session is created — the disconnect indicator is shell-level chrome
/// (renders in every dashboard state, including the no-session picker
/// landing), so the no-session page is sufficient and keeps this journey free
/// of FSI-warmup timing flakiness.
///
/// Registered `Integration.Dedicated "--integration-disconnect"` (mirrors
/// --integration-browser/-hr/-lt) so the default suite excludes it by
/// construction. NOTE: wiring `--integration-disconnect` into Program.fs's
/// CLI dispatch is out of this island's file scope (Program.fs is owned by
/// no one on this cohort item) — `runDisconnectIndicatorJourney` below is the
/// entry point a future CLI-wiring change (or a direct call, as this island's
/// own local verification used) should invoke.
module SageFs.Tests.DashboardDisconnectIndicatorBrowserTests

open System
open System.IO
open System.Net
open System.Net.Http
open System.Net.Sockets
open System.Threading.Tasks
open Expecto
open Microsoft.Playwright

module Integration = SageFs.Tests.TestInfrastructure.Integration

/// Fast-but-not-flaky test values: heartbeat every 1s, stale after 3s (still
/// comfortably the doctrine's 3x ratio). Production defaults (5s / 15s) stay
/// in Timeouts.fs — these are journey-local env overrides only.
module private TestTiming =
  let heartbeatSeconds = "1"
  let staleAfterSeconds = "3"
  /// Generous margin over the stale budget so a loaded CI runner's scheduler
  /// jitter never false-fails the "became stale" assertion.
  let staleWaitBudgetMs = 15_000
  /// Generous margin for the reconnect assertion after the daemon restarts.
  let reconnectWaitBudgetMs = 20_000

/// Poll `condition` until it returns true or `budgetMs` elapses. Not a
/// Thread.Sleep poll loop over a fixed count — it's a bounded wait against a
/// real async condition, the same shape `PlaywrightExpect.waitForText` uses
/// elsewhere in the dashboard browser suite.
let private waitUntil (budgetMs: int) (condition: unit -> Task<bool>) : Task<bool> = task {
  let sw = Diagnostics.Stopwatch.StartNew()
  let mutable ok = false
  while not ok && sw.ElapsedMilliseconds < int64 budgetMs do
    let! result = condition ()
    if result then ok <- true
    else do! Task.Delay(200)
  return ok
}

let private bannerHidden (page: IPage) = task {
  let banner = page.Locator("#server-status")
  let! visible = banner.IsVisibleAsync()
  return not visible
}

let private bannerVisible (page: IPage) = task {
  let banner = page.Locator("#server-status")
  return! banner.IsVisibleAsync()
}

let private dataConnected (page: IPage) (expected: string) = task {
  let! v = page.EvaluateAsync<string>("() => document.body.getAttribute('data-connected')")
  return v = expected
}

/// Owns the full lifecycle of one isolated, non-default-port daemon: pick a
/// free MCP/dashboard port pair, spawn `SageFsBinary.path()` with an isolated
/// SAGEFS_DATA_DIR and the fast heartbeat/stale-after env overrides, drain
/// stdout/stderr to files (never undrained pipes — see DashboardBrowserRunner
/// for why), and wait for /health. Mirrors DashboardBrowserRunner's daemon
/// bootstrap; kept local to this file since this journey needs KILL + RESPAWN
/// on the SAME port pair, which the shared runners don't do.
type private IsolatedDaemon =
  { Process: Diagnostics.Process
    McpPort: int
    DashboardPort: int
    DataDir: string
    OutLog: string
    ErrLog: string }

module private IsolatedDaemon =
  let private pickFreePort () =
    use l = new TcpListener(IPAddress.Loopback, 0)
    l.Start()
    (l.LocalEndpoint :?> IPEndPoint).Port

  let rec private findPortPair attempts =
    let mcp = pickFreePort ()
    let dash = mcp + 1
    try
      use probe = new TcpListener(IPAddress.Loopback, dash)
      probe.Start()
      mcp
    with
    | :? SocketException when attempts > 0 -> findPortPair (attempts - 1)
    | :? SocketException -> failwith "Disconnect-indicator runner: could not find a free port pair"

  let private drain (stream: StreamReader) (path: string) =
    let writer = new StreamWriter(path, append = true)
    let rec loop () =
      async {
        let! line = stream.ReadLineAsync() |> Async.AwaitTask
        if not (isNull line) then
          do! writer.WriteLineAsync(line) |> Async.AwaitTask
          return! loop ()
      }
    async {
      try do! loop () with _ -> ()
      writer.Dispose()
    }
    |> Async.Start

  /// Spawn a fresh daemon process on `mcpPort`/`dashPort` (both must already
  /// be free — the caller reuses the SAME pair across kill+respawn so the
  /// browser's existing SSE connection can reconnect without a page reload).
  let private spawnOn (repoRoot: string) (dataDir: string) (mcpPort: int) : Diagnostics.Process =
    let exe = SageFs.Tests.TestInfrastructure.SageFsBinary.path ()
    let psi = Diagnostics.ProcessStartInfo()
    psi.FileName <- exe
    psi.UseShellExecute <- false
    psi.CreateNoWindow <- true
    psi.WorkingDirectory <- repoRoot
    psi.ArgumentList.Add("--mcp-port")
    psi.ArgumentList.Add(string mcpPort)
    psi.ArgumentList.Add("--no-resume")
    psi.Environment["SAGEFS_DATA_DIR"] <- dataDir
    // Fast heartbeat/stale-after so the journey's staleness assertions land
    // in seconds instead of the 15s production default.
    psi.Environment["SAGEFS_DASHBOARD_HEARTBEAT_SECONDS"] <- TestTiming.heartbeatSeconds
    psi.Environment["SAGEFS_DASHBOARD_STALE_AFTER_SECONDS"] <- TestTiming.staleAfterSeconds
    psi.RedirectStandardOutput <- true
    psi.RedirectStandardError <- true
    let proc = Diagnostics.Process.Start(psi)
    let outLog = Path.Combine(dataDir, sprintf "daemon.stdout.%d.log" proc.Id)
    let errLog = Path.Combine(dataDir, sprintf "daemon.stderr.%d.log" proc.Id)
    drain proc.StandardOutput outLog
    drain proc.StandardError errLog
    proc

  /// Spawn a brand-new isolated daemon on a freshly-picked, free port pair.
  let start (repoRoot: string) : IsolatedDaemon =
    let mcpPort = findPortPair 5
    let dashPort = mcpPort + 1
    let dataDir = Path.Combine(Path.GetTempPath(), "sagefs-disconnect", Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory(dataDir) |> ignore
    let proc = spawnOn repoRoot dataDir mcpPort
    { Process = proc
      McpPort = mcpPort
      DashboardPort = dashPort
      DataDir = dataDir
      OutLog = Path.Combine(dataDir, sprintf "daemon.stdout.%d.log" proc.Id)
      ErrLog = Path.Combine(dataDir, sprintf "daemon.stderr.%d.log" proc.Id) }

  /// Kill this daemon's process (does not free the port instantly — the
  /// caller waits for the browser-observable staleness effect, which is the
  /// thing under test, not the port).
  let kill (d: IsolatedDaemon) =
    try
      if not d.Process.HasExited then d.Process.Kill(entireProcessTree = true)
    with _ -> ()
    try d.Process.WaitForExit(5000) |> ignore with _ -> ()

  /// Respawn on the SAME port pair and data dir `d` was using, returning the
  /// updated record (new OS process, same ports — the point of the test).
  let respawn (repoRoot: string) (d: IsolatedDaemon) : IsolatedDaemon =
    let proc = spawnOn repoRoot d.DataDir d.McpPort
    { d with
        Process = proc
        OutLog = Path.Combine(d.DataDir, sprintf "daemon.stdout.%d.log" proc.Id)
        ErrLog = Path.Combine(d.DataDir, sprintf "daemon.stderr.%d.log" proc.Id) }

  let dumpLogs (d: IsolatedDaemon) =
    for path in [ d.OutLog; d.ErrLog ] do
      try
        if File.Exists path then
          let text = File.ReadAllText(path)
          if not (String.IsNullOrWhiteSpace text) then
            eprintfn "--- %s (tail) ---" (Path.GetFileName path)
            let lines = text.Split('\n')
            lines |> Array.skip (max 0 (lines.Length - 40)) |> Array.iter (eprintfn "%s")
      with _ -> ()

  let waitHealthy (budgetSeconds: float) (d: IsolatedDaemon) : Task<bool> = task {
    use client = new HttpClient(BaseAddress = Uri(sprintf "http://localhost:%d" d.McpPort))
    client.Timeout <- TimeSpan.FromSeconds(5.0)
    let deadline = DateTime.UtcNow.AddSeconds(budgetSeconds)
    let mutable healthy = false
    while not healthy && DateTime.UtcNow < deadline do
      try
        let! resp = client.GetAsync("/health")
        resp.Dispose()
        healthy <- true
      with _ ->
        do! Task.Delay(250)
    return healthy
  }

/// The full kill/restart journey, run as ordinary Playwright.NET code (not a
/// separate JS harness) inside the Expecto suite, per the doctrine this repo
/// enforces for all browser coverage.
let private disconnectIndicatorJourney () = task {
  let repoRoot = Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, ".."))
  let mutable daemon = IsolatedDaemon.start repoRoot
  let mutable playwright: IPlaywright option = None
  let mutable browser: IBrowser option = None
  let consoleErrors = Collections.Generic.List<string>()

  try
    let! initialHealthy = IsolatedDaemon.waitHealthy 60.0 daemon
    match initialHealthy with
    | false ->
      eprintfn "Disconnect-indicator journey: daemon never became healthy on port %d" daemon.McpPort
      IsolatedDaemon.dumpLogs daemon
      Tests.failtestf "isolated daemon on port %d never became healthy" daemon.McpPort
    | true ->

    let! pw = Playwright.CreateAsync()
    let! b = pw.Chromium.LaunchAsync(BrowserTypeLaunchOptions(Headless = true))
    playwright <- Some pw
    browser <- Some b
    let! ctx = b.NewContextAsync()
    let! page = ctx.NewPageAsync()

    // Zero-Datastar-console-errors gate (todo verification §4): collect every
    // console message and uncaught page error for the whole journey; asserted
    // at the end so a late error (e.g. during the kill/respawn window) counts.
    page.Console.Add(fun msg ->
      if msg.Type = "error" then
        consoleErrors.Add(sprintf "[console] %s" msg.Text))
    page.PageError.Add(fun err -> consoleErrors.Add(sprintf "[pageerror] %s" err))

    let! _ = page.GotoAsync(sprintf "http://localhost:%d/dashboard" daemon.DashboardPort)

    // 1. Daemon up: banner hidden, data-connected="true".
    let! initiallyHidden = waitUntil 10_000 (fun () -> bannerHidden page)
    Expect.isTrue initiallyHidden "banner hidden while the daemon is up"
    let! initiallyConnected = waitUntil 5_000 (fun () -> dataConnected page "true")
    Expect.isTrue initiallyConnected "body[data-connected]=\"true\" while the daemon is up"

    // 2. Kill the daemon mid-stream: banner becomes visible + data-connected
    //    flips to "false" within the (test-fast) staleness budget — with NO
    //    page reload, purely the client-side Ds.onInterval staleness check.
    IsolatedDaemon.kill daemon
    let! becameVisible = waitUntil TestTiming.staleWaitBudgetMs (fun () -> bannerVisible page)
    Expect.isTrue becameVisible
      (sprintf "banner became visible within %dms of the daemon dying" TestTiming.staleWaitBudgetMs)
    let! becameDisconnected = waitUntil 5_000 (fun () -> dataConnected page "false")
    Expect.isTrue becameDisconnected "body[data-connected] flipped to \"false\" after staleness"
    let! bannerText = page.Locator("#server-status").TextContentAsync()
    Expect.isTrue
      (not (isNull bannerText) && bannerText.Contains("Daemon not running"))
      "banner keeps the actionable copy (\"Daemon not running—start SageFs to continue\")"

    // 3. Restart on the SAME port pair: the page reconnects and the banner
    //    clears — no navigation, no reload, purely the heartbeat resuming.
    daemon <- IsolatedDaemon.respawn repoRoot daemon
    let! respawnHealthy = IsolatedDaemon.waitHealthy 60.0 daemon
    match respawnHealthy with
    | false ->
      IsolatedDaemon.dumpLogs daemon
      Tests.failtestf "respawned daemon on port %d never became healthy" daemon.McpPort
    | true -> ()
    let! reconnectedHidden = waitUntil TestTiming.reconnectWaitBudgetMs (fun () -> bannerHidden page)
    Expect.isTrue reconnectedHidden "banner cleared after the daemon reconnected (no reload)"
    let! reconnectedConnected = waitUntil 5_000 (fun () -> dataConnected page "true")
    Expect.isTrue reconnectedConnected "body[data-connected] flipped back to \"true\" after reconnect"

    // 4. Zero Datastar console errors across the whole journey (kill+respawn
    //    included — this is exactly the window the old fetch-monkeypatch
    //    mechanism silently failed in).
    let datastarErrors = consoleErrors |> Seq.filter (fun m -> m.ToLowerInvariant().Contains("datastar")) |> List.ofSeq
    Expect.isEmpty datastarErrors
      (sprintf "zero Datastar console errors across the journey, got: %s" (String.concat " | " datastarErrors))

    try do! ctx.CloseAsync() with _ -> ()
  finally
    // Disposing the Playwright driver terminates its browser process, so an
    // explicit (blocking) browser CloseAsync in this sync finally is redundant.
    playwright |> Option.iter (fun p -> try p.Dispose() with _ -> ())
    IsolatedDaemon.kill daemon
    try Directory.Delete(daemon.DataDir, true) with _ -> ()
}

[<Tests>]
let tests =
  testList "Dashboard disconnect-indicator browser tests" [
    testTask "[Integration] Dashboard disconnect indicator: kill/respawn journey" {
      do! disconnectIndicatorJourney () }
  ]
  |> Integration.register (Integration.Dedicated "--integration-disconnect")

/// Entry point for a dedicated CLI runner (mirrors
/// DashboardBrowserRunner.runBrowserJourneys / -HotReload / -LiveTesting).
/// Not wired into Program.fs by this island (out of file scope) — provided so
/// a future wiring change (or a direct call, as this island's own
/// verification used) has a stable, self-contained entry point.
let runDisconnectIndicatorJourney (cliArgs: string array) : int =
  let argv = cliArgs |> Array.filter (fun a -> a <> "--integration-disconnect")
  Tests.runTestsWithCLIArgs [] argv tests
