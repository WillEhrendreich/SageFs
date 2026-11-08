/// The "Since green" tab in a real browser with the real shell, stylesheet and Datastar bundle, served from memory
/// (docs/plans/live-bindings-watch-view.md, step 11). Three pages, each at 1400, 900 and 400 wide:
///
///   1. the tests settled green once and two bindings moved since (one a field of a record) and a third is gone: opening the tab lists the
///      two that moved and hides the one that did not, each moved row says what it was ("was 10"), the tab counts the three bindings that
///      moved, and the one that is no longer bound is named with its old value
///   2. the tests settled green and nothing has moved since: the tab says so
///   3. the tests have not settled green yet: the tab says there is nothing to compare with yet
///
/// Every page is photographed (`since-green-<case>-<width>`), and every page has zero console errors.
module SageFs.Tests.LiveBindingsSinceGreenBrowserTests

open System
open System.IO
open System.Threading.Tasks
open Expecto
open Expecto.Flip
open Falco.Markup
open Microsoft.Playwright
open SageFs
open SageFs.Features.LiveBindingsPane
open SageFs.Features.LiveValueTree
open SageFs.Features.Tweak.SinceGreen
open SageFs.Server
open SageFs.Server.DashboardTypes
open SageFs.Server.DashboardFragments
open SageFs.Tests.DashboardBrowserTests

module Integration = SageFs.Tests.TestInfrastructure.Integration

let private origin = "http://sagefs.test"

let private viewedId = "0a2b3c4d"

/// The walk as it is now: score is 11 and the player's Health 90, where the last green settle saw 10 and 100.
let private walked : LiveBindingsDock.BindingsSource =
  let leaf (label: string) (preview: string) : LiveValueNode =
    { Label = label; TypeName = "int"; Preview = preview; Kind = NodeKind.Leaf; Children = []; BestEffort = false; Assign = Assignment.NotKnown; Depth = 0 }
  let player : LiveValueNode =
    { leaf "player" "{ ... }" with Kind = NodeKind.Record; Children = [ leaf "Health" "90"; leaf "Speed" "4" ] }
  let binding (root: LiveValueNode) : LiveBindingValue = { Name = root.Label; TypeSignature = root.TypeName; Root = root }
  let view : PaneView =
    { Snapshot =
        { SessionId = viewedId
          Generation = 1L
          Bindings = [ binding (leaf "score" "11"); binding (leaf "lives" "3"); binding player ]
          Truncated = false
          CapturedAt = DateTimeOffset.UnixEpoch }
      Notes = { Mode = ValueWalk.standard; Click = NoClickYet } }
  LiveBindingsDock.WalkedBindings view

let private moved : Map<string, Moved> =
  Map.ofList
    [ pathKey "score" [], Moved.Differs("10", "11")
      pathKey "player" [ "Health" ], Moved.Differs("100", "90")
      pathKey "gone" [], Moved.Left "7" ]

let private shellHtml (said: LiveBindingsViewPanel.GreenSaid) : string =
  let empty = Elem.div [] []
  let look = SageFs.View.recommended SageFs.ViewWorkflow.Repl
  let controls = { LiveBindingsViewPanel.render viewedId SageFs.ViewWorkflow.Repl { View = look; Sources = Map.empty } with SinceGreen = said }
  let snap : DashboardSnapshot =
    { DashboardSnapshot.Version = "0.0.0"
      InView = SessionInView.InPlay(SessionState.Ready, SessionDisplayStatus.Running, Readiness.ReadyOlderCode)
      SessionId = viewedId; WorkingDir = "/work"
      WarmupProgress = ""; WorkflowLabel = "Live testing"; EvalStats = FixtureStats.noEvals
      ThemeName = "default"; ConnectionLabel = None
      HotReloadPanel = empty; SessionContextPanel = empty
      OutputPanel = renderOutputForSession viewedId 1 [] "no output"
      SessionsPanel = renderSessionsForSession viewedId [] false
      SessionPicker = empty; ThemePicker = empty; ThemeVars = empty
      BindingsPanel =
        LiveBindingsDock.renderDockWith look (Some controls) SageFs.Features.Tweak.BindingTweak.TweakView.none DockPanes.SessionInView viewedId walked None
      DaemonHealth = empty; FailureNarrativesPanel = empty; DiagnosticsPanel = empty
      FilmstripPanel = empty; AlarmPanel = empty; LiveTestingPanel = empty; FrictionPanel = empty
      CohortPanel = empty; HygienePanel = empty
      ActiveProject = None; ProjectRoles = []
      App = AppRun.AppRunState.NotRunning
      EvalToPixelP50Ms = None; EvalToPixelP99Ms = None }
  Dashboard.renderShell "0.0.0" "client-1" viewedId "/work" (renderMainContent snap) |> renderNode

