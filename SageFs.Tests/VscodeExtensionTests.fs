module SageFs.Tests.VscodeExtensionTests

open System
open System.Diagnostics
open System.Threading.Tasks
open Expecto
open Expecto.Flip
open Microsoft.Playwright

module Integration = SageFs.Tests.TestInfrastructure.Integration

/// Deadline-based wait on an ACTUAL condition — never a fixed sleep. Probes
/// every 100ms until `probe` is true or `timeoutMs` elapses; returns whether
/// the condition was met (callers assert or diagnose on false).
let waitUntil (timeoutMs: int) (probe: unit -> Task<bool>) = task {
  let sw = Stopwatch.StartNew()
  let mutable met = false
  while not met && sw.ElapsedMilliseconds < int64 timeoutMs do
    let! ok = probe ()
    match ok with
    | true -> met <- true
    | false -> do! Task.Delay 100
  return met
}

/// Retry a write the OS may briefly refuse (a just-killed VS Code releasing
/// its profile, the compiler holding a source file) until it succeeds or the
/// deadline passes. Synchronous on purpose — it also runs from `finally`
/// blocks, where awaits are not allowed — but condition-driven, not a sleep.
let writeWithRetry (path: string) (content: string) (timeout: TimeSpan) =
  Threading.SpinWait.SpinUntil(
    (fun () ->
      try
        IO.File.WriteAllText(path, content)
        true
      with :? IO.IOException -> false),
    timeout)

