/// The outcome test the memory-leak report calls for: spawn a real daemon,
/// create NO sessions and make NO requests, leave it alone for a real ten
/// minutes, and assert its own RSS — measured from the outside by PID, not
/// self-reported — did not grow.
///
/// `DaemonRssReturnsToBaselineTests.fs` proves the daemon gives back what
/// FOUR real sessions cost it once they're stopped — a per-session retention
/// leak. This proves something narrower and, on the report's own numbers,
/// more urgent: a daemon that NEVER has a session should not grow AT ALL.
/// The night this was written, "zero sessions, nothing loaded, nothing
/// requested" was measured climbing roughly 2GB/minute from a fresh start —
/// 7.9 -> 8.7 -> 9.7 -> 10.7 -> 12.0GB — and a gcdump at the 12.0GB point
/// showed 11.7GB of LIVE managed heap, not garbage awaiting collection.
///
/// I could not reproduce that climb myself: a fresh isolated daemon, one
/// that resumed 105 real historical sessions off a copy of the real
/// ~/.SageFs, and the same daemon with an /events SSE client attached all
/// settled within tens of MB and stayed flat for 10+ minutes. The root
/// cause (found by a full process dump, not by me) was a directory symlink
/// cycle — Wine's `dosdevices/z:` mapping to `/` under `~/.local/share/
/// Steam/...` — that an unpruned recursive project/file discovery walk
/// followed forever once it started from $HOME; see SafeDirectoryWalkTests
/// .fs and FileWatcherTests.fs's own symlink-cycle regression tests for
/// that specific fix. This test guards the general property their unit
/// tests can't: that a daemon which never even reaches a discovery walk —
/// none of my three reproduction attempts used $HOME as a working
/// directory, which is exactly why none of them reproduced the incident —
/// still doesn't grow on its own from ordinary housekeeping (5s watcher
/// sync, 60s cache/manifest saves, thread-pool/JIT warmup).
/// On duration: the first version of this waited a real ten minutes and was
/// KILLED by the gate's tier timeout on every run (exit 124, reported as
/// `NoReport`, indistinguishable from a crash) — the tier's budget is derived
/// from recorded history, so a shard that ran ~150s could never grant this one
/// 600s, and it could never record a shorter duration because it never
/// finished. A test that always times out is a permanent red, not a guard.
///
/// What actually gives it teeth is the RATE it can detect, not the wall clock.
/// The incident ran ~2GB/minute against a 200MB tolerance — it blows that
/// budget in about six seconds. The default window below covers three full
/// 60s housekeeping cycles and catches anything above ~50MB/minute, which is
/// forty times more sensitive than the bug it was written for, while costing
/// the pre-push gate three minutes instead of ten. Set
/// `SAGEFS_IDLE_RSS_SOAK_MINUTES` to hunt a slower drip than that.
module SageFs.Tests.DaemonIdleRssTests

open System
open System.Diagnostics
open Expecto
open Expecto.Flip
open SageFs.Tests.HttpApiIntegrationTests

module Integration = SageFs.Tests.TestInfrastructure.Integration

let private rssMB (proc: Process) =
  proc.Refresh()
  proc.WorkingSet64 / 1_048_576L

/// Opt in to a longer soak than the gate can afford. Unset or unparseable
/// means the default window — a malformed value must never silently turn the
/// guard into a one-sample no-op.
[<Literal>]
let private SoakMinutesEnvironmentVariable = "SAGEFS_IDLE_RSS_SOAK_MINUTES"

/// Long enough to cross three 60s housekeeping cycles; short enough to belong
/// in a gate that runs before every push.
let private defaultWindow = TimeSpan.FromMinutes 3.0

let private idleWindow () =
  match Environment.GetEnvironmentVariable SoakMinutesEnvironmentVariable with
  | null -> defaultWindow
  | value ->
    match Double.TryParse(value, Globalization.NumberStyles.Float, Globalization.CultureInfo.InvariantCulture) with
    | true, minutes when minutes > 0.0 -> TimeSpan.FromMinutes minutes
    | _ -> defaultWindow

[<Tests>]
let tests =
  Integration.hostList "Daemon idle RSS — zero sessions, no growth while idle" [

    testTask "a daemon with no sessions ever created does not grow while idle" {
      let port = reserveLoopbackPort ()
      let window = idleWindow ()
      // startDaemonWithArgs bakes in --ttl 10m, and the LAST --ttl on the
      // command line wins (DaemonOwnership.parse folds left-to-right,
      // overwriting), so the one appended here is the one that applies.
      // Derive it from the window rather than hardcoding, or a soak longer
      // than the baked-in ttl dies of old age mid-measurement and reports
      // as a growth failure.
      let ttlMinutes = max 20.0 (window.TotalMinutes * 2.0)
      let! (proc: Process), (client: System.Net.Http.HttpClient) =
        startDaemonWithArgs
          port
          testProjectDir
          [ "--no-resume"
            "--ttl"
            sprintf "%dm" (int (ceil ttlMinutes)) ]
      try
        // Let warmup settle (module init, JIT of the request pipeline, the
        // ASP.NET Core request pipeline's own first-hit cost) before taking
        // the baseline — otherwise "baseline" is really "mid-startup", the
        // same reasoning DaemonRssReturnsToBaselineTests.fs uses.
        do! Threading.Tasks.Task.Delay(2000)
        let baselineMB = rssMB proc

        // No session created, no request beyond the readiness polling
        // startDaemonWithArgs already did to observe this daemon exists —
        // exactly "zero sessions, nothing loaded, nothing requested".
        // Sample often enough that the failure message shows the SHAPE of any
        // growth (a ramp, a step, a sawtooth) rather than just two endpoints —
        // that shape is what told us the incident was a steady walk and not a
        // one-off allocation.
        let sampleEvery = TimeSpan.FromSeconds 15.0
        let sampleCount = max 1 (int (window.TotalSeconds / sampleEvery.TotalSeconds))
        let samples = ResizeArray<int64>()
        samples.Add baselineMB
        for _ in 1 .. sampleCount do
          do! Threading.Tasks.Task.Delay sampleEvery
          samples.Add(rssMB proc)

        let finalMB = samples.[samples.Count - 1]
        let peakMB = Seq.max samples
        let growthMB = finalMB - baselineMB

        // Generous tolerance — JIT warmup, GC bookkeeping, thread-pool
        // growth and the handful of housekeeping timers (5s watcher sync,
        // 60s cache/manifest save, 15m orphan sweep) are all real and not
        // the leak this guards against; measured, they settle within tens of
        // MB. Deliberately NOT scaled by the window: legitimate housekeeping
        // plateaus rather than growing linearly, so a fixed budget makes a
        // longer soak strictly MORE sensitive, which is the whole point of
        // running one. At the default window this catches anything above
        // ~66MB/minute; the incident ran ~2GB/minute and would blow it in
        // about six seconds.
        let toleranceMB = 200L
        (growthMB, toleranceMB)
        |> Expect.isLessThanOrEqual
          (sprintf
            "an idle daemon with zero sessions should not grow — window=%gmin baseline=%dMB peak=%dMB final=%dMB samples=%s"
            window.TotalMinutes baselineMB peakMB finalMB (String.concat "," (samples |> Seq.map string)))
      finally
        try
          if not proc.HasExited then
            proc.Kill()
            proc.WaitForExit(5000) |> ignore
        with _ -> ()
        client.Dispose()
    }
  ]
