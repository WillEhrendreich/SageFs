/// The dashboard's session card, when the session's REPL is behind its app.
///
/// The card is built from typed state (`ParsedSession`) and rendered with Falco.Markup, so the first half of this file
/// reads the HTML the card renders: the banner is there for a session that is behind, absent for one that is level, says what
/// to do, and carries a test id so a journey finds it without matching words. The second half puts that same HTML in a real
/// Chromium page with the dashboard's own stylesheet and checks the part only a browser can: at every width the banner wraps
/// inside the card, never spills out of it, never overlaps the buttons beneath it, and never makes the card scroll sideways.
module SageFs.Tests.ReplFreshnessDashboardTests

open System
open System.Threading.Tasks
open Expecto
open Expecto.Flip
open Falco.Markup
open Microsoft.Playwright
open SageFs
open SageFs.Server.Dashboard
open SageFs.Server.DashboardTypes
open SageFs.Server.DashboardFragments

module Integration = SageFs.Tests.TestInfrastructure.Integration

let private cardWith (id: string) (freshness: ReplFreshness) (source: CardSource) : ParsedSession =
  { Id = WorkerProtocol.SessionId.validate id |> Result.defaultValue (WorkerProtocol.SessionId.newId ())
    Status = SessionDisplayStatus.Running
    StatusMessage = None
    ProjectsText = "(RunAppDeltaFixture.fsproj)"
    EvalCount = 3
    Uptime = Since.At 0L
    WorkingDir = "/home/will/Work/SageFs/SageFs.Tests/fixtures/RunAppDeltaFixture/.runs/net10.0-6b66e55132574d57ac62"
    LastActivity = Since.At 0L
    TestSummary = None
    CoverageSummary = None
    TestTreemapEntries = [||]; CoverageTreemap = None; BindingEntries = [||]; AgentBadges = []; GuidanceCssClass = ""
    ActiveProject = None; ProjectRoles = []; App = AppRun.AppRunState.NotRunning; WorkerRssBytes = None
    SelfHostStaleness = None; Health = SessionHealth.Healthy
    Source = source
    Teardown = SageFs.Server.TeardownRegistry.TeardownState.NotTearingDown
    Freshness = freshness }

let private card (id: string) (freshness: ReplFreshness) : ParsedSession = cardWith id freshness CardSource.NotReadOnThisCard

let private builtAt = DateTime(2026, 10, 2, 0, 1, 0, DateTimeKind.Utc)

/// Files were edited after the build the session runs: the DISK is ahead of the build.
let private staleSource =
  CardSource.Read (
    SourceState.Stale
      [ { Path = "/repo/SageFs/Mcp.fs"; Because = StaleBecause.EditedAfterBuild (DateTime(2026, 10, 2, 0, 5, 0, DateTimeKind.Utc), builtAt) }
        { Path = "/repo/SageFs.Core/Timeouts.fs"; Because = StaleBecause.EditedAfterBuild (DateTime(2026, 10, 2, 0, 6, 0, DateTimeKind.Utc), builtAt) } ])

let private inSyncSource = CardSource.Read (SourceState.InSync (builtAt, 12))

let private behind = ReplFreshness.BehindApp (3, [ "RunAppDeltaFixture.Handlers.describe"; "RunAppDeltaFixture.Handlers.makeHeld"; "RunAppDeltaFixture.Handlers.taskBody" ])

let private htmlOf (sessions: ParsedSession list) : string =
  renderSessionsForSession "" sessions false |> renderNode

