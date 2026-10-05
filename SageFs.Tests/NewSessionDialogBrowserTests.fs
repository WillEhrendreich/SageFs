/// The guided new-session dialog in a real browser, against a real daemon.
///
/// A [+] on the Sessions list opens a native modal that finds projects, says in a line what each workflow
/// means, warns before a second session lands where one already works, and shows a refusal from the daemon in
/// place. Everything here is clicked and typed the way a person does it, never posted around the page.
///
/// Each journey owns its own daemon (fresh ports, fresh SAGEFS_DATA_DIR, `--no-resume`) so sessions one journey
/// makes cannot reach another. Every journey ends by asserting zero console and page errors, and the three that
/// matter to a person looking at the screen save screenshots to `SAGEFS_BROWSER_SCREENSHOTS`.
///
/// Registered `Integration.Dedicated "--integration-browser"`, the same tier as the rest of the dashboard
/// journeys.
module SageFs.Tests.NewSessionDialogBrowserTests

open System
open System.IO
open System.Threading.Tasks
open Expecto
open Expecto.Flip
open Microsoft.Playwright
open SageFs.Server.NewSessionDialog
open SageFs.Tests.DashboardBrowserTests
open SageFs.Tests.DashboardSessionSwitchOutputBrowserTests

module Integration = SageFs.Tests.TestInfrastructure.Integration

/// The sizes the layout has to hold across. The narrow one is the width the sidebar was designed down to.
[<RequireQualifiedAccess>]
type Viewport =
  | Phone
  | Wide

module Viewport =
  let size = function
    | Viewport.Phone -> 320, 640
    | Viewport.Wide -> 1400, 900

  let name = function
    | Viewport.Phone -> "320px"
    | Viewport.Wide -> "wide"

/// A directory holding several small sample projects, which is what "discovers the sample projects" means.
let samplesDir = Path.Combine(repoRoot, "samples", "demos")

let byTestId (page: IPage) (id: string) : ILocator = page.GetByTestId id

let dialogOf (page: IPage) : ILocator = page.Locator(sprintf "#%s" NewSessionNames.DialogId)

/// One journey's browser, page and the errors it saw.
type Seen =
  { Page: IPage
    Errors: Collections.Generic.List<string> }

let assertNoErrors (label: string) (seen: Seen) =
  List.ofSeq seen.Errors
  |> Expect.isEmpty (sprintf "[%s] zero console or page errors across the journey, got: %s" label (String.concat " | " seen.Errors))

/// Boot a daemon, open a page of the given size on its dashboard, run the journey, and always tear both down.
let withDashboard (viewport: Viewport) (journey: Daemon -> Seen -> Task<unit>) : Task<unit> = task {
  let daemon = startDaemon ()
  let mutable playwright: IPlaywright option = None
  let mutable browser: IBrowser option = None
  let mutable failure: exn option = None
  try
    let! healthy = waitHealthy daemon
    if not healthy then
      dumpLogs daemon
      Tests.failtestf "the daemon on port %d never became healthy" daemon.McpPort
    let! pw = Playwright.CreateAsync()
    let! chromium = pw.Chromium.LaunchAsync(BrowserTypeLaunchOptions(Headless = true))
    playwright <- Some pw
    browser <- Some chromium
    let width, height = Viewport.size viewport
    let! ctx = chromium.NewContextAsync(BrowserNewContextOptions(ViewportSize = ViewportSize(Width = width, Height = height)))
    let! page = ctx.NewPageAsync()
    let errors = Collections.Generic.List<string>()
    page.Console.Add(fun msg -> if msg.Type = "error" then errors.Add(sprintf "[console] %s" msg.Text))
    page.PageError.Add(fun err -> errors.Add(sprintf "[pageerror] %s" err))
    do! journey daemon { Page = page; Errors = errors }
    try do! ctx.CloseAsync() with _ -> ()
  with ex ->
    failure <- Some ex
    dumpLogs daemon
  match browser with
  | Some b -> do! closeBrowserSafely b
  | None -> ()
  playwright |> Option.iter (fun p -> try p.Dispose() with _ -> ())
  killDaemon daemon
  match failure with
  | Some ex -> return raise ex
  | None -> ()
}

