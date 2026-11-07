/// How current the pane's values are, said in the pane's bar in a real browser with the real shell, stylesheet and Datastar bundle, served
/// from memory (docs/plans/live-bindings-watch-view.md, step 10). One page per freshness the test loop can give (updating, current, held
/// back by compile errors, rebuild failed) and one with no chip, each at 1400, 900 and 400 wide:
///
///   1. the chip says its state in a word, and its dot takes the semantic colour of that state (pass, info, warn, fail) and no other
///   2. red is for the failed rebuild only
///   3. the chip is not cut off and sticks out of neither the bar nor the page
///   4. with no loop there is no chip at all
///
/// Every page is photographed (tweak-style names `freshness-<word>-<width>`), and every page has zero console errors.
module SageFs.Tests.LiveBindingsFreshnessBrowserTests

open System
open System.IO
open System.Threading.Tasks
open Expecto
open Expecto.Flip
open Falco.Markup
open Microsoft.Playwright
open SageFs
open SageFs.Features.LiveBindingsPane
open SageFs.Features.LiveTestActivity
open SageFs.Features.LiveValueTree
open SageFs.Features.Tweak.LoopFreshness
open SageFs.Server
open SageFs.Server.DashboardTypes
open SageFs.Server.DashboardFragments
open SageFs.Tests.DashboardBrowserTests

module Integration = SageFs.Tests.TestInfrastructure.Integration

let private origin = "http://sagefs.test"

let private viewedId = "0a2b3c4d"

let private walked : LiveBindingsDock.BindingsSource =
  let leaf (label: string) (preview: string) : LiveValueNode =
    { Label = label; TypeName = "int"; Preview = preview; Kind = NodeKind.Leaf; Children = []; BestEffort = false; Assign = Assignment.NotKnown; Depth = 0 }
  let binding (label: string) (preview: string) : LiveBindingValue = { Name = label; TypeSignature = "int"; Root = leaf label preview }
  let view : PaneView =
    { Snapshot =
        { SessionId = viewedId
          Generation = 1L
          Bindings = [ binding "score" "10"; binding "lives" "3" ]
          Truncated = false
          CapturedAt = DateTimeOffset.UnixEpoch }
      Notes = { Mode = ValueWalk.standard; Click = NoClickYet } }
  LiveBindingsDock.WalkedBindings view

/// The page for one chip (or none), the dashboard's own shell around the dock the dashboard draws.
let private shellHtml (chip: Chip option) : string =
  let empty = Elem.div [] []
  let look = SageFs.View.recommended SageFs.ViewWorkflow.Repl
  let controls = { LiveBindingsViewPanel.render viewedId SageFs.ViewWorkflow.Repl { View = look; Sources = Map.empty } with Freshness = (match chip with | Some c -> LiveBindingsViewPanel.LoopSaid.Said c | None -> LiveBindingsViewPanel.LoopSaid.NothingSaid) }
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

/// What the chip looks like on the page, and every way it could be wrong.
let private chipReport (expectedToken: string) : string =
  sprintf
    """() => {
      const chip = document.querySelector('[data-testid=live-freshness]');
      if (!chip) return JSON.stringify({ present: false });
      const dot = chip.querySelector('.live-freshness-dot');
      const bar = document.querySelector('.live-pane-bar').getBoundingClientRect();
      const c = chip.getBoundingClientRect();
      const probe = token => { const p = document.createElement('span'); p.style.backgroundColor = 'var(' + token + ')'; document.body.appendChild(p); const v = getComputedStyle(p).backgroundColor; p.remove(); return v; };
      return JSON.stringify({
        present: true,
        text: chip.innerText.trim(),
        dot: dot ? getComputedStyle(dot).backgroundColor : '(no dot)',
        expected: probe('%s'),
        fail: probe('--color-fail'),
        insideBar: c.left >= bar.left - 1 && c.right <= bar.right + 1,
        insidePage: c.left >= -1 && c.right <= window.innerWidth + 1,
        cutOff: chip.scrollWidth > chip.clientWidth + 1,
        pageScrollsSideways: document.scrollingElement.scrollWidth > window.innerWidth + 1,
        title: chip.getAttribute('title') || ''
      });
    }"""
    expectedToken

