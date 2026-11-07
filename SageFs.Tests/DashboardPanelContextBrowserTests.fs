/// The panels that follow the viewer's context, in a real headless browser with no daemon. The page is the real shell and main
/// content with the real stylesheet and Datastar bundle, served from memory (the `DashboardMobileBrowserTests` technique), once
/// per state of the facts and once per width. In every cell each panel is on the page, or its reason line is, or it is quiet and
/// leaves nothing behind; the page does not scroll sideways; and the console holds no error and no `PatchElementsNoTargetsFound`.
module SageFs.Tests.DashboardPanelContextBrowserTests

open System
open System.Collections.Generic
open System.Threading.Tasks
open Expecto
open Expecto.Flip
open Falco.Markup
open Microsoft.Playwright
open SageFs
open SageFs.Cohort
open SageFs.MemberTable
open SageFs.Server
open SageFs.Server.DashboardPanelContext
open SageFs.Server.DashboardTypes
open SageFs.Server.DashboardFragments
open SageFs.Tests.DashboardBrowserTests
open SageFs.Tests.DashboardPanelVisibilityTests

module Integration = SageFs.Tests.TestInfrastructure.Integration

let origin = "http://sagefs.test"

/// How far past the screen's edge the page may reach before it counts as scrolling sideways (sub-pixel rounding).
let overflowTolerance = 1.0

/// The widths the plan names, in CSS pixels: a phone, a tablet and a desktop.
let widths = [ 375; 768; 1400 ]
let heightFor (width: int) = match width with 375 -> 812 | 768 -> 1024 | _ -> 900

/// One state of the facts, with what the real panels would draw for it.
type Scenario = {
  Name: string
  Facts: PanelFacts
  Activity: Features.LiveTestActivity.LiveTestActivity
  Ledger: LedgerEntry<MemberId> list
}

let clean = DiagnosticsCheck.Checked 0

let blocked =
  Features.LiveTestActivity.LiveTestActivity.BlockedByCompileErrors("/src/Math.fs", 2, Features.LiveTestActivity.TestTally.empty)

let orphanedOnly =
  ledgerAfter
    [ CohortCommand.Join(alice, JoinableRole.Implementer, None, CohortScope.Machine)
      CohortCommand.AcquireClaim(alice, ClaimScope.File "src/Foo.fs", "editing", CohortScope.Machine)
      CohortCommand.Depart(alice, CohortScope.Machine) ]

let viewing workflow activity = ViewedSession.Viewing(workflow, activity)

let scenarios : Scenario list =
  [ { Name = "no session"
      Facts = { replFacts with Viewed = ViewedSession.NoSession; Diagnostics = DiagnosticsCheck.NotChecked }
      Activity = off; Ledger = [] }
    { Name = "REPL session, live testing off"
      Facts = { replFacts with Diagnostics = clean }; Activity = off; Ledger = [] }
    { Name = "Hot Reload session"
      Facts = { replFacts with Viewed = viewing hotReload off }; Activity = off; Ledger = [] }
    { Name = "Live Testing session, live testing off"
      Facts = { replFacts with Viewed = viewing WorkflowTypes.SessionWorkflow.LiveTesting off }; Activity = off; Ledger = [] }
    { Name = "Live Testing session, live testing on"
      Facts = { replFacts with Viewed = viewing WorkflowTypes.SessionWorkflow.LiveTesting running }; Activity = running; Ledger = [] }
    { Name = "Live Testing session, live testing blocked"
      Facts = { replFacts with Viewed = viewing WorkflowTypes.SessionWorkflow.LiveTesting blocked }; Activity = blocked; Ledger = [] }
    { Name = "cohort present"
      Facts = { replFacts with Cohort = PanelFacts.cohortPresence (frameOf memberHoldingAClaim) }
      Activity = off; Ledger = memberHoldingAClaim }
    { Name = "cohort holding only orphaned claims"
      Facts = { replFacts with Cohort = PanelFacts.cohortPresence (frameOf orphanedOnly) }
      Activity = off; Ledger = orphanedOnly }
    { Name = "friction opted in"
      Facts = { replFacts with Friction = FrictionPanelOptIn.OptedIn }; Activity = off; Ledger = [] }
    { Name = "diagnostics clean"
      Facts = { replFacts with Diagnostics = DiagnosticsCheck.Checked 0 }; Activity = off; Ledger = [] }
    { Name = "diagnostics failing, an alarm raised and a test failing"
      Facts = { replFacts with Diagnostics = DiagnosticsCheck.Checked 2; Alarms = 1; FailingTests = 1 }; Activity = off; Ledger = [] } ]

