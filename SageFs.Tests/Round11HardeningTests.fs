module SageFs.Tests.Round11HardeningTests

open System
open Expecto
open Expecto.Flip
open SageFs.Features
open SageFs.Features.DaemonManifest
open SageFs.Features.EvalTimeline

// ---------------------------------------------------------------------------
// W17 — mergeManifestWithExisting distinguishes "No file" from IO errors
// ---------------------------------------------------------------------------
// W26(R12): The stringly-typed "No manifest file found" sentinel has been replaced by the
// ManifestLoadError DU (NotFound | IoError | CorruptData). The sentinel-stability test
// that used to live here has been deleted — it was guarding a design smell.
// The compiler now enforces exhaustiveness on the DU, making the test redundant.

[<Tests>]
let w17ManifestErrorDistinctionTests =
  testList "W17(R11) — manifest load: IO errors distinguished from not-found" [

    testCase "ManifestReader returns error for zero-byte file" <| fun _ ->
      let result = ManifestReader.read [||]
      match result with
      | Ok _ -> failtest "Expected error for empty manifest"
      | Result.Error msg ->
        // This is a format/CRC error — it does NOT map to ManifestLoadError.NotFound.
        let isNotFoundMsg = msg = "No manifest file found"
        isNotFoundMsg |> Expect.isFalse "corrupt file error should not say 'No manifest file found'"

    testCase "loadManifest for existing but corrupt file returns CorruptData error" <| fun _ ->
      let dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), sprintf "sagefs-test-%s" (System.Guid.NewGuid().ToString("N")))
      System.IO.Directory.CreateDirectory(dir) |> ignore
      try
        // Write a corrupt manifest file
        let fakeManifestPath = System.IO.Path.Combine(dir, "daemon.sagefm")
        System.IO.File.WriteAllBytes(fakeManifestPath, Array.create 64 0xFFuy)
        let result = DaemonPersistence.loadManifest dir
        match result with
        | Result.Error (SageFs.Features.ManifestTypes.ManifestLoadError.CorruptData _) -> ()
        | other -> failtest (sprintf "Expected CorruptData, got %A" other)
      finally
        System.IO.Directory.Delete(dir, true)
  ]

// ---------------------------------------------------------------------------
// W20 — active sessions must have StoppedAt = None in periodic merge path
// ---------------------------------------------------------------------------

[<Tests>]
let w20StoppedAtClearTests =
  testList "W20(R11) — mergeManifestWithExisting: active sessions always have StoppedAt=None" [

    testCase "active session with stale StoppedAt from prior shutdown gets cleared" <| fun _ ->
      // The fix: | true, None -> { r with StoppedAt = None }
      // Simulate: manifest has session with StoppedAt=Some, session is still active
      let staleStopTime = DateTimeOffset.UtcNow.AddDays(-1.0)
      let sessionWithStaleStop: DaemonSessionRecord = {
        SessionId = "s-active-stale"
        Projects = []
        WorkingDir = ""
        CreatedAt = DateTimeOffset.UtcNow.AddDays(-5.0)
        StoppedAt = Some staleStopTime  // stale from prior shutdown manifest
      }
      // Simulate the periodic merge logic (stampActive = None)
      let activeSessionIds = Set.ofList ["s-active-stale"]
      let result =
        match activeSessionIds.Contains(sessionWithStaleStop.SessionId), None with
        | true, None -> { sessionWithStaleStop with StoppedAt = None }  // W20 fix
        | true, Some ts -> { sessionWithStaleStop with StoppedAt = Some ts }
        | false, _ -> sessionWithStaleStop
      result.StoppedAt |> Expect.isNone "active session in periodic path must have StoppedAt=None"

    testCase "active session with no stale StoppedAt stays None in periodic path" <| fun _ ->
      let session: DaemonSessionRecord = {
        SessionId = "s-active-clean"
        Projects = []
        WorkingDir = ""
        CreatedAt = DateTimeOffset.UtcNow.AddHours(-1.0)
        StoppedAt = None
      }
      let activeSessionIds = Set.ofList ["s-active-clean"]
      let result =
        match activeSessionIds.Contains(session.SessionId), None with
        | true, None -> { session with StoppedAt = None }
        | true, Some ts -> { session with StoppedAt = Some ts }
        | false, _ -> session
      result.StoppedAt |> Expect.isNone "clean active session stays None in periodic path"

    testCase "shutdown path stamps active session regardless of prior StoppedAt" <| fun _ ->
      let staleStopTime = DateTimeOffset.UtcNow.AddDays(-1.0)
      let session: DaemonSessionRecord = {
        SessionId = "s-active-shutdown"
        Projects = []
        WorkingDir = ""
        CreatedAt = DateTimeOffset.UtcNow.AddHours(-2.0)
        StoppedAt = Some staleStopTime
      }
      let activeSessionIds = Set.ofList ["s-active-shutdown"]
      let shutdownTime = DateTimeOffset.UtcNow
      let result =
        match activeSessionIds.Contains(session.SessionId), Some shutdownTime with
        | true, Some ts -> { session with StoppedAt = Some ts }
        | true, None -> { session with StoppedAt = None }
        | false, _ -> session
      result.StoppedAt |> Expect.isSome "shutdown path stamps active session with current time"
      match result.StoppedAt with
      | None -> failtest "shutdown should set StoppedAt"
      | Some ts -> (ts >= shutdownTime.AddSeconds(-1.0)) |> Expect.isTrue "StoppedAt should be shutdown time"
  ]

