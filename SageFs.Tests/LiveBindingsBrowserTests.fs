/// The live-bindings pane in a real browser (Playwright.NET, inside the Expecto suite), across its state matrix: a class
/// binding whose getters are harmless, run code and loop; a click on each; the mode switched to Everything, Off and back to
/// Safe. Every journey also proves zero console errors and zero Datastar PatchElementsNoTargetsFound, and saves screenshots.
///
/// They share the browser tier's one daemon and session (see DashboardBrowserRunner), so they run in order, and the one
/// that leaves a getter spinning in the host (the loop) runs last.
module SageFs.Tests.LiveBindingsBrowserTests

open System
open System.IO
open System.Threading.Tasks
open Expecto
open Microsoft.Playwright
open SageFs.Tests.DashboardBrowserTests

/// A class with one getter of each kind the pane tells apart. Harmless is a constant, so Safe mode runs it. RunsCode calls other
/// code, so Safe mode lists it with a click.
let private probeCode =
  "type Probe() =\n  member _.Harmless = 7\n  member _.RunsCode = string (List.length [ 1; 2; 3 ])\nlet probe = Probe()\nlet probeDefined = \"probe-defined\";;"

/// A getter that calls itself: F# compiles that to a loop, so a click can only be stopped by the deadline.
let private spinnerCode =
  "type Spinner() =\n  member this.Self : int = this.Self\nlet spinner = Spinner()\nlet spinnerDefined = \"spinner-defined\";;"

let private pane = "#bindings-panel"

/// Console errors, page errors and Datastar's own complaint when a morph has no target, all of which fail a journey.
let private watchErrors (page: IPage) : Collections.Generic.List<string> =
  let errors = Collections.Generic.List<string>()
  page.Console.Add(fun message ->
    if message.Type = "error" || message.Text.Contains "PatchElementsNoTargetsFound" then
      errors.Add(sprintf "[console %s] %s" message.Type message.Text))
  page.PageError.Add(fun error -> errors.Add(sprintf "[pageerror] %s" error))
  errors

let private expectNoErrors (errors: Collections.Generic.List<string>) (what: string) =
  Expect.isEmpty (List.ofSeq errors) (sprintf "zero console and Datastar errors %s, got: %s" what (String.concat " | " errors))

let private shot (page: IPage) (name: string) = task {
  let path = Path.Combine(PlaywrightFixture.screenshotDir, sprintf "live-bindings-%s.png" name)
  let! _ = page.ScreenshotAsync(PageScreenshotOptions(Path = path, FullPage = true))
  eprintfn "screenshot: %s" path
}

/// Evaluate code the way a user does, through the Evaluate area, and wait until the output panel shows `marker`.
let private evalCode (page: IPage) (code: string) (marker: string) = task {
  let textarea = DashboardDom.textarea page
  do! DashboardDom.throughPanelReset (fun () -> DashboardDom.openEvalArea page) 5 (fun () -> task {
    do! textarea.FillAsync code
    do! (DashboardDom.evalButton page).ClickAsync()
  })
  do! PlaywrightExpect.waitForSelectorText BrowserWaits.daemonWork page "#output-panel" marker
}

/// The page, once the stream is live and the extra panels are showing.
let private ready (page: IPage) = task {
  do! PlaywrightExpect.waitForSSE BrowserWaits.panelUpdates page
  do! DashboardDom.ensureExpanded page
}

let private paneText (page: IPage) = page.Locator(pane).InnerTextAsync()

let private waitForPaneText (page: IPage) (text: string) =
  PlaywrightExpect.waitForSelectorText BrowserWaits.daemonWork page pane text

/// A class binding is a <details> the user opens, like any expandable value in the pane. Opens it and waits until it is open.
let private openBinding (page: IPage) (binding: string) = task {
  let selector = sprintf "%s details[id^='open_%s_']" pane binding
  let details = page.Locator(selector).First
  do! details.WaitForAsync(LocatorWaitForOptions(State = WaitForSelectorState.Attached, Timeout = float32 BrowserWaits.daemonWork))
  let! isOpen = details.EvaluateAsync<bool>("el => el.open")
  if not isOpen then do! details.Locator("> summary").First.ClickAsync()
  let! _ = page.WaitForFunctionAsync("sel => { const d = document.querySelector(sel); return !!d && d.open; }", selector)
  ()
}

let private modeButton (page: IPage) (name: string) =
  page.Locator(sprintf "%s .live-mode button" pane).Filter(LocatorFilterOptions(HasTextString = name))

