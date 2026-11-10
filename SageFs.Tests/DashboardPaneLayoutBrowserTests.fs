/// Moving, closing and reopening the dashboard's panels, in a real headless browser with no daemon. The page is the real shell,
/// the real main content with the real panels, the real stylesheet and the real Datastar bundle, served from memory (the
/// `DashboardPanelContextBrowserTests` technique). The one difference from a daemon is the route behind the Panels menu: it is
/// answered here by the same code the daemon runs (`BookEdit.parse`, then a real `PaneLayoutStore.Store` over a temporary
/// directory, then a morph of `renderMainContentIn`), so a click really moves a panel, the layout is really saved, and a reload
/// really draws it. Two journeys: a matrix of layouts at 375, 768 and 1400 px (every open panel on the page once, in its region,
/// reachable, nothing sideways, the menu inside the screen, zero console errors) and the moves themselves, by click and by key.
module SageFs.Tests.DashboardPaneLayoutBrowserTests

open System
open System.Collections.Generic
open System.IO
open System.Threading.Tasks
open Expecto
open Expecto.Flip
open Falco.Markup
open Microsoft.Playwright
open SageFs
open SageFs.Cohort
open SageFs.Server
open SageFs.Server.DashboardPanelContext
open SageFs.Server.DashboardTypes
open SageFs.Server.DashboardFragments
open SageFs.Server.PaneMenuView
open SageFs.Tests.DashboardBrowserTests
open SageFs.Tests.DashboardPanelVisibilityTests
open SageFs.Tests.DashboardPanelContextBrowserTests

module Integration = SageFs.Tests.TestInfrastructure.Integration

let widths = [ 375; 768; 1400 ]

/// Every movable panel has something to show: a hot reload session with live testing on, a cohort present and friction asked for.
let allPanelsShown : Scenario =
  { Name = "every panel shown"
    Facts =
      { replFacts with
          Viewed = ViewedSession.Viewing(hotReload, running)
          Cohort = PanelFacts.cohortPresence (frameOf memberHoldingAClaim)
          Friction = FrictionPanelOptIn.OptedIn
          Diagnostics = DiagnosticsCheck.Checked 0 }
    Activity = running
    Ledger = memberHoldingAClaim }

let workflow = LayoutWorkflow.HotReload

/// The snapshot with a session context and a hygiene panel that have some weight to them (long lines, a list), so a panel that
/// does not fit its region shows.
let snapshot : DashboardSnapshot =
  { snapshotFor allPanelsShown with
      // The cohort panel itself, whose root carries the panel's id (the shared fixture wraps it with the lanes in an id-less div).
      CohortPanel = renderCohortPanel (frameOf memberHoldingAClaim)
      SessionContextPanel = renderSessionContextEmpty EmptyBecause.NoSessionInView
      HygienePanel =
        Elem.div [ Attr.id DomIds.HygienePanel; Attr.class' "panel hygiene-panel" ] [
          Elem.p [] [ Text.enc "Workspace hygiene: 3 worktrees under /home/someone/Work/a-very-long-repository-name/.claude/worktrees/agent-abcdef0123456789 are merged and can be removed." ]
          Elem.ul [] [ for i in 1 .. 4 -> Elem.li [] [ Text.enc (sprintf "worktree %d: merged by ancestry, quiet for two days, no uncommitted work" i) ] ] ] }

let rootIdOf (pane: PanelId) : string =
  match pane with
  | PanelId.HotReload -> DomIds.HotReloadPanel
  | PanelId.LiveTesting -> DomIds.LiveTestingPanel
  | PanelId.SessionContext -> DomIds.SessionContext
  | PanelId.Friction -> DomIds.FrictionPanel
  | PanelId.AgentTeam -> DomIds.CohortPanel
  | PanelId.Hygiene -> DomIds.HygienePanel

/// The root ids a list of panes is drawn with, in order.
let expected (panes: PanelId list) : string list = panes |> List.map rootIdOf

/// The page, as the daemon would draw it for the layout the store holds.
type LiveServer(dir: string) =
  let store = PaneLayoutStore.Store dir
  member _.Store = store
  member _.Main() : XmlNode =
    let view = PaneLayoutView.ofRead workflow (store.Read()) allPanelsShown.Facts
    renderMainContentIn view snapshot
  member this.Page() : string = Dashboard.renderShell "0.0.0" "client-1" "abcd1234" "/work" (this.Main()) |> renderNode
  /// The whole `#main` as one datastar-patch-elements event, the way the stream sends it.
  member this.Patch() : string =
    let html = renderNode (this.Main())
    let lines = html.Split('\n') |> Array.map (fun line -> "data: elements " + line)
    "event: datastar-patch-elements\n" + String.Join("\n", lines) + "\n\n"

