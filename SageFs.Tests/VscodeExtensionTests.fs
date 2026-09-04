module SageFs.Tests.VscodeExtensionTests

open System
open System.Diagnostics
open System.Threading.Tasks
open Expecto
open Expecto.Flip
open Microsoft.Playwright

/// Manages a VSCode instance with Chrome DevTools Protocol for Playwright.
/// Launches a separate instance with its own user-data-dir to avoid
/// interfering with the developer's main VSCode window.
module VscodeFixture =
  let mutable pw: IPlaywright option = None
  let mutable browser: IBrowser option = None
  let mutable codePid: int option = None

  let cdpPort = 9222
  let userDataDir = @"C:\temp\sagefs-vscode-test"

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
      // kill; retry briefly instead of failing the journey setup.
      let mutable written = false
      let deadline = DateTime.UtcNow.AddSeconds(15.0)
      while not written && DateTime.UtcNow < deadline do
        try
          IO.File.WriteAllText(settingsPath, settings)
          written <- true
        with :? System.IO.IOException ->
          Threading.Thread.Sleep(500)
      if not written then
        IO.File.WriteAllText(settingsPath, settings)

  /// Kill any Code processes started recently that might hold our CDP port.
  let killOrphans () =
    for p in Process.GetProcessesByName("Code") do
      try
        if (DateTime.Now - p.StartTime).TotalMinutes < 30.0 then
          p.Kill(true)
      with _ -> ()
    Threading.Thread.Sleep(500)

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

  /// Poll CDP /json/version until the endpoint responds.
  let waitForCdp (timeoutMs: int) = task {
    let sw = Stopwatch.StartNew()
    use client = new Net.Http.HttpClient()
    let mutable ready = false
    while not ready && sw.ElapsedMilliseconds < int64 timeoutMs do
      try
        let! resp =
          client.GetStringAsync(
            sprintf "http://127.0.0.1:%d/json/version" cdpPort)
        if resp.Contains("webSocketDebuggerUrl") then
          ready <- true
      with _ ->
        do! Task.Delay(500)
    if not ready then
      failwithf "CDP port %d not available after %dms" cdpPort timeoutMs
  }

  /// Ensure a VSCode instance is running and Playwright is connected.
  /// Reuses existing connection if already established.
  let ensureBrowser (workspaceDir: string) (disableExtensions: bool) = task {
    match browser with
    | Some b -> return b
    | None ->
      killOrphans ()
      let _pid = launchVscode workspaceDir disableExtensions
      do! waitForCdp 15000
      let! playwright = Playwright.CreateAsync()
      pw <- Some playwright
      let! b =
        playwright.Chromium.ConnectOverCDPAsync(
          sprintf "http://127.0.0.1:%d" cdpPort)
      browser <- Some b
      return b
  }

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
    killOrphans ()
    do! Task.Delay(1500)
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

/// Helpers for interacting with VSCode through Playwright.
module VscodeHelpers =
  /// Execute a VS Code command via the Command Palette.
  let executeCommand (page: IPage) (command: string) = task {
    do! page.Keyboard.PressAsync("Control+Shift+p")
    do! Task.Delay(500)
    do! page.Keyboard.TypeAsync(command)
    do! Task.Delay(500)
    do! page.Keyboard.PressAsync("Enter")
    do! Task.Delay(500)
  }

  /// Execute a VS Code command WITHOUT keyboard input, by clicking an anchor
  /// whose href is the command: URI (VS Code's workbench handles command:
  /// links). The DoD journeys prefer this for extension commands: palette
  /// typing is flaky (fuzzy-matches can hit the wrong command) and heavy
  /// keyboard automation can disturb the extension host's SSE sockets.
  let executeCommandUri (page: IPage) (command: string) = task {
    let js =
      "(() => { var a = document.createElement('a'); a.href = 'command:"
      + command
      + "'; a.click(); return true; })()"
    let! _ = page.EvaluateAsync<string>(js)
    do! Task.Delay(800)
  }

  /// Open a file via Quick Open (Ctrl+P).
  let openFile (page: IPage) (filename: string) = task {
    do! page.Keyboard.PressAsync("Control+p")
    do! Task.Delay(500)
    do! page.Keyboard.TypeAsync(filename)
    do! Task.Delay(500)
    do! page.Keyboard.PressAsync("Enter")
    do! Task.Delay(1000)
  }

  /// Press Escape to dismiss any overlay.
  let dismiss (page: IPage) = task {
    do! page.Keyboard.PressAsync("Escape")
    do! Task.Delay(300)
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
    let path = sprintf @"C:\temp\sagefs-vscode-test-%s.png" name
    let! _ = page.ScreenshotAsync(PageScreenshotOptions(Path = path))
    return path
  }

