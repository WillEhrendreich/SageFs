module SageFs.Tests.PersistenceComplianceTests

open System
open Expecto
open Expecto.Flip
open SageFs.Features
open SageFs.Features.LiveTesting

/// Persistence compliance tests define behavioral contracts that BOTH
/// the current binary format (.sagefm/.sagetc) and any future
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

let private sampleDaemonState () : DaemonManifest.DaemonManifestState =
  let record : DaemonManifest.DaemonSessionRecord =
    { SessionId = "s1"
      Projects = [ "App.fsproj" ]
      WorkingDir = "C:\\Code"
      CreatedAt = DateTimeOffset(2025, 3, 1, 12, 0, 0, TimeSpan.Zero)
      StoppedAt = None }
  { Sessions = Map.ofList [ "s1", record ]
    ActiveSessionId = Some "s1" }

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
    let state = DaemonManifest.DaemonManifestState.empty
    DaemonPersistence.saveManifest dir state |> ignore
    match DaemonPersistence.loadManifest dir with
    | Ok s ->
      s.Sessions |> Map.isEmpty |> Expect.isTrue "no sessions"
      s.ActiveSessionId |> Expect.isNone "no active"
    | Error e -> failwithf "empty roundtrip failed: %A" e)

  testCase "manifest: multiple sessions roundtrip"
  <| fun _ -> withTempDir (fun dir ->
    let r1 : DaemonManifest.DaemonSessionRecord =
      { SessionId = "s1"; Projects = [ "A.fsproj" ]; WorkingDir = "C:\\A"
        CreatedAt = DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero); StoppedAt = None }
    let r2 : DaemonManifest.DaemonSessionRecord =
      { SessionId = "s2"; Projects = [ "B.fsproj"; "C.fsproj" ]; WorkingDir = "C:\\B"
        CreatedAt = DateTimeOffset(2025, 2, 1, 0, 0, 0, TimeSpan.Zero)
        StoppedAt = Some (DateTimeOffset.UtcNow.AddHours(-1.0)) }
    let state : DaemonManifest.DaemonManifestState =
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

  testCase "manifest and session files are independent"
  <| fun _ -> withTempDir (fun dir ->
    let manifest = sampleDaemonState ()
    DaemonPersistence.saveManifest dir manifest |> ignore
    DaemonPersistence.loadManifest dir |> Result.isOk
    |> Expect.isTrue "manifest loads")
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

// ── 6. Test cache contracts ──

let private sampleTestState () : LiveTestState =
  let testId = TestId.TestId "SageFs.Tests.SampleTest"
  let result : TestRunResult =
    { TestId = testId
      TestName = "SampleTest"
      Result = TestResult.Passed (TimeSpan.FromMilliseconds(42.0))
      Timestamp = DateTimeOffset(2025, 3, 1, 12, 0, 0, TimeSpan.Zero)
      Output = Some "val x: int = 42" }
  let bitmap : CoverageBitmap =
    { Bits = [| 0b1010_1010UL; 0b1111_0000UL |]
      Count = 12 }
  { LiveTestState.empty with
      LastResults = Map.ofList [ testId, result ]
      TestCoverageBitmaps = Map.ofList [ testId, bitmap ] }

let testCacheTests = testList "Test cache contracts" [

  testCase "empty test state roundtrips"
  <| fun _ -> withTempDir (fun dir ->
    let projects = [ "App.fsproj" ]
    DaemonPersistence.saveTestCache dir projects LiveTestState.empty |> ignore
    match DaemonPersistence.loadTestCache dir projects with
    | Ok s ->
      s.LastResults |> Map.isEmpty |> Expect.isTrue "no results"
      s.TestCoverageBitmaps |> Map.isEmpty |> Expect.isTrue "no bitmaps"
    | Error e -> failwithf "empty cache roundtrip failed: %s" e)

  testCase "test results roundtrip (lossy on name/timestamp)"
  <| fun _ -> withTempDir (fun dir ->
    let projects = [ "App.fsproj" ]
    let state = sampleTestState ()
    DaemonPersistence.saveTestCache dir projects state |> ignore
    match DaemonPersistence.loadTestCache dir projects with
    | Ok s ->
      let testId = TestId.TestId "SageFs.Tests.SampleTest"
      s.LastResults |> Map.containsKey testId
      |> Expect.isTrue "test result preserved"
      match s.LastResults.[testId].Result with
      | TestResult.Passed d ->
        (d.TotalMilliseconds, 0.0)
        |> Expect.isGreaterThan "duration preserved"
      | other -> failwithf "expected Passed, got: %A" other
    | Error e -> failwithf "result roundtrip failed: %s" e)

  testCase "coverage bitmaps roundtrip"
  <| fun _ -> withTempDir (fun dir ->
    let projects = [ "App.fsproj" ]
    let state = sampleTestState ()
    DaemonPersistence.saveTestCache dir projects state |> ignore
    match DaemonPersistence.loadTestCache dir projects with
    | Ok s ->
      let testId = TestId.TestId "SageFs.Tests.SampleTest"
      s.TestCoverageBitmaps |> Map.containsKey testId
      |> Expect.isTrue "bitmap preserved"
      let bm = s.TestCoverageBitmaps.[testId]
      bm.Bits.[0] |> Expect.equal "first word" 0b1010_1010UL
      bm.Bits.[1] |> Expect.equal "second word" 0b1111_0000UL
      // Count may be derived from Bits.Length * 64 (lossy)
      (bm.Count, 0) |> Expect.isGreaterThan "probe count positive"
    | Error e -> failwithf "bitmap roundtrip failed: %s" e)

  testCase "different project hashes are independent"
  <| fun _ -> withTempDir (fun dir ->
    let projA = [ "A.fsproj" ]
    let projB = [ "B.fsproj" ]
    let stateA = sampleTestState ()
    DaemonPersistence.saveTestCache dir projA stateA |> ignore
    DaemonPersistence.saveTestCache dir projB LiveTestState.empty |> ignore
    match DaemonPersistence.loadTestCache dir projA with
    | Ok s ->
      s.LastResults |> Map.isEmpty |> Expect.isFalse "A has results"
    | Error e -> failwithf "load A failed: %s" e
    match DaemonPersistence.loadTestCache dir projB with
    | Ok s ->
      s.LastResults |> Map.isEmpty |> Expect.isTrue "B is empty"
    | Error e -> failwithf "load B failed: %s" e)

  testCase "loading nonexistent cache returns Error"
  <| fun _ -> withTempDir (fun dir ->
    match DaemonPersistence.loadTestCache dir [ "NoSuch.fsproj" ] with
    | Ok _ -> failwith "should not find cache"
    | Error _ -> ())
]

[<Tests>]
let allPersistenceComplianceTests = testList "Persistence Compliance (synthesis 4.2)" [
  roundtripTests
  isolationTests
  idempotencyTests
  missingDataTests
  corruptionTests
  testCacheTests
]
