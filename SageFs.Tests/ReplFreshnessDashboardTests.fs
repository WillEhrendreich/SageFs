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
    Uptime = "4m"
    WorkingDir = "/home/will/Work/SageFs/SageFs.Tests/fixtures/RunAppDeltaFixture/.runs/net10.0-6b66e55132574d57ac62"
    LastActivity = "just now"
    TestSummary = None
    CoverageSummary = None
    TestTreemapEntries = [||]; CoverageTreemap = None; BindingEntries = [||]; AgentBadges = []; GuidanceCssClass = ""
    ActiveProject = None; ProjectRoles = []; App = AppRun.AppRunState.NotRunning; WorkerRssBytes = None
    SelfHostStaleness = None; Health = SessionHealth.Healthy
    Source = source
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

    testCase "WHY - a session whose files changed after its build shows a line of its own, with a test id a journey can find" <| fun _ ->
      let html = htmlOf [ cardWith "0a2b3c4d" ReplFreshness.InSync staleSource ]
      html |> Expect.stringContains "the line has its test id" "data-testid=\"session-card-source\""
      html |> Expect.stringContains "it says the source is stale" "STALE SOURCE"
      html |> Expect.stringContains "it names the files" "/repo/SageFs/Mcp.fs"
      html |> Expect.stringContains "it says what to do" "hard_reset_fsi_session"

    testCase "WHY - a session whose build is current, or whose source this card did not read, is as quiet as it was" <| fun _ ->
      let current = htmlOf [ cardWith "0a2b3c4d" ReplFreshness.InSync inSyncSource ]
      current.Contains "session-card-source" |> Expect.isFalse "in sync says nothing"
      let notRead = htmlOf [ card "0a2b3c4d" ReplFreshness.InSync ]
      notRead.Contains "session-card-source" |> Expect.isFalse "not read says nothing"

    testCase "WHY - a rebuild in progress and an unknown source are said too, because 'could not tell' is not 'fine'" <| fun _ ->
      let rebuilding = htmlOf [ cardWith "0a2b3c4d" ReplFreshness.InSync (CardSource.Read (SourceState.Rebuilding builtAt)) ]
      rebuilding |> Expect.stringContains "rebuilding is said" "A rebuild is in progress"
      let unknown = htmlOf [ cardWith "0a2b3c4d" ReplFreshness.InSync (CardSource.Read (SourceState.Unknown UnknownReason.NoProjectLoaded)) ]
      unknown |> Expect.stringContains "unknown is said" "could not be told"

    testCase "WHY - the two facts have their own lines and their own words, so one can never be read as the other" <| fun _ ->
      let html = htmlOf [ cardWith "0a2b3c4d" behind staleSource ]
      html |> Expect.stringContains "the REPL-behind-the-app line" "data-testid=\"session-card-freshness\""
      html |> Expect.stringContains "the disk-ahead-of-the-build line" "data-testid=\"session-card-source\""
      let freshnessAt = html.IndexOf "session-card-freshness"
      let sourceAt = html.IndexOf "session-card-source"
      (freshnessAt < sourceAt) |> Expect.isTrue "two elements, the REPL line first"
      let freshnessLine = html.Substring(freshnessAt, sourceAt - freshnessAt)
      freshnessLine.Contains "STALE SOURCE" |> Expect.isFalse "the freshness line does not say the source is stale"
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
      // One card with both lines, so each is also checked against the other.
      let html = htmlOf [ cardWith "0a2b3c4d" behind staleSource ]
      let mutable heights = []
      for width in widths do
        do! page.SetViewportSizeAsync(width, 900)
        do! page.SetContentAsync(pageFor width html)
        for line in [ FreshnessLine; SourceLine ] do
          let! problems = layoutProblems page line
          problems |> Expect.equal (sprintf "at %dpx wide nothing about %s overflows or overlaps" width line) ""
          let! height = heightOf page line
          heights <- (width, line, height) :: heights
      let at (w: int) (line: string) = heights |> List.find (fun (width, l, _) -> width = w && l = line) |> fun (_, _, h) -> h
      // Wrapping, not shrinking or clipping: the narrower the card, the taller each line.
      for line in [ FreshnessLine; SourceLine ] do
        Expect.isGreaterThan (sprintf "%s is taller at 240px (%f) than at 1280px (%f), because it wraps" line (at 240 line) (at 1280 line)) (at 240 line, at 1280 line)
    }
    |> Integration.register (Integration.Dedicated "--integration-browser")
  ]
