module SageFs.Tests.Round14HardeningTests

open System
open System.Threading.Tasks
open Expecto
open Expecto.Flip
open SageFs
open SageFs.Features
open SageFs.Features.ManifestTypes
open SageFs.Features.DaemonManifest
open Microsoft.Extensions.Logging.Abstractions

let private nullLog = NullLogger.Instance
// W42: checkDaemonRunning now returns Task<DaemonInfo option> (not bare DaemonInfo option)
let private noDaemonTask () : Task<DaemonInfo option> = Task.FromResult(None)
let private pruneFlags = { Args.DaemonFlags.defaults with Prune = true }
let private noPruneFlags = Args.DaemonFlags.defaults

// ---------------------------------------------------------------------------
// W35 — CorruptData at startup renames manifest to unblock periodic saves
// ---------------------------------------------------------------------------
// W35(R14): When startup encounters CorruptData, it currently leaves daemon.sagefm intact.
// Every subsequent mergeManifestWithExisting call reads the still-corrupt file → Error →
// skips write → ALL new sessions are lost for the daemon's entire lifetime.
// Fix: rename to daemon.sagefm.corrupt.<ms> on CorruptData (not IoError — that's transient).

[<Tests>]
let w35CorruptRenameTests =
  testList "W35(R14) — CorruptData startup renames manifest to unblock periodic saves" [

    testCase "renameCorruptManifest moves daemon.sagefm to daemon.sagefm.corrupt.<ms>" <| fun _ ->
      let dir = IO.Path.Combine(IO.Path.GetTempPath(), sprintf "sagefs-r14-%s" (Guid.NewGuid().ToString("N")))
      IO.Directory.CreateDirectory(dir) |> ignore
      try
        let manifestPath = IO.Path.Combine(dir, "daemon.sagefm")
        IO.File.WriteAllBytes(manifestPath, [| 0xFFuy; 0xFEuy; 0xFDuy |])
        IO.File.Exists(manifestPath) |> Expect.isTrue "corrupt manifest exists before rename"
        let renamed = DaemonPersistence.renameCorruptManifest dir
        renamed |> Expect.isTrue "renameCorruptManifest should return true on success"
        IO.File.Exists(manifestPath) |> Expect.isFalse "original manifest should be gone after rename"
        let corruptFiles = IO.Directory.GetFiles(dir, "daemon.sagefm.corrupt.*")
        corruptFiles.Length |> Expect.equal "one .corrupt. backup file should exist" 1
      finally
        IO.Directory.Delete(dir, true)

    testCase "After corrupt rename, loadManifest returns NotFound (periodic saves unblocked)" <| fun _ ->
      let dir = IO.Path.Combine(IO.Path.GetTempPath(), sprintf "sagefs-r14-%s" (Guid.NewGuid().ToString("N")))
      IO.Directory.CreateDirectory(dir) |> ignore
      try
        IO.File.WriteAllBytes(IO.Path.Combine(dir, "daemon.sagefm"), [| 0xFFuy; 0xFEuy; 0xFDuy |])
        DaemonPersistence.renameCorruptManifest dir |> ignore
        match DaemonPersistence.loadManifest dir with
        | Result.Error ManifestLoadError.NotFound -> ()
        | other -> failtest (sprintf "Expected NotFound after rename, got %A" other)
        match DaemonPersistence.saveManifest dir DaemonManifestState.empty with
        | Ok _ -> ()
        | Error err -> failtest (sprintf "saveManifest should succeed after rename, got Error: %s" err)
      finally
        IO.Directory.Delete(dir, true)

    testCase "renameCorruptManifest returns false when no manifest exists" <| fun _ ->
      let dir = IO.Path.Combine(IO.Path.GetTempPath(), sprintf "sagefs-r14-%s" (Guid.NewGuid().ToString("N")))
      // No directory → no file → returns false
      let renamed = DaemonPersistence.renameCorruptManifest dir
      renamed |> Expect.isFalse "renameCorruptManifest should return false when file does not exist"
  ]

// ---------------------------------------------------------------------------
// W36 — handlePrune returns Result<bool, string> (not bare bool)
// ---------------------------------------------------------------------------
// W36(R14): handlePrune returns false for BOTH "not requested" AND error cases.
// Caller cannot distinguish — daemon starts even when --prune was requested but failed.
// Fix: Result<bool, string> — Ok true=pruned/exit, Ok false=not-requested/continue,
//                             Error msg=requested-but-failed/exit-with-error-message.