[<Tests>]
let renderTests =
  testList "ReplFreshness on the session card (rendered HTML)" [

    testCase "WHY - a session whose REPL is behind its app shows a banner on its own card, with a test id a journey can find" <| fun _ ->
      let html = htmlOf [ card "0a2b3c4d" behind ]
      html |> Expect.stringContains "the banner has its test id" "data-testid=\"session-card-freshness\""
      html |> Expect.stringContains "it says behind" "BEHIND"
      html |> Expect.stringContains "it says what to do" "hard_reset_fsi_session"
      html |> Expect.stringContains "it names what changed" "RunAppDeltaFixture.Handlers.describe"

    testCase "WHY - a session whose freshness could not be read says so on its card, in words, and never looks level" <| fun _ ->
      let html = htmlOf [ card "0a2b3c4d" (ReplFreshness.Unknown (FreshnessUnknown.LookupFailed "registry timed out")) ]
      html |> Expect.stringContains "the same line, with its test id" "data-testid=\"session-card-freshness\""
      html |> Expect.stringContains "it says it could not be read" "could not be read"
      html |> Expect.stringContains "and why" "registry timed out"

    testCase "WHY - a level session has no banner, so the card is as quiet as it was" <| fun _ ->
      let html = htmlOf [ card "0a2b3c4d" ReplFreshness.InSync ]
      html.Contains "session-card-freshness" |> Expect.isFalse "nothing to say"

    testCase "WHY - the banner sits inside the card it is about, so a second session's card is not marked by the first's" <| fun _ ->
      let html = htmlOf [ card "0a2b3c4d" behind; card "0a2b3c4e" ReplFreshness.InSync ]
      let secondCard = html.Substring(html.IndexOf "session-card-0a2b3c4e")
      secondCard.Contains "session-card-freshness" |> Expect.isFalse "the level session's card has none"
      (html.IndexOf "session-card-freshness" < html.IndexOf "session-card-0a2b3c4e") |> Expect.isTrue "the first card has it"

    testCase "WHY - the banner is a part of the card's single column, so horizontal resizing can only wrap it" <| fun _ ->
      let html = htmlOf [ card "0a2b3c4d" behind ]
      // Inside the card body, which the stylesheet makes a wrapping column, and before the actions row.
      let body = html.IndexOf "session-card-body"
      let banner = html.IndexOf "session-card-freshness"
      let actions = html.IndexOf "session-card-actions"
      (body < banner && banner < actions) |> Expect.isTrue (sprintf "body %d, banner %d, actions %d" body banner actions)

    testCase "WHY - the banner's text is escaped, because a declaration name is the user's own text" <| fun _ ->
      let html = htmlOf [ card "0a2b3c4d" (ReplFreshness.BehindApp (1, [ "<script>alert(1)</script>" ])) ]
      html.Contains "<script>alert(1)" |> Expect.isFalse "never raw"
  ]