let shot (page: IPage) (name: string) : Task<unit> = task {
  let path = Path.Combine(PlaywrightFixture.screenshotDir, sprintf "new-session-%s.png" name)
  let! _ = page.ScreenshotAsync(PageScreenshotOptions(Path = path))
  ()
}

let goToDashboard (daemon: Daemon) (page: IPage) : Task<unit> = task {
  let! _ = page.GotoAsync(sprintf "http://localhost:%d/dashboard" daemon.DashboardPort)
  do! (byTestId page NewSessionNames.OpenTestId).WaitForAsync(LocatorWaitForOptions(State = WaitForSelectorState.Visible, Timeout = float32 BrowserWaits.pageRenders))
}

let stateOf (page: IPage) : Task<string> = task {
  let! state = (dialogOf page).GetAttributeAsync "data-state"
  return state
}

let waitForState (page: IPage) (wanted: string) : Task<unit> = task {
  let! reached =
    waitUntil BrowserWaits.daemonWork (fun () -> task {
      let! state = stateOf page
      return state = wanted
    })
  let! last = stateOf page
  reached |> Expect.isTrue (sprintf "the dialog reached state '%s' (it is '%s')" wanted last)
}

let isOpen (page: IPage) : Task<bool> =
  page.EvaluateAsync<bool>(sprintf "() => { var d = document.getElementById('%s'); return d ? d.open : false; }" NewSessionNames.DialogId)

let waitForOpen (page: IPage) (wanted: bool) : Task<unit> = task {
  let! reached = waitUntil BrowserWaits.pageRenders (fun () -> task { let! now = isOpen page in return now = wanted })
  reached |> Expect.isTrue (sprintf "the dialog %s" (match wanted with | true -> "opened" | false -> "closed"))
}

let focusedId (page: IPage) : Task<string> =
  page.EvaluateAsync<string>("() => (document.activeElement && document.activeElement.id) || ''")

let openDialog (page: IPage) : Task<unit> = task {
  do! (byTestId page NewSessionNames.OpenTestId).ClickAsync()
  do! waitForOpen page true
}

/// Type a directory into the dialog and ask it to look there.
let lookIn (page: IPage) (directory: string) : Task<unit> = task {
  do! page.Locator(sprintf "#%s" NewSessionNames.DirectoryInputId).FillAsync directory
  do! (page.GetByRole(AriaRole.Button, PageGetByRoleOptions(Name = "Find projects in this directory"))).ClickAsync()
}

/// One session, made the way an agent or an editor makes it, so a journey can start with something already there.
let createBareSessionAt (daemon: Daemon) (directory: string) : Task<string> = task {
  let! created = post daemon SageFs.Timeouts.browserJourneyWarmup "/api/sessions/create" {| projects = Array.empty<string>; workingDirectory = directory |}
  match created with
  | Error why -> return Tests.failtestf "creating a bare session in %s was refused: %s" directory why
  | Ok _ ->
    let! ready =
      waitUntil BrowserWaits.daemonWork (fun () -> task {
        try
          let! now = sessionsNow daemon
          return now |> List.exists (fun (_, status, dir) -> status = "Ready" && normalize dir = normalize directory)
        with _ -> return false
      })
    ready |> Expect.isTrue "the bare session reached Ready on the daemon"
    let! sessions = sessionsNow daemon
    return idFor sessions directory
}

let boxOf (locator: ILocator) : Task<LocatorBoundingBoxResult> = task {
  let! box = locator.BoundingBoxAsync()
  match box with
  | null -> return Tests.failtest "an element that should be on screen has no box"
  | b -> return b
}

// ── (1) no sessions, bare URL: the plus is there, the dialog finds samples, a bare session is created and warms ──

