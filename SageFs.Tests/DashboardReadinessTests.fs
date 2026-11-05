/// One reading of "is this session fine", on every surface of the dashboard. Three reviewers looked at the Live Bindings page
/// and scored its honesty of state 3, 3 and 5 out of 10: the header read a green "Ready" and the daemon bar a check mark
/// while the Sessions card beside them said the files were ahead of the build, and the header counted "13 evals" where the
/// card counted 12 for the same session. These cases build the whole page for one session and read the surfaces together, so
/// a surface that goes back to answering for itself fails here.
module SageFs.Tests.DashboardReadinessTests

open System
open System.Threading.Tasks
open Expecto
open Expecto.Flip
open Falco.Markup
open SageFs
open SageFs.Features
open SageFs.Server
open SageFs.Server.DashboardTypes
open SageFs.Server.DashboardFragments
open SageFs.Server.Dashboard
open SageFs.Tests.DashboardRenderGateTests
open SageFs.Tests.DashboardParsingTests

let private rebuildStartedAt = SidebarCards.now

/// A Ready session whose rebuild is in progress. A rebuild answers for itself (no disk is read), so the whole page can be
/// built from typed state with no files on disk.
let private rebuildingSession : WorkerProtocol.SessionInfo =
  { SidebarCards.info "0a2b3c4d" WorkerProtocol.SessionStatus.Ready [ "/w/Fixture.fsproj" ] with
      Rebuild = LastRebuild.Latest (RebuildOutcome.InProgress rebuildStartedAt) }

/// A Ready session with no project roles, so there is no build for it to be behind, and with activity right now, so its
/// display status is Running and not Idle (an idle session's badge is grey by design).
let private currentSession : WorkerProtocol.SessionInfo =
  { SidebarCards.info "0a2b3c4d" WorkerProtocol.SessionStatus.Ready [ "/w/Fixture.fsproj" ] with LastActivity = DateTime.UtcNow }

let private healthWith (session: WorkerProtocol.SessionInfo) : HealthSnapshot =
  { DaemonPid = 1
    DaemonPort = 37749
    Uptime = FixtureDurations.uptimeUnread
    Version = "0.0.0"
    SessionSummaries =
      [ { SessionId = WorkerProtocol.SessionId.value session.Id
          ProjectName = "Fixture.fsproj"
          Status = SessionHealthStatus.Ready
          EvalCount = 12
          LastActivity = DateTimeOffset.UnixEpoch } ]
    LiveTestingSummary = None
    MemoryMB = 257
    Anomalies = []
    GcDumpOutcome = None
    MemoryPressure = SageFs.MemoryPressure.Normal
    SupervisorHealth = SageFs.SupervisorHealth.Healthy
    ComponentFailures = [] }

/// The queries for one session: the worker says it has run `workerEvals`, the daemon's tally says `finishedEvals`.
let private queriesFor (session: WorkerProtocol.SessionInfo) (workerEvals: int) (finishedEvals: int) : DashboardQueries =
  { mkQueries (ref 0) [ session ] with
      GetEvalStats = fun _ -> Task.FromResult { SageFs.Affordances.EvalStats.empty with EvalCount = workerEvals }
      GetSessionEvalCounts = fun () -> Map.ofList [ session.Id, finishedEvals ]
      GetDaemonHealth = fun () -> Some (healthWith session) }

let private pageFor (session: WorkerProtocol.SessionInfo) (workerEvals: int) (finishedEvals: int) : Task<string> =
  task {
    let! snap, _, _, _ =
      buildDashboardSnapshotWithSessions (queriesFor session workerEvals finishedEvals) (mkInfra ()) session.Id session.Id "" "default" None [ session ]
    return renderMainContent snap |> renderNode
  }

let private between (html: string) (start: string) (finish: string) : string =
  let from = html.IndexOf start
  html.Substring(from, html.IndexOf(finish, from) - from)

