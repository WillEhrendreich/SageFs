/// The live-bindings knob in a real browser (Playwright.NET, inside the Expecto suite): a row says where its value lives, a step
/// writes the file through the nudge door, and the file's bytes are the proof. One journey on its OWN daemon (fresh ports, fresh
/// SAGEFS_DATA_DIR, `--no-resume`) with a session on a copy of `fixtures/TweakFixture`, made per run and never edited in place:
///
///   1. each kind of row says its state: in source, formula, made of parts, REPL only
///   2. a step writes exactly that literal's range, a drag counts steps and writes once on release, three quick key presses are
///      ONE write, and the reload the write caused is shown on the row
///   3. undo puts the exact bytes back, and redo puts the write back
///   4. a bool is a toggle, a string and a formula are fields (Enter applies, Escape changes nothing), a record's field is a knob
///   5. a click on a row the file changed under shows both versions and writes nothing; a refusal shows the door's rule
///   6. narrow (390) and wide (1920): nothing overflows sideways and the strips stay inside the dock
///
/// Every step proves zero console errors and zero Datastar PatchElementsNoTargetsFound, and the states save screenshots.
module SageFs.Tests.DashboardLiveBindingsTweakBrowserTests

open System
open System.IO
open System.Text.RegularExpressions
open System.Threading.Tasks
open Expecto
open Expecto.Flip
open Microsoft.Playwright
open SageFs.Tests.DashboardBrowserTests

module Integration = SageFs.Tests.TestInfrastructure.Integration
module Sw = SageFs.Tests.DashboardSessionSwitchOutputBrowserTests

let private fixtureSource = Path.Combine(Sw.repoRoot, "SageFs.Tests", "fixtures", "TweakFixture")

/// A copy of the fixture's sources in a directory of its own under the fixture (so the repo's build settings reach it), built once.
let private makeRunCopy () : Task<string> = task {
  let runDir = Path.Combine(fixtureSource, ".runs", Guid.NewGuid().ToString("N"))
  Directory.CreateDirectory runDir |> ignore
  for file in Directory.GetFiles fixtureSource do
    File.Copy(file, Path.Combine(runDir, Path.GetFileName file))
  let psi = Diagnostics.ProcessStartInfo("dotnet", "build -c Debug -v q -nologo")
  psi.WorkingDirectory <- runDir
  psi.RedirectStandardOutput <- true
  psi.RedirectStandardError <- true
  psi.UseShellExecute <- false
  use proc = Diagnostics.Process.Start psi
  let output = proc.StandardOutput.ReadToEndAsync()
  let errors = proc.StandardError.ReadToEndAsync()
  use cts = new Threading.CancellationTokenSource(SageFs.Timeouts.browserJourneyWarmup)
  do! proc.WaitForExitAsync cts.Token
  let! text = output
  let! err = errors
  if proc.ExitCode <> 0 then Tests.failtestf "the fixture copy did not build: %s %s" text err
  return runDir
}

/// The run's copy goes when the run does; a copy that cannot be removed is left, never an error that hides the journey's own.
let private removeRunCopy (dir: string) : unit =
  match dir with
  | "" -> ()
  | _ ->
    try Directory.Delete(dir, true)
    with _ -> ()

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
  let path = Path.Combine(PlaywrightFixture.screenshotDir, sprintf "tweak-%s.png" name)
  let! _ = page.ScreenshotAsync(PageScreenshotOptions(Path = path, FullPage = false))
  eprintfn "screenshot: %s" path
}

let private waitJs (page: IPage) (what: string) (budgetMs: int) (script: string) (arg: obj) : Task<unit> = task {
  try
    let! _ = page.WaitForFunctionAsync(script, arg, PageWaitForFunctionOptions(Timeout = float32 budgetMs))
    ()
  with ex -> Tests.failtestf "timed out after %dms waiting for %s: %s" budgetMs what ex.Message
}

/// A row's strip, by the row it belongs to.
let private row (name: string) = sprintf "[data-testid=tweak-row][data-row='%s']" name

let private waitState (page: IPage) (name: string) (state: string) =
  waitJs page (sprintf "%s to be %s" name state) BrowserWaits.daemonWork
    "([sel, state]) => { const e = document.querySelector(sel); return !!e && e.getAttribute('data-state') === state; }"
    [| row name; state |]

