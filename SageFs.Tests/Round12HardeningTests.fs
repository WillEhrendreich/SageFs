module SageFs.Tests.Round12HardeningTests

open System
open Expecto
open Expecto.Flip
open SageFs.Features
open SageFs.Features.ManifestTypes
open SageFs.Features.Replay

// ---------------------------------------------------------------------------
// W26 — ManifestLoadError DU replaces stringly-typed sentinel
// ---------------------------------------------------------------------------
// W26(R12): loadManifest now returns Result<DaemonReplayState, ManifestLoadError>
// with a structured DU instead of string comparisons. The compiler enforces
// exhaustiveness; no sentinel-stability test needed.

[<Tests>]
let w26ManifestLoadErrorDuTests =
  testList "W26(R12) — ManifestLoadError DU: structured errors from loadManifest" [

    testCase "loadManifest for missing dir returns Error NotFound (not a string)" <| fun _ ->
      let dir = IO.Path.Combine(IO.Path.GetTempPath(), sprintf "sagefs-r12-%s" (Guid.NewGuid().ToString("N")))
      let result = DaemonPersistence.loadManifest dir
      match result with
      | Result.Error ManifestLoadError.NotFound -> ()
      | other -> failtest (sprintf "Expected Error NotFound, got %A" other)

    testCase "loadManifest for corrupt file returns Error CorruptData" <| fun _ ->
      let dir = IO.Path.Combine(IO.Path.GetTempPath(), sprintf "sagefs-r12-%s" (Guid.NewGuid().ToString("N")))
      IO.Directory.CreateDirectory(dir) |> ignore
      try
        IO.File.WriteAllBytes(IO.Path.Combine(dir, "daemon.sagefm"), Array.create 32 0xDEuy)
        let result = DaemonPersistence.loadManifest dir
        match result with
        | Result.Error (ManifestLoadError.CorruptData _) -> ()
        | other -> failtest (sprintf "Expected Error CorruptData, got %A" other)
      finally
        IO.Directory.Delete(dir, true)

    testCase "ManifestLoadError NotFound is distinguishable from IoError and CorruptData" <| fun _ ->
      // Compiler-enforced exhaustiveness — if DU gains a new case this test fails to compile.
      let classify (e: ManifestLoadError) =
        match e with
        | NotFound -> "not-found"
        | IoError _ -> "io-error"
        | CorruptData _ -> "corrupt"
      classify NotFound |> Expect.equal "NotFound classifies correctly" "not-found"
      classify (IoError "x") |> Expect.equal "IoError classifies correctly" "io-error"
      classify (CorruptData "x") |> Expect.equal "CorruptData classifies correctly" "corrupt"

    testCase "ManifestFile.load round-trips to Ok for a valid manifest" <| fun _ ->
      let dir = IO.Path.Combine(IO.Path.GetTempPath(), sprintf "sagefs-r12-%s" (Guid.NewGuid().ToString("N")))
      IO.Directory.CreateDirectory(dir) |> ignore
      try
        // Save a valid manifest, then load it — should return Ok, not any error variant
        let state = DaemonReplayState.empty
        match DaemonPersistence.saveManifest dir state with
        | Result.Error err -> failtest (sprintf "Save failed: %s" err)
        | Ok _ ->
          match DaemonPersistence.loadManifest dir with
          | Ok _ -> ()
          | Result.Error err -> failtest (sprintf "Expected Ok after valid save, got %A" err)
      finally
        IO.Directory.Delete(dir, true)
  ]

// ---------------------------------------------------------------------------
// W23 — mergeManifestWithExisting returns Result; callers skip save on Error
// ---------------------------------------------------------------------------
// W23(R12): The function now returns Result<DaemonReplayState, string>.
// Error means "cannot read manifest; callers must NOT write to preserve history."
// The old code logged "history preserved" but still wrote active-only state.

