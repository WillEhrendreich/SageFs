/// The live bindings pane's header in a real browser, with no daemon: the pane is the real one, drawn into the real shell with the
/// real stylesheet and served from memory. Values come first, so what the pane says about itself above the first value is measured,
/// not guessed: at 700 px or wider the help above the rows is one line, and at 400 px it is at most two.
///
/// A line is a distinct vertical centre among the header's boxes (they are centred on their line, so a button and a caption in one
/// line share a centre), counted for the boxes that are drawn.
module SageFs.Tests.DashboardLiveBindingsHeaderBrowserTests

open System
open System.Threading.Tasks
open Expecto
open Expecto.Flip
open Falco.Markup
open Microsoft.Playwright
open SageFs
open SageFs.Features
open SageFs.Features.LiveBindingsPane
open SageFs.Features.LiveValueTree
open SageFs.Features.Tweak.TweakAddress
open SageFs.Features.Tweak.LiteralEdit
open SageFs.Features.Tweak.Nudge
open SageFs.Features.Tweak.BindingTweak
open SageFs.Features.Tweak.BindingTweakRows
open SageFs.Server
open SageFs.Server.DashboardTypes
open SageFs.Server.DashboardFragments
open SageFs.Tests.DashboardBrowserTests

module Integration = SageFs.Tests.TestInfrastructure.Integration

let origin = "http://sagefs.test"

let node (label: string) (kind: NodeKind) (preview: string) (typeName: string) (children: LiveValueNode list) : LiveValueNode =
  { Label = label; TypeName = typeName; Preview = preview; Kind = kind; Children = children; BestEffort = false; Depth = 0 }

/// A pane with one editable row (`gravity`, in a file) and two bindings no file defines.
let dockHtml (mode: ValueWalk) : string =
  let place : SourceRef =
    { File = "/work/Tuning.fs"
      Address = { ModulePath = []; BindingName = "gravity"; Path = [] }
      Text = "9.8"
      Hash = String('a', 64)
      Span = { Line = 3; Column = 1; EndLine = 3; EndColumn = 3 }
      Kind = ItemKind.Knob(LiteralValue.Real 9.8) }
  let view (state: PersistenceState) (control: Control) (placed: PlaceOf) : RowView =
    { State = state
      Control = control
      Place = placed
      Watching = WatchStatus.Watched
      Reload = RowReload.NoWriteYet
      Undo = HistoryStep.NothingToStep
      Redo = HistoryStep.NothingToStep
      Through = ThroughCall.NoCall }
  let notInAFile = view (PersistenceState.NotInAFile NotInFileWhy.NoOwnedFileBindsIt) Control.NoControl PlaceOf.NotPlaced
  let tweaks : TweakView =
    { Rows =
        Map.ofList
          [ RowKey.top "gravity", view (PersistenceState.InSource place) (Control.RealStepper(9.8, Step.Fraction 1)) (PlaceOf.Placed place)
            RowKey.top "speed", notInAFile
            RowKey.top "scores", notInAFile ] }
  let bindings : LiveBindingValue list =
    [ { Name = "gravity"; TypeSignature = "float"; Root = node "gravity" NodeKind.Leaf "9.8" "Double" [] }
      { Name = "speed"; TypeSignature = "int"; Root = node "speed" NodeKind.Leaf "3" "Int32" [] }
      { Name = "scores"; TypeSignature = "int list"; Root = node "scores" NodeKind.List "[1; 2; 3]" "FSharpList`1[Int32]" [ node "[0]" NodeKind.Leaf "1" "Int32" [] ] } ]
  let walked : PaneView =
    { Snapshot = { SessionId = "abcd1234"; Generation = 7L; Bindings = bindings; Truncated = false; CapturedAt = DateTimeOffset.UnixEpoch }
      Notes = { Mode = mode; Click = NoClickYet } }
  Server.LiveBindingsDock.renderDockWith
    (View.recommended ViewWorkflow.Repl) None tweaks DockPanes.SessionInView "abcd1234" (Server.LiveBindingsDock.WalkedBindings walked) None
  |> renderNode

