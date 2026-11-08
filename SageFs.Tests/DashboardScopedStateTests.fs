/// One scoped state on every surface. Three reviewers read "Healthy" beside "[Ready, changes not live]" as a contradiction (the
/// first word was the daemon's and the second the session's, and nothing said so), found the remedy in a Sessions card that is a
/// strip at narrow widths, far from the Live bindings pane where the warning is felt, and saw the card say a green "running"
/// beside "Changes not live". These cases read the surfaces together: green means the daemon, amber means the session, every
/// surface words one state one way, and the remedy appears beside the warning exactly when the card offers it.
module SageFs.Tests.DashboardScopedStateTests

open System
open Expecto
open Expecto.Flip
open Falco.Markup
open SageFs
open SageFs.Features
open SageFs.Server
open SageFs.Server.DashboardTypes
open SageFs.Server.DashboardFragments
open SageFs.Server.Dashboard
open SageFs.Tests.DashboardParsingTests
open SageFs.Tests.DashboardRenderGateTests

let private startedAt = SidebarCards.now

let private currentSession : WorkerProtocol.SessionInfo =
  { SidebarCards.info "0a2b3c4d" WorkerProtocol.SessionStatus.Ready [ "/w/Fixture.fsproj" ] with LastActivity = DateTime.UtcNow }

let private sessionId = WorkerProtocol.SessionId.value currentSession.Id

let private between (html: string) (start: string) (finish: string) : string =
  let from = html.IndexOf start
  html.Substring(from, html.IndexOf(finish, from) - from)

let private staleSource =
  SourceState.Stale [ { Path = "/w/Tuning.fs"; Because = StaleBecause.EditedAfterBuild (startedAt.AddMinutes 5.0, startedAt) } ]

let private staleCard (status: SessionDisplayStatus) : ParsedSession =
  { SidebarCards.card currentSession with Status = status; Source = CardSource.Read staleSource; WorkingDir = "/w" }

let private snapshotWith (readiness: Readiness) (cards: ParsedSession list) : DashboardSnapshot =
  let empty = Elem.div [] []
  { DashboardSnapshot.Version = "0.0.0"
    InView = SessionInView.InPlay(SessionState.Ready, SessionDisplayStatus.Running, readiness)
    SessionId = sessionId
    WorkingDir = "/w"; WarmupProgress = ""; WorkflowLabel = "REPL"; EvalStats = FixtureStats.noEvals
    ThemeName = "default"; ConnectionLabel = None
    HotReloadPanel = empty; SessionContextPanel = empty; OutputPanel = empty
    SessionsPanel = renderSessionsForSession sessionId cards false
    SessionPicker = empty; ThemePicker = empty; ThemeVars = empty
    BindingsPanel = empty; DaemonHealth = empty; FailureNarrativesPanel = empty; DiagnosticsPanel = empty
    FilmstripPanel = empty; AlarmPanel = empty; LiveTestingPanel = empty; FrictionPanel = empty
    CohortPanel = empty; HygienePanel = empty
    ActiveProject = None; ProjectRoles = []
    App = AppRun.AppRunState.NotRunning
    EvalToPixelP50Ms = None; EvalToPixelP99Ms = None }

let private healthSnapshot (summaries: SessionHealthSummary list) : HealthSnapshot =
  { DaemonPid = 1
    DaemonPort = 37749
    Uptime = FixtureDurations.uptimeUnread
    Version = "0.0.0"
    SessionSummaries = summaries
    LiveTestingSummary = None
    MemoryMB = 257
    Anomalies = []
    ComponentFailures = []
    GcDumpOutcome = None
    MemoryPressure = SageFs.MemoryPressure.Normal
    SupervisorHealth = SageFs.SupervisorHealth.Healthy }

let private fixtureSummary : SessionHealthSummary =
  { SessionId = sessionId; ProjectName = "Fixture.fsproj"; Status = SessionHealthStatus.Ready; EvalCount = 1; LastActivity = DateTimeOffset.UnixEpoch }

/// The whole page for one session: its registry record, the state the daemon reports for it, and the health summaries the Elm
/// model holds (which may disagree with the registry for a moment).
let private pageWith (info: WorkerProtocol.SessionInfo) (state: SessionState) (summaries: SessionHealthSummary list) : System.Threading.Tasks.Task<string> =
  task {
    let queries =
      { mkQueries (ref 0) [ info ] with
          GetSessionState = fun _ -> state
          GetDaemonHealth = fun () -> Some (healthSnapshot summaries) }
    let! built = buildDashboardSnapshotWithSessions (SageFs.Tests.DashboardSnapshotTests.snapshotInput queries (mkInfra ()) info.Id info.Id None [ info ] SageFs.Server.DashboardPanelContext.FrictionPanelOptIn.NotOptedIn)
    return renderMainContent built.Snapshot |> renderNode
  }

