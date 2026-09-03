module SageFs.Tests.BinaryFormatTests

open System
open Expecto
open Expecto.Flip
open FsCheck
open FsCheck.FSharp
open SageFs
open SageFs.Features
open SageFs.Features.TestCacheTypes

// ─── Helpers ─────────────────────────────────────────────────────

// ─── FsCheck Generators ──────────────────────────────────────────

let genSafeString =
  Gen.elements [""; "hello"; "let x = 42"; "val it : int = 42"; "System.IO"; "/path/to/file.fsx"]

// STC generators
let genOutcome =
  Gen.elements [Outcome.Pass; Outcome.Fail; Outcome.Skip; Outcome.Error; Outcome.NotRun]

let genStcTestId =
  Gen.elements ["abc123def456"; ""; "deadbeef0000"; "test_id_here"; "a1b2c3d4e5f6"]

let genCoverageEntry =
  gen {
    let! tid = genStcTestId
    let! wc = Gen.choose(0, 10) |> Gen.map uint32
    let! words = Gen.arrayOfLength (int wc) (Gen.choose(0, Int32.MaxValue) |> Gen.map uint64)
    return { TestId = tid; BitmapWordCount = wc; BitmapWords = words }
  }

let genResultEntry =
  gen {
    let! tid = genStcTestId
    let! outcome = genOutcome
    let! dur = Gen.choose(0, 100000) |> Gen.map uint32
    let! hasMsg = Gen.elements [true; false]
    let! msg =
      match hasMsg with
      | true -> genSafeString |> Gen.map Some
      | false -> Gen.constant None
    return ({ TestId = tid; Outcome = outcome; DurationMs = dur; Message = msg } : ResultEntry)
  }