/// Manages a VSCode instance with Chrome DevTools Protocol for Playwright.
/// Launches a separate instance with its own user-data-dir to avoid
/// interfering with the developer's main VSCode window.
module VscodeFixture =
  let mutable pw: IPlaywright option = None
  let mutable browser: IBrowser option = None
  let mutable codePid: int option = None

  let cdpPort = 9222
  /// Stable per-machine profile under the OS temp dir (never a hardcoded
  /// C:\temp path). Deliberately NOT a fresh temp subdirectory per run: the
  /// profile's settings and its --extensions-dir installs (CI installs the
  /// SageFs VSIX there) must persist across launches.
  let userDataDir = IO.Path.Combine(IO.Path.GetTempPath(), "sagefs-vscode-test")

  let codeExePath =
    // 1. Env var override (VSCODE_PATH=C:\wherever\Code.exe)
    let fromEnv =
      Environment.GetEnvironmentVariable("VSCODE_PATH")
      |> Option.ofObj
      |> Option.filter IO.File.Exists
    // 2. `code` on PATH → resolve to Code.exe via parent dir
    let fromPath =
      lazy
        try
          let psi =
            ProcessStartInfo(
              "where", "code",
              RedirectStandardOutput = true,
              UseShellExecute = false,
              CreateNoWindow = true)
          use p = Process.Start(psi)
          let line = p.StandardOutput.ReadLine()
          p.WaitForExit(3000) |> ignore
          if not (String.IsNullOrEmpty line) then
            // `where code` returns the shim (e.g. …\bin\code or …\bin\code.cmd)
            // Code.exe lives in the parent directory
            let dir = IO.Path.GetDirectoryName(line)
            let candidate = IO.Path.Combine(IO.Path.GetDirectoryName(dir), "Code.exe")
            if IO.File.Exists candidate then Some candidate else None
          else None
        with _ -> None
    // 3. Common install locations
    let wellKnown = [
      @"C:\Program Files\Microsoft VS Code\Code.exe"
      @"C:\Program Files (x86)\Microsoft VS Code\Code.exe"
    ]
    fromEnv
    |> Option.orElseWith (fun () -> fromPath.Value)
    |> Option.orElseWith (fun () -> wellKnown |> List.tryFind IO.File.Exists)

  let codeExe () =
    match codeExePath with
    | Some p -> p
    | None ->
      failwith
        "VS Code not found. Set VSCODE_PATH env var or ensure 'code' is on PATH"

  let isAvailable = codeExePath.IsSome

  /// Pre-configure the test profile so dialogs don't block tests. Honors
  /// SAGEFS_MCP_PORT / SAGEFS_DASHBOARD_PORT so a runner can point the
  /// extension at an isolated daemon; skips the write when the file already
  /// carries the same settings (avoids lock races with a running instance).
  let ensureTestSettings () =
    let userDir = IO.Path.Combine(userDataDir, "User")
    if not (IO.Directory.Exists userDir) then
      IO.Directory.CreateDirectory(userDir) |> ignore
    let settingsPath = IO.Path.Combine(userDir, "settings.json")
    let mcpPort = Environment.GetEnvironmentVariable("SAGEFS_MCP_PORT")
    let dashPort = Environment.GetEnvironmentVariable("SAGEFS_DASHBOARD_PORT")
    let portSettings =
      match mcpPort, dashPort with
      | null, null -> ""
      | m, d ->
        let m = if isNull m then "37749" else m
        let d = if isNull d then "37750" else d
        sprintf """"sagefs.mcpPort":%s,"sagefs.dashboardPort":%s,""" m d
    let settings =
      "{" + portSettings
      + """"security.workspace.trust.enabled":false,"""
      + """"workbench.startupEditor":"none","""
      + """"update.mode":"none","""
      + """"extensions.autoCheckUpdates":false,"""
      + """"telemetry.telemetryLevel":"off"}"""
    let existing =
      try IO.File.ReadAllText(settingsPath) with _ -> null
    if existing <> settings then
      // A previous VS Code instance may still be releasing the file after a
      // kill; retry until writable instead of failing the journey setup.
      match writeWithRetry settingsPath settings (TimeSpan.FromSeconds 15.0) with
      | true -> ()
      | false -> IO.File.WriteAllText(settingsPath, settings)

  /// Kill any Code processes started recently that might hold our CDP port,
  /// then wait for them to actually exit (the CDP port and profile files are
  /// released on exit) — a deadline on the real condition, not a fixed sleep.
  let killOrphans () = task {
    let recent =
      Process.GetProcessesByName("Code")
      |> Array.filter (fun p ->
        try (DateTime.Now - p.StartTime).TotalMinutes < 30.0 with _ -> false)
    for p in recent do
      try p.Kill(true) with _ -> ()
    use cts = new Threading.CancellationTokenSource(TimeSpan.FromSeconds 10.0)
    for p in recent do
      try do! p.WaitForExitAsync(cts.Token) with _ -> ()
  }

  /// Launch VSCode via ShellExecute to detach from parent job object.
  let launchVscode (workspaceDir: string) (disableExtensions: bool) =
    ensureTestSettings ()

    let extFlag = if disableExtensions then " --disable-extensions" else ""
    let extDirFlag =
      // The test profile keeps its own extension installs (CI installs the
      // SageFs VSIX here with --extensions-dir); without this flag VS Code
      // falls back to the default profile's extension set.
      sprintf "--extensions-dir=\"%s\" " (IO.Path.Combine(userDataDir, "extensions"))
    let args =
      sprintf
        "--remote-debugging-port=%d --user-data-dir=\"%s\" %s--new-window%s \"%s\""
        cdpPort userDataDir extDirFlag extFlag workspaceDir

    let psi = ProcessStartInfo(codeExe (), args)
    psi.UseShellExecute <- true
    let proc = Process.Start(psi)
    codePid <- Some proc.Id
    proc.Id

  /// Whether the CDP endpoint currently answers /json/version.
  let cdpResponds () = task {
    use client = new Net.Http.HttpClient(Timeout = TimeSpan.FromSeconds 1.0)
    try
      let! resp =
        client.GetStringAsync(
          sprintf "http://127.0.0.1:%d/json/version" cdpPort)
      return resp.Contains("webSocketDebuggerUrl")
    with _ -> return false
  }

  /// Poll CDP /json/version until the endpoint responds.
  let waitForCdp (timeoutMs: int) = task {
    let! ready = waitUntil timeoutMs cdpResponds
    if not ready then
      failwithf "CDP port %d not available after %dms" cdpPort timeoutMs
  }

  /// Connect Playwright to the CDP endpoint, retrying through the race where
  /// `/json/version` (waitForCdp's own check) already answers but the actual
  /// WebSocket target isn't attached yet — an Electron app's debug port can
  /// listen before the renderer/workspace it targets has finished loading,
  /// so a connect attempted right after waitForCdp succeeds can still see
  /// ECONNREFUSED or a closed socket. Observed in CI: this raced on 2 of 3
  /// consecutive journey runs. If every retry is exhausted, re-probe
  /// /json/version and the launched process's liveness one last time and
  /// fold both into the exception — a bare PlaywrightException gives no way
  /// to tell "VS Code crashed after opening the port" apart from "the port
  /// is up but Playwright's own connect is refused."
  let connectOverCdpWithRetry (pid: int) (playwright: IPlaywright) (maxAttempts: int) = task {
    let mutable result = None
    let mutable lastError : PlaywrightException option = None
    let mutable attempt = 1
    while result.IsNone && attempt <= maxAttempts do
      try
        let! b = playwright.Chromium.ConnectOverCDPAsync(sprintf "http://127.0.0.1:%d" cdpPort)
        result <- Some b
      with :? PlaywrightException as ex ->
        lastError <- Some ex
        if attempt < maxAttempts then do! Task.Delay 1000
      attempt <- attempt + 1
    match result with
    | Some b -> return b
    | None ->
      let! stillRespondsToHttp = cdpResponds ()
      let processState =
        try
          let p = Process.GetProcessById(pid)
          if p.HasExited then sprintf "exited (code %d)" p.ExitCode else "running"
        with :? ArgumentException -> "not found (already exited and reaped)"
      let lastMessage = lastError |> Option.map (fun e -> e.Message) |> Option.defaultValue "(no exception captured)"
      return failwithf
        "CDP connect failed after %d attempts: %s -- /json/version still responds=%b, launched process (pid %d) is %s"
        maxAttempts lastMessage stillRespondsToHttp pid processState
  }

  /// Ensure a VSCode instance is running and Playwright is connected.
  /// Reuses existing connection if already established.
  let ensureBrowser (workspaceDir: string) (disableExtensions: bool) = task {
    match browser with
    | Some b -> return b
    | None ->
      do! killOrphans ()
      let pid = launchVscode workspaceDir disableExtensions
      do! waitForCdp 30_000
      let! playwright = Playwright.CreateAsync()
      pw <- Some playwright
      let! b = connectOverCdpWithRetry pid playwright 10
      browser <- Some b
      return b
  }

  /// Clear VS Code's persisted per-workspace UI state so a fresh launch
  /// starts from a DEFAULT layout. The sidebar's per-container view
  /// visibility is persisted in workspaceStorage — if an earlier run opened
  /// the SageFs container before all four TreeViews registered (only Sessions
  /// existed), that "Sessions-only" layout is restored on every later launch
  /// and the hot-reload view never appears.
  let clearWorkbenchLayoutState () =
    try
      let ws = IO.Path.Combine(userDataDir, "User", "workspaceStorage")
      if IO.Directory.Exists ws then
        for dir in IO.Directory.GetDirectories(ws) do
          try IO.File.Delete(IO.Path.Combine(dir, "state.vscdb")) with _ -> ()
          try IO.File.Delete(IO.Path.Combine(dir, "state.vscdb.backup")) with _ -> ()
    with _ -> ()

  /// Tear down any existing instance so the next ensureBrowser launches a
  /// FRESH VS Code. The DoD journeys use this: a reused instance can carry a
  /// stale/disconnected SSE stream from a previous journey, which makes the
  /// live-testing status bar miss daemon pushes.
  let resetInstance () = task {
    match browser with
    | Some b ->
      try do! b.CloseAsync() with _ -> ()
      browser <- None
    | None -> ()
    match codePid with
    | Some pid ->
      try Process.GetProcessById(pid).Kill(true) with _ -> ()
      codePid <- None
    | None -> ()
    do! killOrphans ()
    clearWorkbenchLayoutState ()
    // The next launch reuses the CDP port: wait until the old instance has
    // actually released it.
    let! _released =
      waitUntil 10_000 (fun () -> task {
        let! up = cdpResponds ()
        return not up })
    ()
  }

  /// Get the main VSCode renderer page.
  let getPage () = task {
    match browser with
    | Some b when b.Contexts.Count > 0 && b.Contexts[0].Pages.Count > 0 ->
      return b.Contexts[0].Pages[0]
    | _ -> return failwith "No VSCode page - call ensureBrowser first"
  }

  /// Disconnect Playwright and kill the VSCode test instance.
  let cleanup () = task {
    match browser with
    | Some b ->
      try do! b.CloseAsync() with _ -> ()
      browser <- None
    | None -> ()
    match pw with
    | Some p -> p.Dispose(); pw <- None
    | None -> ()
    match codePid with
    | Some pid ->
      try Process.GetProcessById(pid).Kill(true) with _ -> ()
      codePid <- None
    | None -> ()
  }