[<Tests>]
let w36HandlePruneResultTests =
  testList "W36(R14) — handlePrune returns Result<bool, string>" [

    testTask "handlePrune with daemon running returns Error (not Ok false)" {
      let dir = IO.Path.Combine(IO.Path.GetTempPath(), sprintf "sagefs-r14-%s" (Guid.NewGuid().ToString("N")))
      let fakeDaemon () : Task<DaemonInfo option> =
        Task.FromResult(Some { Pid = 12345; Port = 37749; DashboardPort = 37750; StartedAt = DateTime.UtcNow; WorkingDirectory = "/"; Version = "0.1.0"; ApiVersion = None; SessionCount = None; ComponentFailures = [] })
      let! result = SageFs.Server.DaemonMode.handlePrune dir nullLog fakeDaemon pruneFlags
      match result with
      | Result.Error msg ->
        msg |> Expect.stringContains "error message should mention PID 12345" "12345"
      | Result.Ok _ -> failtest "handlePrune should return Error when daemon is running, not Ok"
    }

    testTask "handlePrune with CorruptData returns Error (not Ok false)" {
      let dir = IO.Path.Combine(IO.Path.GetTempPath(), sprintf "sagefs-r14-%s" (Guid.NewGuid().ToString("N")))
      IO.Directory.CreateDirectory(dir) |> ignore
      try
        IO.File.WriteAllBytes(IO.Path.Combine(dir, "daemon.sagefm"), [| 0xFFuy; 0xFEuy; 0xFDuy |])
        let! result = SageFs.Server.DaemonMode.handlePrune dir nullLog noDaemonTask pruneFlags
        match result with
        | Result.Error _ -> ()  // correct: CorruptData returns Error
        | Result.Ok _ -> failtest "handlePrune should return Error for corrupt manifest"
      finally
        IO.Directory.Delete(dir, true)
    }

    testTask "handlePrune NotFound returns Ok true (nothing to prune — not an error)" {
      let dir = IO.Path.Combine(IO.Path.GetTempPath(), sprintf "sagefs-r14-%s" (Guid.NewGuid().ToString("N")))
      // No dir created → loadManifest returns NotFound → nothing to prune → Ok true
      let! result = SageFs.Server.DaemonMode.handlePrune dir nullLog noDaemonTask pruneFlags
      result |> Expect.equal "NotFound → Ok true (nothing to prune, but prune was requested)" (Result.Ok true)
    }

    testTask "handlePrune with Prune=false returns Ok false (not requested — not an error)" {
      let dir = IO.Path.Combine(IO.Path.GetTempPath(), sprintf "sagefs-r14-%s" (Guid.NewGuid().ToString("N")))
      let! result = SageFs.Server.DaemonMode.handlePrune dir nullLog noDaemonTask noPruneFlags
      result |> Expect.equal "Prune=false → Ok false (not requested)" (Result.Ok false)
    }
  ]

// ---------------------------------------------------------------------------
// W38+W39 — mergeManifestWithExisting: stamp phantoms; add dir param
// ---------------------------------------------------------------------------
// W38(R14): Sessions absent from snapshot with StoppedAt=None are phantoms (crashed/disappeared).
// They accumulate forever — next restart tries to resume them and fails.
// Fix: in false,_ arm, if StoppedAt=None and absent from snapshot → stamp with current time.
//
// W39(R14): mergeManifestWithExisting hardcodes DaemonState.SageFsDir (~/.SageFs).
// Behavioral tests impossible without hitting developer's real home dir.
// Fix: add (dir: string) as first param; update 2 callsites with DaemonState.SageFsDir.

