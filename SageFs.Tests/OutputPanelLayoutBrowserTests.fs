/// How the output panel holds its place when the boxes around it change, in a real browser and with no daemon. The page is
/// the real shell and the real main content, with the real stylesheet and the real Datastar bundle, served from memory;
/// the stream the daemon would push is left silent, so the only thing that moves is what a journey moves. That is what
/// makes these cases fast and the same every run: a layout change is applied the way the daemon's eval answer applies it,
/// at a moment the case chooses, never at a moment the machine's load chooses.
///
/// The gate once saw the panel 333 px short of the bottom for ten seconds after a long eval. The panel had been squeezed
/// down to a few pixels by the eval's own result block, and a panel that follows the output did not follow its own box.
module SageFs.Tests.OutputPanelLayoutBrowserTests

open System
open System.Threading.Tasks
open Expecto
open Falco.Markup
open Microsoft.Playwright
open SageFs
open SageFs.Server
open SageFs.Server.DashboardTypes
open SageFs.Server.DashboardFragments
open SageFs.Tests.DashboardBrowserTests

module Integration = SageFs.Tests.TestInfrastructure.Integration

/// The window a laptop gives the dashboard. Fixed, so a case means the same on every machine.
let private viewportWidth = 1280
let private viewportHeight = 720

/// More output than the panel can show, so there is room to follow.
let private outputLineCount = 200

/// The lines an eval of a 120-line string answers with. The gate's journey fills the panel the same way.
let private resultLineCount = 120

/// The least of the window the output panel keeps when the Evaluate box is open and shows a long result. A quarter is
/// enough to read several lines, and it is far above what an unbounded result block leaves (a few pixels).
let private minimumOutputShare = 0.25

let private origin = "http://sagefs.test"

let private outputLines : OutputLine list =
  [ for i in 1 .. outputLineCount -> { Timestamp = None; Kind = ResultLine; Text = sprintf "line-%03d" i } ]

let private longResultText : string =
  [ for i in 1 .. resultLineCount -> sprintf "result-%03d" i ] |> String.concat "\n"

let private snapshot (output: XmlNode) : DashboardSnapshot =
  let empty = Elem.div [] []
  { DashboardSnapshot.Version = "0.0.0"
    SessionState = "ready"; SessionId = "s1"; WorkingDir = "/work"
    WarmupProgress = ""; WorkflowLabel = "REPL"; EvalStats = FixtureStats.noEvals
    ThemeName = "default"; ConnectionLabel = None; ConnectionState = DashboardConnectionState.Connected
    HotReloadPanel = empty; SessionContextPanel = empty
    OutputPanel = output
    SessionsPanel = empty; SessionPicker = empty
    ThemePicker = empty; ThemeVars = empty
    BindingsPanel = empty; DaemonHealth = empty; FailureNarrativesPanel = empty; DiagnosticsPanel = empty
    FilmstripPanel = empty; AlarmPanel = empty; LiveTestingPanel = empty; FrictionPanel = empty
    CohortPanel = empty; HygienePanel = empty
    ActiveProject = None; ProjectRoles = []
    App = SageFs.AppRun.AppRunState.NotRunning
    EvalToPixelP50Ms = None; EvalToPixelP99Ms = None }

let private shellHtml : string =
  let panel = renderOutputForSession "s1" 1 outputLines "no output"
  Dashboard.renderShell "0.0.0" "client-1" "s1" "/work" (renderMainContent (snapshot panel)) |> renderNode

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

/// A page on the in-memory dashboard, once the panel has followed the output to the bottom.
let private openDashboard (body: IPage -> Task<unit>) = task {
  let! page = PlaywrightFixture.newPage ()
  try
    do! page.SetViewportSizeAsync(viewportWidth, viewportHeight)
    do! serve page
    let! _ = page.GotoAsync(sprintf "%s/dashboard" origin)
    let! distance = OutputScroll.waitForAtBottom BrowserWaits.pageProbe page
    Expect.isTrue (distance <= OutputScroll.atBottomTolerance) (sprintf "the first render follows to the bottom (%f px from bottom)" distance)
    do! body page
  finally
    PlaywrightFixture.closePage(page).GetAwaiter().GetResult()
}

/// Put an eval's answer where the daemon's eval POST puts it: replace the page's result slot with the fragment.
let private showResult (page: IPage) (text: string) = task {
  let fragment = evalResultShown "output-line output-result" text |> renderNode
  let! _ = page.EvaluateAsync("html => { document.querySelector('#eval-result').outerHTML = html; }", fragment)
  ()
}

let private panelHeight (page: IPage) =
  page.EvaluateAsync<float>("() => document.querySelector('#output-panel').clientHeight")

[<Tests>]
let tests =
  testSequenced <|
  testList "Output panel layout browser tests" [

    testTask "[Integration] Output panel layout: a long eval result under the Evaluate box cannot squeeze the output panel out of the page" {
      do! openDashboard (fun page -> task {
        do! OutputScroll.setEvaluateOpen page true
        do! showResult page longResultText
        let! height = panelHeight page
        let least = minimumOutputShare * float viewportHeight
        Expect.isTrue (height >= least) (sprintf "the output panel keeps at least %f px of a %d px window, got %f" least viewportHeight height)
      })
    }
    |> Integration.register (Integration.Dedicated "--integration-browser")

    testTask "[Integration] Output panel layout: a panel that follows the output stays at the bottom when the Evaluate box opens under it" {
      do! openDashboard (fun page -> task {
        do! OutputScroll.setEvaluateOpen page true
        let! distance = OutputScroll.waitForAtBottom BrowserWaits.pageProbe page
        Expect.isTrue (distance <= OutputScroll.atBottomTolerance) (sprintf "opening Evaluate took height from the panel and it must still be at the bottom (%f px from bottom)" distance)
      })
    }
    |> Integration.register (Integration.Dedicated "--integration-browser")

    testTask "[Integration] Output panel layout: a panel that follows the output stays at the bottom when an eval's result appears under it" {
      do! openDashboard (fun page -> task {
        do! OutputScroll.setEvaluateOpen page true
        let! _ = OutputScroll.waitForAtBottom BrowserWaits.pageProbe page
        do! showResult page longResultText
        let! distance = OutputScroll.waitForAtBottom BrowserWaits.pageProbe page
        Expect.isTrue (distance <= OutputScroll.atBottomTolerance) (sprintf "the result block took height from the panel and it must still be at the bottom (%f px from bottom)" distance)
      })
    }
    |> Integration.register (Integration.Dedicated "--integration-browser")
  ]