/// Every VS Code command the E2E journeys drive through the command
/// palette, as a closed set — no call site may pass an arbitrary string.
/// The palette matches typed text against a command's TITLE, never its
/// internal id (VS Code has never matched by id — microsoft/vscode#113165
/// requests that as a still-open feature) — so mistyping "sagefs.enableLiveTesting"
/// (the id) instead of "SageFs: Enable Live Testing" (the title) leaves the
/// quick-pick showing zero matches and Enter silently dispatches nothing.
/// `SageFsPaletteCommandContractTests` (VscodeExtensionTests.fs) checks every
/// `id` here against the extension's own package.json, so a renamed command
/// there fails a FAST default-suite test instead of silently breaking a
/// [Integration]-only VS Code journey.
type SageFsPaletteCommand =
  | FocusHotReloadView
  | HotReloadWatchAll
  | HotReloadRefresh
  | EnableLiveTesting
  | DisableLiveTesting
  | ShowSageFsContainer
  | FocusOutputView

module SageFsPaletteCommand =
  /// The command id declared in sagefs-vscode/package.json — None for a
  /// VS Code built-in (or VS Code auto-generated per-view focus command)
  /// that isn't declared there, so the contract test has nothing to check.
  let id =
    function
    | FocusHotReloadView -> None // VS Code auto-generates "<viewId>.focus" per view; not in contributes.commands
    | HotReloadWatchAll -> Some "sagefs.hotReloadWatchAll"
    | HotReloadRefresh -> Some "sagefs.hotReloadRefresh"
    | EnableLiveTesting -> Some "sagefs.enableLiveTesting"
    | DisableLiveTesting -> Some "sagefs.disableLiveTesting"
    | ShowSageFsContainer -> None // VS Code built-in, auto-generated from contributes.viewsContainers
    | FocusOutputView -> None // VS Code built-in

  /// The exact text to type into the command palette (Ctrl+Shift+P).
  let title =
    function
    | FocusHotReloadView -> "SageFs: Focus on Hot Reload Files View"
    | HotReloadWatchAll -> "SageFs: Watch All Files"
    | HotReloadRefresh -> "SageFs: Refresh Hot Reload"
    | EnableLiveTesting -> "SageFs: Enable Live Testing"
    | DisableLiveTesting -> "SageFs: Disable Live Testing"
    | ShowSageFsContainer -> "View: Show SageFs"
    | FocusOutputView -> "Output: Focus on Output View"