// ---------------------------------------------------------------------------
// Test wrappers
// ---------------------------------------------------------------------------

/// Run a test against VSCode with extensions disabled (pure UI tests).
let vscodeUiTest name (body: IPage -> Task<unit>) =
  if not VscodeFixture.isAvailable then
    // Kept pending: pure UI tests need VS Code (Code.exe); this placeholder only
    // registers when the fixture is absent on the machine, so it stays pending.
    ptestCase (sprintf "[Integration] VSCode UI: %s" name) ignore
  else
    testCase (sprintf "[Integration] VSCode UI: %s" name) (fun () ->
      let t = task {
        let! _b =
          VscodeFixture.ensureBrowser @"C:\Code\Repos\SageFs" true
        let! page = VscodeFixture.getPage ()
        do! body page
      }
      t.GetAwaiter().GetResult())

/// Run a test against VSCode with extensions enabled (extension tests).
let vscodeExtTest name (body: IPage -> Task<unit>) =
  if not VscodeFixture.isAvailable then
    // Kept pending: extension tests need VS Code + the SageFs extension installed
    // (Code.exe); placeholder only registers when the fixture is absent, so it stays pending.
    ptestCase (sprintf "[Integration] VSCode extension: %s" name) ignore
  else
    testCase (sprintf "[Integration] VSCode extension: %s" name) (fun () ->
      let t = task {
        let! _b =
          VscodeFixture.ensureBrowser @"C:\Code\Repos\SageFs" false
        let! page = VscodeFixture.getPage ()
        do! body page
      }
      t.GetAwaiter().GetResult())

/// Like vscodeExtTest, but opens a specific workspace directory (used by the
/// DoD journeys, which target the FromCSharp sample's 11 Expecto tests rather
/// than the whole repo).
let vscodeExtTestIn (workspaceDir: string) name (body: IPage -> Task<unit>) =
  if not VscodeFixture.isAvailable then
    ptestCase (sprintf "[Integration] VSCode extension: %s" name) ignore
  else
    testCase (sprintf "[Integration] VSCode extension: %s" name) (fun () ->
      let t = task {
        let! _b = VscodeFixture.ensureBrowser workspaceDir false
        let! page = VscodeFixture.getPage ()
        do! body page
      }
      t.GetAwaiter().GetResult())

/// Like vscodeExtTestIn, but ALWAYS starts with a fresh VS Code instance
/// (tears down any reused one first). The DoD journeys need this: live-testing
/// state flows over SSE, and a reused instance can carry a stale/disconnected
/// stream from a prior journey in the same process.
let vscodeExtTestFresh (workspaceDir: string) name (body: IPage -> Task<unit>) =
  if not VscodeFixture.isAvailable then
    ptestCase (sprintf "[Integration] VSCode extension: %s" name) ignore
  else
    testCase (sprintf "[Integration] VSCode extension: %s" name) (fun () ->
      let t = task {
        do! VscodeFixture.resetInstance ()
        let! _b = VscodeFixture.ensureBrowser workspaceDir false
        let! page = VscodeFixture.getPage ()
        do! body page
      }
      t.GetAwaiter().GetResult())

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
    do! Task.Delay(1000)
    let! inputVisible =
      page.Locator(".quick-input-widget").IsVisibleAsync()
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
    do! VscodeHelpers.executeCommand page "Output: Focus on Output View"
    do! Task.Delay(2000)
    // The output panel area should contain "SageFs" somewhere
    let js =
      "(() => { var el = document.querySelector('.panel'); " +
      "return el ? el.textContent : ''; })()"
    let! panelText = page.EvaluateAsync<string>(js)
    let hasSageFsChannel =
      panelText.Contains("SageFs") || panelText.Contains("sagefs")
    if not hasSageFsChannel then
      let! _ = VscodeHelpers.screenshot page "output-channel-fail"
      ()
    Expect.isTrue "SageFs output channel should exist" hasSageFsChannel
  })

  vscodeExtTest "workspace loads with fsproj files" (fun page -> task {
    // Wait a moment for file indexing
    do! Task.Delay(3000)
    do! page.Keyboard.PressAsync("Control+p")
    do! Task.Delay(1000)
    do! page.Keyboard.TypeAsync(".fsproj")
    do! Task.Delay(1500)
    let! quickPickVisible =
      page.Locator(".quick-input-widget").IsVisibleAsync()
    Expect.isTrue "quick pick should be visible" quickPickVisible
    let! resultsText =
      VscodeHelpers.selectorText page ".quick-input-list"
    let hasResults = resultsText.Length > 0
    do! VscodeHelpers.dismiss page
    Expect.isTrue "should find .fsproj files in workspace" hasResults
  })
]