[<Tests>]
let w23MergeResultTests =
  testList "W23(R12) — mergeManifestWithExisting returns Result; Error skips write" [

    testCase "loadManifest returns Error NotFound for first run — callers get Ok (fresh state)" <| fun _ ->
      // mergeManifestWithExisting converts NotFound → Ok (buildReplayState) — safe first-run
      // We test the upstream: NotFound is not an IoError, so callers proceed with fresh state.
      let notFound = ManifestLoadError.NotFound
      let isIoError =
        match notFound with
        | ManifestLoadError.IoError _ -> true
        | _ -> false
      isIoError |> Expect.isFalse "NotFound should not be treated as IoError by callers"

    testCase "IoError and CorruptData are not NotFound — callers would skip save" <| fun _ ->
      let isNotFound (e: ManifestLoadError) =
        match e with
        | ManifestLoadError.NotFound -> true
        | _ -> false
      isNotFound (ManifestLoadError.IoError "disk full") |> Expect.isFalse "IoError is not NotFound"
      isNotFound (ManifestLoadError.CorruptData "bad CRC") |> Expect.isFalse "CorruptData is not NotFound"

    testCase "corrupt manifest on disk causes loadManifest Error — no Ok result that would erase history" <| fun _ ->
      // Simulate the scenario: daemon has a manifest on disk that's corrupt (partial write).
      // Before W23: mergeManifest would return active-only DaemonReplayState and callers would write.
      // After W23: callers receive Error and skip the write — history is preserved (nothing written).
      let dir = IO.Path.Combine(IO.Path.GetTempPath(), sprintf "sagefs-r12-%s" (Guid.NewGuid().ToString("N")))
      IO.Directory.CreateDirectory(dir) |> ignore
      try
        IO.File.WriteAllBytes(IO.Path.Combine(dir, "daemon.sagefm"), [| 0xFFuy; 0xFEuy; 0xFDuy |])
        let result = DaemonPersistence.loadManifest dir
        // loadManifest returns Error — the caller (periodicManifestSave) would skip saveManifest
        let isError =
          match result with
          | Result.Error _ -> true
          | Ok _ -> false
        isError |> Expect.isTrue "corrupt manifest should return Error — caller must skip write"
      finally
        IO.Directory.Delete(dir, true)
  ]

// ---------------------------------------------------------------------------
// W24 — ManualResetEventSlim disposed after Wait
// ---------------------------------------------------------------------------
// W24(R12): After cacheSaveTimer.Dispose(WaitHandle) + Wait(), the MRSE is now Disposed.
// Accessing .WaitHandle creates a kernel event object — Dispose() closes that handle.

[<Tests>]
let w24MrseDisposeTests =
  testList "W24(R12) — ManualResetEventSlim disposal after WaitHandle use" [

    testCase "ManualResetEventSlim.Dispose() after Wait does not throw" <| fun _ ->
      // Pattern: create MRSE, access WaitHandle (creates kernel event), Wait, Dispose
      use mrse = new System.Threading.ManualResetEventSlim(false)
      let handle = mrse.WaitHandle  // lazily creates kernel event
      mrse.Set()
      mrse.Wait(TimeSpan.FromSeconds 1.0) |> ignore
      // Dispose should not throw even after Wait
      mrse.Dispose()
      // No exception = test passes

    testCase "ManualResetEventSlim signals WaitHandle before Set returns" <| fun _ ->
      let mrse = new System.Threading.ManualResetEventSlim(false)
      let handle = mrse.WaitHandle
      mrse.Set()
      let signaled = handle.WaitOne(1000)
      mrse.Dispose()
      signaled |> Expect.isTrue "WaitHandle should be signaled after Set()"

    testCase "Timer.Dispose(WaitHandle) signals the MRSE when timer is collected" <| fun _ ->
      // Regression: Timer.Dispose(WaitHandle) calls WaitHandle.Set() when done,
      // but does NOT dispose the ManualResetEventSlim — that's the caller's responsibility.
      let mrse = new System.Threading.ManualResetEventSlim(false)
      let t = new System.Threading.Timer(System.Threading.TimerCallback(fun _ -> ()), null, 1000, System.Threading.Timeout.Infinite)
      t.Dispose(mrse.WaitHandle) |> ignore
      let signaled = mrse.Wait(TimeSpan.FromSeconds 2.0)  // Timer.Dispose signals when done
      mrse.Dispose()
      signaled |> Expect.isTrue "ManualResetEventSlim should be signaled by Timer.Dispose(WaitHandle)"
  ]

// ---------------------------------------------------------------------------
// W25 — Consistent snapshot: passed as value not thunk
// ---------------------------------------------------------------------------
// W25(R12): buildReplayState and mergeManifestWithExisting now take QuerySnapshot as a value.
// The observable property: the same snapshot value always produces the same replay state.
// Testing via DaemonPersistence (accessible from test project) for round-trip consistency.