/// Helpers for interacting with VSCode through Playwright.
module VscodeHelpers =
  /// The fuzzy matcher's readiness between typing and Enter is the ONE
  /// quick-input state that cannot be polled: any page evaluation there steals
  /// focus from the quick-input, so the Enter that follows lands on the editor
  /// and the command silently never runs (observed: handlerEvidence='' with the
  /// command surfaced in the palette). Every other wait is a condition poll.
  let private matcherSettleMs = 800

  let paletteVisible (page: IPage) () =
    page.Locator(".quick-input-widget").IsVisibleAsync()

  /// Wait (deadline) for the quick-input widget to be visible.
  let waitForPalette (timeoutMs: int) (page: IPage) =
    waitUntil timeoutMs (paletteVisible page)

  /// Wait (deadline) for the quick-input widget to close.
  let waitForPaletteClosed (timeoutMs: int) (page: IPage) =
    waitUntil timeoutMs (fun () -> task {
      let! visible = paletteVisible page ()
      return not visible })

  let private quickPickRowsJs =
    "(() => { var rows = document.querySelectorAll('.quick-input-list .monaco-list-row');" +
    " var out = []; rows.forEach(function(r){ out.push(r.textContent.trim()); }); return out; })()"

  /// Full account of one palette-driven command dispatch, so a caller's
  /// failure message can distinguish every distinct silent-no-op cause: the
  /// palette never opening (Ctrl+Shift+P didn't register, or the window
  /// wasn't focused), our title matching nothing (a rename, or the command
  /// not registered in this VS Code build), or a match closing the palette
  /// without the extension-side effect ever landing (a real product bug).
  type PaletteDriveResult = {
    Opened: bool
    MatchesSeen: string list
    ClosedAfterEnter: bool
  }

  /// Drive the command palette and report exactly what happened at each
  /// step — the caller's diagnostic of last resort when the command's effect
  /// never shows up.
  let executeCommandDiagnosed (page: IPage) (command: SageFsPaletteCommand) = task {
    // Palette execution is the ONLY reliable command path under CDP: a
    // synthesized `command:` anchor click (executeCommandUri) does not route
    // to the extension host. Each step WAITS for the UI state it needs — a
    // fixed sleep can race (palette not open yet → keystrokes land in the
    // editor → command silently never runs).
    do! page.Keyboard.PressAsync("Control+Shift+p")
    let! opened = waitForPalette 5_000 page
    do! page.Keyboard.TypeAsync(SageFsPaletteCommand.title command)
    do! Task.Delay(matcherSettleMs)
    let! matches = page.EvaluateAsync<string[]>(quickPickRowsJs)
    do! page.Keyboard.PressAsync("Enter")
    // The palette closes once the command is dispatched.
    let! closed = waitForPaletteClosed 5_000 page
    return { Opened = opened; MatchesSeen = matches |> Array.toList; ClosedAfterEnter = closed }
  }

  let executeCommand (page: IPage) (command: SageFsPaletteCommand) = task {
    let! _result = executeCommandDiagnosed page command
    ()
  }

  /// Open a file via Quick Open (Ctrl+P).
  let openFile (page: IPage) (filename: string) = task {
    do! page.Keyboard.PressAsync("Control+p")
    let! _opened = waitForPalette 5_000 page
    do! page.Keyboard.TypeAsync(filename)
    do! Task.Delay(matcherSettleMs)
    do! page.Keyboard.PressAsync("Enter")
    let! _closed = waitForPaletteClosed 5_000 page
    ()
  }

  /// Press Escape to dismiss any overlay.
  let dismiss (page: IPage) = task {
    do! page.Keyboard.PressAsync("Escape")
    let! _closed = waitForPaletteClosed 2_000 page
    ()
  }

  /// Get text content of a CSS selector, empty string if not found.
  let selectorText (page: IPage) (selector: string) = task {
    let js =
      sprintf
        "(() => { var el = document.querySelector('%s'); return el ? el.textContent : ''; })()"
        selector
    return! page.EvaluateAsync<string>(js)
  }

  /// Wait for text to appear in any element matching a CSS selector.
  let waitForSelectorText
    (timeoutMs: int) (page: IPage) (selector: string) (text: string) = task {
    let sw = Stopwatch.StartNew()
    let mutable found = false
    while not found && sw.ElapsedMilliseconds < int64 timeoutMs do
      let! content = selectorText page selector
      if content <> null && content.Contains(text) then
        found <- true
      else
        do! Task.Delay(250)
    return found
  }

  /// Wait for VSCode's title bar to contain specific text.
  let waitForTitle (timeoutMs: int) (page: IPage) (text: string) = task {
    let sw = Stopwatch.StartNew()
    let mutable found = false
    while not found && sw.ElapsedMilliseconds < int64 timeoutMs do
      let! title = page.TitleAsync()
      if title.Contains(text) then
        found <- true
      else
        do! Task.Delay(250)
    return found
  }

  /// Get all non-empty status bar item texts as a JSON array string.
  let getStatusBarText (page: IPage) = task {
    let js =
      "(() => { var items = document.querySelectorAll('.statusbar-item');" +
      " var r = []; items.forEach(function(i) { var t = i.textContent.trim();" +
      " if(t) r.push(t); }); return JSON.stringify(r); })()"
    return! page.EvaluateAsync<string>(js)
  }

  /// Take a named screenshot for debugging failed tests.
  let screenshot (page: IPage) (name: string) = task {
    let path = IO.Path.Combine(IO.Path.GetTempPath(), sprintf "sagefs-vscode-test-%s.png" name)
    let! _ = page.ScreenshotAsync(PageScreenshotOptions(Path = path))
    return path
  }

