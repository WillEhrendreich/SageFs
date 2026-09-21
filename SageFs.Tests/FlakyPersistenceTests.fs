/// ## FlakyPersistenceTests
///
/// Proves `SageFs.Features.TestCacheMapping`/`TestCacheWriter`/`TestCacheReader`
/// carry `LiveTestState.FlakyHistory` through the .sagetc binary cache.
///
/// Before this file's fix, `TestCacheMapping.fromLiveTestState` serialized
/// only `TestCoverageBitmaps` and `LastResults`; `toLiveTestState` rebuilt
/// from `LiveTestState.empty`, whose `FlakyHistory = Map.empty`. Every
/// daemon restart silently reset test flakiness to zero samples: with an
/// empty history, `FlakyDetection.assessTest` always returns `Insufficient`,
/// so no test is ever demoted or quarantined until its sample window
/// refills from scratch — after every single restart.
///
/// The fix adds a new FLKY section to the .sagetc binary format, alongside
/// the existing IMAP/TCOV/TRES sections, bound-checked against the
/// section's own declared size (never the whole stream) exactly like its
/// neighbours, and optional — an absent FLKY section (every cache written
/// before this fix) decodes to an empty flaky history, not an error.
module SageFs.Tests.FlakyPersistenceTests

open System
open System.IO
open Expecto
open Expecto.Flip
open FsCheck
open FsCheck.FSharp
open SageFs
open SageFs.Features
open SageFs.Features.TestCacheTypes
open SageFs.Features.LiveTesting

let private windowOf (outcomes: TestOutcome list) : ResultWindow =
  outcomes
  |> List.fold (fun w o -> ResultWindow.add o w) (ResultWindow.create FlakyDefaults.windowSize)

// ─── FsCheck generators ────────────────────────────────────────────

let private genTestOutcome = Gen.elements [ TestOutcome.Pass; TestOutcome.Fail ]

/// A ResultWindow of an arbitrary window size, filled past capacity often
/// enough to exercise wraparound (WriteIndex resetting to 0, older samples
/// overwritten) as well as partially-filled windows.
let private genResultWindow : Gen<ResultWindow> =
  gen {
    let! windowSize = Gen.choose (1, 12)
    let! sampleCount = Gen.choose (0, windowSize * 2)
    let! outcomes = Gen.listOfLength sampleCount genTestOutcome
    return outcomes |> List.fold (fun w o -> ResultWindow.add o w) (ResultWindow.create windowSize)
  }

/// A handful of distinct tests, each with its own independently-generated
/// sample window — including the empty-map case (n = 0).
let private genFlakyHistory : Gen<Map<TestId, ResultWindow>> =
  gen {
    let! n = Gen.choose (0, 6)
    let ids = [ for i in 1 .. n -> TestId.TestId (sprintf "flaky_test_%d" i) ]
    let! windows = Gen.listOfLength n genResultWindow
    return List.zip ids windows |> Map.ofList
  }

let private fsCheckConfig = { FsCheckConfig.defaultConfig with maxTest = 100 }

