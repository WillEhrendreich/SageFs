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

[<Tests>]
let tests =
  Integration.hostList "Daemon idle RSS — zero sessions, ten minutes, no growth" [

    testTask "a daemon with no sessions ever created does not grow over ten minutes" {
      let port = reserveLoopbackPort ()
      // startDaemonWithArgs bakes in --ttl 10m; this run needs to outlive
      // that, and the LAST --ttl on the command line wins (DaemonOwnership
      // .parse folds left-to-right, overwriting), so 20m appended after it
      // is the one that actually applies.
      let! (proc: Process), (client: System.Net.Http.HttpClient) =
        startDaemonWithArgs port testProjectDir [ "--no-resume"; "--ttl"; "20m" ]
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
        let sampleEvery = TimeSpan.FromSeconds 30.0
        let totalWait = TimeSpan.FromMinutes 10.0
        let sampleCount = int (totalWait.TotalSeconds / sampleEvery.TotalSeconds)
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
        // the leak this guards against. What it guards against is the
        // reported ~2GB/minute: even ONE minute of that would blow through
        // this tolerance many times over, let alone ten.
        let toleranceMB = 200L
        (growthMB, toleranceMB)
        |> Expect.isLessThanOrEqual
          (sprintf
            "an idle daemon with zero sessions should not grow — baseline=%dMB peak=%dMB final=%dMB samples=%s"
            baselineMB peakMB finalMB (String.concat "," (samples |> Seq.map string)))
      finally
        try
          if not proc.HasExited then
            proc.Kill()
            proc.WaitForExit(5000) |> ignore
        with _ -> ()
        client.Dispose()
    }
  ]