// ---------------------------------------------------------------------------
// Test wrappers
// ---------------------------------------------------------------------------

/// The repo root the smoke/ext tests open as their workspace. Derived from the
/// test source location (never a hardcoded path — the extension fixture must
/// run from any checkout location).
let repoRoot =
  IO.Path.Combine(__SOURCE_DIRECTORY__, "..", "..")

// The wrappers ALWAYS register real tests. There is no pending (ptestCase)
// fallback when VS Code is absent: a pending placeholder turned a missing
// fixture into a silent green no-op. These are [Integration] tests (excluded
// from the default run), and the --integration-vsc runner fails fast with an
// actionable message when Code.exe is missing (VscodeFixture.isAvailable).

/// The smoke/extension-behavior tests drive a VS Code window on the repo root
/// against whatever daemon is already on the default port — no runner owns
/// that daemon, so they run only on demand (--all), never in CI.
let private onDemandVscode =
  Integration.Dedicated "--all (on demand: VS Code + a daemon already running on 37749)"

/// Run a test against VSCode with extensions disabled (pure UI tests).
let vscodeUiTest name (body: IPage -> Task<unit>) =
  testTask (sprintf "[Integration] VSCode UI: %s" name) {
    let! _b = VscodeFixture.ensureBrowser repoRoot true
    let! page = VscodeFixture.getPage ()
    do! body page
  }
  |> Integration.register onDemandVscode

