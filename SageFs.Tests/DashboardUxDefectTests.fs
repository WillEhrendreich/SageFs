/// The four screenshot-verified dashboard UI defects, pinned where a pure fragment test can pin them:
///
/// 1. A session card's app line prints Core's full diagnostic wall as card body. The pin: the card body
///    carries a compact chip, the full sentence survives one disclosure away, and nothing (compiler
///    output, remediation) is dropped. The card's source line was redone on master (a headline with a
///    Details list), so only the app line is pinned here.
/// 2. The app-output toolbar states its line count twice ("450 lines of app output" and "450 lines").
///    The pin: one fact, one home: the count is stated once, and the stamp only appears when it adds
///    a fact of its own (held while paused, filtered).
/// 3. The toolbar's worded toggles sit in the uniform 28px squares and spill onto their neighbours.
///    The pin: every worded control carries the label-sizing class, and the stylesheet's rule for that
///    class sizes to content.
/// 4. The top chrome's alarm strip paints an honest, transient fault as a page-wide emergency. The pin:
///    the strip is a thin rule in the failure token (a faulted session is a failure, and red is for failure
///    only) on the panel's own background, with no red tint across its box and a title at the chrome's own
///    colour, and the calming removed no information (count, phase, message, timestamp, dismiss are all
///    still rendered).
module SageFs.Tests.DashboardUxDefectTests

open System
open Expecto
open Expecto.Flip
open Falco.Markup
open SageFs
open SageFs.Server
open SageFs.Server.DashboardTypes
open SageFs.Server.DashboardFragments
open SageFs.Server.DockPanes

module private Fixtures =
  let now = DateTime(2026, 9, 20, 12, 0, 0, DateTimeKind.Utc)

  let info (id: string) : WorkerProtocol.SessionInfo =
    { Id = WorkerProtocol.SessionId.validate id |> Result.defaultValue (WorkerProtocol.SessionId.newId ())
      Name = None
      Projects = [ "/repo/store-falco/store-falco.fsproj" ]
      WorkingDirectory = "/repo/store-falco"
      SolutionRoot = None
      CreatedAt = now.AddMinutes -5.0
      LastActivity = now.AddMinutes -3.0
      Status = WorkerProtocol.SessionLifecycleStatus.ofWorkerReport (WorkerProtocol.SessionLifecycleStatus.Ready { Pid = 1; Port = None }) WorkerProtocol.SessionStatus.Ready
      Workflow = WorkflowTypes.SessionWorkflow.Interactive
      ActiveProject = None
      ProjectRoles = []
      App = AppRun.AppRunState.NotRunning
      Rebuild = LastRebuild.NeverRebuilt
      Reload = SessionReload.NoReloadYet
      Freshness = ReplFreshness.InSync }

  /// The defect as it shipped: a failed rebuild whose reason is real compiler output.
  let buildFailed : AppRun.AppRunState =
    AppRun.AppRunState.BuildFailed (
      "/repo/store-falco/store-falco.fsproj",
      "Program.fs(143,5): error FS1093: The type parameter 'T is missing its constraint 'T : equality'.",
      now,
      AppRun.PreviousAddress.ReuseAddress "http://127.0.0.1:5123")

  let cardWith (id: string) (app: AppRun.AppRunState) (source: CardSource) : ParsedSession =
    { sessionCardOf now None 0 SessionHealth.Healthy (info id) with App = app; Source = source }

  let htmlOf (sessions: ParsedSession list) : string =
    renderSessionsForSession "" sessions false |> renderNode

/// The app-output pane with one line on each stream, the same shape AppOutputViewTests starts from.
let private paneWithBothLines =
  SageFs.Server.AppOutputPane.AppOutputPane.create
  |> SageFs.Server.AppOutputPane.AppOutputPane.feedText SageFs.Server.AppOutputPane.OutputStream.Stderr "boom"
  |> SageFs.Server.AppOutputPane.AppOutputPane.feedText SageFs.Server.AppOutputPane.OutputStream.Stdout "hello"

let private facts presence session =
  { Session = session
    Bindings = NoBindingsYet
    AppOutput = presence
    Pin = Unpinned }