let private stateOf (page: IPage) (name: string) : Task<string> =
  page.EvaluateAsync<string>("sel => { const e = document.querySelector(sel); return e ? e.getAttribute('data-state') : ''; }", row name)

let private textIn (page: IPage) (selector: string) : Task<string> =
  page.EvaluateAsync<string>("sel => { const e = document.querySelector(sel); return e ? e.innerText : ''; }", selector)

/// The file as it is, waited on: a change that takes a moment to land is a poll on the real bytes, never a sleep.
let private waitForFile (path: string) (what: string) (expected: string) : Task<unit> = task {
  let! landed =
    Sw.waitUntil (TestTimeouts.asMs TestTimeouts.patience) (fun () -> task { return File.ReadAllText path = expected })
  if not landed then Tests.failtestf "the file never became %s. It is:\n%s" what (File.ReadAllText path)
}

/// What sticks out sideways, or out of the viewport, at the width the page is now: the page, the dock, and every strip in it.
let private layoutProblems (page: IPage) : Task<string> =
  page.EvaluateAsync<string>(
    """() => {
      const problems = [];
      const w = window.innerWidth;
      if (document.scrollingElement.scrollWidth > w + 1)
        problems.push('the page scrolls sideways: ' + document.scrollingElement.scrollWidth + ' > ' + w);
      const dock = document.querySelector('#live-dock');
      if (dock) {
        const d = dock.getBoundingClientRect();
        if (d.left < -1 || d.right > w + 1) problems.push('the dock sticks out sideways: ' + Math.round(d.left) + '..' + Math.round(d.right) + ' of ' + w);
        const tree = dock.querySelector('#live-tree');
        const t = tree ? tree.getBoundingClientRect() : d;
        if (tree && tree.scrollWidth > tree.clientWidth + 1) problems.push('the tree scrolls sideways: ' + tree.scrollWidth + ' > ' + tree.clientWidth);
        for (const el of dock.querySelectorAll('.live-tweak, .live-tweak > *, .live-knob > *')) {
          const r = el.getBoundingClientRect();
          if (r.width === 0 && r.height === 0) continue;
          if (r.right > t.right + 1 || r.left < t.left - 1)
            problems.push((el.className || el.tagName) + ' sticks out of the tree: ' + Math.round(r.left) + '..' + Math.round(r.right) + ' vs ' + Math.round(t.left) + '..' + Math.round(t.right));
        }
      }
      return problems.join(' | ');
    }""")

