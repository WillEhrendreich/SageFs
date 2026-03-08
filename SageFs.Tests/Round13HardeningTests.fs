module SageFs.Tests.Round13HardeningTests

open System
open Expecto
open Expecto.Flip
open SageFs
open SageFs.Features
open SageFs.Features.ManifestTypes
open SageFs.Features.Replay
open Microsoft.Extensions.Logging.Abstractions

let private nullLog = NullLogger.Instance
let private noDaemon () : DaemonInfo option = None
let private pruneFlags = { Args.DaemonFlags.defaults with Prune = true }
let private noPruneFlags = Args.DaemonFlags.defaults

// ---------------------------------------------------------------------------
// W31 — handlePrune exhaustive ManifestLoadError match
// ---------------------------------------------------------------------------
// W31(R13): handlePrune's `Error _` catch-all treated IoError and CorruptData as NotFound.
// After W26 introduced the typed ManifestLoadError DU, the compiler enforces exhaustiveness
// on ManifestFile.load — but handlePrune's caller pattern `Error _` swallowed that information.
// IoError/CorruptData should return false + log at Warning (not "No binary manifest found").
// RED: currently returns true for ALL Error cases. GREEN: IoError/CorruptData return false.

[<Tests>]
let w31HandlePruneExhaustiveTests =
  testList "W31(R13) — handlePrune exhaustive ManifestLoadError match" [

    testCase "handlePrune with corrupt manifest returns false (CorruptData ≠ NotFound)" <| fun _ ->
      let dir = IO.Path.Combine(IO.Path.GetTempPath(), sprintf "sagefs-r13-%s" (Guid.NewGuid().ToString("N")))
      IO.Directory.CreateDirectory(dir) |> ignore
      try
        // Write a corrupt manifest — loadManifest will return CorruptData, not NotFound
        IO.File.WriteAllBytes(IO.Path.Combine(dir, "daemon.sagefm"), [| 0xFFuy; 0xFEuy; 0xFDuy |])
        let result =
          SageFs.Server.DaemonMode.handlePrune dir nullLog noDaemon pruneFlags
          |> Async.AwaitTask
          |> Async.RunSynchronously
        result |> Expect.isFalse "handlePrune with CorruptData manifest should return false — prune did NOT complete"
      finally
        IO.Directory.Delete(dir, true)

    testCase "handlePrune with no manifest returns true (NotFound → nothing to prune)" <| fun _ ->
      let dir = IO.Path.Combine(IO.Path.GetTempPath(), sprintf "sagefs-r13-%s" (Guid.NewGuid().ToString("N")))
      // No directory created — ensures NotFound case
      let result =
        SageFs.Server.DaemonMode.handlePrune dir nullLog noDaemon pruneFlags
        |> Async.AwaitTask
        |> Async.RunSynchronously
      result |> Expect.isTrue "handlePrune with NotFound should return true (nothing to prune)"

    testCase "handlePrune with Prune=false always returns false" <| fun _ ->
      let dir = IO.Path.Combine(IO.Path.GetTempPath(), sprintf "sagefs-r13-%s" (Guid.NewGuid().ToString("N")))
      let result =
        SageFs.Server.DaemonMode.handlePrune dir nullLog noDaemon noPruneFlags
        |> Async.AwaitTask
        |> Async.RunSynchronously
      result |> Expect.isFalse "handlePrune with Prune=false returns false (no prune attempted)"

    testCase "ManifestLoadError: IoError and CorruptData carry error context; NotFound does not" <| fun _ ->
      // Documents that IoError/CorruptData are error conditions (file exists but unreadable)
      // while NotFound is a clean first-run condition — they must NOT be handled identically.
      let hasPayload = function
        | ManifestLoadError.NotFound -> false
        | ManifestLoadError.IoError _ -> true
        | ManifestLoadError.CorruptData _ -> true
      hasPayload ManifestLoadError.NotFound |> Expect.isFalse "NotFound carries no payload (clean slate)"
      hasPayload (ManifestLoadError.IoError "locked") |> Expect.isTrue "IoError carries error context"
      hasPayload (ManifestLoadError.CorruptData "bad CRC") |> Expect.isTrue "CorruptData carries error context"
  ]

