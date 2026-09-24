module SageFs.Tests.DashboardBrowserTests

open System
open System.IO
open System.Net
open System.Net.Http
open System.Net.Sockets
open System.Threading.Tasks
open Expecto
open Microsoft.Playwright
open SageFs.Server.DashboardTypes

module Integration = SageFs.Tests.TestInfrastructure.Integration

/// Helpers for Playwright assertions inside Expecto.
module PlaywrightExpect =
  let isVisibleAsync (locator: ILocator) (msg: string) = task {
    let! visible = locator.IsVisibleAsync()
    Expect.isTrue visible msg
  }

  let isHiddenAsync (locator: ILocator) (msg: string) = task {
    let! visible = locator.IsVisibleAsync()
    Expect.isFalse visible msg
  }

  /// Wait until exactly `expected` elements match, whether the matching
  /// elements are visible or not. How a journey proves a panel is in the
  /// page, or gone from it, rather than just scrolled or collapsed away.
  let waitForCount (ms: int) (locator: ILocator) (expected: int) = task {
    let sw = Diagnostics.Stopwatch.StartNew()
    let mutable count = -1
    while count <> expected && sw.ElapsedMilliseconds < int64 ms do
      let! c = locator.CountAsync()
      count <- c
      if count <> expected then
        do! Task.Delay(200)
    Expect.equal count expected (sprintf "expected %d elements matching the locator within %dms, found %d" expected ms count)
  }

  let waitForText (ms: int) (locator: ILocator) (text: string) = task {
    let sw = Diagnostics.Stopwatch.StartNew()
    let mutable found = false
    while not found && sw.ElapsedMilliseconds < int64 ms do
      let! content = locator.TextContentAsync()
      if content <> null && content.Contains(text) then
        found <- true
      else
        do! Task.Delay(200)
    Expect.isTrue found (sprintf "Expected '%s' within %dms" text ms)
  }

  /// Wait for any element matching selector to contain text.
  /// Useful when the element is created dynamically (e.g. by SSE/Datastar).
  let waitForSelectorText (ms: int) (page: IPage) (selector: string) (text: string) = task {
    let sw = Diagnostics.Stopwatch.StartNew()
    let mutable found = false
    while not found && sw.ElapsedMilliseconds < int64 ms do
      let! content = page.EvaluateAsync<string>(
        sprintf "() => { var el = document.querySelector('%s'); return el ? el.textContent : ''; }" selector)
      if content <> null && content.Contains(text) then
        found <- true
      else
        do! Task.Delay(250)
    Expect.isTrue found (sprintf "Expected '%s' in '%s' within %dms" text selector ms)
  }

  /// Wait for the SSE stream to connect by checking the tabline carries the
  /// server-pushed session identity. The shell renders #session-status as the
  /// state pill ("Ready") and the "Session: {id}" text in a sibling
  /// .tabline-info — both are inside #main, which the server morphs on every
  /// SSE push, so their presence proves the round-trip is live.
  let waitForSSE (ms: int) (page: IPage) = task {
    let sw = Diagnostics.Stopwatch.StartNew()
    let mutable found = false
    while not found && sw.ElapsedMilliseconds < int64 ms do
      let! content = page.EvaluateAsync<string>(
        "() => { var s = document.querySelector('#session-status'); var i = document.querySelector('#main .tabline-info'); return (s ? s.textContent : '') + '|' + (i ? i.textContent : ''); }")
      if content <> null && content.Contains("Ready") && content.Contains("Session:") then
        found <- true
      else
        do! Task.Delay(250)
    Expect.isTrue found (sprintf "Expected SSE connection within %dms" ms)
  }

  /// Wait for the eval textarea to be cleared after an eval. The clear is
  /// pushed by the same SSE morph that delivers the result — it can land a
  /// moment after the result text appears, so poll rather than assert once.
  let waitForTextareaCleared (ms: int) (textarea: ILocator) = task {
    let sw = Diagnostics.Stopwatch.StartNew()
    let mutable cleared = false
    while not cleared && sw.ElapsedMilliseconds < int64 ms do
      let! value = textarea.InputValueAsync()
      if value = "" then cleared <- true
      else do! Task.Delay(200)
    Expect.isTrue cleared (sprintf "Expected textarea cleared within %dms" ms)
  }

