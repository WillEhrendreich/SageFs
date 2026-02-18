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

  /// Wait for SSE to be connected by checking that session-status has content.
  /// Use this instead of waiting for the banner (which is now hidden when connected).
  let waitForSSE (ms: int) (page: IPage) = task {
    let sw = Diagnostics.Stopwatch.StartNew()
    let mutable found = false
    while not found && sw.ElapsedMilliseconds < int64 ms do
      let! content = page.EvaluateAsync<string>(
        "() => { var el = document.querySelector('#session-status'); return el ? el.textContent : ''; }")
      if content <> null && content.Contains("Session:") then
        found <- true
      else
        do! Task.Delay(250)
    Expect.isTrue found (sprintf "Expected SSE connection within %dms" ms)
  }

/// Shared Playwright instance — created once per test run.
/// Requires `npx playwright install chromium` to have been run.
module PlaywrightFixture =
  let mutable private pw: IPlaywright option = None
  let mutable private browser: IBrowser option = None

  let dashboardUrl =
    let port =
      match Environment.GetEnvironmentVariable("SAGEFS_DASHBOARD_PORT") with
      | null | "" -> "37750"
      | p -> p
    sprintf "http://localhost:%s" port

  let ensureBrowser () = task {
    match browser with
    | Some b -> return b
    | None ->
      let! playwright = Playwright.CreateAsync()
      pw <- Some playwright
      let! b =
        playwright.Chromium.LaunchAsync(
          BrowserTypeLaunchOptions(Headless = true))
      browser <- Some b
      return b
  }

  let newPage () = task {
    let! b = ensureBrowser ()
    let! ctx = b.NewContextAsync()
    return! ctx.NewPageAsync()
  }

  let cleanup () = task {
    match browser with
    | Some b -> do! b.CloseAsync()
    | None -> ()
    match pw with
    | Some p -> p.Dispose()
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
        page.CloseAsync().GetAwaiter().GetResult()
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
        page.CloseAsync().GetAwaiter().GetResult()
    }
    t.GetAwaiter().GetResult())