/// Run a test against VSCode with extensions enabled (extension tests).
let vscodeExtTest name (body: IPage -> Task<unit>) =
  testTask (sprintf "[Integration] VSCode extension: %s" name) {
    let! _b = VscodeFixture.ensureBrowser repoRoot false
    let! page = VscodeFixture.getPage ()
    do! body page
  }
  |> Integration.register onDemandVscode

// ---------------------------------------------------------------------------
// Smoke tests — extensions disabled, verifies fixture works
// ---------------------------------------------------------------------------

[<Tests>]
let smokeTests = testList "VSCode fixture smoke" [
  vscodeUiTest "connects and gets page title" (fun page -> task {
    let! title = page.TitleAsync()
    Expect.isNotEmpty "should have a window title" title
  })

  vscodeUiTest "status bar is present" (fun page -> task {
    let! statusText = VscodeHelpers.getStatusBarText page
    Expect.isNotEmpty "status bar should have items" statusText
  })

  vscodeUiTest "can open command palette" (fun page -> task {
    do! page.Keyboard.PressAsync("Control+Shift+p")
    let! inputVisible = VscodeHelpers.waitForPalette 5_000 page
    Expect.isTrue "command palette should be visible" inputVisible
    do! VscodeHelpers.dismiss page
  })

  vscodeUiTest "can take screenshot" (fun page -> task {
    let! path = VscodeHelpers.screenshot page "smoke"
    Expect.isTrue "screenshot file should exist" (IO.File.Exists(path))
    IO.File.Delete(path)
  })
]

// ---------------------------------------------------------------------------
// Extension tests — requires SageFs extension installed + daemon running
// ---------------------------------------------------------------------------