let createJourney (daemon: Daemon) (seen: Seen) : Task<unit> = task {
  let page = seen.Page
  do! goToDashboard daemon page

  // The shell is whole with no session in play: header, Sessions heading, and the picker in the main area.
  do! PlaywrightExpect.isVisibleAsync (page.Locator ".app-header") "the header is there"
  do! PlaywrightExpect.isVisibleAsync (page.GetByRole(AriaRole.Heading, PageGetByRoleOptions(Name = "Sessions"))) "the Sessions heading is there"
  do! PlaywrightExpect.isVisibleAsync (page.Locator "#session-picker") "the picker is the main area with no session in play"

  // (6) The old collapsible form is gone, not hidden.
  let! oldForm = page.Locator(".new-session-panel").CountAsync()
  oldForm |> Expect.equal "no collapsible New Session panel" 0

  // The plus is a small square icon button, the same size as its neighbours in the Sessions header.
  let plus = byTestId page NewSessionNames.OpenTestId
  do! PlaywrightExpect.isVisibleAsync plus "the plus is visible"
  let! plusBox = boxOf plus
  plusBox.Width |> Expect.equal "square" plusBox.Height
  let! headerButtons = page.Locator(".sidebar-header .sidebar-header-btn, .sidebar-header .icon-btn").AllAsync()
  for button in headerButtons do
    let! box = boxOf button
    box.Width |> Expect.equal "every Sessions header button is the same width as the plus" plusBox.Width
    box.Height |> Expect.equal "and the same height" plusBox.Height
  do! shot page "1-closed-sidebar"

  // Open it: a native modal, and discovery of the default directory runs.
  do! openDialog page
  do! waitForState page "choosing"

  // Look in a directory that holds several sample projects.
  do! lookIn page samplesDir
  let candidates = byTestId page NewSessionNames.CandidateTestId
  let! found =
    waitUntil BrowserWaits.daemonWork (fun () -> task {
      let! text = (dialogOf page).InnerTextAsync()
      return text.Contains "SageFs.Samples.ConsoleTicker"
    })
  found |> Expect.isTrue "the dialog lists the sample projects under the directory"
  let! count = candidates.CountAsync()
  (count >= 2) |> Expect.isTrue (sprintf "several sample projects are offered, got %d" count)
  let! dialogText = (dialogOf page).InnerTextAsync()
  dialogText.Contains "net" |> Expect.isTrue "each carries its framework"
  dialogText.Contains "REPL" |> Expect.isTrue "the REPL workflow is described"
  dialogText.Contains "Live Testing" |> Expect.isTrue "the Live Testing workflow is described"
  dialogText.Contains "Hot Reload" |> Expect.isTrue "the Hot Reload workflow is described"
  do! shot page "2-open-dialog"

  // Bare, then Create: the dialog closes into the new session's card.
  do! (dialogOf page).Locator("input[type=radio][value=bare]").CheckAsync()
  let create = byTestId page NewSessionNames.CreateTestId
  let! enabled = create.IsEnabledAsync()
  enabled |> Expect.isTrue "Create is available once Bare is chosen"
  do! create.ClickAsync()
  do! waitForOpen page false

  let! cardAppeared =
    waitUntil BrowserWaits.daemonWork (fun () -> task {
      let! n = page.Locator("[data-testid=session-card]").CountAsync()
      return n >= 1
    })
  cardAppeared |> Expect.isTrue "a session card appears in the list"

  // The card warms to Ready, read from the page and not only from the API.
  let! sessions = sessionsNow daemon
  let id = idFor sessions samplesDir
  let! ready =
    waitUntil BrowserWaits.daemonWork (fun () -> task {
      let! text = page.Locator(sprintf "#session-card-%s" id).InnerTextAsync()
      return text.Contains "Ready"
    })
  ready |> Expect.isTrue "the card shows Ready once the session has warmed up"
  do! waitForState page "closed"
  assertNoErrors "create" seen
}

// ── (2)(3)(4) a session is already there: the warning names it, a refusal is shown in the dialog, Esc and the
//    close button give the focus back ──