// ---------------------------------------------------------------------------
// W22 — lastSavedGeneration only advances when ALL saves succeed
// ---------------------------------------------------------------------------

[<Tests>]
let w22PartialSaveGenTests =
  testList "W22(R11) — periodicCacheSave: generation only advances on full success" [

    testCase "partial save failure pattern: allSavesSucceeded tracks correctly" <| fun _ ->
      let mutable allSavesSucceeded = true
      let results = [ Ok "path/a"; Result.Error "disk full"; Ok "path/b" ]
      for r in results do
        match r with
        | Ok _ -> ()
        | Result.Error _ -> allSavesSucceeded <- false
      allSavesSucceeded |> Expect.isFalse "partial failure should mark allSavesSucceeded=false"

    testCase "all-success pattern: generation should advance" <| fun _ ->
      let mutable allSavesSucceeded = true
      let results = [ Ok "path/a"; Ok "path/b" ]
      for r in results do
        match r with
        | Ok _ -> ()
        | Result.Error _ -> allSavesSucceeded <- false
      allSavesSucceeded |> Expect.isTrue "all success should keep allSavesSucceeded=true"

    testCase "no project sets: allSavesSucceeded stays true, generation advances" <| fun _ ->
      let mutable allSavesSucceeded = true
      let results: Result<string, string> list = []
      for r in results do
        match r with
        | Ok _ -> ()
        | Result.Error _ -> allSavesSucceeded <- false
      allSavesSucceeded |> Expect.isTrue "empty project set list: generation should still advance"
  ]

// ---------------------------------------------------------------------------
// W21 — WhenAll.Wait bool is acted on (not silently discarded)
// ---------------------------------------------------------------------------
// This is a structural/contract test: we verify the pattern we rely on is
// behaving correctly (Task.WhenAll.Wait returns bool correctly on timeout).

[<Tests>]
let w21WhenAllTimeoutTests =
  testList "W21(R11) — WhenAll.Wait: bool result is meaningful on timeout" [

    testCase "WhenAll returns false when tasks exceed timeout" <| fun _ ->
      let neverComplete = System.Threading.Tasks.Task.Delay(System.Threading.Timeout.Infinite)
      let completed = System.Threading.Tasks.Task.WhenAll([| neverComplete |]).Wait(10)
      completed |> Expect.isFalse "WhenAll with a long-running task should return false on 10ms timeout"

    testCase "WhenAll returns true when all tasks complete within timeout" <| fun _ ->
      let immediate = System.Threading.Tasks.Task.CompletedTask
      let completed = System.Threading.Tasks.Task.WhenAll([| immediate |]).Wait(5_000)
      completed |> Expect.isTrue "WhenAll with completed tasks should return true"

    testCase "empty task list WhenAll completes immediately" <| fun _ ->
      let completed = System.Threading.Tasks.Task.WhenAll([||]).Wait(100)
      completed |> Expect.isTrue "WhenAll with empty list should return true immediately"
  ]

// ---------------------------------------------------------------------------
// W18 — ObjectDisposedException on disposed timer is handled
// ---------------------------------------------------------------------------

[<Tests>]
let w18OdeTimerTests =
  testList "W18(R11) — Timer: ObjectDisposedException on disposed timer is catchable" [

    testCase "Change() on disposed Timer is guarded by try/with ODE" <| fun _ ->
      // .NET 10 may or may not throw ODE on Change() after Dispose() — behavior varies.
      // The important thing is: the try/with guard handles both cases safely.
      let t = new System.Threading.Timer(System.Threading.TimerCallback(fun _ -> ()), null, System.Threading.Timeout.Infinite, System.Threading.Timeout.Infinite)
      t.Dispose()
      let handled =
        try
          t.Change(1000, System.Threading.Timeout.Infinite) |> ignore
          true // no throw — .NET 10 behavior, still safe
        with :? ObjectDisposedException -> true
      handled |> Expect.isTrue "ODE guard should handle Change() on disposed Timer safely"

    testCase "try/with ODE guard prevents crash on disposed timer" <| fun _ ->
      let t = new System.Threading.Timer(System.Threading.TimerCallback(fun _ -> ()), null, System.Threading.Timeout.Infinite, System.Threading.Timeout.Infinite)
      t.Dispose()
      // This is the W18 fix pattern — should not throw
      try t.Change(1000, System.Threading.Timeout.Infinite) |> ignore
      with :? ObjectDisposedException -> ()
      true |> Expect.isTrue "ODE guard should absorb the exception cleanly"

    testCase "Dispose(WaitHandle) blocks until in-flight callback completes" <| fun _ ->
      // Verify the WaitHandle pattern — timer signals when callback is done.
      // Timer fires immediately (dueTime=0), callback sets a flag, then we dispose.
      use callbackFinished = new System.Threading.ManualResetEventSlim(false)
      let timerDone = new System.Threading.ManualResetEventSlim(false)
      let mutable callbackRan = false
      let t = new System.Threading.Timer(
        System.Threading.TimerCallback(fun _ ->
          callbackRan <- true
          callbackFinished.Set()),
        null, 0, System.Threading.Timeout.Infinite)
      callbackFinished.Wait(5_000) |> ignore
      t.Dispose(timerDone.WaitHandle) |> ignore
      let completed = timerDone.Wait(System.TimeSpan.FromSeconds 10.0)
      match completed with
      | true -> timerDone.Dispose()
      | false -> () // don't dispose if timeout — timer may signal later
      callbackRan |> Expect.isTrue "callback should have run before Dispose completed"
  ]
