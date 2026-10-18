/// The dashboard on a phone, in a real browser and with no daemon. The context is a mobile one (`IsMobile`, touch, a
/// 375 x 812 screen), which is what makes the viewport meta matter: a desktop-sized context ignores it, a mobile one lays
/// the page out at 980 px without it. The page is the real shell and main content with the real stylesheet and Datastar
/// bundle, served from memory, with several session cards whose project names and paths are far longer than the screen.
module SageFs.Tests.DashboardMobileBrowserTests

open System
open System.Threading.Tasks
open Expecto
open Expecto.Flip
open Falco.Markup
open Microsoft.Playwright
open SageFs
open SageFs.Server
open SageFs.Server.DashboardTypes
open SageFs.Server.DashboardFragments
open SageFs.Tests.DashboardBrowserTests

module Integration = SageFs.Tests.TestInfrastructure.Integration

let private origin = "http://sagefs.test"

/// A phone's screen, in CSS pixels. Fixed so a case means the same on every machine.
let private phoneWidth = 375
let private phoneHeight = 812

/// How far past the screen's edge a box may reach before it counts as overflowing (sub-pixel rounding).
let private overflowTolerance = 1.0

let private sessionIds = [ "0a2b3c4d"; "0a2b3c4e"; "0a2b3c4f" ]

let private longProjects =
  "(SageFs.Tests.Integration.WithAVeryLongProjectName.fsproj, SageFs.Core.fsproj, SageFs.Host.fsproj)"

let private longDirectory =
  "/home/will/Work/SageFs/.claude/worktrees/agent-a31b473c9dfa3ec9b/a/directory/that/goes/on/and/on/and/on"

let private card (id: string) : ParsedSession =
  let sid = WorkerProtocol.SessionId.validate id |> Result.defaultValue (WorkerProtocol.SessionId.newId ())
  { Id = sid; Status = SessionDisplayStatus.Running; StatusMessage = None
    ProjectsText = longProjects; EvalCount = 12
    Uptime = Since.At 0L; WorkingDir = longDirectory; LastActivity = Since.At 0L
    TestSummary = None; CoverageSummary = None; TestTreemapEntries = [||]; CoverageTreemap = None
    BindingEntries = [||]
    AgentBadges =
      [ { Name = "claude-code-with-a-long-agent-name"; IntentLabel = "refactoring the dashboard shell"; CssClass = "agent-badge"; DetailLabel = "" }
        { Name = "codex"; IntentLabel = "writing tests"; CssClass = "agent-badge"; DetailLabel = "" } ]
    GuidanceCssClass = ""
    ActiveProject = None
    ProjectRoles = []
    App = AppRun.AppRunState.NotRunning
    WorkerRssBytes = None; SelfHostStaleness = None; Source = CardSource.NotReadOnThisCard
    Freshness = ReplFreshness.InSync; Health = SessionHealth.Healthy }

let private snapshot : DashboardSnapshot =
  let empty = Elem.div [] []
  let viewing = List.head sessionIds
  { DashboardSnapshot.Version = "0.0.0"
    SessionState = "ready"; SessionId = viewing; WorkingDir = longDirectory
    WarmupProgress = ""; WorkflowLabel = "REPL"; EvalStats = FixtureStats.noEvals
    ThemeName = "default"; ConnectionLabel = None; ConnectionState = DashboardConnectionState.Connected
    HotReloadPanel = empty; SessionContextPanel = empty
    OutputPanel = renderOutputForSession viewing 1 [ { Timestamp = None; Kind = ResultLine; Text = "val it: int = 42" } ] "no output"
    SessionsPanel = renderSessionsForSession viewing (sessionIds |> List.map card) false
    SessionPicker = empty
    ThemePicker = empty; ThemeVars = empty
    BindingsPanel = empty; DaemonHealth = empty; FailureNarrativesPanel = empty; DiagnosticsPanel = empty
    FilmstripPanel = empty; AlarmPanel = empty; LiveTestingPanel = empty; FrictionPanel = empty
    CohortPanel = empty; HygienePanel = empty
    ActiveProject = None; ProjectRoles = []
    App = AppRun.AppRunState.NotRunning
    EvalToPixelP50Ms = None; EvalToPixelP99Ms = None }

