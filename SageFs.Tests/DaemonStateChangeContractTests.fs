module SageFs.Tests.DaemonStateChangeContractTests

/// Contract tests for DaemonStateChange event payloads.
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
open SageFs.Server.DaemonMode

/// 8-char lowercase hex, matching WorkerProtocol.SessionId.validate.
let private sid (raw: string) =
  match WorkerProtocol.SessionId.validate raw with
  | Ok s -> s
  | Error e -> failwithf "test session id %s invalid: %s" raw e

[<Tests>]
let daemonStateChangeContractTests =
  testList "DaemonStateChange event contract" [

    testCase "HotReloadChanged serializes with the affected session ID" <| fun _ ->
      let s = sid "a1b2c3d4"
      let json = DaemonStateChange.toJson (DaemonStateChange.HotReloadChanged s)
      json
      |> Expect.stringContains "payload should carry the session id" "\"sessionId\":\"a1b2c3d4\""
      json
      |> Expect.stringContains "payload should keep the hotReloadChanged marker" "\"hotReloadChanged\":true"

    testCase "FileReloaded serializes with the owning session ID and path" <| fun _ ->
      let s = sid "deadbeef"
      let json = DaemonStateChange.toJson (DaemonStateChange.FileReloaded (s, "C:\\proj\\src\\Lib.fs"))
      json
      |> Expect.stringContains "payload should carry the session id" "\"sessionId\":\"deadbeef\""
      json
      |> Expect.stringContains "payload should carry the file path" "\"fileReloaded\":\"C:\\\\proj\\\\src\\\\Lib.fs\""

    testCase "HotReloadChanged for session B does not match session A's payload" <| fun _ ->
      // Two sessions toggling hot reload produce distinguishable payloads —
      // a client viewing session A can reject session B's event by sessionId.
      let a = DaemonStateChange.toJson (DaemonStateChange.HotReloadChanged (sid "11111111"))
      let b = DaemonStateChange.toJson (DaemonStateChange.HotReloadChanged (sid "22222222"))
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
      let forA = DaemonStateChange.toJson (DaemonStateChange.FileReloaded (sid "aaaa1111", sharedPath))
      let forB = DaemonStateChange.toJson (DaemonStateChange.FileReloaded (sid "bbbb2222", sharedPath))
      forA
      |> Expect.stringContains "session A payload should name A" "\"sessionId\":\"aaaa1111\""
      forB
      |> Expect.stringContains "session B payload should name B" "\"sessionId\":\"bbbb2222\""
  ]

[<Tests>]
let liveTestWatcherStaleEventTests =
  testList "LiveTestWatcherManager stale-event guard" [

    testCase "queued event from a stopped watcher generation is stale and dropped" <| fun _ ->
      // An event queued while dir X was at epoch 0; the watcher was stopped
      // (epoch → 1) before the debounce fired. The pure guard must declare it
      // stale so the debounce callback drops it instead of dispatching a
      // FileContentChanged/FileReloaded for a dead watcher's reload.
      let epochs = System.Collections.Generic.Dictionary<string, int64>()
      // The watcher was stopped after the event queued, advancing the epoch.
      epochs.["/proj"] <- 1L
      let currentEpoch (d: string) =
        match epochs.TryGetValue(d) with
        | true, e -> e
        | false, _ -> 0L
      LiveTestWatcherStaleGuard.isStaleEvent (Some "/proj") (Some 0L) currentEpoch
      |> Expect.isTrue "epoch advanced past queue time → stale"

    testCase "queued event whose dir no longer resolves is stale and dropped" <| fun _ ->
      let currentEpoch (_d: string) = 0L
      LiveTestWatcherStaleGuard.isStaleEvent None (Some 0L) currentEpoch
      |> Expect.isTrue "no resolving dir → stale (fallback dropped too)"

    testCase "queued event from the current watcher generation is fresh" <| fun _ ->
      let epochs = System.Collections.Generic.Dictionary<string, int64>()
      epochs.["/proj"] <- 2L
      let currentEpoch (d: string) =
        match epochs.TryGetValue(d) with
        | true, e -> e
        | false, _ -> 0L
      LiveTestWatcherStaleGuard.isStaleEvent (Some "/proj") (Some 2L) currentEpoch
      |> Expect.isFalse "epoch unchanged since queue → fresh"

    testCase "event queued before any stop and never recreated stays fresh" <| fun _ ->
      let currentEpoch (_d: string) = 0L
      LiveTestWatcherStaleGuard.isStaleEvent (Some "/proj") (Some 0L) currentEpoch
      |> Expect.isFalse "epoch 0 → 0 with a resolving dir → fresh"
  ]

