/// The no-session page at three widths, in a real headless Chromium, served from memory with the real stylesheet.
///
/// At 400px wide the Sessions sidebar sat at y=1186 under a 1050px main area, and the empty live-bindings strip was drawn
/// above the session picker. The page is the real landing page with 266 stopped sessions (the measured shape), so the
/// Resume Previous cap is part of what is proven. A second group looks at the cohort panel's conductor sentence beside the
/// Seat buttons it draws. Screenshots go to `SAGEFS_BROWSER_SCREENSHOTS`.
module SageFs.Tests.DashboardNarrowLayoutBrowserTests

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
open SageFs.Tests.DashboardPanelVisibilityTests
open SageFs.Cohort
open SageFs.MemberTable
open SageFs.Server.DashboardPanelContext

module Integration = SageFs.Tests.TestInfrastructure.Integration

let private origin = "http://sagefs.test"

/// The window height every width is looked at in. The sidebar must start inside it at the narrow width.
let private windowHeight = 900

/// The widths the review measured.
let private widths = [ 1440; 900; 400 ]

let private overflowTolerance = 1.0

let private stopped (n: int) : PreviousSession =
  let project = n % 28
  { Id = sprintf "%08x" (0x1000 + n)
    WorkingDir = sprintf "/work/project-%02d" project
    Projects = [ sprintf "/work/project-%02d/Project%02d.fsproj" project project ]
    LastSeen = DateTime(2026, 11, 8, 12, 0, 0, DateTimeKind.Utc).AddMinutes(float -n) }

/// 266 stopped sessions across 28 projects, the shape measured on a working machine.
let private manyStopped = [ for n in 1 .. 266 -> stopped n ]

let private pageHtml : Lazy<string> =
  lazy (
    let queries =
      { DashboardRenderGateTests.mkQueries (ref 0) [] with
          GetPreviousSessions = fun () -> Task.FromResult manyStopped }
    let page = (Dashboard.renderLanding queries (DashboardRenderGateTests.mkInfra ()) "").GetAwaiter().GetResult()
    renderNode page)

/// Serve the dashboard from memory. The stream is answered with an empty event stream, so nothing is ever pushed.
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