/// The semantic token each tone takes. A closed match, so a tone added to the model has to say here what colour it must be.
let private tokenOf (tone: FreshnessTone) : string =
  match tone with
  | FreshnessTone.Current -> "--color-pass"
  | FreshnessTone.Moving -> "--color-info"
  | FreshnessTone.Held -> "--color-warn"
  | FreshnessTone.Failed -> "--color-fail"

let private journey () : Task<unit> = task {
  let! playwright = Playwright.CreateAsync()
  let! browser = playwright.Chromium.LaunchAsync(BrowserTypeLaunchOptions(Headless = true))
  let mutable failure: exn option = None
  try
    let cases : (string * Chip option) list =
      [ yield "none", None
        for freshness in
          [ Freshness.Updating
            Freshness.Current
            Freshness.HeldByErrors("Game.fs", 2)
            Freshness.HeldByRebuild "build failed" ] do
          yield (word freshness).Replace(' ', '-'), Some { Word = word freshness; Detail = detail freshness; Tone = tone freshness } ]
    for (name, chip) in cases do
      for width in [ 1400; 900; 400 ] do
        let! context = browser.NewContextAsync(BrowserNewContextOptions(ViewportSize = ViewportSize(Width = width, Height = 900)))
        let! page = context.NewPageAsync()
        let errors = Collections.Generic.List<string>()
        // The in-memory server answers 404 for the favicon and the files the shell names that this page does not need; those are not the pane's.
        page.Console.Add(fun message -> if message.Type = "error" && not (message.Text.StartsWith "Failed to load resource") then errors.Add message.Text)
        page.PageError.Add(fun error -> errors.Add error)
        do! serve (shellHtml chip) page
        let! _ = page.GotoAsync(sprintf "%s/dashboard" origin)
        let! _ = page.WaitForSelectorAsync(".live-pane-bar", PageWaitForSelectorOptions(Timeout = float32 BrowserWaits.pageRenders))
        let what = sprintf "%s at %dpx" name width
        match chip with
        | None ->
          let! count = page.Locator("[data-testid=live-freshness]").CountAsync()
          count |> Expect.equal (sprintf "no loop, no chip (%s)" what) 0
        | Some chip ->
          let! json = page.EvaluateAsync<string>(chipReport (tokenOf chip.Tone))
          let report = Text.Json.JsonDocument.Parse(json).RootElement
          let flag (field: string) = report.GetProperty(field).GetBoolean()
          let text (field: string) = report.GetProperty(field).GetString()
          flag "present" |> Expect.isTrue (sprintf "the chip is on the page (%s)" what)
          text "text" |> Expect.equal (sprintf "the chip says its state in a word (%s)" what) chip.Word
          text "dot" |> Expect.equal (sprintf "the dot takes the colour of its tone (%s)" what) (text "expected")
          match chip.Tone with
          | FreshnessTone.Failed -> ()
          | FreshnessTone.Current
          | FreshnessTone.Moving
          | FreshnessTone.Held -> (text "dot" <> text "fail") |> Expect.isTrue (sprintf "red is for a failure only (%s)" what)
          flag "insideBar" |> Expect.isTrue (sprintf "the chip is inside the bar (%s)" what)
          flag "insidePage" |> Expect.isTrue (sprintf "the chip is inside the page (%s)" what)
          flag "cutOff" |> Expect.isFalse (sprintf "the chip's word is not cut off (%s)" what)
          flag "pageScrollsSideways" |> Expect.isFalse (sprintf "the page does not scroll sideways (%s)" what)
          text "title" |> Expect.equal (sprintf "the sentence is the tooltip (%s)" what) chip.Detail
        List.ofSeq errors |> Expect.isEmpty (sprintf "zero console errors (%s): %s" what (String.concat " | " errors))
        let path = Path.Combine(PlaywrightFixture.screenshotDir, sprintf "freshness-%s-%d.png" name width)
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
  testList "Live bindings freshness browser tests" [

    testTask "[Integration] Live bindings freshness browser: the pane's bar says in a word how current its values are, in the colour of that state, at three widths" {
      do! journey () }
    |> Integration.register (Integration.Dedicated "--integration-browser")
  ]