let genStcData =
  gen {
    let! covCount = Gen.choose(0, 20)
    let! covs = Gen.listOfLength covCount genCoverageEntry
    let! resCount = Gen.choose(0, 20)
    let! results = Gen.listOfLength resCount genResultEntry
    let! gen' = Gen.choose(0, 100) |> Gen.map uint32
    let! ts = Gen.choose(0, 1_000_000_000) |> Gen.map int64
    return { CoverageEntries = covs; ResultEntries = results; ImapGeneration = gen'; CreatedAtMs = ts }
  }


// ─── Comparison helpers ──────────────────────────────────────────

let compareCov (a: CoverageEntry) (b: CoverageEntry) =
  a.TestId = b.TestId && a.BitmapWordCount = b.BitmapWordCount && a.BitmapWords = b.BitmapWords

let compareRes (a: ResultEntry) (b: ResultEntry) =
  a.TestId = b.TestId && a.Outcome = b.Outcome && a.DurationMs = b.DurationMs && a.Message = b.Message

let compareStc (a: StcData) (b: StcData) =
  a.ImapGeneration = b.ImapGeneration && a.CreatedAtMs = b.CreatedAtMs &&
  List.length a.CoverageEntries = List.length b.CoverageEntries &&
  List.forall2 compareCov a.CoverageEntries b.CoverageEntries &&
  List.length a.ResultEntries = List.length b.ResultEntries &&
  List.forall2 compareRes a.ResultEntries b.ResultEntries


// ─── Primitive Tests ─────────────────────────────────────────────

let primitiveTests = testList "BinaryPrimitives" [
  testPropertyWithConfig { FsCheckConfig.defaultConfig with maxTest = 200 }
    "lp-string roundtrips" <| fun (s: string) ->
      let s = match s with null -> "" | x -> x
      use ms = new IO.MemoryStream()
      use bw = new IO.BinaryWriter(ms)
      BinaryPrimitives.writeLpString bw s
      ms.Position <- 0L
      use br = new IO.BinaryReader(ms)
      BinaryPrimitives.readLpString br = s

  testCase "lp-string empty roundtrips" <| fun _ ->
    use ms = new IO.MemoryStream()
    use bw = new IO.BinaryWriter(ms)
    BinaryPrimitives.writeLpString bw ""
    ms.Position <- 0L
    use br = new IO.BinaryReader(ms)
    BinaryPrimitives.readLpString br |> Expect.equal "empty string" ""

  testPropertyWithConfig { FsCheckConfig.defaultConfig with maxTest = 200 }
    "lp-string-option roundtrips Some" <| fun (s: string) ->
      let s = match s with null -> "" | x -> x
      use ms = new IO.MemoryStream()
      use bw = new IO.BinaryWriter(ms)
      BinaryPrimitives.writeLpStringOption bw (Some s)
      ms.Position <- 0L
      use br = new IO.BinaryReader(ms)
      BinaryPrimitives.readLpStringOption br = Some s

  testCase "lp-string-option None roundtrips" <| fun _ ->
    use ms = new IO.MemoryStream()
    use bw = new IO.BinaryWriter(ms)
    BinaryPrimitives.writeLpStringOption bw None
    ms.Position <- 0L
    use br = new IO.BinaryReader(ms)
    BinaryPrimitives.readLpStringOption br |> Expect.isNone "should be None"

  testCase "lp-string-option Some empty roundtrips" <| fun _ ->
    use ms = new IO.MemoryStream()
    use bw = new IO.BinaryWriter(ms)
    BinaryPrimitives.writeLpStringOption bw (Some "")
    ms.Position <- 0L
    use br = new IO.BinaryReader(ms)
    BinaryPrimitives.readLpStringOption br |> Expect.equal "Some empty" (Some "")

  testPropertyWithConfig { FsCheckConfig.defaultConfig with maxTest = 200 }
    "CRC-32 deterministic" <| fun (data: byte[]) ->
      let data = match data with null -> [||] | x -> x
      Crc32.computeAll data = Crc32.computeAll data

  testPropertyWithConfig { FsCheckConfig.defaultConfig with maxTest = 200 }
    "CRC-32 different for different data" <| fun (a: byte[]) (b: byte[]) ->
      let a = match a with null -> [||] | x -> x
      let b = match b with null -> [||] | x -> x
      a = b || Crc32.computeAll a <> Crc32.computeAll b

  testCase "CRC-32 known value" <| fun _ ->
    let data = Text.Encoding.ASCII.GetBytes("123456789")
    Crc32.computeAll data |> Expect.equal "CRC of '123456789'" 0xCBF43926u
]

// ─── STC Tests ───────────────────────────────────────────────────

let stcTests = testList "STC v1" [
  testCase "roundtrip empty data" <| fun _ ->
    let data = StcData.empty
    let bytes = TestCacheWriter.write data
    match TestCacheReader.read bytes with
    | Result.Ok rt -> compareStc data rt |> Expect.isTrue "empty roundtrip"
    | Result.Error e -> failwith e

  testCase "magic bytes correct" <| fun _ ->
    let bytes = TestCacheWriter.write StcData.empty
    bytes.[0] |> Expect.equal "S" 0x53uy
    bytes.[1] |> Expect.equal "T" 0x54uy
    bytes.[2] |> Expect.equal "C" 0x43uy
    bytes.[3] |> Expect.equal "1" 0x31uy

  testCase "rejects invalid magic" <| fun _ ->
    let bytes = TestCacheWriter.write StcData.empty
    bytes.[0] <- 0xFFuy
    match TestCacheReader.read bytes with
    | Result.Error msg -> msg |> Expect.stringContains "should mention magic" "magic"
    | Result.Ok _ -> failwith "should have rejected"

  testCase "rejects corrupted header CRC" <| fun _ ->
    let bytes = TestCacheWriter.write StcData.empty
    bytes.[40] <- bytes.[40] ^^^ 0xFFuy
    match TestCacheReader.read bytes with
    | Result.Error msg -> msg |> Expect.stringContains "should mention CRC" "CRC"
    | Result.Ok _ -> failwith "should have rejected"

  testCase "rejects too-short data" <| fun _ ->
    match TestCacheReader.read [| 0uy; 1uy; 2uy |] with
    | Result.Error msg -> msg |> Expect.stringContains "should mention short" "short"
    | Result.Ok _ -> failwith "should have rejected"

  testCase "string option None roundtrips in TRES" <| fun _ ->
    let data = {
      StcData.empty with
        ResultEntries = [{ TestId = "test1"; Outcome = Outcome.Pass; DurationMs = 100u; Message = None }]
    }
    let bytes = TestCacheWriter.write data
    match TestCacheReader.read bytes with
    | Result.Ok rt -> (List.head rt.ResultEntries).Message |> Expect.isNone "should be None"
    | Result.Error e -> failwith e

  testCase "string option Some empty roundtrips in TRES" <| fun _ ->
    let data = {
      StcData.empty with
        ResultEntries = [{ TestId = "test2"; Outcome = Outcome.Fail; DurationMs = 50u; Message = Some "" }]
    }
    let bytes = TestCacheWriter.write data
    match TestCacheReader.read bytes with
    | Result.Ok rt -> (List.head rt.ResultEntries).Message |> Expect.equal "should be Some empty" (Some "")
    | Result.Error e -> failwith e

  testPropertyWithConfig { FsCheckConfig.defaultConfig with maxTest = 200 }
    "full roundtrip random test caches" <|
      Prop.forAll (Arb.fromGen genStcData) (fun data ->
        let bytes = TestCacheWriter.write data
        match TestCacheReader.read bytes with
        | Result.Ok rt -> compareStc data rt
        | Result.Error _ -> false)

  testPropertyWithConfig { FsCheckConfig.defaultConfig with maxTest = 100 }
    "header CRC validates" <|
      Prop.forAll (Arb.fromGen genStcData) (fun data ->
        let bytes = TestCacheWriter.write data
        let storedCrc = BitConverter.ToUInt32(bytes, 36)
        let forCrc = Array.copy bytes
        forCrc.[36] <- 0uy; forCrc.[37] <- 0uy; forCrc.[38] <- 0uy; forCrc.[39] <- 0uy
        Crc32.computeAll forCrc = storedCrc)

  testPropertyWithConfig { FsCheckConfig.defaultConfig with maxTest = 100 }
    "total_file_size field matches actual" <|
      Prop.forAll (Arb.fromGen genStcData) (fun data ->
        let bytes = TestCacheWriter.write data
        let storedSize = BitConverter.ToUInt64(bytes, 24)
        uint64 bytes.Length = storedSize)
]

// ─── Mapping & Integration Tests ─────────────────────────────────

open SageFs.Features.LiveTesting

let genTestId =
  Gen.elements [ for i in 1..20 -> sprintf "%016X" (abs (hash i)) ]
  |> Gen.map TestId.TestId

let genCoverageBitmap =
  gen {
    let! count = Gen.choose (0, 128)
    let wordCount = (count + 63) / 64
    let! words = Gen.arrayOfLength wordCount (Gen.map uint64 (Gen.choose (0, Int32.MaxValue)))
    return { CoverageBitmap.Bits = words; Count = count }
  }

let genLiveTestResult =
  Gen.oneof [
    Gen.map (fun ms -> TestResult.Passed (TimeSpan.FromMilliseconds(float ms))) (Gen.choose (0, 10000))
    gen {
      let! ms = Gen.choose (0, 10000)
      let! msg = Gen.elements ["assertion failed"; "expected 42"; "null ref"; "timeout"]
      return TestResult.Failed (TestFailure.AssertionFailed msg, TimeSpan.FromMilliseconds(float ms))
    }
    Gen.map TestResult.Skipped (Gen.elements ["reason1"; "reason2"])
    Gen.constant TestResult.NotRun
  ]

let genTestRunResult =
  gen {
    let! tid = genTestId
    let! name = Gen.elements ["test1"; "test2"; "myTest.should_work"]
    let! result = genLiveTestResult
    return {
      TestRunResult.TestId = tid
      TestName = name
      Result = result
      Timestamp = DateTimeOffset.UtcNow
      Output = None
    }
  }

let genLiveTestState =
  gen {
    let! n = Gen.choose (0, 10)
    let! tids = Gen.listOfLength n genTestId
    let! bitmaps = Gen.listOfLength n genCoverageBitmap
    let! results = Gen.listOfLength n genTestRunResult
    let coverageMap = List.zip tids bitmaps |> Map.ofList
    let resultsMap =
      List.zip tids results
      |> List.map (fun (tid, r) -> tid, { r with TestId = tid })
      |> Map.ofList
    let! gen = Gen.choose (0, 100)
    return { LiveTestState.empty with
               TestCoverageBitmaps = coverageMap
               LastResults = resultsMap
               LastGeneration = RunGeneration gen }
  }

let private fsCheckConfig = { FsCheckConfig.defaultConfig with maxTest = 100 }

let stcMappingTests = testList "STC Mapping" [

  testPropertyWithConfig fsCheckConfig "coverage entry count roundtrips" <|
    Prop.forAll (Arb.fromGen genLiveTestState) (fun state ->
      let stcData = TestCacheMapping.fromLiveTestState state
      let restored = TestCacheMapping.toLiveTestState stcData
      state.TestCoverageBitmaps |> Map.count
      |> Expect.equal "same coverage count" (restored.TestCoverageBitmaps |> Map.count))

  testPropertyWithConfig fsCheckConfig "coverage bitmap words preserved" <|
    Prop.forAll (Arb.fromGen genLiveTestState) (fun state ->
      let stcData = TestCacheMapping.fromLiveTestState state
      let restored = TestCacheMapping.toLiveTestState stcData
      state.TestCoverageBitmaps
      |> Map.iter (fun tid bm ->
        match Map.tryFind tid restored.TestCoverageBitmaps with
        | Some rbm -> rbm.Bits |> Expect.equal "same bits" bm.Bits
        | None -> failwith (sprintf "Missing coverage for %A" tid)))

  testPropertyWithConfig fsCheckConfig "result count roundtrips" <|
    Prop.forAll (Arb.fromGen genLiveTestState) (fun state ->
      let stcData = TestCacheMapping.fromLiveTestState state
      let restored = TestCacheMapping.toLiveTestState stcData
      state.LastResults |> Map.count
      |> Expect.equal "same result count" (restored.LastResults |> Map.count))

  testPropertyWithConfig fsCheckConfig "outcome roundtrips" <|
    Prop.forAll (Arb.fromGen genLiveTestState) (fun state ->
      let stcData = TestCacheMapping.fromLiveTestState state
      let restored = TestCacheMapping.toLiveTestState stcData
      state.LastResults
      |> Map.iter (fun tid runResult ->
        match Map.tryFind tid restored.LastResults with
        | Some rr ->
          let classify r =
            match r with
            | TestResult.Passed _ -> "pass"
            | TestResult.Failed _ -> "fail"
            | TestResult.Skipped _ -> "skip"
            | TestResult.NotRun -> "skip"
          classify rr.Result |> Expect.equal "same outcome" (classify runResult.Result)
        | None -> failwith (sprintf "Missing result for %A" tid)))

  testPropertyWithConfig fsCheckConfig "generation roundtrips" <|
    Prop.forAll (Arb.fromGen genLiveTestState) (fun state ->
      let stcData = TestCacheMapping.fromLiveTestState state
      let restored = TestCacheMapping.toLiveTestState stcData
      restored.LastGeneration |> Expect.equal "same generation" state.LastGeneration)

  testPropertyWithConfig fsCheckConfig "duration within 1ms" <|
    Prop.forAll (Arb.fromGen genLiveTestState) (fun state ->
      let stcData = TestCacheMapping.fromLiveTestState state
      let restored = TestCacheMapping.toLiveTestState stcData
      state.LastResults
      |> Map.iter (fun tid runResult ->
        match Map.tryFind tid restored.LastResults with
        | Some rr ->
          let getMs r =
            match r with
            | TestResult.Passed d -> d.TotalMilliseconds
            | TestResult.Failed (_, d) -> d.TotalMilliseconds
            | _ -> 0.0
          let diff = abs (getMs runResult.Result - getMs rr.Result)
          (diff, 1.0) |> Expect.isLessThanOrEqual "duration within 1ms"
        | None -> ()))
]

let stcE2eTests = testList "STC End-to-End" [

  testPropertyWithConfig fsCheckConfig "full binary roundtrip preserves coverage" <|
    Prop.forAll (Arb.fromGen genLiveTestState) (fun state ->
      let stcData = TestCacheMapping.fromLiveTestState state
      let bytes = TestCacheWriter.write stcData
      match TestCacheReader.read bytes with
      | Ok restored ->
        restored.CoverageEntries.Length
        |> Expect.equal "same count" stcData.CoverageEntries.Length
        List.zip stcData.CoverageEntries restored.CoverageEntries
        |> List.iter (fun (orig, rest) ->
          rest.TestId |> Expect.equal "same test id" orig.TestId
          rest.BitmapWords |> Expect.equal "same bits" orig.BitmapWords)
      | Error e -> failwith (sprintf "Read failed: %s" e))

  testPropertyWithConfig fsCheckConfig "full binary roundtrip preserves results" <|
    Prop.forAll (Arb.fromGen genLiveTestState) (fun state ->
      let stcData = TestCacheMapping.fromLiveTestState state
      let bytes = TestCacheWriter.write stcData
      match TestCacheReader.read bytes with
      | Ok restored ->
        restored.ResultEntries.Length
        |> Expect.equal "same count" stcData.ResultEntries.Length
        List.zip stcData.ResultEntries restored.ResultEntries
        |> List.iter (fun (orig, rest) ->
          rest.TestId |> Expect.equal "same id" orig.TestId
          rest.Outcome |> Expect.equal "same outcome" orig.Outcome
          rest.DurationMs |> Expect.equal "same duration" orig.DurationMs
          rest.Message |> Expect.equal "same message" orig.Message)
      | Error e -> failwith (sprintf "Read failed: %s" e))

  testPropertyWithConfig fsCheckConfig "full binary roundtrip preserves generation" <|
    Prop.forAll (Arb.fromGen genLiveTestState) (fun state ->
      let stcData = TestCacheMapping.fromLiveTestState state
      let bytes = TestCacheWriter.write stcData
      match TestCacheReader.read bytes with
      | Ok restored ->
        restored.ImapGeneration
        |> Expect.equal "same generation" stcData.ImapGeneration
      | Error e -> failwith (sprintf "Read failed: %s" e))

  testPropertyWithConfig { fsCheckConfig with maxTest = 20 } "file save/load roundtrip" <|
    Prop.forAll (Arb.fromGen genLiveTestState) (fun state ->
      let stcData = TestCacheMapping.fromLiveTestState state
      let hash = sprintf "test-%d" (abs (hash state))
      let tmpDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "sagefs-test-" + System.Guid.NewGuid().ToString("N"))
      match TestCacheFile.save tmpDir hash stcData with
      | Ok path ->
        match TestCacheFile.load tmpDir hash with
        | Ok loaded ->
          loaded.CoverageEntries.Length
          |> Expect.equal "same coverage count" stcData.CoverageEntries.Length
          loaded.ResultEntries.Length
          |> Expect.equal "same result count" stcData.ResultEntries.Length
          loaded.ImapGeneration
          |> Expect.equal "same generation" stcData.ImapGeneration
          try System.IO.Directory.Delete(tmpDir, true) with _ -> ()
        | Error e -> failwith (sprintf "Load failed: %s" e)
      | Error e -> failwith (sprintf "Save failed: %s" e))
]

