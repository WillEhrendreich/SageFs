/// The Sessions card's edges and its marker, in a real browser with the real shell, stylesheet and Datastar bundle, served from
/// memory. Reviewers saw the right edge cut "last: just now" and "2m", the working directory break in the middle of a word, and
/// at 400 and 600px the card folded into the "SESSIONS" strip with the warning showing and the fix nowhere to be found.
module SageFs.Tests.SessionCardEdgesBrowserTests

open System
open System.Threading.Tasks
open Expecto
open Expecto.Flip
open Falco.Markup
open Microsoft.Playwright
open SageFs
open SageFs.Features.LiveBindingsPane
open SageFs.Features.LiveValueTree
open SageFs.Server
open SageFs.Server.DashboardTypes
open SageFs.Server.DashboardFragments
open SageFs.Tests.DashboardBrowserTests

module Integration = SageFs.Tests.TestInfrastructure.Integration

let private origin = "http://sagefs.test"

let private viewedId = "0a2b3c4d"

let private longDirectory = "/home/will/Work/SageFs/SageFs.Tests/fixtures/TweakFixture/.runs/shots"

let private staleSource =
  SourceState.Stale
    [ { Path = longDirectory + "/Tuning.fs"
        Because = StaleBecause.EditedAfterBuild (DateTime(2026, 11, 5, 16, 5, 22, DateTimeKind.Utc), DateTime(2026, 11, 5, 16, 0, 12, DateTimeKind.Utc)) } ]

let private card (id: string) (source: CardSource) : ParsedSession =
  let moment = DateTime.UtcNow.AddMinutes -2.0
  { Id = WorkerProtocol.SessionId.validate id |> Result.defaultValue (WorkerProtocol.SessionId.newId ())
    Status = SessionDisplayStatus.Running; StatusMessage = None
    ProjectsText = "(TweakFixture.fsproj)"; EvalCount = 12
    Uptime = Since.ofUtc moment; WorkingDir = longDirectory; LastActivity = Since.ofUtc moment
    TestSummary = None; CoverageSummary = None; TestTreemapEntries = [||]; CoverageTreemap = None
    BindingEntries = [||]; AgentBadges = []; GuidanceCssClass = ""
    ActiveProject = None; ProjectRoles = []; App = AppRun.AppRunState.NotRunning
    WorkerRssBytes = None; SelfHostStaleness = None; Source = source
    Freshness = ReplFreshness.InSync; Health = SessionHealth.Healthy
    Teardown = SageFs.Server.TeardownRegistry.TeardownState.NotTearingDown }

/// One live binding, so the pane is open (a pane with nothing bound is a folded strip, and its bar is not on the page).
let private walked : LiveBindingsDock.BindingsSource =
  let leaf : LiveValueNode =
    { Label = "x"; TypeName = "int"; Preview = "1"; Kind = NodeKind.Leaf; Children = []; BestEffort = false; Depth = 0 }
  let binding : LiveBindingValue = { Name = "x"; TypeSignature = "int"; Root = leaf }
  let view : PaneView =
    { Snapshot = { SessionId = viewedId; Generation = 1L; Bindings = [ binding ]; Truncated = false; CapturedAt = DateTimeOffset.UnixEpoch }
      Notes = { Mode = ValueWalk.standard; Click = NoClickYet } }
  LiveBindingsDock.WalkedBindings view

let private shellHtml : Lazy<string> =
  lazy (
    let empty = Elem.div [] []
    let stale = RebuildRemedy.paneNote Remedy.RebuildAndReload
    let snap : DashboardSnapshot =
      { DashboardSnapshot.Version = "0.0.0"
        InView = SessionInView.InPlay(SessionState.Ready, SessionDisplayStatus.Running, Readiness.ReadyOlderCode)
        SessionId = viewedId; WorkingDir = longDirectory
        WarmupProgress = ""; WorkflowLabel = "REPL"; EvalStats = FixtureStats.noEvals
        ThemeName = "default"; ConnectionLabel = None
        HotReloadPanel = empty; SessionContextPanel = empty
        OutputPanel = renderOutputForSession viewedId 1 [] "no output"
        SessionsPanel = renderSessionsForSession viewedId [ card viewedId (CardSource.Read staleSource); card "0a2b3c4e" CardSource.NotReadOnThisCard ] false
        SessionPicker = empty; ThemePicker = empty; ThemeVars = empty
        BindingsPanel = LiveBindingsDock.renderDockWithRemedy stale (SageFs.View.recommended SageFs.ViewWorkflow.Repl) None SageFs.Features.Tweak.BindingTweak.TweakView.none DockPanes.SessionInView viewedId walked None
        DaemonHealth = empty; FailureNarrativesPanel = empty; DiagnosticsPanel = empty
        FilmstripPanel = empty; AlarmPanel = empty; LiveTestingPanel = empty; FrictionPanel = empty
        CohortPanel = empty; HygienePanel = empty
        ActiveProject = None; ProjectRoles = []
        App = AppRun.AppRunState.NotRunning
        EvalToPixelP50Ms = None; EvalToPixelP99Ms = None }
    Dashboard.renderShell "0.0.0" "client-1" viewedId "/work" (renderMainContent snap) |> renderNode)