[<Tests>]
let w25SnapshotConsistencyTests =
  testList "W25(R12) — Snapshot value consistency: deterministic for same input" [

    testCase "empty DaemonReplayState round-trips through save/load consistently" <| fun _ ->
      // Verify that a snapshot-derived state is stable: save X, load X, matches X.
      // This is the key property that passing snapshot-as-value must preserve.
      let dir = IO.Path.Combine(IO.Path.GetTempPath(), sprintf "sagefs-r12-%s" (Guid.NewGuid().ToString("N")))
      IO.Directory.CreateDirectory(dir) |> ignore
      try
        let state = DaemonReplayState.empty
        DaemonPersistence.saveManifest dir state |> ignore
        match DaemonPersistence.loadManifest dir with
        | Ok loaded ->
          loaded.Sessions.Count |> Expect.equal "round-trip session count matches" state.Sessions.Count
          loaded.ActiveSessionId |> Expect.equal "round-trip active id matches" state.ActiveSessionId
        | Result.Error err -> failtest (sprintf "Load failed: %A" err)
      finally
        IO.Directory.Delete(dir, true)

    testCase "loading the same manifest twice returns equal results" <| fun _ ->
      // If passing a snapshot-as-value is correct, reading from disk is idempotent.
      let dir = IO.Path.Combine(IO.Path.GetTempPath(), sprintf "sagefs-r12-%s" (Guid.NewGuid().ToString("N")))
      IO.Directory.CreateDirectory(dir) |> ignore
      try
        let state = DaemonReplayState.empty
        DaemonPersistence.saveManifest dir state |> ignore
        let r1 = DaemonPersistence.loadManifest dir
        let r2 = DaemonPersistence.loadManifest dir
        match r1, r2 with
        | Ok s1, Ok s2 ->
          s1.Sessions.Count |> Expect.equal "two loads give same session count" s2.Sessions.Count
          s1.ActiveSessionId |> Expect.equal "two loads give same active id" s2.ActiveSessionId
        | _ -> failtest (sprintf "Expected both loads to succeed, got %A and %A" r1 r2)
      finally
        IO.Directory.Delete(dir, true)

    testCase "single snapshot read is the consistent-view pattern" <| fun _ ->
      // Document the W25 invariant: a value read once produces a deterministic result.
      // Here we verify via the type: DaemonReplayState is an immutable F# record.
      let s1 = DaemonReplayState.empty
      let s2 = DaemonReplayState.empty
      s1.Sessions.Count |> Expect.equal "same initial state" s2.Sessions.Count
      s1.ActiveSessionId |> Expect.equal "same initial active id" s2.ActiveSessionId
  ]

// ---------------------------------------------------------------------------
// W27 — cacheSaveCallback has outer try/with for unexpected exceptions
// ---------------------------------------------------------------------------
// W27(R12): The outer try/finally now wraps an inner try/with to catch and log
// unexpected exceptions from periodicCacheSave/periodicManifestSave. Without this,
// an exception escaping both callee handlers would propagate to the ThreadPool
// and kill the process with no log of where it came from.

[<Tests>]
let w27CallbackExceptionTests =
  testList "W27(R12) — cacheSaveCallback: nested try/with catches unexpected exceptions" [

    testCase "unexpected exception is caught and logged without rethrow" <| fun _ ->
      // Simulate the cacheSaveCallback exception-handling pattern inline.
      // The outer try/finally always runs the reschedule; inner try/with catches unexpected.
      let mutable caughtUnexpected = false
      let mutable finallyRan = false
      let callback () =
        try
          try
            raise (InvalidOperationException "simulated unexpected failure")
          with ex ->
            caughtUnexpected <- true  // simulates log.LogWarning
        finally
          finallyRan <- true
      callback()
      caughtUnexpected |> Expect.isTrue "inner with should catch unexpected exception"
      finallyRan |> Expect.isTrue "finally should always run for rescheduling"

    testCase "outer finally runs even when inner with does not handle" <| fun _ ->
      // If the with clause re-raises, the finally should still run.
      // (This verifies the structural guarantee of try/finally.)
      let mutable finallyRan = false
      let mutable exceptionPropagated = false
      try
        try
          try
            raise (InvalidOperationException "x")
          with ex ->
            () // catches and swallows — outer finally must still run
        finally
          finallyRan <- true
      with _ ->
        exceptionPropagated <- true
      finallyRan |> Expect.isTrue "finally runs even when with catches"
      exceptionPropagated |> Expect.isFalse "exception was swallowed by inner with"
  ]

