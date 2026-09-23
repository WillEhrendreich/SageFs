namespace SageFs.Features

open System

/// The one place that remembers "have we already taken a gcdump this run" —
/// `GcDumpCapture` itself is pure-guard-plus-one-side-effecting-function, this
/// is the stateful wrapper around it, exactly the same split `HealthWatch` is
/// over `HealthAnomaly`. Every RSS observation the daemon already takes
/// (`HealthWatch.observe SignalId.WorkerRss ...`) is fed here too, right
/// beside it; this module decides whether that observation's verdict is the
/// moment to capture, and kicks the actual capture off the caller's hot path
/// with `Async.Start`.
module GcDumpWatch =

  let mutable private captured = false
  let mutable private lastOutcome : GcDumpCapture.CaptureOutcome option = None
  let private gate = obj ()

  /// Feed one `WorkerRss` verdict (the same one `HealthWatch.observe` just
  /// returned). Fires the real capture, in the background, at most once per
  /// daemon run, and only when there is enough machine headroom to attempt
  /// it safely — see `GcDumpCapture.shouldCapture`/`hasHeadroomToCapture` for
  /// the exact rules. Never blocks the caller: this is meant to be called
  /// from the same tick that already samples `/health`, and a multi-second
  /// dump must never make that tick itself slow.
  let maybeCapture
    (pid: int)
    (outputDir: string)
    (processRssBytes: int64)
    (machineAvailableBytes: int64)
    (signal: HealthAnomaly.SignalId)
    (verdict: HealthAnomaly.Verdict)
    : unit =
    let attempt =
      lock gate (fun () ->
        match GcDumpCapture.shouldCapture captured signal verdict with
        | false -> false
        | true ->
          match GcDumpCapture.hasHeadroomToCapture processRssBytes machineAvailableBytes with
          | false ->
            // Not enough headroom: recorded as the outcome so a client can
            // see WHY no dump exists, but does not consume the one-shot —
            // if the machine recovers enough headroom before the daemon
            // exits, a later Broken sample can still try.
            lastOutcome <- Some(GcDumpCapture.CaptureOutcome.Skipped "not enough machine headroom to safely capture a dump")
            false
          | true ->
            captured <- true
            true)
    match attempt with
    | false -> ()
    | true ->
      Async.Start(
        async {
          let! outcome = GcDumpCapture.captureAsync pid outputDir
          lock gate (fun () -> lastOutcome <- Some outcome)
          match outcome with
          | GcDumpCapture.CaptureOutcome.Captured path ->
            SageFs.Utils.Log.warn "[GcDumpWatch] captured a gcdump at %s (worker_rss went Broken)" path
          | GcDumpCapture.CaptureOutcome.Failed reason ->
            SageFs.Utils.Log.warn "[GcDumpWatch] gcdump capture failed: %s" reason
          | GcDumpCapture.CaptureOutcome.Skipped reason ->
            SageFs.Utils.Log.warn "[GcDumpWatch] gcdump capture skipped: %s" reason
        }
      )

  /// The latest outcome, for the health payload — `None` until either a
  /// capture has been attempted or one was skipped for lacking headroom.
  let lastCaptureOutcome () : GcDumpCapture.CaptureOutcome option = lock gate (fun () -> lastOutcome)

  /// For tests, and for a daemon that wants to start clean.
  let reset () : unit =
    lock gate (fun () ->
      captured <- false
      lastOutcome <- None)