let private serve (page: IPage) = task {
  do! page.RouteAsync("**/*", Func<IRoute, Task>(fun route ->
    task {
      let fulfil (contentType: string) (body: string) =
        route.FulfillAsync(RouteFulfillOptions(Status = 200, ContentType = contentType, Body = body))
      match Uri(route.Request.Url).AbsolutePath with
      | "/dashboard" -> do! fulfil "text/html; charset=utf-8" shellHtml.Value
      | "/dashboard/dashboard.css" -> do! fulfil "text/css; charset=utf-8" Dashboard.dashboardCss
      | "/dashboard/datastar.js" -> do! fulfil "application/javascript; charset=utf-8" Dashboard.datastarBundle
      | path when path.StartsWith "/dashboard/stream/" -> do! fulfil "text/event-stream" ""
      | _ -> do! route.FulfillAsync(RouteFulfillOptions(Status = 404, Body = ""))
    } :> Task))
}

/// The page at one width, with the clock text filled in, run through `body`.
let private atWidth (width: int) (body: IPage -> Task<unit>) = task {
  let! playwright = Playwright.CreateAsync()
  let! browser = playwright.Chromium.LaunchAsync(BrowserTypeLaunchOptions(Headless = true))
  let! context = browser.NewContextAsync(BrowserNewContextOptions(ViewportSize = ViewportSize(Width = width, Height = 900)))
  let! page = context.NewPageAsync()
  let! failure =
    task {
      try
        do! serve page
        let! _ = page.GotoAsync(sprintf "%s/dashboard" origin)
        let! _ = page.WaitForSelectorAsync("[data-testid=session-card]", PageWaitForSelectorOptions(Timeout = float32 BrowserWaits.pageRenders))
        // The card's clocks fill in from the browser's own time once the first tick lands.
        let! _ = page.WaitForFunctionAsync("() => /\\d+m|just now/.test(document.querySelector('[data-testid=session-card]').innerText)", null, PageWaitForFunctionOptions(Timeout = float32 BrowserWaits.pageRenders))
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

/// Every box inside the viewed card with text that the card cuts off or that spills out of it, named and measured.
let private edgeProblems (page: IPage) : Task<string> =
  page.EvaluateAsync<string>("""() => {
    const card = document.querySelector('[data-testid="session-card"]');
    const c = card.getBoundingClientRect();
    const problems = [];
    const name = el => el.tagName.toLowerCase() + '.' + String(el.className).split(' ')[0];
    for (const el of card.querySelectorAll('*')) {
      if (!(el.innerText || '').trim()) continue;
      const r = el.getBoundingClientRect();
      if (r.width <= 0) continue;
      // Text that touches the border reads as cut off: "2m" and "last: just now" sat on the card's right edge.
      if (r.right > c.right - 4 && r.right <= c.right + 0.5 && el.children.length === 0)
        problems.push(name(el) + ' touches the card\'s right edge: "' + el.innerText.trim().slice(0, 30) + '"');
      if (r.right > c.right + 0.5) problems.push(name(el) + ' spills ' + Math.round(r.right - c.right) + 'px past the card: "' + el.innerText.trim().slice(0, 30) + '"');
      const overflow = getComputedStyle(el).overflowX;
      if ((overflow === 'hidden' || overflow === 'clip') && el.scrollWidth > el.clientWidth + 1)
        problems.push(name(el) + ' cuts its own text (' + el.scrollWidth + ' in ' + el.clientWidth + '): "' + el.innerText.trim().slice(0, 30) + '"');
    }
    for (let p = card.parentElement; p; p = p.parentElement) {
      const o = getComputedStyle(p).overflowX;
      if (o === 'hidden' || o === 'clip' || o === 'auto' || o === 'scroll') {
        const pr = p.getBoundingClientRect();
        if (c.right > pr.right + 0.5) problems.push('the card is cut by ' + name(p) + ' by ' + Math.round(c.right - pr.right) + 'px');
      }
    }
    return problems.join(' | ');
  }""")

[<Tests>]
let tests =
  testSequenced <|
  testList "Sessions card edges and marker (real browser)" [

    testTask "[Integration] the viewed card's text is neither cut by its right edge nor spilled out of it, at 320 and 1400px" {
      for width in [ 320; 1400 ] do
        do! atWidth width (fun page -> task {
          let! problems = edgeProblems page
          problems |> Expect.equal (sprintf "at %dpx nothing in the card is cut" width) ""
        })
    }
    |> Integration.register (Integration.Dedicated "--integration-browser")

    testTask "[Integration] the card's working directory breaks only after a slash, at 320px" {
      do! atWidth 320 (fun page -> task {
        let! brokenWords =
          page.EvaluateAsync<string>("""() => {
            const el = document.querySelector('[data-testid="session-card"] .session-dir');
            const range = document.createRange();
            // Every text node of the path, split into the lines the browser drew: a line may end only at a slash.
            const lines = [];
            for (const node of el.childNodes) {
              if (node.nodeType !== Node.TEXT_NODE) continue;
              let start = 0, lastTop = null;
              for (let i = 0; i < node.length; i++) {
                range.setStart(node, i); range.setEnd(node, i + 1);
                const rects = range.getClientRects();
                if (!rects.length) continue;
                const top = Math.round(rects[0].top);
                if (lastTop !== null && top !== lastTop) lines.push(node.data.slice(start, i));
                if (lastTop === null || top !== lastTop) start = i;
                lastTop = top;
              }
            }
            return lines.filter(line => !line.endsWith('/') && !line.endsWith(' ')).join(' | ');
          }""")
        brokenWords |> Expect.equal "no line of the path ends inside a name" ""
      })
    }
    |> Integration.register (Integration.Dedicated "--integration-browser")

    testTask "[Integration] at 400 and 600px the strip shows the stale marker as a word, with the card folded away" {
      for width in [ 400; 600 ] do
        do! atWidth width (fun page -> task {
          // Fold the card away: the strip is all that is left of the Sessions panel.
          let! _ = page.EvaluateAsync("() => { if (document.querySelector('#sidebar-toggle-btn').innerText.trim() === '»') document.querySelector('#sidebar-toggle-btn').click() }")
          let marker = page.Locator("[data-testid=sessions-stale-marker]")
          let! visible = marker.IsVisibleAsync()
          visible |> Expect.isTrue (sprintf "at %dpx the marker is visible" width)
          let! text = marker.InnerTextAsync()
          text |> Expect.stringContains "and it is a word" "Changes not live"
          let! inside = page.EvaluateAsync<bool>("() => { const r = document.querySelector('[data-testid=sessions-stale-marker]').getBoundingClientRect(); return r.left >= 0 && r.right <= window.innerWidth + 0.5 }")
          inside |> Expect.isTrue (sprintf "at %dpx the marker is inside the screen" width)
        })
    }
    |> Integration.register (Integration.Dedicated "--integration-browser")

    testTask "[Integration] the live bindings pane's note and its button are visible and inside the screen at 400, 600 and 1400px" {
      for width in [ 400; 600; 1400 ] do
        do! atWidth width (fun page -> task {
          let button = page.Locator("[data-testid=live-pane-rebuild]")
          let! visible = button.IsVisibleAsync()
          // When it is not, say which ancestor hides it.
          let! chain =
            page.EvaluateAsync<string>("""() => {
              const out = [];
              for (let e = document.querySelector('[data-testid=live-pane-rebuild]'); e && e !== document.body; e = e.parentElement) {
                const s = getComputedStyle(e), r = e.getBoundingClientRect();
                out.push(e.tagName.toLowerCase() + '.' + String(e.className).split(' ')[0] + ' display=' + s.display + ' overflow=' + s.overflowX + ' ' + Math.round(r.width) + 'x' + Math.round(r.height) + '@' + Math.round(r.left));
              }
              return out.join(' < ');
            }""")
          visible |> Expect.isTrue (sprintf "at %dpx the pane's button is visible (%s)" width chain)
          let! inside = page.EvaluateAsync<bool>("() => { const r = document.querySelector('[data-testid=live-pane-rebuild]').getBoundingClientRect(); return r.left >= 0 && r.right <= window.innerWidth + 0.5 }")
          inside |> Expect.isTrue (sprintf "at %dpx the pane's button is inside the screen" width)
          // On one line, and clear of the filter box beside it: the first draw squeezed "Rebuild and reload" into a column over it.
          let! crowding =
            page.EvaluateAsync<string>("""() => {
              const b = document.querySelector('[data-testid=live-pane-rebuild]').getBoundingClientRect();
              const f = document.querySelector('.live-filter').getBoundingClientRect();
              const problems = [];
              if (b.height > 34) problems.push('the button is ' + Math.round(b.height) + 'px tall, so its words wrapped');
              const overlapX = Math.min(b.right, f.right) - Math.max(b.left, f.left);
              const overlapY = Math.min(b.bottom, f.bottom) - Math.max(b.top, f.top);
              if (overlapX > 1 && overlapY > 1) problems.push('the button overlaps the filter box');
              return problems.join(' | ');
            }""")
          crowding |> Expect.equal (sprintf "at %dpx the note's button is whole" width) ""
        })
    }
    |> Integration.register (Integration.Dedicated "--integration-browser")
  ]
