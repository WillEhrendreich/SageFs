module SageFs.Tests.DashboardHealthTests

open System
open Expecto
open Expecto.Flip
open Falco.Markup
open SageFs
open SageFs.Features
open SageFs.Server
open SageFs.Server.DashboardTypes

/// An uptime no assertion in this file depends on: it only has to be a daemon that is not brand new.
let private unremarkableUptime = FixtureDurations.uptimeUnread

let private makeHealthSnapshot
  (sessions: SessionHealthSummary list)
  (liveTests: LiveTestHealthSummary option)
  (uptime: TimeSpan)
  (memoryMB: int)
  : HealthSnapshot =
  { DaemonPid = 12345
    DaemonPort = 37749
    Uptime = uptime
    Version = "0.6.43"
    SessionSummaries = sessions
    LiveTestingSummary = liveTests
    MemoryMB = memoryMB
    Anomalies = []
    GcDumpOutcome = None
    MemoryPressure = SageFs.MemoryPressure.Normal
    SupervisorHealth = SageFs.SupervisorHealth.Healthy }

/// The moment every case's daemon started. Fixed, because the bar renders it and never the elapsed time.
let private startedAt = DateTimeOffset.UnixEpoch

let private viewOf (snap: HealthSnapshot) = DashboardTypes.DaemonHealthView.fromSnapshot startedAt snap

let private defaultSnap () =
  makeHealthSnapshot [] None unremarkableUptime 128

let private makeSession id proj status evalCount : SessionHealthSummary =
  { SessionId = id
    ProjectName = proj
    Status = status
    EvalCount = evalCount
    LastActivity = DateTimeOffset.UtcNow }

[<Tests>]
let daemonHealthViewTests =
  testList "DaemonHealthView type" [

    testCase "fromSnapshot populates Version" <| fun () ->
      let snap = defaultSnap ()
      let view = viewOf snap
      view.Version |> Expect.equal "version round-trips" "0.6.43"

    testCase "fromSnapshot populates MemoryMB" <| fun () ->
      let snap = makeHealthSnapshot [] None unremarkableUptime 256
      let view = viewOf snap
      view.MemoryMB |> Expect.equal "memory MB round-trips" 256

    testCase "fromSnapshot carries the moment the daemon started, not an uptime label" <| fun () ->
      let snap = makeHealthSnapshot [] None FixtureDurations.uptimeHoursLabel 128
      let view = viewOf snap
      view.StartedAt |> Expect.equal "the start the caller gave, whatever the sampled uptime" (Since.ofOffset startedAt)

    testCase "fromSnapshot returns Healthy when sessions are ready" <| fun () ->
      let s = makeSession "abc" "MyProject.fsproj" SessionHealthStatus.Ready 10
      let snap = makeHealthSnapshot [s] None unremarkableUptime 128
      let view = viewOf snap
      view.OverallHealth |> Expect.equal "healthy with ready session" OverallHealth.Healthy

    testCase "fromSnapshot returns Degraded when session is faulted" <| fun () ->
      let s = makeSession "abc" "MyProject.fsproj" SessionHealthStatus.Faulted 0
      let snap = makeHealthSnapshot [s] None unremarkableUptime 128
      let view = viewOf snap
      view.OverallHealth |> Expect.equal "degraded with faulted session" OverallHealth.Degraded

    testCase "fromSnapshot returns Healthy when no sessions (idle)" <| fun () ->
      let snap = defaultSnap ()
      let view = viewOf snap
      view.OverallHealth |> Expect.equal "healthy when idle (no sessions)" OverallHealth.Healthy

    testCase "fromSnapshot populates SessionCount" <| fun () ->
      let sessions =
        [ makeSession "s1" "Proj1" SessionHealthStatus.Ready 5
          makeSession "s2" "Proj2" SessionHealthStatus.WarmingUp 0 ]
      let snap = makeHealthSnapshot sessions None unremarkableUptime 128
      let view = viewOf snap
      view.SessionCount |> Expect.equal "session count matches" 2

    testCase "fromSnapshot populates SessionSummaries" <| fun () ->
      let sessions = [ makeSession "s1" "Alpha.fsproj" SessionHealthStatus.Ready 3 ]
      let snap = makeHealthSnapshot sessions None unremarkableUptime 128
      let view = viewOf snap
      view.SessionSummaries |> Expect.hasCountOf "one summary" (fun _ -> true) 1u

    testCase "fromSnapshot populates TotalTestsPassed from live tests" <| fun () ->
      let lt : LiveTestHealthSummary =
        { TotalTests = 100; Passed = 95; Failed = 5; Running = 0 }
      let snap = makeHealthSnapshot [] (Some lt) unremarkableUptime 128
      let view = viewOf snap
      view.TestsPassed |> Expect.equal "tests passed from live test summary" (Some 95)

    testCase "fromSnapshot returns None for TestsPassed when no live test summary" <| fun () ->
      let snap = defaultSnap ()
      let view = viewOf snap
      view.TestsPassed |> Expect.equal "no tests when no summary" None

  ]