[<Tests>]
let w38w39MergeTests =
  testList "W38+W39(R14) — mergeManifestWithExisting: phantom session stamp + dir param" [

    testCase "W39: mergeManifestWithExisting accepts dir param — behavioral round-trip possible" <| fun _ ->
      // Tests that the function is now parametrized by dir (not hardcoded SageFsDir).
      // If this compiles and runs, W39 is implemented.
      let dir = IO.Path.Combine(IO.Path.GetTempPath(), sprintf "sagefs-r14-%s" (Guid.NewGuid().ToString("N")))
      IO.Directory.CreateDirectory(dir) |> ignore
      try
        let emptySnapshot = SessionManager.QuerySnapshot.empty
        // W39: dir param is first — this call would fail to compile if dir param missing
        match SageFs.Server.DaemonMode.mergeManifestWithExisting dir nullLog emptySnapshot None None with
        | Result.Ok state ->
          state.Sessions.Count |> Expect.equal "empty merge produces empty session map" 0
        | Result.Error err -> failtest (sprintf "Expected Ok for empty dir, got %A" err)
      finally
        IO.Directory.Delete(dir, true)

    testCase "W38: phantom session (absent from snapshot, StoppedAt=None) gets stamped" <| fun _ ->
      let dir = IO.Path.Combine(IO.Path.GetTempPath(), sprintf "sagefs-r14-%s" (Guid.NewGuid().ToString("N")))
      IO.Directory.CreateDirectory(dir) |> ignore
      try
        // Save manifest with a phantom session alive (StoppedAt = None)
        let phantomSession = {
          DaemonSessionRecord.SessionId = "phantom-001"
          Projects = [ "Phantom.fsproj" ]
          WorkingDir = "/tmp/phantom"
          CreatedAt = DateTimeOffset.UtcNow.AddHours(-2.0)
          StoppedAt = None  // alive in manifest but will be absent from snapshot
        }
        let manifestState = {
          DaemonManifestState.Sessions = Map.ofList [("phantom-001", phantomSession)]
          ActiveSessionId = None
        }
        DaemonPersistence.saveManifest dir manifestState |> ignore
        // Empty snapshot — phantom session is absent (crashed, not running)
        let emptySnapshot = SessionManager.QuerySnapshot.empty
        // Periodic save path: stampActive = None (use current time for phantoms)
        let beforeMerge = DateTimeOffset.UtcNow
        match SageFs.Server.DaemonMode.mergeManifestWithExisting dir nullLog emptySnapshot None None with
        | Result.Ok merged ->
          match merged.Sessions |> Map.tryFind "phantom-001" with
          | Some session ->
            session.StoppedAt |> Expect.isSome "phantom session should be stamped with StoppedAt"
            match session.StoppedAt with
            | Some ts ->
              (ts, beforeMerge) |> Expect.isGreaterThanOrEqual "stamp should be at or after merge start"
            | None -> ()
          | None -> failtest "phantom session should still be present in merged state"
        | Result.Error err -> failtest (sprintf "merge should succeed, got %A" err)
      finally
        IO.Directory.Delete(dir, true)

    testCase "W38: already-stopped session preserves original StoppedAt (not re-stamped)" <| fun _ ->
      let dir = IO.Path.Combine(IO.Path.GetTempPath(), sprintf "sagefs-r14-%s" (Guid.NewGuid().ToString("N")))
      IO.Directory.CreateDirectory(dir) |> ignore
      try
        // Truncate to ms precision since binary manifest stores ToUnixTimeMilliseconds.
        let rawStop = DateTimeOffset.UtcNow.AddHours(-1.0)
        let originalStop = DateTimeOffset.FromUnixTimeMilliseconds(rawStop.ToUnixTimeMilliseconds())
        let stoppedSession = {
          DaemonSessionRecord.SessionId = "stopped-001"
          Projects = []
          WorkingDir = "/tmp/stopped"
          CreatedAt = DateTimeOffset.UtcNow.AddHours(-3.0)
          StoppedAt = Some originalStop  // already explicitly stopped
        }
        let manifestState = {
          DaemonManifestState.Sessions = Map.ofList [("stopped-001", stoppedSession)]
          ActiveSessionId = None
        }
        DaemonPersistence.saveManifest dir manifestState |> ignore
        let emptySnapshot = SessionManager.QuerySnapshot.empty
        match SageFs.Server.DaemonMode.mergeManifestWithExisting dir nullLog emptySnapshot None None with
        | Result.Ok merged ->
          match merged.Sessions |> Map.tryFind "stopped-001" with
          | Some session ->
            session.StoppedAt |> Expect.equal "already-stopped StoppedAt should be preserved" (Some originalStop)
          | None -> failtest "stopped session should still be present"
        | Result.Error err -> failtest (sprintf "merge failed: %A" err)
      finally
        IO.Directory.Delete(dir, true)

    testCase "W38: phantom stamp during shutdown uses shutdown timestamp (stampActive = Some ts)" <| fun _ ->
      let dir = IO.Path.Combine(IO.Path.GetTempPath(), sprintf "sagefs-r14-%s" (Guid.NewGuid().ToString("N")))
      IO.Directory.CreateDirectory(dir) |> ignore
      try
        let phantomSession = {
          DaemonSessionRecord.SessionId = "phantom-002"
          Projects = []
          WorkingDir = "/tmp/phantom2"
          CreatedAt = DateTimeOffset.UtcNow.AddHours(-1.0)
          StoppedAt = None
        }
        let manifestState = {
          DaemonManifestState.Sessions = Map.ofList [("phantom-002", phantomSession)]
          ActiveSessionId = None
        }
        DaemonPersistence.saveManifest dir manifestState |> ignore
        let emptySnapshot = SessionManager.QuerySnapshot.empty
        let shutdownTime = DateTimeOffset.UtcNow
        // Shutdown path: stampActive = Some shutdownTime
        match SageFs.Server.DaemonMode.mergeManifestWithExisting dir nullLog emptySnapshot None (Some shutdownTime) with
        | Result.Ok merged ->
          match merged.Sessions |> Map.tryFind "phantom-002" with
          | Some session ->
            session.StoppedAt |> Expect.equal "phantom during shutdown gets shutdown timestamp" (Some shutdownTime)
          | None -> failtest "phantom session should be present"
        | Result.Error err -> failtest (sprintf "merge failed: %A" err)
      finally
        IO.Directory.Delete(dir, true)
  ]