/// Wait until the pressed mode button is `name`, and it is the only one pressed.
let private waitForMode (page: IPage) (name: string) = task {
  do! PlaywrightExpect.waitForCount BrowserWaits.daemonWork (page.Locator(sprintf "%s .live-mode button[aria-pressed='true']" pane)) 1
  do! PlaywrightExpect.waitForCount BrowserWaits.daemonWork (page.Locator(sprintf "%s .live-mode button[aria-pressed='true']" pane).Filter(LocatorFilterOptions(HasTextString = name))) 1
}

/// What sticks out of the pane or sits on top of a neighbour, at the width the page is now.
let private layoutProblems (page: IPage) =
  page.EvaluateAsync<string>(
    """() => {
      const problems = [];
      const pane = document.querySelector('#bindings-panel');
      if (!pane) return 'no bindings panel';
      const pr = pane.getBoundingClientRect();
      if (document.scrollingElement.scrollWidth > window.innerWidth + 1)
        problems.push('the page scrolls sideways: ' + document.scrollingElement.scrollWidth + ' > ' + window.innerWidth);
      for (const el of pane.querySelectorAll('.live-pane-head, .live-held-row, .live-mode, .live-held-btn')) {
        const r = el.getBoundingClientRect();
        if (r.right > pr.right + 1 || r.left < pr.left - 1)
          problems.push(el.className + ' sticks out of the pane: ' + Math.round(r.left) + '..' + Math.round(r.right) + ' vs ' + Math.round(pr.left) + '..' + Math.round(pr.right));
      }
      for (const row of pane.querySelectorAll('.live-held-row, .live-pane-head')) {
        const kids = Array.from(row.children).filter(k => k.getBoundingClientRect().width > 0 && getComputedStyle(k).display !== 'none');
        for (let i = 0; i < kids.length; i++) for (let j = i + 1; j < kids.length; j++) {
          const a = kids[i].getBoundingClientRect(), b = kids[j].getBoundingClientRect();
          const overlapX = Math.min(a.right, b.right) - Math.max(a.left, b.left);
          const overlapY = Math.min(a.bottom, b.bottom) - Math.max(a.top, b.top);
          if (overlapX > 1 && overlapY > 1) problems.push(row.className + ': ' + kids[i].className + ' overlaps ' + kids[j].className);
        }
      }
      return problems.join(' | ');
    }""")

