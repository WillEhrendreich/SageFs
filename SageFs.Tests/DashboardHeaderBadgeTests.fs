/// What the header's one status badge says, and what the main area says when the session in view faulted. The badge was
/// green whatever the session was doing, because its class came from the connection state, which the server only ever
/// built as Connected; and the main area of a faulted session said only "Faulted", with the reason and the way out on a
/// sidebar card the person was not reading.
module SageFs.Tests.DashboardHeaderBadgeTests

open Expecto
open Expecto.Flip
open Falco.Markup
open SageFs
open SageFs.Server.DashboardTypes
open SageFs.Server.DashboardFragments

let private inPlay (state: SessionState) (display: SessionDisplayStatus) : SessionInView =
  SessionInView.InPlay(state, display)

/// Every way a session in view can be, with the class its badge must carry.
let private classByStatus : (string * SessionInView * string) list =
  [ "running", inPlay SessionState.Ready SessionDisplayStatus.Running, "status-ready"
    "idle", inPlay SessionState.Ready SessionDisplayStatus.Idle, "status-idle"
    "starting", inPlay SessionState.WarmingUp SessionDisplayStatus.Starting, "status-warming"
    "restarting", inPlay SessionState.WarmingUp SessionDisplayStatus.Restarting, "status-warming"
    "faulted", inPlay SessionState.Faulted (SessionDisplayStatus.Faulted "the project did not build"), "status-faulted"
    "lost", inPlay SessionState.Uninitialized SessionDisplayStatus.Lost, "status-faulted"
    "stopped", inPlay SessionState.Faulted SessionDisplayStatus.Stopped, "status-faulted"
    "no session in play", SessionInView.NoSessionInPlay, "status-idle" ]

let private render (node: XmlNode) : string = renderNode node

/// A whole snapshot whose only interesting part is the session in view.
let private snapshotOf (view: SessionInView) : DashboardSnapshot =
  let empty = Elem.div [] []
  { DashboardSnapshot.Version = "0.0.0"
    InView = view; SessionId = (match view with SessionInView.NoSessionInPlay -> "" | SessionInView.InPlay _ -> "0a2b3c4d")
    WorkingDir = "/work"; WarmupProgress = ""; WorkflowLabel = "REPL"; EvalStats = FixtureStats.noEvals
    ThemeName = "default"; ConnectionLabel = None
    HotReloadPanel = empty; SessionContextPanel = empty; OutputPanel = empty
    SessionsPanel = empty; SessionPicker = empty; ThemePicker = empty; ThemeVars = empty
    BindingsPanel = empty; DaemonHealth = empty; FailureNarrativesPanel = empty; DiagnosticsPanel = empty
    FilmstripPanel = empty; AlarmPanel = empty; LiveTestingPanel = empty; FrictionPanel = empty
    CohortPanel = empty; HygienePanel = empty
    ActiveProject = None; ProjectRoles = []
    App = AppRun.AppRunState.NotRunning
    EvalToPixelP50Ms = None; EvalToPixelP99Ms = None }

/// The header's status badge as the page draws it: its element, from the tag to the closing span.
let private headerBadge (html: string) : string =
  let anchor = html.IndexOf "id=\"session-status\""
  let start = html.IndexOf("<span", anchor)
  html.Substring(start, html.IndexOf("</span>", start) - start)