// ---------------------------------------------------------------------------
// DoD real-client journeys (HR-VSC-E2E, LT-VSC-E2E)
//
// These drive REAL VS Code + the SageFs extension against a REAL daemon. They
// assume the same environment the extension-activation tests above assume: a
// SageFs daemon is running on 37749 with a Ready session whose project has
// Expecto tests (e.g. samples/from-csharp/SageFs.Samples.FromCSharp — 11
// tests — with the daemon's session created on that directory), and VS Code
// is open on that workspace. FR-VSC-E2E has no extension command: friction
// is recorded agent/MCP-side (the daemon's report_friction tool), so the
// honest VS Code journey for friction is not expressible through the
// extension UI — recorded in the DoD evidence as an extension-surface gap.
// ---------------------------------------------------------------------------

/// Wait for the status bar to contain `text` (poll textContent).
let vscodeWaitForStatusText (timeoutMs: int) (page: IPage) (text: string) = task {
  let sw = Diagnostics.Stopwatch.StartNew()
  let mutable found = false
  while not found && sw.ElapsedMilliseconds < int64 timeoutMs do
    let! status = VscodeHelpers.getStatusBarText page
    if status.Contains(text) then found <- true
    else do! Task.Delay(500)
  Expect.isTrue "status bar should contain text" found
}

/// Wait for a CSS selector's text content to contain `text`.
let vscodeWaitForSelectorText (timeoutMs: int) (page: IPage) (selector: string) (text: string) = task {
  let sw = Diagnostics.Stopwatch.StartNew()
  let mutable found = false
  while not found && sw.ElapsedMilliseconds < int64 timeoutMs do
    let! content = VscodeHelpers.selectorText page selector
    if content <> null && content.Contains(text) then found <- true
    else do! Task.Delay(500)
  Expect.isTrue "selector should contain text" found
}

/// The workspace the DoD journeys run against: the FromCSharp sample with its
/// 11 Expecto tests. A daemon session must exist on this directory (the
/// --integration-vsc runner creates it, as does the local manual setup).
let sampleWorkspace =
  IO.Path.Combine(
    __SOURCE_DIRECTORY__, "..", "samples", "from-csharp",
    "SageFs.Samples.FromCSharp")