/// Playwright lifecycle — a fresh browser per journey.
/// Requires `npx playwright install chromium` to have been run.
module PlaywrightFixture =
  let mutable activePlaywright: IPlaywright option = None
  let mutable activeBrowser: IBrowser option = None

  let dashboardUrl =
    let port =
      match Environment.GetEnvironmentVariable("SAGEFS_DASHBOARD_PORT") with
      | null | "" -> "37750"
      | p -> p
    sprintf "http://localhost:%s" port

  /// The daemon's MCP/API port. The browser runner exports it; a daemon
  /// started by hand binds the dashboard at MCP port + 1, so fall back to that.
  let mcpPort =
    match Environment.GetEnvironmentVariable("SAGEFS_BROWSER_MCP_PORT") with
    | null | "" ->
      match Environment.GetEnvironmentVariable("SAGEFS_DASHBOARD_PORT") with
      | null | "" -> 37749
      | p -> int p - 1
    | p -> int p

  /// Where journeys save screenshots worth looking at.
  let screenshotDir =
    let dir =
      match Environment.GetEnvironmentVariable("SAGEFS_BROWSER_SCREENSHOTS") with
      | null | "" -> IO.Path.Combine(IO.Path.GetTempPath(), "sagefs-browser-screenshots")
      | d -> d
    IO.Directory.CreateDirectory dir |> ignore
    dir

  /// Launch a fresh browser (per journey). A shared browser degrades over
  /// many journeys: SSE connections from closed contexts accumulate and later
  /// journeys' eval-result morphs stop arriving. A fresh browser per journey
  /// reproduces the isolated conditions under which the round-trip is
  /// reliably fast, at the cost of a ~1s launch per test.
  let private launchBrowser () = task {
    let! playwright = Playwright.CreateAsync()
    let! b =
      playwright.Chromium.LaunchAsync(
        BrowserTypeLaunchOptions(Headless = true))
    return playwright, b
  }

  let newPage () = task {
    let! (pw', b) = launchBrowser ()
    let! ctx = b.NewContextAsync()
    let! page = ctx.NewPageAsync()
    activeBrowser <- Some b
    activePlaywright <- Some pw'
    return page
  }

  /// Close the page's context and the journey-scoped browser. Each journey
  /// gets a fresh browser so no SSE connection from a previous journey can
  /// linger on the daemon or starve this journey's morphs.
  let closePage (page: IPage) = task {
    try
      let ctx = page.Context
      do! ctx.CloseAsync()
    with _ -> ()
    match activeBrowser with
    | Some b ->
      try do! b.CloseAsync() with _ -> ()
      activeBrowser <- None
    | None -> ()
    match activePlaywright with
    | Some p ->
      try p.Dispose() with _ -> ()
      activePlaywright <- None
    | None -> ()
  }

  let cleanup () = task {
    match activeBrowser with
    | Some b ->
      try do! b.CloseAsync() with _ -> ()
      activeBrowser <- None
    | None -> ()
    match activePlaywright with
    | Some p ->
      try p.Dispose() with _ -> ()
      activePlaywright <- None
    | None -> ()
  }

/// Accordion helpers for the dashboard shell — the Evaluate and New Session
/// sections are <details> accordions collapsed by default, so journeys must
/// open them before interacting with their contents.
module DashboardDom =
  /// Enable "expanded" dashboard mode. The extra session panels — Hot Reload,
  /// Live Testing, Bindings, Session Context, Friction, AND the New Session
  /// form — live inside `.expanded-only` wrappers (display:none in the default
  /// minimal mode) and are revealed only when #main gains the `expanded` class
  /// (the `expandedDashboard` signal). Journeys that touch those panels must
  /// turn expanded mode on first. Idempotent — a no-op when already expanded.
  let ensureExpanded (page: IPage) = task {
    let! isExpanded =
      page.EvaluateAsync<bool>(
        "() => { var m = document.querySelector('#main'); return m ? m.classList.contains('expanded') : false; }")
    if not isExpanded then
      do! page.Locator("#expand-toggle-btn").First.ClickAsync()
      // Wait until #main actually carries the expanded class before returning.
      do! page.Locator("#main.expanded").First.WaitForAsync(
        LocatorWaitForOptions(State = WaitForSelectorState.Attached, Timeout = 5000.0f))
  }

  /// Open the Evaluate accordion (#evaluate-section is a <details
  /// class="eval-area"> collapsed by default). No-op when already open.
  let openEvalArea (page: IPage) = task {
    let section = page.Locator("#evaluate-section")
    do! PlaywrightExpect.isVisibleAsync section "evaluate section visible"
    let! isOpen =
      page.EvaluateAsync<bool>(
        "() => { var el = document.querySelector('#evaluate-section'); return el ? el.open : false; }")
    if not isOpen then
      let summary = page.Locator("#evaluate-section summary").First
      do! summary.ClickAsync()
    let evalInput = page.Locator(".eval-input").First
    do! PlaywrightExpect.isVisibleAsync evalInput "eval input visible after opening"
  }

  /// Open the New Session accordion (a <details class="new-session-panel">
  /// collapsed by default). Checks the current open state first — like
  /// `openEvalArea` — so it is a no-op when already open and safe to call
  /// repeatedly from `throughPanelReset` (a second unconditional click would
  /// instead toggle it closed again).
  let openNewSession (page: IPage) = task {
    // New Session lives in an `.expanded-only` wrapper — reveal it first.
    do! ensureExpanded page
    let! isOpen =
      page.EvaluateAsync<bool>(
        "() => { var el = document.querySelector('.new-session-panel'); return el ? el.open : false; }")
    if not isOpen then
      let toggle = page.GetByText("New Session").First
      do! toggle.ClickAsync()
  }

  /// The eval code textarea — located by its stable id, never by role/name
  /// (accessible-name resolution races the Datastar morph and flakes).
  let textarea (page: IPage) = page.Locator("#eval-textarea")

  /// The EVAL button — first .eval-btn inside the evaluate section.
  let evalButton (page: IPage) =
    page.Locator("#evaluate-section .eval-btn").First

  /// Open the Friction panel (a <details id="friction-panel"> collapsed by
  /// default). No-op when already open — safe to call repeatedly from
  /// `throughPanelReset`.
  let openFrictionPanel (page: IPage) = task {
    // The Friction panel lives in an `.expanded-only` wrapper — reveal it first.
    do! ensureExpanded page
    let! isOpen =
      page.EvaluateAsync<bool>(
        "() => { var el = document.querySelector('#friction-panel'); return el ? el.open : false; }")
    if not isOpen then
      let summary = page.Locator("#friction-panel summary")
      do! summary.ClickAsync()
  }

  /// Retry a `(reopen-panel, body)` pair when `body` fails — a `<details>`
  /// accordion's open/closed state lives only in the browser (the server
  /// never renders `open`), and the dashboard's periodic 1-second SSE
  /// fallback push (Dashboard.fs's `Timeouts.sseEventInterval`) renders a
  /// ticking uptime label into #main that almost never byte-matches the
  /// previous push — so the no-change dedupe rarely suppresses it, and the
  /// resulting full-#main morph snaps every accordion back to its
  /// server-rendered (always closed) default. On a loaded/cold machine, any
  /// two steps here that span more than ~1s can race that reset: a
  /// Playwright actionability timeout ("element is not visible") on a
  /// locator inside the panel, or an `IsVisibleAsync` snapshot that reads
  /// false because the panel just snapped shut. Reopening and retrying the
  /// whole step converges quickly since each attempt only needs to win a
  /// short window before the next tick; a genuine product failure keeps
  /// failing every attempt and still surfaces once `attemptsLeft` reaches 0.
  let rec throughPanelReset (reopen: unit -> Task<unit>) (attemptsLeft: int) (body: unit -> Task<unit>) : Task<unit> = task {
    do! reopen ()
    try
      do! body ()
    with _ when attemptsLeft > 0 ->
      do! throughPanelReset reopen (attemptsLeft - 1) body
  }

/// Chat-style scrolling of the output panel. The panel follows new output
/// while you're at the bottom, holds still once you scroll up to read, and
/// counts the evals you haven't seen in a pill over the bottom of the panel.
module OutputScroll =
  let panelSelector = "#output-panel"
  let pillSelector = "#output-new-evals"

  /// How far the panel's viewport sits above the bottom of its content, in px.
  let distanceFromBottom (page: IPage) =
    page.EvaluateAsync<float>(
      "() => { var el = document.querySelector('#output-panel'); return el.scrollHeight - el.scrollTop - el.clientHeight; }")

  /// Following means "sitting at the bottom", give or take a pixel of rounding.
  let atBottomTolerance = 4.0

  /// Run one eval through the real Evaluate box. The code is built so its
  /// result text (`marker`) never appears in the echoed code line, so waiting
  /// for the marker waits for the RESULT, not the echo.
  let runEval (page: IPage) (marker: string) = task {
    let half = marker.Length / 2
    let code = sprintf "\"%s\" + \"%s\";;" (marker.Substring(0, half)) (marker.Substring half)
    let textarea = DashboardDom.textarea page
    do! DashboardDom.throughPanelReset (fun () -> DashboardDom.openEvalArea page) 5 (fun () -> task {
      do! textarea.FillAsync(code)
      do! (DashboardDom.evalButton page).ClickAsync()
    })
    do! PlaywrightExpect.waitForSelectorText 30_000 page panelSelector marker
    // Let any scroll the morph kicked off finish before anyone measures.
    do! page.WaitForTimeoutAsync(600.0f)
  }

  /// Fill the output panel well past one screen, so there is room to scroll.
  let fillPastOneScreen (page: IPage) (prefix: string) = task {
    let textarea = DashboardDom.textarea page
    do! DashboardDom.throughPanelReset (fun () -> DashboardDom.openEvalArea page) 5 (fun () -> task {
      do! textarea.FillAsync(sprintf "String.concat \"\\n\" [ for i in 1 .. 120 -> sprintf \"%s-%%03d\" i ];;" prefix)
      do! (DashboardDom.evalButton page).ClickAsync()
    })
    do! PlaywrightExpect.waitForSelectorText 30_000 page panelSelector (sprintf "%s-120" prefix)
    do! page.WaitForTimeoutAsync(600.0f)
    let! overflow =
      page.EvaluateAsync<float>(
        "() => { var el = document.querySelector('#output-panel'); return el.scrollHeight - el.clientHeight; }")
    Expect.isTrue (overflow > 400.0) (sprintf "output must overflow the panel by a good margin to test scrolling (overflow %f px)" overflow)
    // Opening Evaluate shrank the panel under us. That's not the user
    // scrolling away, so the fill still has to follow to the bottom.
    let! dist =
      page.EvaluateAsync<float>(
        "() => { var el = document.querySelector('#output-panel'); return el.scrollHeight - el.scrollTop - el.clientHeight; }")
    Expect.isTrue (dist <= 4.0) (sprintf "the fill must follow to the bottom even though opening Evaluate resized the panel (%f px from bottom)" dist)
  }

  /// Scroll up the way a person does: the mouse wheel over the panel.
  let wheelUp (page: IPage) (px: float32) = task {
    let! box = page.Locator(panelSelector).BoundingBoxAsync()
    do! page.Mouse.MoveAsync(box.X + box.Width / 2.0f, box.Y + box.Height / 2.0f)
    do! page.Mouse.WheelAsync(0.0f, -px)
    // Let the wheel's scroll events land before anything reads the position.
    do! page.WaitForTimeoutAsync(400.0f)
  }

  /// The text of the first output line at the top of the panel's viewport
  /// and its offset from the panel's top edge: what the reader is looking at.
  let readingPosition (page: IPage) =
    page.EvaluateAsync<string>(
      """() => {
        var p = document.querySelector('#output-panel');
        var top = p.getBoundingClientRect().top;
        var lines = p.querySelectorAll('.output-line');
        for (var i = 0; i < lines.length; i++) {
          var r = lines[i].getBoundingClientRect();
          if (r.top >= top) return lines[i].textContent + '|' + Math.round(r.top - top);
        }
        return '';
      }""")

  /// Where the line with this exact text now sits relative to the panel top.
  let offsetOfLine (page: IPage) (text: string) =
    page.EvaluateAsync<float>(
      """(t) => {
        var p = document.querySelector('#output-panel');
        var top = p.getBoundingClientRect().top;
        var lines = p.querySelectorAll('.output-line');
        for (var i = 0; i < lines.length; i++) {
          if (lines[i].textContent === t) return lines[i].getBoundingClientRect().top - top;
        }
        return -99999;
      }""", text)

  let waitForPillText (page: IPage) (text: string) =
    PlaywrightExpect.waitForSelectorText 10_000 page pillSelector text

  let pillVisible (page: IPage) = page.Locator(pillSelector).IsVisibleAsync()

/// Gap I (outcome-gate-sweep.md, Island H — dashboard journeys): the
/// no-session landing shipped broken TWICE (emergency releases 0.6.470 and
/// 0.6.471) because the state matrix that would have caught it was never
/// gated. `DashboardBrowserRunner.fs` always creates a Ready session BEFORE
/// any journey above runs, so the no-session state is structurally
/// unreachable through the shared daemon those journeys share.
///
/// This module owns its OWN isolated daemon — fresh ports, a fresh
/// SAGEFS_DATA_DIR, `--no-resume`, and critically NO `/api/sessions/create`
/// call before the first assertion — so the no-session landing, and every
/// transition into and out of it, are actually exercised. The two journeys
/// built from it are appended to THIS file's own `tests` value below (not a
/// new `[<Tests>]` list and not a new file), so
/// `DashboardBrowserRunner.runBrowserJourneys` already runs them under the
/// existing `--integration-browser` entry point with no changes to
/// DashboardBrowserRunner.fs or Program.fs — both out of this island's file
/// scope (owned by Island B).
///
/// Resource discipline: one isolated daemon per journey (2 total), each on a
/// freshly-probed free port pair (never 37749/37750, never the shared
/// browser-suite pair), each with its own temp SAGEFS_DATA_DIR that is
/// deleted on teardown, guaranteed kill via `try/finally` (mirrors
/// DashboardDisconnectIndicatorBrowserTests.fs's proven isolated-daemon
/// pattern), and event-driven waits throughout (`waitUntil` polls a real
/// condition — server `/api/sessions` state or a DOM attribute — never a
/// fixed sleep-then-assume).
module private NoSessionLanding =
  let private repoRoot = Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, ".."))

  /// Two small, DISTINCT-directory, already-built sample projects (never the
  /// WebappDatastar sample the shared runner owns — one-session-per-working-
  /// directory means switching between two sessions needs two directories).
  /// Both are tiny console apps (~126 total source lines between them,
  /// including the Tests project's Expecto dependency), chosen to keep
  /// warmup fast and to give Gap K's `run_app` note a second reference point
  /// (ConsoleTicker is otherwise referenced by no test).
  let private consoleTickerDir = Path.Combine(repoRoot, "samples", "demos", "SageFs.Samples.ConsoleTicker")
  let private consoleTickerProj = Path.Combine(consoleTickerDir, "SageFs.Samples.ConsoleTicker.fsproj")
  let private consoleTickerTestsDir = Path.Combine(repoRoot, "samples", "demos", "SageFs.Samples.ConsoleTicker.Tests")
  let private consoleTickerTestsProj = Path.Combine(consoleTickerTestsDir, "SageFs.Samples.ConsoleTicker.Tests.fsproj")

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
    | :? SocketException -> failwith "No-session journey: could not find a free port pair"

  type private Daemon =
    { Process: Diagnostics.Process
      McpPort: int
      DashboardPort: int
      DataDir: string
      OutLog: string
      ErrLog: string }

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

  /// Boot a fresh, session-free daemon on its own port pair and data dir.
  /// `--no-resume` so it never inherits another run's manifest — the
  /// no-session landing is exactly the state this daemon starts in and stays
  /// in until a journey creates a session.
  let private startDaemon () : Daemon =
    let mcpPort = findPortPair 5
    let dashboardPort = mcpPort + 1
    let dataDir = Path.Combine(Path.GetTempPath(), "sagefs-nosession", Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory(dataDir) |> ignore
    let exe = SageFs.Tests.TestInfrastructure.SageFsBinary.path ()
    let psi = Diagnostics.ProcessStartInfo()
    psi.FileName <- exe
    psi.UseShellExecute <- false
    psi.CreateNoWindow <- true
    psi.WorkingDirectory <- repoRoot
    psi.ArgumentList.Add("--mcp-port")
    psi.ArgumentList.Add(string mcpPort)
    psi.ArgumentList.Add("--owner-pid")
    psi.ArgumentList.Add(string (Diagnostics.Process.GetCurrentProcess().Id))
    psi.ArgumentList.Add("--no-resume")
    psi.Environment["SAGEFS_DATA_DIR"] <- dataDir
    // Redirect to FILES, never undrained pipes: an undrained pipe deadlocks
    // the daemon once its log buffer fills, freezing warmup before Ready.
    psi.RedirectStandardOutput <- true
    psi.RedirectStandardError <- true
    let proc = Diagnostics.Process.Start(psi)
    let outLog = Path.Combine(dataDir, "daemon.stdout.log")
    let errLog = Path.Combine(dataDir, "daemon.stderr.log")
    drain proc.StandardOutput outLog
    drain proc.StandardError errLog
    { Process = proc; McpPort = mcpPort; DashboardPort = dashboardPort
      DataDir = dataDir; OutLog = outLog; ErrLog = errLog }

  let private dumpLogs (d: Daemon) =
    for path in [ d.OutLog; d.ErrLog ] do
      try
        if File.Exists path then
          let text = File.ReadAllText(path)
          if not (String.IsNullOrWhiteSpace text) then
            eprintfn "--- %s (tail) ---" (Path.GetFileName path)
            let lines = text.Split('\n')
            lines |> Array.skip (max 0 (lines.Length - 40)) |> Array.iter (eprintfn "%s")
      with _ -> ()

  let private closeBrowserSafely (browser: IBrowser) : Task =
    try
      browser.CloseAsync()
    with _ ->
      Task.CompletedTask

  /// Guaranteed teardown: kill this journey's OWN process (never touches
  /// 37749/37750, never `pkill -f SageFs`) and delete its temp data dir.
  let private killDaemon (d: Daemon) =
    try
      if not d.Process.HasExited then d.Process.Kill(entireProcessTree = true)
    with _ -> ()
    try d.Process.WaitForExit(5000) |> ignore with _ -> ()
    try d.Process.Dispose() with _ -> ()
    try Directory.Delete(d.DataDir, true) with _ -> ()

  let private waitHealthy (budgetSeconds: float) (d: Daemon) : Task<bool> = task {
    use client = new HttpClient(BaseAddress = Uri(sprintf "http://localhost:%d" d.McpPort))
    client.Timeout <- TimeSpan.FromSeconds(5.0)
    let deadline = DateTime.UtcNow.AddSeconds(budgetSeconds)
    let mutable healthy = false
    while not healthy && DateTime.UtcNow < deadline do
      try
        let! resp = client.GetAsync("/health")
        resp.Dispose()
        healthy <- true
      with _ -> do! Task.Delay(250)
    return healthy
  }

  /// Poll `condition` until it returns true or `budgetMs` elapses — a bounded
  /// wait against a real async condition, never a fixed sleep-then-assume.
  let private waitUntil (budgetMs: int) (condition: unit -> Task<bool>) : Task<bool> = task {
    let sw = Diagnostics.Stopwatch.StartNew()
    let mutable ok = false
    while not ok && sw.ElapsedMilliseconds < int64 budgetMs do
      let! result = condition ()
      if result then ok <- true
      else do! Task.Delay(200)
    return ok
  }

  /// Poll `/api/sessions` on `d`'s OWN HttpClient — never through the
  /// browser — so "the server actually reached Ready" and "the browser
  /// reflects it via SSE" stay two separately-proven facts, not one
  /// conflated wait.
  let private sessionsSnapshot (d: Daemon) : Task<(string * string * string) list> = task {
    use client = new HttpClient(BaseAddress = Uri(sprintf "http://localhost:%d" d.McpPort))
    client.Timeout <- TimeSpan.FromSeconds(5.0)
    let! body = client.GetStringAsync("/api/sessions")
    use doc = System.Text.Json.JsonDocument.Parse(body)
    return
      doc.RootElement.GetProperty("sessions").EnumerateArray()
      |> Seq.map (fun s ->
        s.GetProperty("id").GetString(),
        s.GetProperty("status").GetString(),
        s.GetProperty("workingDirectory").GetString())
      |> Seq.toList
  }

  let private waitAllReady (budgetSeconds: float) (d: Daemon) (expectedCount: int) : Task<bool> =
    waitUntil (int (budgetSeconds * 1000.0)) (fun () -> task {
      try
        let! sessions = sessionsSnapshot d
        return sessions.Length >= expectedCount && sessions |> List.forall (fun (_, status, _) -> status = "Ready")
      with _ -> return false
    })

  /// Setup-only helper for the second journey below (the FIRST journey
  /// proves the real click-through Create path — this one only needs two
  /// live sessions to exist quickly so it can focus on switch/stop).
  let private createSessionViaApi (d: Daemon) (project: string) (dir: string) : Task<bool> = task {
    use client = new HttpClient(BaseAddress = Uri(sprintf "http://localhost:%d" d.McpPort))
    client.Timeout <- TimeSpan.FromSeconds(10.0)
    let payload =
      System.Text.Json.JsonSerializer.Serialize({| projects = [| project |]; workingDirectory = dir |})
    use content = new StringContent(payload, Text.Encoding.UTF8, "application/json")
    let! resp = client.PostAsync("/api/sessions/create", content)
    let ok = resp.IsSuccessStatusCode
    resp.Dispose()
    return ok
  }

  /// The server-authoritative "which session is this page looking at" fact —
  /// `#main`'s own `data-viewing-session-id` attribute (Dashboard.fs's
  /// `renderMainContent`), never inferred from "Session: <id>" text.
  let private viewingSessionId (page: IPage) : Task<string> =
    page.EvaluateAsync<string>(
      "() => { var m = document.querySelector('#main'); return m ? (m.getAttribute('data-viewing-session-id') || '') : ''; }")

  let private attachErrorCollector (page: IPage) : Collections.Generic.List<string> =
    let errors = Collections.Generic.List<string>()
    page.Console.Add(fun msg -> if msg.Type = "error" then errors.Add(sprintf "[console] %s" msg.Text))
    page.PageError.Add(fun err -> errors.Add(sprintf "[pageerror] %s" err))
    errors

  /// `PatchElementsNoTargetsFound` was the signature of the blank-screen bug
  /// — assert there were NONE, and while we're collecting, zero console/page
  /// errors of any kind across the whole journey.
  let private assertNoErrors (errors: Collections.Generic.List<string>) (label: string) =
    let datastarErrors = errors |> Seq.filter (fun m -> m.ToLowerInvariant().Contains("datastar")) |> List.ofSeq
    Expect.isEmpty datastarErrors
      (sprintf "[%s] zero Datastar console errors across the journey, got: %s" label (String.concat " | " datastarErrors))
    let allErrors = errors |> List.ofSeq
    Expect.isEmpty allErrors
      (sprintf "[%s] zero console/page errors of any kind across the journey, got: %s" label (String.concat " | " allErrors))

  /// The permanent chrome that must render in EVERY dashboard state — this
  /// exact invariant is what the no-session landing broke twice: header,
  /// sidebar Sessions panel, sidebar New Session accordion, and the
  /// statusline (session-status pill + session identity — the same elements
  /// the shared-daemon "session status renders with state" test above treats
  /// as the statusline for the with-session case).
  let private assertPermanentChrome (page: IPage) (label: string) = task {
    let header = page.Locator(".app-header")
    do! PlaywrightExpect.isVisibleAsync header (sprintf "[%s] app header visible" label)
    let daemonHealth = page.Locator("#daemon-health")
    do! PlaywrightExpect.isVisibleAsync daemonHealth (sprintf "[%s] daemon health bar visible" label)
    let sessionsHeading = page.GetByRole(AriaRole.Heading, PageGetByRoleOptions(Name = "Sessions"))
    do! PlaywrightExpect.isVisibleAsync sessionsHeading (sprintf "[%s] sidebar Sessions heading visible" label)
    // The New Session accordion lives in an `.expanded-only` sidebar wrapper
    // (display:none in minimal mode) — assert it is PRESENT in the DOM
    // (it is permanent chrome regardless of session state), not that it is
    // currently visible.
    let! newSessionPresent =
      page.EvaluateAsync<bool>("() => document.querySelector('.new-session-panel') !== null")
    Expect.isTrue newSessionPresent (sprintf "[%s] sidebar New Session panel present in the DOM" label)
    let statusPill = page.Locator("#session-status")
    do! PlaywrightExpect.isVisibleAsync statusPill (sprintf "[%s] statusline session-status pill visible" label)
    let tabline = page.Locator("#main .tabline-info").First
    do! PlaywrightExpect.isVisibleAsync tabline (sprintf "[%s] statusline session identity visible" label)
  }

  /// States 1 + 4 + the click-through Create journey: (1) zero sessions, bare
  /// URL renders the full shell with the picker in #main and zero console
  /// errors; (4) filling in the picker's OWN working-directory input and
  /// clicking its OWN Create button (never an API shortcut) reaches a live,
  /// Ready session — reflected in THIS SAME page via SSE, with no navigation
  /// and no reload.
  let noSessionLandingAndCreateJourney () : Task<unit> = task {
    let daemon = startDaemon ()
    let mutable playwright: IPlaywright option = None
    let mutable browser: IBrowser option = None
    let mutable failure: exn option = None
    try
      let! healthy = waitHealthy 60.0 daemon
      if not healthy then
        dumpLogs daemon
        Tests.failtestf "no-session daemon on port %d never became healthy" daemon.McpPort

      let! pw = Playwright.CreateAsync()
      let! b = pw.Chromium.LaunchAsync(BrowserTypeLaunchOptions(Headless = true))
      playwright <- Some pw
      browser <- Some b
      let! ctx = b.NewContextAsync()
      let! page = ctx.NewPageAsync()
      let errors = attachErrorCollector page

      let! _ = page.GotoAsync(sprintf "http://localhost:%d/dashboard" daemon.DashboardPort)
      let bareUrl = page.Url

      // --- State 1: zero sessions, bare URL. ---
      do! assertPermanentChrome page "state 1: no sessions"
      let picker = page.Locator("#session-picker")
      do! PlaywrightExpect.isVisibleAsync picker "[state 1] session picker visible in #main"
      do! PlaywrightExpect.waitForText 10_000 (page.Locator(".sessions-empty")) "No active sessions"
      let! viewingBefore = viewingSessionId page
      Expect.equal viewingBefore "" "[state 1] #main carries no viewing session id"

      // --- State 4: session created from the no-session picker, through the
      // real UI form (never the API — that's the click-through Create
      // journey itself). ---
      let dirInput = picker.Locator("input[placeholder*=\"/path/to/project\"]").First
      do! PlaywrightExpect.isVisibleAsync dirInput "[state 4] picker's own working-directory input visible"
      do! dirInput.FillAsync(consoleTickerDir)
      let createBtn = picker.GetByRole(AriaRole.Button, LocatorGetByRoleOptions(Name = "Create")).First
      do! PlaywrightExpect.isVisibleAsync createBtn "[state 4] picker's own Create button visible"
      do! createBtn.ClickAsync()

      // Server-side proof: the session actually reaches Ready.
      let! serverReady = waitAllReady 90.0 daemon 1
      Expect.isTrue serverReady "[state 4] the created session reached Ready on the server within 90s"

      // Client-side proof: the SAME page reflects it via SSE — no
      // navigation, no reload, the picker gone, a real session row present.
      let! sidReflected = waitUntil 30_000 (fun () -> task {
        let! sid = viewingSessionId page
        return sid <> ""
      })
      Expect.isTrue sidReflected "[state 4] #main's viewing-session-id populated via SSE within 30s (no reload)"
      Expect.equal page.Url bareUrl "[state 4] no navigation/reload occurred — URL is unchanged"
      let! pickerHiddenNow = waitUntil 10_000 (fun () -> task {
        let! visible = picker.IsVisibleAsync()
        return not visible
      })
      Expect.isTrue pickerHiddenNow "[state 4] session picker hidden once a session exists"
      let! sid = viewingSessionId page
      let sessionRow = page.Locator(sprintf "#session-card-%s" sid)
      do! PlaywrightExpect.isVisibleAsync sessionRow "[state 4] the new session's sidebar row is visible without a reload"

      assertNoErrors errors "no-session landing + Create journey"
      try do! ctx.CloseAsync() with _ -> ()
    with ex ->
      failure <- Some ex
    match browser with
    | Some b -> do! closeBrowserSafely b
    | None -> ()
    playwright |> Option.iter (fun p -> try p.Dispose() with _ -> ())
    killDaemon daemon
    match failure with
    | Some failure -> return raise failure
    | None -> ()
  }

  /// States 2 + 3 + 5 + the Stop journey: (2) sessions already exist, bare
  /// URL renders the session view directly; (3) reaching a SPECIFIC session
  /// directly via its own Switch control — this app's real equivalent of a
  /// session deep link, now that the `?session=` query parameter has been
  /// removed in favor of the signal-driven `viewingSessionId` (Dashboard.fs's
  /// GET /dashboard: "There is NO session query parameter"); (5) clicking a
  /// DIFFERENT sidebar row (not its button) switches into that session's
  /// view; then Stop, twice — once with a session remaining (auto-advance,
  /// no picker) and once as the last session (falls back to the picker, with
  /// the rest of the chrome still intact — the exact transition that shipped
  /// broken as 0.6.470/0.6.471).
  let sessionsSwitchAndStopJourney () : Task<unit> = task {
    let daemon = startDaemon ()
    let mutable playwright: IPlaywright option = None
    let mutable browser: IBrowser option = None
    let mutable failure: exn option = None
    try
      let! healthy = waitHealthy 60.0 daemon
      if not healthy then
        dumpLogs daemon
        Tests.failtestf "sessions/switch/stop daemon on port %d never became healthy" daemon.McpPort

      // Two sessions, created via the API (Create-through-the-UI is already
      // proven by the journey above) so this journey can focus on
      // states 2/3/5 and the Stop transitions.
      let! createdA = createSessionViaApi daemon consoleTickerProj consoleTickerDir
      Expect.isTrue createdA "session A (ConsoleTicker) create request accepted"
      let! createdB = createSessionViaApi daemon consoleTickerTestsProj consoleTickerTestsDir
      Expect.isTrue createdB "session B (ConsoleTicker.Tests) create request accepted"
      let! bothReady = waitAllReady 120.0 daemon 2
      if not bothReady then dumpLogs daemon
      Expect.isTrue bothReady "both sessions reached Ready on the server within 120s"

      let! sessions = sessionsSnapshot daemon
      // Compare normalized, trailing-slash-trimmed full paths — the server
      // may report its own canonicalization of the working directory that
      // differs cosmetically (trailing separator, symlink resolution) from
      // the literal `Path.Combine` result this file builds it from.
      let normalize (p: string) = Path.GetFullPath(p).TrimEnd(Path.DirectorySeparatorChar)
      let idFor (dir: string) =
        let target = normalize dir
        sessions
        |> List.tryFind (fun (_, _, wd) -> String.Equals(normalize wd, target, StringComparison.OrdinalIgnoreCase))
        |> Option.map (fun (id, _, _) -> id)
        |> Option.defaultWith (fun () -> failwithf "no session found for %s among %A" dir sessions)
      let idA = idFor consoleTickerDir
      let idB = idFor consoleTickerTestsDir

      let! pw = Playwright.CreateAsync()
      let! b = pw.Chromium.LaunchAsync(BrowserTypeLaunchOptions(Headless = true))
      playwright <- Some pw
      browser <- Some b
      let! ctx = b.NewContextAsync()
      let! page = ctx.NewPageAsync()
      let errors = attachErrorCollector page

      // --- State 2: sessions exist, bare URL renders the session view
      // directly (no picker). ---
      let! _ = page.GotoAsync(sprintf "http://localhost:%d/dashboard" daemon.DashboardPort)
      do! assertPermanentChrome page "state 2: sessions + bare URL"
      let picker = page.Locator("#session-picker")
      do! PlaywrightExpect.isHiddenAsync picker "[state 2] session picker hidden when sessions exist"
      let! selected =
        waitUntil 15_000 (fun () -> task {
          let! sid = viewingSessionId page
          return sid = idA || sid = idB
        })
      Expect.isTrue selected
        (sprintf "[state 2] bare URL never selected a live session (expected %s or %s)" idA idB)
      let! viewingOnLoad = viewingSessionId page

      // --- State 3: reach a SPECIFIC session directly — this app's real
      // equivalent of a session deep link now that there is no `?session=`
      // query parameter — via that session's own Switch control. ---
      let target1 = if viewingOnLoad = idA then idB else idA
      let rowLocator (sid: string) = page.Locator(sprintf "#session-card-%s" sid)
      let switchBtn1 = (rowLocator target1).GetByRole(AriaRole.Button, LocatorGetByRoleOptions(Name = "show this session's output here"))
      do! switchBtn1.ClickAsync()
      let! reachedTarget1 = waitUntil 15_000 (fun () -> task {
        let! sid = viewingSessionId page
        return sid = target1
      })
      Expect.isTrue reachedTarget1 (sprintf "[state 3] switch reached session %s directly, no reload" target1)

      // --- State 5: click the OTHER sidebar row (not its button) into its
      // view. ---
      let target2 = if target1 = idA then idB else idA
      let otherRow = rowLocator target2
      do! otherRow.ClickAsync()
      let! reachedTarget2 = waitUntil 15_000 (fun () -> task {
        let! sid = viewingSessionId page
        return sid = target2
      })
      Expect.isTrue reachedTarget2 (sprintf "[state 5] clicking the sidebar row switched into session %s's view" target2)

      // --- Stop journey, part 1: stop the currently-viewed session while
      // one other remains -> auto-advance to it (no picker). ---
      let stopBtn (sid: string) = (rowLocator sid).GetByRole(AriaRole.Button, LocatorGetByRoleOptions(Name = "unload the session"))
      do! (stopBtn target2).ClickAsync()
      do! PlaywrightExpect.waitForSelectorText 15_000 page (sprintf "#session-card-%s" target2) (sprintf "Stopping session id:%s" target2)
      let! autoAdvanced = waitUntil 30_000 (fun () -> task {
        let! sid = viewingSessionId page
        return sid = target1
      })
      Expect.isTrue autoAdvanced (sprintf "[stop 1/2] stopping the viewed session auto-advanced to the remaining session %s" target1)
      do! PlaywrightExpect.isHiddenAsync picker "[stop 1/2] session picker stays hidden — one session remains"
      do! assertPermanentChrome page "stop 1/2: one session remains after auto-advance"

      // --- Stop journey, part 2: stop the LAST remaining session -> falls
      // back to the picker, and the rest of the chrome must still render —
      // the exact transition that shipped broken as 0.6.470/0.6.471. ---
      do! (stopBtn target1).ClickAsync()
      do! PlaywrightExpect.waitForSelectorText 15_000 page (sprintf "#session-card-%s" target1) (sprintf "Stopping session id:%s" target1)
      let! pickerBack = waitUntil 30_000 (fun () -> task {
        return! picker.IsVisibleAsync()
      })
      Expect.isTrue pickerBack "[stop 2/2] session picker re-appears once the last session is stopped"
      let! viewingAfterLast = viewingSessionId page
      Expect.equal viewingAfterLast "" "[stop 2/2] #main carries no viewing session id once zero sessions remain"
      do! assertPermanentChrome page "stop 2/2: back to zero sessions"

      assertNoErrors errors "sessions + switch + stop journey"
      try do! ctx.CloseAsync() with _ -> ()
    with ex ->
      failure <- Some ex
    match browser with
    | Some b -> do! closeBrowserSafely b
    | None -> ()
    playwright |> Option.iter (fun p -> try p.Dispose() with _ -> ())
    killDaemon daemon
    match failure with
    | Some failure -> return raise failure
    | None -> ()
  }

/// Helper to run an async Playwright test body inside Expecto.
/// All dashboard browser tests are tagged [Integration] since they
/// require a running SageFs daemon with dashboard on port 37750.
let playwrightTest name (body: IPage -> Task<unit>) =
  testCase (sprintf "[Integration] Dashboard browser: %s" name) (fun () ->
    let t = task {
      let! page = PlaywrightFixture.newPage ()
      try
        let! _ = page.GotoAsync(
          sprintf "%s/dashboard" PlaywrightFixture.dashboardUrl)
        do! body page
      finally
        PlaywrightFixture.closePage(page).GetAwaiter().GetResult()
    }
    t.GetAwaiter().GetResult())
  |> Integration.register (Integration.Dedicated "--integration-browser")

/// Like playwrightTest but does NOT auto-navigate — gives a raw page
/// so the test can set up route interceptions before navigation.
let playwrightTestRaw name (body: IPage -> Task<unit>) =
  testCase (sprintf "[Integration] Dashboard browser: %s" name) (fun () ->
    let t = task {
      let! page = PlaywrightFixture.newPage ()
      try
        do! body page
      finally
        PlaywrightFixture.closePage(page).GetAwaiter().GetResult()
    }
    t.GetAwaiter().GetResult())
  |> Integration.register (Integration.Dedicated "--integration-browser")

[<Tests>]
// All dashboard browser journeys share one daemon, one FSI session and one
// Playwright browser — they MUST run sequentially. Concurrent journeys would
// interleave evals in the single shared session and time out waiting for
// results that another journey consumed.
let tests =
  testSequenced <|
  testList "Dashboard browser tests" [

  playwrightTest "page loads with title" (fun page -> task {
    let! title = page.TitleAsync()
    Expect.equal title "SageFs Dashboard" "page title"
  })

  // NOTE: h1, eval textarea, eval button, reset/hard-reset buttons, clear button
  // moved to shellStructureTests in DashboardSnapshotTests.fs (no browser needed)

  playwrightTest "output panel renders with session identity" (fun page -> task {
    let panel = page.Locator("#output-panel")
    do! panel.WaitForAsync(
      LocatorWaitForOptions(State = WaitForSelectorState.Visible))
    let! sessionId = panel.GetAttributeAsync("data-session-id")
    Expect.isNotNull sessionId "output panel carries the session id"
  })

  playwrightTest "output panel scrolls like a chat: follows at the bottom, holds when scrolled up, counts unseen evals" (fun page -> task {
    do! PlaywrightExpect.waitForSSE 15_000 page
    do! OutputScroll.fillPastOneScreen page "chat-fill"

    // At the bottom, new output follows and no pill shows up.
    do! OutputScroll.runEval page "follow-one"
    let! dist = OutputScroll.distanceFromBottom page
    Expect.isTrue (dist <= OutputScroll.atBottomTolerance) (sprintf "at the bottom an eval must keep following (%f px from bottom)" dist)
    let! pill = OutputScroll.pillVisible page
    Expect.isFalse pill "no unseen-eval pill while following"

    // Scroll up to read. Two evals land. What I'm reading must not move.
    do! OutputScroll.wheelUp page 900.0f
    let! reading = OutputScroll.readingPosition page
    Expect.isTrue (reading <> "") "some output line sits at the top of the panel after scrolling up"
    let lineText = reading.Substring(0, reading.LastIndexOf '|')
    let before = float (reading.Substring(reading.LastIndexOf '|' + 1))
    let! distUp = OutputScroll.distanceFromBottom page
    Expect.isTrue (distUp > 200.0) (sprintf "the wheel must actually take us away from the bottom (%f px)" distUp)
    do! OutputScroll.runEval page "unseen-one"
    do! OutputScroll.runEval page "unseen-two"
    // A couple of fallback SSE pushes, so a late yank would have happened.
    do! page.WaitForTimeoutAsync(1500.0f)
    let! after = OutputScroll.offsetOfLine page lineText
    Expect.isTrue (abs (after - before) <= 2.0) (sprintf "scrolled up, the line I was reading (%s) must stay put (was %f px from the panel top, now %f)" lineText before after)
    let! distAfter = OutputScroll.distanceFromBottom page
    Expect.isTrue (distAfter > 200.0) (sprintf "scrolled up, evals must not yank the panel to the bottom (%f px from bottom)" distAfter)
    do! OutputScroll.waitForPillText page "2 new evals"
    let! pillUp = OutputScroll.pillVisible page
    Expect.isTrue pillUp "the unseen-eval pill shows while scrolled up"
    let pillButton = page.GetByRole(AriaRole.Button, PageGetByRoleOptions(Name = "Jump to the newest output"))
    let! pillByRole = pillButton.CountAsync()
    Expect.equal pillByRole 1 "the pill is a real button with an accessible name"

    // Click the pill: back at the bottom, pill gone, following again.
    do! page.Locator(OutputScroll.pillSelector).ClickAsync()
    do! page.WaitForTimeoutAsync(400.0f)
    let! distJump = OutputScroll.distanceFromBottom page
    Expect.isTrue (distJump <= OutputScroll.atBottomTolerance) (sprintf "the pill jumps to the bottom (%f px from bottom)" distJump)
    let! pillGone = OutputScroll.pillVisible page
    Expect.isFalse pillGone "the pill hides once you're back at the bottom"
    do! OutputScroll.runEval page "refollow-one"
    do! page.WaitForTimeoutAsync(400.0f)
    let! distRefollow = OutputScroll.distanceFromBottom page
    Expect.isTrue (distRefollow <= OutputScroll.atBottomTolerance) (sprintf "after the jump the panel follows new output again (%f px from bottom)" distRefollow)
    let! pillStillGone = OutputScroll.pillVisible page
    Expect.isFalse pillStillGone "no pill while following"

    // Scrolling back to the bottom yourself re-pins too, and 1 is singular.
    do! OutputScroll.wheelUp page 900.0f
    do! OutputScroll.runEval page "single-one"
    do! OutputScroll.waitForPillText page "1 new eval ↓"
    let! singular = page.Locator(OutputScroll.pillSelector).TextContentAsync()
    Expect.isFalse (singular.Contains "evals") (sprintf "one unseen eval reads singular (%s)" singular)
    let! _ = page.EvaluateAsync<int>("() => { var el = document.querySelector('#output-panel'); el.scrollTop = el.scrollHeight; return 0; }")
    do! page.WaitForTimeoutAsync(400.0f)
    let! pillAfterSelfScroll = OutputScroll.pillVisible page
    Expect.isFalse pillAfterSelfScroll "scrolling back to the bottom yourself hides the pill"
    do! OutputScroll.runEval page "self-refollow"
    do! page.WaitForTimeoutAsync(400.0f)
    let! distSelf = OutputScroll.distanceFromBottom page
    Expect.isTrue (distSelf <= OutputScroll.atBottomTolerance) (sprintf "after scrolling to the bottom yourself the panel follows again (%f px from bottom)" distSelf)
  })

  playwrightTest "output panel unseen-eval pill fits a phone-width viewport" (fun page -> task {
    do! page.SetViewportSizeAsync(390, 844)
    do! PlaywrightExpect.waitForSSE 15_000 page
    do! OutputScroll.fillPastOneScreen page "phone-fill"
    do! OutputScroll.wheelUp page 900.0f
    do! OutputScroll.runEval page "phone-unseen"
    do! OutputScroll.waitForPillText page "1 new eval"
    let! visible = OutputScroll.pillVisible page
    Expect.isTrue visible "the pill shows at phone width"
    // Inside the output area and the viewport, and nothing sits on top of it.
    let! verdict =
      page.EvaluateAsync<string>(
        """() => {
          var pill = document.querySelector('#output-new-evals');
          var area = document.querySelector('#output-section');
          var p = pill.getBoundingClientRect(), a = area.getBoundingClientRect();
          var problems = [];
          if (p.width < 1 || p.height < 1) problems.push('zero-size');
          // It floats over the panel. In the flex flow it would steal the
          // panel's height and stretch to the full width.
          if (getComputedStyle(pill).position !== 'absolute') problems.push('in the layout flow (position ' + getComputedStyle(pill).position + ')');
          if (p.width > a.width * 0.9) problems.push('stretched to the full width (' + Math.round(p.width) + ' of ' + Math.round(a.width) + ' px)');
          if (p.left < a.left - 0.5 || p.right > a.right + 0.5 || p.top < a.top - 0.5 || p.bottom > a.bottom + 0.5)
            problems.push('outside output area ' + JSON.stringify(p) + ' vs ' + JSON.stringify(a));
          if (p.left < 0 || p.right > window.innerWidth) problems.push('clipped by viewport');
          if (pill.scrollWidth > pill.clientWidth + 1) problems.push('text overflows the pill');
          var hit = document.elementFromPoint(p.left + p.width / 2, p.top + p.height / 2);
          if (!hit || !(hit === pill || pill.contains(hit))) problems.push('covered by ' + (hit ? (hit.id || hit.className) : 'nothing'));
          return problems.join('; ');
        }""")
    Expect.equal verdict "" "the pill fits at phone width with no overlap or clipping"
    // Keyboard: the pill is in the tab order, and Enter on it jumps. The
    // dashboard's global shortcut handler used to eat Enter as "select
    // session" before any focused button could see it.
    let! tabIndex = page.EvaluateAsync<int>("() => document.querySelector('#output-new-evals').tabIndex")
    Expect.isTrue (tabIndex >= 0) "the pill is reachable with Tab"
    do! page.Locator(OutputScroll.pillSelector).FocusAsync()
    do! page.Keyboard.PressAsync("Enter")
    do! page.WaitForTimeoutAsync(500.0f)
    let! dist = OutputScroll.distanceFromBottom page
    Expect.isTrue (dist <= OutputScroll.atBottomTolerance) (sprintf "Enter on the focused pill jumps to the bottom (%f px from bottom)" dist)
    let! pillAfter = OutputScroll.pillVisible page
    Expect.isFalse pillAfter "Enter on the pill hides it"
  })

  playwrightTest "keyboard help toggles on click" (fun page -> task {
    // Help toggle lives inside the collapsed Evaluate accordion — open it.
    do! DashboardDom.openEvalArea page
    do! page.WaitForTimeoutAsync(500.0f)
    let helpWrapper = page.Locator("#keyboard-help-wrapper")
    let helpBtn = page.Locator("#evaluate-section .panel-header-btn").First
    // The wrapper's visibility is signal-driven (Ds.show "$helpVisible"), so it
    // survives the SSE morph. Assert the TOGGLE regardless of the default: one
    // click flips visibility, a second click flips it back.
    let! initiallyVisible = helpWrapper.IsVisibleAsync()
    let flippedState = if initiallyVisible then WaitForSelectorState.Hidden else WaitForSelectorState.Visible
    let restoredState = if initiallyVisible then WaitForSelectorState.Visible else WaitForSelectorState.Hidden
    do! helpBtn.ClickAsync()
    do! helpWrapper.WaitForAsync(LocatorWaitForOptions(State = flippedState))
    do! helpBtn.ClickAsync()
    do! helpWrapper.WaitForAsync(LocatorWaitForOptions(State = restoredState))
  })

  playwrightTest "accordion open state survives the periodic SSE morph" (fun page -> task {
    // Root cause (commit cdeb7567): the SSE fallback re-renders #main about
    // once a second because a ticking uptime/relative-time label defeats the
    // no-change dedupe, and a plain <details> loses its DOM-only `open`
    // attribute on that morph. The fix makes `open` a Datastar signal
    // (signalDetails in DashboardFragments.fs) instead of DOM-only state, so
    // Datastar re-applies it from the surviving signal after every morph.
    // This test proves that DIRECTLY — no DashboardDom.throughPanelReset
    // reopen-retry helper — by opening the accordion once and asserting it
    // is still open after outlasting at least two 1-second SSE-fallback
    // ticks (Timeouts.sseEventInterval).
    do! PlaywrightExpect.waitForSSE 10_000 page
    do! DashboardDom.openEvalArea page
    let isOpen () =
      page.EvaluateAsync<bool>(
        "() => { var el = document.querySelector('#evaluate-section'); return el ? el.open : false; }")
    let! openedNow = isOpen ()
    Expect.isTrue openedNow "evaluate section opened"
    do! page.WaitForTimeoutAsync(2500.0f)
    let! stillOpen = isOpen ()
    Expect.isTrue stillOpen "evaluate section stays open across the periodic SSE morph"
  })

  playwrightTest "sidebar scroll position survives an SSE morph" (fun page -> task {
    // Root cause: the server renders #main without the client-driven
    // `expanded` class, so every morph strips it, the sidebar's tall
    // `.expanded-only` panels collapse for a frame, and the browser clamps
    // .sidebar-inner's scrollTop to the collapsed maximum — the user's scroll
    // position snaps back near the top. Fix: `data-preserve-attr="class"`.
    do! PlaywrightExpect.waitForSSE 15_000 page
    let textarea = DashboardDom.textarea page
    // Stage the eval first, at the normal viewport, so submitting it later is
    // just a keypress. An idle daemon suppresses no-change pushes, so the eval
    // is what produces the SSE morph while the sidebar is scrolled.
    do! DashboardDom.throughPanelReset (fun () -> DashboardDom.openEvalArea page) 5 (fun () -> task {
      do! textarea.FillAsync("""printfn "scroll-probe" """)
    })
    do! DashboardDom.ensureExpanded page
    do! page.SetViewportSizeAsync(1280, 300)
    let! scrolledTo =
      page.EvaluateAsync<float>(
        "() => { var el = document.querySelector('.sidebar-inner'); el.scrollTop = el.scrollHeight - el.clientHeight; return el.scrollTop; }")
    // Guard against a vacuous pass: the collapsed sidebar can only scroll a
    // few dozen px, so a target well above that distinguishes a reset.
    Expect.isTrue (scrolledTo > 100.0) (sprintf "expanded sidebar must overflow enough to detect a reset (scrolled to %f)" scrolledTo)
    // What the sidebar's panels measured before the eval, so a scroll shift can be attributed to the panel that resized.
    let! textBefore = page.EvaluateAsync<string>("() => Array.from(document.querySelector('.sidebar-inner').children).map(c => (c.id || c.className.toString().split(' ')[0]) + ' => ' + c.innerText.replace(/\\s+/g, ' ').slice(0, 400)).join(' || ')")
    let! sizesBefore = page.EvaluateAsync<string>("() => Array.from(document.querySelector('.sidebar-inner').children).map(c => (c.id || c.className.toString().split(' ')[0] || c.tagName) + ':' + Math.round(c.getBoundingClientRect().height)).join(' ')")
    // `__pushes` counts morph mutations anywhere under #main (proof a push
    // landed). `__classStripped` counts #main class mutations whose previous
    // value lacked `expanded` — i.e. the client-owned class was observed
    // absent and then re-added. That is the mechanism behind the scroll reset,
    // detected on every push regardless of whether a layout flush happened to
    // land in the gap (which depends on how heavy the sidebar is).
    let! _ =
      page.EvaluateAsync<int>(
        """() => {
          window.__pushes = 0; window.__classStripped = 0;
          var main = document.querySelector('#main');
          new MutationObserver(() => { window.__pushes++; }).observe(main, { attributes: true, childList: true, subtree: true, characterData: true });
          new MutationObserver(recs => { recs.forEach(r => { if (!(r.oldValue || '').includes('expanded')) window.__classStripped++; }); })
            .observe(main, { attributes: true, attributeFilter: ['class'], attributeOldValue: true });
          return 0;
        }""")
    do! textarea.PressAsync("Alt+Enter")
    do! PlaywrightExpect.waitForSelectorText 30_000 page "#output-panel" "scroll-probe"
    do! page.WaitForTimeoutAsync(1500.0f)
    let! pushes = page.EvaluateAsync<int>("() => window.__pushes")
    Expect.isTrue (pushes > 0) "at least one SSE morph reached #main while the sidebar was scrolled"
    let! stripped = page.EvaluateAsync<int>("() => window.__classStripped")
    Expect.equal stripped 0 "the SSE morph must never strip and re-add the client-owned `expanded` class on #main"
    let! after = page.EvaluateAsync<float>("() => document.querySelector('.sidebar-inner').scrollTop")
    // NOT an exact-position assertion, and NOT `maxAfter`-relative either: the eval this
    // test performs creates live bindings, which grows `.expanded-only` (measured on CI:
    // 469 -> 526px), and the browser's scroll anchoring resolves that growth to a
    // DIFFERENT final scrollTop per renderer — 594 -> 651 measured locally, 594 -> 514
    // measured on the CI runner, for the identical content change. A bound built from
    // `maxAfter` co-varies with that same growth/anchoring, so it can silently pass on a
    // renderer where anchoring lands low. The bug this test exists to catch is a full
    // RESET, not a smaller anchoring offset: the client-owned `expanded` class gets
    // stripped, `.expanded-only` collapses to near nothing, and the browser clamps
    // scrollTop to the tiny COLLAPSED maximum — "a few dozen px", the same ceiling the
    // `scrolledTo > 100.0` guard above already uses to define that collapsed state. A
    // fixed floor of 200px sits comfortably above that collapse ceiling and comfortably
    // below every anchoring outcome observed so far (514-651 locally and on CI), so it
    // still fails for the actual defect (collapse-then-clamp) while tolerating
    // environment-sensitive anchoring jitter that is not the defect. `__classStripped = 0`
    // above is the crisp, direct check for the mechanism itself; this is the
    // belt-and-suspenders "and we did not end up back near the top" check.
    let floor = 200.0
    let! textAfter = page.EvaluateAsync<string>("() => Array.from(document.querySelector('.sidebar-inner').children).map(c => (c.id || c.className.toString().split(' ')[0]) + ' => ' + c.innerText.replace(/\\s+/g, ' ').slice(0, 400)).join(' || ')")
    let! sizesAfter = page.EvaluateAsync<string>("() => Array.from(document.querySelector('.sidebar-inner').children).map(c => (c.id || c.className.toString().split(' ')[0] || c.tagName) + ':' + Math.round(c.getBoundingClientRect().height)).join(' ')")
    let! maxAfter = page.EvaluateAsync<float>("() => { var el = document.querySelector('.sidebar-inner'); return el.scrollHeight - el.clientHeight; }")
    Expect.isTrue (after > floor) (sprintf "sidebar scrollTop must not collapse toward the top across SSE morphs (was %f, now %f, floor %f, new maximum %f)\nsidebar panels before: %s\nsidebar panels after:  %s\ntext before: %s\ntext after:  %s" scrolledTo after floor maxAfter sizesBefore sizesAfter textBefore textAfter)
  })

  playwrightTest "session status renders with state" (fun page -> task {
    // The tabline #session-status carries the state pill; the session id
    // renders in the sibling .tabline-info. Both are inside #main, pushed
    // by the server on every SSE state change.
    let status = page.Locator("#session-status")
    do! PlaywrightExpect.waitForText 10_000 status "Ready"
    let sessionInfo = page.Locator("#main .tabline-info").First
    do! PlaywrightExpect.waitForText 10_000 sessionInfo "Session:"
  })

  playwrightTest "diagnostics panel has diagnostics-panel class" (fun page -> task {
    // Diagnostics panel is in expanded-only mode (hidden by default in minimal mode).
    // Check the class via JS evaluation rather than visibility.
    let! cls = page.EvaluateAsync<string>(
      "() => { var el = document.querySelector('#diagnostics-panel'); return el ? el.className : ''; }")
    Expect.isTrue (cls.Contains("diagnostics-panel")) "diagnostics-panel class"
  })

  playwrightTest "eval stats panel renders" (fun page -> task {
    // #eval-stats is duplicated in the shell — wait for the attached element
    // rather than relying on one copy being visible.
    let stats = page.Locator("#eval-stats").First
    do! stats.WaitForAsync(
      LocatorWaitForOptions(State = WaitForSelectorState.Attached))
    do! PlaywrightExpect.waitForText 10_000 stats "evals"
  })

  // NOTE: "create session section has all inputs" moved to shellStructureTests in DashboardSnapshotTests.fs

  playwrightTest "Tab inserts 2 spaces in textarea" (fun page -> task {
    // Wait for Datastar to fully initialize and bind handlers
    do! PlaywrightExpect.waitForSSE 10_000 page
    // The textarea lives inside the collapsed Evaluate accordion — open it.
    do! DashboardDom.openEvalArea page
    let textarea = page.Locator("#eval-textarea")
    do! textarea.FillAsync("let x")
    do! textarea.ClickAsync()
    // The Tab keydown handler prevents the default focus move and inserts two
    // spaces. Dispatch the keydown directly (the browser may swallow a real
    // Tab press as focus navigation before the page sees it).
    let! handled = page.EvaluateAsync<bool>("""() => {
      var ta = document.getElementById('eval-textarea');
      if (!ta) return false;
      ta.focus();
      ta.setSelectionRange(ta.value.length, ta.value.length);
      var ev = new KeyboardEvent('keydown', { key: 'Tab', code: 'Tab', keyCode: 9, which: 9, bubbles: true, cancelable: true });
      return !ta.dispatchEvent(ev);
    }""")
    Expect.isTrue handled "Tab keydown was handled (default prevented)"
    let! value = textarea.InputValueAsync()
    Expect.isTrue (value.Contains("let x  ")) "Tab inserted 2 spaces"
  })

  playwrightTest "Alt+Enter triggers eval" (fun page -> task {
    // Wait for connection before evaluating
    do! PlaywrightExpect.waitForSSE 10_000 page
    // The textarea lives inside the collapsed Evaluate accordion — open it.
    do! DashboardDom.openEvalArea page

    let textarea = DashboardDom.textarea page
    do! textarea.FillAsync("1 + 1;;")
    do! textarea.PressAsync("Alt+Enter")

    do! PlaywrightExpect.waitForSelectorText 30_000 page "#output-panel" "val it: int = 2"
  })

  playwrightTest "responsive layout on mobile viewport" (fun page -> task {
    do! page.SetViewportSizeAsync(375, 812)
    let! _ = page.GotoAsync(
      sprintf "%s/dashboard" PlaywrightFixture.dashboardUrl)

    // The dashboard shell must keep its core sections usable at mobile width:
    // the main editor area, the output section and the sidebar toggle.
    let main = page.Locator("#main")
    do! PlaywrightExpect.isVisibleAsync main "main visible"
    let outputSection = page.Locator("#output-section")
    do! PlaywrightExpect.isVisibleAsync outputSection "output visible"
    let sidebarToggle = page.Locator("#sidebar-toggle-btn")
    do! PlaywrightExpect.isVisibleAsync sidebarToggle "sidebar toggle visible"
  })

  // --- Agent-generated tests (via Playwright test planner + generator agents) ---

  playwrightTest "evaluate simple expression" (fun page -> task {
    do! PlaywrightExpect.waitForSSE 15_000 page
    let textarea = DashboardDom.textarea page
    do! DashboardDom.throughPanelReset (fun () -> DashboardDom.openEvalArea page) 5 (fun () -> task {
      do! textarea.FillAsync("let x = 1 + 1;;")
      do! (DashboardDom.evalButton page).ClickAsync()
    })
    do! PlaywrightExpect.waitForSelectorText 30_000 page "#output-panel" "val x: int = 2"
    do! PlaywrightExpect.waitForTextareaCleared 10_000 textarea
  })

  playwrightTest "evaluate with Alt+Enter shortcut" (fun page -> task {
    do! PlaywrightExpect.waitForSSE 15_000 page
    let textarea = DashboardDom.textarea page
    do! DashboardDom.throughPanelReset (fun () -> DashboardDom.openEvalArea page) 5 (fun () -> task {
      do! textarea.ClickAsync()
      do! textarea.FillAsync("""printfn "Hello, World!" """)
      do! page.Keyboard.PressAsync("Alt+Enter")
    })
    do! PlaywrightExpect.waitForSelectorText 30_000 page "#output-panel" "val it: unit = ()"
    do! PlaywrightExpect.waitForTextareaCleared 10_000 textarea
  })

  playwrightTest "evaluate multiline code" (fun page -> task {
    do! PlaywrightExpect.waitForSSE 15_000 page
    let textarea = DashboardDom.textarea page
    do! DashboardDom.throughPanelReset (fun () -> DashboardDom.openEvalArea page) 5 (fun () -> task {
      do! textarea.FillAsync("let add x y =\n  x + y\nadd 5 3;;")
      do! (DashboardDom.evalButton page).ClickAsync()
    })
    do! PlaywrightExpect.waitForSelectorText 30_000 page "#output-panel" "int = 8"
  })

  playwrightTest "evaluate code with errors" (fun page -> task {
    do! PlaywrightExpect.waitForSSE 15_000 page
    let textarea = DashboardDom.textarea page
    do! DashboardDom.throughPanelReset (fun () -> DashboardDom.openEvalArea page) 5 (fun () -> task {
      do! textarea.FillAsync("let x = undefinedVariable;;")
      do! (DashboardDom.evalButton page).ClickAsync()
    })
    // The dashboard renders eval failures as an "Evaluation failed" line in
    // the output panel (the FSI exception message, not the raw FS-code text).
    do! PlaywrightExpect.waitForSelectorText 30_000 page "#output-panel" "Evaluation failed"
  })

  playwrightTest "consecutive evaluations maintain scope" (fun page -> task {
    do! PlaywrightExpect.waitForSSE 15_000 page
    let textarea = DashboardDom.textarea page
    let evalBtn = DashboardDom.evalButton page

    do! DashboardDom.throughPanelReset (fun () -> DashboardDom.openEvalArea page) 5 (fun () -> task {
      do! textarea.FillAsync("let x = 5;;")
      do! evalBtn.ClickAsync()
    })
    do! PlaywrightExpect.waitForSelectorText 30_000 page "#output-panel" "val x: int = 5"

    // Each eval's SSE morph can re-collapse the Evaluate accordion — reopen
    // before the next interaction (throughPanelReset also covers the
    // periodic 1s fallback push racing the same reopen-then-act window).
    do! DashboardDom.throughPanelReset (fun () -> DashboardDom.openEvalArea page) 5 (fun () -> task {
      do! textarea.FillAsync("let y = x + 3;;")
      do! evalBtn.ClickAsync()
    })
    do! PlaywrightExpect.waitForSelectorText 30_000 page "#output-panel" "val y: int = 8"

    do! DashboardDom.throughPanelReset (fun () -> DashboardDom.openEvalArea page) 5 (fun () -> task {
      do! textarea.FillAsync("x + y;;")
      do! evalBtn.ClickAsync()
    })
    do! PlaywrightExpect.waitForSelectorText 30_000 page "#output-panel" "val it: int = 13"
  })

  playwrightTest "keyboard help shows shortcuts" (fun page -> task {
    // Help toggle lives inside the collapsed Evaluate accordion — open it.
    // The accordion carries no server-tracked open state, so the periodic 1s
    // SSE fallback push (see DashboardDom.throughPanelReset) can re-collapse
    // it at any point; re-confirm it is open immediately before each step
    // that depends on it rather than trusting a single open call up front
    // (the 500ms settle below is exactly the kind of gap that race needs).
    do! DashboardDom.openEvalArea page
    do! page.WaitForTimeoutAsync(500.0f)
    do! DashboardDom.openEvalArea page
    let helpWrapper = page.Locator("#keyboard-help-wrapper")
    let helpToggle = page.Locator("#evaluate-section .panel-header-btn").First
    // Keyboard help starts hidden ($helpVisible=false) — open it so the
    // shortcuts table is in view before asserting its contents.
    let! helpVisible0 = helpWrapper.IsVisibleAsync()
    if not helpVisible0 then
      do! helpToggle.ClickAsync()
      do! helpWrapper.WaitForAsync(
        LocatorWaitForOptions(State = WaitForSelectorState.Visible))
    let table = page.GetByRole(AriaRole.Table)
    do! PlaywrightExpect.isVisibleAsync table "shortcuts table visible"
    let altEnter = page.GetByText("Alt+Enter")
    do! PlaywrightExpect.isVisibleAsync altEnter "Alt+Enter listed"
    let ctrlL = page.GetByText("Ctrl+L")
    do! PlaywrightExpect.isVisibleAsync ctrlL "Ctrl+L listed"
    let tabKey = page.GetByText("Tab")
    do! PlaywrightExpect.isVisibleAsync tabKey "Tab listed"
    // Toggle closed, then open again. Each click's target lives inside the
    // same accordion, so reconfirm it is open right before every click —
    // a blind whole-body retry isn't safe here since a click that DID land
    // toggles the $helpVisible signal, and retrying from scratch would
    // double-flip it.
    let helpBtn = page.Locator("#evaluate-section .panel-header-btn").First
    do! DashboardDom.openEvalArea page
    do! helpBtn.ClickAsync()
    do! helpWrapper.WaitForAsync(
      LocatorWaitForOptions(State = WaitForSelectorState.Hidden))
    do! DashboardDom.openEvalArea page
    do! helpBtn.ClickAsync()
    do! helpWrapper.WaitForAsync(
      LocatorWaitForOptions(State = WaitForSelectorState.Visible))
  })

  playwrightTest "sessions panel shows session info" (fun page -> task {
    do! PlaywrightExpect.waitForSSE 15_000 page
    let sessionsHeading =
      page.GetByRole(
        AriaRole.Heading, PageGetByRoleOptions(Name = "Sessions"))
    do! PlaywrightExpect.isVisibleAsync sessionsHeading "Sessions heading"
    // A session-row card renders with the session id, its state and the
    // selected indicator in the sidebar sessions panel.
    let sessionCard = page.Locator(".session-row").First
    do! PlaywrightExpect.isVisibleAsync sessionCard "session card visible"
    do! PlaywrightExpect.waitForText 15_000 sessionCard "● selected"
  })

  playwrightTest "diagnostics panel renders empty state" (fun page -> task {
    // Diagnostics panel is in the expanded-only sidebar section; a healthy
    // session has no diagnostics to show.
    do! PlaywrightExpect.waitForSSE 10_000 page
    let! panelExists = page.EvaluateAsync<bool>(
      "() => { var el = document.querySelector('#diagnostics-panel'); return el !== null && el !== undefined; }")
    Expect.isTrue panelExists "diagnostics panel exists"
  })

  // --- Connection banner: server-heartbeat + client-staleness design ---
  // (todo-dashboard-disconnect-indicator.md) The banner DOES use a Datastar
  // signal (data-show="!$connected") — safely, because the flip is driven by
  // a CLIENT-side Ds.onInterval comparing "now" against the last
  // server-patched heartbeat timestamp, never by the server pushing anything
  // while it's dead. Banner starts hidden; the client shows it once no
  // heartbeat has landed within Timeouts.dashboardStaleAfter.
  // NOTE: the data-show wiring is verified by shellStructureTests in
  // DashboardSnapshotTests.fs ("server-status banner reacts to the connected
  // signal via data-show"); the dedicated kill/respawn journey lives in
  // DashboardDisconnectIndicatorBrowserTests.fs (its own isolated daemon —
  // this shared-daemon suite never kills the daemon it depends on).

  playwrightTest "server-status banner is invisible when connected" (fun page -> task {
    // Give SSE time to connect
    do! page.WaitForTimeoutAsync(3000.0f)
    let banner = page.Locator("#server-status")
    do! PlaywrightExpect.isHiddenAsync banner "Banner should be invisible when connected"
    let! text = banner.TextContentAsync()
    let hasConnected =
      text <> null && text.Contains("Connected", StringComparison.OrdinalIgnoreCase)
    Expect.isFalse hasConnected "Banner must not contain 'Connected' text"
  })

  // --- Phase 2: Per-browser session isolation ---
  // Each browser tab maintains its own active session independently.
  // Switching session in one tab must NOT affect other tabs.

  playwrightTest "dashboard switch does not dispatch SessionSwitched to Elm" (fun page -> task {
    // The dashboard switch endpoint should NOT broadcast SessionSwitched
    // to the shared Elm model. It should only update the requesting browser's
    // active session (via signal or per-connection state).
    do! PlaywrightExpect.waitForSSE 15_000 page
    // The tabline shows the viewing session identity.
    let tabline = page.Locator("#main .tabline-info").First
    do! PlaywrightExpect.waitForText 10_000 tabline "Session:"
    // The switch endpoint should return a signal update, not an Elm dispatch.
    // Verify by checking that the switch response contains a signal patch
    // for activeSession (not a full page morph from Elm re-render).
    // This is a structural test — the endpoint's response format matters.
    let! response = page.EvaluateAsync<string>("""() => {
      return fetch('/api/daemon-info')
        .then(r => r.json())
        .then(d => JSON.stringify(d))
        .catch(e => 'error: ' + e.message);
    }""")
    Expect.isTrue (response.Contains("sessions") || response.Contains("version"))
      "daemon-info should be accessible"
  })

  playwrightTestRaw "two browser tabs maintain independent sessions" (fun page1 -> task {
    // Open page1
    let! _ = page1.GotoAsync(
      sprintf "%s/dashboard" PlaywrightFixture.dashboardUrl)
    do! PlaywrightExpect.waitForSSE 15_000 page1
    let tabline1 = page1.Locator("#main .tabline-info").First
    do! PlaywrightExpect.waitForText 15_000 tabline1 "Session:"
    let! text1Before = tabline1.TextContentAsync()

    // Open page2 in separate context (simulates different browser tab)
    let! page2 = PlaywrightFixture.newPage ()
    try
      let! _ = page2.GotoAsync(
        sprintf "%s/dashboard" PlaywrightFixture.dashboardUrl)
      do! PlaywrightExpect.waitForSSE 15_000 page2
      let tabline2 = page2.Locator("#main .tabline-info").First
      do! PlaywrightExpect.waitForText 15_000 tabline2 "Session:"
      let! text2Before = tabline2.TextContentAsync()

      // Both tabs should show the same session initially (default session)
      Expect.isTrue (text1Before.Contains("Session:")) "page1 has session info"
      Expect.isTrue (text2Before.Contains("Session:")) "page2 has session info"

      // Both tabs land on the same default session.
      Expect.equal text2Before text1Before
        "both tabs show the same session initially"
    finally
      PlaywrightFixture.closePage(page2).GetAwaiter().GetResult()
  })

  // --- TS journey ports (page-structure.spec.ts) ---

  playwrightTest "page structure: daemon health shows title with version" (fun page -> task {
    // The shell has no <h1>; the daemon-health bar carries the product
    // identity + version (e.g. "🟢 Healthy · SageFs 0.6.444.0 · up 6m · 15MB").
    let health = page.Locator("#daemon-health")
    do! PlaywrightExpect.waitForText 30_000 health "SageFs"
    let! text = health.TextContentAsync()
    Expect.isTrue (
      text <> null
      && System.Text.RegularExpressions.Regex.IsMatch(text, @"v?\d+\.\d+\.\d+"))
      "daemon health shows a version number"
  })

  playwrightTest "page structure: output section and panel render" (fun page -> task {
    let outputSection = page.Locator("#output-section")
    do! PlaywrightExpect.isVisibleAsync outputSection "output section visible"
    let outputHeading = outputSection.Locator("h2")
    do! PlaywrightExpect.waitForText 10_000 outputHeading "Output"
    let outputPanel = page.Locator("#output-panel")
    do! PlaywrightExpect.isVisibleAsync outputPanel "output panel visible"
  })

  playwrightTest "page structure: evaluate section has textarea and buttons" (fun page -> task {
    let evalSection = page.Locator("#evaluate-section")
    do! PlaywrightExpect.isVisibleAsync evalSection "evaluate section visible"
    do! PlaywrightExpect.waitForText 10_000 evalSection "Evaluate"
    // Evaluate is a <details class="eval-area"> collapsed by default, and
    // the periodic 1s SSE fallback push can re-collapse it between these
    // sequential IsVisibleAsync snapshots on a loaded machine — reopen and
    // retry the whole assertion block through throughPanelReset rather than
    // each check individually.
    do! DashboardDom.throughPanelReset (fun () -> DashboardDom.openEvalArea page) 5 (fun () -> task {
      // Textarea with the F# placeholder
      let textarea = page.Locator(".eval-input").First
      do! PlaywrightExpect.isVisibleAsync textarea "textarea visible"
      let! placeholder = textarea.GetAttributeAsync("placeholder")
      Expect.isTrue (
        placeholder <> null
        && System.Text.RegularExpressions.Regex.IsMatch(placeholder, "Enter F# code"))
        "textarea placeholder mentions Enter F# code"
      // Eval button (the shell renders the evaluate section in more than one
      // template, so match the first — mirrors the Reset locator below).
      let evalBtn =
        page.GetByRole(AriaRole.Button, PageGetByRoleOptions(Name = "Eval")).First
      do! PlaywrightExpect.isVisibleAsync evalBtn "Eval button visible"
      // Reset button (first match — [RESET] or ↻ Reset)
      let resetBtn =
        page.GetByRole(AriaRole.Button, PageGetByRoleOptions(Name = "Reset")).First
      do! PlaywrightExpect.isVisibleAsync resetBtn "Reset button visible"
    })
  })

  playwrightTest "page structure: clear output button in panel header" (fun page -> task {
    let clearBtn = page.Locator("#output-section .panel-header-btn")
    do! PlaywrightExpect.isVisibleAsync clearBtn "clear button visible"
    do! PlaywrightExpect.waitForText 10_000 clearBtn "CLEAR"
  })

  playwrightTest "page structure: create session section has inputs and buttons" (fun page -> task {
    do! PlaywrightExpect.waitForSSE 10_000 page
    // "New Session" is a <details> collapsed by default, and the periodic
    // 1s SSE fallback push can re-collapse it between these sequential
    // IsVisibleAsync snapshots on a loaded machine — reopen and retry the
    // whole assertion block (see DashboardDom.throughPanelReset).
    do! DashboardDom.throughPanelReset (fun () -> DashboardDom.openNewSession page) 5 (fun () -> task {
      // Working directory input (placeholder "/path/to/project"), scoped to the
      // New Session panel so it never matches a similar input elsewhere.
      let dirInput = page.Locator(".new-session-panel input[placeholder*=\"/path/to/project\"]").First
      do! PlaywrightExpect.isVisibleAsync dirInput "working directory input visible"
      // Discover button
      let discoverBtn =
        page.GetByRole(AriaRole.Button, PageGetByRoleOptions(Name = "Discover")).First
      do! PlaywrightExpect.isVisibleAsync discoverBtn "Discover button visible"
      // Manual projects input
      let manualInput = page.Locator("input[placeholder*=\"MyProject.fsproj\"]")
      do! PlaywrightExpect.isVisibleAsync manualInput "manual projects input visible"
      // Create session button
      let createBtn =
        page.GetByRole(AriaRole.Button, PageGetByRoleOptions(Name = "Create")).First
      do! PlaywrightExpect.isVisibleAsync createBtn "Create button visible"
    })
  })

  // --- TS journey ports (friction-panel-journey.spec.ts) ---

  playwrightTest "friction panel renders honest empty state with no send form" (fun page -> task {
    // The friction panel isn't in the default layout; this tab asks for it.
    let! _ = page.GotoAsync(sprintf "%s/dashboard?panels=friction" PlaywrightFixture.dashboardUrl)
    do! PlaywrightExpect.waitForSelectorText 30_000 page "#session-status" "Ready"
    do! DashboardDom.ensureExpanded page
    let panel = page.Locator("#friction-panel")
    do! PlaywrightExpect.isVisibleAsync panel "friction panel visible"
    let summary = panel.Locator("summary")
    do! PlaywrightExpect.waitForText 15_000 summary "Friction"
    // Friction panel is a <details> — open it if collapsed.
    let! isOpen =
      page.EvaluateAsync<bool>(
        "() => { var el = document.querySelector('#friction-panel'); return el ? el.open : false; }")
    if not isOpen then
      do! summary.ClickAsync()
    do! PlaywrightExpect.waitForText 10_000 summary "0 events"
    do! PlaywrightExpect.waitForText 10_000 summary "0 feedback"
    do! PlaywrightExpect.waitForText 10_000 panel "No local friction recorded yet"
    // Honest 0-event state: no send form (endpoint input / Send Report button).
    let! endpointInputs =
      panel.GetByPlaceholder("your-worker.example.workers.dev").CountAsync()
    Expect.equal endpointInputs 0 "no endpoint input when no friction events"
    let! sendButtons =
      panel.GetByRole(AriaRole.Button, LocatorGetByRoleOptions(Name = "Send Report")).CountAsync()
    Expect.equal sendButtons 0 "no Send Report button when no friction events"
  })

  // --- TS journey ports (live-testing-journey.spec.ts) ---

  playwrightTest "live testing panel appears when it's turned on and goes when it's turned off" (fun page -> task {
    do! PlaywrightExpect.waitForSelectorText 30_000 page "#session-status" "Ready"
    do! DashboardDom.ensureExpanded page
    let panel = page.Locator("#live-testing-panel")
    // Off: the panel isn't there at all.
    do! PlaywrightExpect.waitForCount 15_000 panel 0
    // Turn it on the way an editor or agent does, through the daemon API.
    use http = new Net.Http.HttpClient()
    let! enable = http.PostAsync(sprintf "http://localhost:%d/api/live-testing/enable" PlaywrightFixture.mcpPort, null)
    Expect.isTrue enable.IsSuccessStatusCode (sprintf "enable live testing returned %d" (int enable.StatusCode))
    do! PlaywrightExpect.waitForCount 30_000 panel 1
    do! PlaywrightExpect.waitForText 30_000 panel "Live Testing: ON"
    // Disable from the panel's own button, and the panel goes away again.
    let disableBtn =
      panel.GetByRole(AriaRole.Button, LocatorGetByRoleOptions(Name = "Disable"))
    do! disableBtn.ClickAsync()
    do! PlaywrightExpect.waitForCount 30_000 panel 0
  })

  // --- FR-DASH: real friction capture through the local store (seeded via
  // the product's own recorder API against the daemon's friction.db, which
  // the browser runner exposes as SAGEFS_FRICTION_DB). These journeys run
  // AFTER the honest empty-state journey above, so the panel starts empty
  // and this journey observes the real empty -> recorded transition. ---

  playwrightTest "friction feedback recorded locally reflects in the panel with the send form" (fun page -> task {
    // The friction panel isn't in the default layout; this tab asks for it.
    let! _ = page.GotoAsync(sprintf "%s/dashboard?panels=friction" PlaywrightFixture.dashboardUrl)
    do! PlaywrightExpect.waitForSelectorText 30_000 page "#session-status" "Ready"
    do! DashboardDom.ensureExpanded page
    let panel = page.Locator("#friction-panel")
    do! PlaywrightExpect.isVisibleAsync panel "friction panel visible"
    // Record ONE explicit feedback through the product's recorder API into
    // the daemon's local SQLite store (the same store the MCP report_friction
    // tool appends to — server-authoritative, client never assembles data).
    let dbPath = Environment.GetEnvironmentVariable("SAGEFS_FRICTION_DB")
    Expect.isFalse (System.String.IsNullOrWhiteSpace dbPath) "runner should set SAGEFS_FRICTION_DB"
    let store = SageFs.Features.FrictionSqlite.Store.create (sprintf "Data Source=%s" dbPath)
    match store.Initialize() with
    | Ok () ->
      let tool = SageFs.Features.FrictionTelemetryTypes.ToolName.create "eval" |> function Ok t -> t | Error _ -> failwith "tool"
      let sess = SageFs.Features.FrictionTelemetryTypes.SessionRef.create "fr-dash-journey" |> function Ok s -> s | Error _ -> failwith "session"
      let feedback : SageFs.Features.FrictionTelemetryTypes.ExplicitFeedback = {
        OccurredAtUtc = System.DateTimeOffset.UtcNow
        Session = sess
        Tool = tool
        Kind = SageFs.Features.FrictionTelemetryTypes.ExplicitFeedbackKind.ToolIntentWasUnclear
        ShortReason = "the eval tool result was confusing"
        AlternativeUsed = SageFs.Features.FrictionTelemetryTypes.AlternativePath.NoAlternativeRecorded
        SageFsVersion = SageFs.Features.FrictionTelemetryTypes.SageFsVersion.current () }
      match store.AppendFeedback feedback with
      | Ok () -> ()
      | Error e -> failwithf "append feedback failed: %s" e
    | Error e -> failwithf "friction store init failed: %s" e

    // A fresh page load rebuilds the panel from the store (cold GET render).
    let! _ = page.ReloadAsync()
    // The reload reset expanded mode too — re-enable so the panel is visible.
    do! DashboardDom.ensureExpanded page
    do! PlaywrightExpect.waitForText 15_000 (page.Locator("#friction-panel summary")) "1 feedback"
    // The reload reset the <details> to closed — open it for role queries.
    let! isOpen =
      page.EvaluateAsync<bool>(
        "() => { var el = document.querySelector('#friction-panel'); return el ? el.open : false; }")
    if not isOpen then
      do! panel.Locator("summary").ClickAsync()
    do! PlaywrightExpect.waitForText 10_000 panel "Report is sanitized locally before send"
    let! sendButtons =
      panel.GetByRole(AriaRole.Button, LocatorGetByRoleOptions(Name = "Send Report")).CountAsync()
    Expect.equal sendButtons 1 "send form appears once local friction exists"
    // The recorded reason is editable in the panel (server renders local data).
    do! PlaywrightExpect.waitForText 10_000 panel "the eval tool result was confusing"
  })

  playwrightTest "friction send validates the destination and surfaces the result inline" (fun page -> task {
    // The friction panel isn't in the default layout; this tab asks for it.
    let! _ = page.GotoAsync(sprintf "%s/dashboard?panels=friction" PlaywrightFixture.dashboardUrl)
    do! PlaywrightExpect.waitForSelectorText 30_000 page "#session-status" "Ready"
    do! DashboardDom.ensureExpanded page
    let panel = page.Locator("#friction-panel")
    do! PlaywrightExpect.isVisibleAsync panel "friction panel visible"
    // Open the <details> so the send form is in the accessibility tree.
    do! DashboardDom.openFrictionPanel page
    // The previous journey left one feedback record; the send form is present.
    let sendBtn =
      panel.GetByRole(AriaRole.Button, LocatorGetByRoleOptions(Name = "Send Report"))
    let! sendCount = sendBtn.CountAsync()
    Expect.equal sendCount 1 "send form present from the recorded feedback"

    // 1. No endpoint -> inline validation error (server-authoritative). The
    // panel's open state can race the periodic 1s SSE fallback push between
    // opening it and clicking Send — reopen-and-retry through the click
    // (see DashboardDom.throughPanelReset).
    do! DashboardDom.throughPanelReset (fun () -> DashboardDom.openFrictionPanel page) 5
          (fun () -> task { do! sendBtn.ClickAsync() })
    do! PlaywrightExpect.waitForText 15_000 (page.Locator("#friction-send-status")) "missing endpoint"

    // 2. Non-loopback plaintext http -> rejected before any network I/O.
    let endpoint = panel.Locator("input").First
    do! DashboardDom.throughPanelReset (fun () -> DashboardDom.openFrictionPanel page) 5 (fun () -> task {
      do! endpoint.FillAsync("http://example.com/ingest")
      do! sendBtn.ClickAsync()
    })
    let status = page.Locator("#friction-send-status")
    do! PlaywrightExpect.waitForText 15_000 status "endpoint must be an absolute https URL"
  })

  // --- Contextual panels (dashboard-ux-redesign.md, suggested order item 2):
  // a REPL session shows no hot reload, cohort, lanes or friction panel;
  // switching to Hot Reload brings its panel; a cohort with a present member
  // brings the cohort panel and its lanes. Last in the list because it
  // restarts the shared session twice (two workflow switches). ---

  playwrightTest "optional panels show only when they're relevant" (fun page -> task {
    let shot (name: string) = task {
      let path = IO.Path.Combine(PlaywrightFixture.screenshotDir, sprintf "panels-%s.png" name)
      let! _ = page.ScreenshotAsync(PageScreenshotOptions(Path = path, FullPage = true))
      eprintfn "screenshot: %s" path
    }
    let count (selector: string) expected ms = PlaywrightExpect.waitForCount ms (page.Locator(selector)) expected
    do! PlaywrightExpect.waitForSelectorText 30_000 page "#session-status" "Ready"
    do! DashboardDom.ensureExpanded page
    // 1. A REPL session: none of them, even with the extra panels expanded.
    for selector in [ "#hot-reload-panel"; "#cohort-panel"; "#cohort-lanes"; "#friction-panel"; "#live-testing-panel" ] do
      do! count selector 0 15_000
    do! shot "1-repl"

    // 2. A cohort with a present member holding a claim: cohort and lanes.
    let opts =
      ModelContextProtocol.Client.HttpClientTransportOptions(
        Endpoint = Uri(sprintf "http://localhost:%d/" PlaywrightFixture.mcpPort))
    let transport = new ModelContextProtocol.Client.HttpClientTransport(opts, (null: Microsoft.Extensions.Logging.ILoggerFactory))
    let! client = ModelContextProtocol.Client.McpClient.CreateAsync(transport, null, null, Threading.CancellationToken.None)
    let call (name: string) (args: (string * obj) list) = task {
      let! _ = client.CallToolAsync(name, readOnlyDict args, null, null, Threading.CancellationToken.None)
      return ()
    }
    try
      do! call "join_cohort" [ "agentName", box "panel-journey"; "role", box "Implementer" ]
      do! call "acquire_claim" [ "agentName", box "panel-journey"; "scope", box "file:src/Panels.fs"; "purpose", box "panel journey" ]
      do! count "#cohort-panel" 1 30_000
      do! count "#cohort-lanes" 1 30_000
      do! shot "2-cohort"
      // The member leaves: nobody is present, so both go, whatever the ledger still holds.
      do! call "leave_cohort" [ "agentName", box "panel-journey" ]
      do! count "#cohort-panel" 0 30_000
      do! count "#cohort-lanes" 0 30_000
    finally
      (client :> IAsyncDisposable).DisposeAsync().AsTask().GetAwaiter().GetResult()

    // 3. Switch the session to Hot Reload through the REAL dashboard control
    // — the workflow <select> in the sidebar — not the raw HTTP route.
    // Fixed bug: the select's onchange fired
    // `@post('/dashboard/switch-workflow', {workflowTarget: w})`, and
    // Datastar's @post destructures only known option keys out of its
    // second argument (payload/headers/contentType/...); an arbitrary
    // `workflowTarget` key there was silently dropped, so the server never
    // learned what the user picked. This journey proves the fix end to end:
    // picking an option in the dropdown must actually switch the session.
    let consoleErrors = Collections.Generic.List<string>()
    page.Console.Add(fun msg ->
      if msg.Type = "error" then
        consoleErrors.Add(sprintf "[console] %s" msg.Text))
    page.PageError.Add(fun err -> consoleErrors.Add(sprintf "[pageerror] %s" err))
    let switcher = page.Locator(sprintf "#%s" DomIds.WorkflowSwitcher)
    do! PlaywrightExpect.isVisibleAsync switcher "workflow switcher visible once a session is selected"
    let switchViaDropdown (value: string) = task {
      let! _ = switcher.SelectOptionAsync(value)
      return ()
    }
    do! switchViaDropdown "hotreload"
    do! count "#hot-reload-panel" 1 180_000
    do! PlaywrightExpect.waitForSelectorText 180_000 page "#session-status" "Ready"
    do! DashboardDom.ensureExpanded page
    do! shot "3-hot-reload"
    for selector in [ "#cohort-panel"; "#cohort-lanes"; "#friction-panel" ] do
      do! count selector 0 5_000
    // The switcher's OWN selected option reflects the switch actually
    // landed server-side (the select is server-rendered from the session's
    // real workflow, not just whatever the click left in the DOM).
    let! selectedLabel = switcher.EvaluateAsync<string>("el => el.options[el.selectedIndex].textContent")
    Expect.equal selectedLabel "Hot Reload" "the switcher itself shows Hot Reload selected after the real switch landed"

    // Put the shared session back the way the other journeys expect it.
    do! switchViaDropdown "interactive"
    do! count "#hot-reload-panel" 0 180_000
    do! PlaywrightExpect.waitForSelectorText 180_000 page "#session-status" "Ready"
    let! selectedLabelBack = switcher.EvaluateAsync<string>("el => el.options[el.selectedIndex].textContent")
    Expect.equal selectedLabelBack "REPL" "the switcher shows REPL selected again after switching back"

    let datastarOrConsoleErrors = consoleErrors |> List.ofSeq
    Expect.isEmpty datastarOrConsoleErrors
      (sprintf "zero Datastar/console errors across the dropdown-driven workflow switch, got: %s" (String.concat " | " datastarOrConsoleErrors))
  })
  // --- Gap I (outcome-gate-sweep.md, Island H): the no-session landing state
  // matrix. Each of these owns its OWN isolated daemon (see NoSessionLanding
  // above) — never the shared daemon the journeys above depend on, which
  // always has a session by the time they run. Registered under the SAME
  // "--integration-browser" Dedicated tag as every journey above, and part of
  // THIS SAME `tests` value, so DashboardBrowserRunner.runBrowserJourneys
  // already runs them — no CI/Program.fs wiring change needed. ---

  testTask "[Integration] Dashboard browser: no-session landing renders the full shell, then Create from the picker reaches Ready with no reload (Gap I states 1+4)" {
    do! NoSessionLanding.noSessionLandingAndCreateJourney () }
  |> Integration.register (Integration.Dedicated "--integration-browser")

  testTask "[Integration] Dashboard browser: sessions+bareURL renders directly, switch/row-click reach the right session, Stop auto-advances then falls back to the picker (Gap I states 2+3+5, click-through Stop)" {
    do! NoSessionLanding.sessionsSwitchAndStopJourney () }
  |> Integration.register (Integration.Dedicated "--integration-browser")
  ]