let private serve (html: string) (page: IPage) = task {
  do! page.RouteAsync("**/*", Func<IRoute, Task>(fun route ->
    task {
      let fulfil (contentType: string) (body: string) =
        route.FulfillAsync(RouteFulfillOptions(Status = 200, ContentType = contentType, Body = body))
      match Uri(route.Request.Url).AbsolutePath with
      | "/dashboard" -> do! fulfil "text/html; charset=utf-8" html
      | "/dashboard/dashboard.css" -> do! fulfil "text/css; charset=utf-8" Dashboard.dashboardCss
      | "/dashboard/datastar.js" -> do! fulfil "application/javascript; charset=utf-8" Dashboard.datastarBundle
      | path when path.StartsWith "/dashboard/stream/" -> do! fulfil "text/event-stream" ""
      | _ -> do! route.FulfillAsync(RouteFulfillOptions(Status = 404, Body = ""))
    } :> Task))
}

/// What the page shows once the tab is open: which binding rows are visible, what each says it was, the tab's count and its words.
let private tabReport : string =
  """() => {
    // A record's fields are inside a details that is closed until a person opens it; open them, so their rows can be read.
    document.querySelectorAll('details.live-node').forEach(d => { d.open = true; });
    const visible = el => !!el && el.checkVisibility();
    const rows = [...document.querySelectorAll('.live-binding-node')].map(row => {
      const name = row.querySelector(':scope > .live-name')?.textContent ?? '';
      const was = row.querySelector('[data-testid=live-was]');
      return { name, visible: visible(row), was: was ? was.textContent.trim() : '' };
    });
    const empty = document.querySelector('[data-testid=live-tab-empty-since-green]');
    const left = document.querySelector('[data-testid=live-since-green-left]');
    const tree = document.querySelector('#live-tree').getBoundingClientRect();
    const outside = rows.length && [...document.querySelectorAll('.live-binding-node')].some(r => r.checkVisibility() && (r.getBoundingClientRect().right > tree.right + 1));
    return JSON.stringify({
      rows,
      pressed: document.querySelector('[data-testid=live-tab-since-green]').getAttribute('aria-pressed'),
      allPressed: document.querySelector('[data-testid=live-tab-all]').getAttribute('aria-pressed'),
      count: document.querySelector('[data-testid=live-tab-count-since-green]')?.innerText.trim() ?? '(no count)',
      empty: visible(empty) ? empty.innerText.trim() : '',
      left: visible(left) ? left.innerText.trim() : '',
      outside,
      pageScrollsSideways: document.scrollingElement.scrollWidth > window.innerWidth + 1
    });
  }"""

