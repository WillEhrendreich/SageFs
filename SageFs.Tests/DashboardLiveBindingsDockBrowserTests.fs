/// The bottom dock in a real browser (Playwright.NET, inside the Expecto suite): the live bindings where the Evaluate bar used to
/// be, and Evaluate as a small popover. One journey over the state matrix, on its OWN daemon (fresh ports, fresh SAGEFS_DATA_DIR,
/// `--no-resume`) because it needs a daemon with no session first and then one with a session of its own:
///
///   1. no sessions, bare URL: the full shell, the session picker, and a dock that says why it has nothing (and offers no pin)
///   2. a session, bare URL: the shell, the session's card, its output
///   3. a REPL session with bindings: evaluate through the popover, the bindings appear without a reload, each row says its
///      kind, a changed value flashes and an unchanged one does not, the filter and the pin to the top survive the morph
///   4. a session with no bindings: ONE line that says why, and the pin opens the empty pane, which says it again
///   5. a click on the sidebar's session row
///   6. narrow and wide viewports: nothing overflows sideways, the dock and the popover stay inside the viewport
///   7. the popover: closed at first, opens (button, or key e), takes the code, submits, shows the result, closes (button, Escape)
///
/// Every step proves zero console errors and zero Datastar PatchElementsNoTargetsFound, and states 2, 3, 4 and 6 save screenshots.
module SageFs.Tests.DashboardLiveBindingsDockBrowserTests

open System
open System.IO
open System.Threading.Tasks
open Expecto
open Expecto.Flip
open Microsoft.Playwright
open SageFs.Server
open SageFs.Server.DockPanes
open SageFs.Tests.DashboardBrowserTests

module Integration = SageFs.Tests.TestInfrastructure.Integration
module Sw = SageFs.Tests.DashboardSessionSwitchOutputBrowserTests

/// Console errors, page errors and Datastar's own complaint when a morph has no target, all of which fail the journey.
let private watchErrors (page: IPage) : Collections.Generic.List<string> =
  let errors = Collections.Generic.List<string>()
  page.Console.Add(fun message ->
    if message.Type = "error" || message.Text.Contains "PatchElementsNoTargetsFound" then
      errors.Add(sprintf "[console %s] %s" message.Type message.Text))
  page.PageError.Add(fun error -> errors.Add(sprintf "[pageerror] %s" error))
  errors