// ---------------------------------------------------------------------------
// W41 — NotFound arm should log at Error (invariant violation)
// ---------------------------------------------------------------------------
// W41(R14): When mergeManifestWithExisting converts NotFound → Ok (which it always does),
// the caller's NotFound arm "should not be reached." But if it IS reached, it's an
// invariant violation — a programming error. Logging at Warning silently skips saves.
// Fix: upgrade NotFound arms in periodicManifestSave and performGracefulShutdown to LogError.

[<Tests>]
let w41NotFoundLogLevelTests =
  testList "W41(R14) — NotFound propagation is invariant violation, should LogError" [

    testCase "W41: mergeManifestWithExisting does NOT return NotFound (converts to Ok)" <| fun _ ->
      // Verify the invariant directly: with NotFound on disk, mergeManifestWithExisting returns Ok.
      let dir = IO.Path.Combine(IO.Path.GetTempPath(), sprintf "sagefs-r14-%s" (Guid.NewGuid().ToString("N")))
      // No directory → loadManifest returns NotFound → should be converted to Ok
      let emptySnapshot = SessionManager.QuerySnapshot.empty
      match SageFs.Server.DaemonMode.mergeManifestWithExisting dir nullLog emptySnapshot None None with
      | Result.Ok _ -> ()  // correct: NotFound internally handled → Ok returned to caller
      | Result.Error ManifestLoadError.NotFound ->
        failtest "mergeManifestWithExisting must not propagate NotFound to callers"
      | Result.Error err ->
        failtest (sprintf "Unexpected error from merge: %A" err)
  ]

// ---------------------------------------------------------------------------
// W42 — checkDaemonRunning: unit -> Task<DaemonInfo option> (not Async.RunSynchronously)
// ---------------------------------------------------------------------------
// W42(R14): handlePrune's checkDaemonRunning: unit -> DaemonInfo option calls
// DaemonState.read() which internally uses Async.RunSynchronously inside task{}.
// This can cause thread pool starvation (theoretical for single-user CLI, real for servers).
// Fix: change to unit -> Task<DaemonInfo option>; use let! daemonInfo = checkDaemonRunning().

[<Tests>]
let w42TaskCheckDaemonRunningTests =
  testList "W42(R14) — handlePrune checkDaemonRunning: unit -> Task<DaemonInfo option>" [

    testTask "handlePrune accepts Task-returning checkDaemonRunning (W42 type contract)" {
      // This test verifies the new signature compiles: unit -> Task<DaemonInfo option>.
      // If handlePrune still takes unit -> DaemonInfo option, noDaemonTask won't type-check.
      let dir = IO.Path.Combine(IO.Path.GetTempPath(), sprintf "sagefs-r14-%s" (Guid.NewGuid().ToString("N")))
      let! result = SageFs.Server.DaemonMode.handlePrune dir nullLog noDaemonTask noPruneFlags
      result |> Expect.equal "Task-returning noDaemon with Prune=false → Ok false" (Result.Ok false)
    }

    testTask "handlePrune with Task daemon-detected returns Error (W42 + W36 combined)" {
      let dir = IO.Path.Combine(IO.Path.GetTempPath(), sprintf "sagefs-r14-%s" (Guid.NewGuid().ToString("N")))
      let taskDaemon () : Task<DaemonInfo option> =
        Task.FromResult(Some { Pid = 99; Port = 37749; DashboardPort = 37750; StartedAt = DateTime.UtcNow; WorkingDirectory = "/"; Version = "test"; ApiVersion = None; SessionCount = None; ComponentFailures = [] })
      let! result = SageFs.Server.DaemonMode.handlePrune dir nullLog taskDaemon pruneFlags
      match result with
      | Result.Error _ -> ()
      | Result.Ok _ -> failtest "Should return Error when daemon detected via Task injection"
    }
  ]

[<Tests>]
let allRound14Tests =
  testList "Round 14 Hardening (R14) — W35 W36 W38 W39 W41 W42" [
    w35CorruptRenameTests
    w36HandlePruneResultTests
    w38w39MergeTests
    w41NotFoundLogLevelTests
    w42TaskCheckDaemonRunningTests
  ]