let private journey () : Task<unit> = task {
  let! playwright = Playwright.CreateAsync()
  let! browser = playwright.Chromium.LaunchAsync(BrowserTypeLaunchOptions(Headless = true))
  let mutable failure: exn option = None
  try
    let cases : (string * LiveBindingsViewPanel.GreenSaid) list =
      [ "moved", LiveBindingsViewPanel.GreenSaid.Moved moved
        "nothing-moved", LiveBindingsViewPanel.GreenSaid.Moved Map.empty
        "no-green-yet", LiveBindingsViewPanel.GreenSaid.NoGreenSettleYet ]
    for (name, said) in cases do
      for width in [ 1400; 900; 400 ] do
        let! context = browser.NewContextAsync(BrowserNewContextOptions(ViewportSize = ViewportSize(Width = width, Height = 900)))
        let! page = context.NewPageAsync()
        let errors = Collections.Generic.List<string>()
        // The in-memory server answers 404 for the favicon and the files the shell names that this page does not need; those are not the pane's.
        page.Console.Add(fun message -> if message.Type = "error" && not (message.Text.StartsWith "Failed to load resource") then errors.Add message.Text)
        page.PageError.Add(fun error -> errors.Add error)
        do! serve (shellHtml said) page
        let! _ = page.GotoAsync(sprintf "%s/dashboard" origin)
        let! _ = page.WaitForSelectorAsync("[data-testid=live-tab-since-green]", PageWaitForSelectorOptions(Timeout = float32 BrowserWaits.pageRenders))
        do! page.Locator("[data-testid=live-tab-since-green]").ClickAsync()
        let! _ = page.WaitForFunctionAsync("() => [...document.querySelectorAll('.live-binding-node')].some(row => !row.checkVisibility())", null, PageWaitForFunctionOptions(Timeout = float32 BrowserWaits.pageRenders))
        let! json = page.EvaluateAsync<string>(tabReport)
        let report = Text.Json.JsonDocument.Parse(json).RootElement
        let what = sprintf "%s at %dpx" name width
        let rows =
          [ for row in report.GetProperty("rows").EnumerateArray() ->
              row.GetProperty("name").GetString(), row.GetProperty("visible").GetBoolean(), row.GetProperty("was").GetString() ]
        let visibleNames = rows |> List.filter (fun (_, v, _) -> v) |> List.map (fun (n, _, _) -> n)
        let rowNamed (wanted: string) =
          match rows |> List.tryFind (fun (n, _, _) -> n = wanted) with
          | Some row -> row
          | None -> Tests.failtestf "no row named %s (%s); the rows are: %s" wanted what (rows |> List.map (fun (n, v, w) -> sprintf "%s visible=%b was='%s'" n v w) |> String.concat ", ")
        report.GetProperty("pressed").GetString() |> Expect.equal (sprintf "the Since green tab is the pressed one (%s)" what) "true"
        report.GetProperty("allPressed").GetString() |> Expect.equal (sprintf "and All is not (%s)" what) "false"
        report.GetProperty("outside").GetBoolean() |> Expect.isFalse (sprintf "no row sticks out of the tree (%s)" what)
        report.GetProperty("pageScrollsSideways").GetBoolean() |> Expect.isFalse (sprintf "the page does not scroll sideways (%s)" what)
        match name with
        | "moved" ->
          rowNamed "score" |> fun (_, v, was) ->
            v |> Expect.isTrue (sprintf "score moved, so it is listed (%s)" what)
            was |> Expect.equal (sprintf "score says what it was (%s)" what) "was 10"
          rowNamed "lives" |> fun (_, v, _) -> v |> Expect.isFalse (sprintf "lives did not move, so it is not listed (%s)" what)
          rowNamed "Health" |> fun (_, v, was) ->
            v |> Expect.isTrue (sprintf "a field of a record that moved is listed (%s)" what)
            was |> Expect.equal (sprintf "the field says what it was (%s)" what) "was 100"
          visibleNames |> List.contains "player" |> Expect.isTrue (sprintf "the record holding it is listed (%s)" what)
          report.GetProperty("count").GetString() |> Expect.equal (sprintf "the tab counts the three bindings that moved (%s)" what) "3"
          report.GetProperty("left").GetString() |> Expect.stringContains (sprintf "the binding that is gone is named with what it was (%s)" what) "gone"
          report.GetProperty("left").GetString() |> Expect.stringContains (sprintf "its old value (%s)" what) "7"
        | "nothing-moved" ->
          visibleNames |> Expect.isEmpty (sprintf "nothing moved, so no row is listed (%s)" what)
          report.GetProperty("empty").GetString() |> Expect.stringContains (sprintf "the tab says nothing has moved (%s)" what) "Nothing has moved"
        | _ ->
          visibleNames |> Expect.isEmpty (sprintf "no comparison point, so no row is listed (%s)" what)
          report.GetProperty("empty").GetString() |> Expect.stringContains (sprintf "the tab says there is nothing to compare with (%s)" what) "not settled green"
        List.ofSeq errors |> Expect.isEmpty (sprintf "zero console errors (%s): %s" what (String.concat " | " errors))
        let path = Path.Combine(PlaywrightFixture.screenshotDir, sprintf "since-green-%s-%d.png" name width)
        do! PlaywrightFixture.takeScreenshot page false path
        try do! context.CloseAsync() with _ -> ()
  with ex -> failure <- Some ex
  try do! browser.CloseAsync() with _ -> ()
  playwright.Dispose()
  match failure with
  | Some ex -> System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex).Throw()
  | None -> ()
}

[<Tests>]
let tests =
  testSequenced <|
  testList "Live bindings since green browser tests" [

    testTask "[Integration] Live bindings since green browser: the tab lists what moved after the tests last settled green, each with what it was, and says so when nothing did or nothing is known" {
      do! journey () }
    |> Integration.register (Integration.Dedicated "--integration-browser")
  ]