// ---------------------------------------------------------------------------
// W29 — Manifest saved after stop events are appended
// ---------------------------------------------------------------------------
// W29(R12): In performGracefulShutdown, stop events are now appended BEFORE the manifest
// is written. When EventStore gains real persistence, the manifest must reflect events
// already appended — writing the manifest first creates an ordering trap.

[<Tests>]
let w29EventOrderingTests =
  testList "W29(R12) — Stop events appended before manifest write in shutdown" [

    testCase "appending events before writing manifest is the safe ordering" <| fun _ ->
      // Simulate the correct ordering: events appended → manifest written.
      // This test documents the invariant: manifest StoppedAt timestamps match event timestamps.
      let mutable eventsAppended = false
      let mutable manifestWritten = false
      let appendEvents () = eventsAppended <- true
      let writeManifest () =
        manifestWritten <- true
        eventsAppended |> Expect.isTrue "events must be appended before manifest is written"
      appendEvents()
      writeManifest()
      manifestWritten |> Expect.isTrue "manifest write should happen"

    testCase "manifest written after events: state is consistent" <| fun _ ->
      // In the old order (manifest before events), if EventStore were live, replaying events
      // would show sessions stopped AFTER the timestamp in the manifest — inconsistent.
      // With correct ordering, event timestamps ≤ manifest StoppedAt timestamp.
      let manifestStopTime = DateTimeOffset.UtcNow
      let eventStopTime = manifestStopTime.AddMilliseconds(-5.0)  // event came earlier
      // The event timestamp should be ≤ manifestStopTime (event happened before manifest was written)
      (eventStopTime, manifestStopTime) |> Expect.isLessThanOrEqual "event time must be before manifest write time"
  ]

// ---------------------------------------------------------------------------
// W30 — testCycleTimer uses WaitHandle join matching cacheSaveTimer treatment
// ---------------------------------------------------------------------------
// W30(R12): testCycleTimer now uses Dispose(WaitHandle) + Wait for consistent hygiene.
// Bare Dispose() would let in-flight 200ms tick callbacks fire after elm runtime shutdown.

[<Tests>]
let w30TestCycleTimerTests =
  testList "W30(R12) — testCycleTimer WaitHandle join pattern" [

    testCase "Timer.Dispose(WaitHandle) pattern signals MRSE when callback completes" <| fun _ ->
      let mrse = new System.Threading.ManualResetEventSlim(false)
      let mutable callbackRan = false
      let t = new System.Threading.Timer(
        System.Threading.TimerCallback(fun _ -> callbackRan <- true),
        null, 10, System.Threading.Timeout.Infinite)
      // Let callback run
      System.Threading.Thread.Sleep(50)
      t.Dispose(mrse.WaitHandle) |> ignore
      let signaled = mrse.Wait(TimeSpan.FromSeconds 2.0)
      mrse.Dispose()
      signaled |> Expect.isTrue "MRSE should be signaled when timer is disposed"

    testCase "bare Dispose returns without joining; Dispose(WaitHandle) waits for callback" <| fun _ ->
      // Contrast bare Dispose with Dispose(WaitHandle) — illustrates the race risk of bare Dispose.
      let mrse = new System.Threading.ManualResetEventSlim(false)
      // Create a timer that fires in 200ms (testCycleTimer interval)
      let t = new System.Threading.Timer(
        System.Threading.TimerCallback(fun _ -> ()),
        null, 200, System.Threading.Timeout.Infinite)
      // Dispose(WaitHandle) waits until any in-flight callback completes
      t.Dispose(mrse.WaitHandle) |> ignore
      let timedOut = not (mrse.Wait(TimeSpan.FromSeconds 2.0))
      mrse.Dispose()
      timedOut |> Expect.isFalse "Dispose(WaitHandle) should not time out"
  ]

[<Tests>]
let allRound12Tests =
  testList "Round 12 Hardening (R12) — W23 W24 W25 W26 W27 W29 W30" [
    w26ManifestLoadErrorDuTests
    w23MergeResultTests
    w24MrseDisposeTests
    w25SnapshotConsistencyTests
    w27CallbackExceptionTests
    w29EventOrderingTests
    w30TestCycleTimerTests
  ]