[<Tests>]
let tests =
  testList "Dashboard header badge and fault notice" [

    testCase "WHY — the header badge takes its colour from the session's display status, because a Faulted, Starting or Lost session read green beside the word that said otherwise" <| fun _ ->
      for name, view, expected in classByStatus do
        SessionInView.badgeCssClass view
        |> Expect.equal (sprintf "the badge for %s" name) expected

    testCase "green is only for a session that is running, never for one that is not" <| fun _ ->
      let green =
        classByStatus
        |> List.filter (fun (_, view, _) -> SessionInView.badgeCssClass view = "status-ready")
        |> List.map (fun (name, _, _) -> name)
      green |> Expect.equal "only the running session is green" [ "running" ]

    testCase "WHY — the badge keeps the words clients wait on, because journeys and scripts read #session-status for \"Ready\"" <| fun _ ->
      let labels =
        [ inPlay SessionState.Ready SessionDisplayStatus.Running, "Ready"
          inPlay SessionState.Ready SessionDisplayStatus.Idle, "Ready"
          inPlay SessionState.Evaluating SessionDisplayStatus.Running, "Evaluating"
          inPlay SessionState.WarmingUp SessionDisplayStatus.Starting, "WarmingUp"
          inPlay SessionState.Faulted (SessionDisplayStatus.Faulted "boom"), "Faulted"
          inPlay SessionState.Uninitialized SessionDisplayStatus.Lost, "Uninitialized"
          SessionInView.NoSessionInPlay, "No session" ]
      for view, expected in labels do
        SessionInView.badgeLabel view |> Expect.equal (sprintf "the label for %A" view) expected

    testCase "WHY — a faulted session's main area says why and what to do, because the reason and the remedy sat on a sidebar card" <| fun _ ->
      let html =
        renderSessionFaultNotice (inPlay SessionState.Faulted (SessionDisplayStatus.Faulted "NuGet restore failed for SageFs.Core.fsproj"))
        |> render
      html |> Expect.stringContains "it says the session faulted" "faulted"
      html |> Expect.stringContains "it says why" "NuGet restore failed for SageFs.Core.fsproj"
      html |> Expect.stringContains "it says how to get out: stop it, then start a new one" "Stop it"
      html |> Expect.stringContains "it is announced, because the person may not be looking at the card" "role=\"alert\""

    testCase "the reason is text, never markup, because a fault message can carry a path or an error with angle brackets in it" <| fun _ ->
      let html =
        renderSessionFaultNotice (inPlay SessionState.Faulted (SessionDisplayStatus.Faulted "<script>alert(1)</script>"))
        |> render
      html.Contains "<script>" |> Expect.isFalse "no raw script tag reaches the page"

    testCase "WHY — the page draws the header badge and the fault notice from the snapshot's session in view, in every state of the matrix" <| fun _ ->
      let faulted = inPlay SessionState.Faulted (SessionDisplayStatus.Faulted "the project did not build")
      let page (view: SessionInView) = renderMainContent (snapshotOf view) |> render
      let badge (view: SessionInView) = page view |> headerBadge

      badge faulted |> Expect.stringContains "a faulted session's badge is red" "status status-faulted"
      badge faulted |> Expect.stringContains "and says Faulted" ">Faulted"
      page faulted |> Expect.stringContains "and the main area says why" "the project did not build"
      page faulted |> Expect.stringContains "and what to do" "session-fault-remedy"

      badge (inPlay SessionState.WarmingUp SessionDisplayStatus.Starting)
      |> Expect.stringContains "a warming session's badge is the warming colour" "status status-warming"
      (page (inPlay SessionState.WarmingUp SessionDisplayStatus.Starting)).Contains "session-fault-notice"
      |> Expect.isFalse "a warming session has no fault notice"

      badge FixtureViews.running |> Expect.stringContains "a running session's badge is green" "status status-ready"
      badge FixtureViews.running |> Expect.stringContains "and says Ready" ">Ready"

      badge SessionInView.NoSessionInPlay |> Expect.stringContains "no session in play says so" ">No session"
      badge SessionInView.NoSessionInPlay |> Expect.stringContains "and is not green" "status status-idle"
      let landing = page SessionInView.NoSessionInPlay
      landing |> Expect.stringContains "the shell is whole with no session: the header is there" "app-header"
      landing |> Expect.stringContains "and so is the sidebar" "id=\"sidebar\""

    testCase "a session that is not faulted gets no notice, and neither does no session at all" <| fun _ ->
      for view in
        [ inPlay SessionState.Ready SessionDisplayStatus.Running
          inPlay SessionState.WarmingUp SessionDisplayStatus.Starting
          inPlay SessionState.Faulted SessionDisplayStatus.Stopped
          SessionInView.NoSessionInPlay ] do
        renderSessionFaultNotice view |> render |> Expect.equal (sprintf "nothing for %A" view) ""
  ]