/// The snapshot a scenario would push, with the panels a count asks for drawn by their real renderers.
let snapshotFor (scenario: Scenario) : DashboardSnapshot =
  let raw = snapshotWith scenario.Activity scenario.Ledger
  let alarms =
    [ for i in 1 .. scenario.Facts.Alarms ->
        { Phase = "warmup"; Message = sprintf "Alarm %d: a component failed with a message long enough to wrap on a phone." i; Timestamp = DateTimeOffset.UtcNow } ]
  let diagnostics =
    match scenario.Facts.Diagnostics with
    | DiagnosticsCheck.Checked n ->
      [ for i in 1 .. n -> { Severity = DiagError; Message = sprintf "Type mismatch %d in a binding with a long name" i; Line = i; Col = 1 } ]
    | DiagnosticsCheck.NotChecked -> []
  let failing =
    match scenario.Facts.FailingTests with
    | 0 -> raw.FailureNarrativesPanel
    | n -> Elem.div [ Attr.id DomIds.FailureNarratives ] [ Text.raw (sprintf "%d failing tests" n) ]
  PanelVisibility.apply
    scenario.Facts
    { raw with
        AlarmPanel = renderAlarmBanner alarms
        DiagnosticsPanel = renderCurrentDiagnostics diagnostics
        FailureNarrativesPanel = failing }

let shellFor (scenario: Scenario) : string =
  Dashboard.renderShell "0.0.0" "client-1" "abcd1234" "/work" (renderMainContent (snapshotFor scenario)) |> renderNode

/// Serve a page from memory. The stream is answered with an empty event stream, so nothing is ever pushed. Every POST is
/// recorded and answered with an empty event stream, so a click that posts can be seen to have posted.
let serve (page: IPage) (html: string) (posts: List<string>) = task {
  do! page.RouteAsync("**/*", Func<IRoute, Task>(fun route ->
    task {
      let fulfil (contentType: string) (body: string) =
        route.FulfillAsync(RouteFulfillOptions(Status = 200, ContentType = contentType, Body = body))
      let path = Uri(route.Request.Url).AbsolutePath
      match route.Request.Method with
      | "POST" ->
        lock posts (fun () -> posts.Add path)
        do! fulfil "text/event-stream" ""
      | _ ->
        match path with
        | "/dashboard" -> do! fulfil "text/html; charset=utf-8" html
        | "/dashboard/dashboard.css" -> do! fulfil "text/css; charset=utf-8" Dashboard.dashboardCss
        | "/dashboard/datastar.js" -> do! fulfil "application/javascript; charset=utf-8" Dashboard.datastarBundle
        | path when path.StartsWith "/dashboard/stream/" -> do! fulfil "text/event-stream" ""
        | path when path.StartsWith "/dashboard/fonts/" && Dashboard.dashboardFonts.ContainsKey(path.Substring "/dashboard/fonts/".Length) ->
          let bytes = Dashboard.dashboardFonts.[path.Substring "/dashboard/fonts/".Length]
          do! route.FulfillAsync(RouteFulfillOptions(Status = 200, ContentType = "font/woff2", BodyBytes = bytes))
        | _ ->
          lock posts (fun () -> posts.Add(sprintf "unserved %s" path))
          do! route.FulfillAsync(RouteFulfillOptions(Status = 404, Body = ""))
    } :> Task))
}

