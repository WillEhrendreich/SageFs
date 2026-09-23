/// The detector is wired to the daemon's own health, not just sitting in a
/// module. The incident this pins: the daemon at 51.7GB of a 62GB machine,
/// every session Ready, and `/health` reporting Healthy the whole time.
module SageFs.Tests.HealthWatchWiringTests

open System
open Expecto
open Expecto.Flip
open SageFs.Features

let private evidence signal value =
  { Signal = signal
    ObservedAt = DateTimeOffset.UtcNow
    ObservedValue = value
    BaselineMean = 400.0
    BaselineStdDev = 40.0
    Direction = HealthAnomaly.SignalDirection.Increased
    DeviationInSigmas = 120.0
    SustainedFor = TimeSpan.FromMinutes 2.0
    SamplesSustained = 12 }
  : HealthAnomaly.SignalEvidence

let private snapshotWith anomalies sessions =
  { DaemonPid = 1
    DaemonPort = 37749
    Uptime = TimeSpan.FromHours 6.0
    Version = "0.6.812"
    SessionSummaries = sessions
    LiveTestingSummary = None
    MemoryMB = 51_700
    Anomalies = anomalies
    GcDumpOutcome = None
    MemoryPressure = SageFs.MemoryPressure.Normal }
  : HealthSnapshot

let private readySession =
  { SessionId = "a1b2c3d4"
    ProjectName = "Whatever.fsproj"
    Status = SessionHealthStatus.Ready
    EvalCount = 42
    LastActivity = DateTimeOffset.UtcNow }

[<Tests>]
let healthWatchWiringTests =
  // Sequenced: these share the global HealthWatch registry, and reset() in one
  // case raced another case mid-run under the parallel scheduler.
  testSequenced <| testList "Daemon health reads its own telemetry" [
    testCase "a broken signal makes the daemon unhealthy even with every session Ready" <| fun _ ->
      snapshotWith [ HealthAnomaly.Verdict.Broken(evidence HealthAnomaly.SignalId.WorkerRss 51_700.0) ] [ readySession ]
      |> DaemonHealth.overallStatus
      |> Expect.equal "the sessions are fine and the daemon is eating the machine" OverallHealth.Unhealthy

    testCase "a drifting signal is reported without crying wolf" <| fun _ ->
      snapshotWith [ HealthAnomaly.Verdict.Drifting(evidence HealthAnomaly.SignalId.HealthLatency 900.0) ] [ readySession ]
      |> DaemonHealth.overallStatus
      |> Expect.equal "drift is worth showing, not worth calling the daemon unhealthy" OverallHealth.Healthy

    testCase "a faulted session still degrades, with nothing anomalous" <| fun _ ->
      snapshotWith [] [ { readySession with Status = SessionHealthStatus.Faulted } ]
      |> DaemonHealth.overallStatus
      |> Expect.equal "the old rule still holds" OverallHealth.Degraded

    testCase "a quiet daemon with nothing wrong is healthy" <| fun _ ->
      snapshotWith [] [ readySession ]
      |> DaemonHealth.overallStatus
      |> Expect.equal "no anomaly, no fault" OverallHealth.Healthy

    // The gap this closes: INCIDENT 2 in HealthAnomalyTests.fs proves a
    // steady climb to 37GB eventually reads as Drifting OR Broken through
    // the shape-based detector alone — but the smoother the ramp, the more
    // the EWMA baseline keeps pace, and both real incidents reported
    // `anomalies: []` for their entire climb. `MemoryPressure` is judged
    // independently of `Anomalies`, against the machine's OWN available
    // memory, so a smooth ramp is still caught the moment it gets dangerous.
    testCase "WHY — overallStatus — the machine being almost out of memory is unhealthy even when the shape detector never fired" <| fun _ ->
      { snapshotWith [] [ readySession ] with MemoryPressure = SageFs.MemoryPressure.Critical }
      |> DaemonHealth.overallStatus
      |> Expect.equal "a smooth ramp with anomalies: [] is exactly what ate the machine twice" OverallHealth.Unhealthy

    // The watch is what the daemon actually calls on its tick, so the wiring
    // is only real if feeding it the incident's shape produces a verdict the
    // snapshot can carry.
    testCase "feeding the watch a sustained climb ends up in troubled()" <| fun _ ->
      HealthWatch.reset ()
      let start = DateTimeOffset.UtcNow
      // A flat hour at 400MB, then a climb to 50GB, the shape of the incident.
      for i in 0 .. 59 do
        HealthWatch.observe
          HealthAnomaly.SignalId.WorkerRss
          (400.0 + float (i % 3))
          (start.AddSeconds(float i))
        |> ignore
      for i in 60 .. 120 do
        HealthWatch.observe
          HealthAnomaly.SignalId.WorkerRss
          (400.0 + float (i - 59) * 800.0)
          (start.AddSeconds(float i))
        |> ignore
      let troubled = HealthWatch.troubled ()
      troubled |> Expect.isNonEmpty "a daemon growing to 50GB has something to say"
      troubled
      |> List.exists (function
        | HealthAnomaly.Verdict.Broken _ -> true
        | _ -> false)
      |> Expect.isTrue "and it says broken, not just drifting"
      HealthWatch.reset ()

    testCase "a watch that has seen nothing has nothing to say" <| fun _ ->
      HealthWatch.reset ()
      HealthWatch.troubled () |> Expect.isEmpty "no readings, no claims"
  ]

/// The evidence has to reach a client, not just change a flag. A daemon that
/// flips to unhealthy without saying which signal moved is the same silence
/// in a different costume.
[<Tests>]
let healthSurfacingTests =
  testSequenced <| testList "A daemon's own trouble reaches the client" [
    testCase "a verdict has a name and its evidence, for any wire format" <| fun _ ->
      let broken = HealthAnomaly.Verdict.Broken(evidence HealthAnomaly.SignalId.WorkerRss 51_700.0)
      HealthAnomaly.verdictName broken |> Expect.equal "the one place this becomes text" "broken"
      HealthAnomaly.evidenceOf broken
      |> Option.map (fun e -> e.ObservedValue)
      |> Expect.equal "the payload needs the numbers, not just the word" (Some 51_700.0)

    testCase "a normal verdict carries no evidence and says so" <| fun _ ->
      HealthAnomaly.verdictName HealthAnomaly.Verdict.Normal |> Expect.equal "named, not blank" "normal"
      HealthAnomaly.evidenceOf HealthAnomaly.Verdict.Normal |> Expect.isNone "nothing to report"

    testCase "the dashboard view carries a sentence per anomaly" <| fun _ ->
      let snap = snapshotWith [ HealthAnomaly.Verdict.Broken(evidence HealthAnomaly.SignalId.WorkerRss 51_700.0) ] [ readySession ]
      let view = SageFs.Server.DashboardTypes.DaemonHealthView.fromSnapshot snap
      view.Anomalies |> Expect.hasLength "one anomaly, one line" 1
      view.Anomalies.Head |> Expect.stringContains "it names the signal" "worker_rss"
      view.Anomalies.Head |> Expect.stringContains "and what it is now" "5.17e+04"

    testCase "a healthy daemon's view has nothing to show" <| fun _ ->
      let view = SageFs.Server.DashboardTypes.DaemonHealthView.fromSnapshot (snapshotWith [] [ readySession ])
      view.Anomalies |> Expect.isEmpty "no anomaly, no noise in the panel"
  ]