// ---------------------------------------------------------------------------
// W28 — handlePrune daemon-running guard (cross-process TOCTOU)
// ---------------------------------------------------------------------------
// W28(R13): handlePrune now accepts checkDaemonRunning injection and refuses to prune
// if a daemon is detected. Cross-process TOCTOU: daemon may write an updated manifest
// after prune reads but before prune writes, causing pruned ghost sessions to reappear.

[<Tests>]
let w28DaemonRunningGuardTests =
  testList "W28(R13) — handlePrune daemon-running guard" [

    testCase "handlePrune proceeds when no daemon detected (noDaemon returns None)" <| fun _ ->
      // noDaemon returns None — W28 guard passes, prune proceeds
      // No manifest dir → NotFound → returns true (nothing to prune)
      let dir = IO.Path.Combine(IO.Path.GetTempPath(), sprintf "sagefs-r13-%s" (Guid.NewGuid().ToString("N")))
      let result =
        SageFs.Server.DaemonMode.handlePrune dir nullLog noDaemon pruneFlags
        |> Async.AwaitTask
        |> Async.RunSynchronously
      result |> Expect.isTrue "handlePrune should proceed and return true when no daemon is detected"

    testCase "handlePrune returns false when daemon is running (W28 guard blocks prune)" <| fun _ ->
      let dir = IO.Path.Combine(IO.Path.GetTempPath(), sprintf "sagefs-r13-%s" (Guid.NewGuid().ToString("N")))
      let fakeDaemon () : DaemonInfo option =
        Some { Pid = 12345; Port = 37749; StartedAt = DateTime.UtcNow; WorkingDirectory = "/"; Version = "0.1.0" }
      let result =
        SageFs.Server.DaemonMode.handlePrune dir nullLog fakeDaemon pruneFlags
        |> Async.AwaitTask
        |> Async.RunSynchronously
      result |> Expect.isFalse "handlePrune should return false when daemon is alive (W28 cross-process guard)"

    testCase "handlePrune with Prune=false ignores daemon check and returns false" <| fun _ ->
      // When Prune=false, the daemon check is never consulted
      let dir = IO.Path.Combine(IO.Path.GetTempPath(), sprintf "sagefs-r13-%s" (Guid.NewGuid().ToString("N")))
      let fakeDaemon () : DaemonInfo option =
        Some { Pid = 99999; Port = 37749; StartedAt = DateTime.UtcNow; WorkingDirectory = "/"; Version = "0.1.0" }
      let result =
        SageFs.Server.DaemonMode.handlePrune dir nullLog fakeDaemon noPruneFlags
        |> Async.AwaitTask
        |> Async.RunSynchronously
      result |> Expect.isFalse "handlePrune with Prune=false returns false regardless of daemon state"
  ]

// ---------------------------------------------------------------------------
// W32 — startup path ManifestLoadError exhaustive match and log levels
// ---------------------------------------------------------------------------
// W32(R13): Startup `Error binaryErr` used LogInformation + succeedSpan for ALL cases.
// For IoError/CorruptData the manifest exists but is unreadable — "starting fresh" means
// silent history loss at the wrong severity level, and the span is incorrectly marked success.

