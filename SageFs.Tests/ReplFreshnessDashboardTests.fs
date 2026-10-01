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

let private card (id: string) (freshness: ReplFreshness) : ParsedSession =
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
    Freshness = freshness }

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

/// Every width the card has to survive, from a phone to a wide sidebar.
let private widths = [ 240; 320; 480; 800; 1280 ]

/// What is wrong with the card at the current width, as one string (empty when nothing is).
let private layoutProblems (page: IPage) : Task<string> =
  page.EvaluateAsync<string>("""() => {
    const problems = [];
    const card = document.querySelector('[data-testid="session-card"]');
    const banner = document.querySelector('[data-testid="session-card-freshness"]');
    const actions = card && card.querySelector('.session-card-actions');
    if (!card) return 'no card';
    if (!banner) return 'no banner';
    const c = card.getBoundingClientRect(), b = banner.getBoundingClientRect();
    if (b.width <= 0 || b.height <= 0) problems.push('the banner has no size');
    if (b.left < c.left - 0.5 || b.right > c.right + 0.5) problems.push('the banner spills out of the card sideways');
    if (banner.scrollWidth > banner.clientWidth + 1) problems.push('the banner text overflows its own box');
    if (card.scrollWidth > card.clientWidth + 1) problems.push('the card scrolls sideways');
    if (document.documentElement.scrollWidth > document.documentElement.clientWidth + 1) problems.push('the page scrolls sideways');
    if (actions) {
      const a = actions.getBoundingClientRect();
      if (b.bottom > a.top + 0.5) problems.push('the banner overlaps the buttons beneath it');
    }
    for (const kid of card.querySelectorAll('.session-card-body > *')) {
      if (kid === banner) continue;
      const k = kid.getBoundingClientRect();
      const overlapX = Math.min(k.right, b.right) - Math.max(k.left, b.left);
      const overlapY = Math.min(k.bottom, b.bottom) - Math.max(k.top, b.top);
      if (overlapX > 1 && overlapY > 1) problems.push('the banner overlaps ' + kid.className);
    }
    return problems.join(' | ');
  }""")

let private heightOfBanner (page: IPage) : Task<float> =
  page.EvaluateAsync<float>("() => document.querySelector('[data-testid=\"session-card-freshness\"]').getBoundingClientRect().height")

let private pageFor (width: int) (html: string) : string =
  sprintf """<!doctype html><html><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1"><style>%s</style></head><body style="margin:0"><div id="sessions-host" style="width:%dpx;box-sizing:border-box;padding:0 8px">%s</div></body></html>""" dashboardCss width html

[<Tests>]
let browserTests =
  testList "ReplFreshness on the session card (real browser)" [
    testCase "[Integration] the banner wraps inside the card at every width and never overlaps, overflows or scrolls the card sideways" (fun () ->
      let work = task {
        let! playwright = Playwright.CreateAsync()
        use _playwright = playwright
        let! browser = playwright.Chromium.LaunchAsync(BrowserTypeLaunchOptions(Headless = true))
        try
          let! page = browser.NewPageAsync()
          let html = htmlOf [ card "0a2b3c4d" behind ]
          let mutable heights = []
          for width in widths do
            do! page.SetViewportSizeAsync(width, 900)
            do! page.SetContentAsync(pageFor width html)
            let! problems = layoutProblems page
            problems |> Expect.equal (sprintf "at %dpx wide nothing overflows or overlaps" width) ""
            let! height = heightOfBanner page
            heights <- (width, height) :: heights
          let at (w: int) = heights |> List.find (fun (width, _) -> width = w) |> snd
          // Wrapping, not shrinking or clipping: the narrower the card, the taller the banner.
          Expect.isGreaterThan (sprintf "the banner is taller at 240px (%f) than at 1280px (%f), because it wraps" (at 240) (at 1280)) (at 240, at 1280)
        finally
          browser.CloseAsync().GetAwaiter().GetResult()
      }
      work.GetAwaiter().GetResult())
    |> Integration.register (Integration.Dedicated "--integration-browser")
  ]