/// The page at `width`, run through `body`. The browser is closed by awaiting, and a failure is carried out and rethrown after.
let private atWidthWith (html: string) (waitFor: string) (width: int) (body: IPage -> Task<unit>) = task {
  let! playwright = Playwright.CreateAsync()
  let! browser = playwright.Chromium.LaunchAsync(BrowserTypeLaunchOptions(Headless = true))
  let! context = browser.NewContextAsync(BrowserNewContextOptions(ViewportSize = ViewportSize(Width = width, Height = windowHeight)))
  let! page = context.NewPageAsync()
  let! failure =
    task {
      try
        do! serve html page
        let! _ = page.GotoAsync(sprintf "%s/dashboard" origin)
        let! _ = page.WaitForSelectorAsync(waitFor, PageWaitForSelectorOptions(State = WaitForSelectorState.Attached, Timeout = float32 BrowserWaits.pageRenders))
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

let private atWidth (width: int) (body: IPage -> Task<unit>) =
  atWidthWith pageHtml.Value "#session-picker .picker-container" width body

/// A screenshot of the window, saved for a person to look at. Chromium sometimes answers `Unable to capture screenshot` under load,
/// so it is asked again, up to a few times, before the picture is given up on: the assertions carry the proof either way.
let private saveWindow (page: IPage) (path: string) : Task<unit> = task {
  let mutable attempts = 0
  let mutable saved = false
  while not saved && attempts < 5 do
    attempts <- attempts + 1
    try
      let! _ = page.ScreenshotAsync(PageScreenshotOptions(Path = path))
      saved <- true
    with _ -> ()
  match saved with
  | true -> eprintfn "screenshot: %s" path
  | false -> eprintfn "screenshot skipped after %d attempts: %s" attempts path
}

let private screenshot (page: IPage) (width: int) =
  saveWindow page (IO.Path.Combine(PlaywrightFixture.screenshotDir, sprintf "narrow-layout-%d.png" width))

let private alice = MemberId.Minted "alice"
let private bob = MemberId.Minted "bob"
let private machine = CohortScope.Machine
let private join who = CohortCommand.Join(who, JoinableRole.Implementer, None, machine)
let private claim who file = CohortCommand.AcquireClaim(who, ClaimScope.File file, "editing", machine)

/// The states the panel has a sentence for, each with the number of Seat buttons it must draw.
let private cohortStates : (string * CohortCommand<MemberId> list * int) list =
  [ "one member", [ join alice ], 0
    // The orphan is bob's: a conductor holding a claim may not leave, so alice leaves empty-handed.
    "vacant seat, nobody present", [ join alice; join bob; claim bob "src/A.fs"; CohortCommand.Depart(bob, machine); CohortCommand.Depart(alice, machine) ], 0
    "vacant seat, a member present", [ join alice; join bob; CohortCommand.Depart(alice, machine); claim bob "src/B.fs" ], 1 ]

/// The page with the cohort panel drawn from `commands`, opened so its sentence is on screen.
let private cohortPage (commands: CohortCommand<MemberId> list) : string =
  let ledger = ledgerAfter commands
  let facts = { replFacts with Cohort = PanelFacts.cohortPresence (frameOf ledger) }
  let snap = PanelVisibility.apply facts (snapshotWith off ledger)
  Dashboard.renderShell "0.0.0" "client-1" "abcd1234" "/work" (renderMainContent snap) |> renderNode

[<Tests>]
let tests =
  testSequenced <|
  testList "Dashboard layout journeys" [
    for width in widths do
      testTask (sprintf "[Integration] Narrow layout: at %d wide the page does not scroll sideways" width) {
        do! atWidth width (fun page -> task {
          do! screenshot page width
          let! overflow = page.EvaluateAsync<float>("() => document.documentElement.scrollWidth - window.innerWidth")
          Expect.isTrue (sprintf "the page is %f px wider than the window" overflow) (overflow <= overflowTolerance)
        })
      }
      |> Integration.register (Integration.Dedicated "--integration-browser")

    testTask "[Integration] Narrow layout: at 400 wide the Sessions sidebar starts inside the window, not a screen and a half down" {
      do! atWidth 400 (fun page -> task {
        let! top = page.EvaluateAsync<float>("() => document.querySelector('.sidebar').getBoundingClientRect().top + window.scrollY")
        Expect.isTrue (sprintf "the sidebar starts at y=%f in a %d px window" top windowHeight) (top < float windowHeight)
        // Putting the list first must not push the first-run action out of the first screen.
        let! quickStartTop = page.EvaluateAsync<float>("() => document.querySelector('[data-testid=quick-start]').getBoundingClientRect().top + window.scrollY")
        Expect.isTrue (sprintf "Quick Start starts at y=%f in a %d px window" quickStartTop windowHeight) (quickStartTop < float windowHeight)
      })
    }
    |> Integration.register (Integration.Dedicated "--integration-browser")

    testTask "[Integration] Narrow layout: at 400 wide no empty live-bindings strip is drawn above, or at all beside, the picker" {
      do! atWidth 400 (fun page -> task {
        let! shown = page.EvaluateAsync<bool>("() => { var d = document.getElementById('live-dock'); return !!d && getComputedStyle(d).display !== 'none'; }")
        Expect.isFalse "the empty dock is not drawn while the picker is the page" shown
        let! height = page.EvaluateAsync<float>("() => { var d = document.getElementById('live-dock'); return d ? d.getBoundingClientRect().height : 0; }")
        Expect.equal "and it takes no height" height 0.0
      })
    }
    |> Integration.register (Integration.Dedicated "--integration-browser")

    testTask "[Integration] Narrow layout: 266 stopped sessions draw at most the open cap of rows, the rest sit behind show all" {
      do! atWidth 1440 (fun page -> task {
        let! open' = page.EvaluateAsync<int>("() => document.querySelectorAll('.picker-previous > button.picker-session-row').length")
        Expect.isTrue (sprintf "%d rows are open" open') (open' <= SageFs.Server.DashboardFragments.PreviousListCaps.standard.Total)
        let! hidden = page.EvaluateAsync<int>("() => document.querySelectorAll('#previous-show-all .picker-session-row').length")
        Expect.equal "every other session is behind the disclosure" (open' + hidden) 266
        let! visibleBefore = page.EvaluateAsync<bool>("() => document.querySelector('#previous-show-all .picker-session-row').checkVisibility()")
        Expect.isFalse "the hidden rows are not drawn until the disclosure opens" visibleBefore
        do! page.Locator("#previous-show-all > summary").FocusAsync()
        do! page.Keyboard.PressAsync("Enter")
        let! visibleAfter = page.EvaluateAsync<bool>("() => document.querySelector('#previous-show-all .picker-session-row').checkVisibility()")
        Expect.isTrue "Enter on the focused summary opens it" visibleAfter
      })
    }
    |> Integration.register (Integration.Dedicated "--integration-browser")

    for name, commands, seatButtons in cohortStates do
      testTask (sprintf "[Integration] Cohort panel: %s says who conducts or what to do, and its Seat buttons match it" name) {
        do! atWidthWith (cohortPage commands) "[data-testid='cohort-conductor']" 1440 (fun page -> task {
          // Open the panel the way a person does, so the sentence is drawn and the picture shows it.
          do! page.Locator("#cohort-panel > summary").ClickAsync()
          let! drawn = page.EvaluateAsync<bool>("() => document.querySelector(\"[data-testid='cohort-conductor']\").checkVisibility()")
          Expect.isTrue "the conductor sentence is drawn once the panel is open" drawn
          let! sentence = page.EvaluateAsync<string>("() => document.querySelector(\"[data-testid='cohort-conductor']\").innerText")
          let! buttons = page.EvaluateAsync<int>("() => document.querySelectorAll(\"[data-testid='cohort-seat']\").length")
          let path = IO.Path.Combine(PlaywrightFixture.screenshotDir, sprintf "cohort-seat-%s.png" (name.Replace(' ', '-').Replace(',', '-')))
          do! saveWindow page path
          Expect.equal (sprintf "Seat buttons drawn for '%s'" sentence) buttons seatButtons
          let promisesAButton = sentence.Contains "click Seat" && not (sentence.Contains "no Seat button yet")
          Expect.isTrue (sprintf "'%s' promises a Seat button only when one is drawn" sentence) (not promisesAButton || buttons > 0)
        })
      }
      |> Integration.register (Integration.Dedicated "--integration-browser")
  ]