[<Tests>]
let extensionTests = testList "VSCode extension behavior" [
  vscodeExtTest "workspace folder is open" (fun page -> task {
    // Wait for workspace to load — title should contain folder name
    let! hasTitle =
      VscodeHelpers.waitForTitle 10000 page "SageFs"
    if not hasTitle then
      let! _ = VscodeHelpers.screenshot page "workspace-fail"
      ()
    Expect.isTrue "title should contain workspace name" hasTitle
  })

  vscodeExtTest "extension activates with SageFs status" (fun page -> task {
    // Wait for extension to activate — poll status bar for up to 15s
    let sw = Diagnostics.Stopwatch.StartNew()
    let mutable hasSageFs = false
    while not hasSageFs && sw.ElapsedMilliseconds < 15000L do
      let! statusText = VscodeHelpers.getStatusBarText page
      hasSageFs <-
        statusText.Contains("SageFs") || statusText.Contains("sagefs")
      if not hasSageFs then do! Task.Delay(1000)
    if not hasSageFs then
      let! _ = VscodeHelpers.screenshot page "ext-activate-fail"
      ()
    Expect.isTrue "status bar should contain SageFs indicator" hasSageFs
  })

  vscodeExtTest "output channel exists" (fun page -> task {
    // Open Output panel and switch to SageFs channel
    do! VscodeHelpers.executeCommand page FocusOutputView
    // The output panel area should contain "SageFs" somewhere once rendered.
    let js =
      "(() => { var el = document.querySelector('.panel'); " +
      "return el ? el.textContent : ''; })()"
    let! hasSageFsChannel =
      waitUntil 10_000 (fun () -> task {
        let! panelText = page.EvaluateAsync<string>(js)
        return panelText.Contains("SageFs") || panelText.Contains("sagefs") })
    if not hasSageFsChannel then
      let! _ = VscodeHelpers.screenshot page "output-channel-fail"
      ()
    Expect.isTrue "SageFs output channel should exist" hasSageFsChannel
  })

  vscodeExtTest "workspace loads with fsproj files" (fun page -> task {
    do! page.Keyboard.PressAsync("Control+p")
    let! quickPickVisible = VscodeHelpers.waitForPalette 5_000 page
    Expect.isTrue "quick pick should be visible" quickPickVisible
    do! page.Keyboard.TypeAsync(".fsproj")
    // File indexing is asynchronous: poll Quick Open until it lists a project.
    let! hasResults =
      waitUntil 15_000 (fun () -> task {
        let! resultsText = VscodeHelpers.selectorText page ".quick-input-list"
        return resultsText.Contains("fsproj") })
    do! VscodeHelpers.dismiss page
    Expect.isTrue "should find .fsproj files in workspace" hasResults
  })
]

// ---------------------------------------------------------------------------
// DoD real-client journeys (HR-VSC-E2E, LT-VSC-E2E) — RETIRED 2026-09-12.
// The CDP/keystroke-driven approach that lived here (command palette typing,
// activity-bar CSS selectors, a --remote-debugging-port connection that went
// unreachable in CI on two different Windows runner images) never passed
// once since being wired in — see issue #133. Replaced by
// SageFs.Tests/VscodeCommandProofTests.fs, which proves the same real
// commands reach the real daemon via VS Code's own official extension-test
// API (@vscode/test-electron) — vscode.commands.executeCommand called
// directly inside a real extension host, no UI automation, no debug port.
// ---------------------------------------------------------------------------

// ---------------------------------------------------------------------------
// SageFsPaletteCommand contract: no VS Code required, so this runs in the
// FAST default suite — a title renamed in package.json (or a typo introduced
// in the DU) fails here immediately, instead of surfacing as a silent no-op
// deep inside a slow [Integration]-only VS Code journey.
// ---------------------------------------------------------------------------

[<Tests>]
let sageFsPaletteCommandContractTests =
  testList "SageFsPaletteCommand contract" [
    testCase "every command with a declared id matches its package.json title" <| fun _ ->
      let packageJsonPath =
        IO.Path.Combine(__SOURCE_DIRECTORY__, "..", "sagefs-vscode", "package.json")
      use doc = Text.Json.JsonDocument.Parse(IO.File.ReadAllText packageJsonPath)
      let titleById =
        doc.RootElement.GetProperty("contributes").GetProperty("commands").EnumerateArray()
        |> Seq.map (fun c -> c.GetProperty("command").GetString(), c.GetProperty("title").GetString())
        |> Map.ofSeq
      let allCommands =
        [ FocusHotReloadView; HotReloadWatchAll; HotReloadRefresh
          EnableLiveTesting; DisableLiveTesting; ShowSageFsContainer; FocusOutputView ]
      for cmd in allCommands do
        match SageFsPaletteCommand.id cmd with
        | None -> () // VS Code built-in / auto-generated — nothing in package.json to check
        | Some cmdId ->
          match Map.tryFind cmdId titleById with
          | None ->
            failwithf
              "SageFsPaletteCommand.%A declares id '%s' but sagefs-vscode/package.json has no such command"
              cmd cmdId
          | Some packageTitle ->
            SageFsPaletteCommand.title cmd
            |> Expect.equal
              (sprintf "SageFsPaletteCommand.%A's palette title must match package.json's title for '%s'" cmd cmdId)
              packageTitle
  ]
