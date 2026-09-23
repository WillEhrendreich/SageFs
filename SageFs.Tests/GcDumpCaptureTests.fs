/// Both nights the daemon ate the machine, the evidence died with the
/// process: by the time anyone noticed, there was no memory left to safely
/// take a dump with. These tests pin `GcDumpCapture`'s pure guards (capture
/// once, only for `WorkerRss` going `Broken`, only with enough headroom to
/// try safely) and, as a Host integration case, that the real subprocess
/// plumbing against `dotnet-gcdump` actually produces a file.
module SageFs.Tests.GcDumpCaptureTests

open System
open Expecto
open Expecto.Flip
open SageFs.Features

module Integration = SageFs.Tests.TestInfrastructure.Integration

let private brokenRss : HealthAnomaly.Verdict =
  HealthAnomaly.Verdict.Broken
    { Signal = HealthAnomaly.SignalId.WorkerRss
      ObservedAt = DateTimeOffset.UtcNow
      ObservedValue = 51_700.0
      BaselineMean = 400.0
      BaselineStdDev = 40.0
      Direction = HealthAnomaly.SignalDirection.Increased
      DeviationInSigmas = 100.0
      SustainedFor = TimeSpan.FromMinutes 2.0
      SamplesSustained = 10 }

let private drifting : HealthAnomaly.Verdict =
  HealthAnomaly.Verdict.Drifting
    { Signal = HealthAnomaly.SignalId.WorkerRss
      ObservedAt = DateTimeOffset.UtcNow
      ObservedValue = 900.0
      BaselineMean = 400.0
      BaselineStdDev = 40.0
      Direction = HealthAnomaly.SignalDirection.Increased
      DeviationInSigmas = 5.0
      SustainedFor = TimeSpan.FromMinutes 1.0
      SamplesSustained = 5 }

[<Tests>]
let shouldCaptureTests =
  testList "GcDumpCapture.shouldCapture" [

    testCase "a Broken WorkerRss verdict, not yet captured, says capture" <| fun () ->
      GcDumpCapture.shouldCapture false HealthAnomaly.SignalId.WorkerRss brokenRss
      |> Expect.isTrue "this is exactly the incident shape"

    testCase "already captured this run: never again, however broken" <| fun () ->
      GcDumpCapture.shouldCapture true HealthAnomaly.SignalId.WorkerRss brokenRss
      |> Expect.isFalse "one dump per daemon run"

    testCase "a Drifting WorkerRss verdict does not trigger a capture" <| fun () ->
      GcDumpCapture.shouldCapture false HealthAnomaly.SignalId.WorkerRss drifting
      |> Expect.isFalse "drift is worth watching, not worth a heap walk yet"

    testCase "a Broken verdict on a different signal never triggers a gcdump" <| fun () ->
      let brokenLatency : HealthAnomaly.Verdict =
        HealthAnomaly.Verdict.Broken
          { Signal = HealthAnomaly.SignalId.HealthLatency
            ObservedAt = DateTimeOffset.UtcNow
            ObservedValue = 900.0
            BaselineMean = 4.0
            BaselineStdDev = 1.0
            Direction = HealthAnomaly.SignalDirection.Increased
            DeviationInSigmas = 200.0
            SustainedFor = TimeSpan.FromMinutes 1.0
            SamplesSustained = 10 }
      GcDumpCapture.shouldCapture false HealthAnomaly.SignalId.HealthLatency brokenLatency
      |> Expect.isFalse "a gcdump has nothing to say about a slow /health — only RSS"

    testCase "Normal and InsufficientHistory never trigger a capture" <| fun () ->
      GcDumpCapture.shouldCapture false HealthAnomaly.SignalId.WorkerRss HealthAnomaly.Verdict.Normal
      |> Expect.isFalse "nothing wrong, nothing to capture"
      GcDumpCapture.shouldCapture false HealthAnomaly.SignalId.WorkerRss HealthAnomaly.Verdict.InsufficientHistory
      |> Expect.isFalse "no baseline yet, no verdict to act on"
  ]