/// A browser at `width`, on the page `html`, run through `body` with the console's errors and the requests posted. The
/// browser is closed by awaiting, and a failure is carried out of the block and rethrown after.
let inBrowser (width: int) (html: string) (body: IPage -> List<string> -> List<string> -> Task<unit>) = task {
  let! playwright = Playwright.CreateAsync()
  let! browser = playwright.Chromium.LaunchAsync(BrowserTypeLaunchOptions(Headless = true))
  let! context = browser.NewContextAsync(BrowserNewContextOptions(ViewportSize = ViewportSize(Width = width, Height = heightFor width)))
  let! page = context.NewPageAsync()
  let errors = List<string>()
  let posts = List<string>()
  page.Console.Add(fun message ->
    if message.Type = "error" || message.Text.Contains "PatchElementsNoTargetsFound" then
      lock errors (fun () -> errors.Add(sprintf "[%s] %s" message.Type message.Text)))
  page.PageError.Add(fun error -> lock errors (fun () -> errors.Add(sprintf "[pageerror] %s" error)))
  let! failure =
    task {
      try
        do! serve page html posts
        let! _ = page.GotoAsync(sprintf "%s/dashboard" origin)
        let! _ = page.WaitForSelectorAsync(".app-layout", PageWaitForSelectorOptions(Timeout = float32 BrowserWaits.pageRenders))
        do! body page errors posts
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

/// The id each panel carries when it is on the page.
let shownId (panel: ContextPanel) : string =
  match panel with
  | ContextPanel.HotReload -> DomIds.HotReloadPanel
  | ContextPanel.LiveTesting -> DomIds.LiveTestingPanel
  | ContextPanel.Cohort -> DomIds.CohortPanel
  | ContextPanel.Friction -> DomIds.FrictionPanel
  | ContextPanel.Diagnostics -> DomIds.DiagnosticsPanel
  | ContextPanel.FailureNarratives -> DomIds.FailureNarratives
  | ContextPanel.Alarms -> DomIds.AlarmBanner

let count (page: IPage) (id: string) : Task<int> =
  page.EvaluateAsync<int>(sprintf "() => document.querySelectorAll('#%s').length" id)

/// What is wrong in one cell, one sentence per fault; empty when the cell is right.
let faultsIn (scenario: Scenario) (page: IPage) (errors: List<string>) (requests: List<string>) : Task<string list> = task {
  let faults = List<string>()
  // A request the in-memory server could not answer is named, so a 404 in the console says which resource it was.
  lock requests (fun () ->
    for request in requests do
      if request.StartsWith "unserved " then faults.Add request)
  for panel, presence in PanelContext.decisions scenario.Facts do
    let! shown = count page (shownId panel)
    let! reason = count page (PanelVisibility.reasonId panel)
    match presence with
    | PanelPresence.Shown ->
      if shown <> 1 then faults.Add(sprintf "%A is shown, so #%s is on the page once (found %d)" panel (shownId panel) shown)
      if reason <> 0 then faults.Add(sprintf "%A is shown, so no reason line stands in for it" panel)
    | PanelPresence.Hidden _ ->
      if reason <> 1 then faults.Add(sprintf "%A is hidden, so its reason line #%s is on the page once (found %d)" panel (PanelVisibility.reasonId panel) reason)
      if shown <> 0 then faults.Add(sprintf "%A is hidden, so the panel itself is not on the page" panel)
      let! visible = page.Locator(sprintf "#%s > summary" (PanelVisibility.reasonId panel)).IsVisibleAsync()
      if not visible then faults.Add(sprintf "%A: the reason line's name is not visible" panel)
    | PanelPresence.Quiet _ ->
      if reason <> 0 then faults.Add(sprintf "%A is quiet, so it leaves no reason line" panel)
  let! overflow = page.EvaluateAsync<float>("() => document.documentElement.scrollWidth - window.innerWidth")
  if overflow > overflowTolerance then faults.Add(sprintf "the page scrolls sideways (%.1f px too wide)" overflow)
  let! boxes =
    page.EvaluateAsync<string>(
      sprintf
        """() => {
          var limit = window.innerWidth + %f;
          var scrolls = el => { for (var p = el.parentElement; p && p !== document.body; p = p.parentElement) { var o = getComputedStyle(p).overflowX; if (o === 'auto' || o === 'scroll') return true; } return false; };
          return Array.from(document.querySelectorAll('#main *'))
            .filter(el => el.getBoundingClientRect().width > 0 && el.getBoundingClientRect().right > limit && !scrolls(el))
            .slice(0, 6)
            .map(el => el.tagName.toLowerCase() + (el.id ? '#' + el.id : '') + ' right=' + Math.round(el.getBoundingClientRect().right))
            .join(' | ');
        }"""
        overflowTolerance)
  if boxes <> "" then faults.Add(sprintf "boxes past the right edge: %s" boxes)
  lock errors (fun () ->
    for error in errors do
      faults.Add(sprintf "console: %s" error))
  return List.ofSeq faults
}

[<Tests>]
let tests =
  testSequenced <|
  testList "Dashboard panel context browser tests" [

    testTask "[Integration] Panels follow context: every state of the facts at 375, 768 and 1400 px puts each panel or its reason on the page, without sideways scroll or console errors" {
      let report = List<string>()
      for scenario in scenarios do
        for width in widths do
          do! inBrowser width (shellFor scenario) (fun page errors requests -> task {
            let! faults = faultsIn scenario page errors requests
            for fault in faults do report.Add(sprintf "%s @ %d px: %s" scenario.Name width fault)
            match scenario.Name, width with
            | "REPL session, live testing off", _ | "cohort holding only orphaned claims", _ ->
              let path = IO.Path.Combine(PlaywrightFixture.screenshotDir, sprintf "panel-context-%s-%d.png" (scenario.Name.Replace(' ', '-').Replace(',', '-')) width)
              do! PlaywrightFixture.takeScreenshot page true path
            | _ -> ()
            return () })
      Expect.equal (sprintf "every cell is right:\n%s" (String.Join("\n", report))) 0 report.Count
    }
    |> Integration.register (Integration.Dedicated "--integration-browser")

    testTask "[Integration] Panels follow context: the Turn on button on the live testing line posts to the enable route" {
      let scenario = scenarios |> List.find (fun s -> s.Name = "REPL session, live testing off")
      do! inBrowser 1400 (shellFor scenario) (fun page errors posts -> task {
        let turnOn = page.Locator("#hidden-reason-live-testing").GetByRole(AriaRole.Button, LocatorGetByRoleOptions(Name = "Turn on"))
        do! PlaywrightExpect.isVisibleAsync turnOn "the Turn on button is visible without opening the line"
        let! request =
          page.RunAndWaitForRequestAsync(
            Func<Task>(fun () -> turnOn.ClickAsync()),
            Func<IRequest, bool>(fun r -> r.Method = "POST" && r.Url.EndsWith "/dashboard/live-testing/enable"))
        Expect.equal "the click posted to the enable route" "/dashboard/live-testing/enable" (Uri(request.Url).AbsolutePath)
        Expect.isEmpty "no console error" (List.ofSeq errors)
      })
    }
    |> Integration.register (Integration.Dedicated "--integration-browser")
  ]
