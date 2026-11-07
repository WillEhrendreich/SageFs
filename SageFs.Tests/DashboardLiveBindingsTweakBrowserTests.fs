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
  SageFs.Tests.TestInfrastructure.SpawnEnvironment.apply psi
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
  do! PlaywrightFixture.takeScreenshot page false path
}

let private waitJs (page: IPage) (what: string) (budgetMs: int) (script: string) (arg: obj) : Task<unit> = task {
  try
    let! _ = page.WaitForFunctionAsync(script, arg, PageWaitForFunctionOptions(Timeout = float32 budgetMs))
    ()
  with ex -> Tests.failtestf "timed out after %dms waiting for %s: %s" budgetMs what ex.Message
}

/// A row's strip, by the row it belongs to.
let private row (name: string) = sprintf "[data-testid=tweak-row][data-row='%s']" name

let private stateOf (page: IPage) (name: string) : Task<string> =
  page.EvaluateAsync<string>("sel => { const e = document.querySelector(sel); return e ? e.getAttribute('data-state') : ''; }", row name)

/// Wait for a row's `data-state`. `stateOf` is defined above it because a let-bound function is not
/// in scope for code textually above it, and this function reads it on the failure path.
let private waitState (page: IPage) (name: string) (state: string) : Task<unit> = task {
  try
    do!
      waitJs page (sprintf "%s to be %s" name state) BrowserWaits.daemonWork
        "([sel, state]) => { const e = document.querySelector(sel); return !!e && e.getAttribute('data-state') === state; }"
        [| row name; state |]
  with ex ->
    // The generic timeout says what was waited for and nothing about what was there instead, and
    // that gap is the whole difference between "the click never landed", "it landed and the daemon
    // answered something else", and "the row re-rendered under me". Read the row once and say what
    // it actually showed: something the next reader can act on, not another opaque timeout.
    let! observed =
      task {
        try
          return! stateOf page name
        with _ -> return "(the row could not be read)"
      }
    // And what the row said in words, with the line around it (the binding's own value sits beside the strip, not inside it), because
    // "DiffersFromFile" alone does not say which of the two values was not what the test expected.
    let! said =
      task {
        try
          return!
            page.EvaluateAsync<string>(
              "sel => { const e = document.querySelector(sel); if (!e) return ''; const line = e.closest('.live-row, .live-node-summary, details') || e; return line.innerText; }",
              row name)
        with _ -> return "(the row's words could not be read)"
      }
    // And the last of what the session printed, because a row that did not change after an eval is explained there (an error, or an
    // eval that never ran) more often than in the row.
    let! outputTail =
      task {
        try
          return!
            page.EvaluateAsync<string>("() => { const e = document.getElementById('output-section'); return e ? e.innerText.slice(-700) : '(no output pane)'; }")
        with _ -> return "(the output could not be read)"
      }
    return Tests.failtestf "%s — the row showed '%s' instead, saying: %s. The output ended: %s" ex.Message observed (said.Replace("\n", " | ")) (outputTail.Replace("\n", " | "))
}

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
             "let gravity = 9.8;;\nlet maxHealth = 100;;\nlet hardMode = false;;\nlet title = \"Nudge\";;\nlet jump = gravity * 2.0;;\ntype Feel = { JumpVelocity: float; CoyoteTime: float };;\nlet feel = { JumpVelocity = 13.2; CoyoteTime = 0.12 };;\ntype Rig = { Mass: float; Drag: float };;\nlet makeRig () = { Mass = 80.0; Drag = 0.25 };;\nlet rig = makeRig ();;\nlet speed = 3;;\nlet scores = [1; 2; 3];;"
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
    // A REPL-only value has no sentence on its row. Its Source cell says "n/a" so the column has an answer in every row; the title bar
    // says the rest once, with a count, and its reason is on hover.
    let! speedText = textIn page (row "speed")
    speedText.Trim() |> Expect.equal "a REPL-only row says n/a in its Source cell and nothing else" "n/a"
    let! chipText = textIn page "[data-testid=live-repl-only]"
    // The chip says what it counts in the pane's own words: the sentence for a count of 0, without the number in front.
    chipText |> Expect.stringContains "the title bar counts the REPL-only bindings" ((SageFs.Features.LiveBindingsPane.PaneNote.text (SageFs.Features.LiveBindingsPane.PaneNote.NotInAnyFile 0)).Substring 1)
    let! chipReason = page.Locator("[data-testid=live-repl-only]").GetAttributeAsync("title")
    chipReason |> Expect.stringContains "and its reason is on hover" "nothing to save it to"
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
          "sel => { const e = document.querySelector(sel); return !!e && ['DiffersFromFile', 'InSource'].includes(e.getAttribute('data-state')) && /9\\.9/.test(e.innerText + ' ' + Array.from(e.querySelectorAll('input')).map(i => i.value).join(' ')); }"
          (box (row "gravity"))
    // What hot reload did with the write is on the row: its verdict, or, when it is not watching the file, the one sentence that says so.
    do! waitJs page "the reload verdict, or the not-watching sentence, on the gravity row" BrowserWaits.daemonWork
          "sel => { const e = document.querySelector(sel + ' [data-testid=tweak-reload], ' + sel + ' [data-testid=tweak-watching]'); return !!e && e.innerText.trim().length > 0; }" (box (row "gravity"))
    let! reloadText = textIn page (sprintf "%s [data-testid=tweak-reload], %s [data-testid=tweak-watching]" (row "gravity") (row "gravity"))
    do! shot page "2-reload-on-the-row"
    eprintfn "reload line: %s" reloadText
    // The session takes the file's value. When nothing watches the file the row offers the button for it (a REPL session always does),
    // and pressing it is the answer; when hot reload brought the row back in step already, the binding is evaluated again by hand.
    let apply = page.Locator(sprintf "%s [data-testid=tweak-apply]" (row "gravity"))
    let! offered = apply.CountAsync()
    match offered with
    | 0 ->
      let! _ = Sw.post daemon TestTimeouts.requestPatience "/exec" {| code = "let gravity = 9.9;;"; working_directory = built |}
      ()
    | _ -> do! apply.ClickAsync()
    do! waitState page "gravity" "InSource"
    // The session holds the file's value now, so no sentence on the row says it still has the old one, and the button is gone.
    let! staleSentences = page.Locator(sprintf "%s [data-testid=tweak-watching]" (row "gravity")).CountAsync()
    staleSentences |> Expect.equal "once the session holds the file's value, no sentence says it does not" 0
    let! leftoverButtons = page.Locator(sprintf "%s [data-testid=tweak-apply]" (row "gravity")).CountAsync()
    leftoverButtons |> Expect.equal "and the Apply button has nothing left to apply" 0
    expectNoErrors errors "after a step"

    // A drag counts steps while it moves, and writes once, on release.
    let slider = page.Locator(sprintf "%s [data-testid=tweak-slider]" (row "gravity"))
    let! _ = slider.EvaluateAsync("el => { el.value = '5'; el.dispatchEvent(new Event('input', { bubbles: true })); }")
    do! waitJs page "the readout to follow the drag" BrowserWaits.panelUpdates
          "sel => document.querySelector(sel + ' [data-testid=tweak-readout]').value.trim() === '10.4'" (box (row "gravity"))
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

    // The number is a box: a typed number and Enter write it through the door the step buttons use, and text that is not a number
    // changes nothing and says why on the row.
    let health = page.Locator(sprintf "%s [data-testid=tweak-readout]" (row "maxHealth"))
    do! health.FillAsync "250"
    do! health.PressAsync "Enter"
    expected <- expected.Replace("let maxHealth = 100", "let maxHealth = 250")
    do! waitForFile tuning "a typed number written, and only that literal changed" expected
    do! health.FillAsync "lots"
    do! health.PressAsync "Enter"
    do! waitState page "maxHealth" "Refused"
    File.ReadAllText tuning |> Expect.equal "text that is not a number wrote nothing" expected
    expectNoErrors errors "after typing a number and typing words into one"

    // A field of a value the file builds with a call is the callee's literal: the group says once, above its fields, which callee builds
    // it and how many calls a write reaches (it is not repeated under every field), and the step changes the one literal in `makeRig`,
    // never the two calls.
    do! page.Locator("#lb_726967 summary").First.ClickAsync()
    let built = "rig/Mass"
    do! waitState page built "InSource"
    do! waitJs page "the group to say which callee builds it and how many calls a write reaches" BrowserWaits.panelUpdates
          "sel => { const e = document.querySelector(sel + ' [data-testid=live-shared-note]'); return !!e && /makeRig builds this/.test(e.innerText) && /2 places call makeRig/.test(e.innerText); }"
          "#lb_726967"
    do! page.Locator(sprintf "%s [data-testid=tweak-step-up]" (row built)).ClickAsync()
    expected <- expected.Replace("{ Mass = 80.0; Drag = 0.25 }", "{ Mass = 80.1; Drag = 0.25 }")
    do! waitForFile tuning "the one literal in the callee stepped, and both calls read as before" expected
    do! shot page "5b-field-of-a-call"
    expectNoErrors errors "after stepping a field built by a call"

    // ── 5. a click on a row the file changed under, and a refusal ─────────────────────────────────────────────────────
    let! unwatched = http.PostAsync(sprintf "%s/api/sessions/%s/hotreload/unwatch-all" dashboardBase sessionId, json ())
    unwatched.IsSuccessStatusCode |> Expect.isTrue "hot reload stopped watching, so nothing repaints the page behind the test's back"
    let handler = "data-on:change__debounce.300ms"
    let! shown = slider.EvaluateAsync<string>("(el, name) => el.getAttribute(name)", handler)
    let edited = expected.Replace("let gravity = 10.7", "let gravity = 7.7")
    File.WriteAllText(tuning, edited)
    let waitForTheEditsRepaint () =
      waitJs page "the row repainted for the edit" BrowserWaits.daemonWork
        "sel => { const e = document.querySelector(sel); return !!e && e.getAttribute('data-state') === 'DiffersFromFile' && /7\\.7/.test(e.innerText + ' ' + Array.from(e.querySelectorAll('input')).map(i => i.value).join(' ')); }"
        (box (row "gravity"))
    let forgeTheOldHash () =
      slider.EvaluateAsync("(el, [name, value]) => el.setAttribute(name, value)", [| box handler; box shown |])
    // A stale click is the row's handler carrying the hash from before the file changed. The handler is an attribute in the
    // page, and every push of the shell rewrites it with the file's current hash. The dashboard no longer pushes on a
    // clock (an idle shell is the same bytes, so nothing is sent), but it still pushes whenever something real changes,
    // and any such push rewrites the forged hash. Whether a real change lands inside this window has not been measured,
    // which is why the repeat below stays. Forge the old hash and click before a push lands and the click is stale. Lose the race and the forged hash is gone: the click is a legitimate write and
    // the row reads DiffersFromFile. Load decides who wins, and it failed the release gate, so the attempt is repeated, the
    // way `throughPanelReset` repeats a step against the same push. Each attempt starts from the same place (the file holds
    // the edit, and the row has repainted to say so), so a lost one leaves nothing behind for the next.
    // What the page sends is recorded so that a failure says what happened: the forged hash was gone by the click, the click
    // sent nothing, or it sent the forged hash and the daemon answered something else.
    let posts = Collections.Generic.List<string>()
    page.Request.Add(fun request ->
      if request.Method = "POST" then
        let body = match request.PostData with | null -> "" | text -> text
        posts.Add(sprintf "%s %s" request.Url (body.Substring(0, min 700 body.Length))))
    let staleClickAttempts = 5
    let rowIsStale () =
      task {
        try
          let! _ =
            page.WaitForFunctionAsync(
              "([sel, state]) => { const e = document.querySelector(sel); return !!e && e.getAttribute('data-state') === state; }",
              [| row "gravity"; "StaleAddress" |],
              PageWaitForFunctionOptions(Timeout = float32 BrowserWaits.panelUpdates))
          return true
        with _ -> return false
      }
    let lostAttempts = Collections.Generic.List<string>()
    let rec staleClick (attempt: int) : Task<unit> =
      task {
        File.WriteAllText(tuning, edited)
        do! waitForTheEditsRepaint ()
        let! _ = forgeTheOldHash ()
        let! forgedAtTheClick = slider.EvaluateAsync<string>("(el, name) => el.getAttribute(name)", handler)
        do! page.Locator(sprintf "%s [data-testid=tweak-step-up]" (row "gravity")).ClickAsync()
        let! stale = rowIsStale ()
        match stale with
        | true -> ()
        | false ->
          let! handlerNow = slider.EvaluateAsync<string>("(el, name) => el.getAttribute(name)", handler)
          let! stateNow = stateOf page "gravity"
          lostAttempts.Add(
            sprintf
              "attempt %d: row showed '%s' | forged hash held right before the click: %b and after it: %b | the file still holds the edit: %b"
              attempt stateNow (forgedAtTheClick = shown) (handlerNow = shown) (File.ReadAllText tuning = edited))
          match attempt < staleClickAttempts with
          | true -> do! staleClick (attempt + 1)
          | false ->
            Tests.failtestf
              "a stale click never read StaleAddress in %d attempts: %s | POSTs the page sent: %s"
              staleClickAttempts (String.concat " ;; " lostAttempts) (String.concat " || " posts)
      }
    do! staleClick 1
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