// ─── Daemon Persistence Integration ──────────────────────────────

let daemonPersistenceTests = testList "Daemon Persistence" [
  test "projectHash is deterministic" {
    let hash1 = DaemonPersistence.projectHash ["A.fsproj"; "B.fsproj"]
    let hash2 = DaemonPersistence.projectHash ["A.fsproj"; "B.fsproj"]
    hash1 |> Expect.equal "same hash for same input" hash2
  }

  test "projectHash is order-independent" {
    let hash1 = DaemonPersistence.projectHash ["A.fsproj"; "B.fsproj"]
    let hash2 = DaemonPersistence.projectHash ["B.fsproj"; "A.fsproj"]
    hash1 |> Expect.equal "order doesn't matter" hash2
  }

  test "projectHash is case-insensitive" {
    let hash1 = DaemonPersistence.projectHash ["MyProject.fsproj"]
    let hash2 = DaemonPersistence.projectHash ["myproject.fsproj"]
    hash1 |> Expect.equal "case doesn't matter" hash2
  }

  test "projectHash normalizes path separators" {
    let hash1 = DaemonPersistence.projectHash [@"src\MyProject.fsproj"]
    let hash2 = DaemonPersistence.projectHash ["src/MyProject.fsproj"]
    hash1 |> Expect.equal "separators normalized" hash2
  }

  test "projectHash differs for different projects" {
    let hash1 = DaemonPersistence.projectHash ["A.fsproj"]
    let hash2 = DaemonPersistence.projectHash ["B.fsproj"]
    hash1 |> Expect.notEqual "different projects → different hash" hash2
  }

  test "saveTestCache/loadTestCache roundtrip with empty state" {
    let tmpDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "sagefs-dp-" + System.Guid.NewGuid().ToString("N"))
    let projects = ["Test.fsproj"]
    let state = LiveTesting.LiveTestState.empty
    match DaemonPersistence.saveTestCache tmpDir projects state with
    | Ok _ ->
      match DaemonPersistence.loadTestCache tmpDir projects with
      | Ok restored ->
        restored.TestCoverageBitmaps |> Map.count |> Expect.equal "empty coverage" 0
        restored.LastResults |> Map.count |> Expect.equal "empty results" 0
      | Error e -> failwith e
    | Error e -> failwith e
    try System.IO.Directory.Delete(tmpDir, true) with _ -> ()
  }

  test "loadTestCache returns Error for missing cache" {
    let tmpDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "sagefs-dp-" + System.Guid.NewGuid().ToString("N"))
    match DaemonPersistence.loadTestCache tmpDir ["NonExistent.fsproj"] with
    | Error _ -> ()
    | Ok _ -> failwith "should have returned error for missing cache"
  }

  testPropertyWithConfig { fsCheckConfig with maxTest = 50 } "test cache roundtrip preserves coverage count" <|
    Prop.forAll (Arb.fromGen genLiveTestState) (fun state ->
      let tmpDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "sagefs-dp-" + System.Guid.NewGuid().ToString("N"))
      match DaemonPersistence.saveTestCache tmpDir ["Test.fsproj"] state with
      | Ok _ ->
        match DaemonPersistence.loadTestCache tmpDir ["Test.fsproj"] with
        | Ok restored ->
          restored.TestCoverageBitmaps |> Map.count
          |> Expect.equal "same coverage count" (state.TestCoverageBitmaps |> Map.count)
          try System.IO.Directory.Delete(tmpDir, true) with _ -> ()
        | Error e -> failwith e
      | Error e -> failwith e)

  testPropertyWithConfig { fsCheckConfig with maxTest = 50 } "test cache roundtrip preserves result count" <|
    Prop.forAll (Arb.fromGen genLiveTestState) (fun state ->
      let tmpDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "sagefs-dp-" + System.Guid.NewGuid().ToString("N"))
      match DaemonPersistence.saveTestCache tmpDir ["Test.fsproj"] state with
      | Ok _ ->
        match DaemonPersistence.loadTestCache tmpDir ["Test.fsproj"] with
        | Ok restored ->
          restored.LastResults |> Map.count
          |> Expect.equal "same result count" (state.LastResults |> Map.count)
          try System.IO.Directory.Delete(tmpDir, true) with _ -> ()
        | Error e -> failwith e
      | Error e -> failwith e)
]