let guardsJourney (daemon: Daemon) (seen: Seen) : Task<unit> = task {
  let page = seen.Page
  let! existing = createBareSessionAt daemon samplesDir
  do! goToDashboard daemon page
  do! openDialog page

  // (2) A live session already works in this directory: the dialog says so before anything is created.
  do! lookIn page samplesDir
  do! waitForState page "warning"
  let warning = byTestId page NewSessionNames.WarningTestId
  do! PlaywrightExpect.isVisibleAsync warning "the warning is visible"
  let! warningText = warning.InnerTextAsync()
  warningText.Contains existing |> Expect.isTrue (sprintf "the warning names the existing session %s" existing)
  do! PlaywrightExpect.isVisibleAsync (byTestId page NewSessionNames.SwitchTestId) "and offers to switch to it"
  do! shot page "3-warning"

  // Creating on purpose is still possible. The daemon refuses an exact twin, and the refusal lands in the dialog.
  do! (dialogOf page).Locator("input[type=radio][value=bare]").CheckAsync()
  do! (byTestId page NewSessionNames.CreateTestId).ClickAsync()
  do! waitForState page "refused"
  do! waitForOpen page true
  let refusal = byTestId page NewSessionNames.RefusalTestId
  do! PlaywrightExpect.isVisibleAsync refusal "(3) the daemon's refusal is shown in the dialog"
  let! refusalText = refusal.InnerTextAsync()
  refusalText.Contains "already exists" |> Expect.isTrue "it is named"
  refusalText.Contains "Next:" |> Expect.isTrue "and says what to do next"
  do! shot page "4-refused"

  // (4) The close button closes it, and focus is back on the plus.
  do! (byTestId page NewSessionNames.CloseTestId).ClickAsync()
  do! waitForOpen page false
  do! waitForState page "closed"
  let! afterButton = focusedId page
  afterButton |> Expect.equal "focus returns to the plus after the close button" NewSessionNames.OpenButtonId

  // Esc closes it too, and focus is back on the plus.
  do! openDialog page
  do! page.Keyboard.PressAsync "Escape"
  do! waitForOpen page false
  do! waitForState page "closed"
  let! afterEscape = focusedId page
  afterEscape |> Expect.equal "focus returns to the plus after Esc" NewSessionNames.OpenButtonId

  // (3) A directory that is not there is refused by name, inside the dialog, with the path in it.
  let missing = Path.Combine(samplesDir, "no-such-directory-here")
  do! openDialog page
  do! lookIn page missing
  do! waitForState page "refused"
  let! missingText = (byTestId page NewSessionNames.RefusalTestId).InnerTextAsync()
  missingText.Contains "does not exist" |> Expect.isTrue "a missing directory is named as missing"
  missingText.Contains missing |> Expect.isTrue "with the path that was typed"
  do! page.Keyboard.PressAsync "Escape"
  do! waitForOpen page false

  // Switching to the existing session from the warning closes the dialog and views that session.
  do! openDialog page
  do! lookIn page samplesDir
  do! waitForState page "warning"
  do! (byTestId page NewSessionNames.SwitchTestId).ClickAsync()
  do! waitForOpen page false
  let! viewing =
    waitUntil BrowserWaits.daemonWork (fun () -> task {
      let! now = page.Locator("#main").GetAttributeAsync "data-viewing-session-id"
      return now = existing
    })
  viewing |> Expect.isTrue "the page is viewing the session the warning named"
  assertNoErrors "guards" seen
}

// ── (5) the layout holds at 320 px and wide ──

type Measures =
  { PageScroll: float
    PageWidth: float
    DialogLeft: float
    DialogRight: float
    DialogTop: float
    DialogBottom: float
    DialogScroll: float
    DialogClient: float
    ViewportHeight: float }