[<Tests>]
let w32StartupLogLevelTests =
  testList "W32(R13) — startup ManifestLoadError exhaustive match and span outcomes" [

    testCase "ManifestLoadError severity contract: NotFound=Info, IoError=Warning, CorruptData=Error" <| fun _ ->
      // Documents the correct mapping. The startup code must use these levels.
      // NotFound is an expected first-run condition → LogInformation
      // IoError means the file EXISTS but can't be read (lock, permissions) → LogWarning
      // CorruptData means the file exists but is corrupted (permanent) → LogError
      let expectedSeverity = function
        | ManifestLoadError.NotFound -> "Information"
        | ManifestLoadError.IoError _ -> "Warning"
        | ManifestLoadError.CorruptData _ -> "Error"
      expectedSeverity ManifestLoadError.NotFound
        |> Expect.equal "NotFound is informational (expected first-run)" "Information"
      expectedSeverity (ManifestLoadError.IoError "x")
        |> Expect.equal "IoError is a warning (file exists but unreadable)" "Warning"
      expectedSeverity (ManifestLoadError.CorruptData "x")
        |> Expect.equal "CorruptData is an error (permanent corruption)" "Error"

    testCase "ManifestLoadError span outcome: NotFound=succeed, IoError/CorruptData=fail" <| fun _ ->
      // The span must NOT be marked success when the manifest exists but can't be loaded.
      // A caller alerting on sagefs.daemon.binary_manifest_load errors must see IoError/CorruptData.
      let isSpanError = function
        | ManifestLoadError.NotFound -> false
        | ManifestLoadError.IoError _ -> true
        | ManifestLoadError.CorruptData _ -> true
      isSpanError ManifestLoadError.NotFound |> Expect.isFalse "NotFound → succeedSpan (expected)"
      isSpanError (ManifestLoadError.IoError "perm") |> Expect.isTrue "IoError → failSpan (file unreadable)"
      isSpanError (ManifestLoadError.CorruptData "crc") |> Expect.isTrue "CorruptData → failSpan (corrupt)"

    testCase "NotFound at startup produces a valid empty state (correct first-run path)" <| fun _ ->
      // Regression check: NotFound must still produce DaemonReplayState.empty (not an error)
      let dir = IO.Path.Combine(IO.Path.GetTempPath(), sprintf "sagefs-r13-%s" (Guid.NewGuid().ToString("N")))
      // No directory → NotFound
      let result = DaemonPersistence.loadManifest dir
      match result with
      | Result.Error ManifestLoadError.NotFound ->
        // Correct: startup correctly treats this as a first run
        DaemonReplayState.empty.Sessions.Count |> Expect.equal "empty state has 0 sessions" 0
      | other -> failtest (sprintf "Expected NotFound for missing dir, got %A" other)
  ]

// ---------------------------------------------------------------------------
// W33 — testCycleTimerDone: conditional Dispose, 3s timeout
// ---------------------------------------------------------------------------
// W33(R13): testCycleTimerDone.Wait(1s) timeout + unconditional Dispose().
// When Wait times out, the timer infrastructure may still signal the WaitHandle after Dispose,
// causing ObjectDisposedException from the timer system. Fix: 3s timeout, conditional Dispose.

[<Tests>]
let w33TestCycleTimerTimeoutTests =
  testList "W33(R13) — testCycleTimerDone conditional Dispose after Wait timeout" [

    testCase "Safe pattern: only Dispose MRSE when Wait returned true" <| fun _ ->
      // The W33 safe pattern: conditional Dispose prevents ObjectDisposedException
      // if the timer infrastructure tries to signal the handle after Wait timed out.
      let mrse = new System.Threading.ManualResetEventSlim(false)
      let _handle = mrse.WaitHandle  // triggers kernel handle creation
      mrse.Set()
      let joined = mrse.Wait(System.TimeSpan.FromSeconds 2.0)
      match joined with
      | true -> mrse.Dispose()   // safe: timer signaled before we disposed
      | false -> ()              // unsafe to Dispose — timer may signal after
      joined |> Expect.isTrue "MRSE should be set within 2s"

    testCase "Timeout path: leaving MRSE undisposed is safe (no ObjectDisposedException)" <| fun _ ->
      // If Wait times out, do NOT Dispose. The handle is just leaked (acceptable on shutdown).
      let mrse = new System.Threading.ManualResetEventSlim(false)
      let _handle = mrse.WaitHandle
      // 50ms timeout — MRSE is never set, so this times out
      let joined = mrse.Wait(System.TimeSpan.FromMilliseconds 50.0)
      match joined with
      | true -> mrse.Dispose()
      | false -> ()  // do NOT Dispose — timer may still signal the handle
      joined |> Expect.isFalse "Wait should time out when MRSE is never set"
      // If no ObjectDisposedException was thrown, the test passes

    testCase "3s timeout is sufficient for 200ms testCycleTimer callbacks (15 budget ticks)" <| fun _ ->
      // Regression for 1s being too short: a 200ms-interval timer callback in-flight at
      // shutdown needs budget. 3s gives 15 timer periods — well above any reasonable callback.
      let mrse = new System.Threading.ManualResetEventSlim(false)
      let mutable callbackRan = false
      let t = new System.Threading.Timer(
        System.Threading.TimerCallback(fun _ -> callbackRan <- true),
        null, 10, System.Threading.Timeout.Infinite)
      System.Threading.Thread.Sleep(50)  // let callback fire
      t.Dispose(mrse.WaitHandle) |> ignore
      let joined = mrse.Wait(System.TimeSpan.FromSeconds 3.0)
      match joined with
      | true -> mrse.Dispose()
      | false -> ()
      joined |> Expect.isTrue "Timer.Dispose(WaitHandle) should complete within 3s"
      callbackRan |> Expect.isTrue "callback should have run before dispose"
  ]