let private expectNoErrors (errors: Collections.Generic.List<string>) (when': string) =
  List.ofSeq errors |> Expect.isEmpty (sprintf "zero console and Datastar errors %s, got: %s" when' (String.concat " | " errors))

let private shot (page: IPage) (name: string) = task {
  let path = Path.Combine(PlaywrightFixture.screenshotDir, sprintf "dock-%s.png" name)
  do! PlaywrightFixture.takeScreenshot page false path
}

/// Wait until `script` (a JS predicate over `arg`) is true in the page, or say what was being waited for.
let private waitJs (page: IPage) (what: string) (budgetMs: int) (script: string) (arg: obj) : Task<unit> = task {
  try
    let! _ = page.WaitForFunctionAsync(script, arg, PageWaitForFunctionOptions(Timeout = float32 budgetMs))
    ()
  with ex -> Tests.failtestf "timed out after %dms waiting for %s: %s" budgetMs what ex.Message
}

let private visibleJs = "sel => { const e = document.querySelector(sel); if (!e) return false; const r = e.getBoundingClientRect(); return r.width > 0 && r.height > 0 && e.checkVisibility({ visibilityProperty: true }); }"

let private waitVisible (page: IPage) (selector: string) = waitJs page (sprintf "%s to be visible" selector) BrowserWaits.panelUpdates visibleJs (box selector)

let private waitHidden (page: IPage) (selector: string) =
  waitJs page (sprintf "%s to be hidden" selector) BrowserWaits.panelUpdates (sprintf "sel => !(%s)(sel)" visibleJs) (box selector)

let private textOf (page: IPage) (selector: string) : Task<string> =
  page.EvaluateAsync<string>("sel => { const e = document.querySelector(sel); return e ? e.innerText : ''; }", selector)

let private waitText (page: IPage) (selector: string) (text: string) =
  PlaywrightExpect.waitForSelectorText BrowserWaits.daemonWork page selector text

/// What sticks out sideways, or out of the viewport, at the width the page is now.
let private layoutProblems (page: IPage) : Task<string> =
  page.EvaluateAsync<string>(
    """() => {
      const problems = [];
      const w = window.innerWidth, h = window.innerHeight;
      if (document.scrollingElement.scrollWidth > w + 1)
        problems.push('the page scrolls sideways: ' + document.scrollingElement.scrollWidth + ' > ' + w);
      const dock = document.querySelector('#live-dock');
      if (dock) {
        const d = dock.getBoundingClientRect();
        if (d.left < -1 || d.right > w + 1) problems.push('the dock sticks out sideways: ' + Math.round(d.left) + '..' + Math.round(d.right) + ' of ' + w);
        for (const el of dock.querySelectorAll('.live-pane-bar > *, .live-dock-bar > *, .live-pane-head > *, .live-binding-node')) {
          const r = el.getBoundingClientRect();
          if (r.width === 0 && r.height === 0) continue;
          if (r.right > d.right + 1 || r.left < d.left - 1)
            problems.push(el.className + ' sticks out of the dock: ' + Math.round(r.left) + '..' + Math.round(r.right) + ' vs ' + Math.round(d.left) + '..' + Math.round(d.right));
        }
      }
      const pop = document.querySelector('.eval-pop[open] .eval-pop-body');
      if (pop) {
        const p = pop.getBoundingClientRect();
        if (p.left < -1 || p.right > w + 1 || p.top < -1 || p.bottom > h + 1)
          problems.push('the evaluate popover is not inside the viewport: ' + [p.left, p.top, p.right, p.bottom].map(Math.round).join(',') + ' in ' + w + 'x' + h);
      }
      return problems.join(' | ');
    }""")

/// Open the Evaluate popover with the button and wait until its box is on screen.
let private openPopover (page: IPage) = task {
  let! isOpen = page.EvaluateAsync<bool>("() => document.querySelector('#evaluate-section').open")
  if not isOpen then do! page.Locator("[data-testid=evaluate-toggle]").ClickAsync()
  do! waitVisible page "#evaluate-section .eval-pop-body"
}

let private closePopoverWithButton (page: IPage) = task {
  do! page.Locator("[data-testid=evaluate-close]").ClickAsync()
  do! waitHidden page "#evaluate-section .eval-pop-body"
}

/// Evaluate `code` the way a user does, through the popover, and wait until the output panel shows `marker`.
let private evaluateThroughPopover (page: IPage) (code: string) (marker: string) = task {
  do! openPopover page
  do! page.Locator("#eval-textarea").FillAsync code
  do! page.Locator("#evaluate-section .eval-btn").First.ClickAsync()
  do! waitText page "#output-panel" marker
}

/// Make the daemon push once more and wait until that push has reached the page, so whatever the journey set before it has
/// been through a morph. An idle page is NOT pushed to (a push that changes nothing is suppressed), so the push is caused:
/// the session prints a marker, and the marker reaching the output panel means a whole-`#main` morph, dock included, landed.
let private waitForMorph (daemon: Sw.Daemon) (subject: Sw.Subject) (page: IPage) = task {
  do! Sw.printMarker daemon subject
  do! waitText page "#output-panel" subject.Marker
}

let journey () : Task<unit> = task {
  let daemon = Sw.startDaemon ()
  let mutable playwright: IPlaywright option = None
  let mutable browser: IBrowser option = None
  let mutable failure: exn option = None
  try
    let! healthy = Sw.waitHealthy daemon
    if not healthy then
      Sw.dumpLogs daemon
      Tests.failtestf "the daemon on port %d never became healthy" daemon.McpPort

    let! pw = Playwright.CreateAsync()
    let! chromium = pw.Chromium.LaunchAsync(BrowserTypeLaunchOptions(Headless = true))
    playwright <- Some pw
    browser <- Some chromium
    let! ctx = chromium.NewContextAsync(BrowserNewContextOptions(ViewportSize = ViewportSize(Width = 1400, Height = 850)))
    let! page = ctx.NewPageAsync()
    let errors = watchErrors page
    let dashboard = sprintf "http://localhost:%d/dashboard" daemon.DashboardPort

    // ── 1. no sessions + bare URL ───────────────────────────────────────────────────────────────────────────────────────
    let! _ = page.GotoAsync dashboard
    do! waitVisible page "#live-dock-bar"
    let! noSessionText = textOf page "#live-dock-bar"
    noSessionText |> Expect.stringContains "the dock says there is no session" (CollapsedBecause.text NoSessionOpen)
    do! waitVisible page "#sidebar"
    do! waitVisible page "#session-picker"
    let! pinCount = page.Locator("#live-dock .live-pane-pin").CountAsync()
    pinCount |> Expect.equal "no pin to offer: there is nothing to pin it on" 0
    let! pickerText = textOf page "#session-picker"
    (pickerText.Length > 0) |> Expect.isTrue "the session picker is the landing, not a blank"
    let! problems1 = layoutProblems page
    problems1 |> Expect.equal (sprintf "nothing overflows with no session: %s" problems1) ""
    expectNoErrors errors "with no session on a bare URL"

    // ── a session ──────────────────────────────────────────────────────────────────────────────────────────────────────
    do! Sw.createSession daemon Sw.projectA Sw.sampleA
    let! ready =
      Sw.waitUntil (TestTimeouts.asMs SageFs.Timeouts.browserJourneyWarmup) (fun () -> task {
        try
          let! now = Sw.sessionsNow daemon
          return now.Length >= 1 && now |> List.forall (fun (_, status, _) -> status = "Ready")
        with _ -> return false
      })
    if not ready then Sw.dumpLogs daemon
    ready |> Expect.isTrue "the session reached Ready on the daemon"
    let! sessions = Sw.sessionsNow daemon
    let sessionId = Sw.idFor sessions Sw.sampleA
    let runTag = Guid.NewGuid().ToString("N").Substring(0, 8)
    let push (n: string) =
      waitForMorph daemon ({ Id = sessionId; WorkingDirectory = Sw.sampleA; Marker = sprintf "DOCKPUSH-%s-%s" runTag n } : Sw.Subject) page

    // ── 2. a session + bare URL ────────────────────────────────────────────────────────────────────────────────────────
    let! _ = page.GotoAsync dashboard
    do! PlaywrightExpect.waitForSSE BrowserWaits.panelUpdates page
    do! waitVisible page "#sidebar"
    do! waitVisible page (sprintf "#session-card-%s" sessionId)
    do! waitVisible page "#output-panel"
    // The Evaluate bar is gone: nothing but a small button, with its box closed.
    let! popoverOpen = page.EvaluateAsync<bool>("() => document.querySelector('#evaluate-section').open")
    popoverOpen |> Expect.isFalse "Evaluate is closed until someone opens it"
    do! waitHidden page "#evaluate-section .eval-pop-body"
    let! toggle = page.Locator("[data-testid=evaluate-toggle]").BoundingBoxAsync()
    // It carries its word ("λ Evaluate"), so it is as tall as every other control in the bar and as wide as the word needs: a bare
    // glyph in a 28px square was read by three reviewers as "a mystery button".
    let! toggleText = page.Locator("[data-testid=evaluate-toggle]").InnerTextAsync()
    toggleText |> Expect.stringContains "the button says what it is" "Evaluate"
    (abs (toggle.Height - 28.0f) < 1.0f && toggle.Width >= 28.0f)
    |> Expect.isTrue (sprintf "the Evaluate button is 28px tall, like the other controls, and at least a square wide, got %fx%f" toggle.Width toggle.Height)
    expectNoErrors errors "on a bare URL with a session"

    // ── 4. a session with no bindings ──────────────────────────────────────────────────────────────────────────────────
    do! waitVisible page "#live-dock-bar"
    let! noBindingsText = textOf page "#live-dock-bar"
    noBindingsText |> Expect.stringContains "ONE line that says why there are no bindings" (CollapsedBecause.text NothingBoundYet)
    do! waitHidden page "#bindings-panel"
    do! shot page "4a-no-bindings-reason"
    do! shot page "2-session-bare-url"
    // The pin opens the empty pane, which says it again instead of showing a blank.
    do! page.Locator("#live-bar-pin").ClickAsync()
    do! waitVisible page "#bindings-panel"
    do! waitHidden page "#live-dock-bar"
    let! emptyText = textOf page "[data-testid=live-bindings-empty]"
    emptyText |> Expect.stringContains "the pinned empty pane says why" (CollapsedBecause.text NothingBoundYet)
    let! pinnedState = page.Locator("#live-pane-pin").GetAttributeAsync("aria-pressed")
    pinnedState |> Expect.equal "the pin reads as pressed" "true"
    do! shot page "4b-pinned-empty"
    // And it survives a morph: make the daemon push once more and wait for that push to reach the page.
    do! push "pinned"
    do! waitVisible page "#bindings-panel"
    do! page.Locator("#live-pane-pin").ClickAsync()
    do! waitVisible page "#live-dock-bar"
    do! waitHidden page "#bindings-panel"
    expectNoErrors errors "while pinning the empty pane open and shut"

    // ── 5. click the sidebar's session row ────────────────────────────────────────────────────────────────────────────
    do! page.Locator(sprintf "#session-card-%s" sessionId).ClickAsync()
    do! waitJs page "the page to view the session" BrowserWaits.panelUpdates
          "id => { const m = document.querySelector('#main'); return !!m && m.getAttribute('data-viewing-session-id') === id; }" (box sessionId)
    do! waitVisible page "#live-dock"
    do! waitVisible page "#output-panel"
    expectNoErrors errors "after clicking the session row"

    // ── 3 + 7. evaluate through the popover; the bindings appear without a reload ─────────────────────────────────────
    let! _ = page.EvaluateAsync("() => { window.__noReload = 'still the same page'; }")
    let tag = Guid.NewGuid().ToString("N").Substring(0, 8)
    let code =
      sprintf
        "type Person = { Name: string; Age: int }\nlet person = { Name = \"Ada\"; Age = 36 }\nlet scores = [ 1; 2; 3 ]\nlet counter = ref 0\nprintfn \"DOCKPROBE-%s\";;" tag
    do! evaluateThroughPopover page code (sprintf "DOCKPROBE-%s" tag)
    do! waitVisible page "#bindings-panel"
    do! waitText page "#bindings-panel" "person"
    do! waitText page "#bindings-panel" "scores"
    let! sentinel = page.EvaluateAsync<string>("() => window.__noReload")
    sentinel |> Expect.equal "the bindings appeared with no page reload" "still the same page"
    let! paneText = textOf page "#bindings-panel"
    paneText |> Expect.stringContains "the pane counts the user's bindings" "bindings"
    paneText.Contains "_SageFs" |> Expect.isFalse "the tooling's own bindings are not listed"
    // A row's kind is a glyph with the word on hover and as its accessible name, and not a boxed word on every row.
    let! recordGlyphs = page.Locator("#bindings-panel .live-glyph[title=record][aria-label=record]").CountAsync()
    (recordGlyphs >= 1) |> Expect.isTrue "a record row carries a record glyph with the word as its name"
    let! listGlyphs = page.Locator("#bindings-panel .live-glyph[title=list][aria-label=list]").CountAsync()
    (listGlyphs >= 1) |> Expect.isTrue "a list row carries a list glyph with the word as its name"
    let! boxedKinds = page.Locator("#bindings-panel .live-kind").CountAsync()
    boxedKinds |> Expect.equal "no boxed kind word on any row" 0
    // The title is a panel heading: its words, in the brightest text colour on the page (it was blue, one more small caption among
    // five; it is now the largest and brightest word in the bar), and never red, which is for failure.
    let! titleText = textOf page "#bindings-panel .live-pane-title"
    titleText.Trim() |> Expect.equal "the title is its words and no red circle" "Live bindings"
    let! titleColours =
      page.EvaluateAsync<string>(
        """() => {
          const title = document.querySelector('#bindings-panel .live-pane-title');
          const colourOf = token => { const probe = document.createElement('span'); probe.style.color = 'var(' + token + ')'; document.body.appendChild(probe); const c = getComputedStyle(probe).color; probe.remove(); return c; };
          const own = getComputedStyle(title).color;
          return own === colourOf('--fg-default') ? 'text colour' : own === colourOf('--fg-red') ? 'red' : 'other: ' + own;
        }""")
    titleColours |> Expect.equal "the title is the text colour, the brightest on the page" "text colour"
    let! titleSize = page.EvaluateAsync<float>("() => parseFloat(getComputedStyle(document.querySelector('#bindings-panel .live-pane-title')).fontSize)")
    (titleSize >= 13.0) |> Expect.isTrue (sprintf "the title is larger than the pane's 12px captions, got %fpx" titleSize)
    // With no app running the output pane draws nothing visible.
    let! outputBar = page.EvaluateAsync<float>("() => { const e = document.querySelector('#app-output-bar'); return e ? e.getBoundingClientRect().height : 0; }")
    outputBar |> Expect.equal "the app-output pane takes no height when no app has written" 0.0
    do! waitHidden page "#live-dock-bar"
    // The popover stays out of the way: close it, and the bindings are what is left at the bottom.
    // Ten bindings in the dock at its default ceiling show at least eight rows: one line a row, and nothing under a REPL-only row.
    do! evaluateThroughPopover page (sprintf "let a1 = 1\nlet a2 = 2\nlet a3 = 3\nlet a4 = 4\nlet a5 = 5\nlet a6 = 6\nlet a7 = 7\nprintfn \"DOCKPROBE-%s-ten\";;" tag) (sprintf "DOCKPROBE-%s-ten" tag)
    do! closePopoverWithButton page
    do! waitText page "#bindings-panel" "a7"
    let! geometry =
      page.EvaluateAsync<string>(
        """() => {
          const tree = document.querySelector('#live-tree').getBoundingClientRect();
          const rows = [...document.querySelectorAll('#live-tree > .live-binding')];
          const inView = rows.filter(e => { const r = e.getBoundingClientRect(); return r.height > 0 && r.top >= tree.top - 1 && r.bottom <= tree.bottom + 1; }).length;
          const heights = rows.map(e => (e.id || e.className) + '=' + Math.round(e.getBoundingClientRect().height)).join(' ');
          return inView + '|tree=' + Math.round(tree.height) + '|rows=' + rows.length + '|' + heights;
        }""")
    let! shownMarkers = page.EvaluateAsync<int>("() => [...document.querySelectorAll('#live-tree .live-tweak-marker')].filter(e => getComputedStyle(e).display !== 'none').length")
    shownMarkers |> Expect.equal "a REPL-only marker takes no space (the later .live-tweak rule must not win)" 0
    let! flowPins = page.EvaluateAsync<int>("() => [...document.querySelectorAll('#live-tree .live-binding-pin')].filter(e => getComputedStyle(e).position !== 'absolute').length")
    flowPins |> Expect.equal "every row's pin is out of the flow (the aria-label tooltip rule must not win)" 0
    let rowsInView = geometry.Split('|').[0] |> int
    (rowsInView >= 8) |> Expect.isTrue (sprintf "ten collapsed bindings show at least eight rows in the dock, got %d (%s)" rowsInView geometry)
    do! shot page "3-bindings"

    // The view is a setting: a choice saved for this app (its own .SageFs/settings.json) reaches the page that is already open on the
    // next push, and clearing it brings the workflow's recommendation back. The file is restored to what it was.
    let appSettings = System.IO.Path.Combine(Sw.sampleA, ".SageFs", "settings.json")
    let settingsBefore = match System.IO.File.Exists appSettings with | true -> Some(System.IO.File.ReadAllText appSettings) | false -> None
    try
      let paths : SageFs.ConfigPaths =
        { GlobalDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), sprintf "sagefs-viewjourney-%s" runTag)
          Repo = SageFs.RepoRootAt Sw.sampleA }
      let descriptorOf (value: SageFs.AxisValue) = SageFs.ViewSettings.descriptor SageFs.ViewWorkflow.Repl (SageFs.AxisValue.axisOf value)
      let chosen =
        [ SageFs.AxisValue.TypeColumn SageFs.Off
          SageFs.AxisValue.ValueMode SageFs.ValueMode.Full
          SageFs.AxisValue.RowHeight SageFs.RowHeight.Roomy ]
      for value in chosen do
        match SageFs.SettingsCatalog.edit paths SageFs.LRepo (SageFs.AxisValue.token value) (descriptorOf value) with
        | Result.Ok _ -> ()
        | Result.Error e -> Tests.failtestf "saving the view for this app should persist: %A" e
      do! push "view-set"
      do! waitJs page "the open page to take the saved view" BrowserWaits.panelUpdates
            "() => { const t = document.querySelector('#live-tree'); return !!t && t.dataset.values === 'full' && t.dataset.row === 'roomy' && t.querySelectorAll('.live-type').length === 0; }" null
      for value in chosen do
        match SageFs.SettingsCatalog.clear paths SageFs.LRepo (descriptorOf value) with
        | Result.Ok _ -> ()
        | Result.Error e -> Tests.failtestf "resetting the view for this app should work: %A" e
      do! push "view-cleared"
      do! waitJs page "the open page to take the recommended view back" BrowserWaits.panelUpdates
            "() => { const t = document.querySelector('#live-tree'); return !!t && t.dataset.values === 'clipped' && t.dataset.row === 'compact' && t.querySelectorAll('.live-type').length > 0; }" null

      // The same through the panel a person uses. A click saves for this app (the scope's default), and the open page changes with no
      // other push to carry it: the route asks for one.
      let rowIs (value: string) =
        sprintf "() => { const t = document.querySelector('#live-tree'); return !!t && t.dataset.row === '%s'; }" value
      do! waitHidden page "#live-view-panel"
      do! page.Locator("#live-view-btn").ClickAsync()
      do! waitVisible page "#live-view-panel"
      let! compactPressed = page.Locator("#live-view-panel button[aria-pressed=true]:has-text('Compact')").CountAsync()
      compactPressed |> Expect.equal "the recommended row height is the pressed one" 1
      do! page.Locator("#live-view-panel button:has-text('Roomy')").ClickAsync()
      do! waitJs page "the tree to turn roomy from the panel" BrowserWaits.panelUpdates (rowIs "roomy") null
      let! roomyPressed = page.Locator("#live-view-panel button[aria-pressed=true]:has-text('Roomy')").CountAsync()
      roomyPressed |> Expect.equal "the pressed button follows the saved choice" 1
      let! saidWhere = textOf page "#live-view-panel"
      saidWhere |> Expect.stringContains "the panel says where the choice is saved" "saved for this project"
      do! shot page "3-view-panel"
      // A reload keeps it: the file is the memory.
      let! _ = page.GotoAsync dashboard
      do! PlaywrightExpect.waitForSSE BrowserWaits.panelUpdates page
      do! waitVisible page "#bindings-panel"
      do! waitJs page "the choice to survive a reload" BrowserWaits.panelUpdates (rowIs "roomy") null
      // The Type column, off from the panel.
      do! page.Locator("#live-view-btn").ClickAsync()
      do! waitVisible page "#live-view-panel"
      do! page.Locator("#live-view-panel [aria-label='Show type'] button:has-text('Off')").ClickAsync()
      do! waitJs page "the type cells to go" BrowserWaits.panelUpdates "() => document.querySelectorAll('#live-tree .live-type').length === 0" null
      // Reset puts the recommendation back.
      do! page.Locator("#live-view-panel button:has-text('Reset to recommended')").ClickAsync()
      do! waitJs page "the recommended view to return" BrowserWaits.panelUpdates
            "() => { const t = document.querySelector('#live-tree'); return !!t && t.dataset.row === 'compact' && t.querySelectorAll('.live-type').length > 0; }" null
      expectNoErrors errors "using the view panel"
    finally
      match settingsBefore with
      | Some text -> System.IO.File.WriteAllText(appSettings, text)
      | None -> (try System.IO.File.Delete appSettings with _ -> ())

    // The pane folds its own tree away: the tree can be taller than the dock's ceiling, and the
    // drag handle that changes the ceiling is a thin line to find. The fold is the user's, so it
    // survives a push.
    do! page.Locator("#live-pane-collapse").ClickAsync()
    do! waitHidden page "#live-tree"
    do! waitVisible page ".live-pane-bar"
    let! foldState = page.Locator("#live-pane-collapse").GetAttributeAsync("aria-expanded")
    foldState |> Expect.equal "the button says the tree is folded away" "false"
    do! push "folded"
    do! waitHidden page "#live-tree"
    let! stillFolded = page.Locator("#live-pane-collapse").GetAttributeAsync("aria-expanded")
    stillFolded |> Expect.equal "the fold survived the push" "false"
    do! shot page "3b-folded"
    do! page.Locator("#live-pane-collapse").ClickAsync()
    do! waitVisible page "#live-tree"
    do! waitVisible page "#lb_706572736f6e"
    expectNoErrors errors "while folding the tree away and opening it again"

    // A changed value flashes and an unchanged one does not. The page's own observer records which preview cells have a
    // running animation at the moment their text is replaced.
    let! _ =
      page.EvaluateAsync(
        """() => {
          window.__flashes = [];
          new MutationObserver(records => records.forEach(r => {
            const node = r.type === 'characterData' ? r.target.parentElement : r.target;
            const cell = node && node.closest ? node.closest('#live-dock .live-preview') : null;
            if (cell && cell.getAnimations().length > 0) window.__flashes.push(cell.textContent);
          })).observe(document.body, { subtree: true, childList: true, characterData: true });
        }""")
    do! evaluateThroughPopover page (sprintf "counter.Value <- 5\nprintfn \"DOCKPROBE-%s-2\";;" tag) (sprintf "DOCKPROBE-%s-2" tag)
    do! closePopoverWithButton page
    do! waitJs page "the changed value to flash" BrowserWaits.daemonWork "() => window.__flashes.some(t => t.includes('contents = 5') || t.trim() === '= 5')" null
    let! flashes = page.EvaluateAsync<string[]>("() => window.__flashes")
    flashes |> Array.exists (fun t -> t.Contains "Ada") |> Expect.isFalse "a value that did not change did not flash"
    flashes |> Array.exists (fun t -> t.Contains "[1; 2; 3]") |> Expect.isFalse "a list that did not change did not flash"

    // The filter hides what does not match, and it survives the page's next morph.
    do! page.Locator("#live-filter").FillAsync "pers"
    do! waitHidden page "#lb_73636f726573"
    do! push "filtered"
    do! waitVisible page "#lb_706572736f6e"
    do! waitHidden page "#lb_73636f726573"
    do! page.Locator("#live-filter").FillAsync "zzz-nothing"
    do! waitVisible page "#live-nomatch"
    do! page.Locator("#live-filter").FillAsync ""
    do! waitVisible page "#lb_73636f726573"
    // Pinning a binding moves it to the top, and it stays there across a morph.
    let topOf (id: string) = page.EvaluateAsync<float>("id => document.getElementById(id).getBoundingClientRect().top", id)
    do! page.Locator("#lbp_73636f726573").ClickAsync()
    do! waitJs page "the pinned binding to ride to the top" BrowserWaits.panelUpdates
          "() => document.getElementById('lb_73636f726573').getBoundingClientRect().top < document.getElementById('lb_706572736f6e').getBoundingClientRect().top" null
    do! push "bindingpinned"
    let! scoresTop = topOf "lb_73636f726573"
    let! personTop = topOf "lb_706572736f6e"
    (scoresTop < personTop) |> Expect.isTrue "the pinned binding is still above the others after a push"
    let! pinPressed = page.Locator("#lbp_73636f726573").GetAttributeAsync("aria-pressed")
    pinPressed |> Expect.equal "its pin reads as pressed" "true"
    expectNoErrors errors "while evaluating, filtering and pinning"

    // ── 7. the popover's keyboard: key e opens it with the box focused, Escape closes it ───────────────────────────────
    // The View panel opened earlier in the journey is still open, and it sits over the lower part of Output, so the click goes to
    // Output's top-left corner where nothing overlaps (the middle of the panel is under the popover, and Playwright refuses to click
    // through it, as a mouse would not).
    do! page.Locator("#output-panel").ClickAsync(LocatorClickOptions(Position = Position(X = 20.0f, Y = 8.0f)))
    do! page.Keyboard.PressAsync "e"
    do! waitVisible page "#evaluate-section .eval-pop-body"
    do! waitJs page "the box to take focus" BrowserWaits.panelUpdates "() => document.activeElement && document.activeElement.id === 'eval-textarea'" null
    do! page.Keyboard.PressAsync "Escape"
    do! waitHidden page "#evaluate-section .eval-pop-body"
    let! closedAgain = page.EvaluateAsync<bool>("() => !document.querySelector('#evaluate-section').open")
    closedAgain |> Expect.isTrue "Escape closed the popover"

    // ── 6. narrow and wide ─────────────────────────────────────────────────────────────────────────────────────────────
    for (width, height, name) in [ 1920, 1000, "wide"; 1280, 800, "desktop"; 768, 900, "tablet"; 390, 780, "narrow" ] do
      do! page.SetViewportSizeAsync(width, height)
      do! waitVisible page "#live-dock"
      let! problems = layoutProblems page
      problems |> Expect.equal (sprintf "at %dpx wide nothing overflows sideways and the dock stays inside the viewport: %s" width problems) ""
      do! shot page (sprintf "6-%s-%d" name width)
      // Output keeps a few lines of height at every width, whatever the dock holds. On the stacked layout it was the flex child that
      // shrank: with the View panel open (dock cap 54rem) it measured 1px, and 34px in a busy session at 390x780.
      let! outputHeight = page.EvaluateAsync<float>("() => document.querySelector('#output-section').getBoundingClientRect().height")
      (outputHeight >= 120.0) |> Expect.isTrue (sprintf "at %dx%d the Output section keeps at least 120px, got %f" width height outputHeight)
      // With the popover open too: its box stays inside the viewport at this size.
      do! openPopover page
      let! withPopover = layoutProblems page
      withPopover |> Expect.equal (sprintf "at %dpx wide the open popover stays inside the viewport: %s" width withPopover) ""
      if name = "narrow" || name = "wide" then do! shot page (sprintf "6-%s-%d-popover" name width)
      do! closePopoverWithButton page
    do! page.SetViewportSizeAsync(1400, 850)
    expectNoErrors errors "across every width"

    // ── the same facts after a reload: the server's state is what comes back, and nothing breaks ───────────────────────
    let! _ = page.GotoAsync dashboard
    do! PlaywrightExpect.waitForSSE BrowserWaits.panelUpdates page
    do! waitVisible page "#bindings-panel"
    do! waitText page "#bindings-panel" "person"
    let! popoverAfterReload = page.EvaluateAsync<bool>("() => document.querySelector('#evaluate-section').open")
    popoverAfterReload |> Expect.isFalse "the popover remembers nothing between page loads"
    expectNoErrors errors "after a reload"
    try do! ctx.CloseAsync() with _ -> ()
  with ex ->
    failure <- Some ex
  match browser with
  | Some b -> do! Sw.closeBrowserSafely b
  | None -> ()
  playwright |> Option.iter (fun p -> try p.Dispose() with _ -> ())
  Sw.killDaemon daemon
  match failure with
  | Some ex -> return raise ex
  | None -> ()
}

[<Tests>]
let tests =
  testSequenced <|
  testList "Dashboard live bindings dock browser tests" [

    testTask "[Integration] Dashboard dock browser: the live bindings where Evaluate was, and Evaluate as a popover, across the state matrix and every width" {
      do! journey () }
    |> Integration.register (Integration.Dedicated "--integration-browser")
  ]
