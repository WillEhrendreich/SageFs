/// Hostile names through the dashboard's attribute and Datastar sinks, in a real headless Chromium, served from memory.
///
/// `DashboardSinkTests` reads the markup. This file runs it: Datastar evaluates the handlers and signals the page carries,
/// so a name that escaped its literal would run, and a literal that broke would throw. Each page sets a trap
/// (`window.__xss`) that every payload tries to spring, and each check reads back the exact text the browser ended up with:
/// the question a Seat button asks, the setting value the input shows, the file the treemap drills into.
module SageFs.Tests.DashboardSinkBrowserTests

open System
open System.Threading.Tasks
open Expecto
open Expecto.Flip
open Falco.Markup
open Microsoft.Playwright
open SageFs
open SageFs.Cohort
open SageFs.MemberTable
open SageFs.Server
open SageFs.Server.DashboardFragments
open SageFs.Tests.DashboardPanelVisibilityTests

module Integration = SageFs.Tests.TestInfrastructure.Integration

let private origin = "http://sagefs.test"

/// Names that each try to run `window.__xss = 1` from a different place: an attribute, a JS literal, a script element.
let private payloads =
  [ "\"><img src=x onerror=\"window.__xss=1\">"
    "';window.__xss=1;'"
    "\\';window.__xss=1;//"
    "\" data-on:click=\"window.__xss=1"
    "</script><script>window.__xss=1</script>"
    "line\nbreak`${window.__xss=1}`" ]

/// Serve `html` at /dashboard and /dashboard/settings with the real stylesheet and Datastar bundle. The stream answers with
/// an empty event stream, so nothing is pushed.
let private serve (html: string) (page: IPage) = task {
  do! page.RouteAsync("**/*", Func<IRoute, Task>(fun route ->
    task {
      let fulfil (contentType: string) (body: string) =
        route.FulfillAsync(RouteFulfillOptions(Status = 200, ContentType = contentType, Body = body))
      match Uri(route.Request.Url).AbsolutePath with
      | "/dashboard" | "/dashboard/settings" -> do! fulfil "text/html; charset=utf-8" html
      | "/dashboard/dashboard.css" -> do! fulfil "text/css; charset=utf-8" Dashboard.dashboardCss
      | "/dashboard/datastar.js" -> do! fulfil "application/javascript; charset=utf-8" Dashboard.datastarBundle
      | path when path.StartsWith "/dashboard/stream/" -> do! fulfil "text/event-stream" ""
      | _ -> do! route.FulfillAsync(RouteFulfillOptions(Status = 404, Body = ""))
    } :> Task))
}

/// What the page did besides what the check asked: dialogs it opened and script errors it threw.
type private PageLog = { Dialogs: ResizeArray<string>; Errors: ResizeArray<string> }