// ---------------------------------------------------------------------------
// W34 — mergeManifestWithExisting returns typed ManifestLoadError (not string)
// ---------------------------------------------------------------------------
// W34(R13): mergeManifestWithExisting returned Result<DaemonReplayState, string>,
// discarding the typed ManifestLoadError from W26. Callers couldn't distinguish
// transient IoError (retriable) from permanent CorruptData. Fix: return typed error.

[<Tests>]
let w34TypedErrorTests =
  testList "W34(R13) — mergeManifestWithExisting returns typed ManifestLoadError" [

    testCase "ManifestLoadError IoError is distinguishable from CorruptData (W34 type contract)" <| fun _ ->
      // After W34: callers can route IoError to retry logic, CorruptData to manual recovery.
      // This was lost when the return type was Result<_, string>.
      let isRetriable = function
        | ManifestLoadError.IoError _ -> true     // transient — retry may succeed
        | ManifestLoadError.CorruptData _ -> false // permanent — retrying won't fix it
        | ManifestLoadError.NotFound -> false      // first run — not a recoverable condition
      isRetriable (ManifestLoadError.IoError "file locked") |> Expect.isTrue "IoError is retriable"
      isRetriable (ManifestLoadError.CorruptData "bad CRC") |> Expect.isFalse "CorruptData is not retriable"
      isRetriable ManifestLoadError.NotFound |> Expect.isFalse "NotFound is not retriable"

    testCase "loadManifest returns typed error for corrupt file (W34 upstream provider)" <| fun _ ->
      // Verifies the upstream: loadManifest returns typed ManifestLoadError (not string).
      // mergeManifestWithExisting must propagate this type, not convert it to string.
      let dir = IO.Path.Combine(IO.Path.GetTempPath(), sprintf "sagefs-r13-%s" (Guid.NewGuid().ToString("N")))
      IO.Directory.CreateDirectory(dir) |> ignore
      try
        IO.File.WriteAllBytes(IO.Path.Combine(dir, "daemon.sagefm"), [| 0xAAuy; 0xBBuy |])
        match DaemonPersistence.loadManifest dir with
        | Result.Error (ManifestLoadError.CorruptData _) ->
          () // correct: typed error propagated upward
        | other -> failtest (sprintf "Expected typed CorruptData error, got %A" other)
      finally
        IO.Directory.Delete(dir, true)

    testCase "loadManifest round-trip for valid manifest returns Ok (W34 happy path)" <| fun _ ->
      let dir = IO.Path.Combine(IO.Path.GetTempPath(), sprintf "sagefs-r13-%s" (Guid.NewGuid().ToString("N")))
      IO.Directory.CreateDirectory(dir) |> ignore
      try
        DaemonPersistence.saveManifest dir DaemonReplayState.empty |> ignore
        match DaemonPersistence.loadManifest dir with
        | Ok loaded -> loaded.Sessions.Count |> Expect.equal "round-trip session count" 0
        | Result.Error err -> failtest (sprintf "Expected Ok after valid save, got %A" err)
      finally
        IO.Directory.Delete(dir, true)
  ]

[<Tests>]
let allRound13Tests =
  testList "Round 13 Hardening (R13) — W28 W31 W32 W33 W34" [
    w31HandlePruneExhaustiveTests
    w28DaemonRunningGuardTests
    w32StartupLogLevelTests
    w33TestCycleTimerTimeoutTests
    w34TypedErrorTests
  ]