let shellHtml (mode: ValueWalk) : string =
  let empty = Elem.div [] []
  let snapshot : DashboardSnapshot =
    { DashboardSnapshot.Version = "0.0.0"
      SessionId = "abcd1234"; WorkingDir = "/work"
      WarmupProgress = ""; WorkflowLabel = "REPL"; EvalStats = FixtureStats.noEvals
      ThemeName = "default"; ConnectionLabel = None; InView = FixtureViews.running
      HotReloadPanel = empty; SessionContextPanel = empty
      OutputPanel = empty
      SessionsPanel = empty
      SessionPicker = empty
      ThemePicker = empty; ThemeVars = empty
      BindingsPanel = Text.raw (dockHtml mode); DaemonHealth = empty; FailureNarrativesPanel = empty; DiagnosticsPanel = empty
      FilmstripPanel = empty; AlarmPanel = empty; LiveTestingPanel = empty; FrictionPanel = empty
      CohortPanel = empty; HygienePanel = empty
      ActiveProject = None; ProjectRoles = []
      App = AppRun.AppRunState.NotRunning
      EvalToPixelP50Ms = None; EvalToPixelP99Ms = None }
  Dashboard.renderShell "0.0.0" "client-1" "abcd1234" "/work" (renderMainContent snapshot) |> renderNode

/// Serve the dashboard from memory at a viewport width, and run `body` on the page.
let atWidth (mode: ValueWalk) (width: int) (body: IPage -> Task<unit>) = task {
  let html = shellHtml mode
  let! playwright = Playwright.CreateAsync()
  let! browser = playwright.Chromium.LaunchAsync(BrowserTypeLaunchOptions(Headless = true))
  let! context = browser.NewContextAsync(BrowserNewContextOptions(ViewportSize = ViewportSize(Width = width, Height = 900)))
  let! page = context.NewPageAsync()
  let! failure =
    task {
      try
        do! page.RouteAsync("**/*", Func<IRoute, Task>(fun route ->
          task {
            let fulfil (contentType: string) (text: string) =
              route.FulfillAsync(RouteFulfillOptions(Status = 200, ContentType = contentType, Body = text))
            match Uri(route.Request.Url).AbsolutePath with
            | "/dashboard" -> do! fulfil "text/html; charset=utf-8" html
            | "/dashboard/dashboard.css" -> do! fulfil "text/css; charset=utf-8" Dashboard.dashboardCss
            | "/dashboard/datastar.js" -> do! fulfil "application/javascript; charset=utf-8" Dashboard.datastarBundle
            | path when path.StartsWith "/dashboard/stream/" -> do! fulfil "text/event-stream" ""
            | _ -> do! route.FulfillAsync(RouteFulfillOptions(Status = 404, Body = ""))
          } :> Task))
        let! _ = page.GotoAsync(sprintf "%s/dashboard" origin)
        let! _ = page.WaitForSelectorAsync(".live-pane-head", PageWaitForSelectorOptions(Timeout = float32 BrowserWaits.pageRenders))
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

/// How many lines the pane header's drawn boxes make, and how far down the first value row starts from the top of the header.
let headerLines (page: IPage) : Task<int> =
  page.EvaluateAsync<int>(
    """() => {
      const head = document.querySelector('.live-pane-head');
      const centres = new Set(
        Array.from(head.children)
          .map(child => child.getBoundingClientRect())
          .filter(box => box.width > 0 && box.height > 0)
          .map(box => Math.round(box.top + box.height / 2)));
      return centres.size;
    }""")

/// The most lines of help a header may take above the rows at a width: one when there is room, two on a narrow screen.
let allowedLines (width: int) : int =
  match width >= 700 with
  | true -> 1
  | false -> 2

let widths = [ 400; 700; 1000 ]

[<Tests>]
let tests =
  testSequenced <|
  testList "Live bindings header browser tests" [
    for width in widths do
      testTask (sprintf "[Integration] Header: at %d px the pane's own help above the rows is at most %d line(s), in the standard mode" width (allowedLines width)) {
        do! atWidth ValueWalk.standard width (fun page -> task {
          let! lines = headerLines page
          (lines, allowedLines width) |> Expect.isLessThanOrEqual (sprintf "at %d px the header takes %d lines" width lines)
        })
      }
      |> Integration.register (Integration.Dedicated "--integration-browser")

    testTask "[Integration] Header: a mode that is not the standard adds exactly one line, its meaning, at 700 px" {
      let standard = ref 0
      let other = ref 0
      do! atWidth ValueWalk.standard 700 (fun page -> task { let! lines = headerLines page in standard.Value <- lines })
      do! atWidth WalkEverything 700 (fun page -> task { let! lines = headerLines page in other.Value <- lines })
      other.Value |> Expect.equal "the meaning of the chosen mode is the one extra line" (standard.Value + 1)
    }
    |> Integration.register (Integration.Dedicated "--integration-browser")
  ]