// ─── RestoreTestCache Elm Message ───────────────────────────────

let restoreTestCacheTests = testList "RestoreTestCache" [

  test "RestoreTestCache injects coverage bitmaps into model" {
    let tid = LiveTesting.TestId.TestId "test1"
    let bm : LiveTesting.CoverageBitmap = { Bits = [| 0xFFUL |]; Count = 64 }
    let cachedState =
      { LiveTesting.LiveTestState.empty with
          TestCoverageBitmaps = Map.ofList [ tid, bm ] }

    let model = (SageFsModel.initial())
    let updated, effects = SageFsUpdate.update (SageFsMsg.RestoreTestCache cachedState) model

    updated.LiveTesting.TestState.TestCoverageBitmaps.Count
    |> Expect.equal "should have 1 coverage entry" 1

    effects |> Expect.isEmpty "should produce no effects"
  }

  test "RestoreTestCache injects last results into model" {
    let tid = LiveTesting.TestId.TestId "test1"
    let result : LiveTesting.TestRunResult = {
      TestId = tid
      TestName = "test1"
      Result = LiveTesting.TestResult.Passed (TimeSpan.FromMilliseconds 42.0)
      Timestamp = DateTimeOffset.UtcNow
      Output = None
    }
    let cachedState =
      { LiveTesting.LiveTestState.empty with
          LastResults = Map.ofList [ tid, result ] }

    let model = (SageFsModel.initial())
    let updated, _ = SageFsUpdate.update (SageFsMsg.RestoreTestCache cachedState) model

    updated.LiveTesting.TestState.LastResults.Count
    |> Expect.equal "should have 1 result entry" 1
  }

  test "RestoreTestCache injects generation into model" {
    let gen = LiveTesting.RunGeneration 42
    let cachedState =
      { LiveTesting.LiveTestState.empty with LastGeneration = gen }

    let model = (SageFsModel.initial())
    let updated, _ = SageFsUpdate.update (SageFsMsg.RestoreTestCache cachedState) model

    updated.LiveTesting.TestState.LastGeneration
    |> Expect.equal "should restore generation" gen
  }

  test "RestoreTestCache preserves existing model fields" {
    let cachedState =
      { LiveTesting.LiveTestState.empty with
          TestCoverageBitmaps = Map.ofList [ LiveTesting.TestId.TestId "t1", { Bits = [| 1UL |]; Count = 64 } ] }

    let model =
      { (SageFsModel.initial()) with ThemeName = "Custom" }
    let updated, _ = SageFsUpdate.update (SageFsMsg.RestoreTestCache cachedState) model

    updated.ThemeName |> Expect.equal "should preserve theme" "Custom"
  }

  test "RestoreTestCache with empty state is a no-op" {
    let model = (SageFsModel.initial())
    let updated, effects = SageFsUpdate.update (SageFsMsg.RestoreTestCache LiveTesting.LiveTestState.empty) model

    updated.LiveTesting.TestState.TestCoverageBitmaps.Count
    |> Expect.equal "should remain empty" 0
    effects |> Expect.isEmpty "should produce no effects"
  }
]

// ─── Daemon Roundtrip Properties ────────────────────────────────

