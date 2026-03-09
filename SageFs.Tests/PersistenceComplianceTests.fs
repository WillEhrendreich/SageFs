module SageFs.Tests.PersistenceComplianceTests

open System
open Expecto
open Expecto.Flip
open SageFs.Features

/// Persistence compliance tests define behavioral contracts that BOTH
/// the current binary format (.sagefm/.sagefs/.sagetc) and any future
/// storage implementation must satisfy (synthesis 4.2).
///
/// Contract categories:
/// 1. Roundtrip: save → load produces identical domain data
/// 2. Isolation: operations on one entity don't corrupt another
/// 3. Idempotency: saving the same data twice doesn't create duplicates
/// 4. Missing data: loading nonexistent data returns a typed error, not an exception
/// 5. Corruption resilience: corrupt data returns an error, not a crash

// ── Test helpers ──

let private withTempDir (f: string -> unit) =
  let dir =
    System.IO.Path.Combine(
      System.IO.Path.GetTempPath(),
      "sagefs-compliance-" + Guid.NewGuid().ToString("N").[..7])
  System.IO.Directory.CreateDirectory(dir) |> ignore
  try
    f dir
  finally
    try System.IO.Directory.Delete(dir, true) with _ -> ()

let private sampleDaemonState () : Replay.DaemonReplayState =
  let record : Replay.DaemonSessionRecord =
    { SessionId = "s1"
      Projects = [ "App.fsproj" ]
      WorkingDir = "C:\\Code"
      CreatedAt = DateTimeOffset(2025, 3, 1, 12, 0, 0, TimeSpan.Zero)
      StoppedAt = None }
  { Sessions = Map.ofList [ "s1", record ]
    ActiveSessionId = Some "s1" }

let private sampleSessionState () : Replay.SessionReplayState =
  { Status = Replay.ReplayStatus.Ready
    EvalCount = 1
    FailedEvalCount = 0
    ResetCount = 0
    HardResetCount = 0
    LastEvalResult = Some "val x: int = 42"
    WarmupErrors = []
    EvalHistory =
      [ { Code = "let x = 42;;"
          Result = "val x: int = 42"
          TypeSignature = Some "int"
          Duration = TimeSpan.FromMilliseconds(120.0)
          Timestamp = DateTimeOffset(2025, 3, 1, 12, 0, 0, TimeSpan.Zero) } ]
    LastDiagnostics = []
    StartedAt = Some (DateTimeOffset(2025, 3, 1, 12, 0, 0, TimeSpan.Zero))
    LastActivity = Some (DateTimeOffset(2025, 3, 1, 12, 0, 1, TimeSpan.Zero)) }

// ── 1. Roundtrip contracts ──