/// A value the session holds that the file does not, and a value the program changed, on their own daemon and the same fixture:
///
///   1. a typed number is tried in the session (Try): the file is not touched, the row reads `tweaked`, the strip counts it and lists
///      before and after, and Save says what it writes ("Save 9.5 to Tuning.fs:3")
///   2. Save writes exactly that literal and the tweak ends; Revert puts the file's value back and the row is in step again
///   3. Save all writes every tweak; Revert all puts every tweak back and writes nothing
///   4. a value the program assigns (`lives <- 1`, a `let mutable`) reads `evolved`: no plain Save, a Reset to the file's value, and
///      "Use 1 as the initial value" that asks first (the question names the line and what it means) and writes only when told to
///   5. a mutable record field is assigned in place and tried like a number
///   6. a typed value the literal cannot take is refused with the door's own rule
///   7. at 1400, 900 and 400 wide nothing overflows, and every step has zero console and Datastar errors
let statesJourney () : Task<unit> = task {
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
    let dashboardBase = sprintf "http://localhost:%d" daemon.DashboardPort
    let evalIn (code: string) = Sw.post daemon TestTimeouts.requestPatience "/exec" {| code = code; working_directory = built |}
    let! evaled =
      evalIn "let gravity = 9.8;;\nlet mutable lives = 3;;\ntype Player = { Speed: float; mutable Health: int };;\nlet player = { Speed = 4.5; Health = 100 };;"
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

    let boxOf (name: string) = page.Locator(sprintf "%s [data-testid=tweak-readout]" (row name))
    let press (name: string) (testId: string) = page.Locator(sprintf "%s [data-testid=%s]" (row name) testId).ClickAsync()
    let countOf (testId: string) (name: string) = page.Locator(sprintf "%s [data-testid=%s]" (row name) testId).CountAsync()
    let stripText () = textIn page "[data-testid=live-changes-count]"
    let waitNoStrip () =
      waitJs page "the changes strip to go" BrowserWaits.daemonWork "() => !document.querySelector('[data-testid=live-changes]')" null

    for name in [ "gravity"; "lives" ] do
      do! waitState page name "InSource"
    do! waitJs page "the walk to say the record's field" BrowserWaits.panelUpdates "() => !!document.getElementById('lb_706c61796572')" null
    expectNoErrors errors "with the rows in step"

    // ── 1. a typed number is tried, not written ───────────────────────────────────────────────────────────────────────
    do! boxOf("gravity").FillAsync "9.5"
    do! press "gravity" "tweak-try"
    do! waitState page "gravity" "Tweaked"
    File.ReadAllText tuning |> Expect.equal "trying a value does not touch the file" original
    do! waitJs page "the strip to count the tweak" BrowserWaits.daemonWork
          "() => { const e = document.querySelector('[data-testid=live-changes-count]'); return !!e && /1 tweak not saved/.test(e.innerText); }" null
    let! listing = textIn page "[data-testid=live-changes]"
    listing |> Expect.stringContains "the strip lists before and after" "gravity: 9.8 → 9.5"
    let! saveWords = textIn page (sprintf "%s [data-testid=tweak-save]" (row "gravity"))
    saveWords.Trim() |> Expect.equal "Save says what it writes and where" "Save 9.5 to Tuning.fs:3"
    do! shot page "9-tweaked-and-strip"
    expectNoErrors errors "after trying a value"

    // ── 2. Save writes that literal; Revert puts the file's value back ────────────────────────────────────────────────
    do! press "gravity" "tweak-save"
    let saved = original.Replace("let gravity = 9.8", "let gravity = 9.5")
    do! waitForFile tuning "the tweak written to its literal" saved
    do! waitState page "gravity" "InSource"
    do! waitNoStrip ()
    do! boxOf("gravity").FillAsync "9.0"
    do! press "gravity" "tweak-try"
    do! waitState page "gravity" "Tweaked"
    do! press "gravity" "tweak-revert"
    do! waitState page "gravity" "InSource"
    File.ReadAllText tuning |> Expect.equal "Revert wrote nothing" saved
    do! waitNoStrip ()
    expectNoErrors errors "after a save and a revert"

    // ── 3. Save all and Revert all ───────────────────────────────────────────────────────────────────────────────────
    do! boxOf("gravity").FillAsync "9.0"
    do! press "gravity" "tweak-try"
    do! boxOf("lives").FillAsync "7"
    do! press "lives" "tweak-try"
    do! waitState page "gravity" "Tweaked"
    do! waitState page "lives" "Tweaked"
    do! waitJs page "the strip to count two tweaks" BrowserWaits.daemonWork
          "() => { const e = document.querySelector('[data-testid=live-changes-count]'); return !!e && /2 tweaks not saved/.test(e.innerText); }" null
    do! page.Locator("[data-testid=live-changes-revert-all]").ClickAsync()
    do! waitState page "gravity" "InSource"
    do! waitState page "lives" "InSource"
    File.ReadAllText tuning |> Expect.equal "Revert all wrote nothing" saved
    do! boxOf("gravity").FillAsync "9.0"
    do! press "gravity" "tweak-try"
    do! boxOf("lives").FillAsync "7"
    do! press "lives" "tweak-try"
    do! waitState page "gravity" "Tweaked"
    do! waitState page "lives" "Tweaked"
    do! page.Locator("[data-testid=live-changes-save-all]").ClickAsync()
    let savedAll = saved.Replace("let gravity = 9.5", "let gravity = 9.0").Replace("let mutable lives = 3", "let mutable lives = 7")
    do! waitForFile tuning "every tweak written to its own literal" savedAll
    do! waitState page "gravity" "InSource"
    do! waitState page "lives" "InSource"
    do! waitNoStrip ()
    expectNoErrors errors "after Save all and Revert all"

    // ── 4. a value the program assigned is evolved: reset it, or adopt it after the question ──────────────────────────
    let! assigned = evalIn "lives <- 1;;"
    assigned |> Expect.isOk "the program's assignment ran"
    do! waitState page "lives" "Evolved"
    do! waitJs page "the strip to count the program's change" BrowserWaits.daemonWork
          "() => { const e = document.querySelector('[data-testid=live-changes-count]'); return !!e && /1 value the program changed/.test(e.innerText); }" null
    let! plainSaves = countOf "tweak-save" "lives"
    plainSaves |> Expect.equal "an evolved value has no plain Save" 0
    let! resetWords = textIn page (sprintf "%s [data-testid=tweak-reset]" (row "lives"))
    resetWords.Trim() |> Expect.equal "Reset names the value it goes back to" "Reset to 7"
    let! adoptWords = textIn page (sprintf "%s [data-testid=tweak-adopt]" (row "lives"))
    adoptWords.Trim() |> Expect.equal "adopt names the value it writes" "Use 1 as the initial value"
    do! shot page "10-evolved"
    do! press "lives" "tweak-adopt"
    let confirm = page.Locator(sprintf "%s [data-testid=tweak-adopt-confirm]" (row "lives"))
    do! waitJs page "the question to open" BrowserWaits.panelUpdates
          "sel => { const e = document.querySelector(sel); return !!e && e.getBoundingClientRect().height > 0; }"
          (box (sprintf "%s [data-testid=tweak-adopt-confirm]" (row "lives")))
    let! adoptDisabled = page.Locator(sprintf "%s [data-testid=tweak-adopt]" (row "lives")).IsDisabledAsync()
    adoptDisabled |> Expect.isFalse "the button that opened the question is still a button, not a dimmed dead control"
    let! question = confirm.InnerTextAsync()
    question |> Expect.stringContains "the question names the line" "Write Tuning.fs:"
    question |> Expect.stringContains "and the value" "lives = 1"
    question |> Expect.stringContains "and that the program can move it again" "can move again"
    File.ReadAllText tuning |> Expect.equal "asking wrote nothing" savedAll
    do! shot page "11-adopt-question"
    do! press "lives" "tweak-adopt-no"
    do! waitJs page "the question to close" BrowserWaits.panelUpdates
          "sel => { const e = document.querySelector(sel); return !!e && e.getBoundingClientRect().height === 0; }"
          (box (sprintf "%s [data-testid=tweak-adopt-confirm]" (row "lives")))
    File.ReadAllText tuning |> Expect.equal "declining wrote nothing" savedAll
    do! press "lives" "tweak-adopt"
    do! press "lives" "tweak-adopt-yes"
    let adopted = savedAll.Replace("let mutable lives = 7", "let mutable lives = 1")
    do! waitForFile tuning "the program's value written as the initial value" adopted
    do! waitState page "lives" "InSource"
    let! assignedAgain = evalIn "lives <- 0;;"
    assignedAgain |> Expect.isOk "the program changed it again"
    do! waitState page "lives" "Evolved"
    do! press "lives" "tweak-reset"
    do! waitState page "lives" "InSource"
    File.ReadAllText tuning |> Expect.equal "Reset wrote nothing" adopted
    expectNoErrors errors "after an evolved value was declined, adopted and reset"

    // ── 5. a mutable record field is assigned in place ───────────────────────────────────────────────────────────────
    do! page.Locator("#lb_706c61796572 summary").First.ClickAsync()
    let field = "player/Health"
    do! waitState page field "InSource"
    do! boxOf(field).FillAsync "70"
    do! press field "tweak-try"
    do! waitState page field "Tweaked"
    File.ReadAllText tuning |> Expect.equal "trying a field wrote nothing" adopted
    do! press field "tweak-revert"
    do! waitState page field "InSource"
    expectNoErrors errors "after a mutable field was tried and reverted"

    // ── 5b. the Evaluate box runs every statement of what it is given, and names the one that fails ───────────────────
    let! sessions = Sw.sessionsNow daemon
    let sessionId = Sw.idFor sessions built
    use http = new Net.Http.HttpClient(Timeout = TestTimeouts.httpRequest)
    let evaluateBox (code: string) =
      task {
        let body = System.Text.Json.JsonSerializer.Serialize({| code = code; viewingSessionId = sessionId |})
        use content = new Net.Http.StringContent(body, Text.Encoding.UTF8, "application/json")
        let! response = http.PostAsync(sprintf "%s/dashboard/eval" dashboardBase, content)
        response.IsSuccessStatusCode |> Expect.isTrue "the Evaluate box took it"
      }
    do! evaluateBox "let evA = 1;;\nlet evB = 2;;"
    do! waitState page "evA" "NotInAFile"
    do! waitState page "evB" "NotInAFile"
    do! evaluateBox "let evC = 3;;\nlet evD = nope;;\nlet evE = 5;;"
    do! waitState page "evC" "NotInAFile"
    do! waitJs page "the output to name the statement that failed" BrowserWaits.daemonWork
          "() => { const e = document.getElementById('output-section'); return !!e && /Statement 2 of 3 failed/.test(e.innerText); }" null
    let! outputWords = textIn page "#output-section"
    outputWords |> Expect.stringContains "the statement is named" "let evD = nope;;"
    outputWords |> Expect.stringContains "and what did not run" "The 1 statement after it did not run."
    let! ranAfterTheFailure = page.Locator(row "evE").CountAsync()
    ranAfterTheFailure |> Expect.equal "the statement after the failure never ran" 0
    expectNoErrors errors "after the Evaluate box ran several statements"

    // ── 6. a value the literal cannot take is refused with the door's rule ───────────────────────────────────────────
    do! boxOf("gravity").FillAsync "fast"
    do! press "gravity" "tweak-try"
    do! waitState page "gravity" "Refused"
    let! refusedText = textIn page (row "gravity")
    refusedText |> Expect.stringContains "the door's own words" "fast"
    File.ReadAllText tuning |> Expect.equal "a refused value wrote nothing" adopted
    expectNoErrors errors "after a refused value"

    // ── 7. three widths, with a tweak and the strip on screen ────────────────────────────────────────────────────────
    do! boxOf("gravity").FillAsync "9.5"
    do! press "gravity" "tweak-try"
    do! waitState page "gravity" "Tweaked"
    // ── 7b. the tabs list the bindings they are for ───────────────────────────────────────────────────────────────────
    let bindingBox (name: string) = sprintf "#lb_%s" (Convert.ToHexStringLower(Text.Encoding.UTF8.GetBytes name))
    let isListed (name: string) =
      page.EvaluateAsync<bool>("sel => { const e = document.querySelector(sel); return !!e && !e.classList.contains('live-hidden'); }", bindingBox name)
    let waitListed (name: string) (expected: bool) =
      waitJs page (sprintf "%s to be %s" name (match expected with | true -> "listed" | false -> "hidden")) BrowserWaits.panelUpdates
        "([sel, expected]) => { const e = document.querySelector(sel); return !!e && !e.classList.contains('live-hidden') === expected; }"
        [| box (bindingBox name); box expected |]
    let openTab (token: string) = page.Locator(sprintf "[data-testid=live-tab-%s]" token).ClickAsync()
    do! openTab "changes"
    do! waitListed "gravity" true
    do! waitListed "lives" false
    do! waitListed "player" false
    do! shot page "13-tab-changes"
    do! openTab "pinned"
    do! waitListed "gravity" false
    do! waitJs page "the empty Pinned tab to say so" BrowserWaits.panelUpdates
          "() => { const e = document.querySelector('[data-testid=live-tab-empty-pinned]'); return !!e && !e.classList.contains('live-hidden') && /Nothing is pinned/.test(e.innerText); }" null
    // A pin is set on a listed row, so it is set from All, and the Pinned tab then lists it.
    do! openTab "all"
    do! waitListed "gravity" true
    do! page.Locator(sprintf "#lbp_%s" (Convert.ToHexStringLower(Text.Encoding.UTF8.GetBytes "gravity"))).ClickAsync()
    do! openTab "pinned"
    do! waitListed "gravity" true
    do! waitListed "lives" false
    let! pinnedCount = textIn page "[data-testid=live-tab-count-pinned]"
    pinnedCount.Trim() |> Expect.equal "the Pinned tab counts the pin" "1"
    do! shot page "13b-tab-pinned"
    do! openTab "all"
    do! waitListed "lives" true
    let! allListed = isListed "gravity"
    allListed |> Expect.isTrue "All lists everything again"
    expectNoErrors errors "after the tabs were opened"

    // ── 7c. each tab has a view of its own, and a choice made on one tab is kept for that tab only ───────────────────────
    let waitCols (what: string) (check: string) =
      waitJs page what BrowserWaits.panelUpdates (sprintf "() => { const t = document.getElementById('live-tree'); const c = t ? t.getAttribute('data-cols') || '' : ''; return %s; }" check) null
    do! waitCols "the All tab to draw the workflow's own view: the type column and no more" "c.trim() === 'type'"
    do! openTab "changes"
    do! waitCols "the Changes tab to draw its own view: the type, the source and the state" "c.includes('type') && c.includes('source') && c.includes('state')"
    do! shot page "14-changes-tab-own-view"
    for (width, height) in [ 900, 900; 400, 800 ] do
      do! page.SetViewportSizeAsync(width, height)
      let! overflow = layoutProblems page
      overflow |> Expect.equal (sprintf "the Changes tab at %dpx wide has nothing sticking out: %s" width overflow) ""
      do! shot page (sprintf "14-changes-tab-own-view-%d" width)
    do! page.SetViewportSizeAsync(1400, 1000)
    do! page.Locator("#live-view-btn").ClickAsync()
    do! waitJs page "the panel to say it is for the Changes tab" BrowserWaits.panelUpdates
          "() => { const e = document.querySelector('[data-testid=live-view-heading]'); return !!e && /Changes tab/.test(e.innerText); }" null
    do! page.Locator("[aria-label='Show type'] button", PageLocatorOptions(HasText = "Off")).ClickAsync()
    do! waitCols "the Changes tab to lose its type column" "!c.includes('type') && c.includes('state')"
    do! openTab "all"
    do! waitCols "the All tab to keep its type column: the choice was the Changes tab's" "c.trim() === 'type'"
    let! _ = page.ReloadAsync()
    do! PlaywrightExpect.waitForSSE BrowserWaits.panelUpdates page
    do! waitCols "a reloaded page to start on All" "c.trim() === 'type'"
    do! openTab "changes"
    do! waitCols "the Changes tab to still have the choice after a reload: it is kept for the app" "!c.includes('type') && c.includes('state') && c.includes('source')"
    expectNoErrors errors "after each tab drew its own view"

    // ── 7d. the Watch tab: expressions the person keeps; a name is read, code waits for Run, a bad one is refused ───────────
    let watchRows = "[data-testid=live-watch-row]"
    let waitWatch (what: string) (check: string) =
      waitJs page what BrowserWaits.panelUpdates (sprintf "() => { const rows = [...document.querySelectorAll('%s')].map(r => ({ expr: r.querySelector('[data-testid=live-watch-expression]')?.innerText ?? '', state: r.querySelector('[data-testid=live-watch-state]')?.innerText ?? '', text: r.innerText })); return %s; }" watchRows check) null
    let addExpression (text: string) =
      task {
        do! page.Locator("[data-testid=live-watch-input]").FillAsync text
        do! page.Locator("[data-testid=live-watch-add]").ClickAsync()
      }
    do! openTab "watch"
    do! waitJs page "the Watch tab to show" BrowserWaits.panelUpdates
          "() => { const e = document.getElementById('live-watch-tab'); return !!e && !e.classList.contains('live-hidden'); }" null
    do! waitListed "gravity" false
    let! emptyWords = textIn page "[data-testid=live-watch-empty]"
    emptyWords |> Expect.stringContains "an empty Watch tab says so" "Nothing is watched"
    // a name is read from the session's values: the tweaked gravity, with no code run
    do! addExpression "gravity"
    do! waitWatch "a row for gravity that reads 9.5" "rows.length === 1 && rows[0].expr === 'gravity' && rows[0].state === 'value' && /9\\.5/.test(rows[0].text)"
    // code is held until Run: the sentinel is printed only if it ran, and the row's own text does not spell it
    do! addExpression "printfn \"ran-%s\" \"a63f\"; gravity"
    do! waitWatch "a held row for the expression that runs code" "rows.length === 2 && rows[1].state === 'held' && /not run/.test(rows[1].text)"
    let! outputBefore = page.EvaluateAsync<string>("() => document.body.innerText")
    outputBefore.Contains "ran-a63f" |> Expect.isFalse "the held expression has not run"
    do! shot page "15-watch-tab"
    do! page.Locator(sprintf "%s:nth-child(2) [data-testid=live-watch-run]" watchRows).ClickAsync()
    do! waitWatch "the second row to say it ran" "rows.length === 2 && rows[1].state === 'ran'"
    let! outputAfter = page.EvaluateAsync<string>("() => document.body.innerText")
    outputAfter |> Expect.stringContains "the click ran the expression: its output is in the session's output" "ran-a63f"
    // an expression that does not parse is refused with the parser's reason, and the list stays as it was
    do! addExpression "player.("
    do! waitJs page "the note to say it does not parse" BrowserWaits.panelUpdates
          "() => { const e = document.querySelector('[data-testid=live-watch-note]'); return !!e && /does not parse/.test(e.innerText); }" null
    do! waitWatch "the list unchanged by the refused add" "rows.length === 2"
    // the list is saved for the app, and a reloaded page has it
    let appSettings = Path.Combine(built, ".SageFs", "settings.json")
    File.Exists appSettings |> Expect.isTrue "the list is saved in the app's own settings file"
    File.ReadAllText appSettings |> Expect.stringContains "under its key" "bindings.watch.expressions"
    let! _ = page.ReloadAsync()
    do! PlaywrightExpect.waitForSSE BrowserWaits.panelUpdates page
    do! openTab "watch"
    do! waitWatch "both expressions after a reload" "rows.length === 2 && rows[0].expr === 'gravity'"
    let! watchCount = textIn page "[data-testid=live-tab-count-watch]"
    watchCount.Trim() |> Expect.equal "the tab counts the expressions" "2"
    // a row can be removed
    do! page.Locator(sprintf "%s:nth-child(2) [data-testid=live-watch-remove]" watchRows).ClickAsync()
    do! waitWatch "one row left after a remove" "rows.length === 1 && rows[0].expr === 'gravity'"
    for (width, height) in [ 900, 900; 400, 800; 1400, 1000 ] do
      do! page.SetViewportSizeAsync(width, height)
      let! overflow = layoutProblems page
      overflow |> Expect.equal (sprintf "the Watch tab at %dpx wide has nothing sticking out: %s" width overflow) ""
      do! shot page (sprintf "15-watch-tab-%d" width)
    expectNoErrors errors "after the Watch tab was used"
    do! openTab "all"

    for (width, height, name) in [ 1400, 1000, "1400"; 900, 900, "900"; 400, 800, "400" ] do
      do! page.SetViewportSizeAsync(width, height)
      do! waitJs page "the dock" BrowserWaits.panelUpdates "() => !!document.querySelector('#live-dock')" null
      let! problems = layoutProblems page
      problems |> Expect.equal (sprintf "at %dpx wide nothing overflows sideways: %s" width problems) ""
      do! shot page (sprintf "12-tweaked-%s" name)
    do! page.SetViewportSizeAsync(1400, 1000)
    expectNoErrors errors "across three widths"
    try do! ctx.CloseAsync() with _ -> ()
  with ex ->
    // The daemon's own words about the failure, because a row stuck in a state says what the pane showed and not why.
    Sw.dumpLogs daemon
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

/// How a row shows its value (step 9), in a real browser on its own daemon and the same fixture: a whole number carries a picker that moves
/// it through As is, Hex, Binary and Colour; the choice is kept for the page across the pushes that follow a new value, a page that
/// reloads starts with every row as it is, a number a format cannot draw says why in words, and a type with nothing else to show has no
/// picker at all.
///
///   1. `flags = 255` carries a button that says `As is`, and `gravity` (a Double) carries none
///   2. a press moves 255 to `0xFF`, then `0b11111111`, then the colour `#0000FF` with a blue swatch, then back to the number
///   3. a value that changes (`flags = 4096`) keeps its format: `0x1000`
///   4. a colour row (`tint = 16711680`) draws a red swatch; a number past 0xFFFFFF says it is out of range, in words, and still shows the number
///   5. the button is the page's one button height (28px), square, and a row with nothing chosen is not made taller by it
///   6. a reload starts with every row as it is
///   7. at 1400, 900 and 400 wide nothing overflows sideways, with a format on two rows; screenshots are taken for each
///
/// Every step proves zero console errors and zero Datastar PatchElementsNoTargetsFound.
let formatJourney () : Task<unit> = task {
  let runDir = ref ""
  let daemon = Sw.startDaemon ()
  let mutable playwright: IPlaywright option = None
  let mutable browser: IBrowser option = None
  let mutable failure: exn option = None
  try
    let! built = makeRunCopy ()
    runDir.Value <- built
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
    let dashboardBase = sprintf "http://localhost:%d" daemon.DashboardPort
    let evalIn (code: string) = Sw.post daemon TestTimeouts.requestPatience "/exec" {| code = code; working_directory = built |}
    let! evaled = evalIn "let gravity = 9.8;;\nlet flags = 255;;\nlet tint = 16711680;;"
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

    // A binding's row, found by the name in it: the picker, the value and the format the row carries.
    let rowJs = "name => [...document.querySelectorAll('.live-binding-node')].find(n => n.querySelector(':scope > .live-name')?.innerText === name)"
    let readRow (name: string) (read: string) =
      page.EvaluateAsync<string>(sprintf "name => { const row = (%s)(name); if (!row) return ''; %s }" rowJs read, name)
    let valueOf (name: string) = readRow name "const v = row.querySelector(':scope > .live-preview'); return v ? v.innerText.trim() : '';"
    let wordOf (name: string) = readRow name "const b = row.querySelector(':scope > [data-testid=live-fmt-button]'); return b ? b.innerText.trim() : '(no button)';"
    let waitValue (name: string) (value: string) =
      waitJs page (sprintf "%s to read '%s'" name value) BrowserWaits.daemonWork
        (sprintf "name => { const row = (%s)(name); const v = row && row.querySelector(':scope > .live-preview'); return !!v && v.innerText.trim() === '%s'; }" rowJs value) (box name)
    let pick (name: string) = task {
      let row = page.Locator(sprintf ".live-binding-node:has(> .live-name:text-is('%s'))" name)
      do! row.HoverAsync()
      do! row.Locator(":scope > [data-testid=live-fmt-button]").ClickAsync()
    }

    do! waitValue "flags" "= 255"
    do! waitValue "tint" "= 16711680"
    do! waitValue "gravity" "= 9.8"
    expectNoErrors errors "with the rows drawn"

    // ── 1. whole numbers carry the picker, a Double carries none ──────────────────────────────────────────────────────
    let! flagsWord = wordOf "flags"
    flagsWord |> Expect.equal "flags says how it is shown now" "As is"
    let! gravityWord = wordOf "gravity"
    gravityWord |> Expect.equal "a Double has nothing else to show, so no picker" "(no button)"

    // ── 2. a press moves through the spellings and back ───────────────────────────────────────────────────────────────
    do! pick "flags"
    do! waitValue "flags" "= 0xFF"
    do! waitJs page "the button to say Hex" BrowserWaits.panelUpdates "() => /Hex/.test(document.querySelector('[data-testid=live-fmt-button]').innerText)" null
    let! hexWord = wordOf "flags"
    hexWord |> Expect.equal "the button names the format the row is in" "Hex"
    do! pick "flags"
    do! waitValue "flags" "= 0b11111111"
    do! pick "flags"
    do! waitValue "flags" "= #0000FF"
    let! swatch = readRow "flags" "const s = row.querySelector(':scope > [data-testid=live-swatch]'); return s ? getComputedStyle(s).backgroundColor : '(no swatch)';"
    swatch |> Expect.equal "the colour row draws its colour" "rgb(0, 0, 255)"
    do! shot page "16-format-colour"
    do! pick "flags"
    do! waitValue "flags" "= 255"
    let! swatchGone = readRow "flags" "return row.querySelector('[data-testid=live-swatch]') ? 'swatch' : 'none';"
    swatchGone |> Expect.equal "back to the number, no swatch" "none"
    expectNoErrors errors "after a press through every format"

    // ── 3. a new value keeps the format ───────────────────────────────────────────────────────────────────────────────
    do! pick "flags"
    do! waitValue "flags" "= 0xFF"
    let! changed = evalIn "let flags = 4096;;"
    changed |> Expect.isOk "the REPL took the new value"
    do! waitValue "flags" "= 0x1000"

    // ── 4. a colour, and a number a colour cannot be ──────────────────────────────────────────────────────────────────
    do! pick "tint"
    do! pick "tint"
    do! pick "tint"
    do! waitValue "tint" "= #FF0000"
    let! red = readRow "tint" "const s = row.querySelector(':scope > [data-testid=live-swatch]'); return s ? getComputedStyle(s).backgroundColor : '(no swatch)';"
    red |> Expect.equal "red is red" "rgb(255, 0, 0)"
    let! big = evalIn "let tint = 20000000;;"
    big |> Expect.isOk "the REPL took a number past white"
    do! waitJs page "the row to say a colour cannot be that number, and still show the number" BrowserWaits.daemonWork
          (sprintf "() => { const row = (%s)('tint'); const note = row && row.querySelector('[data-testid=live-fmt-note]'); const v = row && row.querySelector(':scope > .live-preview'); return !!note && /16777215/.test(note.innerText) && !!v && v.innerText.trim() === '= 20000000'; }" rowJs) null
    expectNoErrors errors "after a colour that does not fit"
    let! back = evalIn "let tint = 16711680;;"
    back |> Expect.isOk "the REPL took red again"
    do! waitValue "tint" "= #FF0000"

    // ── 5. one button, 28px, square; a row with nothing chosen is not made taller ─────────────────────────────────────
    let! look = readRow "flags" "const b = row.querySelector(':scope > [data-testid=live-fmt-button]'); const s = getComputedStyle(b); return JSON.stringify({ h: b.getBoundingClientRect().height, radius: s.borderTopLeftRadius, label: b.getAttribute('aria-label') });"
    let buttonLook = Text.Json.JsonDocument.Parse(look).RootElement
    buttonLook.GetProperty("h").GetDouble() |> Expect.equal "the one button height" 28.0
    buttonLook.GetProperty("radius").GetString() |> Expect.equal "square corners" "0px"
    buttonLook.GetProperty("label").GetString() |> Expect.stringContains "the accessible name says what a press does" "Show flags as"
    do! pick "flags"
    do! pick "flags"
    do! pick "flags"
    do! waitValue "flags" "= 4096"
    let! plainHeight = readRow "flags" "return String(row.getBoundingClientRect().height);"
    (float plainHeight < 28.0) |> Expect.isTrue (sprintf "an as-is row (%s px) is not made taller than its button by the button" plainHeight)

    // ── 6. a reload starts with every row as it is ────────────────────────────────────────────────────────────────────
    do! pick "tint"
    do! pick "flags"
    let! _ = page.ReloadAsync()
    do! PlaywrightExpect.waitForSSE BrowserWaits.panelUpdates page
    do! waitValue "flags" "= 4096"
    do! waitValue "tint" "= 16711680"
    expectNoErrors errors "after a reload"

    // ── 7. three widths, a format on two rows ─────────────────────────────────────────────────────────────────────────
    do! pick "flags"
    do! pick "tint"
    do! pick "tint"
    do! pick "tint"
    do! waitValue "flags" "= 0x1000"
    do! waitValue "tint" "= #FF0000"
    for (width, height, name) in [ 1400, 1000, "1400"; 900, 900, "900"; 400, 800, "400" ] do
      do! page.SetViewportSizeAsync(width, height)
      do! waitJs page "the dock" BrowserWaits.panelUpdates "() => !!document.querySelector('#live-dock')" null
      let! problems = layoutProblems page
      problems |> Expect.equal (sprintf "at %dpx wide nothing overflows sideways: %s" width problems) ""
      let! rowProblems =
        page.EvaluateAsync<string>(
          """() => {
            const tree = document.querySelector('#live-tree').getBoundingClientRect();
            const out = [];
            for (const el of document.querySelectorAll('.live-binding-node > *')) {
              const r = el.getBoundingClientRect();
              if (r.width === 0 && r.height === 0) continue;
              if (r.right > tree.right + 1 || r.left < tree.left - 1) out.push((el.className || el.tagName) + ' sticks out: ' + Math.round(r.left) + '..' + Math.round(r.right) + ' vs ' + Math.round(tree.left) + '..' + Math.round(tree.right));
            }
            return out.join(' | ');
          }""")
      rowProblems |> Expect.equal (sprintf "at %dpx wide every cell of a row is inside the tree: %s" width rowProblems) ""
      do! shot page (sprintf "16-format-%s" name)
    do! page.SetViewportSizeAsync(1400, 1000)
    expectNoErrors errors "across three widths"
    try do! ctx.CloseAsync() with _ -> ()
  with ex ->
    Sw.dumpLogs daemon
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

    testTask "[Integration] Dashboard knob browser: a tried value is tweaked until saved, a value the program assigned is evolved and asks before it is written, and Save all and Revert all act on tweaks only" {
      do! statesJourney () }
    |> Integration.register (Integration.Dedicated "--integration-browser")

    testTask "[Integration] Dashboard row format browser: a whole number is shown as it is, in hex, in binary or as a colour, the choice survives a new value and not a reload, and a number a format cannot draw says why" {
      do! formatJourney () }
    |> Integration.register (Integration.Dedicated "--integration-browser")
  ]