[<Tests>]
let sourceRenderTests =
  testList "SourceState on the session card (rendered HTML)" [

    testCase "WHY - a session whose files changed after its build shows a block of its own, with a test id a journey can find, one sentence and the file names" <| fun _ ->
      let html = htmlOf [ cardWith "0a2b3c4d" ReplFreshness.InSync staleSource ]
      html |> Expect.stringContains "the block has its test id" "data-testid=\"session-card-source\""
      html |> Expect.stringContains "it says in one sentence what changed" "2 files changed since the build."
      html |> Expect.stringContains "and the card's state word says the session runs older code" "running older code"
      html |> Expect.stringContains "it names the files, as file names" "Mcp.fs"
      html.Contains "STALE SOURCE" |> Expect.isFalse "the red monospace wall is the tool result's wording, not the card's"

    testCase "WHY - the stale block has one button that does the dashboard's own hard reset with a rebuild, on the card being viewed" <| fun _ ->
      let html = renderSessionsForSession "0a2b3c4d" [ cardWith "0a2b3c4d" ReplFreshness.InSync staleSource ] false |> renderNode
      html |> Expect.stringContains "the button has its test id" "data-testid=\"session-card-rebuild\""
      html |> Expect.stringContains "it posts to the existing hard reset route" "@post('/dashboard/hard-reset')"
      html |> Expect.stringContains "it says what it does, in words" "Rebuild and reload"
      let fromBlock = html.Substring(html.IndexOf "session-card-source")
      let block = fromBlock.Substring(0, fromBlock.IndexOf "session-card-actions")
      block.Split("<button").Length - 1 |> Expect.equal "exactly one button in the block" 1

    testCase "WHY - a card that is not being viewed has no rebuild button, because the hard reset acts on the session in view" <| fun _ ->
      let html = htmlOf [ cardWith "0a2b3c4d" ReplFreshness.InSync staleSource ]
      html.Contains "session-card-rebuild" |> Expect.isFalse "no button on a card the reset would not act on"

    testCase "WHY - the times and the file list sit in a closed disclosure, so the card shows one sentence until the person asks for more" <| fun _ ->
      let html = renderSessionsForSession "0a2b3c4d" [ cardWith "0a2b3c4d" ReplFreshness.InSync staleSource ] false |> renderNode
      html |> Expect.stringContains "a details element holds the detail" "<details"
      html |> Expect.stringContains "it has its test id" "data-testid=\"session-card-source-details\""
      let details = html.Substring(html.IndexOf "session-card-source-details")
      details |> Expect.stringContains "the edit time is in it" "edited 2026-10-02 00:05:00Z"
      details |> Expect.stringContains "and the build time" "the build is from 2026-10-02 00:01:00Z"
      html.Contains "<details open" |> Expect.isFalse "it is closed until opened"

    testCase "WHY - the card never prints an absolute path, because a path from the root of the disk is a wall nobody reads" <| fun _ ->
      let workingDir = "/home/will/proj"
      let inside =
        CardSource.Read (
          SourceState.Stale
            [ { Path = "/home/will/proj/src/Tuning.fs"; Because = StaleBecause.EditedAfterBuild (builtAt.AddMinutes 5.0, builtAt) }
              { Path = "/somewhere/else/Other.fs"; Because = StaleBecause.EditedAfterBuild (builtAt.AddMinutes 6.0, builtAt) } ])
      let html =
        renderSessionsForSession "0a2b3c4d" [ { cardWith "0a2b3c4d" ReplFreshness.InSync inside with WorkingDir = workingDir } ] false |> renderNode
      let block = html.Substring(html.IndexOf "data-testid=\"session-card-source\"")
      // The card's own working-directory row follows the block and is not part of it.
      let block = block.Substring(0, block.IndexOf "class=\"session-dir\"")
      block |> Expect.stringContains "a file under the working directory is named relative to it" "src/Tuning.fs"
      block |> Expect.stringContains "a file outside it is named by its file name" "Other.fs"
      block.Contains "/home/will" |> Expect.isFalse "no absolute path from the working directory"
      block.Contains "/somewhere/else" |> Expect.isFalse "no absolute path from elsewhere"

    testCase "WHY - a session whose build is current, or whose source this card did not read, is as quiet as it was" <| fun _ ->
      let current = htmlOf [ cardWith "0a2b3c4d" ReplFreshness.InSync inSyncSource ]
      current.Contains "session-card-source" |> Expect.isFalse "in sync says nothing"
      let notRead = htmlOf [ card "0a2b3c4d" ReplFreshness.InSync ]
      notRead.Contains "session-card-source" |> Expect.isFalse "not read says nothing"

    testCase "WHY - a rebuild in progress and an unknown source are said too, because 'could not tell' is not 'fine'" <| fun _ ->
      let rebuilding = htmlOf [ cardWith "0a2b3c4d" ReplFreshness.InSync (CardSource.Read (SourceState.Rebuilding builtAt)) ]
      rebuilding |> Expect.stringContains "rebuilding is said" "Rebuilding: this session keeps serving the build from before it"
      let unknown = htmlOf [ cardWith "0a2b3c4d" ReplFreshness.InSync (CardSource.Read (SourceState.Unknown (UnknownReason.LoadTimeNotReported "no report"))) ]
      unknown |> Expect.stringContains "unknown is said" "Cannot tell whether this session runs current code."
      unknown |> Expect.stringContains "and why, behind the disclosure" "no report"

    testCase "WHY - a session with no project says nothing about its build, because there is no build for it to be behind" <| fun _ ->
      let bare = htmlOf [ cardWith "0a2b3c4d" ReplFreshness.InSync (CardSource.Read (SourceState.Unknown UnknownReason.NoProjectLoaded)) ]
      bare.Contains "session-card-source" |> Expect.isFalse "a bare session is not behind anything"

    testCase "WHY - the two facts have their own lines and their own words, so one can never be read as the other" <| fun _ ->
      let html = htmlOf [ cardWith "0a2b3c4d" behind staleSource ]
      html |> Expect.stringContains "the REPL-behind-the-app line" "data-testid=\"session-card-freshness\""
      html |> Expect.stringContains "the disk-ahead-of-the-build line" "data-testid=\"session-card-source\""
      let freshnessAt = html.IndexOf "session-card-freshness"
      let sourceAt = html.IndexOf "session-card-source"
      (freshnessAt < sourceAt) |> Expect.isTrue "two elements, the REPL line first"
      let freshnessLine = html.Substring(freshnessAt, sourceAt - freshnessAt)
      freshnessLine.Contains "older code" |> Expect.isFalse "the freshness line does not say the source is stale"
      let actionsAt = html.IndexOf "session-card-actions"
      let sourceLine = html.Substring(sourceAt, actionsAt - sourceAt)
      sourceLine.Contains "BEHIND" |> Expect.isFalse "the source line does not say the REPL is behind the app"

    testCase "WHY - the source line sits inside the card's single column before the buttons, so horizontal resizing can only wrap it" <| fun _ ->
      let html = htmlOf [ cardWith "0a2b3c4d" ReplFreshness.InSync staleSource ]
      let body = html.IndexOf "session-card-body"
      let line = html.IndexOf "session-card-source"
      let actions = html.IndexOf "session-card-actions"
      (body < line && line < actions) |> Expect.isTrue (sprintf "body %d, line %d, actions %d" body line actions)

    testCase "WHY - the source line's text is escaped, because a path is the user's own text" <| fun _ ->
      let hostile =
        CardSource.Read (SourceState.Stale [ { Path = "/repo/<script>alert(1)</script>.fs"; Because = StaleBecause.EditedAfterBuild (builtAt.AddMinutes 1.0, builtAt) } ])
      let html = htmlOf [ cardWith "0a2b3c4d" ReplFreshness.InSync hostile ]
      html.Contains "<script>alert(1)" |> Expect.isFalse "never raw"
  ]