[<Tests>]
let flakyPersistenceTests = testList "FlakyHistory persistence" [

  // ── The RED test from the brief ──────────────────────────────────

  testCase "FlakyHistory survives a .sagetc round-trip" <| fun _ ->
    let window =
      windowOf [ TestOutcome.Pass; TestOutcome.Fail; TestOutcome.Pass; TestOutcome.Fail; TestOutcome.Pass ]
    let original =
      { LiveTestState.empty with
          FlakyHistory = Map.ofList [ TestId.TestId "flaky.test.a", window ] }
    let roundTripped =
      original
      |> TestCacheMapping.fromLiveTestState
      |> TestCacheMapping.toLiveTestState
    roundTripped.FlakyHistory
    |> Expect.equal "flake history must survive persistence" original.FlakyHistory

  // ── The same guarantee through the actual bytes on disk, not just the
  //    in-memory LiveTestState <-> StcData mapping ────────────────────

  testCase "FlakyHistory survives the full binary write-then-read, not just the in-memory mapping" <| fun _ ->
    let window = windowOf [ TestOutcome.Fail; TestOutcome.Fail; TestOutcome.Pass ]
    let original =
      { LiveTestState.empty with
          FlakyHistory = Map.ofList [ TestId.TestId "flaky.test.b", window ] }
    let stcData = TestCacheMapping.fromLiveTestState original
    let bytes = TestCacheWriter.write stcData
    match TestCacheReader.read bytes with
    | Error e -> failwithf "expected a successful read, got Error %s" e
    | Ok roundTripped ->
      let restored = TestCacheMapping.toLiveTestState roundTripped
      restored.FlakyHistory
      |> Expect.equal "flake history must survive the byte-level round trip" original.FlakyHistory

  testCase "a wrapped-around window (WriteIndex has cycled) round-trips exactly, not just its Count" <| fun _ ->
    // 7 samples through a window of size 3 forces two full wraps — this is
    // the shape a lossy encoding (writing only Count outcome bytes instead
    // of every WindowSize slot) would get wrong.
    let window =
      windowOf [ TestOutcome.Pass; TestOutcome.Fail; TestOutcome.Pass
                 TestOutcome.Fail; TestOutcome.Fail; TestOutcome.Pass; TestOutcome.Fail ]
    let original =
      { LiveTestState.empty with
          FlakyHistory = Map.ofList [ TestId.TestId "flaky.test.wrap", window ] }
    let bytes = TestCacheWriter.write (TestCacheMapping.fromLiveTestState original)
    match TestCacheReader.read bytes with
    | Error e -> failwithf "expected a successful read, got Error %s" e
    | Ok data ->
      let restored = TestCacheMapping.toLiveTestState data
      restored.FlakyHistory
      |> Expect.equal "the wrapped-around window's exact Outcomes/WriteIndex/Count must survive" original.FlakyHistory

  // ── Backward and forward compatibility ───────────────────────────

  testCase "empty FlakyHistory round-trips to an empty section, not an error" <| fun _ ->
    let bytes = TestCacheWriter.write StcData.empty
    match TestCacheReader.read bytes with
    | Error e -> failwithf "empty StcData must be writable and readable, got Error %s" e
    | Ok data -> data.FlakyEntries |> Expect.equal "no flaky entries" []

  testCase "a pre-fix v2 cache with no FLKY section at all loads with an empty flaky history, not an error" <| fun _ ->
    // Hand-construct exactly what every daemon wrote before this fix: a
    // 3-section STC1 file (IMAP, TCOV, TRES) with no FLKY directory entry
    // or payload whatsoever. The current reader must still accept it.
    let imapPayload = [| 0uy; 0uy; 0uy; 0uy |] // count = 0
    let tcovPayload = [| 0uy; 0uy; 0uy; 0uy |]
    let tresPayload = [| 0uy; 0uy; 0uy; 0uy |]
    let headerSize = 64
    let dirSize = 3 * 16
    let imapOffset = uint64 (headerSize + dirSize)
    let tcovOffset = imapOffset + uint64 imapPayload.Length
    let tresOffset = tcovOffset + uint64 tcovPayload.Length
    let totalSize = tresOffset + uint64 tresPayload.Length
    let imapCrc = Crc32.computeAll imapPayload
    let tcovCrc = Crc32.computeAll tcovPayload
    let tresCrc = Crc32.computeAll tresPayload
    use ms = new MemoryStream()
    use bw = new BinaryWriter(ms)
    bw.Write([| 0x53uy; 0x54uy; 0x43uy; 0x31uy |]) // "STC1"
    bw.Write(2us)                                  // format_version (pre-FLKY)
    bw.Write(1us)                                  // min_reader_version
    bw.Write(3u)                                   // section_count — no FLKY
    bw.Write(0u)                                   // flags
    bw.Write(0L)                                   // created_at_ms
    bw.Write(totalSize)                            // total_file_size
    bw.Write(0u)                                   // test_count
    bw.Write(0u)                                   // header_crc placeholder @36
    bw.Write(0u)                                   // imap_generation
    bw.Write(Array.zeroCreate<byte> 20)            // reserved to 64
    bw.Write(0x494D4150u); bw.Write(imapOffset); bw.Write(imapCrc) // IMAP
    bw.Write(0x54434F56u); bw.Write(tcovOffset); bw.Write(tcovCrc) // TCOV
    bw.Write(0x54524553u); bw.Write(tresOffset); bw.Write(tresCrc) // TRES
    bw.Write(imapPayload)
    bw.Write(tcovPayload)
    bw.Write(tresPayload)
    bw.Flush()
    let bytes = ms.ToArray()
    let forCrc = Array.copy bytes
    forCrc.[36] <- 0uy; forCrc.[37] <- 0uy; forCrc.[38] <- 0uy; forCrc.[39] <- 0uy
    let hcrc = Crc32.computeAll forCrc
    Array.Copy(BitConverter.GetBytes(hcrc), 0, bytes, 36, 4)
    match TestCacheReader.read bytes with
    | Error e -> failwithf "a pre-fix v2 cache (no FLKY section) must still load, got Error %s" e
    | Ok data -> data.FlakyEntries |> Expect.equal "absent FLKY decodes to empty flaky history" []

  // ── Corruption must degrade to a Result, never crash the daemon ─────

  testCase "a corrupted FLKY section fails the whole read, exactly like any other section" <| fun _ ->
    let window = windowOf [ TestOutcome.Pass; TestOutcome.Fail ]
    let state =
      { LiveTestState.empty with
          FlakyHistory = Map.ofList [ TestId.TestId "flaky.test.c", window ] }
    let bytes = TestCacheWriter.write (TestCacheMapping.fromLiveTestState state)
    // FLKY is directory entry index 3 (see TestCacheWriter.write's layout note).
    let flkyOffset = BitConverter.ToUInt64(bytes, 64 + 3 * 16 + 4) |> int
    let corrupted = Array.copy bytes
    corrupted.[flkyOffset] <- corrupted.[flkyOffset] ^^^ 0xFFuy
    match TestCacheReader.read corrupted with
    | Error _ -> ()
    | Ok _ -> failwith "a corrupted FLKY section must fail the read, not silently accept a tampered file"

  // ── Property-based coverage ─────────────────────────────────────

  testPropertyWithConfig fsCheckConfig "FlakyHistory round-trips through fromLiveTestState/toLiveTestState for any window shape" <|
    Prop.forAll (Arb.fromGen genFlakyHistory) (fun history ->
      let state = { LiveTestState.empty with FlakyHistory = history }
      let restored = state |> TestCacheMapping.fromLiveTestState |> TestCacheMapping.toLiveTestState
      restored.FlakyHistory = history)

  testPropertyWithConfig fsCheckConfig "FlakyHistory round-trips through the full binary write/read for any window shape" <|
    Prop.forAll (Arb.fromGen genFlakyHistory) (fun history ->
      let state = { LiveTestState.empty with FlakyHistory = history }
      let bytes = TestCacheWriter.write (TestCacheMapping.fromLiveTestState state)
      match TestCacheReader.read bytes with
      | Ok data -> (TestCacheMapping.toLiveTestState data).FlakyHistory = history
      | Error _ -> false)

  testPropertyWithConfig fsCheckConfig "reading a cache truncated at any point degrades to a Result, never an exception" <|
    Prop.forAll (Arb.fromGen genFlakyHistory) (fun history ->
      let state = { LiveTestState.empty with FlakyHistory = history }
      let bytes = TestCacheWriter.write (TestCacheMapping.fromLiveTestState state)
      [ 64 .. 7 .. bytes.Length ]
      |> List.forall (fun cut ->
        try TestCacheReader.read bytes.[0 .. cut - 1] |> ignore; true
        with _ -> false))
]
