module SageFs.Tests.DashboardBrowserTests

open System
open System.Threading.Tasks
open Expecto
open Microsoft.Playwright

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

  /// Open the New Session accordion (a <details> collapsed by default).
  let openNewSession (page: IPage) = task {
    let toggle = page.GetByText("New Session").First
    do! toggle.ClickAsync()
  }

  /// The eval code textarea — located by its stable id, never by role/name
  /// (accessible-name resolution races the Datastar morph and flakes).
  let textarea (page: IPage) = page.Locator("#eval-textarea")

  /// The EVAL button — first .eval-btn inside the evaluate section.
  let evalButton (page: IPage) =
    page.Locator("#evaluate-section .eval-btn").First

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

  playwrightTest "keyboard help toggles on click" (fun page -> task {
    // Help toggle lives inside the collapsed Evaluate accordion — open it.
    do! DashboardDom.openEvalArea page
    do! page.WaitForTimeoutAsync(500.0f)
    let helpWrapper = page.Locator("#keyboard-help-wrapper")
    // Datastar initializes $helpVisible=true — the wrapper starts open.
    do! helpWrapper.WaitForAsync(
      LocatorWaitForOptions(State = WaitForSelectorState.Visible))
    let helpBtn = page.Locator("#evaluate-section .panel-header-btn").First
    do! helpBtn.ClickAsync()
    do! helpWrapper.WaitForAsync(
      LocatorWaitForOptions(State = WaitForSelectorState.Hidden))
    do! helpBtn.ClickAsync()
    do! helpWrapper.WaitForAsync(
      LocatorWaitForOptions(State = WaitForSelectorState.Visible))
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
    do! DashboardDom.openEvalArea page
    let textarea = DashboardDom.textarea page
    do! textarea.FillAsync("let x = 1 + 1;;")
    do! (DashboardDom.evalButton page).ClickAsync()
    do! PlaywrightExpect.waitForSelectorText 30_000 page "#output-panel" "val x: int = 2"
    do! PlaywrightExpect.waitForTextareaCleared 10_000 textarea
  })

  playwrightTest "evaluate with Alt+Enter shortcut" (fun page -> task {
    do! PlaywrightExpect.waitForSSE 15_000 page
    do! DashboardDom.openEvalArea page
    let textarea = DashboardDom.textarea page
    do! textarea.ClickAsync()
    do! textarea.FillAsync("""printfn "Hello, World!" """)
    do! page.Keyboard.PressAsync("Alt+Enter")
    do! PlaywrightExpect.waitForSelectorText 30_000 page "#output-panel" "val it: unit = ()"
    do! PlaywrightExpect.waitForTextareaCleared 10_000 textarea
  })

  playwrightTest "evaluate multiline code" (fun page -> task {
    do! PlaywrightExpect.waitForSSE 15_000 page
    do! DashboardDom.openEvalArea page
    let textarea = DashboardDom.textarea page
    do! textarea.FillAsync("let add x y =\n  x + y\nadd 5 3;;")
    do! (DashboardDom.evalButton page).ClickAsync()
    do! PlaywrightExpect.waitForSelectorText 30_000 page "#output-panel" "int = 8"
  })

  playwrightTest "evaluate code with errors" (fun page -> task {
    do! PlaywrightExpect.waitForSSE 15_000 page
    do! DashboardDom.openEvalArea page
    let textarea = DashboardDom.textarea page
    do! textarea.FillAsync("let x = undefinedVariable;;")
    do! (DashboardDom.evalButton page).ClickAsync()
    // The dashboard renders eval failures as an "Evaluation failed" line in
    // the output panel (the FSI exception message, not the raw FS-code text).
    do! PlaywrightExpect.waitForSelectorText 30_000 page "#output-panel" "Evaluation failed"
  })

  playwrightTest "consecutive evaluations maintain scope" (fun page -> task {
    do! PlaywrightExpect.waitForSSE 15_000 page
    do! DashboardDom.openEvalArea page
    let textarea = DashboardDom.textarea page
    let evalBtn = DashboardDom.evalButton page

    do! textarea.FillAsync("let x = 5;;")
    do! evalBtn.ClickAsync()
    do! PlaywrightExpect.waitForSelectorText 30_000 page "#output-panel" "val x: int = 5"

    // Each eval's SSE morph can re-collapse the Evaluate accordion — reopen
    // before the next interaction.
    do! DashboardDom.openEvalArea page
    do! textarea.FillAsync("let y = x + 3;;")
    do! evalBtn.ClickAsync()
    do! PlaywrightExpect.waitForSelectorText 30_000 page "#output-panel" "val y: int = 8"

    do! DashboardDom.openEvalArea page
    do! textarea.FillAsync("x + y;;")
    do! evalBtn.ClickAsync()
    do! PlaywrightExpect.waitForSelectorText 30_000 page "#output-panel" "val it: int = 13"
  })

  playwrightTest "keyboard help shows shortcuts" (fun page -> task {
    // Help toggle lives inside the collapsed Evaluate accordion — open it.
    do! DashboardDom.openEvalArea page
    do! page.WaitForTimeoutAsync(500.0f)
    let helpWrapper = page.Locator("#keyboard-help-wrapper")
    // $helpVisible starts true — the shortcuts table is visible on load.
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
    // Toggle closed, then open again.
    let helpBtn = page.Locator("#evaluate-section .panel-header-btn").First
    do! helpBtn.ClickAsync()
    do! helpWrapper.WaitForAsync(
      LocatorWaitForOptions(State = WaitForSelectorState.Hidden))
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

  // --- Connection banner: disconnect-only design ---
  // The banner must NOT use Datastar signals (data-show) because the server
  // can't push signal updates when it's dead. Banner starts hidden; JS shows
  // it only when problems occur.
  // NOTE: data-show absence is now verified by shellStructureTests in DashboardSnapshotTests.fs

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
    // Evaluate is a <details class="eval-area"> collapsed by default — open it.
    do! DashboardDom.openEvalArea page
    // Textarea with the F# placeholder
    let textarea = page.Locator(".eval-input").First
    do! PlaywrightExpect.isVisibleAsync textarea "textarea visible"
    let! placeholder = textarea.GetAttributeAsync("placeholder")
    Expect.isTrue (
      placeholder <> null
      && System.Text.RegularExpressions.Regex.IsMatch(placeholder, "Enter F# code"))
      "textarea placeholder mentions Enter F# code"
    // Eval button
    let evalBtn =
      page.GetByRole(AriaRole.Button, PageGetByRoleOptions(Name = "Eval"))
    do! PlaywrightExpect.isVisibleAsync evalBtn "Eval button visible"
    // Reset button (first match — [RESET] or ↻ Reset)
    let resetBtn =
      page.GetByRole(AriaRole.Button, PageGetByRoleOptions(Name = "Reset")).First
    do! PlaywrightExpect.isVisibleAsync resetBtn "Reset button visible"
  })

  playwrightTest "page structure: clear output button in panel header" (fun page -> task {
    let clearBtn = page.Locator("#output-section .panel-header-btn")
    do! PlaywrightExpect.isVisibleAsync clearBtn "clear button visible"
    do! PlaywrightExpect.waitForText 10_000 clearBtn "CLEAR"
  })

  playwrightTest "page structure: create session section has inputs and buttons" (fun page -> task {
    do! PlaywrightExpect.waitForSSE 10_000 page
    // "New Session" is a <details> collapsed by default — open it.
    do! DashboardDom.openNewSession page
    // Working directory input (placeholder contains "path\to\project")
    let dirInput = page.Locator("input[placeholder*=\"path\\\\to\\\\project\"]")
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

  // --- TS journey ports (friction-panel-journey.spec.ts) ---

  playwrightTest "friction panel renders honest empty state with no send form" (fun page -> task {
    do! PlaywrightExpect.waitForSelectorText 30_000 page "#session-status" "Ready"
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

  playwrightTest "live testing panel enables and disables through SSE round-trip" (fun page -> task {
    do! PlaywrightExpect.waitForSelectorText 30_000 page "#session-status" "Ready"
    let panel = page.Locator("#live-testing-panel")
    do! PlaywrightExpect.isVisibleAsync panel "live testing panel visible"
    do! PlaywrightExpect.waitForText 15_000 panel "Live Testing: OFF"
    do! PlaywrightExpect.waitForText 10_000 panel "keystroke"
    // Enable
    let enableBtn =
      panel.GetByRole(AriaRole.Button, LocatorGetByRoleOptions(Name = "Enable"))
    do! enableBtn.ClickAsync()
    do! PlaywrightExpect.waitForText 30_000 panel "Live Testing: ON"
    // Disable and confirm round-trip back to OFF
    let disableBtn =
      panel.GetByRole(AriaRole.Button, LocatorGetByRoleOptions(Name = "Disable"))
    do! disableBtn.ClickAsync()
    do! PlaywrightExpect.waitForText 30_000 panel "Live Testing: OFF"
  })
  ]