let roundtripTests = testList "Roundtrip contracts" [

  testCase "manifest: save → load preserves sessions"
  <| fun _ -> withTempDir (fun dir ->
    let state = sampleDaemonState ()
    DaemonPersistence.saveManifest dir state |> ignore
    match DaemonPersistence.loadManifest dir with
    | Ok s ->
      s.Sessions.Count |> Expect.equal "session count" 1
      s.Sessions.["s1"].SessionId |> Expect.equal "session id" "s1"
      s.Sessions.["s1"].Projects |> Expect.equal "projects" [ "App.fsproj" ]
      s.ActiveSessionId |> Expect.equal "active" (Some "s1")
    | Error e -> failwithf "roundtrip failed: %A" e)

  testCase "manifest: empty state roundtrips"
  <| fun _ -> withTempDir (fun dir ->
    let state = Replay.DaemonReplayState.empty
    DaemonPersistence.saveManifest dir state |> ignore
    match DaemonPersistence.loadManifest dir with
    | Ok s ->
      s.Sessions |> Map.isEmpty |> Expect.isTrue "no sessions"
      s.ActiveSessionId |> Expect.isNone "no active"
    | Error e -> failwithf "empty roundtrip failed: %A" e)

  testCase "session: save → load preserves eval history"
  <| fun _ -> withTempDir (fun dir ->
    let state = sampleSessionState ()
    DaemonPersistence.saveSession dir "s1" "App.fsproj" "C:\\Code" [ "FSharp.Core" ] state |> ignore
    match DaemonPersistence.loadSession dir "s1" with
    | Ok s ->
      s.EvalHistory.Length |> Expect.equal "eval count" 1
      s.EvalHistory.[0].Code |> Expect.equal "code" "let x = 42;;"
      s.EvalHistory.[0].Result |> Expect.equal "result" "val x: int = 42"
      s.EvalCount |> Expect.equal "eval counter" 1
    | Error e -> failwithf "session roundtrip failed: %s" e)

  testCase "manifest: multiple sessions roundtrip"
  <| fun _ -> withTempDir (fun dir ->
    let r1 : Replay.DaemonSessionRecord =
      { SessionId = "s1"; Projects = [ "A.fsproj" ]; WorkingDir = "C:\\A"
        CreatedAt = DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero); StoppedAt = None }
    let r2 : Replay.DaemonSessionRecord =
      { SessionId = "s2"; Projects = [ "B.fsproj"; "C.fsproj" ]; WorkingDir = "C:\\B"
        CreatedAt = DateTimeOffset(2025, 2, 1, 0, 0, 0, TimeSpan.Zero)
        StoppedAt = Some (DateTimeOffset.UtcNow.AddHours(-1.0)) }
    let state : Replay.DaemonReplayState =
      { Sessions = Map.ofList [ "s1", r1; "s2", r2 ]
        ActiveSessionId = Some "s1" }
    DaemonPersistence.saveManifest dir state |> ignore
    match DaemonPersistence.loadManifest dir with
    | Ok s ->
      s.Sessions.Count |> Expect.equal "count" 2
      s.Sessions.["s2"].StoppedAt |> Expect.isSome "s2 stopped"
    | Error e -> failwithf "multi roundtrip failed: %A" e)
]

// ── 2. Isolation contracts ──

let isolationTests = testList "Isolation contracts" [

  testCase "saving session A doesn't affect session B"
  <| fun _ -> withTempDir (fun dir ->
    let stateA = sampleSessionState ()
    let stateB =
      { sampleSessionState () with
          EvalHistory =
            [ { Code = "let y = 99;;"
                Result = "val y: int = 99"
                TypeSignature = Some "int"
                Duration = TimeSpan.FromMilliseconds(50.0)
                Timestamp = DateTimeOffset(2025, 3, 2, 0, 0, 0, TimeSpan.Zero) } ] }
    DaemonPersistence.saveSession dir "s1" "A.fsproj" "C:\\A" [] stateA |> ignore
    DaemonPersistence.saveSession dir "s2" "B.fsproj" "C:\\B" [] stateB |> ignore
    match DaemonPersistence.loadSession dir "s1" with
    | Ok s ->
      s.EvalHistory.[0].Code |> Expect.equal "A unchanged" "let x = 42;;"
    | Error e -> failwithf "load A failed: %s" e
    match DaemonPersistence.loadSession dir "s2" with
    | Ok s ->
      s.EvalHistory.[0].Code |> Expect.equal "B has its own code" "let y = 99;;"
    | Error e -> failwithf "load B failed: %s" e)

  testCase "manifest and session files are independent"
  <| fun _ -> withTempDir (fun dir ->
    let manifest = sampleDaemonState ()
    let session = sampleSessionState ()
    DaemonPersistence.saveManifest dir manifest |> ignore
    DaemonPersistence.saveSession dir "s1" "A.fsproj" "C:\\A" [] session |> ignore
    DaemonPersistence.loadManifest dir |> Result.isOk
    |> Expect.isTrue "manifest loads"
    DaemonPersistence.loadSession dir "s1" |> Result.isOk
    |> Expect.isTrue "session loads")
]

// ── 3. Idempotency contracts ──