let daemonRoundtripPropertyTests = testList "Daemon Roundtrip Properties" [

  testPropertyWithConfig fsCheckConfig "coverage bitmap count survives save→load roundtrip" <|
    fun (count: PositiveInt) ->
      let n = min count.Get 50
      let coverages =
        [ for i in 1..n ->
            let tid = LiveTesting.TestId.TestId (sprintf "test_%d" i)
            let bm : LiveTesting.CoverageBitmap = { Bits = [| uint64 i |]; Count = 64 }
            tid, bm ]
        |> Map.ofList
      let state = { LiveTesting.LiveTestState.empty with TestCoverageBitmaps = coverages }
      let dir = IO.Path.Combine(IO.Path.GetTempPath(), sprintf "dp_rt_%s" (Guid.NewGuid().ToString("N")))
      try
        match DaemonPersistence.saveTestCache dir ["test.fsproj"] state with
        | Result.Ok _ ->
          match DaemonPersistence.loadTestCache dir ["test.fsproj"] with
          | Result.Ok loaded -> loaded.TestCoverageBitmaps.Count = n
          | Result.Error e -> failwith e
        | Result.Error e -> failwith e
      finally
        match IO.Directory.Exists(dir) with
        | true -> IO.Directory.Delete(dir, true)
        | false -> ()

  testPropertyWithConfig fsCheckConfig "result count survives save→load roundtrip" <|
    fun (count: PositiveInt) ->
      let n = min count.Get 50
      let results =
        [ for i in 1..n ->
            let tid = LiveTesting.TestId.TestId (sprintf "test_%d" i)
            let rr : LiveTesting.TestRunResult = {
              TestId = tid
              TestName = sprintf "test_%d" i
              Result = LiveTesting.TestResult.Passed (TimeSpan.FromMilliseconds(float i))
              Timestamp = DateTimeOffset.UtcNow
              Output = None
            }
            tid, rr ]
        |> Map.ofList
      let state = { LiveTesting.LiveTestState.empty with LastResults = results }
      let dir = IO.Path.Combine(IO.Path.GetTempPath(), sprintf "dp_rt_%s" (Guid.NewGuid().ToString("N")))
      try
        match DaemonPersistence.saveTestCache dir ["test.fsproj"] state with
        | Result.Ok _ ->
          match DaemonPersistence.loadTestCache dir ["test.fsproj"] with
          | Result.Ok loaded -> loaded.LastResults.Count = n
          | Result.Error e -> failwith e
        | Result.Error e -> failwith e
      finally
        match IO.Directory.Exists(dir) with
        | true -> IO.Directory.Delete(dir, true)
        | false -> ()

  testPropertyWithConfig fsCheckConfig "generation survives save→load roundtrip" <|
    fun (gen: PositiveInt) ->
      let state = { LiveTesting.LiveTestState.empty with LastGeneration = LiveTesting.RunGeneration gen.Get }
      let dir = IO.Path.Combine(IO.Path.GetTempPath(), sprintf "dp_rt_%s" (Guid.NewGuid().ToString("N")))
      try
        match DaemonPersistence.saveTestCache dir ["test.fsproj"] state with
        | Result.Ok _ ->
          match DaemonPersistence.loadTestCache dir ["test.fsproj"] with
          | Result.Ok loaded -> loaded.LastGeneration = LiveTesting.RunGeneration gen.Get
          | Result.Error e -> failwith e
        | Result.Error e -> failwith e
      finally
        match IO.Directory.Exists(dir) with
        | true -> IO.Directory.Delete(dir, true)
        | false -> ()
]

// ─── Robustness Tests ────────────────────────────────────────────

let robustnessTests = testList "Robustness rejects corrupted and adversarial input" [
  testCase "all Outcome values roundtrip through STC" <| fun _ ->
    for o in [ Outcome.Pass; Outcome.Fail; Outcome.Skip; Outcome.Error; Outcome.NotRun ] do
      let d: StcData = {
        CoverageEntries = []; ImapGeneration = 0u; CreatedAtMs = 0L
        ResultEntries = [{ TestId = "t1"; Outcome = o; DurationMs = 1u; Message = None }] }
      let bytes = TestCacheWriter.write d
      match TestCacheReader.read bytes with
      | Result.Ok rt ->
        rt.ResultEntries.[0].Outcome
        |> Expect.equal (sprintf "outcome %A roundtrip" o) o
      | Result.Error e -> failwith e

  testCase "atomic overwrite: STC file replaced correctly" <| fun _ ->
    let dir = IO.Path.Combine(IO.Path.GetTempPath(), sprintf "stc_ow_%s" (Guid.NewGuid().ToString("N")))
    try
      let state1 = { LiveTesting.LiveTestState.empty with LastGeneration = LiveTesting.RunGeneration 1 }
      let state2 = { LiveTesting.LiveTestState.empty with LastGeneration = LiveTesting.RunGeneration 42 }
      DaemonPersistence.saveTestCache dir ["p.fsproj"] state1 |> ignore
      DaemonPersistence.saveTestCache dir ["p.fsproj"] state2 |> ignore
      match DaemonPersistence.loadTestCache dir ["p.fsproj"] with
      | Result.Ok loaded ->
        loaded.LastGeneration
        |> Expect.equal "overwrite kept second write" (LiveTesting.RunGeneration 42)
      | Result.Error e -> failwith e
    finally
      match IO.Directory.Exists(dir) with
      | true -> IO.Directory.Delete(dir, true) | false -> ()

  testPropertyWithConfig { FsCheckConfig.defaultConfig with maxTest = 200 }
    "STC CRC catches any single-bit flip"
    (fun (idx: PositiveInt) ->
      let d: StcData = {
        CoverageEntries = []
        ResultEntries = [{ TestId = "x"; Outcome = Outcome.Pass; DurationMs = 1u; Message = None }]
        ImapGeneration = 1u; CreatedAtMs = 0L }
      let bytes = TestCacheWriter.write d
      let i = idx.Get % bytes.Length
      match i >= 36 && i <= 39 with
      | true -> true
      | false ->
        let c = Array.copy bytes
        c.[i] <- c.[i] ^^^ 0x01uy
        TestCacheReader.read c |> Result.isError)

  testPropertyWithConfig { FsCheckConfig.defaultConfig with maxTest = 100 }
    "STC roundtrip with N results"
    (fun (n: NonNegativeInt) ->
      let n = n.Get % 50
      let entries = [ for i in 0..n-1 -> { TestId = sprintf "t%d" i; Outcome = Outcome.Pass; DurationMs = uint32 i; Message = None } ]
      let d: StcData = { CoverageEntries = []; ResultEntries = entries; ImapGeneration = 1u; CreatedAtMs = 0L }
      let bytes = TestCacheWriter.write d
      match TestCacheReader.read bytes with
      | Result.Ok rt -> rt.ResultEntries.Length = entries.Length
      | Result.Error _ -> false)
]

// ─── Version & Validation Tests ──────────────────────────────────