let measure (page: IPage) : Task<Measures> = task {
  let! values =
    page.EvaluateAsync<float[]>(
      sprintf
        """() => {
          var d = document.getElementById('%s');
          var r = d.getBoundingClientRect();
          return [document.documentElement.scrollWidth, window.innerWidth, r.left, r.right, r.top, r.bottom, d.scrollWidth, d.clientWidth, window.innerHeight];
        }"""
        NewSessionNames.DialogId)
  return
    { PageScroll = values.[0]; PageWidth = values.[1]
      DialogLeft = values.[2]; DialogRight = values.[3]; DialogTop = values.[4]; DialogBottom = values.[5]
      DialogScroll = values.[6]; DialogClient = values.[7]; ViewportHeight = values.[8] }
}

let layoutJourney (viewport: Viewport) (daemon: Daemon) (seen: Seen) : Task<unit> = task {
  let page = seen.Page
  let label = Viewport.name viewport
  let! _ = createBareSessionAt daemon samplesDir
  do! goToDashboard daemon page
  do! openDialog page
  // The longest content the dialog has: a found list AND a warning.
  do! lookIn page samplesDir
  do! waitForState page "warning"
  let! m = measure page
  (m.PageScroll <= m.PageWidth) |> Expect.isTrue (sprintf "[%s] the page has no horizontal overflow with the dialog open (scroll %.0f of %.0f)" label m.PageScroll m.PageWidth)
  (m.DialogLeft >= 0.0 && m.DialogRight <= m.PageWidth) |> Expect.isTrue (sprintf "[%s] the dialog sits inside the viewport horizontally (%.0f..%.0f of %.0f)" label m.DialogLeft m.DialogRight m.PageWidth)
  (m.DialogTop >= 0.0 && m.DialogBottom <= m.ViewportHeight) |> Expect.isTrue (sprintf "[%s] the dialog sits inside the viewport vertically (%.0f..%.0f of %.0f)" label m.DialogTop m.DialogBottom m.ViewportHeight)
  (m.DialogScroll <= m.DialogClient) |> Expect.isTrue (sprintf "[%s] nothing inside the dialog overflows sideways (scroll %.0f of %.0f)" label m.DialogScroll m.DialogClient)

  // Both footer buttons are on screen and do not overlap each other.
  let! create = boxOf (byTestId page NewSessionNames.CreateTestId)
  let! cancel = boxOf (byTestId page NewSessionNames.CancelTestId)
  let overlap = create.X < cancel.X + cancel.Width && cancel.X < create.X + create.Width && create.Y < cancel.Y + cancel.Height && cancel.Y < create.Y + create.Height
  overlap |> Expect.isFalse (sprintf "[%s] Create and Cancel do not overlap" label)
  (float create.X >= m.DialogLeft && float (create.X + create.Width) <= m.DialogRight) |> Expect.isTrue (sprintf "[%s] Create is inside the dialog" label)
  (float cancel.X >= m.DialogLeft && float (cancel.X + cancel.Width) <= m.DialogRight) |> Expect.isTrue (sprintf "[%s] Cancel is inside the dialog" label)
  do! shot page (sprintf "5-%s" label)
  assertNoErrors label seen
}

[<Tests>]
let tests =
  testSequenced <|
  testList "New session dialog browser tests" [

    testTask "[Integration] New session dialog: no sessions, the plus opens a dialog that finds the samples, a bare session is created and warms to Ready, and the old form is gone" {
      do! withDashboard Viewport.Wide createJourney }
    |> Integration.register (Integration.Dedicated "--integration-browser")

    testTask "[Integration] New session dialog: it warns about an existing session by name, shows the daemon's refusal in place, and Esc and the close button return focus to the plus" {
      do! withDashboard Viewport.Wide guardsJourney }
    |> Integration.register (Integration.Dedicated "--integration-browser")

    testTask "[Integration] New session dialog: at 320 px the dialog stays inside the viewport with no horizontal overflow" {
      do! withDashboard Viewport.Phone (layoutJourney Viewport.Phone) }
    |> Integration.register (Integration.Dedicated "--integration-browser")

    testTask "[Integration] New session dialog: wide, the dialog stays inside the viewport with no horizontal overflow" {
      do! withDashboard Viewport.Wide (layoutJourney Viewport.Wide) }
    |> Integration.register (Integration.Dedicated "--integration-browser")
  ]