let idempotencyTests = testList "Idempotency contracts" [

  testCase "saving manifest twice produces identical load"
  <| fun _ -> withTempDir (fun dir ->
    let state = sampleDaemonState ()
    DaemonPersistence.saveManifest dir state |> ignore
    DaemonPersistence.saveManifest dir state |> ignore
    match DaemonPersistence.loadManifest dir with
    | Ok s ->
      s.Sessions.Count |> Expect.equal "still one session" 1
    | Error e -> failwithf "idempotent save failed: %A" e)

  testCase "overwriting session preserves latest data"
  <| fun _ -> withTempDir (fun dir ->
    let v1 = sampleSessionState ()
    DaemonPersistence.saveSession dir "s1" "A.fsproj" "C:\\A" [] v1 |> ignore
    let v2 =
      { v1 with
          EvalCount = 2
          EvalHistory =
            v1.EvalHistory @
            [ { Code = "let z = 100;;"
                Result = "val z: int = 100"
                TypeSignature = Some "int"
                Duration = TimeSpan.FromMilliseconds(80.0)
                Timestamp = DateTimeOffset(2025, 3, 1, 12, 1, 0, TimeSpan.Zero) } ] }
    DaemonPersistence.saveSession dir "s1" "A.fsproj" "C:\\A" [] v2 |> ignore
    match DaemonPersistence.loadSession dir "s1" with
    | Ok s ->
      s.EvalHistory.Length |> Expect.equal "has both evals" 2
    | Error e -> failwithf "overwrite failed: %s" e)
]

// ── 4. Missing data contracts ──

let missingDataTests = testList "Missing data contracts" [

  testCase "loading manifest from empty dir returns NotFound"
  <| fun _ -> withTempDir (fun dir ->
    match DaemonPersistence.loadManifest dir with
    | Ok _ -> failwith "should not find manifest in empty dir"
    | Error e ->
      match e with
      | ManifestTypes.ManifestLoadError.NotFound -> ()
      | other -> failwithf "expected NotFound, got: %A" other)

  testCase "loading nonexistent session returns Error"
  <| fun _ -> withTempDir (fun dir ->
    match DaemonPersistence.loadSession dir "nonexistent" with
    | Ok _ -> failwith "should not find session"
    | Error _ -> ())
]

// ── 5. Corruption resilience contracts ──

let corruptionTests = testList "Corruption resilience contracts" [

  testCase "corrupt manifest file returns CorruptData error"
  <| fun _ -> withTempDir (fun dir ->
    let path = System.IO.Path.Combine(dir, "daemon.sagefm")
    System.IO.File.WriteAllBytes(path, [| 0xFFuy; 0xFEuy; 0xFDuy; 0xFCuy |])
    match DaemonPersistence.loadManifest dir with
    | Ok _ -> failwith "should not load corrupt manifest"
    | Error e ->
      match e with
      | ManifestTypes.ManifestLoadError.CorruptData _ -> ()
      | ManifestTypes.ManifestLoadError.IoError _ -> ()
      | ManifestTypes.ManifestLoadError.NotFound ->
        failwith "corrupt file should not return NotFound")

  testCase "renameCorruptManifest moves corrupt file"
  <| fun _ -> withTempDir (fun dir ->
    let path = System.IO.Path.Combine(dir, "daemon.sagefm")
    System.IO.File.WriteAllBytes(path, [| 0xFFuy; 0xFEuy |])
    let renamed = DaemonPersistence.renameCorruptManifest dir
    renamed |> Expect.isTrue "should rename corrupt file"
    System.IO.File.Exists(path) |> Expect.isFalse "original removed"
    match DaemonPersistence.loadManifest dir with
    | Error ManifestTypes.ManifestLoadError.NotFound -> ()
    | other -> failwithf "expected NotFound after rename, got: %A" other)
]

[<Tests>]
let allPersistenceComplianceTests = testList "Persistence Compliance (synthesis 4.2)" [
  roundtripTests
  isolationTests
  idempotencyTests
  missingDataTests
  corruptionTests
]