let private draw (pane: SageFs.Server.AppOutputPane.AppOutputPane) (presence: AppOutputPresence) (session: SessionInView) =
  PaneState.decide (facts presence session) DockPane.AppOutput
  |> fun state -> SageFs.Server.AppOutputView.render state pane
  |> renderNode

/// The segment of rendered card HTML from a marker to the first `<details>` after it: what the card
/// shows with no disclosure open. This is the card BODY, and it is where the diagnostic wall used to be.
let private bodyBeforeDisclosure (html: string) (marker: string) : string =
  match html.IndexOf marker, html.IndexOf("<details", (match html.IndexOf marker with | i when i >= 0 -> i | _ -> 0)) with
  | -1, _ -> failtestf "marker %s is not on the card" marker
  | _, -1 -> failtestf "no disclosure follows %s" marker
  | start, details -> html.Substring(start, details - start)

/// How many times a needle occurs: the duplicate-count pin for "one fact, one home".
let private occurrences (needle: string) (haystack: string) : int =
  let mutable count = 0
  let mutable idx = haystack.IndexOf needle
  while idx >= 0 do
    count <- count + 1
    idx <- haystack.IndexOf(needle, idx + needle.Length)
  count

/// The full opening tag of the element carrying this marker (an id or a test id): where a button's
/// classes can be read without counting on attribute order.
let private elementTagWith (html: string) (marker: string) : string =
  let at = html.IndexOf marker
  at >= 0 |> Expect.isTrue (sprintf "%s is rendered" marker)
  let tagStart = html.LastIndexOf("<", at)
  let tagEnd = html.IndexOf('>', at)
  html.Substring(tagStart, tagEnd - tagStart + 1)

/// The declarations of the first CSS rule whose selector is exactly this one: what the browser will
/// actually apply, read from the stylesheet the daemon serves (`Dashboard.dashboardCss`).
let private cssRule (selector: string) : string =
  let css = SageFs.Server.Dashboard.dashboardCss
  let marker = selector + " {"
  let start = css.IndexOf marker
  start >= 0 |> Expect.isTrue (sprintf "%s exists in the stylesheet" marker)
  let openBrace = css.IndexOf('{', start)
  let close = css.IndexOf('}', openBrace)
  css.Substring(start, close - start + 1)

[<Tests>]
let cardStateChipTests =
  testList "the session card's state lines are compact chips, with the full text one disclosure away" [

    testCase "WHY - a failed rebuild states where it broke in one line: the compiler wall never renders as card body" <| fun _ ->
      let html = Fixtures.htmlOf [ Fixtures.cardWith "397e8400" Fixtures.buildFailed CardSource.NotReadOnThisCard ]
      html |> Expect.stringContains "the chip names the first error location" "Build failed: Program.fs(143,5)"
      let body = bodyBeforeDisclosure html "session-card-app"
      body.Contains "error FS1093" |> Expect.isFalse "the compiler error is not card body"

    testCase "WHY - the failed rebuild's full sentence survives whole in the disclosure, call to action included" <| fun _ ->
      let html = Fixtures.htmlOf [ Fixtures.cardWith "397e8400" Fixtures.buildFailed CardSource.NotReadOnThisCard ]
      html |> Expect.stringContains "the Core sentence is intact" "could not be rebuilt"
      html |> Expect.stringContains "the compiler reason is intact" "error FS1093"
      html |> Expect.stringContains "the call to action is intact" "press ▶ Run"

    testCase "WHY - a state that is already one line gains no chip and no disclosure: quiet stays quiet" <| fun _ ->
      let running =
        AppRun.AppRunState.Running
          { RunId = "r1"; Project = "/repo/store-falco/store-falco.fsproj"; EntryPoint = "store-falco"
            Endpoint = AppRun.AppEndpoint.NoServer; StartedAt = Fixtures.now }
      let html = Fixtures.htmlOf [ Fixtures.cardWith "397e8400" running CardSource.NotReadOnThisCard ]
      html |> Expect.stringContains "the sentence renders as it always did" (AppRun.describeState running)
      html.Contains "card-state-chip" |> Expect.isFalse "no chip for a one-line state"
  ]