/// Every width the card has to survive, from a phone to a wide sidebar.
let private widths = [ 240; 320; 480; 800; 1280 ]

/// The two lines a card can carry about a session's build: the REPL is behind the app, and the files are ahead of the build.
[<Literal>]
let private FreshnessLine = "session-card-freshness"

[<Literal>]
let private SourceLine = "session-card-source"

/// What is wrong with the card at the current width for the line with this test id, as one string (empty when nothing is).
/// Every other child of the card body, the other line included, must not be overlapped by it.
let private layoutProblems (page: IPage) (testId: string) : Task<string> =
  page.EvaluateAsync<string>("""testId => {
    const problems = [];
    const card = document.querySelector('[data-testid="session-card"]');
    const banner = document.querySelector('[data-testid="' + testId + '"]');
    const actions = card && card.querySelector('.session-card-actions');
    if (!card) return 'no card';
    if (!banner) return 'no ' + testId;
    const c = card.getBoundingClientRect(), b = banner.getBoundingClientRect();
    if (b.width <= 0 || b.height <= 0) problems.push('the line has no size');
    if (b.left < c.left - 0.5 || b.right > c.right + 0.5) problems.push('the line spills out of the card sideways');
    if (banner.scrollWidth > banner.clientWidth + 1) problems.push('the line text overflows its own box');
    const rebuild = card.querySelector('[data-testid="session-card-rebuild"]');
    if (rebuild && testId === 'session-card-source') {
      const r = rebuild.getBoundingClientRect();
      if (r.width <= 0 || r.height <= 0) problems.push('the rebuild button has no size');
      if (r.left < c.left - 0.5 || r.right > c.right + 0.5) problems.push('the rebuild button spills out of the card sideways');
      if (rebuild.scrollWidth > rebuild.clientWidth + 1) problems.push('the rebuild button text is clipped');
    }
    if (card.scrollWidth > card.clientWidth + 1) problems.push('the card scrolls sideways');
    if (document.documentElement.scrollWidth > document.documentElement.clientWidth + 1) problems.push('the page scrolls sideways');
    if (actions) {
      const a = actions.getBoundingClientRect();
      if (b.bottom > a.top + 0.5) problems.push('the line overlaps the buttons beneath it');
    }
    for (const kid of card.querySelectorAll('.session-card-body > *')) {
      if (kid === banner) continue;
      const k = kid.getBoundingClientRect();
      const overlapX = Math.min(k.right, b.right) - Math.max(k.left, b.left);
      const overlapY = Math.min(k.bottom, b.bottom) - Math.max(k.top, b.top);
      if (overlapX > 1 && overlapY > 1) problems.push('the line overlaps ' + kid.className);
    }
    return problems.join(' | ');
  }""", testId)

