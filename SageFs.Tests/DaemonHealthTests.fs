module SageFs.Tests.DaemonHealthTests

open System
open Expecto
open Expecto.Flip
open SageFs.Features

[<Tests>]
let healthSnapshotTests =
  testList "DaemonHealth snapshot" [

    testCase "healthy daemon with ready sessions" <| fun _ ->
      let snapshot = {
        DaemonPid = 1234
        DaemonPort = 37749
        Uptime = TimeSpan.FromHours 2.5
        Version = "0.5.761"
        SessionSummaries = [
          { SessionId = "abc123"; ProjectName = "MyLib"; Status = SessionHealthStatus.Ready; EvalCount = 42; LastActivity = DateTimeOffset.UtcNow }
        ]
        LiveTestingSummary = Some { TotalTests = 100; Passed = 98; Failed = 2; Running = 0 }
        MemoryMB = 256
      }
      let health = DaemonHealth.overallStatus snapshot
      health |> Expect.equal "should be healthy" OverallHealth.Healthy

    testCase "degraded when any session is faulted" <| fun _ ->
      let snapshot = {
        DaemonPid = 1234
        DaemonPort = 37749
        Uptime = TimeSpan.FromMinutes 5.0
        Version = "0.5.761"
        SessionSummaries = [
          { SessionId = "abc"; ProjectName = "Good"; Status = SessionHealthStatus.Ready; EvalCount = 10; LastActivity = DateTimeOffset.UtcNow }
          { SessionId = "def"; ProjectName = "Bad"; Status = SessionHealthStatus.Faulted; EvalCount = 0; LastActivity = DateTimeOffset.UtcNow }
        ]
        LiveTestingSummary = None
        MemoryMB = 128
      }
      let health = DaemonHealth.overallStatus snapshot
      health |> Expect.equal "should be degraded" OverallHealth.Degraded

    testCase "unhealthy when no sessions exist" <| fun _ ->
      let snapshot = {
        DaemonPid = 1234
        DaemonPort = 37749
        Uptime = TimeSpan.FromSeconds 2.0
        Version = "0.5.761"
        SessionSummaries = []
        LiveTestingSummary = None
        MemoryMB = 64
      }
      let health = DaemonHealth.overallStatus snapshot
      health |> Expect.equal "no sessions = unhealthy" OverallHealth.Unhealthy
  ]

[<Tests>]
let healthFormatTests =
  testList "DaemonHealth formatting" [

    testCase "summary includes key metrics" <| fun _ ->
      let snapshot = {
        DaemonPid = 5678
        DaemonPort = 37749
        Uptime = TimeSpan.FromHours 1.5
        Version = "0.5.761"
        SessionSummaries = [
          { SessionId = "s1"; ProjectName = "Lib"; Status = SessionHealthStatus.Ready; EvalCount = 25; LastActivity = DateTimeOffset.UtcNow }
        ]
        LiveTestingSummary = Some { TotalTests = 50; Passed = 48; Failed = 2; Running = 0 }
        MemoryMB = 200
      }
      let text = DaemonHealth.formatSummary snapshot
      text |> Expect.stringContains "has pid" "5678"
      text |> Expect.stringContains "has version" "0.5.761"
      text |> Expect.stringContains "has session" "Lib"

    testCase "emoji health indicator" <| fun _ ->
      DaemonHealth.healthEmoji OverallHealth.Healthy |> Expect.equal "green" "🟢"
      DaemonHealth.healthEmoji OverallHealth.Degraded |> Expect.equal "yellow" "🟡"
      DaemonHealth.healthEmoji OverallHealth.Unhealthy |> Expect.equal "red" "🔴"

    testCase "health label" <| fun _ ->
      DaemonHealth.healthLabel OverallHealth.Healthy |> Expect.equal "healthy" "Healthy"
      DaemonHealth.healthLabel OverallHealth.Degraded |> Expect.equal "degraded" "Degraded"
      DaemonHealth.healthLabel OverallHealth.Unhealthy |> Expect.equal "unhealthy" "Unhealthy"
  ]

[<Tests>]
let sessionHealthTests =
  testList "DaemonHealth session status" [

    testCase "session status labels" <| fun _ ->
      DaemonHealth.sessionStatusLabel SessionHealthStatus.Ready |> Expect.equal "ready" "Ready"
      DaemonHealth.sessionStatusLabel SessionHealthStatus.Evaluating |> Expect.equal "eval" "Evaluating"
      DaemonHealth.sessionStatusLabel SessionHealthStatus.WarmingUp |> Expect.equal "warmup" "Warming Up"
      DaemonHealth.sessionStatusLabel SessionHealthStatus.Faulted |> Expect.equal "faulted" "Faulted"
      DaemonHealth.sessionStatusLabel SessionHealthStatus.Stopped |> Expect.equal "stopped" "Stopped"

    testCase "session status emoji" <| fun _ ->
      DaemonHealth.sessionStatusEmoji SessionHealthStatus.Ready |> Expect.equal "ready" "✅"
      DaemonHealth.sessionStatusEmoji SessionHealthStatus.Faulted |> Expect.equal "faulted" "❌"
      DaemonHealth.sessionStatusEmoji SessionHealthStatus.WarmingUp |> Expect.equal "warmup" "⏳"

    testCase "uptime formatting" <| fun _ ->
      DaemonHealth.formatUptime (TimeSpan.FromMinutes 45.0)
      |> Expect.equal "minutes" "45m"
      DaemonHealth.formatUptime (TimeSpan.FromHours 2.5)
      |> Expect.equal "hours" "2h 30m"
      DaemonHealth.formatUptime (TimeSpan.FromDays 1.5)
      |> Expect.equal "days" "1d 12h"
  ]