let journey () : Task<unit> = task {
  let runDir = ref ""
  let daemon = Sw.startDaemon ()
  let mutable playwright: IPlaywright option = None
  let mutable browser: IBrowser option = None
  let mutable failure: exn option = None
  try
    let! built = makeRunCopy ()
    runDir.Value <- built
    let tuning = Path.Combine(built, "Tuning.fs")
    let original = File.ReadAllText tuning
    let! healthy = Sw.waitHealthy daemon
    if not healthy then
      Sw.dumpLogs daemon
      Tests.failtestf "the daemon on port %d never became healthy" daemon.McpPort
    do! Sw.createSession daemon (Path.Combine(built, "TweakFixture.fsproj")) built
    let! ready =
      Sw.waitUntil (TestTimeouts.asMs SageFs.Timeouts.browserJourneyWarmup) (fun () -> task {
        try
          let! now = Sw.sessionsNow daemon
          return now.Length = 1 && now |> List.forall (fun (_, status, _) -> status = "Ready")
        with _ -> return false
      })
    if not ready then Sw.dumpLogs daemon
    ready |> Expect.isTrue "the fixture session reached Ready"
    let! sessions = Sw.sessionsNow daemon
    let sessionId = Sw.idFor sessions built
    let dashboardBase = sprintf "http://localhost:%d" daemon.DashboardPort
    // Hot reload watches the file, so a write reaches the running session and the row can show what the reload said about it.
    use http = new Net.Http.HttpClient(Timeout = TestTimeouts.httpRequest)
    let json () = new Net.Http.StringContent("{}", Text.Encoding.UTF8, "application/json")
    let! watched = http.PostAsync(sprintf "%s/api/sessions/%s/hotreload/watch-all" dashboardBase sessionId, json ())
    watched.IsSuccessStatusCode |> Expect.isTrue "hot reload is watching the project's files"

    // The REPL binds the values a person would be tuning, and one that no file holds.
    let! evaled =
      Sw.post daemon TestTimeouts.requestPatience "/exec"
        {| code =
             "let gravity = 9.8;;\nlet maxHealth = 100;;\nlet hardMode = false;;\nlet title = \"Nudge\";;\nlet jump = gravity * 2.0;;\ntype Feel = { JumpVelocity: float; CoyoteTime: float };;\nlet feel = { JumpVelocity = 13.2; CoyoteTime = 0.12 };;\nlet speed = 3;;\nlet scores = [1; 2; 3];;"
           working_directory = built |}
    evaled |> Expect.isOk "the REPL took the bindings"

    let! pw = Playwright.CreateAsync()
    let! chromium = pw.Chromium.LaunchAsync(BrowserTypeLaunchOptions(Headless = true))
    playwright <- Some pw
    browser <- Some chromium
    let! ctx = chromium.NewContextAsync(BrowserNewContextOptions(ViewportSize = ViewportSize(Width = 1400, Height = 1000)))
    let! page = ctx.NewPageAsync()
    let errors = watchErrors page
    let! _ = page.GotoAsync(sprintf "%s/dashboard" dashboardBase)
    do! PlaywrightExpect.waitForSSE BrowserWaits.panelUpdates page

    // ── 1. every kind of row says its state ───────────────────────────────────────────────────────────────────────────
    for name in [ "gravity"; "maxHealth"; "hardMode"; "title" ] do
      do! waitState page name "InSource"
    do! waitState page "jump" "Derived"
    do! waitState page "feel" "Derived"
    do! waitState page "speed" "NotInAFile"
    do! waitState page "scores" "NotInAFile"
    let! speedText = textIn page (row "speed")
    speedText |> Expect.stringContains "a REPL-only value says there is nothing to save it to" "nothing to save it to"
    let! jumpControl = page.Locator(sprintf "%s [data-testid=tweak-expression]" (row "jump")).CountAsync()
    jumpControl |> Expect.equal "a formula is edited as an expression" 1
    let! speedControls = page.Locator(sprintf "%s input, %s button" (row "speed") (row "speed")).CountAsync()
    speedControls |> Expect.equal "a REPL-only row offers no control at all, only its reason" 0
    let! problems1 = layoutProblems page
    problems1 |> Expect.equal (sprintf "nothing overflows at the start: %s" problems1) ""
    do! shot page "1-states"
    expectNoErrors errors "with every state on screen"

    // ── 2. a step, a drag, three quick key presses ────────────────────────────────────────────────────────────────────
    let step = page.Locator(sprintf "%s [data-testid=tweak-step-up]" (row "gravity"))
    do! step.ClickAsync()
    let afterStep = original.Replace("let gravity = 9.8", "let gravity = 9.9")
    do! waitForFile tuning "9.9 with only that literal changed" afterStep
    // The row follows the file. Until the REPL's binding is re-evaluated (or hot reload re-evaluates the file into the session)
    // the REPL holds 9.8 and the page says so instead of claiming they agree: either answer is honest, a stale "in source" is not.
    do! waitJs page "the row to say the REPL and the file differ, or that hot reload brought them back in step" BrowserWaits.daemonWork
          "sel => { const e = document.querySelector(sel); return !!e && ['DiffersFromFile', 'InSource'].includes(e.getAttribute('data-state')) && /9\\.9/.test(e.innerText); }"
          (box (row "gravity"))
    // The reload the write caused is on the row.
    do! waitJs page "the reload verdict on the gravity row" BrowserWaits.daemonWork
          "sel => { const e = document.querySelector(sel + ' [data-testid=tweak-reload]'); return !!e && e.innerText.trim().length > 0; }" (box (row "gravity"))
    let! reloadText = textIn page (sprintf "%s [data-testid=tweak-reload]" (row "gravity"))
    do! shot page "2-reload-on-the-row"
    eprintfn "reload line: %s" reloadText
    // The REPL re-evaluates it, and the row is back in step.
    let! _ = Sw.post daemon TestTimeouts.requestPatience "/exec" {| code = "let gravity = 9.9;;"; working_directory = built |}
    do! waitState page "gravity" "InSource"
    expectNoErrors errors "after a step"

    // A drag counts steps while it moves, and writes once, on release.
    let slider = page.Locator(sprintf "%s [data-testid=tweak-slider]" (row "gravity"))
    let! _ = slider.EvaluateAsync("el => { el.value = '5'; el.dispatchEvent(new Event('input', { bubbles: true })); }")
    do! waitJs page "the readout to follow the drag" BrowserWaits.panelUpdates
          "sel => document.querySelector(sel + ' [data-testid=tweak-readout]').innerText.trim() === '10.4'" (box (row "gravity"))
    File.ReadAllText tuning |> Expect.equal "nothing is written while the handle is moving" afterStep
    do! shot page "3-drag-in-progress"
    let! _ = slider.EvaluateAsync("el => el.dispatchEvent(new Event('change', { bubbles: true }))")
    let afterDrag = original.Replace("let gravity = 9.8", "let gravity = 10.4")
    do! waitForFile tuning "10.4 after the drag was released" afterDrag
    let! _ = Sw.post daemon TestTimeouts.requestPatience "/exec" {| code = "let gravity = 10.4;;"; working_directory = built |}
    do! waitState page "gravity" "InSource"

    // Three presses of the up arrow in a row are ONE write of +0.3: if each were its own, the second would carry a hash the
    // first had already made stale, and the row would say so.
    do! slider.FocusAsync()
    for _ in 1..3 do
      do! page.Keyboard.PressAsync "ArrowUp"
    let afterKeys = original.Replace("let gravity = 9.8", "let gravity = 10.7")
    do! waitForFile tuning "10.7 after three key presses" afterKeys
    let! _ = Sw.post daemon TestTimeouts.requestPatience "/exec" {| code = "let gravity = 10.7;;"; working_directory = built |}
    do! waitState page "gravity" "InSource"
    expectNoErrors errors "after a drag and three key presses"

    // ── 3. undo puts the exact bytes back, redo the write ─────────────────────────────────────────────────────────────
    let undo = page.Locator(sprintf "%s [data-testid=tweak-undo]" (row "gravity"))
    do! undo.ClickAsync()
    do! waitForFile tuning "the file before the last write" afterDrag
    let redo = page.Locator(sprintf "%s [data-testid=tweak-redo]" (row "gravity"))
    do! waitJs page "the redo button to be enabled" BrowserWaits.panelUpdates
          "sel => { const b = document.querySelector(sel + ' [data-testid=tweak-redo]'); return !!b && !b.disabled; }" (box (row "gravity"))
    do! shot page "4-undone-and-redo-offered"
    do! redo.ClickAsync()
    do! waitForFile tuning "the write put back" afterKeys
    expectNoErrors errors "after undo and redo"

    // ── 4. a bool, a string, a formula, a record's field ──────────────────────────────────────────────────────────────
    let mutable expected = afterKeys
    do! page.Locator(sprintf "%s [data-testid=tweak-toggle]" (row "hardMode")).ClickAsync()
    expected <- expected.Replace("let hardMode = false", "let hardMode = true")
    do! waitForFile tuning "the switch turned on" expected

    let title = page.Locator(sprintf "%s [data-testid=tweak-field]" (row "title"))
    do! title.FillAsync "Nudge2"
    do! title.PressAsync "Enter"
    expected <- expected.Replace("let title = \"Nudge\"", "let title = \"Nudge2\"")
    do! waitForFile tuning "the string replaced, quotes kept" expected
    // Escape changes nothing and puts back what the file holds.
    do! title.FillAsync "never written"
    do! title.PressAsync "Escape"
    do! waitJs page "the field to show what the file holds again" BrowserWaits.panelUpdates
          "sel => document.querySelector(sel + ' [data-testid=tweak-field]').value === 'Nudge2'" (box (row "title"))
    File.ReadAllText tuning |> Expect.equal "Escape wrote nothing" expected

    let jump = page.Locator(sprintf "%s [data-testid=tweak-expression]" (row "jump"))
    do! jump.FillAsync "gravity * 3.0"
    do! jump.PressAsync "Enter"
    expected <- expected.Replace("let jump = gravity * 2.0", "let jump = gravity * 3.0")
    do! waitForFile tuning "the formula replaced as an expression" expected

    // A record's field is a knob of its own, under the record's details.
    do! page.Locator("#lb_6665656c summary").First.ClickAsync()
    let field = "feel/JumpVelocity"
    do! waitJs page "the record's field row" BrowserWaits.panelUpdates
          "sel => !!document.querySelector(sel)" (box (row field))
    do! page.Locator(sprintf "%s [data-testid=tweak-step-up]" (row field)).ClickAsync()
    expected <- expected.Replace("JumpVelocity = 13.2", "JumpVelocity = 13.3")
    do! waitForFile tuning "the record field stepped, in its own range" expected
    do! shot page "5-record-field-and-bool"
    expectNoErrors errors "after a bool, a string, a formula and a record field"

    // ── 5. a click on a row the file changed under, and a refusal ─────────────────────────────────────────────────────
    let! unwatched = http.PostAsync(sprintf "%s/api/sessions/%s/hotreload/unwatch-all" dashboardBase sessionId, json ())
    unwatched.IsSuccessStatusCode |> Expect.isTrue "hot reload stopped watching, so nothing repaints the page behind the test's back"
    let handler = "data-on:change__debounce.300ms"
    let! shown = slider.EvaluateAsync<string>("(el, name) => el.getAttribute(name)", handler)
    let edited = expected.Replace("let gravity = 10.7", "let gravity = 7.7")
    File.WriteAllText(tuning, edited)
    // The page is showing the file as it was, which is exactly what a stale row is: put back the hash it showed, whatever has
    // been drawn since, and click.
    let! _ = slider.EvaluateAsync("(el, [name, value]) => el.setAttribute(name, value)", [| box handler; box shown |])
    do! page.Locator(sprintf "%s [data-testid=tweak-step-up]" (row "gravity")).ClickAsync()
    do! waitState page "gravity" "StaleAddress"
    File.ReadAllText tuning |> Expect.equal "a stale click wrote nothing: the file is what the person left" edited
    let! staleText = textIn page (row "gravity")
    staleText |> Expect.stringContains "what the row showed" "10.7"
    staleText |> Expect.stringContains "what the file holds now" "7.7"
    staleText |> Expect.stringContains "that nothing was written" "Nothing was written"
    do! shot page "6-stale-address"

    let formula = page.Locator(sprintf "%s [data-testid=tweak-expression]" (row "jump"))
    do! formula.FillAsync "gravity *"
    do! formula.PressAsync "Enter"
    do! waitState page "jump" "Refused"
    let! refusedText = textIn page (row "jump")
    refusedText |> Expect.stringContains "the door's rule is shown" "not an F# expression"
    File.ReadAllText tuning |> Expect.equal "a refused expression wrote nothing" edited
    do! shot page "7-refused"
    expectNoErrors errors "after a stale click and a refusal"

    // ── 6. narrow and wide ────────────────────────────────────────────────────────────────────────────────────────────
    for (width, height, name) in [ 1920, 1000, "wide"; 390, 780, "narrow" ] do
      do! page.SetViewportSizeAsync(width, height)
      do! waitJs page "the dock" BrowserWaits.panelUpdates "() => !!document.querySelector('#live-dock')" null
      let! problems = layoutProblems page
      problems |> Expect.equal (sprintf "at %dpx wide nothing overflows sideways and every strip stays inside the tree: %s" width problems) ""
      do! shot page (sprintf "8-%s-%d" name width)
    do! page.SetViewportSizeAsync(1400, 1000)
    expectNoErrors errors "across both widths"
    try do! ctx.CloseAsync() with _ -> ()
  with ex ->
    failure <- Some ex
  match browser with
  | Some b -> do! Sw.closeBrowserSafely b
  | None -> ()
  playwright |> Option.iter (fun p -> try p.Dispose() with _ -> ())
  Sw.killDaemon daemon
  removeRunCopy runDir.Value
  match failure with
  | Some ex -> return raise ex
  | None -> ()
}

[<Tests>]
let tests =
  testSequenced <|
  testList "Dashboard live bindings knob browser tests" [

    testTask "[Integration] Dashboard knob browser: a row says where its value lives, a step writes exactly that range, undo restores the bytes, a stale click and a refusal say why" {
      do! journey () }
    |> Integration.register (Integration.Dedicated "--integration-browser")
  ]