let versionAndValidationTests = testList "Version & Validation" [
  testCase "STC: rejects file with future min_reader_version" <| fun _ ->
    let d: StcData = {
      CoverageEntries = []
      ResultEntries = [{ TestId = "x"; Outcome = Outcome.Pass; DurationMs = 1u; Message = None }]
      ImapGeneration = 1u; CreatedAtMs = 0L }
    let bytes = TestCacheWriter.write d
    let patched = Array.copy bytes
    let v99 = System.BitConverter.GetBytes(99us)
    System.Array.Copy(v99, 0, patched, 6, 2)
    patched.[36] <- 0uy; patched.[37] <- 0uy; patched.[38] <- 0uy; patched.[39] <- 0uy
    let crc = Crc32.computeAll patched
    System.Array.Copy(System.BitConverter.GetBytes(crc), 0, patched, 36, 4)
    match TestCacheReader.read patched with
    | Result.Error msg ->
      msg |> Expect.stringContains "should mention version" "version"
    | Result.Ok _ -> failwith "STC reader accepted min_reader_version=99"

  testCase "STC: accepts file with current min_reader_version" <| fun _ ->
    let d: StcData = {
      CoverageEntries = []
      ResultEntries = [{ TestId = "x"; Outcome = Outcome.Pass; DurationMs = 1u; Message = None }]
      ImapGeneration = 1u; CreatedAtMs = 0L }
    match TestCacheReader.read (TestCacheWriter.write d) with
    | Result.Ok _ -> ()
    | Result.Error e -> failwith e

  testCase "STC: corrupting TOC directory byte is detected (regression)" <| fun _ ->
    let d: StcData = {
      CoverageEntries = []
      ResultEntries = [{ TestId = "x"; Outcome = Outcome.Pass; DurationMs = 1u; Message = None }]
      ImapGeneration = 1u; CreatedAtMs = 0L }
    let bytes = TestCacheWriter.write d
    let corrupted = Array.copy bytes
    corrupted.[72] <- corrupted.[72] ^^^ 0xFFuy
    match TestCacheReader.read corrupted with
    | Result.Error _ -> ()
    | Result.Ok _ -> failwith "TOC corruption at byte 72 was NOT detected"

  testCase "STC: unknown Outcome byte returns Error" <| fun _ ->
    let d: StcData = {
      CoverageEntries = []
      ResultEntries = [{ TestId = "x"; Outcome = Outcome.Pass; DurationMs = 1u; Message = None }]
      ImapGeneration = 1u; CreatedAtMs = 0L }
    let bytes = TestCacheWriter.write d
    let patched = Array.copy bytes
    let tresOffset = System.BitConverter.ToUInt64(patched, 64 + 2*16 + 4) |> int
    let outcomeByte = tresOffset + 4 + 4 + 1
    patched.[outcomeByte] <- 42uy
    let totalSize = System.BitConverter.ToUInt64(patched, 24) |> int
    let tresPayload = patched.[tresOffset .. totalSize - 1]
    let tresCrc = Crc32.computeAll tresPayload
    System.Array.Copy(System.BitConverter.GetBytes(tresCrc), 0, patched, 108, 4)
    patched.[36] <- 0uy; patched.[37] <- 0uy; patched.[38] <- 0uy; patched.[39] <- 0uy
    let hcrc = Crc32.computeAll patched
    System.Array.Copy(System.BitConverter.GetBytes(hcrc), 0, patched, 36, 4)
    match TestCacheReader.read patched with
    | Result.Error msg ->
      msg |> Expect.stringContains "should mention Outcome" "Outcome"
    | Result.Ok _ -> failwith "Accepted unknown Outcome 42"
]

// ─── Tmp File Cleanup ────────────────────────────────────────────

let tmpCleanupTests = testList "tmp cleanup" [
  test "removes .sagetc.tmp files" {
    let tmpDir = IO.Path.Combine(IO.Path.GetTempPath(), sprintf "sagefs-test-cleanup-%d" (System.Random.Shared.Next()))
    try
      let cacheD = IO.Path.Combine(tmpDir, "cache")
      IO.Directory.CreateDirectory(cacheD) |> ignore
      IO.File.WriteAllText(IO.Path.Combine(cacheD, "abc123.sagetc.tmp"), "junk")
      IO.File.WriteAllText(IO.Path.Combine(cacheD, "abc123.sagetc"), "real")
      let removed = TestCacheFile.cleanupOrphanedTmpFiles tmpDir
      removed |> Expect.equal "should remove 1 tmp file" 1
      IO.File.Exists(IO.Path.Combine(cacheD, "abc123.sagetc.tmp"))
      |> Expect.isFalse "tmp file should be gone"
      IO.File.Exists(IO.Path.Combine(cacheD, "abc123.sagetc"))
      |> Expect.isTrue "real file should remain"
    finally
      if IO.Directory.Exists(tmpDir) then IO.Directory.Delete(tmpDir, true)
  }

  test "returns 0 when no tmp files" {
    let tmpDir = IO.Path.Combine(IO.Path.GetTempPath(), sprintf "sagefs-test-cleanup-%d" (System.Random.Shared.Next()))
    try
      let cacheD = IO.Path.Combine(tmpDir, "cache")
      IO.Directory.CreateDirectory(cacheD) |> ignore
      IO.File.WriteAllText(IO.Path.Combine(cacheD, "abc.sagetc"), "real")
      let removed = TestCacheFile.cleanupOrphanedTmpFiles tmpDir
      removed |> Expect.equal "should remove 0" 0
    finally
      if IO.Directory.Exists(tmpDir) then IO.Directory.Delete(tmpDir, true)
  }

  test "returns 0 when dir does not exist" {
    let tmpDir = IO.Path.Combine(IO.Path.GetTempPath(), sprintf "sagefs-test-cleanup-nonexist-%d" (System.Random.Shared.Next()))
    let removed = TestCacheFile.cleanupOrphanedTmpFiles tmpDir
    removed |> Expect.equal "should remove 0 for nonexistent dir" 0
  }
]

// ─── Bounds Check Tests ──────────────────────────────────────────