let serveLive (page: IPage) (server: LiveServer) (posts: List<string>) = task {
  do! page.RouteAsync("**/*", Func<IRoute, Task>(fun route ->
    task {
      let fulfil (contentType: string) (body: string) =
        route.FulfillAsync(RouteFulfillOptions(Status = 200, ContentType = contentType, Body = body))
      let uri = Uri route.Request.Url
      let path = uri.AbsolutePath
      match route.Request.Method with
      | "POST" ->
        lock posts (fun () -> posts.Add(path + uri.Query))
        match path with
        | "/dashboard/layout" ->
          let text = System.Web.HttpUtility.ParseQueryString(uri.Query).["e"] |> Option.ofObj |> Option.defaultValue ""
          match BookEdit.parse text with
          | Result.Ok edit ->
            match server.Store.Edit edit with
            | Result.Ok() -> do! fulfil "text/event-stream" (server.Patch())
            | Result.Error why -> failwithf "the store refused %s: %s" text (ConfigError.describe why)
          | Result.Error refusal -> failwithf "the menu posted something unreadable (%s): %s" text (LayoutRefusal.describe refusal)
        | _ -> do! fulfil "text/event-stream" ""
      | _ ->
        match path with
        | "/dashboard" -> do! fulfil "text/html; charset=utf-8" (server.Page())
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

/// A browser at `width` on a live server whose layout starts as `saved` (nothing saved when None), run through `body`. The
/// browser is closed by awaiting, a failure is carried out of the block and rethrown after, and the temporary directory is removed.
let inLiveBrowser (width: int) (saved: PaneLayout option) (body: IPage -> LiveServer -> List<string> -> List<string> -> Task<unit>) = task {
  let dir = Path.Combine(Path.GetTempPath(), "pane-browser-" + Guid.NewGuid().ToString("N"))
  Directory.CreateDirectory dir |> ignore
  match saved with
  | Some layout -> SettingsStore.setKey (SettingsStore.globalPath dir) (LayoutWorkflow.key workflow) (PaneLayout.render layout) |> Expect.isOk "seed the layout"
  | None -> ()
  let server = LiveServer dir
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
        do! serveLive page server posts
        let! _ = page.GotoAsync(sprintf "%s/dashboard" origin)
        let! _ = page.WaitForSelectorAsync(".app-layout", PageWaitForSelectorOptions(Timeout = float32 BrowserWaits.pageRenders))
        do! body page server errors posts
        return None
      with ex -> return Some ex
    }
  try do! context.CloseAsync() with _ -> ()
  try do! browser.CloseAsync() with _ -> ()
  playwright.Dispose()
  try Directory.Delete(dir, true) with _ -> ()
  match failure with
  | Some ex -> System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex).Throw()
  | None -> ()
}

let layoutOf (sidebar: PanelId list) (dock: PanelId list) (closed: PanelId list) : PaneLayout =
  { Sidebar = sidebar; Dock = dock; Closed = closed }

/// The layouts of the matrix: the default, everything in the dock, a split, and everything closed.
let layouts : (string * PaneLayout option) list =
  [ "default", None
    "everything in the dock", Some(layoutOf [] PanelId.all [])
    "split between sidebar and dock",
    Some(layoutOf [ PanelId.AgentTeam; PanelId.HotReload; PanelId.Hygiene ] [ PanelId.LiveTesting; PanelId.SessionContext; PanelId.Friction ] [])
    "everything closed", Some(layoutOf [] [] PanelId.all) ]

let sidebarIds (page: IPage) : Task<string list> = task {
  let! ids = page.EvaluateAsync<string>("() => Array.from(document.querySelectorAll('.sidebar-inner > *')).map(e => e.id).filter(id => id).join(',')")
  return ids.Split([| ',' |], StringSplitOptions.RemoveEmptyEntries) |> List.ofArray
}

let dockIds (page: IPage) : Task<string list> = task {
  let! ids = page.EvaluateAsync<string>("() => Array.from(document.querySelectorAll('#pane-dock > *')).map(e => e.id).filter(id => id).join(',')")
  return ids.Split([| ',' |], StringSplitOptions.RemoveEmptyEntries) |> List.ofArray
}

/// What is wrong in one cell, one sentence per fault; empty when the cell is right.
let faultsIn (layout: PaneLayout) (page: IPage) (errors: List<string>) : Task<string list> = task {
  let faults = List<string>()
  let! inSidebar = sidebarIds page
  let! inDock = dockIds page
  // Each region holds its panels in the layout's order, and nothing else with an id.
  if inSidebar <> expected layout.Sidebar then faults.Add(sprintf "the sidebar holds %A, the layout says %A" inSidebar (expected layout.Sidebar))
  if inDock <> expected layout.Dock then faults.Add(sprintf "the dock holds %A, the layout says %A" inDock (expected layout.Dock))
  // A panel is on the page once if it is open and not at all if it is closed.
  for pane in PanelId.all do
    let! count = page.EvaluateAsync<int>(sprintf "() => document.querySelectorAll('#%s').length" (rootIdOf pane))
    let want = if List.contains pane layout.Closed then 0 else 1
    if count <> want then faults.Add(sprintf "%s is on the page %d times, expected %d" (PanelId.label pane) count want)
  // Every open panel can be scrolled into view and is not wider than the screen.
  for pane in layout.Sidebar @ layout.Dock do
    let! box =
      page.EvaluateAsync<string>(
        sprintf
          """() => { var el = document.getElementById('%s'); if (!el) return 'missing'; el.scrollIntoView({block: 'nearest', inline: 'nearest'}); var r = el.getBoundingClientRect();
                     return (r.width > 0 && r.height > 0 ? 'ok' : 'empty') + ' left=' + Math.round(r.left) + ' right=' + Math.round(r.right) + ' of ' + innerWidth; }"""
          (rootIdOf pane))
    if not (box.StartsWith "ok") then faults.Add(sprintf "%s is %s" (PanelId.label pane) box)
    else
      let right = box.Split([| "right="; " of " |], StringSplitOptions.None).[1] |> int
      let width = box.Split([| " of " |], StringSplitOptions.None).[1] |> int
      if right > width + 1 then faults.Add(sprintf "%s reaches past the right edge (%s)" (PanelId.label pane) box)
  let! overflow = page.EvaluateAsync<float>("() => document.documentElement.scrollWidth - window.innerWidth")
  if overflow > overflowTolerance then faults.Add(sprintf "the page scrolls sideways (%.1f px too wide)" overflow)
  let! boxes =
    page.EvaluateAsync<string>(
      sprintf
        """() => {
          var limit = window.innerWidth + %f;
          var scrolls = el => { for (var p = el.parentElement; p && p !== document.body; p = p.parentElement) { var o = getComputedStyle(p).overflowX; if (o === 'auto' || o === 'scroll') return true; } return false; };
          return Array.from(document.querySelectorAll('#main *'))
            .filter(el => el.getBoundingClientRect().width > 0 && el.getBoundingClientRect().right > limit && !scrolls(el) && !el.closest('.pane-menu-body'))
            .slice(0, 6)
            .map(el => el.tagName.toLowerCase() + (el.id ? '#' + el.id : '') + ' right=' + Math.round(el.getBoundingClientRect().right))
            .join(' | ');
        }"""
        overflowTolerance)
  if boxes <> "" then faults.Add(sprintf "boxes past the right edge: %s" boxes)
  // The header keeps its right-hand controls inside the screen with the menu button added.
  let! header =
    page.EvaluateAsync<string>(
      """() => Array.from(document.querySelectorAll('.tabline-right > *'))
           .filter(el => el.getBoundingClientRect().width > 0 && el.getBoundingClientRect().right > innerWidth + 1)
           .map(el => el.tagName.toLowerCase() + '.' + el.className + ' right=' + Math.round(el.getBoundingClientRect().right)).join(' | ')""")
  if header <> "" then faults.Add(sprintf "the header's right side runs off the screen: %s" header)
  lock errors (fun () ->
    for error in errors do
      faults.Add(sprintf "console: %s" error))
  return List.ofSeq faults
}

/// The menu opened: its body is inside the screen, scrolls inside itself, and every button is inside it and big enough to hit.
let menuFaults (page: IPage) : Task<string list> = task {
  let faults = List<string>()
  do! page.Locator("#pane-menu > summary").ClickAsync()
  let! isOpen = page.EvaluateAsync<bool>("() => document.getElementById('pane-menu').open")
  if not isOpen then faults.Add "the menu did not open"
  let! body =
    page.EvaluateAsync<string>(
      """() => { var b = document.querySelector('.pane-menu-body'); var r = b.getBoundingClientRect();
                 var out = [];
                 if (r.left < -1 || r.right > innerWidth + 1) out.push('body left=' + Math.round(r.left) + ' right=' + Math.round(r.right) + ' of ' + innerWidth);
                 if (r.top < -1 || r.bottom > innerHeight + 1) out.push('body top=' + Math.round(r.top) + ' bottom=' + Math.round(r.bottom) + ' of ' + innerHeight);
                 if (b.scrollWidth > b.clientWidth + 1) out.push('body scrolls sideways');
                 Array.from(b.querySelectorAll('button')).forEach(btn => { var q = btn.getBoundingClientRect();
                   if (q.width < 24 || q.height < 24) out.push(btn.id + ' is ' + Math.round(q.width) + 'x' + Math.round(q.height));
                   if (q.right > r.right + 1 || q.left < r.left - 1) out.push(btn.id + ' is outside the menu'); });
                 return out.join(' | '); }""")
  if body <> "" then faults.Add body
  return List.ofSeq faults
}

let menuPaneIds (page: IPage) (place: string) : Task<string list> = task {
  let! ids =
    page.EvaluateAsync<string>(
      sprintf "() => Array.from(document.querySelectorAll('.pane-menu-row[data-place=\"%s\"]')).map(r => r.getAttribute('data-pane')).join(',')" place)
  return ids.Split([| ',' |], StringSplitOptions.RemoveEmptyEntries) |> List.ofArray
}

let tokens (panes: PanelId list) : string list = panes |> List.map PanelId.token

let savePicture (page: IPage) (name: string) : Task<unit> =
  PlaywrightFixture.takeScreenshot page false (Path.Combine(PlaywrightFixture.screenshotDir, name))

[<Tests>]
let tests =
  testSequenced <|
  testList "Dashboard pane layout browser tests" [

    testTask "[Integration] Panes: every layout at 375, 768 and 1400 px draws each open panel once in its region, reachable, with the menu inside the screen and no console error" {
      let report = List<string>()
      for name, saved in layouts do
        for width in widths do
          let layout = saved |> Option.defaultValue (PaneLayout.defaultFor workflow)
          do! inLiveBrowser width saved (fun page _ errors _ -> task {
            let! faults = faultsIn layout page errors
            for fault in faults do report.Add(sprintf "%s @ %d px: %s" name width fault)
            let! menu = menuFaults page
            for fault in menu do report.Add(sprintf "%s @ %d px, menu: %s" name width fault)
            let! closedInMenu = menuPaneIds page "closed"
            if closedInMenu <> tokens layout.Closed then
              report.Add(sprintf "%s @ %d px: the menu lists %A as closed, the layout says %A" name width closedInMenu (tokens layout.Closed))
            do! savePicture page (sprintf "pane-layout-%s-%d.png" (name.Replace(' ', '-')) width)
            return () })
      Expect.equal (sprintf "every cell is right:\n%s" (String.Join("\n", report))) 0 report.Count
    }
    |> Integration.register (Integration.Dedicated "--integration-browser")

    testTask "[Integration] Panes: a panel is moved to the dock, up, closed, reopened and reset by clicking the menu, at 375, 768 and 1400 px, and the layout survives a reload" {
      for width in widths do
        do! inLiveBrowser width None (fun page _ errors posts -> task {
          let at (what: string) = sprintf "%d px: %s" width what
          let openMenu = page.Locator("#pane-menu > summary")
          do! openMenu.ClickAsync()
          // Hot reload to the dock: it leaves the sidebar and is drawn once, in the dock region.
          do! page.Locator("#pane-hot-reload-move").ClickAsync()
          do! PlaywrightExpect.waitForCount BrowserWaits.pageRenders (page.Locator("#pane-dock > #hot-reload-panel")) 1
          let! sidebar = sidebarIds page
          sidebar |> Expect.equal (at "the sidebar lost it and kept the order of the rest")
            (expected [ PanelId.LiveTesting; PanelId.SessionContext; PanelId.Friction; PanelId.AgentTeam; PanelId.Hygiene ])
          let! menuOpen = page.EvaluateAsync<bool>("() => document.getElementById('pane-menu').open")
          menuOpen |> Expect.isTrue (at "the menu stays open across the morph, so several moves fit one visit")
          let! docked = menuPaneIds page "dock"
          docked |> Expect.equal (at "the menu lists it in the dock") [ "hot-reload" ]
          do! savePicture page (sprintf "pane-layout-moved-to-dock-%d.png" width)
          // Cohort up, by keyboard only: focus the button and press Enter.
          do! page.Locator("#pane-cohort-up").FocusAsync()
          do! page.Keyboard.PressAsync("Enter")
          do! PlaywrightExpect.waitForCount BrowserWaits.pageRenders (page.Locator(".pane-menu-row[data-pane='cohort'] + .pane-menu-row[data-pane='friction']")) 1
          let! afterUp = sidebarIds page
          afterUp |> Expect.equal (at "cohort moved above friction")
            (expected [ PanelId.LiveTesting; PanelId.SessionContext; PanelId.AgentTeam; PanelId.Friction; PanelId.Hygiene ])
          // Friction closed: it leaves the page and is listed under Closed with a Reopen button.
          do! page.Locator("#pane-friction-close").ClickAsync()
          do! PlaywrightExpect.waitForCount BrowserWaits.pageRenders (page.Locator("#friction-panel")) 0
          do! PlaywrightExpect.waitForCount BrowserWaits.pageRenders (page.Locator("#pane-friction-reopen")) 1
          let! closed = menuPaneIds page "closed"
          closed |> Expect.equal (at "listed as closed") [ "friction" ]
          // Reopened: it comes back to the end of the sidebar.
          do! page.Locator("#pane-friction-reopen").ClickAsync()
          do! PlaywrightExpect.waitForCount BrowserWaits.pageRenders (page.Locator("#friction-panel")) 1
          let! afterReopen = sidebarIds page
          afterReopen |> Expect.equal (at "reopened at the end of the sidebar")
            (expected [ PanelId.LiveTesting; PanelId.SessionContext; PanelId.AgentTeam; PanelId.Hygiene; PanelId.Friction ])
          // A reload draws the saved layout: the page was rendered from the file, not from the browser.
          let! _ = page.ReloadAsync()
          let! _ = page.WaitForSelectorAsync(".app-layout", PageWaitForSelectorOptions(Timeout = float32 BrowserWaits.pageRenders))
          let! afterReload = sidebarIds page
          afterReload |> Expect.equal (at "the layout survives a reload") (expected [ PanelId.LiveTesting; PanelId.SessionContext; PanelId.AgentTeam; PanelId.Hygiene; PanelId.Friction ])
          let! dockAfterReload = dockIds page
          dockAfterReload |> Expect.equal (at "and so does the dock") [ DomIds.HotReloadPanel ]
          // Reset this layout: every panel is back in its default place and the dock region is gone.
          do! page.Locator("#pane-menu > summary").ClickAsync()
          do! page.Locator("#pane-reset").ClickAsync()
          do! PlaywrightExpect.waitForCount BrowserWaits.pageRenders (page.Locator("#pane-dock")) 0
          let! afterReset = sidebarIds page
          afterReset |> Expect.equal (at "reset restores the default order") (expected PanelId.all)
          let! resetDisabled = page.Locator("#pane-reset").IsDisabledAsync()
          resetDisabled |> Expect.isTrue (at "and Reset is disabled again, because there is nothing to reset")
          do! savePicture page (sprintf "pane-layout-after-reset-%d.png" width)
          Expect.isEmpty (at "no console error") (List.ofSeq errors)
          Expect.isGreaterThan (at "the clicks posted") (posts.Count, 4)
          return () })
    }
    |> Integration.register (Integration.Dedicated "--integration-browser")

    testTask "[Integration] Panes: the menu opens and a panel moves with the keyboard alone, and every control has an accessible name" {
      do! inLiveBrowser 768 None (fun page _ errors _ -> task {
        // Enter on the focused summary opens the menu.
        do! page.Locator("#pane-menu > summary").FocusAsync()
        do! page.Keyboard.PressAsync("Enter")
        let! isOpen = page.EvaluateAsync<bool>("() => document.getElementById('pane-menu').open")
        isOpen |> Expect.isTrue "Enter opens the menu"
        // Space on a focused button moves the panel.
        do! page.Locator("#pane-cohort-move").FocusAsync()
        do! page.Keyboard.PressAsync("Space")
        do! PlaywrightExpect.waitForCount BrowserWaits.pageRenders (page.Locator("#pane-dock > #cohort-panel")) 1
        let! unnamed =
          page.EvaluateAsync<string>(
            """() => Array.from(document.querySelectorAll('.pane-menu-body button, .pane-menu-toggle'))
                 .filter(el => !(el.getAttribute('aria-label') || el.textContent).trim())
                 .map(el => el.id || el.className).join(',')""")
        unnamed |> Expect.equal "every control in the menu has a name" ""
        Expect.isEmpty "no console error" (List.ofSeq errors)
        return () })
    }
    |> Integration.register (Integration.Dedicated "--integration-browser")
  ]