[<Tests>]
let renderDaemonHealthTests =
  testList "renderDaemonHealth HTML" [

    testCase "renders healthy emoji for Healthy" <| fun () ->
      let s = makeSession "s1" "Proj.fsproj" SessionHealthStatus.Ready 10
      let snap = makeHealthSnapshot [s] None unremarkableUptime 128
      let view = viewOf snap
      let html = DashboardFragments.renderDaemonHealth view |> renderNode
      html |> Expect.stringContains "green circle for healthy" "🟢"

    testCase "renders degraded emoji for Degraded" <| fun () ->
      let s = makeSession "s1" "Proj.fsproj" SessionHealthStatus.Faulted 0
      let snap = makeHealthSnapshot [s] None unremarkableUptime 128
      let view = viewOf snap
      let html = DashboardFragments.renderDaemonHealth view |> renderNode
      html |> Expect.stringContains "yellow circle for degraded" "🟡"

    testCase "renders version in output" <| fun () ->
      let snap = defaultSnap ()
      let view = viewOf snap
      let html = DashboardFragments.renderDaemonHealth view |> renderNode
      html |> Expect.stringContains "version in rendered output" "0.6.43"

    testCase "renders memory in output" <| fun () ->
      let snap = makeHealthSnapshot [] None unremarkableUptime 312
      let view = viewOf snap
      let html = DashboardFragments.renderDaemonHealth view |> renderNode
      html |> Expect.stringContains "memory MB in rendered output" "312"

    testCase "renders uptime in output" <| fun () ->
      let snap = makeHealthSnapshot [] None FixtureDurations.uptimeHoursLabel 128
      let view = viewOf snap
      let html = DashboardFragments.renderDaemonHealth view |> renderNode
      html |> Expect.isNotEmpty "renders uptime info"

    testCase "renders session project name" <| fun () ->
      let s = makeSession "s1" "MyAwesomeProject" SessionHealthStatus.Ready 7
      let snap = makeHealthSnapshot [s] None unremarkableUptime 128
      let view = viewOf snap
      let html = DashboardFragments.renderDaemonHealth view |> renderNode
      html |> Expect.stringContains "project name in output" "MyAwesomeProject"

    testCase "health row omits test counts (now in live testing panel)" <| fun () ->
      let lt : LiveTestHealthSummary =
        { TotalTests = 50; Passed = 48; Failed = 2; Running = 0 }
      let snap = makeHealthSnapshot [] (Some lt) unremarkableUptime 128
      let view = viewOf snap
      let html = DashboardFragments.renderDaemonHealth view |> renderNode
      let containsTestCount = html.Contains("48")
      containsTestCount |> Expect.isFalse "health row should not contain test counts"

    testCase "renders daemon-health element id" <| fun () ->
      let snap = defaultSnap ()
      let view = viewOf snap
      let html = DashboardFragments.renderDaemonHealth view |> renderNode
      html |> Expect.stringContains "has daemon-health id" DomIds.DaemonHealth

    testCase "renders gracefully with zero sessions" <| fun () ->
      let snap = defaultSnap ()
      let view = viewOf snap
      let html = DashboardFragments.renderDaemonHealth view |> renderNode
      html |> Expect.isNotEmpty "renders without crashing when no sessions"
  ]