[<Tests>]
let dodJourneys =
  testList "VSCode DoD real-client journeys" [

    vscodeExtTestFresh sampleWorkspace "HR-VSC-E2E: watch all arms the hot-reload tree with watched files" (fun page -> task {
      // Reveal the SageFs activity-bar container (id "sagefs") which hosts the
      // hot-reload tree view (id "sagefs-hotReload"). The container stacks its
      // views — click the activity-bar icon, then the "Hot Reload Files" view
      // header to surface the tree.
      do! VscodeHelpers.executeCommand page "workbench.view.extension.sagefs"
      do! Task.Delay(1500)
      // Fall back to clicking the activity-bar icon labelled SageFs.
      let sagefsIcon =
        page.Locator(".activitybar .action-item").Filter(LocatorFilterOptions(HasText = "SageFs")).First
      try
        do! sagefsIcon.ClickAsync(LocatorClickOptions(Timeout = 3000.0f))
        do! Task.Delay(1500)
      with _ -> ()
      // Click the view header for the hot-reload view (stacked under Sessions).
      let headerSel = ".sidebar .view-header"
      let hotReloadHeader =
        page.Locator(headerSel).Filter(LocatorFilterOptions(HasText = "Hot Reload Files")).First
      try
        do! hotReloadHeader.ClickAsync(LocatorClickOptions(Timeout = 3000.0f))
      with _ ->
        // Not stacked/found — the view may already be visible.
        ()
      do! Task.Delay(1500)
      let treeSel = ".view-id-sagefs-hotReload"
      do! VscodeHelpers.executeCommandUri page "sagefs.hotReloadWatchAll"
      // The tree rows render either per-file "● watching" descriptions or
      // directory "N/M watched" descriptions.
      let sw = Diagnostics.Stopwatch.StartNew()
      let mutable found = false
      while not found && sw.ElapsedMilliseconds < 30_000L do
        let! content = VscodeHelpers.selectorText page (treeSel + " .monaco-list-row")
        if content <> null && (content.Contains("watching") || content.Contains("watched"))
        then found <- true
        else do! Task.Delay(500)
      if not found then
        let! _ = VscodeHelpers.screenshot page "hr-fail"
        let! rows = VscodeHelpers.selectorText page (treeSel + " .monaco-list-row")
        let! sidebar =
          VscodeHelpers.selectorText page ".sidebar"
        let! status = VscodeHelpers.getStatusBarText page
        failwithf
          "HR-VSC: tree never showed watching/watched. rows='%s' sidebar='%s' status='%s'"
          rows sidebar status
      let! rows = VscodeHelpers.selectorText page (treeSel + " .monaco-list-row")
      let hasWatchState =
        (rows.Contains("watching") || rows.Contains("watched"))
        && not (rows.Contains("No session active"))
      do! VscodeHelpers.executeCommandUri page "sagefs.hotReloadRefresh"
      do! Task.Delay(3000)
      let! rows2 = VscodeHelpers.selectorText page (treeSel + " .monaco-list-row")
      Expect.isTrue "tree should show watch state after refresh"
        (rows2.Contains("watching") || rows2.Contains("watched"))
      Expect.isTrue "tree should show watch state (not 'No session active')" hasWatchState
    })

    vscodeExtTestFresh sampleWorkspace "LT-VSC-E2E: live-testing surfaces a failing edit then recovers to all-passing" (fun page -> task {
      let helloPath = IO.Path.Combine(sampleWorkspace, "Hello.fs")
      let canonicalAdd = "let add a b = a + b"
      let brokenAdd = "let add a b = a + b + 1"
      let readHello () = IO.File.ReadAllText helloPath
      let writeHello (content: string) =
        // The host/compiler can briefly hold the file; retry like LT-DASH.
        let mutable written = false
        let deadline = DateTime.UtcNow.AddSeconds(15.0)
        while not written && DateTime.UtcNow < deadline do
          try
            IO.File.WriteAllText(helloPath, content)
            written <- true
          with :? System.IO.IOException ->
            Threading.Thread.Sleep(500)
        Expect.isTrue "Hello.fs should be writable" written
      let original = readHello ()
      Expect.isTrue "fixture should contain the editable add" (original.Contains canonicalAdd)
      try
        // Enable live testing via the extension command; the daemon's baseline
        // run has the 11 tests passing.
        do! VscodeHelpers.executeCommandUri page "sagefs.enableLiveTesting"
        // Mutate `add` on disk: the edit-triggered rerun must surface the
        // failing "add infers int" test in the extension's status bar.
        writeHello (original.Replace(canonicalAdd, brokenAdd))
        let sw = Diagnostics.Stopwatch.StartNew()
        let mutable sawFailure = false
        while not sawFailure && sw.ElapsedMilliseconds < 120_000L do
          let! status = VscodeHelpers.getStatusBarText page
          if status.Contains("failed") then sawFailure <- true
          else do! Task.Delay(1000)
        if not sawFailure then
          let! _ = VscodeHelpers.screenshot page "lt-fail"
          let! status = VscodeHelpers.getStatusBarText page
          failwithf "LT-VSC: status bar never showed a failure after the edit. status='%s'" status
        // Restore: the rerun must recover to all green.
        writeHello original
        let sw2 = Diagnostics.Stopwatch.StartNew()
        let mutable sawGreen = false
        while not sawGreen && sw2.ElapsedMilliseconds < 120_000L do
          let! status = VscodeHelpers.getStatusBarText page
          if status.Contains("11/11 passed") then sawGreen <- true
          else do! Task.Delay(1000)
        if not sawGreen then
          let! _ = VscodeHelpers.screenshot page "lt-recover-fail"
          let! status = VscodeHelpers.getStatusBarText page
          failwithf "LT-VSC: status bar never recovered to 11/11 passed. status='%s'" status
        // Disable again and confirm the status bar returns to the off state.
        do! VscodeHelpers.executeCommandUri page "sagefs.disableLiveTesting"
        do! vscodeWaitForStatusText 30_000 page "Live testing off"
      finally
        // Restore Hello.fs regardless of outcome.
        try writeHello original with _ -> ()
    })
  ]