[<Tests>]
let appOutputToolbarTests =
  testList "the app-output toolbar keeps facts in the title's tooltip, not in the bar" [

    testCase "WHY - the count describes the pane, so it belongs on the title's tooltip and not in the bar" <| fun _ ->
      let html = draw paneWithBothLines (HasAppOutput 2) SessionInView
      html |> Expect.stringContains "the tooltip says what opened the pane" "2 lines of app output"
      html.Contains "live-pane-count" |> Expect.isFalse "no count badge competing with the controls"
      html.Contains "live-pane-stamp" |> Expect.isFalse "no stamp restating the count"

    testCase "WHY - a paused pane's tooltip still says what it holds: it adds a fact beyond the count" <| fun _ ->
      let paused = SageFs.Server.AppOutputPane.AppOutputPane.setPaused true paneWithBothLines
      let html = draw paused (HasAppOutput 2) SessionInView
      html |> Expect.stringContains "the tooltip says what is being held" "2 lines while paused"

    testCase "WHY - a filtered pane's tooltip still says it is filtered: it adds a fact, so it stays" <| fun _ ->
      let filtering = SageFs.Server.AppOutputPane.AppOutputPane.setSearch "hello" paneWithBothLines
      let html = draw filtering (HasAppOutput 2) SessionInView
      html |> Expect.stringContains "the tooltip says the view is filtered" "2 lines, filtered"
  ]

[<Tests>]
let wordedControlSizingTests =
  testList "the app-output toolbar's worded controls are sized for their words" [

    testCase "WHY - every control whose visible text is a word carries the label-sizing class, so no label spills onto its neighbour" <| fun _ ->
      let html = draw paneWithBothLines (HasAppOutput 2) SessionInView
      for marker in [ "id=\"app-output-follow\""; "id=\"app-output-pause\""; "id=\"pane-layout-toggle\"" ] do
        let tag = elementTagWith html marker
        tag.Contains "session-btn-labeled" |> Expect.isTrue (sprintf "%s is sized to its label, not left at the 28px square" marker)
      // One picker answers the stream question that three worded buttons used to take three boxes for.
      html |> Expect.stringContains "the stream choice is one picker" "data-testid=\"app-output-stream-select\""

    testCase "WHY - the stylesheet's labeled-button rule sizes to content with the square's height as its floor" <| fun _ ->
      let rule = cssRule ".session-btn-labeled"
      rule.Contains "width: auto" |> Expect.isTrue "the width is the label's"
      rule.Contains "min-width: 28px" |> Expect.isTrue "the floor is the uniform square"
  ]

[<Tests>]
let calmAlarmStripTests =
  testList "the top chrome's alarm strip keeps every fact and stops painting an emergency" [

    testCase "WHY - the strip is a thin rule in the failure token on the panel's own background: a faulted session is a failure, and red means failure only" <| fun _ ->
      let rule = cssRule ".alarm-banner"
      rule.Contains "border-left: 3px solid var(--fg-red" |> Expect.isTrue "the thin rule is the failure token"
      rule.Contains "--bg-panel" |> Expect.isTrue "the strip sits on the panel's own background"
      rule.Contains "color-mix" |> Expect.isFalse "no red tint floods the strip's box"
      rule.Contains "--fg-yellow" |> Expect.isFalse "a failure is not marked in a warning colour"

    testCase "WHY - the alarm title is plain text at the chrome's own volume" <| fun _ ->
      let rule = cssRule ".alarm-title"
      rule.Contains "--fg-red" |> Expect.isFalse "no alarm red on the title"

    testCase "calming the strip removed no information: count, phase, message, timestamp and dismiss all still render" <| fun _ ->
      let alarm = { Phase = "update"; Message = "null ref in render"; Timestamp = DateTimeOffset(2026, 10, 26, 20, 0, 0, TimeSpan.Zero) }
      let html = renderAlarmBanner [ alarm ] |> renderNode
      html |> Expect.stringContains "the count" "System Alarm (1)"
      html |> Expect.stringContains "the phase" "[update]"
      html |> Expect.stringContains "the message" "null ref in render"
      html |> Expect.stringContains "the timestamp" "@ "
      html |> Expect.stringContains "the dismiss" "dismiss"
  ]