let private heightOf (page: IPage) (testId: string) : Task<float> =
  page.EvaluateAsync<float>("testId => document.querySelector('[data-testid=\"' + testId + '\"]').getBoundingClientRect().height", testId)

let private pageFor (width: int) (html: string) : string =
  sprintf """<!doctype html><html><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1"><style>%s</style></head><body style="margin:0"><div id="sessions-host" style="width:%dpx;box-sizing:border-box;padding:0 8px">%s</div></body></html>""" dashboardCss width html

[<Tests>]
let browserTests =
  testList "ReplFreshness on the session card (real browser)" [
    testTask "[Integration] the banner wraps inside the card at every width and never overlaps, overflows or scrolls the card sideways" {
      let! (playwright: IPlaywright) = Playwright.CreateAsync()
      // The driver lives as long as the test process, the way the other browser journeys' does; the browser is closed with the test.
      let! (browser: IBrowser) = playwright.Chromium.LaunchAsync(BrowserTypeLaunchOptions(Headless = true))
      use _browser = (browser :> IAsyncDisposable)
      let! (page: IPage) = browser.NewPageAsync()
      // One card with both lines, so each is also checked against the other. It is the viewed card, so the stale block carries its
      // rebuild button and the button is checked too: the remedy must never be clipped.
      let html = renderSessionsForSession "0a2b3c4d" [ cardWith "0a2b3c4d" behind staleSource ] false |> renderNode
      let mutable heights = []
      for width in widths do
        do! page.SetViewportSizeAsync(width, 900)
        do! page.SetContentAsync(pageFor width html)
        for line in [ FreshnessLine; SourceLine ] do
          let! problems = layoutProblems page line
          problems |> Expect.equal (sprintf "at %dpx wide nothing about %s overflows or overlaps" width line) ""
          let! height = heightOf page line
          heights <- (width, line, height) :: heights
        // The disclosure opened: the file list wraps inside the card too, and nothing in it overflows.
        let! _ = page.EvaluateAsync("() => { for (const d of document.querySelectorAll('[data-testid=\"session-card-source-details\"]')) d.open = true }")
        let! openProblems = layoutProblems page SourceLine
        openProblems |> Expect.equal (sprintf "at %dpx wide the opened details do not overflow or overlap" width) ""
      let at (w: int) (line: string) = heights |> List.find (fun (width, l, _) -> width = w && l = line) |> fun (_, _, h) -> h
      // Wrapping, not shrinking or clipping: the narrower the card, the taller each line, or the same height when the words still fit.
      // The source note is now the cause only ("1 file changed since the build."), short enough to fit a 240px card on one line, so
      // it is held to never getting shorter; the freshness line is long enough that it must wrap.
      Expect.isGreaterThan (sprintf "%s is taller at 240px (%f) than at 1280px (%f), because it wraps" FreshnessLine (at 240 FreshnessLine) (at 1280 FreshnessLine)) (at 240 FreshnessLine, at 1280 FreshnessLine)
      Expect.isGreaterThanOrEqual (sprintf "%s is no shorter at 240px (%f) than at 1280px (%f)" SourceLine (at 240 SourceLine) (at 1280 SourceLine)) (at 240 SourceLine, at 1280 SourceLine)
    }
    |> Integration.register (Integration.Dedicated "--integration-browser")
  ]