// ── Behavioral: LiveTestWatcherManager session attribution ──────────────
// Real FileSystemWatcher + debounce (75ms) — integration territory, so it
// stays out of the default suite. Verifies the isolation fix end-to-end:
// two sessions sharing ONE working dir each get the FileReloaded event for a
// file save, and removing one session's claim stops its events while the
// other session's continue.

open System
open System.IO
open System.Threading
open SageFs.Server.DaemonMode

[<Tests>]
let liveTestWatcherAttributionTests =
  testList "LiveTestWatcherManager session attribution" [

    testCase "[Integration] file save in a shared dir fires FileReloaded for every owning session" <| fun _ ->
      let dir = Path.Combine(Path.GetTempPath(), "sagefs-watcher-test-" + Guid.NewGuid().ToString("N"))
      Directory.CreateDirectory(dir) |> ignore
      let sA = sid "aaaaaaaa"
      let sB = sid "bbbbbbbb"
      let fired = System.Collections.Concurrent.ConcurrentQueue<string * string>()
      use mgr =
        new LiveTestWatcherManager(
          (fun _ -> ()),
          (fun s p -> fired.Enqueue(WorkerProtocol.SessionId.value s, p)),
          None)
      mgr.AddDirectory(dir, sA)
      mgr.AddDirectory(dir, sB)
      try
        let probe = Path.Combine(dir, "Lib.fs")
        File.WriteAllText(probe, "module Lib")
        // Debounce is 75ms; allow generous time for the watcher + debounce.
        let sw = Diagnostics.Stopwatch.StartNew()
        let mutable seenA = false
        let mutable seenB = false
        while (not seenA || not seenB) && sw.ElapsedMilliseconds < 10000L do
          for (s, p) in fired do
            if s = "aaaaaaaa" && p = probe then seenA <- true
            if s = "bbbbbbbb" && p = probe then seenB <- true
          if not (seenA && seenB) then Thread.Sleep 50
        Expect.isTrue "session A should receive the FileReloaded for the shared-dir save" seenA
        Expect.isTrue "session B should receive the FileReloaded for the shared-dir save" seenB
      finally
        try File.Delete(Path.Combine(dir, "Lib.fs")) with _ -> ()
        try Directory.Delete(dir, true) with _ -> ()

    testCase "[Integration] removing one session's claim stops only its FileReloaded events" <| fun _ ->
      let dir = Path.Combine(Path.GetTempPath(), "sagefs-watcher-test-" + Guid.NewGuid().ToString("N"))
      Directory.CreateDirectory(dir) |> ignore
      let sA = sid "aaaaaaaa"
      let sB = sid "bbbbbbbb"
      let fired = System.Collections.Concurrent.ConcurrentQueue<string * string>()
      use mgr =
        new LiveTestWatcherManager(
          (fun _ -> ()),
          (fun s p -> fired.Enqueue(WorkerProtocol.SessionId.value s, p)),
          None)
      mgr.AddDirectory(dir, sA)
      mgr.AddDirectory(dir, sB)
      try
        // Warm up the watcher so the first event is not swallowed by
        // FileSystemWatcher startup.
        let warm = Path.Combine(dir, "Warm.fs")
        File.WriteAllText(warm, "module Warm")
        Thread.Sleep 500
        File.Delete warm

        // Both sessions claimed; drop B's claim.
        mgr.RemoveDirectory(dir, sB)

        let probe = Path.Combine(dir, "Lib2.fs")
        File.WriteAllText(probe, "module Lib2")
        let sw = Diagnostics.Stopwatch.StartNew()
        let mutable seenA = false
        let mutable seenB = false
        // Wait until A's event arrives, then keep waiting a little for a
        // possible (wrong) B event before declaring B silent.
        while sw.ElapsedMilliseconds < 10000L && (not seenA || sw.ElapsedMilliseconds < 1000L) do
          for (s, p) in fired do
            if s = "aaaaaaaa" && p = probe then seenA <- true
            if s = "bbbbbbbb" && p = probe then seenB <- true
          Thread.Sleep 50
        Expect.isTrue "session A should still receive the FileReloaded" seenA
        Expect.isFalse "session B must NOT receive FileReloaded after its claim was removed" seenB
      finally
        try File.Delete(Path.Combine(dir, "Lib2.fs")) with _ -> ()
        try Directory.Delete(dir, true) with _ -> ()
  ]