[<Tests>]
let tests =
  testList "one scoped state on every surface" [

    testCase "WHY — a viewed card that read a stale build says so in its badge, in the header's words and tone, not a green 'running'" <| fun _ ->
      let badge = CardBadge.ofCard (staleCard SessionDisplayStatus.Running)
      badge.Words |> Expect.equal "the header's words" "Ready, changes not live"
      badge.CssClass |> Expect.equal "the caution tone" "status-notice"

    testCase "WHY — an idle session's badge reads the same way, because idle is a Ready session nobody has used lately" <| fun _ ->
      (CardBadge.ofCard (staleCard SessionDisplayStatus.Idle)).Words |> Expect.equal "words" "Ready, changes not live"

    testCase "WHY — a viewed card with a current build says the header's word, Ready, in the card's own green" <| fun _ ->
      let current = { staleCard SessionDisplayStatus.Running with Source = CardSource.Read (SourceState.InSync (startedAt, 3)) }
      CardBadge.ofCard current |> Expect.equal "current" { Words = "Ready"; CssClass = "status-ready" }

    testCase "WHY — a viewed idle session keeps the quiet grey class and says Ready, like the header" <| fun _ ->
      let idle = { staleCard SessionDisplayStatus.Idle with Source = CardSource.Read (SourceState.InSync (startedAt, 3)) }
      CardBadge.ofCard idle |> Expect.equal "idle" { Words = "Ready"; CssClass = "status-idle" }

    testCase "WHY — a card that read nothing keeps its own badge, because it has no reading to join" <| fun _ ->
      let unread = { staleCard SessionDisplayStatus.Running with Source = CardSource.NotReadOnThisCard }
      CardBadge.ofCard unread |> Expect.equal "not read" { Words = "running"; CssClass = "status-ready" }

    testCase "WHY — a viewed card that is warming up says the header's words, 'Warming up', and keeps the warming colour" <| fun _ ->
      for status in [ SessionDisplayStatus.Starting; SessionDisplayStatus.Restarting ] do
        (CardBadge.ofCard (staleCard status)) |> Expect.equal (sprintf "%A" status) { Words = "Warming up"; CssClass = "status-warming" }

    testCase "WHY — a faulted, lost or stopped card keeps its own words" <| fun _ ->
      for status in [ SessionDisplayStatus.Faulted "boom"; SessionDisplayStatus.Lost; SessionDisplayStatus.Stopped ] do
        (CardBadge.ofCard (staleCard status)).Words |> Expect.equal (sprintf "%A" status) (SessionDisplayStatus.label status)

    testCase "WHY — the registry's reading of a session wins over the Elm model's, because the model lags it at every transition" <| fun _ ->
      SessionDisplayStatus.reconcile (Some SessionDisplayStatus.Running) SessionDisplayStatus.Starting |> Expect.equal "registry Ready" SessionDisplayStatus.Running
      SessionDisplayStatus.reconcile (Some SessionDisplayStatus.Starting) SessionDisplayStatus.Running |> Expect.equal "registry warming" SessionDisplayStatus.Starting
      SessionDisplayStatus.reconcile None SessionDisplayStatus.Starting |> Expect.equal "no registry record: the model's reading" SessionDisplayStatus.Starting

    testCase "WHY — every display status has one health status, so the summaries cannot invent a seventh" <| fun _ ->
      [ SessionDisplayStatus.Running; SessionDisplayStatus.Idle; SessionDisplayStatus.Starting; SessionDisplayStatus.Restarting
        SessionDisplayStatus.Faulted "x"; SessionDisplayStatus.Lost; SessionDisplayStatus.Stopped ]
      |> List.map SessionDisplayStatus.healthStatus
      |> Expect.equal "the table"
           [ SessionHealthStatus.Ready; SessionHealthStatus.Ready; SessionHealthStatus.WarmingUp; SessionHealthStatus.WarmingUp
             SessionHealthStatus.Faulted; SessionHealthStatus.Faulted; SessionHealthStatus.Stopped ]

    testCase "WHY — the rendered card carries the badge's words, so the Sessions card no longer says running in green beside the warning" <| fun _ ->
      let html = renderSessionsForSession sessionId [ staleCard SessionDisplayStatus.Running ] false |> renderNode
      let badge = between html "class=\"status badge" "</span>"
      badge |> Expect.stringContains "the words" "Ready, changes not live"
      badge |> Expect.stringContains "the tone" "status-notice"
      badge.Contains "status-ready" |> Expect.isFalse "not green"

    testCase "WHY — the pane's note and the card's button read one decision, so they appear together over every source and every viewing" <| fun _ ->
      let sources =
        [ CardSource.NotReadOnThisCard
          CardSource.Read (SourceState.InSync (startedAt, 3))
          CardSource.Read staleSource
          CardSource.Read (SourceState.Rebuilding startedAt)
          CardSource.Read (SourceState.Unknown UnknownReason.NotAssessed) ]
      for source in sources do
        for viewing in [ true; false ] do
          let card = { staleCard SessionDisplayStatus.Running with Source = source }
          let html = renderSessionsForSession (match viewing with | true -> sessionId | false -> "") [ card ] false |> renderNode
          let paneNote = RebuildRemedy.paneNote (viewedRemedy card.Id [ card ]) |> renderNode
          html.Contains "session-card-rebuild"
          |> Expect.equal (sprintf "%A viewing=%b: the card's button is the pane's note, when viewed" source viewing) (viewing && paneNote.Contains "live-pane-rebuild")

    testCase "WHY — a stale source offers the rebuild on both, so the test above is not agreeing about nothing" <| fun _ ->
      viewedRemedy currentSession.Id [ staleCard SessionDisplayStatus.Running ] |> Expect.equal "the pane offers it" Remedy.RebuildAndReload
      renderSessionsForSession sessionId [ staleCard SessionDisplayStatus.Running ] false |> renderNode
      |> Expect.stringContains "and so does the viewed card" "session-card-rebuild"

    testCase "WHY — a session with no card has no remedy to offer" <| fun _ ->
      viewedRemedy currentSession.Id [] |> Expect.equal "nothing" Remedy.NothingToDo

    testCase "WHY — the pane's note says what the state means for the values below it, not the session's state word a fourth time, and carries the same hard reset route, with its own test ids" <| fun _ ->
      let html = RebuildRemedy.paneNote Remedy.RebuildAndReload |> renderNode
      html |> Expect.stringContains "its test id" "data-testid=\"live-pane-stale\""
      html |> Expect.stringContains "the words are the pane's own" (sprintf ">%s<" (Remedy.paneWords Remedy.RebuildAndReload))
      Remedy.paneWords Remedy.RebuildAndReload |> Expect.stringContains "they are about the values the pane shows" "values"
      Remedy.paneWords Remedy.RebuildAndReload |> Expect.notEqual "the session's own phrase is said on the card, the header and the strip, not here" (Remedy.headline Remedy.RebuildAndReload)
      html |> Expect.stringContains "the button's test id" "data-testid=\"live-pane-rebuild\""
      html |> Expect.stringContains "the same route" "@post('/dashboard/hard-reset')"
      html |> Expect.stringContains "the same button words" "Rebuild and reload"
      RebuildRemedy.paneNote Remedy.NothingToDo |> renderNode |> Expect.equal "nothing when there is nothing to do" ""

    testCase "WHY — the pane bar carries the note the caller hands it, inside the bar, so the remedy sits where the warning is felt" <| fun _ ->
      let look = SageFs.View.recommended SageFs.ViewWorkflow.Repl
      let none = SageFs.Features.Tweak.BindingTweak.TweakView.none
      let html =
        LiveBindingsDock.renderDockWithRemedy (RebuildRemedy.paneNote Remedy.RebuildAndReload) look None none DockPanes.SessionInView sessionId LiveBindingsDock.NothingBound None
        |> renderNode
      let bar = between html "class=\"live-pane-bar\"" "id=\"live-filter\""
      bar |> Expect.stringContains "the note is in the bar" "live-pane-rebuild"
      LiveBindingsDock.renderDockWith look None none DockPanes.SessionInView sessionId LiveBindingsDock.NothingBound None
      |> renderNode
      |> fun without -> without.Contains "live-pane-rebuild" |> Expect.isFalse "a dock with no remedy draws none"

    testCase "WHY — the collapsed Sessions strip shows a word for a card that has something to say, not only a colour" <| fun _ ->
      let html = renderMainContent (snapshotWith Readiness.ReadyOlderCode [ staleCard SessionDisplayStatus.Running ]) |> renderNode
      let header = between html "class=\"sidebar-header\"" "id=\"sidebar-toggle-btn\""
      header |> Expect.stringContains "the marker is in the strip's header" "data-testid=\"sessions-stale-marker\""
      header |> Expect.stringContains "and says the phrase" "Changes not live"

    testCase "WHY — the strip says nothing for a session with nothing to say" <| fun _ ->
      let html = renderMainContent (snapshotWith Readiness.ReadyCurrent []) |> renderNode
      html.Contains "sessions-stale-marker" |> Expect.isFalse "no marker"

    testCase "WHY — the strip's marker uses the readiness's short phrase for every qualified readiness" <| fun _ ->
      for readiness in [ Readiness.ReadyOlderCode; Readiness.ReadyRebuilding; Readiness.ReadyReplBehindApp; Readiness.ReadyUnchecked ] do
        let html = renderMainContent (snapshotWith readiness []) |> renderNode
        let phrase = Readiness.brief readiness |> Option.defaultValue "no phrase"
        html |> Expect.stringContains (sprintf "%A" readiness) phrase

    testCase "WHY — the daemon bar scopes its words: 'Daemon healthy' is the daemon's, and the session's readiness is named as the session's" <| fun _ ->
      let view = DaemonHealthView.fromSnapshot DateTimeOffset.UnixEpoch (healthSnapshot [ fixtureSummary ])
      let html = renderDaemonHealthWith (Map.ofList [ sessionId, Readiness.ReadyOlderCode ]) view |> renderNode
      html |> Expect.stringContains "the daemon's word" "Daemon healthy"
      html |> Expect.stringContains "the session's words" "Session Fixture.fsproj: Ready, changes not live"
      html.Contains "Healthy ·" |> Expect.isFalse "a bare 'Healthy' no longer sits beside the warning"

    testCase "WHY — a daemon with no session still says whose health the glyph is, in a word" <| fun _ ->
      renderDaemonHealth (DaemonHealthView.fromSnapshot DateTimeOffset.UnixEpoch (healthSnapshot []))
      |> renderNode
      |> Expect.stringContains "a word beside the glyph" "Daemon healthy"

    testCase "WHY — the card's working directory breaks after each slash, so a long path wraps at its separators and not in the middle of a word" <| fun _ ->
      let dir = "/home/will/Work/SageFs/SageFs.Tests/fixtures/TweakFixture/.runs/shots"
      let html = renderSessionsForSession "" [ { staleCard SessionDisplayStatus.Running with WorkingDir = dir } ] false |> renderNode
      let row = between html "class=\"session-dir\"" "</div>"
      row |> Expect.stringContains "a break opportunity after a separator" "/<wbr>"
      row.Replace("<wbr>", "") |> Expect.stringContains "and the path is all there, in order" dir
      row |> Expect.stringContains "the full path is the tooltip" (sprintf "title=\"%s\"" dir)

    testCase "WHY — the card's working directory is escaped, because a directory name is the user's own text" <| fun _ ->
      let html = renderSessionsForSession "" [ { staleCard SessionDisplayStatus.Running with WorkingDir = "/w/<script>alert(1)</script>/x" } ] false |> renderNode
      html.Contains "<script>alert(1)" |> Expect.isFalse "never raw"

    testTask "WHY — in the seconds after a session is created, the badge, the health bar chip and the card say one word and one tone, even when the Elm model's summary still says Warming Up" {
      // The registry says the worker is Ready. The health summary (built from the Elm model, which lags the registry) still says
      // WarmingUp. Before, the badge read a green Ready over a bar reading "Warming Up".
      let info = { currentSession with Freshness = ReplFreshness.InSync }
      let summary = { fixtureSummary with Status = SessionHealthStatus.WarmingUp }
      let! html = pageWith info SessionState.Ready [ summary ]
      let badge = between html "id=\"session-status\"" "</span>"
      badge |> Expect.stringContains "the header says Ready" ">Ready"
      let bar = between html "id=\"daemon-health\"" "</div>"
      bar |> Expect.stringContains "the bar says the same word, scoped to the session" "Session Fixture.fsproj: Ready"
      bar.Contains "Warming" |> Expect.isFalse "and not Warming Up"
      let card = between html "class=\"status badge" "</span>"
      card |> Expect.stringContains "the card says the same word" ">Ready"
      badge |> Expect.stringContains "in the same tone as the card" "status-ready"
      card |> Expect.stringContains "the card's tone" "status-ready"
    }

    testTask "WHY — a session that really is warming up reads 'Warming up' on the badge, the bar and the card, in the warming tone, even when the summary says Ready" {
      let info = SidebarCards.info "0a2b3c4d" WorkerProtocol.SessionStatus.Starting [ "/w/Fixture.fsproj" ]
      let summary = { fixtureSummary with Status = SessionHealthStatus.Ready }
      let! html = pageWith info SessionState.WarmingUp [ summary ]
      let badge = between html "id=\"session-status\"" "</span>"
      badge |> Expect.stringContains "the header" ">Warming up"
      badge |> Expect.stringContains "the header's tone" "status-warming"
      let bar = between html "id=\"daemon-health\"" "</div>"
      bar |> Expect.stringContains "the bar" "Session Fixture.fsproj: Warming up"
      let card = between html "class=\"status badge" "</span>"
      card |> Expect.stringContains "the card" ">Warming up"
      card |> Expect.stringContains "the card's tone" "status-warming"
    }
  ]