[<Tests>]
let tests =
  testSequenced <|
  testList "Live bindings pane browser tests" [

  playwrightTest "live bindings: Safe mode shows a harmless getter's value and lists the one that runs code, with a click, at every width" (fun page -> task {
    let errors = watchErrors page
    do! ready page
    do! evalCode page probeCode "probe-defined"
    do! waitForPaneText page "probe"
    do! waitForMode page "Safe"
    do! openBinding page "probe"
    let! text = paneText page
    Expect.stringContains text "Harmless" "the harmless getter is shown"
    Expect.stringContains text "7" "with its value, because Safe mode ran it"
    Expect.stringContains text "the getter calls other code" "the getter that runs code says why it was not read"
    Expect.stringContains text "1 not evaluated" "the header counts the rows listed and not read"
    let buttons = page.Locator(sprintf "%s .live-held-row .live-held-btn" pane)
    do! PlaywrightExpect.waitForCount BrowserWaits.pageRenders buttons 1
    let! box = buttons.First.BoundingBoxAsync()
    Expect.isTrue (abs (box.Width - 28.0f) < 1.0f && abs (box.Height - 28.0f) < 1.0f) (sprintf "the click button is the uniform 28x28 square, got %fx%f" box.Width box.Height)
    for width in [ 1280; 600; 360 ] do
      do! page.SetViewportSizeAsync(width, 900)
      let! problems = layoutProblems page
      Expect.equal problems "" (sprintf "at %dpx wide nothing overflows or overlaps: %s" width problems)
      do! shot page (sprintf "1-safe-%d" width)
    do! page.SetViewportSizeAsync(1280, 900)
    expectNoErrors errors "while Safe mode is shown"
  })

  playwrightTest "live bindings: a click shows evaluating in the row at once, then the value, the containment line and a lower count" (fun page -> task {
    let errors = watchErrors page
    do! ready page
    do! waitForPaneText page "probe"
    do! openBinding page "probe"
    // Hold the click's request until the journey has looked at the row, so "evaluating" is observed, not raced.
    let release = TaskCompletionSource()
    do! page.RouteAsync("**/live-values/evaluate", Func<IRoute, Task>(fun route ->
      task {
        let! _ = release.Task
        do! route.ContinueAsync()
      } :> Task))
    let button = page.Locator(sprintf "%s .live-held-row" pane).Filter(LocatorFilterOptions(HasTextString = "RunsCode")).Locator(".live-held-btn")
    do! button.ClickAsync()
    let evaluating = page.Locator(sprintf "%s .live-held-evaluating" pane)
    do! PlaywrightExpect.waitForCount BrowserWaits.pageRenders evaluating 1
    let! visible = evaluating.First.IsVisibleAsync()
    Expect.isTrue visible "the row says it is evaluating while the request is out"
    let! disabled = button.IsDisabledAsync()
    Expect.isTrue disabled "and its button is disabled until the answer arrives"
    do! shot page "2-evaluating"
    release.SetResult()
    do! waitForPaneText page "last click:"
    let! text = paneText page
    Expect.isFalse (text.Contains "1 not evaluated") "the clicked row is no longer counted as not evaluated"
    Expect.isTrue
      (text.Contains "ran under a syscall filter" || text.Contains "no I/O containment here:")
      "the containment line says how the getter was kept from doing harm, or that nothing could be"
    Expect.stringContains text "\"3\"" "the clicked getter's value is in the row"
    do! shot page "3-clicked"
    do! page.UnrouteAsync("**/live-values/evaluate")
    expectNoErrors errors "across a click"
  })

  playwrightTest "live bindings: Everything runs the getter and offers no click, Off collapses the class, and Safe lists it again" (fun page -> task {
    let errors = watchErrors page
    do! ready page
    do! waitForPaneText page "probe"
    do! openBinding page "probe"
    let everything = modeButton page "Everything"
    let! tooltip = everything.GetAttributeAsync("title")
    Expect.stringContains tooltip "getters" "the tooltip tells the user that Everything runs their getters"
    do! everything.ClickAsync()
    do! waitForMode page "Everything"
    do! openBinding page "probe"
    do! PlaywrightExpect.waitForCount BrowserWaits.daemonWork (page.Locator(sprintf "%s .live-held-btn" pane)) 0
    let! everythingText = paneText page
    Expect.stringContains everythingText "RunsCode" "the getter is still listed"
    Expect.stringContains everythingText "\"3\"" "and it ran, because Everything runs every getter"
    Expect.isFalse (everythingText.Contains "not evaluated") "nothing is left listed and not read"
    do! shot page "4-everything"

    do! (modeButton page "Off").ClickAsync()
    do! waitForMode page "Off"
    do! waitForPaneText page "does not open class instances"
    do! PlaywrightExpect.waitForCount BrowserWaits.daemonWork (page.Locator(sprintf "%s .live-held-btn" pane)) 0
    do! shot page "5-off"

    do! (modeButton page "Safe").ClickAsync()
    do! waitForMode page "Safe"
    do! openBinding page "probe"
    do! waitForPaneText page "the getter calls other code"
    do! PlaywrightExpect.waitForCount BrowserWaits.daemonWork (page.Locator(sprintf "%s .live-held-btn" pane)) 1
    let! problems = layoutProblems page
    Expect.equal problems "" (sprintf "back in Safe mode nothing overflows or overlaps: %s" problems)
    expectNoErrors errors "across the mode switches"
  })

  playwrightTest "live bindings: a getter that never returns shows as unknown with its reason, never as empty, and the page stays live" (fun page -> task {
    let errors = watchErrors page
    do! ready page
    do! evalCode page spinnerCode "spinner-defined"
    do! waitForPaneText page "spinner"
    do! openBinding page "spinner"
    do! waitForPaneText page "the getter loops or calls itself"
    let row = page.Locator(sprintf "%s .live-held-row" pane).Filter(LocatorFilterOptions(HasTextString = "Self"))
    do! row.Locator(".live-held-btn").ClickAsync()
    // Every clickable row carries its own evaluating mark, hidden until its own click, and the pane still lists the earlier
    // journeys' `probe` getter. So the question is which marks are SHOWN: exactly one, and it is the clicked row's. Counting
    // the marks in the page would count the probe row's hidden one too, and would only come out as 1 after the getter's
    // deadline, when the clicked row stops offering a click.
    do! PlaywrightExpect.waitForCount BrowserWaits.pageRenders (page.Locator(sprintf "%s .live-held-evaluating:visible" pane)) 1
    do! PlaywrightExpect.waitForCount BrowserWaits.pageRenders (row.Locator(".live-held-evaluating:visible")) 1
    do! shot page "6-loop-evaluating"
    // The host gives the getter its deadline, then gives up on the thread; the answer is a reason, not a blank.
    do! waitForPaneText page "unknown"
    let! text = paneText page
    Expect.stringContains text "did not return in time" "the row says why the value is unknown"
    Expect.isTrue
      (text.Contains "ran under a syscall filter" || text.Contains "no I/O containment here:")
      "the containment line is shown for the click that timed out"
    let! problems = layoutProblems page
    Expect.equal problems "" (sprintf "nothing overflows or overlaps: %s" problems)
    do! shot page "7-loop-unknown"
    expectNoErrors errors "across a click that timed out"
  })
  ]