[<Tests>]
let pageTests =
  testList "the dashboard says one thing about a session's readiness" [

    testTask "WHY — a Ready session whose rebuild is in progress reads the same on the header badge, the daemon bar and the card, and none of them is a plain green Ready" {
      let! html = pageFor rebuildingSession 12 12
      let badge = between html "id=\"session-status\"" "</span>"
      badge |> Expect.stringContains "the header badge says it in words" ">Ready, rebuilding"
      badge |> Expect.stringContains "in the caution colour" "status status-notice"
      badge.Contains "status-ready" |> Expect.isFalse "and not in green"
      let bar = between html "id=\"daemon-health\"" "</div>"
      bar |> Expect.stringContains "the daemon bar names the session and says it in the same words" "Fixture.fsproj [Ready, rebuilding]"
      bar.Contains "✅ Fixture.fsproj" |> Expect.isFalse "the bar's check mark is gone from the session"
      let card = between html "data-testid=\"session-card-source\"" "session-card-actions"
      card |> Expect.stringContains "the card says the same fact" "Rebuilding"
      card |> Expect.stringContains "and carries the same readiness words for a journey to read" "data-readiness=\"Ready, rebuilding\""
    }

    testTask "WHY — a Ready session with a current build still reads a plain green Ready everywhere, so the warning is not noise" {
      let! html = pageFor currentSession 12 12
      let badge = between html "id=\"session-status\"" "</span>"
      badge |> Expect.stringContains "the words clients wait on" ">Ready"
      badge.Contains "Ready," |> Expect.isFalse "and nothing added to them"
      badge |> Expect.stringContains "green" "status-ready"
      html.Contains "session-card-source" |> Expect.isFalse "the card says nothing about a build that is current"
      html.Contains "statusline-readiness" |> Expect.isFalse "and so does the statusline"
    }

    testTask "WHY — the header counts the evals the card counts, because the worker's tally said 13 while the card said 12 for the same session" {
      let! html = pageFor currentSession 13 12
      html |> Expect.stringContains "the card" "evals: 12"
      html |> Expect.stringContains "the header" "12 evals"
      html.Contains "13 evals" |> Expect.isFalse "the worker's own tally is not printed beside the card's"
    }

    testTask "WHY — the header and the card agree over every pair of tallies, the worker's and the daemon's" {
      for worker in [ 0; 1; 12; 13; 40 ] do
        for finished in [ 1; 12; 13; 40 ] do
          let! html = pageFor currentSession worker finished
          html |> Expect.stringContains (sprintf "worker %d, daemon %d: the card" worker finished) (sprintf "evals: %d" finished)
          html |> Expect.stringContains (sprintf "worker %d, daemon %d: the header" worker finished) (sprintf "%d evals" finished)
    }
  ]

[<Tests>]
let statuslineTests =
  testList "the statusline repeats a Ready that is not plain, and only that" [

    testCase "WHY — a Ready session running older code says so in the statusline, in the badge's words and tone" <| fun _ ->
      let html = renderStatuslineLeft Readiness.ReadyOlderCode "/w" |> renderNode
      html |> Expect.stringContains "the words" ">Ready, running older code<"
      html |> Expect.stringContains "the caution tone" "status-notice"
      html |> Expect.stringContains "the working directory is still there" "<div id=\"statusline-file\" class=\"statusline-file\">/w</div>"

    testCase "WHY — a plain Ready, and any other state, add nothing to the statusline, because the header badge already says it" <| fun _ ->
      for readiness in [ Readiness.ReadyCurrent; Readiness.InState SessionState.Faulted; Readiness.InState SessionState.WarmingUp ] do
        renderStatuslineLeft readiness "/w" |> renderNode
        |> fun html -> html.Contains "statusline-readiness" |> Expect.isFalse (sprintf "%A adds no chip" readiness)

    testCase "WHY — the statusline and the badge use the same words for every readiness" <| fun _ ->
      for readiness in [ Readiness.ReadyOlderCode; Readiness.ReadyRebuilding; Readiness.ReadyReplBehindApp; Readiness.ReadyUnchecked ] do
        let view = SessionInView.InPlay(SessionState.Ready, SessionDisplayStatus.Running, readiness)
        renderStatuslineLeft readiness "/w" |> renderNode
        |> Expect.stringContains (sprintf "%A" readiness) (sprintf ">%s<" (SessionInView.badgeLabel view))
  ]

