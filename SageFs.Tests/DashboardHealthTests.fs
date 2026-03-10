module SageFs.Tests.DashboardHealthTests

open System
open Expecto
open Falco.Markup
open SageFs
open SageFs.Features
open SageFs.Server
open SageFs.Server.DashboardTypes

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
    MemoryMB = memoryMB }

let private defaultSnap () =
  makeHealthSnapshot [] None (TimeSpan.FromMinutes 5.0) 128

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
      let view = DashboardTypes.DaemonHealthView.fromSnapshot snap
      Expect.equal view.Version "0.6.43" "version round-trips"

    testCase "fromSnapshot populates MemoryMB" <| fun () ->
      let snap = makeHealthSnapshot [] None (TimeSpan.FromMinutes 5.0) 256
      let view = DashboardTypes.DaemonHealthView.fromSnapshot snap
      Expect.equal view.MemoryMB 256 "memory MB round-trips"

    testCase "fromSnapshot populates UptimeLabel" <| fun () ->
      let snap = makeHealthSnapshot [] None (TimeSpan.FromHours 2.5) 128
      let view = DashboardTypes.DaemonHealthView.fromSnapshot snap
      Expect.isNotEmpty view.UptimeLabel "uptime label is non-empty"

    testCase "fromSnapshot returns Healthy when sessions are ready" <| fun () ->
      let s = makeSession "abc" "MyProject.fsproj" SessionHealthStatus.Ready 10
      let snap = makeHealthSnapshot [s] None (TimeSpan.FromMinutes 5.0) 128
      let view = DashboardTypes.DaemonHealthView.fromSnapshot snap
      Expect.equal view.OverallHealth OverallHealth.Healthy "healthy with ready session"

    testCase "fromSnapshot returns Degraded when session is faulted" <| fun () ->
      let s = makeSession "abc" "MyProject.fsproj" SessionHealthStatus.Faulted 0
      let snap = makeHealthSnapshot [s] None (TimeSpan.FromMinutes 5.0) 128
      let view = DashboardTypes.DaemonHealthView.fromSnapshot snap
      Expect.equal view.OverallHealth OverallHealth.Degraded "degraded with faulted session"

    testCase "fromSnapshot returns Unhealthy when no sessions" <| fun () ->
      let snap = defaultSnap ()
      let view = DashboardTypes.DaemonHealthView.fromSnapshot snap
      Expect.equal view.OverallHealth OverallHealth.Unhealthy "unhealthy with no sessions"

    testCase "fromSnapshot populates SessionCount" <| fun () ->
      let sessions =
        [ makeSession "s1" "Proj1" SessionHealthStatus.Ready 5
          makeSession "s2" "Proj2" SessionHealthStatus.WarmingUp 0 ]
      let snap = makeHealthSnapshot sessions None (TimeSpan.FromMinutes 5.0) 128
      let view = DashboardTypes.DaemonHealthView.fromSnapshot snap
      Expect.equal view.SessionCount 2 "session count matches"

    testCase "fromSnapshot populates SessionSummaries" <| fun () ->
      let sessions = [ makeSession "s1" "Alpha.fsproj" SessionHealthStatus.Ready 3 ]
      let snap = makeHealthSnapshot sessions None (TimeSpan.FromMinutes 5.0) 128
      let view = DashboardTypes.DaemonHealthView.fromSnapshot snap
      Expect.hasCountOf view.SessionSummaries 1u (fun _ -> true) "one summary"

    testCase "fromSnapshot populates TotalTestsPassed from live tests" <| fun () ->
      let lt : LiveTestHealthSummary =
        { TotalTests = 100; Passed = 95; Failed = 5; Running = 0 }
      let snap = makeHealthSnapshot [] (Some lt) (TimeSpan.FromMinutes 5.0) 128
      let view = DashboardTypes.DaemonHealthView.fromSnapshot snap
      Expect.equal view.TestsPassed (Some 95) "tests passed from live test summary"

    testCase "fromSnapshot returns None for TestsPassed when no live test summary" <| fun () ->
      let snap = defaultSnap ()
      let view = DashboardTypes.DaemonHealthView.fromSnapshot snap
      Expect.equal view.TestsPassed None "no tests when no summary"

    testCase "UptimeLabel is human-readable for minutes" <| fun () ->
      let snap = makeHealthSnapshot [] None (TimeSpan.FromMinutes 42.0) 128
      let view = DashboardTypes.DaemonHealthView.fromSnapshot snap
      Expect.stringContains view.UptimeLabel "m" "minutes label contains m"

    testCase "UptimeLabel is human-readable for hours" <| fun () ->
      let snap = makeHealthSnapshot [] None (TimeSpan.FromHours 3.5) 128
      let view = DashboardTypes.DaemonHealthView.fromSnapshot snap
      Expect.stringContains view.UptimeLabel "h" "hours label contains h"
  ]