let boundsCheckTests = testList "bounds check" [
  test "STC rejects out-of-bounds offset" {
    let data: StcData = {
      CoverageEntries = [ { TestId = "t1"; BitmapWordCount = 1u; BitmapWords = [| 1UL |] } ]
      ResultEntries = [ { TestId = "t1"; Outcome = Outcome.Pass; DurationMs = 10u; Message = None } ]
      ImapGeneration = 1u; CreatedAtMs = 0L
    }
    let bytes = TestCacheWriter.write data
    let patchedBytes = Array.copy bytes
    // TOC entry: tag(4) + offset(8) + crc(4). Offset is at tocStart + 4
    BitConverter.GetBytes(0xFFFFFFFF_FFFFFFFFuL)
    |> fun b -> Array.Copy(b, 0, patchedBytes, 64 + 4, 8)
    patchedBytes.[36] <- 0uy; patchedBytes.[37] <- 0uy; patchedBytes.[38] <- 0uy; patchedBytes.[39] <- 0uy
    let crc = Crc32.computeAll patchedBytes
    BitConverter.GetBytes(crc) |> fun b -> Array.Copy(b, 0, patchedBytes, 36, 4)
    match TestCacheReader.read patchedBytes with
    | Result.Error msg ->
      (msg.Contains("offset") || msg.Contains("bounds") || msg.Contains("exceeds"))
      |> Expect.isTrue (sprintf "error mentions bounds: '%s'" msg)
    | Result.Ok _ -> failwith "Should have rejected out-of-bounds offset"
  }

  // W4: readLpString must guard against uint32→int32 overflow (lengths > Int32.MaxValue)
  test "readLpString rejects length > Int32.MaxValue (overflow guard)" {
    use ms = new System.IO.MemoryStream()
    use bw = new System.IO.BinaryWriter(ms)
    bw.Write(0x80000000u)  // = 2147483648u, overflows to -2147483648 when cast to int32
    bw.Write([| 0uy |])    // some padding bytes
    ms.Position <- 0L
    use br = new System.IO.BinaryReader(ms)
    let ex =
      try
        BinaryPrimitives.readLpString br |> ignore
        failwith "expected exception was not thrown"
      with e -> e
    (ex :? InvalidOperationException)
    |> Expect.isTrue (sprintf "must throw InvalidOperationException, got %s: %s" (ex.GetType().Name) ex.Message)
    (ex.Message.ToLowerInvariant().Contains("overflow")
     || ex.Message.ToLowerInvariant().Contains("exceeds")
     || ex.Message.ToLowerInvariant().Contains("maximum"))
    |> Expect.isTrue (sprintf "message must describe the overflow: '%s'" ex.Message)
  }
]

// ─── Version Consistency Tests ───────────────────────────────────

let versionConsistencyTests = testList "version consistency" [
  test "STC version consistency" {
    let data: StcData = StcData.empty
    let bytes = TestCacheWriter.write data
    let writerMinReader = BitConverter.ToUInt16(bytes, 6)
    writerMinReader |> Expect.equal "writer min_reader matches reader constant" TestCacheReader.readerVersion
  }
]

// ─── Golden File Tests ───────────────────────────────────────────

let goldenFileTests = testList "golden files" [
  test "STC writer is deterministic" {
    let data: StcData = {
      CoverageEntries = [
        { TestId = "test-alpha"; BitmapWordCount = 2u; BitmapWords = [| 0xDEADBEEF_CAFEBABEuL; 0x0102030405060708UL |] }
        { TestId = "test-beta"; BitmapWordCount = 1u; BitmapWords = [| 0UL |] }
      ]
      ResultEntries = [
        { TestId = "test-alpha"; Outcome = Outcome.Pass; DurationMs = 100u; Message = None }
        { TestId = "test-beta"; Outcome = Outcome.Fail; DurationMs = 200u; Message = Some "assertion failed" }
      ]
      ImapGeneration = 42u; CreatedAtMs = 1709337600000L
    }
    let bytes1 = TestCacheWriter.write data
    let bytes2 = TestCacheWriter.write data
    bytes1 |> Expect.equal "identical input -> identical bytes" bytes2
  }

  test "STC golden roundtrip" {
    let data: StcData = {
      CoverageEntries = [
        { TestId = "golden-test"; BitmapWordCount = 1u; BitmapWords = [| 0xABCD1234_5678EF00UL |] }
      ]
      ResultEntries = [
        { TestId = "golden-test"; Outcome = Outcome.Pass; DurationMs = 50u; Message = None }
      ]
      ImapGeneration = 7u; CreatedAtMs = 1709337600000L
    }
    let bytes = TestCacheWriter.write data
    Text.Encoding.ASCII.GetString(bytes, 0, 4) |> Expect.equal "magic" "STC1"
    match TestCacheReader.read bytes with
    | Result.Ok rt -> rt |> Expect.equal "roundtrip" data
    | Result.Error e -> failwith e
  }
]

// ─── Centralized Enum tryParse Tests ─────────────────────────────

let enumTryParseTests = testList "centralized enum tryParse" [
  test "Outcome.tryParse valid range" {
    for b in 0uy .. 3uy do
      match Outcome.tryParse b with
      | Result.Ok _ -> ()
      | Result.Error e -> failwith (sprintf "Valid byte %d rejected: %s" b e)
  }
  test "Outcome.tryParse invalid byte" {
    for b in [ 5uy; 128uy; 255uy ] do
      match Outcome.tryParse b with
      | Result.Error _ -> ()
      | Result.Ok v -> failwith (sprintf "Invalid byte %d accepted as %A" b v)
  }
]

// ─── P0 Field-Level Bounds Checking ──────────────────────────────

let fieldBoundsTests = testList "field-level bounds" [
  testCase "readLpString rejects length exceeding remaining bytes" <| fun _ ->
    use ms = new IO.MemoryStream()
    use bw = new IO.BinaryWriter(ms)
    bw.Write(1000u)
    bw.Write([| 0x41uy; 0x42uy; 0x43uy |])
    bw.Flush()
    ms.Position <- 0L
    use br = new IO.BinaryReader(ms)
    let threw =
      try BinaryPrimitives.readLpString br |> ignore; false
      with _ -> true
    threw |> Expect.isTrue "should reject length exceeding remaining bytes"

  testCase "readLpStringOption rejects length exceeding remaining bytes" <| fun _ ->
    use ms = new IO.MemoryStream()
    use bw = new IO.BinaryWriter(ms)
    bw.Write(0x7FFFFFFFu)
    bw.Write([| 0x41uy |])
    bw.Flush()
    ms.Position <- 0L
    use br = new IO.BinaryReader(ms)
    let threw =
      try BinaryPrimitives.readLpStringOption br |> ignore; false
      with _ -> true
    threw |> Expect.isTrue "should reject oversized optional string length"

  testCase "readLpString with 0xFFFFFFFF does not OOM" <| fun _ ->
    use ms = new IO.MemoryStream()
    use bw = new IO.BinaryWriter(ms)
    bw.Write(0xFFFFFFFFu)
    bw.Flush()
    ms.Position <- 0L
    use br = new IO.BinaryReader(ms)
    let threw =
      try BinaryPrimitives.readLpString br |> ignore; false
      with :? OutOfMemoryException -> false | _ -> true
    threw |> Expect.isTrue "should throw clean error, not OOM"

  testCase "readLpStringOption None sentinel passes" <| fun _ ->
    use ms = new IO.MemoryStream()
    use bw = new IO.BinaryWriter(ms)
    bw.Write(0xFFFFFFFFu)
    bw.Flush()
    ms.Position <- 0L
    use br = new IO.BinaryReader(ms)
    BinaryPrimitives.readLpStringOption br
    |> Expect.isNone "should return None for sentinel"

  testCase "readLpString valid string still works" <| fun _ ->
    use ms = new IO.MemoryStream()
    use bw = new IO.BinaryWriter(ms)
    BinaryPrimitives.writeLpString bw "hello world"
    bw.Flush()
    ms.Position <- 0L
    use br = new IO.BinaryReader(ms)
    BinaryPrimitives.readLpString br
    |> Expect.equal "should read valid string" "hello world"

  testCase "readLpStringOption valid string still works" <| fun _ ->
    use ms = new IO.MemoryStream()
    use bw = new IO.BinaryWriter(ms)
    BinaryPrimitives.writeLpStringOption bw (Some "test")
    bw.Flush()
    ms.Position <- 0L
    use br = new IO.BinaryReader(ms)
    BinaryPrimitives.readLpStringOption br
    |> Expect.equal "should read valid optional string" (Some "test")
]