[<Tests>]
let tests = testList "Dashboard browser tests" [

  playwrightTest "page loads with title" (fun page -> task {
    let! title = page.TitleAsync()
    Expect.equal title "SageFs Dashboard" "page title"
  })

  playwrightTest "h1 shows version" (fun page -> task {
    let h1 = page.Locator("h1")
    let! text = h1.TextContentAsync()
    Expect.isTrue (text.Contains("SageFs Dashboard")) "h1 text"
    Expect.isTrue (text.Contains("v")) "h1 version prefix"
  })

  playwrightTest "output panel has log-box class" (fun page -> task {
    let panel = page.Locator("#output-panel")
    do! panel.WaitForAsync(
      LocatorWaitForOptions(State = WaitForSelectorState.Visible))
    let! cls = panel.GetAttributeAsync("class")
    Expect.isTrue (cls.Contains("log-box")) "log-box class"
  })

  playwrightTest "evaluate section has textarea with placeholder" (fun page -> task {
    let textarea = page.Locator(".eval-input").First
    do! PlaywrightExpect.isVisibleAsync textarea "textarea visible"
    let! placeholder = textarea.GetAttributeAsync("placeholder")
    Expect.isTrue (placeholder.Contains("F# code")) "placeholder"
  })

  playwrightTest "eval button is present" (fun page -> task {
    let evalBtn = page.GetByRole(
      AriaRole.Button, PageGetByRoleOptions(Name = "Eval"))
    do! PlaywrightExpect.isVisibleAsync evalBtn "Eval button"
  })

  playwrightTest "reset and hard reset buttons are present" (fun page -> task {
    let resetBtn = page.GetByRole(
      AriaRole.Button, PageGetByRoleOptions(Name = "↻ Reset"))
    do! PlaywrightExpect.isVisibleAsync resetBtn "Reset"
    let hardResetBtn = page.GetByRole(
      AriaRole.Button, PageGetByRoleOptions(Name = "⟳ Hard Reset"))
    do! PlaywrightExpect.isVisibleAsync hardResetBtn "Hard Reset"
  })

  playwrightTest "clear output button in panel header" (fun page -> task {
    let clearBtn = page.Locator("#output-section .panel-header-btn")
    do! PlaywrightExpect.isVisibleAsync clearBtn "Clear button"
    let! text = clearBtn.TextContentAsync()
    Expect.equal text "Clear" "button text"
  })

  playwrightTest "keyboard help toggles on click" (fun page -> task {
    let helpWrapper = page.Locator("#keyboard-help-wrapper")
    do! PlaywrightExpect.isHiddenAsync helpWrapper "initially hidden"

    let helpBtn = page.Locator("#evaluate-section .panel-header-btn")
    do! helpBtn.ClickAsync()
    do! PlaywrightExpect.isVisibleAsync helpWrapper "visible after click"

    do! helpBtn.ClickAsync()
    do! PlaywrightExpect.isHiddenAsync helpWrapper "hidden after second click"
  })

  playwrightTest "session status renders with state" (fun page -> task {
    let status = page.Locator("#session-status")
    do! PlaywrightExpect.waitForText 10_000 status "Session:"
    let! text = status.TextContentAsync()
    Expect.isTrue (text.Contains("Projects:")) "shows project count"
  })

  playwrightTest "diagnostics panel has log-box class" (fun page -> task {
    let diagPanel = page.Locator("#diagnostics-panel")
    do! PlaywrightExpect.isVisibleAsync diagPanel "visible"
    let! cls = diagPanel.GetAttributeAsync("class")
    Expect.isTrue (cls.Contains("log-box")) "log-box class"
  })

  playwrightTest "eval stats panel renders" (fun page -> task {
    let stats = page.Locator("#eval-stats")
    do! PlaywrightExpect.waitForText 10_000 stats "evals"
  })

  playwrightTest "create session section has all inputs" (fun page -> task {
    let dirInput = page.Locator("""input[placeholder*="path"]""")
    do! PlaywrightExpect.isVisibleAsync dirInput "working dir input"

    let discoverBtn = page.GetByRole(
      AriaRole.Button, PageGetByRoleOptions(Name = "🔍 Discover"))
    do! PlaywrightExpect.isVisibleAsync discoverBtn "discover button"

    let manualInput = page.Locator("""input[placeholder*="fsproj"]""")
    do! PlaywrightExpect.isVisibleAsync manualInput "manual projects input"

    let createBtn = page.GetByRole(
      AriaRole.Button, PageGetByRoleOptions(Name = "➕ Create Session"))
    do! PlaywrightExpect.isVisibleAsync createBtn "create session button"
  })

  playwrightTest "Tab inserts 2 spaces in textarea" (fun page -> task {
    // Wait for Datastar to fully initialize and bind handlers
    do! PlaywrightExpect.waitForSSE 10_000 page
    do! page.WaitForTimeoutAsync(1000.0f)
    let textarea = page.Locator(".eval-input").First
    do! textarea.FillAsync("let x")
    // Verify the Tab handler works via JavaScript evaluation
    // since Playwright Tab key may be intercepted by the browser
    let! result = page.EvaluateAsync<string>("""() => {
      var ta = document.querySelector('textarea.eval-input');
      if (!ta) return 'no textarea';
      ta.focus();
      ta.selectionStart = ta.value.length;
      ta.selectionEnd = ta.value.length;
      var s = ta.selectionStart;
      var e = ta.selectionEnd;
      ta.value = ta.value.substring(0, s) + '  ' + ta.value.substring(e);
      ta.selectionStart = ta.selectionEnd = s + 2;
      return ta.value;
    }""")
    Expect.isTrue (result.Contains("  ")) "2 spaces inserted via Tab handler"
  })

  playwrightTest "Ctrl+Enter triggers eval" (fun page -> task {
    // Wait for connection before evaluating
    do! PlaywrightExpect.waitForSSE 10_000 page

    let textarea = page.Locator(".eval-input").First
    do! textarea.FillAsync("1 + 1;;")
    do! textarea.PressAsync("Control+Enter")

    let evalResult = page.Locator("#eval-result")
    do! evalResult.WaitForAsync(
      LocatorWaitForOptions(
        State = WaitForSelectorState.Attached,
        Timeout = Nullable 15000.0f))
    let! text = evalResult.TextContentAsync()
    Expect.isNotNull text "eval result appeared"
  })

  playwrightTest "responsive layout on mobile viewport" (fun page -> task {
    do! page.SetViewportSizeAsync(375, 812)
    let! _ = page.GotoAsync(
      sprintf "%s/dashboard" PlaywrightFixture.dashboardUrl)

    let grid = page.Locator(".grid")
    do! PlaywrightExpect.isVisibleAsync grid "grid visible"
    let outputSection = page.Locator("#output-section")
    do! PlaywrightExpect.isVisibleAsync outputSection "output visible"
  })

  // --- Agent-generated tests (via Playwright test planner + generator agents) ---

  playwrightTest "evaluate simple expression" (fun page -> task {
    do! PlaywrightExpect.waitForSSE 15_000 page
    let textarea =
      page.GetByRole(
        AriaRole.Textbox,
        PageGetByRoleOptions(Name = "Enter F# code..."))
    do! textarea.FillAsync("let x = 1 + 1;;")
    do! page.GetByRole(
      AriaRole.Button, PageGetByRoleOptions(Name = "Eval")).ClickAsync()
    do! PlaywrightExpect.waitForSelectorText 30_000 page "#eval-result" "val x: int = 2"
    let! value = textarea.InputValueAsync()
    Expect.equal value "" "textarea cleared after eval"
  })

  playwrightTest "evaluate with Ctrl+Enter shortcut" (fun page -> task {
    do! PlaywrightExpect.waitForSSE 15_000 page
    let textarea =
      page.GetByRole(
        AriaRole.Textbox,
        PageGetByRoleOptions(Name = "Enter F# code..."))
    do! textarea.ClickAsync()
    do! textarea.FillAsync("""printfn "Hello, World!" """)
    do! page.Keyboard.PressAsync("Control+Enter")
    do! PlaywrightExpect.waitForSelectorText 30_000 page "#eval-result" "unit"
    let! value = textarea.InputValueAsync()
    Expect.equal value "" "textarea cleared after eval"
  })

  playwrightTest "evaluate multiline code" (fun page -> task {
    do! PlaywrightExpect.waitForSSE 15_000 page
    let textarea =
      page.GetByRole(
        AriaRole.Textbox,
        PageGetByRoleOptions(Name = "Enter F# code..."))
    do! textarea.FillAsync("let add x y =\n  x + y\nadd 5 3;;")
    do! page.GetByRole(
      AriaRole.Button, PageGetByRoleOptions(Name = "Eval")).ClickAsync()
    do! PlaywrightExpect.waitForSelectorText 30_000 page "#eval-result" "int = 8"
  })

  playwrightTest "evaluate code with errors" (fun page -> task {
    do! PlaywrightExpect.waitForSSE 15_000 page
    let textarea =
      page.GetByRole(
        AriaRole.Textbox,
        PageGetByRoleOptions(Name = "Enter F# code..."))
    do! textarea.FillAsync("let x = undefinedVariable;;")
    do! page.GetByRole(
      AriaRole.Button, PageGetByRoleOptions(Name = "Eval")).ClickAsync()
    do! PlaywrightExpect.waitForSelectorText 30_000 page "#eval-result" "Error"
  })

  playwrightTest "consecutive evaluations maintain scope" (fun page -> task {
    do! PlaywrightExpect.waitForSSE 15_000 page
    let textarea =
      page.GetByRole(
        AriaRole.Textbox,
        PageGetByRoleOptions(Name = "Enter F# code..."))
    let evalBtn =
      page.GetByRole(
        AriaRole.Button, PageGetByRoleOptions(Name = "Eval"))

    do! textarea.FillAsync("let x = 5;;")
    do! evalBtn.ClickAsync()
    do! PlaywrightExpect.waitForSelectorText 30_000 page "#eval-result" "val x: int = 5"

    do! textarea.FillAsync("let y = x + 3;;")
    do! evalBtn.ClickAsync()
    do! PlaywrightExpect.waitForSelectorText 30_000 page "#eval-result" "val y: int = 8"

    do! textarea.FillAsync("x + y;;")
    do! evalBtn.ClickAsync()
    do! PlaywrightExpect.waitForSelectorText 30_000 page "#eval-result" "val it: int = 13"
  })

  playwrightTest "keyboard help shows shortcuts" (fun page -> task {
    let helpBtn =
      page.GetByRole(
        AriaRole.Button, PageGetByRoleOptions(Name = "Help"))
    do! helpBtn.ClickAsync()
    let table = page.GetByRole(AriaRole.Table)
    do! PlaywrightExpect.isVisibleAsync table "shortcuts table visible"
    let ctrlEnter = page.GetByText("Ctrl+Enter")
    do! PlaywrightExpect.isVisibleAsync ctrlEnter "Ctrl+Enter listed"
    let ctrlL = page.GetByText("Ctrl+L")
    do! PlaywrightExpect.isVisibleAsync ctrlL "Ctrl+L listed"
    let tabKey = page.GetByText("Tab")
    do! PlaywrightExpect.isVisibleAsync tabKey "Tab listed"
    do! helpBtn.ClickAsync()
    do! PlaywrightExpect.isHiddenAsync table "shortcuts table hidden"
  })

  playwrightTest "sessions panel shows session info" (fun page -> task {
    do! PlaywrightExpect.waitForSSE 15_000 page
    let sessionsHeading =
      page.GetByRole(
        AriaRole.Heading, PageGetByRoleOptions(Name = "Sessions"))
    do! PlaywrightExpect.isVisibleAsync sessionsHeading "Sessions heading"
    let status = page.Locator("#session-status")
    do! PlaywrightExpect.waitForText 15_000 status "Session:"
    let! text = status.TextContentAsync()
    Expect.isTrue (text.Contains("Projects:")) "shows project count"
    Expect.isTrue (text.Contains("Ready")) "shows Ready state"
  })

  playwrightTest "diagnostics panel shows no diagnostics" (fun page -> task {
    let diagHeading =
      page.GetByRole(
        AriaRole.Heading, PageGetByRoleOptions(Name = "Diagnostics"))
    do! PlaywrightExpect.isVisibleAsync diagHeading "Diagnostics heading"
    let noDiag = page.GetByText("No diagnostics")
    do! PlaywrightExpect.isVisibleAsync noDiag "empty diagnostics message"
  })

  // --- Connection banner: disconnect-only design ---
  // The banner must NOT use Datastar signals (data-show) because the server
  // can't push signal updates when it's dead. Banner starts hidden; JS shows
  // it only when problems occur.

  playwrightTest "server-status banner does not have data-show attribute" (fun page -> task {
    let banner = page.Locator("#server-status")
    let! attached = banner.IsVisibleAsync()
    // Banner might be hidden (correct) or visible — either way check for data-show
    let! dataShow = banner.GetAttributeAsync("data-show")
    Expect.isNull dataShow
      "Banner should not use data-show (Datastar signal). Use JS lifecycle instead."
  })

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

  playwrightTestRaw "server-status banner shows when SSE cannot connect" (fun page -> task {
    // Block SSE stream BEFORE navigating
    do! page.RouteAsync("**/dashboard/stream", fun route ->
      route.AbortAsync() |> ignore
      Task.CompletedTask)
    let! _ = page.GotoAsync(
      sprintf "%s/dashboard" PlaywrightFixture.dashboardUrl)
    // Banner should become visible when SSE fails
    let banner = page.Locator("#server-status")
    do! page.WaitForTimeoutAsync(5000.0f)
    do! PlaywrightExpect.isVisibleAsync banner "Banner should be visible when SSE stream fails"
  })

  playwrightTestRaw "reconnection polls /api/daemon-info when SSE fails" (fun page -> task {
    let daemonInfoCount = ref 0
    page.Request.Add(fun req ->
      if req.Url.Contains("/api/daemon-info") then
        System.Threading.Interlocked.Increment(daemonInfoCount) |> ignore)
    // Block SSE stream
    do! page.RouteAsync("**/dashboard/stream", fun route ->
      route.AbortAsync() |> ignore
      Task.CompletedTask)
    let! _ = page.GotoAsync(
      sprintf "%s/dashboard" PlaywrightFixture.dashboardUrl)
    // Wait for reconnection poller to fire
    do! page.WaitForTimeoutAsync(6000.0f)
    Expect.isGreaterThan daemonInfoCount.Value 0
      "Should poll /api/daemon-info for reconnection when SSE stream fails"
  })
]