/// Open `html` at `path`, wait for `ready` (a JS predicate that is true once Datastar has applied the page), and run `body`.
let private withPage (html: string) (path: string) (ready: string) (body: IPage -> PageLog -> Task<unit>) = task {
  let! playwright = Playwright.CreateAsync()
  let! browser = playwright.Chromium.LaunchAsync(BrowserTypeLaunchOptions(Headless = true))
  let! context = browser.NewContextAsync(BrowserNewContextOptions(ViewportSize = ViewportSize(Width = 1440, Height = 900)))
  let! page = context.NewPageAsync()
  let log = { Dialogs = ResizeArray(); Errors = ResizeArray() }
  page.add_PageError(fun _ message -> log.Errors.Add message)
  page.add_Dialog(fun _ dialog ->
    log.Dialogs.Add dialog.Message
    dialog.DismissAsync() |> ignore)
  let! failure =
    task {
      try
        do! serve html page
        let! _ = page.GotoAsync(sprintf "%s%s" origin path)
        let! _ = page.WaitForFunctionAsync(ready, null, PageWaitForFunctionOptions(Timeout = float32 BrowserWaits.pageRenders))
        do! body page log
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

/// No payload ran and none made an element.
let private expectTrapUnsprung (page: IPage) (log: PageLog) = task {
  let! sprung = page.EvaluateAsync<bool>("() => window.__xss !== undefined")
  sprung |> Expect.isFalse "no payload ran"
  let! images = page.EvaluateAsync<int>("() => document.querySelectorAll('img').length")
  images |> Expect.equal "no payload made an element" 0
  log.Errors |> List.ofSeq |> Expect.isEmpty "the page threw no script error"
}

let private machine = CohortScope.Machine
let private join (who: MemberId) = CohortCommand.Join(who, JoinableRole.Implementer, None, machine)

/// The cohort panel on a vacant seat, every present member named by a payload, beside a coverage treemap whose one file
/// is named by a payload, inside the real shell whose default directory is a payload too.
let private cohortAndTreemapPage : Lazy<string> =
  lazy (
    let alice = MemberId.Minted "alice"
    let frame =
      ledgerAfter ([ join alice ] @ (payloads |> List.map (MemberId.Minted >> join)) @ [ CohortCommand.Depart(alice, machine) ])
      |> frameOf
    let leaf : Features.Treemap.CoverageTreemapNode =
      { Id = payloads.[0]; Name = payloads.[0]; Kind = Features.Treemap.CoverageNodeKind.File; Weight = 3.0; ProbeCount = 3
        CoveredCount = 2; Status = Features.Treemap.CoverageStatus.Covered; Children = [] }
    let root = { leaf with Id = "root"; Name = "root"; Kind = Features.Treemap.CoverageNodeKind.Project; Children = [ leaf ] }
    let content =
      Elem.div [] [
        renderCohortPanel frame
        Elem.div [ Attr.id "treemap-under-test" ] [ renderCoverageTreemap "abcd1234" (Some root) ] ]
    Dashboard.renderShell "0.0.0" "client-1" "abcd1234" payloads.[1] content |> renderNode)

/// Datastar has run once the treemap's child level is hidden by its `data-show`.
let private treemapApplied =
  "() => { var levels = document.querySelectorAll('#treemap-under-test > div > div:last-child > div'); return levels.length > 1 && getComputedStyle(levels[1]).display === 'none'; }"

/// The settings page for a repo whose directory is a payload, with a row whose saved value is a payload.
let private settingsPage (saved: string) (repoDir: string) : string =
  let d =
    SessionOptionSettings.all
    |> List.find (fun d ->
      match d.Applicability with
      | ReadOnly _ -> false
      | Live | RestartRequired | NextSession -> true)
  let d = { d with Render = fun _ -> saved }
  let provenance : Provenance = { Effective = d.Default; Source = LRepo; PerLayer = [ LDefault, d.Default; LRepo, d.Default ] }
  let row : SettingsPanel.SettingRow =
    { Descriptor = d; Resolved = Ok provenance; InUse = InUseSameAsSaved; Override = SettingsCatalog.NotOverridden; Notes = [] }
  SettingsPanel.renderPage (RepoRootAt repoDir) [ row ] |> renderNode

[<Tests>]
let tests =
  testSequenced <|
  testList "Dashboard sinks in a browser" [
    testTask "[Integration] Sinks: a Seat button for an agent with a hostile name asks about exactly that name and runs nothing else" {
      do! withPage cohortAndTreemapPage.Value "/dashboard" treemapApplied (fun page log -> task {
        do! page.Locator("#cohort-panel > summary").ClickAsync()
        let! names = page.EvaluateAsync<string[]>("() => Array.from(document.querySelectorAll(\"[data-testid='cohort-seat']\")).map(b => b.getAttribute('data-member'))")
        names |> Array.sort |> Expect.equal "each button carries its member's exact name" (payloads |> List.sort |> Array.ofList)
        let buttons = page.Locator("[data-testid='cohort-seat']")
        let! count = buttons.CountAsync()
        for i in 0 .. count - 1 do
          do! buttons.Nth(i).ClickAsync()
        let expected =
          names
          |> Array.map (sprintf "Seat %s as the conductor of this cohort? The conductor mints member tokens, configures the integration and reassigns claims.")
          |> Array.sort
        log.Dialogs |> Seq.sort |> Array.ofSeq |> Expect.equal "each click asked about its own member, word for word" expected
        do! expectTrapUnsprung page log
      })
    }
    |> Integration.register (Integration.Dedicated "--integration-browser")

    testTask "[Integration] Sinks: the coverage treemap drills into a file with a hostile path and shows that path" {
      do! withPage cohortAndTreemapPage.Value "/dashboard" treemapApplied (fun page log -> task {
        do! page.Locator("#treemap-under-test [aria-label]").First.ClickAsync()
        let! shown =
          page.WaitForFunctionAsync(
            "(name) => { var levels = document.querySelectorAll('#treemap-under-test > div > div:last-child > div'); var leaf = levels[1]; return !!leaf && leaf.checkVisibility() && Array.from(leaf.querySelectorAll('div')).some(d => d.textContent === name); }",
            payloads.[0],
            PageWaitForFunctionOptions(Timeout = float32 BrowserWaits.pageRenders))
        shown |> isNull |> Expect.isFalse "the file's level is shown under its exact path"
        do! expectTrapUnsprung page log
      })
    }
    |> Integration.register (Integration.Dedicated "--integration-browser")

    testTask "[Integration] Sinks: a cloned repo's saved setting shows as its exact text in the Settings box and runs nothing" {
      let saved = payloads.[1]
      let html = settingsPage saved payloads.[0]
      let ready = "() => { var i = document.querySelector('.settings-input'); return !!i && i.value.length > 0; }"
      do! withPage html "/dashboard/settings" ready (fun page log -> task {
        let! value = page.EvaluateAsync<string>("() => document.querySelector('.settings-input').value")
        value |> Expect.equal "the box holds the saved text, nothing more" saved
        do! expectTrapUnsprung page log
      })
    }
    |> Integration.register (Integration.Dedicated "--integration-browser")
  ]