let fuzzPropertyTests = testList "fuzz properties" [
  testPropertyWithConfig { FsCheckConfig.defaultConfig with maxTest = 500 }
    "readLpString never OOMs on random bytes" <| fun (data: byte[]) ->
    match data.Length >= 4 with
    | false -> ()
    | true ->
      use ms = new IO.MemoryStream(data)
      use br = new IO.BinaryReader(ms)
      try BinaryPrimitives.readLpString br |> ignore
      with :? OutOfMemoryException -> failwith "OOM on random input" | _ -> ()

  testPropertyWithConfig { FsCheckConfig.defaultConfig with maxTest = 500 }
    "readLpStringOption never OOMs on random bytes" <| fun (data: byte[]) ->
    match data.Length >= 4 with
    | false -> ()
    | true ->
      use ms = new IO.MemoryStream(data)
      use br = new IO.BinaryReader(ms)
      try BinaryPrimitives.readLpStringOption br |> ignore
      with :? OutOfMemoryException -> failwith "OOM on random input" | _ -> ()

  testPropertyWithConfig { FsCheckConfig.defaultConfig with maxTest = 200 }
    "STC reader never OOMs on random bytes" <| fun (data: byte[]) ->
    match data.Length >= 64 with
    | false -> ()
    | true ->
      try TestCacheReader.read data |> ignore
      with :? OutOfMemoryException -> failwith "STC reader OOM" | _ -> ()

  testPropertyWithConfig { FsCheckConfig.defaultConfig with maxTest = 500 }
    "lp-string roundtrip preserved after bounds fix" <| fun (s: string) ->
    let s = match s with null -> "" | v -> v
    use ms = new IO.MemoryStream()
    use bw = new IO.BinaryWriter(ms)
    BinaryPrimitives.writeLpString bw s
    bw.Flush()
    ms.Position <- 0L
    use br = new IO.BinaryReader(ms)
    BinaryPrimitives.readLpString br
    |> Expect.equal "roundtrip should preserve string" s

  testPropertyWithConfig { FsCheckConfig.defaultConfig with maxTest = 500 }
    "lp-string-option roundtrip preserved after bounds fix" <| fun (opt: string option) ->
    let opt = match opt with Some null -> Some "" | v -> v
    use ms = new IO.MemoryStream()
    use bw = new IO.BinaryWriter(ms)
    BinaryPrimitives.writeLpStringOption bw opt
    bw.Flush()
    ms.Position <- 0L
    use br = new IO.BinaryReader(ms)
    BinaryPrimitives.readLpStringOption br
    |> Expect.equal "roundtrip should preserve optional string" opt
]

// ─── Session Isolation Property Tests ────────────────────────────

let sessionIsolationTests = testList "Session Isolation" [

  testProperty "STC: two caches roundtrip independently" <| fun (PositiveInt countA) (PositiveInt countB) ->
    let cA = min countA 10
    let cB = min countB 10
    let mkCache (tag: string) count =
      { StcData.CoverageEntries = []
        ResultEntries = [
          for i in 1..count do
            { TestId = sprintf "%s_%d" tag i
              Outcome = Outcome.Pass
              DurationMs = uint32 (100 * i)
              Message = Option.None } ]
        ImapGeneration = 1u
        CreatedAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }
    let dataA = mkCache "Alpha" cA
    let dataB = mkCache "Beta" cB
    let bytesA = TestCacheWriter.write dataA
    let bytesB = TestCacheWriter.write dataB
    let rtA = TestCacheReader.read bytesA
    let rtB = TestCacheReader.read bytesB
    match rtA, rtB with
    | Ok a, Ok b ->
      a.ResultEntries.Length |> Expect.equal "A result count" cA
      b.ResultEntries.Length |> Expect.equal "B result count" cB
    | Error e, _ -> failwithf "STC A failed: %s" e
    | _, Error e -> failwithf "STC B failed: %s" e

  testCase "inflated STC section count rejected even with recomputed CRC" <| fun _ ->
    let bytes = TestCacheWriter.write StcData.empty
    let patched = Array.copy bytes
    let cb = BitConverter.GetBytes(0xFFFFFFu)
    Array.Copy(cb, 0, patched, 8, 4)
    // Recompute the whole-file CRC so the tamper is CONSISTENT — the bounds
    // check must catch the inflated count, not the CRC.
    let forCrc = Array.copy patched
    forCrc.[36] <- 0uy; forCrc.[37] <- 0uy; forCrc.[38] <- 0uy; forCrc.[39] <- 0uy
    let crc = Crc32.computeAll forCrc
    let cb2 = BitConverter.GetBytes(crc)
    Array.Copy(cb2, 0, patched, 36, 4)
    match TestCacheReader.read patched with
    | Error msg ->
      (msg.Contains("count") || msg.Contains("capacity"))
      |> Expect.isTrue "error should cite the inflated section count"
    | Ok _ -> failwith "Should reject an inflated STC section count even with a valid CRC"

  testCase "declared STC total size mismatch rejected even with recomputed CRC" <| fun _ ->
    let bytes = TestCacheWriter.write StcData.empty
    let patched = Array.copy bytes
    let cb = BitConverter.GetBytes(uint64 bytes.Length + 2048UL)
    Array.Copy(cb, 0, patched, 24, 8)
    let forCrc = Array.copy patched
    forCrc.[36] <- 0uy; forCrc.[37] <- 0uy; forCrc.[38] <- 0uy; forCrc.[39] <- 0uy
    let crc = Crc32.computeAll forCrc
    let cb2 = BitConverter.GetBytes(crc)
    Array.Copy(cb2, 0, patched, 36, 4)
    match TestCacheReader.read patched with
    | Error msg ->
      (msg.Contains("total size") || msg.Contains("actual file size"))
      |> Expect.isTrue "error should cite the declared total size"
    | Ok _ -> failwith "Should reject a declared total size mismatch even with a valid CRC"
]

[<Tests>]
let allBinaryFormatTests = testList "Binary Format encode-decode identity and corruption detection" [
  primitiveTests
  stcTests
  stcMappingTests
  stcE2eTests
  daemonPersistenceTests
  restoreTestCacheTests
  daemonRoundtripPropertyTests
  robustnessTests
  versionAndValidationTests
  tmpCleanupTests
  boundsCheckTests
  versionConsistencyTests
  goldenFileTests
  enumTryParseTests
  fieldBoundsTests
  fuzzPropertyTests
  sessionIsolationTests
]