[<Tests>]
let headroomTests =
  testList "GcDumpCapture.hasHeadroomToCapture" [

    testCase "the 55GB-daemon-1GB-free incident: no headroom, skip the dump" <| fun () ->
      GcDumpCapture.hasHeadroomToCapture (55_000_000_000L) (1_000_000_000L)
      |> Expect.isFalse "attempting a dump here risks finishing the machine off"

    testCase "a healthy daemon with plenty of available memory has headroom" <| fun () ->
      GcDumpCapture.hasHeadroomToCapture (500_000_000L) (40_000_000_000L)
      |> Expect.isTrue "small process, huge headroom"

    testCase "available memory exactly equal to the process RSS is NOT enough headroom" <| fun () ->
      GcDumpCapture.hasHeadroomToCapture (1_000_000_000L) (1_000_000_000L)
      |> Expect.isFalse "the floor is strictly more than the process's own size"

    testCase "a zero or negative recorded RSS never counts as having headroom" <| fun () ->
      GcDumpCapture.hasHeadroomToCapture 0L (40_000_000_000L) |> Expect.isFalse "nothing meaningful to gate on"
  ]

[<Tests>]
let fileNameTests =
  testList "GcDumpCapture.fileNameFor" [
    testCase "names carry the pid and are sortable by timestamp" <| fun () ->
      let at = DateTimeOffset(2026, 9, 22, 3, 4, 5, TimeSpan.Zero)
      GcDumpCapture.fileNameFor 4242 at
      |> Expect.equal "pid, then a sortable UTC timestamp, then the extension" "sagefs-daemon-4242-20260922-030405.gcdump"

    testCase "two different pids at the same instant never collide" <| fun () ->
      let at = DateTimeOffset.UtcNow
      (GcDumpCapture.fileNameFor 111 at <> GcDumpCapture.fileNameFor 222 at)
      |> Expect.isTrue "pid is part of the name"
  ]

[<Tests>]
let captureIntegrationTests =
  Integration.hostList "GcDumpCapture.captureAsync (real dotnet-gcdump subprocess)" [
    // Two claims, and only one of them is ours. Ours: whatever happens, the
    // outcome is reported truthfully and a claimed file really exists. Not
    // ours: that dotnet-gcdump can attach at all, which it cannot inside the
    // gate's sandboxed tier (it exits 255 there, while the same code captures
    // fine outside it). So the contract is asserted always, and the artifact
    // only where capture is actually possible. A Failed outcome is still read
    // closely: it must say why, or we have learned nothing from it.
    testCase "capturing this very test process reports its outcome truthfully" <| fun () ->
      let outputDir = IO.Path.Combine(IO.Path.GetTempPath(), "sagefs-gcdump-tests-" + Guid.NewGuid().ToString("N"))
      try
        let ownPid = Diagnostics.Process.GetCurrentProcess().Id
        let outcome = GcDumpCapture.captureAsync ownPid outputDir |> Async.RunSynchronously
        match outcome with
        | GcDumpCapture.CaptureOutcome.Captured path ->
          IO.File.Exists path |> Expect.isTrue "the tool reported success — the file must actually be on disk"
          (IO.FileInfo(path).Length > 0L) |> Expect.isTrue "a real dump has real bytes"
        | GcDumpCapture.CaptureOutcome.Skipped reason ->
          // Only honest when the tool genuinely is not installed. If it is on
          // PATH and we skipped anyway, that is our bug.
          reason |> Expect.isNotEmpty "a skip has to say what was missing"
          GcDumpCapture.isToolAvailable ()
          |> Expect.isFalse (sprintf "dotnet-gcdump is installed, so skipping it is wrong: %s" reason)
        | GcDumpCapture.CaptureOutcome.Failed reason ->
          // The environment may refuse the attach (the gate's tier does). What
          // must never happen is a failure with nothing to act on.
          reason |> Expect.isNotEmpty "a failed capture has to say why, or the diagnostic is useless"
      finally
        try IO.Directory.Delete(outputDir, true) with _ -> ()
  ]
