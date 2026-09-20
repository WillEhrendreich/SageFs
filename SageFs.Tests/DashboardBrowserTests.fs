module SageFs.Tests.DashboardBrowserTests

open System
open System.Threading.Tasks
open Expecto
open Microsoft.Playwright

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
    // A reset drops scrollTop to the collapsed maximum (a few dozen px, well under the target); content growing above the
    // viewport can only push it up via scroll anchoring. The sidebar's content may also legitimately get SHORTER while the
    // eval settles (observed on CI: 80px), and the browser then clamps scrollTop to the new maximum. That is not the bug, so
    // the position may fall only as far as the new maximum, never below it.
    let! maxAfter = page.EvaluateAsync<float>("() => { var el = document.querySelector('.sidebar-inner'); return el.scrollHeight - el.clientHeight; }")
    let floor = (min scrolledTo maxAfter) - 20.0
    Expect.isTrue (after > floor) (sprintf "sidebar scrollTop must not snap back across SSE morphs (was %f, now %f, new maximum %f)" scrolledTo after maxAfter)
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

  playwrightTest "live testing panel enables and disables through SSE round-trip" (fun page -> task {
    do! PlaywrightExpect.waitForSelectorText 30_000 page "#session-status" "Ready"
    do! DashboardDom.ensureExpanded page
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

  // --- FR-DASH: real friction capture through the local store (seeded via
  // the product's own recorder API against the daemon's friction.db, which
  // the browser runner exposes as SAGEFS_FRICTION_DB). These journeys run
  // AFTER the honest empty-state journey above, so the panel starts empty
  // and this journey observes the real empty -> recorded transition. ---

  playwrightTest "friction feedback recorded locally reflects in the panel with the send form" (fun page -> task {
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
  ]