let private shellHtml : string =
  Dashboard.renderShell "0.0.0" "client-1" (List.head sessionIds) "/work" (renderMainContent snapshot) |> renderNode

/// Serve the dashboard from memory. The stream is answered with an empty event stream, so nothing is ever pushed.
let private serve (page: IPage) = task {
  do! page.RouteAsync("**/*", Func<IRoute, Task>(fun route ->
    task {
      let fulfil (contentType: string) (body: string) =
        route.FulfillAsync(RouteFulfillOptions(Status = 200, ContentType = contentType, Body = body))
      match Uri(route.Request.Url).AbsolutePath with
      | "/dashboard" -> do! fulfil "text/html; charset=utf-8" shellHtml
      | "/dashboard/dashboard.css" -> do! fulfil "text/css; charset=utf-8" Dashboard.dashboardCss
      | "/dashboard/datastar.js" -> do! fulfil "application/javascript; charset=utf-8" Dashboard.datastarBundle
      | path when path.StartsWith "/dashboard/stream/" -> do! fulfil "text/event-stream" ""
      | _ -> do! route.FulfillAsync(RouteFulfillOptions(Status = 404, Body = ""))
    } :> Task))
}

/// A phone on the in-memory dashboard, run through `body`. The browser is closed by awaiting, never by blocking a thread
/// on it, so a failure is carried out of the block and rethrown after.
let private onAPhone (body: IPage -> Task<unit>) = task {
  let! playwright = Playwright.CreateAsync()
  let! browser = playwright.Chromium.LaunchAsync(BrowserTypeLaunchOptions(Headless = true))
  let! context =
    browser.NewContextAsync(
      BrowserNewContextOptions(
        ViewportSize = ViewportSize(Width = phoneWidth, Height = phoneHeight),
        IsMobile = true,
        HasTouch = true,
        DeviceScaleFactor = 2.0f))
  let! page = context.NewPageAsync()
  let! failure =
    task {
      try
        do! serve page
        let! _ = page.GotoAsync(sprintf "%s/dashboard" origin)
        let! _ = page.WaitForSelectorAsync(".session-row", PageWaitForSelectorOptions(Timeout = float32 BrowserWaits.pageRenders))
        do! body page
        return None
      with ex -> return Some ex
    }
  try do! context.CloseAsync() with _ -> ()
  try do! browser.CloseAsync() with _ -> ()
  playwright.Dispose()
  match failure with
  | Some ex -> System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex).Throw()
  | None -> ()
}

/// Every box under `#main` whose right edge is past the screen, named by its tag, id and class.
let private overflowingBoxes (page: IPage) : Task<string> =
  page.EvaluateAsync<string>(
    sprintf
      """() => {
        var limit = window.innerWidth + %f;
        // A box inside a scroller the page offers on purpose is allowed to be wider than the screen.
        var scrolls = el => { for (var p = el.parentElement; p && p !== document.body; p = p.parentElement) { var o = getComputedStyle(p).overflowX; if (o === 'auto' || o === 'scroll') return true; } return false; };
        return Array.from(document.querySelectorAll('#main *'))
          .filter(el => el.getBoundingClientRect().width > 0 && el.getBoundingClientRect().right > limit && !scrolls(el))
          .slice(0, 8)
          .map(el => el.tagName.toLowerCase() + (el.id ? '#' + el.id : '') + '.' + String(el.className).split(' ')[0] + ' right=' + Math.round(el.getBoundingClientRect().right))
          .join(' | ');
      }"""
      overflowTolerance)

let private screenshot (page: IPage) (name: string) = task {
  let path = IO.Path.Combine(PlaywrightFixture.screenshotDir, sprintf "mobile-%s.png" name)
  do! PlaywrightFixture.takeScreenshot page true path
}

