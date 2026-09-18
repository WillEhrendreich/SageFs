module SageFs.Tests.DaemonStateChangeContractTests

/// Contract tests for SseEvent event payloads.
///
/// Session-isolation blocker (quality-gap plan): HotReloadChanged previously
/// carried NO session identity, so downstream code fetched hot-reload state
/// from a global "active session" — an event for session B pushed session A's
/// state whenever A was the active tab. FileReloaded carried only a path, so a
/// file reload could not be attributed to the session whose live-test state
/// changed (two sessions may share one working directory).
///
/// These tests pin the new wire contract: both events serialize WITH their
/// session ID so consumers can filter and reject mismatched snapshots.

open Expecto
open Expecto.Flip
open SageFs
open SageFs.Server

/// 8-char lowercase hex, matching WorkerProtocol.SessionId.validate.
let private sid (raw: string) =
  match WorkerProtocol.SessionId.validate raw with
  | Ok s -> s
  | Error e -> failwithf "test session id %s invalid: %s" raw e

[<Tests>]
let daemonStateChangeContractTests =
  testList "SseEvent event contract" [

    testCase "HotReloadChanged serializes with the affected session ID" <| fun _ ->
      let s = sid "a1b2c3d4"
      let json = SseEvent.toJson (SseEvent.HotReloadChanged s)
      json
      |> Expect.stringContains "payload should carry the session id" "\"sessionId\":\"a1b2c3d4\""
      json
      |> Expect.stringContains "payload should keep the hotReloadChanged marker" "\"hotReloadChanged\":true"

    testCase "FileReloaded serializes with the owning session ID and path" <| fun _ ->
      let s = sid "deadbeef"
      let json = SseEvent.toJson (SseEvent.FileReloaded (s, "C:\\proj\\src\\Lib.fs"))
      json
      |> Expect.stringContains "payload should carry the session id" "\"sessionId\":\"deadbeef\""
      json
      |> Expect.stringContains "payload should carry the file path" "\"fileReloaded\":\"C:\\\\proj\\\\src\\\\Lib.fs\""

    testCase "HotReloadChanged for session B does not match session A's payload" <| fun _ ->
      // Two sessions toggling hot reload produce distinguishable payloads —
      // a client viewing session A can reject session B's event by sessionId.
      let a = SseEvent.toJson (SseEvent.HotReloadChanged (sid "11111111"))
      let b = SseEvent.toJson (SseEvent.HotReloadChanged (sid "22222222"))
      a
      |> Expect.stringContains "A payload should name A" "\"sessionId\":\"11111111\""
      b
      |> Expect.stringContains "B payload should name B" "\"sessionId\":\"22222222\""
      (a.Contains("\"sessionId\":\"22222222\""))
      |> Expect.isFalse "A's payload must never name session B"

    testCase "FileReloaded for shared working dir distinguishes owning sessions" <| fun _ ->
      // Two sessions can share one working dir; the watcher manager attributes
      // a reloaded path to each owning session. The payloads must differ by
      // session even for the identical path.
      let sharedPath = "C:\\shared\\src\\Lib.fs"
      let forA = SseEvent.toJson (SseEvent.FileReloaded (sid "aaaa1111", sharedPath))
      let forB = SseEvent.toJson (SseEvent.FileReloaded (sid "bbbb2222", sharedPath))
      forA
      |> Expect.stringContains "session A payload should name A" "\"sessionId\":\"aaaa1111\""
      forB
      |> Expect.stringContains "session B payload should name B" "\"sessionId\":\"bbbb2222\""
  ]

// LiveTestWatcherStaleGuard and its tests were deleted (roast-9 #10): the
// epoch/generation guard is now structurally impossible to need. A single
// mailbox owner processes messages FIFO, so a FileSaved posted by a watcher
// that is later torn down always sits behind the RemoveDirectory/StopWatch
// that tore it down — by the time DebounceElapsed drains it, the resolved
// session claim is already gone and the path is dropped. See
// SageFs.Core/LiveTestWatcherCore.fs and SageFs.Tests/LiveTestWatcherCoreTests.fs.


// ── Behavioral: LiveTestWatcherManager session attribution ──────────────
// Two real-FileSystemWatcher Integration tests previously lived here ("file
// save in a shared dir fires FileReloaded for every owning session" and
// "removing one session's claim stops only its FileReloaded events") —
// each spun up a real watcher, wrote probe files, and polled up to 10s.
// Both are superseded by a DST harness that folds the REAL
// LiveTestWatcherCore.apply/isUnderWatchedDir/sessionsForPath (the exact
// pure functions the watcher shell calls) through the identical claim/save/
// drain scenarios in milliseconds, with two twin routers proving the
// invariants have teeth. See SageFs.Simulation/FileReloadRoutingSim.fs and
// SageFs.Tests/FileReloadRoutingSimTests.fs. The pure serialization test
// above ("FileReloaded for shared working dir distinguishes owning
// sessions") stays — it pins the wire format, which the DST does not
// exercise.