[<Tests>]
let daemonBarTests =
  let summary : SessionHealthSummary =
    { SessionId = "0a2b3c4d"; ProjectName = "Fixture.fsproj"; Status = SessionHealthStatus.Ready; EvalCount = 1; LastActivity = DateTimeOffset.UnixEpoch }
  let barFor (readinessOf: Map<string, Readiness>) (summaries: SessionHealthSummary list) =
    let snapshot = { healthWith currentSession with SessionSummaries = summaries }
    renderDaemonHealthWith readinessOf (DaemonHealthView.fromSnapshot DateTimeOffset.UnixEpoch snapshot) |> renderNode
  testList "the daemon health bar" [

    testCase "WHY — a session the caller read a build for reads as its readiness, with the caution glyph, and not as a check mark" <| fun _ ->
      let html = barFor (Map.ofList [ "0a2b3c4d", Readiness.ReadyOlderCode ]) [ summary ]
      html |> Expect.stringContains "words and glyph" "⚠️ Fixture.fsproj [Ready, running older code]"

    testCase "WHY — a session nobody read a build for reads as the snapshot says, which is what the bar always did" <| fun _ ->
      barFor Map.empty [ summary ] |> Expect.stringContains "unchanged" "✅ Fixture.fsproj [Ready]"

    testCase "WHY — with several sessions, one that is not plainly ready names itself in words, and 'all ready' is not claimed" <| fun _ ->
      let others = [ for i in 1 .. 3 -> { summary with SessionId = sprintf "0a2b3c4%d" i; ProjectName = sprintf "P%d.fsproj" i } ]
      let html = barFor (Map.ofList [ "0a2b3c4d", Readiness.ReadyOlderCode ]) (summary :: others)
      html.Contains "all ready" |> Expect.isFalse "four sessions, one running older code"
      html |> Expect.stringContains "the session is named" "Fixture.fsproj"
      let two = barFor (Map.ofList [ "0a2b3c4d", Readiness.ReadyOlderCode ]) [ summary; others.Head ]
      two |> Expect.stringContains "a few sessions say the words beside the glyph" "Fixture.fsproj (Ready, running older code)"
  ]

[<Tests>]
let helperTests =
  testList "viewed readiness and the header's eval count" [

    testCase "WHY — a viewed session that has no card is judged by its state alone, and a Ready one is never a plain Ready" <| fun _ ->
      viewedReadiness SessionState.Ready currentSession.Id []
      |> Expect.equal "unchecked" Readiness.ReadyUnchecked

    testCase "WHY — a viewed card whose source was not read is not read as current" <| fun _ ->
      let card = SidebarCards.card currentSession
      viewedReadiness SessionState.Ready currentSession.Id [ card ]
      |> Expect.equal "the card says it did not read the source" Readiness.ReadyUnchecked

    testCase "WHY — a viewed card with a current source and a level REPL is a plain Ready; with a stale source it is older code" <| fun _ ->
      let card = SidebarCards.card currentSession
      viewedReadiness SessionState.Ready currentSession.Id [ { card with Source = CardSource.Read (SourceState.InSync (rebuildStartedAt, 3)) } ]
      |> Expect.equal "current" Readiness.ReadyCurrent
      let stale = SourceState.Stale [ { Path = "/w/A.fs"; Because = StaleBecause.EditedAfterBuild (rebuildStartedAt.AddMinutes 1.0, rebuildStartedAt) } ]
      viewedReadiness SessionState.Ready currentSession.Id [ { card with Source = CardSource.Read stale } ]
      |> Expect.equal "older code" Readiness.ReadyOlderCode

    testCase "WHY — the header takes the viewed card's eval count and keeps the worker's timings" <| fun _ ->
      let card = { SidebarCards.card currentSession with EvalCount = 12 }
      let stats : EvalStatsView = { FixtureStats.noEvals with Count = 13; Sparkline = "▁▂"; P50Ms = Some 3.0 }
      let agreed = HeaderEvals.agreeWithCard currentSession.Id [ card ] stats
      agreed.Count |> Expect.equal "the card's count" 12
      { agreed with Count = 13 } |> Expect.equal "everything else is the worker's" stats

    testCase "WHY — a viewed session with no card keeps the worker's count, because there is no card to disagree with" <| fun _ ->
      let stats : EvalStatsView = { FixtureStats.noEvals with Count = 13 }
      HeaderEvals.agreeWithCard currentSession.Id [] stats |> Expect.equal "unchanged" stats
  ]