[<Tests>]
let tests =
  testSequenced <|
  testList "Dashboard mobile browser tests" [

    testTask "[Integration] Mobile: the page is laid out at the phone's width, so the width media queries apply" {
      do! onAPhone (fun page -> task {
        let! layoutWidth = page.EvaluateAsync<int>("() => window.innerWidth")
        Expect.isTrue (sprintf "the layout viewport is the phone's screen (%d px), not a 980 px desktop page (got %d)" phoneWidth layoutWidth) (layoutWidth <= phoneWidth)
        let! narrow = page.EvaluateAsync<bool>("() => matchMedia('(max-width: 768px)').matches")
        Expect.isTrue "the 768 px layout applies" narrow
        let! phone = page.EvaluateAsync<bool>("() => matchMedia('(max-width: 480px)').matches")
        Expect.isTrue "the 480 px layout applies" phone
      })
    }
    |> Integration.register (Integration.Dedicated "--integration-browser")

    testTask "[Integration] Mobile: nothing in the page reaches past the screen's right edge" {
      do! onAPhone (fun page -> task {
        do! screenshot page "overflow"
        let! pageOverflow = page.EvaluateAsync<float>("() => document.documentElement.scrollWidth - window.innerWidth")
        Expect.isTrue (sprintf "the page itself does not scroll sideways (%f px too wide)" pageOverflow) (pageOverflow <= overflowTolerance)
        let! boxes = overflowingBoxes page
        Expect.equal (sprintf "no box past the right edge: %s" boxes) "" boxes
      })
    }
    |> Integration.register (Integration.Dedicated "--integration-browser")

    testTask "[Integration] Mobile: the header's cells keep their text on one line inside the bar, not wrapped out of it" {
      do! onAPhone (fun page -> task {
        // The bar is one row high (--tabline-h). A cell whose text wraps to a second line spills out of the bar and is
        // clipped, so "0 evals" read as a cut-off "0" over "evals".
        let! clipped =
          page.EvaluateAsync<string>(
            """() => {
              var bar = document.querySelector('.app-header');
              return Array.from(bar.querySelectorAll('*'))
                .filter(el => el.scrollHeight > el.clientHeight + 1 && el.clientHeight > 0 && getComputedStyle(el).overflowY !== 'visible')
                .map(el => el.tagName.toLowerCase() + '.' + String(el.className).split(' ')[0] + ' "' + el.innerText.replace(/\s+/g, ' ').slice(0, 24) + '" ' + el.scrollHeight + ' tall in ' + el.clientHeight)
                .join(' | ');
            }""")
        Expect.equal (sprintf "no header cell is taller than the bar it sits in: %s" clipped) "" clipped
      })
    }
    |> Integration.register (Integration.Dedicated "--integration-browser")

    testTask "[Integration] Mobile: session cards stack without overlapping each other, and none is wider than its column" {
      do! onAPhone (fun page -> task {
        let! report =
          page.EvaluateAsync<string>(
            """() => {
              var cards = Array.from(document.querySelectorAll('.session-row')).map(c => c.getBoundingClientRect());
              var problems = [];
              if (cards.length < 2) problems.push('fewer than two cards rendered: ' + cards.length);
              for (var i = 1; i < cards.length; i++) {
                if (cards[i].top < cards[i - 1].bottom - 1) problems.push('card ' + i + ' starts at ' + Math.round(cards[i].top) + ' before card ' + (i - 1) + ' ends at ' + Math.round(cards[i - 1].bottom));
              }
              Array.from(document.querySelectorAll('.session-row')).forEach((c, i) => {
                if (c.scrollWidth > c.clientWidth + 1) problems.push('card ' + i + ' content is ' + c.scrollWidth + ' wide in a ' + c.clientWidth + ' px card');
              });
              return problems.join(' | ');
            }""")
        do! screenshot page "cards"
        Expect.equal (sprintf "cards do not overlap or spill: %s" report) "" report
      })
    }
    |> Integration.register (Integration.Dedicated "--integration-browser")
  ]