[<Tests>]
let renderDaemonHealthTests =
  testList "renderDaemonHealth HTML" [

    testCase "renders healthy emoji for Healthy" <| fun () ->
      let s = makeSession "s1" "Proj.fsproj" SessionHealthStatus.Ready 10
      let snap = makeHealthSnapshot [s] None (TimeSpan.FromMinutes 5.0) 128
      let view = DashboardTypes.DaemonHealthView.fromSnapshot snap
      let html = DashboardFragments.renderDaemonHealth view |> renderNode
      Expect.stringContains html "🟢" "green circle for healthy"

    testCase "renders degraded emoji for Degraded" <| fun () ->
      let s = makeSession "s1" "Proj.fsproj" SessionHealthStatus.Faulted 0
      let snap = makeHealthSnapshot [s] None (TimeSpan.FromMinutes 5.0) 128
      let view = DashboardTypes.DaemonHealthView.fromSnapshot snap
      let html = DashboardFragments.renderDaemonHealth view |> renderNode
      Expect.stringContains html "🟡" "yellow circle for degraded"

    testCase "renders version in output" <| fun () ->
      let snap = defaultSnap ()
      let view = DashboardTypes.DaemonHealthView.fromSnapshot snap
      let html = DashboardFragments.renderDaemonHealth view |> renderNode
      Expect.stringContains html "0.6.43" "version in rendered output"

    testCase "renders memory in output" <| fun () ->
      let snap = makeHealthSnapshot [] None (TimeSpan.FromMinutes 5.0) 312
      let view = DashboardTypes.DaemonHealthView.fromSnapshot snap
      let html = DashboardFragments.renderDaemonHealth view |> renderNode
      Expect.stringContains html "312" "memory MB in rendered output"

    testCase "renders uptime in output" <| fun () ->
      let snap = makeHealthSnapshot [] None (TimeSpan.FromMinutes 90.0) 128
      let view = DashboardTypes.DaemonHealthView.fromSnapshot snap
      let html = DashboardFragments.renderDaemonHealth view |> renderNode
      Expect.isNotEmpty html "renders uptime info"

    testCase "renders session project name" <| fun () ->
      let s = makeSession "s1" "MyAwesomeProject" SessionHealthStatus.Ready 7
      let snap = makeHealthSnapshot [s] None (TimeSpan.FromMinutes 5.0) 128
      let view = DashboardTypes.DaemonHealthView.fromSnapshot snap
      let html = DashboardFragments.renderDaemonHealth view |> renderNode
      Expect.stringContains html "MyAwesomeProject" "project name in output"

    testCase "renders test pass count when available" <| fun () ->
      let lt : LiveTestHealthSummary =
        { TotalTests = 50; Passed = 48; Failed = 2; Running = 0 }
      let snap = makeHealthSnapshot [] (Some lt) (TimeSpan.FromMinutes 5.0) 128
      let view = DashboardTypes.DaemonHealthView.fromSnapshot snap
      let html = DashboardFragments.renderDaemonHealth view |> renderNode
      Expect.stringContains html "48" "passed count in output"

    testCase "renders daemon-health element id" <| fun () ->
      let snap = defaultSnap ()
      let view = DashboardTypes.DaemonHealthView.fromSnapshot snap
      let html = DashboardFragments.renderDaemonHealth view |> renderNode
      Expect.stringContains html DomIds.DaemonHealth "has daemon-health id"

    testCase "renders gracefully with zero sessions" <| fun () ->
      let snap = defaultSnap ()
      let view = DashboardTypes.DaemonHealthView.fromSnapshot snap
      let html = DashboardFragments.renderDaemonHealth view |> renderNode
      Expect.isNotEmpty html "renders without crashing when no sessions"
  ]
