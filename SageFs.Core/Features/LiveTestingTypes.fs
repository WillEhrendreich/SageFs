namespace SageFs.Features.LiveTesting

open System
open System.IO
open System.Reflection
open System.Security.Cryptography
open System.Text
open System.Text.RegularExpressions
open SageFs.Measures

// --- Assembly Load Diagnostics ---

/// Errors that can occur when loading assemblies for test discovery.
[<RequireQualifiedAccess>]
type AssemblyLoadError =
  | FileNotFound of path: string * message: string
  | LoadFailed of path: string * message: string
  | BadImage of path: string * message: string

module AssemblyLoadError =
  let path (e: AssemblyLoadError) =
    match e with
    | AssemblyLoadError.FileNotFound (p, _)
    | AssemblyLoadError.LoadFailed (p, _)
    | AssemblyLoadError.BadImage (p, _) -> p

  let message (e: AssemblyLoadError) =
    match e with
    | AssemblyLoadError.FileNotFound (_, m)
    | AssemblyLoadError.LoadFailed (_, m)
    | AssemblyLoadError.BadImage (_, m) -> m

  let describe (e: AssemblyLoadError) =
    match e with
    | AssemblyLoadError.FileNotFound (p, m) -> sprintf "Assembly not found: %s (%s)" p m
    | AssemblyLoadError.LoadFailed (p, m) -> sprintf "Assembly load failed: %s (%s)" p m
    | AssemblyLoadError.BadImage (p, m) -> sprintf "Bad image format: %s (%s)" p m

  /// Load an assembly from disk, returning a typed error on failure.
  let loadAssembly (path: string) : Result<Assembly, AssemblyLoadError> =
    try
      Ok(Assembly.LoadFrom(path))
    with
    | :? FileNotFoundException as ex ->
      Error(AssemblyLoadError.FileNotFound(path, ex.Message))
    | :? FileLoadException as ex ->
      Error(AssemblyLoadError.LoadFailed(path, ex.Message))
    | :? BadImageFormatException as ex ->
      Error(AssemblyLoadError.BadImage(path, ex.Message))

// --- Test Framework ---

[<RequireQualifiedAccess>]
type TestFramework =
  | Expecto
  | XUnit
  | NUnit
  | MSTest
  | TUnit
  | Unknown of string

module TestFramework =
  let toString = function
    | TestFramework.Expecto -> "expecto"
    | TestFramework.XUnit -> "xunit"
    | TestFramework.NUnit -> "nunit"
    | TestFramework.MSTest -> "mstest"
    | TestFramework.TUnit -> "tunit"
    | TestFramework.Unknown s -> s

  let parse = function
    | "expecto" -> TestFramework.Expecto
    | "xunit" -> TestFramework.XUnit
    | "nunit" -> TestFramework.NUnit
    | "mstest" -> TestFramework.MSTest
    | "tunit" -> TestFramework.TUnit
    | s -> TestFramework.Unknown s

/// Configuration constants for the live testing cycle.
[<RequireQualifiedAccess>]
module LiveTestingDefaults =
  /// Default test module identifier for detecting test functions by namespace.
  let [<Literal>] TestModuleIdentifier = ".Tests."
  /// Default framework string for Expecto-based projects.
  let Framework = TestFramework.Expecto

// --- Stable Test Identity ---

[<Struct; RequireQualifiedAccess>]
type TestId = TestId of string

module TestId =
  /// 16 hex chars = 64 bits of entropy from SHA256. Collision probability
  /// is negligible for projects with < 10^9 tests (birthday bound ~2^32).
  let create (fullName: string) (framework: TestFramework) =
    let input = sprintf "%s|%s" fullName (TestFramework.toString framework)
    let bytes = Encoding.UTF8.GetBytes(input)
    let hash = SHA256.HashData(bytes)
    TestId.TestId(Convert.ToHexString(hash).Substring(0, 16))

  let value (TestId.TestId id) = id

// --- Test Categories & Run Policies ---

[<RequireQualifiedAccess>]
type TestCategory =
  | Unit
  | Integration
  | Browser
  | Benchmark
  | Architecture
  | Property
  | Custom of string

[<RequireQualifiedAccess>]
type RunPolicy =
  | OnEveryChange
  | OnSaveOnly
  | OnDemand
  | Disabled

module RunPolicyDefaults =
  let defaults =
    Map.ofList [
      TestCategory.Unit, RunPolicy.OnEveryChange
      TestCategory.Integration, RunPolicy.OnDemand
      TestCategory.Browser, RunPolicy.OnDemand
      TestCategory.Benchmark, RunPolicy.OnDemand
      TestCategory.Architecture, RunPolicy.OnSaveOnly
      TestCategory.Property, RunPolicy.OnEveryChange
    ]

[<RequireQualifiedAccess>]
type RunTrigger =
  | Keystroke
  | FileSave
  | ExplicitRun

// --- Assembly Info ---

type AssemblyInfo = {
  Name: string
  Location: string
  ReferencedAssemblies: AssemblyName array
}

// --- Source-Level Detection ---

[<Struct>]
type SourceTestLocation = {
  AttributeName: string
  FunctionName: string
  FilePath: string
  Line: int
  Column: int
}

// --- Test Origin ---

[<RequireQualifiedAccess>]
type TestOrigin =
  | SourceMapped of file: string * line: int
  | ReflectionOnly

// --- Test Case ---

type TestCase = {
  Id: TestId
  FullName: string
  DisplayName: string
  Origin: TestOrigin
  Labels: string list
  Framework: TestFramework
  Category: TestCategory
}

// --- Test Failure & Results ---

[<RequireQualifiedAccess>]
type TestFailure =
  | AssertionFailed of message: string
  | ExceptionThrown of message: string * stackTrace: string
  | TimedOut of after: TimeSpan

[<RequireQualifiedAccess>]
type TestResult =
  | Passed of duration: TimeSpan
  | Failed of failure: TestFailure * duration: TimeSpan
  | Skipped of reason: string
  | NotRun

type TestRunResult = {
  TestId: TestId
  TestName: string
  Result: TestResult
  Timestamp: DateTimeOffset
  /// Captured console output (stdout) from the test execution.
  Output: string option
}

// --- Run History ---

[<RequireQualifiedAccess>]
type RunHistory =
  | NeverRun
  | PreviousRun of duration: TimeSpan

// --- Test Run Status (UI lifecycle) ---

[<RequireQualifiedAccess>]
type TestRunStatus =
  | Detected
  | Queued
  | Running
  | Passed of duration: TimeSpan
  | Failed of failure: TestFailure * duration: TimeSpan
  | Skipped of reason: string
  | Stale
  | PolicyDisabled

type TestStatusEntry = {
  TestId: TestId
  DisplayName: string
  FullName: string
  Origin: TestOrigin
  Framework: TestFramework
  Category: TestCategory
  CurrentPolicy: RunPolicy
  Status: TestRunStatus
  PreviousStatus: TestRunStatus
}

[<RequireQualifiedAccess>]
type StatusEntriesProjectionState =
  | Materialized
  | Deferred

// --- Provider Descriptions (pure data — stored in Elm model) ---

type AttributeProviderDescription = {
  Name: TestFramework
  TestAttributes: string list
  AssemblyMarker: string
}

type CustomProviderDescription = {
  Name: TestFramework
  AssemblyMarker: string
}

[<RequireQualifiedAccess>]
type ProviderDescription =
  | AttributeBased of AttributeProviderDescription
  | Custom of CustomProviderDescription

// --- FCS Symbol Extraction (IO boundary) ---

/// How a symbol appears in source — definition site or reference site.
[<RequireQualifiedAccess>]
type SymbolUseKind =
  | Definition
  | Reference

/// Extracted symbol use — pure data, no FCS dependency.
/// This is the bridge between FCS (IO) and the pure dependency graph builder.
type ExtractedSymbolUse = {
  FullName: string
  DisplayName: string
  UseKind: SymbolUseKind
  StartLine: int
  EndLine: int
}

// --- Dependency Graph ---

type TestDependencyGraph = {
  SymbolToTests: Map<string, TestId array>
  TransitiveCoverage: Map<string, TestId array>
  PerFileIndex: Map<string, Map<string, TestId array>>
  SourceVersion: int
}

// --- Coverage ---

/// Whether all tests covering a symbol are passing.
[<RequireQualifiedAccess>]
type CoverageHealth =
  | AllPassing
  | SomeFailing

[<RequireQualifiedAccess>]
type CoverageStatus =
  | Covered of testCount: int * health: CoverageHealth
  | NotCovered
  | Pending

[<RequireQualifiedAccess>]
type BranchCoverage =
  | FullyCovered
  | PartiallyCovered of covered: int * total: int
  | NotCovered
  | Unknown

type CoverageAnnotation = {
  Symbol: string
  FilePath: string
  DefinitionLine: int
  Status: CoverageStatus
  BranchCoverage: BranchCoverage
}

// --- IL Branch Coverage ---

[<RequireQualifiedAccess>]
type LineCoverage =
  | FullyCovered
  | PartiallyCovered of covered: int * total: int
  | NotCovered

type SequencePoint = {
  File: string
  Line: int
  Column: int
  EndLine: int
  EndColumn: int
  BranchId: int
}

module SequencePoint =
  /// Check if the sequence point has valid range data (non-degenerate).
  let hasRange (sp: SequencePoint) =
    sp.EndLine > 0 && (sp.EndLine > sp.Line || (sp.EndLine = sp.Line && sp.EndColumn > sp.Column))

type CoverageState = {
  Slots: SequencePoint array
  Hits: bool array
}

/// Maps instrumented sequence point slots to source locations.
/// Created once per assembly instrumentation, reused across test runs.
type InstrumentationMap = {
  Slots: SequencePoint array
  TotalProbes: int
  TrackerTypeName: string
  HitsFieldName: string
}

module InstrumentationMap =
  let empty =
    { Slots = [||]
      TotalProbes = 0
      TrackerTypeName = "__SageFsCoverage"
      HitsFieldName = "Hits" }

  /// Convert raw hit data + instrumentation map → CoverageState.
  let toCoverageState (hits: bool array) (map: InstrumentationMap) : CoverageState =
    match hits.Length <> map.TotalProbes with
    | true -> { Slots = [||]; Hits = [||] }
    | false -> { Slots = map.Slots; Hits = hits }

  /// Merge multiple maps into one (concatenates slots).
  /// Worker collects concatenated hits across all assemblies,
  /// so the merged map's slot order must match.
  let merge (maps: InstrumentationMap array) : InstrumentationMap =
    match maps.Length with
    | 0 -> empty
    | 1 -> maps.[0]
    | _ ->
      let allSlots = maps |> Array.collect (fun m -> m.Slots)
      { Slots = allSlots
        TotalProbes = allSlots.Length
        TrackerTypeName = "__SageFsCoverage"
        HitsFieldName = "Hits" }

/// Pure functions for computing line-level coverage from IL probe data.
module ILCoverage =
  /// Group sequence point hits by (file, line) → per-line coverage status.
  let computeLineCoverage (state: CoverageState) : Map<string, Map<int, LineCoverage>> =
    match state.Slots.Length = 0 || state.Slots.Length <> state.Hits.Length with
    | true -> Map.empty
    | false ->
      state.Slots
      |> Array.mapi (fun i sp -> sp, state.Hits.[i])
      |> Array.groupBy (fun (sp, _) -> sp.File)
      |> Array.map (fun (file, points) ->
        let lineMap =
          points
          |> Array.groupBy (fun (sp, _) -> sp.Line)
          |> Array.map (fun (line, linePoints) ->
            let total = linePoints.Length
            let covered = linePoints |> Array.filter snd |> Array.length
            let status =
              match covered = total, covered > 0 with
              | true, _ -> LineCoverage.FullyCovered
              | false, true -> LineCoverage.PartiallyCovered(covered, total)
              | false, false -> LineCoverage.NotCovered
            line, status)
          |> Map.ofArray
        file, lineMap)
      |> Map.ofArray

  /// Get coverage for a specific file.
  let forFile (filePath: string) (coverage: Map<string, Map<int, LineCoverage>>) : (int * LineCoverage) array =
    match Map.tryFind filePath coverage with
    | None -> [||]
    | Some lineMap -> lineMap |> Map.toArray

/// Packed bit-vector representation of coverage data.
/// Uses uint64[] instead of bool[] for 8× memory reduction and SIMD-friendly comparison.
/// Designed for efficient equivalence checks in many-worlds mutation testing (Molina).
type CoverageBitmap = {
  Bits: uint64 array
  Count: int
}

module CoverageBitmap =
  let inline private wordsNeeded count = (count + 63) / 64

  let empty = { Bits = [||]; Count = 0 }

  /// Pack a bool array into a CoverageBitmap.
  let ofBoolArray (hits: bool array) : CoverageBitmap =
    let count = hits.Length
    match count = 0 with
    | true -> empty
    | false ->
      let words = wordsNeeded count
      let bits = Array.zeroCreate<uint64> words
      for i in 0 .. count - 1 do
        match hits.[i] with
        | true ->
          let word = i / 64
          let bit = i % 64
          bits.[word] <- bits.[word] ||| (1UL <<< bit)
        | false -> ()
      { Bits = bits; Count = count }

  /// Unpack a CoverageBitmap back to a bool array.
  let toBoolArray (bm: CoverageBitmap) : bool array =
    match bm.Count = 0 with
    | true -> [||]
    | false ->
      let result = Array.zeroCreate<bool> bm.Count
      for i in 0 .. bm.Count - 1 do
        let word = i / 64
        let bit = i % 64
        result.[i] <- (bm.Bits.[word] &&& (1UL <<< bit)) <> 0UL
      result

  /// Check if two bitmaps have identical coverage (same size + same bits).
  /// JIT auto-vectorizes this comparison loop.
  let equivalent (a: CoverageBitmap) (b: CoverageBitmap) : bool =
    match a.Count <> b.Count with
    | true -> false
    | false ->
      let mutable eq = true
      let mutable i = 0
      while eq && i < a.Bits.Length do
        eq <- a.Bits.[i] = b.Bits.[i]
        i <- i + 1
      eq

  /// Count number of set bits (covered probes).
  let popCount (bm: CoverageBitmap) : int =
    let mutable total = 0
    for w in bm.Bits do
      total <- total + (System.Numerics.BitOperations.PopCount(w) |> int)
    total

  /// Bitwise AND — intersection of two coverage bitmaps.
  let intersect (a: CoverageBitmap) (b: CoverageBitmap) : CoverageBitmap =
    match a.Count <> b.Count with
    | true -> failwithf "CoverageBitmap.intersect size mismatch: a.Count=%d, b.Count=%d" a.Count b.Count
    | false -> ()
    let bits = Array.init a.Bits.Length (fun i -> a.Bits.[i] &&& b.Bits.[i])
    { Bits = bits; Count = a.Count }

  /// Bitwise XOR — symmetric difference of two coverage bitmaps.
  let xorDiff (a: CoverageBitmap) (b: CoverageBitmap) : CoverageBitmap =
    match a.Count <> b.Count with
    | true -> failwithf "CoverageBitmap.xorDiff size mismatch: a.Count=%d, b.Count=%d" a.Count b.Count
    | false -> ()
    let bits = Array.init a.Bits.Length (fun i -> a.Bits.[i] ^^^ b.Bits.[i])
    { Bits = bits; Count = a.Count }

  /// Bitwise OR — union of two coverage bitmaps.
  let union (a: CoverageBitmap) (b: CoverageBitmap) : CoverageBitmap =
    match a.Count = 0 && b.Count = 0, a.Count <> b.Count with
    | true, _ -> empty
    | _, true -> failwithf "CoverageBitmap.union size mismatch: a.Count=%d, b.Count=%d" a.Count b.Count
    | false, false ->
      let bits = Array.init a.Bits.Length (fun i -> a.Bits.[i] ||| b.Bits.[i])
      { Bits = bits; Count = a.Count }

  /// Check if a specific probe index is set.
  let isSet (index: int) (bm: CoverageBitmap) : bool =
    match index < 0 || index >= bm.Count with
    | true -> false
    | false ->
      let word = index / 64
      let bit = index % 64
      (bm.Bits.[word] &&& (1UL <<< bit)) <> 0UL

  /// Build a bitmap mask with bits set for all probes in the given file.
  let buildFileMask (filePath: string) (maps: InstrumentationMap array) : CoverageBitmap =
    let merged = InstrumentationMap.merge maps
    match merged.TotalProbes = 0 with
    | true -> empty
    | false ->
      let hits = Array.zeroCreate<bool> merged.TotalProbes
      for i in 0 .. merged.Slots.Length - 1 do
        match merged.Slots.[i].File = filePath with
        | true -> hits.[i] <- true
        | false -> ()
      ofBoolArray hits

  /// Find tests whose coverage bitmaps intersect with probes in the changed file.
  /// Returns test IDs that have at least one probe hit in the file, based on stored bitmaps.
  /// Skips tests with mismatched bitmap sizes (stale instrumentation generation).
  let findCoverageAffected
    (filePath: string)
    (maps: InstrumentationMap array)
    (bitmaps: Map<TestId, CoverageBitmap>)
    : TestId array =
    let mask = buildFileMask filePath maps
    match mask.Count = 0 || popCount mask = 0 with
    | true -> [||]
    | false ->
      bitmaps
      |> Map.toArray
      |> Array.choose (fun (tid, bm) ->
        match bm.Count <> mask.Count with
        | true -> None
        | false ->
          let intersection = intersect bm mask
          match popCount intersection > 0 with
          | true -> Some tid
          | false -> None)

  /// Merge all test bitmaps via OR, compute LineCoverage per line for a file.
  let computeLineCoverageForFile
    (filePath: string)
    (maps: InstrumentationMap array)
    (bitmaps: Map<TestId, CoverageBitmap>)
    : Map<int, LineCoverage> =
    let merged = InstrumentationMap.merge maps
    match merged.TotalProbes = 0 || merged.Slots.Length = 0 with
    | true -> Map.empty
    | false ->
      let compatBitmaps =
        bitmaps |> Map.values |> Seq.filter (fun bm -> bm.Count = merged.TotalProbes) |> Seq.toArray
      match compatBitmaps.Length = 0 with
      | true -> Map.empty
      | false ->
        let combined =
          compatBitmaps
          |> Array.reduce (fun acc bm ->
            let bits = Array.init acc.Bits.Length (fun i -> acc.Bits.[i] ||| bm.Bits.[i])
            { Bits = bits; Count = acc.Count })
        let hits = toBoolArray combined
        let state = InstrumentationMap.toCoverageState hits merged
        let allLineCov = ILCoverage.computeLineCoverage state
        allLineCov |> Map.tryFind filePath |> Option.defaultValue Map.empty

  /// Compute per-test coverage weights for a set of changed files.
  /// Weight = popCount(intersect(testBitmap, fileMask)) — how many probes the test
  /// hits in the changed file(s). Uses file-level granularity (not line-level).
  let computeCoverageWeights
    (changedFiles: string list)
    (maps: InstrumentationMap array)
    (bitmaps: Map<TestId, CoverageBitmap>)
    : Map<TestId, int> =
    match changedFiles with
    | [] -> Map.empty
    | _ ->
      let fileMasks =
        changedFiles
        |> List.map (fun fp -> buildFileMask fp maps)
        |> List.filter (fun m -> m.Count > 0 && popCount m > 0)
      match fileMasks with
      | [] -> Map.empty
      | _ ->
        bitmaps
        |> Map.map (fun _ bm ->
          fileMasks
          |> List.sumBy (fun mask ->
            match bm.Count = mask.Count with
            | true -> popCount (intersect bm mask)
            | false -> 0))
        |> Map.filter (fun _ weight -> weight > 0)

/// Per-test coverage info for a specific symbol
type CoveringTestInfo = {
  TestId: TestId
  DisplayName: string
  Result: TestResult option
}

/// Full coverage detail for a symbol — includes which specific tests cover it
[<RequireQualifiedAccess>]
type CoverageDetail =
  | NotCovered
  | Pending
  | Covered of tests: CoveringTestInfo array

// --- Test Cycle Timing ---

[<RequireQualifiedAccess>]
type TestCycleDepth =
  | TreeSitterOnly of treeSitter: TimeSpan
  | ThroughFcs of treeSitter: TimeSpan * fcs: TimeSpan
  | ThroughExecution of treeSitter: TimeSpan * fcs: TimeSpan * execution: TimeSpan

type TestCycleTiming = {
  Depth: TestCycleDepth
  TotalTests: int
  AffectedTests: int
  Trigger: RunTrigger
  Timestamp: DateTimeOffset
}

// --- Gutter Rendering ---

[<RequireQualifiedAccess>]
type GutterIcon =
  | TestDiscovered
  | TestPassed
  | TestFailed
  | TestRunning
  | TestSkipped
  | TestFlaky
  | Covered
  | NotCovered
  | CellStale
  | BranchFullyCovered
  | BranchPartiallyCovered
  | BranchNotCovered

[<Struct>]
type LineAnnotation = {
  Line: int
  Icon: GutterIcon
  Tooltip: string
}

/// Source location of a test within a source file and REPL cell.
/// Used by editor integrations to navigate to test definitions.
type TestSourceLocation = {
  CellId:    int
  TestName:  string
  FilePath:  string
  StartLine: int
  EndLine:   int
}

// --- Test Summary ---

type TestSummary = {
  Total: int
  Passed: int
  Failed: int
  Stale: int
  Running: int
  Disabled: int
  Enabled: bool
}

// --- Run Generation & Phase (replaces IsRunning: bool) ---

[<Struct>]
type RunGeneration = RunGeneration of int

module RunGeneration =
  let zero = RunGeneration 0
  let next (RunGeneration n) = RunGeneration (n + 1)
  let value (RunGeneration n) = n

[<Struct>]
type TestRunPhase =
  | Idle
  | Running of generation: RunGeneration
  | RunningButEdited of generation: RunGeneration

/// Why did results arrive the way they did?
[<Struct>]
type ResultFreshness =
  | Fresh
  | StaleCodeEdited
  | StaleWrongGeneration

module TestRunPhase =
  let startRun (currentGen: RunGeneration) : TestRunPhase * RunGeneration =
    let gen = RunGeneration.next currentGen
    Running gen, gen

  let onEdit (phase: TestRunPhase) : TestRunPhase =
    match phase with
    | Idle -> Idle
    | Running gen -> RunningButEdited gen
    | RunningButEdited gen -> RunningButEdited gen

  let onResultsArrived (resultGen: RunGeneration) (phase: TestRunPhase) : TestRunPhase * ResultFreshness =
    match phase with
    | Idle -> Idle, Fresh
    | Running gen when resultGen = gen -> Idle, Fresh
    | Running _ -> Idle, StaleWrongGeneration
    | RunningButEdited gen when resultGen = gen -> Idle, StaleCodeEdited
    | RunningButEdited _ -> Idle, StaleWrongGeneration

  let isRunning (phase: TestRunPhase) : bool =
    match phase with
    | Idle -> false
    | Running _ | RunningButEdited _ -> true

  /// Check if a specific session is running in the per-session RunPhases map.
  let isSessionRunning (sessionId: string option) (phases: Map<string, TestRunPhase>) : bool =
    match sessionId with
    | Some sid ->
      phases |> Map.tryFind sid
      |> Option.map isRunning
      |> Option.defaultValue false
    | None -> phases |> Map.exists (fun _ p -> isRunning p)

  /// Check if any session is running.
  let isAnyRunning (phases: Map<string, TestRunPhase>) : bool =
    phases |> Map.exists (fun _ p -> isRunning p)

  let currentGeneration (lastGen: RunGeneration) (phase: TestRunPhase) : RunGeneration =
    match phase with
    | Idle -> lastGen
    | Running gen | RunningButEdited gen -> gen

// --- Live Test State (Elm model aggregate) ---

/// Whether live testing is active or inactive.
[<RequireQualifiedAccess>]
type LiveTestingActivation =
  | Active
  | Inactive

/// Whether coverage gutter annotations are shown or hidden.
[<RequireQualifiedAccess>]
type CoverageVisibility =
  | Shown
  | Hidden

// --- Flaky Test Detection ---

/// Binary outcome for flaky detection (simpler than full TestResult).
[<Struct; RequireQualifiedAccess>]
type TestOutcome = Pass | Fail

/// Fixed-size circular buffer of recent test outcomes.
type ResultWindow = {
  Outcomes: TestOutcome array
  WriteIndex: int
  Count: int
  WindowSize: int
}

module ResultWindow =
  let create windowSize = {
    Outcomes = Array.create windowSize TestOutcome.Pass
    WriteIndex = 0
    Count = 0
    WindowSize = windowSize
  }

  let add (outcome: TestOutcome) (w: ResultWindow) =
    let outcomes = Array.copy w.Outcomes
    outcomes[w.WriteIndex] <- outcome
    { w with
        Outcomes = outcomes
        WriteIndex = (w.WriteIndex + 1) % w.WindowSize
        Count = min (w.Count + 1) w.WindowSize }

  let toList (w: ResultWindow) =
    match w.Count = 0, w.Count < w.WindowSize with
    | true, _ -> []
    | false, true ->
      Array.toList w.Outcomes[0 .. w.Count - 1]
    | false, false ->
      let start = w.WriteIndex
      [ for i in 0 .. w.WindowSize - 1 do
          yield w.Outcomes[(start + i) % w.WindowSize] ]

  let countFlips (w: ResultWindow) =
    let items = toList w
    match items with
    | [] | [_] -> 0
    | _ ->
      items
      |> List.pairwise
      |> List.sumBy (fun (a, b) -> match a <> b with | true -> 1 | false -> 0)

/// Stability assessment for a test based on outcome history.
[<RequireQualifiedAccess>]
type TestStability =
  | Insufficient
  | Stable
  | Flaky of flipCount: int

module TestStability =
  let assess (minSamples: int) (flipThreshold: int) (w: ResultWindow) =
    match w.Count < minSamples with
    | true -> TestStability.Insufficient
    | false ->
      let flips = ResultWindow.countFlips w
      match flips >= flipThreshold with
      | true -> TestStability.Flaky flips
      | false -> TestStability.Stable

module FlakyDefaults =
  let windowSize = 10
  let flipThreshold = 2
  let minSamples = 3

/// Distinguishes WHY a test is flaky. FsCheck property tests that intermittently
/// find counterexamples are NOT "flaky" — they found real bugs via random testing.
[<RequireQualifiedAccess>]
type FlakyClassification =
  /// Not enough samples to classify
  | Insufficient
  /// Deterministic — passes or fails consistently
  | Stable
  /// Environment-induced flakiness: timing, race conditions, resource contention
  | Environmental of flipCount: int
  /// FsCheck property test found intermittent counterexample — this is a real bug,
  /// not environmental flakiness. Carries the shrunk (or original) counterexample.
  | PropertyCounterexample of counterexample: string

module FlakyDetection =
  let private fsCheckClassifyPattern =
    Regex(
      @"Falsifiable.*?Original:\s*\n(.+?)(?:\nShrunk:\s*\n(.+?))?$",
      RegexOptions.Compiled ||| RegexOptions.Singleline)

  /// Extract FsCheck counterexample from a failure message.
  /// Returns the shrunk counterexample if present, otherwise the original.
  let isFsCheckFailure (msg: string) : string option =
    match System.String.IsNullOrWhiteSpace msg with
    | true -> None
    | false ->
      let m = fsCheckClassifyPattern.Match(msg)
      match m.Success with
      | true ->
        let original = m.Groups.[1].Value.Trim()
        match m.Groups.[2].Success with
        | true -> Some (m.Groups.[2].Value.Trim())
        | false -> Some original
      | false -> None

  let outcomeOf (result: TestResult) =
    match result with
    | TestResult.Passed _ -> TestOutcome.Pass
    | TestResult.Failed _ -> TestOutcome.Fail
    | TestResult.Skipped _ | TestResult.NotRun -> TestOutcome.Pass

  let recordResult
    (testId: TestId)
    (result: TestResult)
    (history: Map<TestId, ResultWindow>)
    : Map<TestId, ResultWindow> =
    let window =
      history
      |> Map.tryFind testId
      |> Option.defaultWith (fun () -> ResultWindow.create FlakyDefaults.windowSize)
    let updated = ResultWindow.add (outcomeOf result) window
    Map.add testId updated history

  let assessTest
    (testId: TestId)
    (history: Map<TestId, ResultWindow>)
    : TestStability =
    match Map.tryFind testId history with
    | None -> TestStability.Insufficient
    | Some w -> TestStability.assess FlakyDefaults.minSamples FlakyDefaults.flipThreshold w

  /// Classify flakiness with FsCheck awareness. Checks both flip history
  /// AND last failure message to distinguish environmental from property-based.
  let classifyFlakiness
    (testId: TestId)
    (flakyHistory: Map<TestId, ResultWindow>)
    (lastResults: Map<TestId, TestRunResult>)
    : FlakyClassification =
    match assessTest testId flakyHistory with
    | TestStability.Insufficient -> FlakyClassification.Insufficient
    | TestStability.Stable -> FlakyClassification.Stable
    | TestStability.Flaky flipCount ->
      // Flaky detected — check if last failure was FsCheck
      let lastFailureMsg =
        Map.tryFind testId lastResults
        |> Option.bind (fun r ->
          match r.Result with
          | TestResult.Failed (TestFailure.AssertionFailed msg, _) -> Some msg
          | TestResult.Failed (TestFailure.ExceptionThrown (msg, _), _) -> Some msg
          | _ -> None)
      match lastFailureMsg |> Option.bind isFsCheckFailure with
      | Some counterexample -> FlakyClassification.PropertyCounterexample counterexample
      | None -> FlakyClassification.Environmental flipCount

// ── Quarantine ────────────────────────────────────────────────────────
// Auto-quarantines environmentally flaky tests so they don't block
// the feedback loop. Manual quarantine is never auto-released.

/// Why a test was quarantined — environmental flakiness is auto-detected,
/// manual quarantine is human-initiated.
type QuarantineReason =
  | EnvironmentalFlaky of flipCount: int * detectedAt: DateTimeOffset
  | ManualQuarantine of reason: string * at: DateTimeOffset

/// Action to take after evaluating a test's flaky classification.
[<RequireQualifiedAccess>]
type QuarantineAction =
  | DoQuarantine of testId: TestId * reason: QuarantineReason
  | Release of testId: TestId
  | NoChange

module QuarantineLogic =
  /// Evaluate whether a test should be quarantined, released, or left alone.
  let evaluate
    (testId: TestId)
    (classification: FlakyClassification)
    (quarantined: Map<TestId, QuarantineReason>)
    (now: DateTimeOffset)
    : QuarantineAction =
    match classification, quarantined |> Map.tryFind testId with
    | FlakyClassification.Environmental flips, None ->
      QuarantineAction.DoQuarantine (testId, EnvironmentalFlaky (flips, now))
    | FlakyClassification.Environmental _, Some _ -> QuarantineAction.NoChange
    | FlakyClassification.Stable, Some (EnvironmentalFlaky _) -> QuarantineAction.Release testId
    | FlakyClassification.Stable, Some (ManualQuarantine _) -> QuarantineAction.NoChange
    | FlakyClassification.Stable, None -> QuarantineAction.NoChange
    | FlakyClassification.PropertyCounterexample _, _ -> QuarantineAction.NoChange
    | FlakyClassification.Insufficient, _ -> QuarantineAction.NoChange

  /// Apply a quarantine action to the quarantine map.
  let apply
    (action: QuarantineAction)
    (quarantined: Map<TestId, QuarantineReason>)
    : Map<TestId, QuarantineReason> =
    match action with
    | QuarantineAction.DoQuarantine (testId, reason) -> quarantined |> Map.add testId reason
    | QuarantineAction.Release testId -> quarantined |> Map.remove testId
    | QuarantineAction.NoChange -> quarantined

  /// Check if a test is currently quarantined.
  let isQuarantined
    (testId: TestId)
    (quarantined: Map<TestId, QuarantineReason>)
    : bool =
    quarantined |> Map.containsKey testId

  /// Filter out quarantined tests from an array.
  let filterQuarantined
    (quarantined: Map<TestId, QuarantineReason>)
    (tests: TestCase array)
    : TestCase array =
    tests |> Array.filter (fun tc -> quarantined |> Map.containsKey tc.Id |> not)

// ── Failure Narratives ─────────────────────────────────────────────────
// Enriches test failures with temporal context, causal analysis, and
// property violation details. Separate from TestRunStatus to avoid
// breaking ~100 pattern matches across the codebase.

/// What changed between the last pass and this failure.
[<RequireQualifiedAccess>]
type CausalChange =
  | SymbolChanged of symbolName: string
  | FileChanged of filePath: string
  | Unknown

/// Details about an algebraic property violation (FsCheck).
type PropertyViolationDetail = {
  PropertyName: string option
  ShrunkCounterexample: string
  AlgebraicCategory: string option
}

/// Contextual narrative enriching a test failure with
/// when it last passed, what changed, and property violation details.
type FailureNarrative = {
  LastPassedAt: DateTimeOffset option
  TimeSinceLastPass: TimeSpan option
  CausalChanges: CausalChange list
  PropertyViolation: PropertyViolationDetail option
  Summary: string
}

module FailureNarrative =
  let empty = {
    LastPassedAt = None
    TimeSinceLastPass = None
    CausalChanges = []
    PropertyViolation = None
    Summary = "Test failed"
  }

module FailureNarrativeBuilder =
  let private propertyNamePattern =
    Regex(@"Property:\s*(.+?)(?:\r?\n|$)", RegexOptions.Compiled)

  let extractPropertyName (failureMsg: string) : string option =
    match System.String.IsNullOrWhiteSpace failureMsg with
    | true -> None
    | false ->
      let m = propertyNamePattern.Match(failureMsg)
      match m.Success with
      | true -> Some (m.Groups.[1].Value.Trim())
      | false -> None

  let private algebraicKeywords =
    [ "associat", "associativity"
      "commutat", "commutativity"
      "identity", "identity"
      "idempoten", "idempotence"
      "distribut", "distributivity"
      "inverse", "inverse"
      "absorp", "absorption"
      "closure", "closure" ]

  let detectAlgebraicCategory (name: string) : string option =
    let lower = name.ToLowerInvariant()
    algebraicKeywords
    |> List.tryPick (fun (prefix, category) ->
      match lower.Contains(prefix) with
      | true -> Some category
      | false -> None)

  let buildPropertyViolation
    (failureMsg: string)
    (counterexample: string)
    : PropertyViolationDetail =
    let propName = extractPropertyName failureMsg
    let category = propName |> Option.bind detectAlgebraicCategory
    { PropertyName = propName
      ShrunkCounterexample = counterexample
      AlgebraicCategory = category }

  let buildNarrative
    (now: DateTimeOffset)
    (lastPassedResult: TestRunResult option)
    (changedSymbols: string list)
    (changedFiles: string list)
    (flakyClassification: FlakyClassification)
    (currentFailure: TestFailure)
    : FailureNarrative =
    let lastPassedAt = lastPassedResult |> Option.map (fun r -> r.Timestamp)
    let timeSinceLastPass = lastPassedAt |> Option.map (fun t -> now - t)

    let causalChanges =
      let symbolChanges = changedSymbols |> List.map CausalChange.SymbolChanged
      let fileChanges = changedFiles |> List.map CausalChange.FileChanged
      match symbolChanges @ fileChanges with
      | [] ->
        match lastPassedResult with
        | Some _ -> [ CausalChange.Unknown ]
        | None -> [ CausalChange.Unknown ]
      | changes -> changes

    let propertyViolation =
      match flakyClassification with
      | FlakyClassification.PropertyCounterexample counterexample ->
        let failureMsg =
          match currentFailure with
          | TestFailure.AssertionFailed msg -> msg
          | TestFailure.ExceptionThrown (msg, _) -> msg
          | TestFailure.TimedOut _ -> ""
        Some (buildPropertyViolation failureMsg counterexample)
      | _ -> None

    let timePart =
      match timeSinceLastPass with
      | Some ts when ts.TotalHours >= 24.0 ->
        sprintf "was passing %.0f days ago" (ts.TotalDays)
      | Some ts when ts.TotalHours >= 1.0 ->
        sprintf "was passing %.0f hours ago" (ts.TotalHours)
      | Some ts ->
        sprintf "was passing %.0f minutes ago" (ts.TotalMinutes)
      | None -> "never passed"

    let changePart =
      match causalChanges with
      | [ CausalChange.SymbolChanged name ] ->
        sprintf " — caused by change to '%s'" name
      | [ CausalChange.FileChanged path ] ->
        sprintf " — caused by change to '%s'" (System.IO.Path.GetFileName path)
      | changes when changes.Length > 1 ->
        let symbolCount = changes |> List.filter (function CausalChange.SymbolChanged _ -> true | _ -> false) |> List.length
        let fileCount = changes |> List.filter (function CausalChange.FileChanged _ -> true | _ -> false) |> List.length
        match symbolCount, fileCount with
        | s, 0 -> sprintf " — %d symbols changed" s
        | 0, f -> sprintf " — %d files changed" f
        | s, f -> sprintf " — %d symbols and %d files changed" s f
      | _ -> ""

    let propPart =
      match propertyViolation with
      | Some pv ->
        let catStr = pv.AlgebraicCategory |> Option.defaultValue "property"
        sprintf " [%s violation: %s]" catStr pv.ShrunkCounterexample
      | None -> ""

    let summary = sprintf "Test %s%s%s" timePart changePart propPart

    { LastPassedAt = lastPassedAt
      TimeSinceLastPass = timeSinceLastPass
      CausalChanges = causalChanges
      PropertyViolation = propertyViolation
      Summary = summary }

type LiveTestState = {
  SourceLocations: SourceTestLocation array
  DiscoveredTests: TestCase array
  LastResults: Map<TestId, TestRunResult>
  StatusEntries: TestStatusEntry array
  StatusEntrySlots: Map<TestId, int>
  StatusEntryIndex: Map<TestId, TestStatusEntry>
  StatusEntriesProjection: StatusEntriesProjectionState
  CoverageAnnotations: CoverageAnnotation array
  /// Per-session run phase tracking for concurrent multi-worker execution.
  RunPhases: Map<string, TestRunPhase>
  LastGeneration: RunGeneration
  History: RunHistory
  AffectedTests: Set<TestId>
  Activation: LiveTestingActivation
  CoverageDisplay: CoverageVisibility
  RunPolicies: Map<TestCategory, RunPolicy>
  DetectedProviders: ProviderDescription list
  CachedEditorAnnotations: LineAnnotation array
  AssemblyLoadErrors: AssemblyLoadError list
  FlakyHistory: Map<TestId, ResultWindow>
  /// Maps each TestId to the session that discovered it, enabling per-session execution routing.
  TestSessionMap: Map<TestId, string>
  /// Per-test packed coverage bitmaps from IL probe hits, keyed by TestId.
  /// All tests in the same batch share the same bitmap (conservative: any test might have hit any probe).
  TestCoverageBitmaps: Map<TestId, CoverageBitmap>
  /// Monotonic version counter — incremented in recomputeStatuses on every test state change.
  /// Used by SseDedupKey for O(1) change detection instead of recomputing from StatusEntries.
  StateVersion: int64
  /// Pre-computed test summary — updated in recomputeStatuses to avoid O(n log n) filtering
  /// in the dedup key hot path (was 50-100ms with 3131 tests, now O(1) field read).
  CachedTestSummary: TestSummary
  /// Enriched failure context for tests that recently transitioned Passed→Failed.
  FailureNarratives: Map<TestId, FailureNarrative>
  /// Timestamp of the most recent TestsDiscovered event merge. Used by run_tests to detect
  /// whether discovery completed after a hot-reload before proceeding with stale test list.
  LastDiscoveryTime: System.DateTimeOffset
  /// Sessions currently running an explicit discovery request triggered by live-testing enablement.
  PendingDiscoverySessions: Set<string>
}

[<RequireQualifiedAccess>]
type LiveTestDiscoveryState =
  | Disabled
  | Discovering
  | ReadyZeroTests
  | ReadyWithTests of discoveredCount: int

module LiveTestDiscoveryState =
  let toWireValue = function
    | LiveTestDiscoveryState.Disabled -> "disabled"
    | LiveTestDiscoveryState.Discovering -> "discovering"
    | LiveTestDiscoveryState.ReadyZeroTests -> "ready_zero_tests"
    | LiveTestDiscoveryState.ReadyWithTests _ -> "ready_with_tests"

  let hint = function
    | LiveTestDiscoveryState.Disabled ->
      "Live testing is not active. Call enable_live_testing to start discovery."
    | LiveTestDiscoveryState.Discovering ->
      "Live testing is active and discovery is still in progress."
    | LiveTestDiscoveryState.ReadyZeroTests ->
      "Live testing completed discovery but found zero tests."
    | LiveTestDiscoveryState.ReadyWithTests count ->
      sprintf "Live testing discovered %d tests." count

module LiveTestState =
  let discoveryState (state: LiveTestState) =
    match state.Activation with
    | LiveTestingActivation.Inactive -> LiveTestDiscoveryState.Disabled
    | LiveTestingActivation.Active ->
      match state.DiscoveredTests.Length with
      | count when count > 0 -> LiveTestDiscoveryState.ReadyWithTests count
      | _ when state.LastDiscoveryTime > System.DateTimeOffset.MinValue -> LiveTestDiscoveryState.ReadyZeroTests
      | _ -> LiveTestDiscoveryState.Discovering

  let requiresPrimingEval (_state: LiveTestState) =
    // Initial discovery is requested directly from each worker when live testing is
    // enabled, so callers should wait for discovery rather than forcing a synthetic eval.
    false

  let discoveryHint (state: LiveTestState) =
    discoveryState state
    |> LiveTestDiscoveryState.hint

  let empty = {
    SourceLocations = Array.empty
    DiscoveredTests = Array.empty
    LastResults = Map.empty
    StatusEntries = Array.empty
    StatusEntrySlots = Map.empty
    StatusEntryIndex = Map.empty
    StatusEntriesProjection = StatusEntriesProjectionState.Materialized
    CoverageAnnotations = Array.empty
    RunPhases = Map.empty
    LastGeneration = RunGeneration.zero
    History = RunHistory.NeverRun
    AffectedTests = Set.empty
    Activation = LiveTestingActivation.Inactive
    CoverageDisplay = CoverageVisibility.Shown
    RunPolicies = RunPolicyDefaults.defaults
    DetectedProviders = []
    CachedEditorAnnotations = Array.empty
    AssemblyLoadErrors = []
    FlakyHistory = Map.empty
    TestSessionMap = Map.empty
    TestCoverageBitmaps = Map.empty
    StateVersion = 0L
    CachedTestSummary = { Total = 0; Passed = 0; Failed = 0; Stale = 0; Running = 0; Disabled = 0; Enabled = true }
    FailureNarratives = Map.empty
    LastDiscoveryTime = System.DateTimeOffset.MinValue
    PendingDiscoverySessions = Set.empty
  }

  let buildStatusEntrySlots (entries: TestStatusEntry array) : Map<TestId, int> =
    entries
    |> Array.mapi (fun index entry -> entry.TestId, index)
    |> Map.ofArray

  let buildStatusEntryIndex (entries: TestStatusEntry array) : Map<TestId, TestStatusEntry> =
    entries
    |> Array.map (fun entry -> entry.TestId, entry)
    |> Map.ofArray

  let statusEntryIndex (state: LiveTestState) : Map<TestId, TestStatusEntry> =
    match Map.isEmpty state.StatusEntryIndex, Array.isEmpty state.StatusEntries with
    | true, false -> buildStatusEntryIndex state.StatusEntries
    | _ -> state.StatusEntryIndex

  let tryFindStatusEntry (testId: TestId) (state: LiveTestState) =
    statusEntryIndex state
    |> Map.tryFind testId

  let orderedStatusEntries (state: LiveTestState) : TestStatusEntry array =
    match state.StatusEntriesProjection with
    | StatusEntriesProjectionState.Materialized -> state.StatusEntries
    | StatusEntriesProjectionState.Deferred ->
      let index = statusEntryIndex state
      match Map.isEmpty index with
      | true -> state.StatusEntries
      | false ->
        state.DiscoveredTests
        |> Array.choose (fun test -> Map.tryFind test.Id index)

  /// Filter StatusEntries to only include tests belonging to the given session.
  /// When sessionId is empty or no session map entries exist, returns all entries (backwards compat).
  /// When sessionId is provided and TestSessionMap is populated, tests with no session attribution
  /// are excluded (not leaked) — they belong to an untracked path and should not bleed across sessions.
  let statusEntriesForSession (sessionId: string) (state: LiveTestState) : TestStatusEntry array =
    let entries = orderedStatusEntries state
    match System.String.IsNullOrEmpty sessionId || Map.isEmpty state.TestSessionMap with
    | true -> entries
    | false ->
      entries
      |> Array.filter (fun e ->
        match Map.tryFind e.TestId state.TestSessionMap with
        | Some sid -> sid = sessionId
        | None -> false)

  let withStatusEntries (entries: TestStatusEntry array) (state: LiveTestState) : LiveTestState =
    { state with
        StatusEntries = entries
        StatusEntrySlots = buildStatusEntrySlots entries
        StatusEntryIndex = buildStatusEntryIndex entries
        StatusEntriesProjection = StatusEntriesProjectionState.Materialized }

// --- Gutter Rendering Pure Functions ---

module GutterIcon =
  let toChar = function
    | GutterIcon.TestDiscovered -> '\u25C6'
    | GutterIcon.TestPassed -> '\u2713'
    | GutterIcon.TestFailed -> '\u2717'
    | GutterIcon.TestRunning -> '\u27F3'
    | GutterIcon.TestSkipped -> '\u25CB'
    | GutterIcon.TestFlaky -> '\u2248'
    | GutterIcon.Covered -> '\u258E'
    | GutterIcon.NotCovered -> '\u00B7'
    | GutterIcon.CellStale -> '\u26A0'
    | GutterIcon.BranchFullyCovered -> '\u2590'
    | GutterIcon.BranchPartiallyCovered -> '\u25D0'
    | GutterIcon.BranchNotCovered -> '\u258C'

  let toColorIndex = function
    | GutterIcon.TestDiscovered -> 33uy
    | GutterIcon.TestPassed -> 34uy
    | GutterIcon.TestFailed -> 160uy
    | GutterIcon.TestRunning -> 75uy
    | GutterIcon.TestSkipped -> 242uy
    | GutterIcon.TestFlaky -> 214uy
    | GutterIcon.Covered -> 34uy
    | GutterIcon.NotCovered -> 160uy
    | GutterIcon.CellStale -> 214uy
    | GutterIcon.BranchFullyCovered -> 34uy
    | GutterIcon.BranchPartiallyCovered -> 214uy
    | GutterIcon.BranchNotCovered -> 160uy

  let toLabel = function
    | GutterIcon.TestDiscovered -> "TestDiscovered"
    | GutterIcon.TestPassed -> "TestPassed"
    | GutterIcon.TestFailed -> "TestFailed"
    | GutterIcon.TestRunning -> "TestRunning"
    | GutterIcon.TestSkipped -> "TestSkipped"
    | GutterIcon.TestFlaky -> "TestFlaky"
    | GutterIcon.Covered -> "Covered"
    | GutterIcon.NotCovered -> "NotCovered"
    | GutterIcon.CellStale -> "CellStale"
    | GutterIcon.BranchFullyCovered -> "BranchFullyCovered"
    | GutterIcon.BranchPartiallyCovered -> "BranchPartiallyCovered"
    | GutterIcon.BranchNotCovered -> "BranchNotCovered"

  let parseLabel = function
    | "TestDiscovered" -> Some GutterIcon.TestDiscovered
    | "TestPassed" -> Some GutterIcon.TestPassed
    | "TestFailed" -> Some GutterIcon.TestFailed
    | "TestRunning" -> Some GutterIcon.TestRunning
    | "TestSkipped" -> Some GutterIcon.TestSkipped
    | "TestFlaky" -> Some GutterIcon.TestFlaky
    | "Covered" -> Some GutterIcon.Covered
    | "NotCovered" -> Some GutterIcon.NotCovered
    | "CellStale" -> Some GutterIcon.CellStale
    | "BranchFullyCovered" -> Some GutterIcon.BranchFullyCovered
    | "BranchPartiallyCovered" -> Some GutterIcon.BranchPartiallyCovered
    | "BranchNotCovered" -> Some GutterIcon.BranchNotCovered
    | _ -> None

  let toEmoji = function
    | GutterIcon.TestDiscovered -> "◆"
    | GutterIcon.TestPassed -> "✅"
    | GutterIcon.TestFailed -> "❌"
    | GutterIcon.TestRunning -> "⏳"
    | GutterIcon.TestSkipped -> "⏭️"
    | GutterIcon.TestFlaky -> "🔀"
    | GutterIcon.Covered -> "🟢"
    | GutterIcon.NotCovered -> "⚪"
    | GutterIcon.CellStale -> "⚠️"
    | GutterIcon.BranchFullyCovered -> "🟩"
    | GutterIcon.BranchPartiallyCovered -> "🟨"
    | GutterIcon.BranchNotCovered -> "🟥"

  let toStatusText = function
    | GutterIcon.TestDiscovered -> "discovered"
    | GutterIcon.TestPassed -> "passed"
    | GutterIcon.TestFailed -> "failed"
    | GutterIcon.TestRunning -> "running"
    | GutterIcon.TestSkipped -> "skipped"
    | GutterIcon.TestFlaky -> "flaky"
    | GutterIcon.Covered -> "covered"
    | GutterIcon.NotCovered -> "not covered"
    | GutterIcon.CellStale -> "stale"
    | GutterIcon.BranchFullyCovered -> "branches covered"
    | GutterIcon.BranchPartiallyCovered -> "branches partial"
    | GutterIcon.BranchNotCovered -> "branches uncovered"

  let toAnsiColor = function
    | GutterIcon.TestDiscovered -> "\x1b[33m"   // yellow
    | GutterIcon.TestPassed -> "\x1b[32m"       // green
    | GutterIcon.TestFailed -> "\x1b[31m"       // red
    | GutterIcon.TestRunning -> "\x1b[36m"      // cyan
    | GutterIcon.TestSkipped -> "\x1b[90m"      // dim gray
    | GutterIcon.TestFlaky -> "\x1b[33m"        // yellow (same as discovered — warning tone)
    | GutterIcon.Covered -> "\x1b[32m"          // green
    | GutterIcon.NotCovered -> "\x1b[90m"       // dim gray
    | GutterIcon.CellStale -> "\x1b[33m"        // yellow (stale = warning)
    | GutterIcon.BranchFullyCovered -> "\x1b[32m"  // green
    | GutterIcon.BranchPartiallyCovered -> "\x1b[33m" // yellow
    | GutterIcon.BranchNotCovered -> "\x1b[31m"    // red

module StatusToGutter =
  let fromTestStatus (status: TestRunStatus) : GutterIcon =
    match status with
    | TestRunStatus.Detected -> GutterIcon.TestDiscovered
    | TestRunStatus.Queued -> GutterIcon.TestDiscovered
    | TestRunStatus.Running -> GutterIcon.TestRunning
    | TestRunStatus.Passed _ -> GutterIcon.TestPassed
    | TestRunStatus.Failed _ -> GutterIcon.TestFailed
    | TestRunStatus.Skipped _ -> GutterIcon.TestSkipped
    | TestRunStatus.Stale -> GutterIcon.TestDiscovered
    | TestRunStatus.PolicyDisabled -> GutterIcon.TestSkipped

  let fromCoverageStatus (status: CoverageStatus) : GutterIcon =
    match status with
    | CoverageStatus.Covered (_, CoverageHealth.AllPassing) -> GutterIcon.Covered
    | CoverageStatus.Covered (_, CoverageHealth.SomeFailing) -> GutterIcon.NotCovered
    | CoverageStatus.NotCovered -> GutterIcon.NotCovered
    | CoverageStatus.Pending -> GutterIcon.TestDiscovered

  let tooltip (testName: string) (status: TestRunStatus) : string =
    match status with
    | TestRunStatus.Detected -> sprintf "%s (detected)" testName
    | TestRunStatus.Queued -> sprintf "%s (queued)" testName
    | TestRunStatus.Running -> sprintf "%s (running...)" testName
    | TestRunStatus.Passed d -> sprintf "%s \u2713 %dms" testName (int d.TotalMilliseconds)
    | TestRunStatus.Failed (f, d) ->
      let msg =
        match f with
        | TestFailure.AssertionFailed m -> m
        | TestFailure.ExceptionThrown (m, _) -> m
        | TestFailure.TimedOut t -> sprintf "timed out after %ds" (int t.TotalSeconds)
      sprintf "%s \u2717 %s (%dms)" testName msg (int d.TotalMilliseconds)
    | TestRunStatus.Skipped r -> sprintf "%s (skipped: %s)" testName r
    | TestRunStatus.Stale -> sprintf "%s (stale \u2014 needs re-run)" testName
    | TestRunStatus.PolicyDisabled -> sprintf "%s (disabled by policy)" testName

  let toAnnotation (line: int) (testName: string) (status: TestRunStatus) : LineAnnotation =
    { Line = line; Icon = fromTestStatus status; Tooltip = tooltip testName status }

  let coverageAnnotation (line: int) (symbol: string) (status: CoverageStatus) : LineAnnotation =
    let tip =
      match status with
      | CoverageStatus.Covered (n, CoverageHealth.AllPassing) -> sprintf "%s: covered by %d test(s), all passing" symbol n
      | CoverageStatus.Covered (n, CoverageHealth.SomeFailing) -> sprintf "%s: covered by %d test(s), some failing" symbol n
      | CoverageStatus.NotCovered -> sprintf "%s: not covered by any test" symbol
      | CoverageStatus.Pending -> sprintf "%s: coverage pending" symbol
    { Line = line; Icon = fromCoverageStatus status; Tooltip = tip }

module TestSummary =
  let empty = { Total = 0; Passed = 0; Failed = 0; Stale = 0; Running = 0; Disabled = 0; Enabled = true }

  let private applyStatusCountDelta
    (delta: int)
    (status: TestRunStatus)
    (summary: TestSummary)
    : TestSummary =
    match status with
    | TestRunStatus.Passed _ -> { summary with Passed = summary.Passed + delta }
    | TestRunStatus.Failed _ -> { summary with Failed = summary.Failed + delta }
    | TestRunStatus.Stale -> { summary with Stale = summary.Stale + delta }
    | TestRunStatus.Running -> { summary with Running = summary.Running + delta }
    | TestRunStatus.PolicyDisabled -> { summary with Disabled = summary.Disabled + delta }
    | _ -> summary

  let fromStatuses (activation: LiveTestingActivation) (statuses: TestRunStatus array) : TestSummary =
    let mutable passed = 0
    let mutable failed = 0
    let mutable stale = 0
    let mutable running = 0
    let mutable disabled = 0
    for s in statuses do
      match s with
      | TestRunStatus.Passed _ -> passed <- passed + 1
      | TestRunStatus.Failed _ -> failed <- failed + 1
      | TestRunStatus.Stale -> stale <- stale + 1
      | TestRunStatus.Running -> running <- running + 1
      | TestRunStatus.PolicyDisabled -> disabled <- disabled + 1
      | _ -> ()
    { Total = statuses.Length
      Passed = passed
      Failed = failed
      Stale = stale
      Running = running
      Disabled = disabled
      Enabled = activation = LiveTestingActivation.Active }

  let applyStatusEntryChanges
    (activation: LiveTestingActivation)
    (current: TestSummary)
    (changedEntries: TestStatusEntry array)
    : TestSummary =
    let adjusted =
      changedEntries
      |> Array.fold (fun summary entry ->
        summary
        |> applyStatusCountDelta -1 entry.PreviousStatus
        |> applyStatusCountDelta 1 entry.Status)
           current
    { adjusted with Enabled = activation = LiveTestingActivation.Active }

  /// Compact inline badge for per-session display: "✓42 ✗2" or "✓10" or "⟳3"
  let toInlineBadge (s: TestSummary) : string =
    match s.Total with
    | 0 -> ""
    | _ ->
      let parts = System.Collections.Generic.List<string>()
      match s.Passed > 0 with
      | true -> parts.Add(sprintf "✓%d" s.Passed)
      | false -> ()
      match s.Failed > 0 with
      | true -> parts.Add(sprintf "✗%d" s.Failed)
      | false -> ()
      match s.Running > 0 with
      | true -> parts.Add(sprintf "⟳%d" s.Running)
      | false -> ()
      match s.Stale > 0 with
      | true -> parts.Add(sprintf "●%d" s.Stale)
      | false -> ()
      match parts.Count = 0 with
      | true -> sprintf "%d tests" s.Total
      | false -> System.String.Join(" ", parts)

  let toStatusBar (s: TestSummary) : string =
    match s.Total, s.Failed, s.Running, s.Stale with
    | 0, _, _, _ -> "Tests: none"
    | _, f, _, _ when f > 0 ->
      sprintf "Tests: %d/%d \u2717%d" s.Passed s.Total s.Failed
    | _, _, r, _ when r > 0 ->
      sprintf "Tests: %d/%d \u27F3%d" s.Passed s.Total s.Running
    | _, _, _, st when st > 0 ->
      sprintf "Tests: %d/%d \u25CF%d" s.Passed s.Total s.Stale
    | _ ->
      sprintf "Tests: %d/%d \u2713" s.Passed s.Total

// --- Coverage Summary for per-session bitmap visualization ---

type CoverageSummary = {
  TotalProbes: int
  CoveredProbes: int
  CoveragePercent: float
  /// Downsampled density array — each element is coverage ratio in that chunk
  DensityStrip: float array
}

module CoverageSummary =
  let empty = { TotalProbes = 0; CoveredProbes = 0; CoveragePercent = 0.0; DensityStrip = [||] }

  /// Build a coverage summary from bitmaps belonging to a session.
  /// Merges all bitmaps via OR to get total coverage, then downsamples for rendering.
  let fromBitmaps (stripWidth: int) (bitmaps: CoverageBitmap seq) : CoverageSummary =
    let bmArray = bitmaps |> Seq.filter (fun bm -> bm.Count > 0) |> Seq.toArray
    match bmArray.Length with
    | 0 -> empty
    | _ ->
      let first = bmArray.[0]
      let compatible = bmArray |> Array.filter (fun bm -> bm.Count = first.Count)
      match compatible.Length with
      | 0 -> empty
      | _ ->
        let combined =
          compatible
          |> Array.reduce (fun acc bm ->
            let bits = Array.init acc.Bits.Length (fun i -> acc.Bits.[i] ||| bm.Bits.[i])
            { Bits = bits; Count = acc.Count })
        let totalProbes = combined.Count
        let coveredProbes = CoverageBitmap.popCount combined
        let coveragePercent =
          match totalProbes with
          | 0 -> 0.0
          | n -> float coveredProbes / float n * 100.0
        let bools = CoverageBitmap.toBoolArray combined
        let chunkSize = max 1 (totalProbes / stripWidth)
        let strip =
          [| for i in 0 .. stripWidth - 1 do
               let startIdx = i * chunkSize
               let endIdx = min (startIdx + chunkSize) totalProbes
               match endIdx > startIdx with
               | true ->
                 let chunk = bools.[startIdx .. endIdx - 1]
                 let hits = chunk |> Array.filter id |> Array.length
                 float hits / float chunk.Length
               | false -> 0.0 |]
        { TotalProbes = totalProbes
          CoveredProbes = coveredProbes
          CoveragePercent = coveragePercent
          DensityStrip = strip }

  /// Render compact text strip for TUI: █▓▒░· per chunk + percentage
  let toTextStrip (summary: CoverageSummary) : string =
    match summary.TotalProbes with
    | 0 -> ""
    | _ ->
      let blocks =
        summary.DensityStrip
        |> Array.map (fun d ->
          match d with
          | x when x >= 0.75 -> '\u2588'
          | x when x >= 0.50 -> '\u2593'
          | x when x >= 0.25 -> '\u2592'
          | x when x > 0.0 -> '\u2591'
          | _ -> '\u00B7')
      sprintf "%s %.0f%%" (System.String(blocks)) summary.CoveragePercent

// --- Test Treemap (WizTree-style: area = duration) ---

/// One rectangle in a WizTree-style test treemap.
/// Area is proportional to duration — you instantly see which tests are slow.
[<RequireQualifiedAccess>]
type TreemapStatus = Passed | Failed | Skipped | Running | Other

type TestTreemapEntry = {
  DisplayName: string
  FullName: string
  DurationMs: float
  Status: TreemapStatus
}

/// A positioned rectangle in the treemap layout.
type TreemapRect = {
  Entry: TestTreemapEntry
  X: float
  Y: float
  W: float
  H: float
}

module TestTreemap =
  /// Extract treemap entries from test status entries.
  /// Only includes tests with known durations (Passed/Failed).
  let fromStatusEntries (entries: TestStatusEntry array) : TestTreemapEntry array =
    entries
    |> Array.choose (fun e ->
      match e.Status with
      | TestRunStatus.Passed duration ->
        Some { DisplayName = e.DisplayName; FullName = e.FullName
               DurationMs = duration.TotalMilliseconds; Status = TreemapStatus.Passed }
      | TestRunStatus.Failed (_, duration) ->
        Some { DisplayName = e.DisplayName; FullName = e.FullName
               DurationMs = duration.TotalMilliseconds; Status = TreemapStatus.Failed }
      | TestRunStatus.Running ->
        Some { DisplayName = e.DisplayName; FullName = e.FullName
               DurationMs = 0.0; Status = TreemapStatus.Running }
      | TestRunStatus.Skipped _ ->
        Some { DisplayName = e.DisplayName; FullName = e.FullName
               DurationMs = 0.0; Status = TreemapStatus.Skipped }
      | _ -> None)
    |> Array.sortByDescending (fun e -> e.DurationMs)

  /// Squarified treemap layout algorithm (Bruls, Huizing, van Wijk).
  /// Packs rectangles into a bounding box with near-square aspect ratios.
  /// Area of each rect is proportional to DurationMs.
  let layout (width: float) (height: float) (entries: TestTreemapEntry array) : TreemapRect array =
    match entries.Length with
    | 0 -> [||]
    | _ ->
      let totalMs = entries |> Array.sumBy (fun e -> max e.DurationMs 0.1)
      let totalArea = width * height
      // Normalize: each entry's area = (durationMs / totalMs) * totalArea
      let areas = entries |> Array.map (fun e -> max e.DurationMs 0.1 / totalMs * totalArea)
      let result = ResizeArray<TreemapRect>()
      let mutable x0 = 0.0
      let mutable y0 = 0.0
      let mutable w0 = width
      let mutable h0 = height
      let mutable idx = 0
      while idx < entries.Length do
        // Lay out a strip along the shorter side
        let isVertical = w0 >= h0
        let side = match isVertical with | true -> h0 | false -> w0
        // Greedily add entries to the current strip while aspect ratio improves
        let mutable stripArea = 0.0
        let mutable bestWorst = System.Double.MaxValue
        let mutable stripEnd = idx
        let mutable improving = true
        while stripEnd < entries.Length && improving do
          stripArea <- stripArea + areas.[stripEnd]
          let stripLen = stripArea / side
          // Worst aspect ratio in this strip
          let worstAspect =
            let mutable worst = 0.0
            for j in idx .. stripEnd do
              let cellLen = areas.[j] / stripLen
              let aspect = max (cellLen / stripLen) (stripLen / cellLen)
              worst <- max worst aspect
            worst
          match worstAspect <= bestWorst with
          | true ->
            bestWorst <- worstAspect
            stripEnd <- stripEnd + 1
          | false ->
            // Adding this entry made it worse — back off
            stripArea <- stripArea - areas.[stripEnd]
            improving <- false
        // If we added nothing (shouldn't happen, but safety), take one
        let stripEnd = match stripEnd = idx with | true -> idx + 1 | false -> stripEnd
        let stripLen = stripArea / side
        // Place entries in the strip
        let mutable offset = 0.0
        for j in idx .. stripEnd - 1 do
          let cellLen = areas.[j] / stripLen
          match isVertical with
          | true ->
            result.Add { Entry = entries.[j]; X = x0; Y = y0 + offset; W = stripLen; H = cellLen }
          | false ->
            result.Add { Entry = entries.[j]; X = x0 + offset; Y = y0; W = cellLen; H = stripLen }
          offset <- offset + cellLen
        // Shrink remaining area
        match isVertical with
        | true ->
          x0 <- x0 + stripLen
          w0 <- w0 - stripLen
        | false ->
          y0 <- y0 + stripLen
          h0 <- h0 - stripLen
        idx <- stripEnd
      result.ToArray()

// --- Enriched SSE Batch Payload ---

/// Whether a batch of test results represents a complete run.
[<RequireQualifiedAccess>]
type BatchCompletion =
  | Complete of requested: int * returned: int
  | Partial of requested: int * returned: int
  | Superseded

type TestResultsBatchPayload = {
  Generation: RunGeneration
  Freshness: ResultFreshness
  Completion: BatchCompletion
  Entries: TestStatusEntry array
  Summary: TestSummary
}

module TestResultsBatchPayload =
  let create
    (generation: RunGeneration)
    (freshness: ResultFreshness)
    (completion: BatchCompletion)
    (activation: LiveTestingActivation)
    (entries: TestStatusEntry array)
    : TestResultsBatchPayload =
    let summary =
      entries
      |> Array.map (fun e -> e.Status)
      |> TestSummary.fromStatuses activation
    { Generation = generation
      Freshness = freshness
      Completion = completion
      Entries = entries
      Summary = summary }

  /// Derive completion from requested vs returned counts and freshness.
  let deriveCompletion (freshness: ResultFreshness) (requested: int) (returned: int) : BatchCompletion =
    match freshness with
    | StaleCodeEdited | StaleWrongGeneration -> BatchCompletion.Superseded
    | Fresh ->
      match returned >= requested with
      | true -> BatchCompletion.Complete(requested, returned)
      | false -> BatchCompletion.Partial(requested, returned)

  let isEmpty (p: TestResultsBatchPayload) =
    Array.isEmpty p.Entries

module CategoryDetection =
  let private ioPatterns = [|
    "System.IO"; "File."; "Directory."; "Path."; "StreamReader"; "StreamWriter"
    "HttpClient"; "WebClient"; "Socket"; "TcpClient"; "UdpClient"; "System.Net"
    "SqlConnection"; "DbConnection"; "NpgsqlConnection"; "SqliteConnection"; "IDbConnection"
    "Process.Start"; "ProcessStartInfo"
  |]

  let private containsIoPattern (source: string) =
    ioPatterns |> Array.exists source.Contains

  let categorize
    (labels: string list)
    (fullName: string)
    (_framework: TestFramework)
    (referencedAssemblies: string array)
    (sourceContent: string option)
    : TestCategory =
    let labelLower = labels |> List.map (fun l -> l.ToLowerInvariant())
    match labelLower |> List.exists (fun l -> l.Contains "unit") with
    | true -> TestCategory.Unit
    | false ->
    match labelLower |> List.exists (fun l -> l.Contains "integration") with
    | true -> TestCategory.Integration
    | false ->
    match labelLower |> List.exists (fun l -> l.Contains "browser" || l.Contains "e2e" || l.Contains "ui") with
    | true -> TestCategory.Browser
    | false ->
    match labelLower |> List.exists (fun l -> l.Contains "benchmark") with
    | true -> TestCategory.Benchmark
    | false ->
    match labelLower |> List.exists (fun l -> l.Contains "property") with
    | true -> TestCategory.Property
    | false ->
    match labelLower |> List.exists (fun l -> l.Contains "architecture" || l.Contains "arch") with
    | true -> TestCategory.Architecture
    | false ->
    match fullName.ToLowerInvariant().Contains "integration" with
    | true -> TestCategory.Integration
    | false ->
    match fullName.ToLowerInvariant().Contains "browser" || fullName.ToLowerInvariant().Contains "e2e" with
    | true -> TestCategory.Browser
    | false ->
    match referencedAssemblies |> Array.exists (fun a -> a.Contains "Microsoft.Playwright") with
    | true -> TestCategory.Browser
    | false ->
    match referencedAssemblies |> Array.exists (fun a -> a.Contains "BenchmarkDotNet") with
    | true -> TestCategory.Benchmark
    | false ->
    match sourceContent with
    | Some src when containsIoPattern src -> TestCategory.Integration
    | _ -> TestCategory.Unit

// --- Pure functions ---

module TestDependencyGraph =
  let empty = {
    SymbolToTests = Map.empty
    TransitiveCoverage = Map.empty
    PerFileIndex = Map.empty
    SourceVersion = 0
  }

  /// Merge all per-file indexes into a single symbol→tests map,
  /// concatenating TestId arrays for symbols referenced across multiple files.
  let mergePerFileIndexes (perFile: Map<string, Map<string, TestId array>>) : Map<string, TestId array> =
    perFile
    |> Map.values
    |> Seq.collect (fun fileIndex -> fileIndex |> Map.toSeq)
    |> Seq.groupBy fst
    |> Seq.map (fun (sym, entries) ->
      sym, entries |> Seq.collect snd |> Seq.distinct |> Seq.toArray)
    |> Map.ofSeq

  /// Create a graph where transitive = direct (no call graph).
  let fromDirect (symbolToTests: Map<string, TestId array>) =
    { SymbolToTests = symbolToTests
      TransitiveCoverage = symbolToTests
      PerFileIndex = Map.empty
      SourceVersion = 1 }

  let findAffected (changedSymbols: string list) (graph: TestDependencyGraph) : TestId array =
    changedSymbols
    |> List.choose (fun sym -> Map.tryFind sym graph.TransitiveCoverage)
    |> Array.concat
    |> Array.distinct

  /// BFS from a symbol through the call graph, returning all reachable symbols.
  let reachableFrom (callGraph: Map<string, string array>) (start: string) : string list =
    let visited = System.Collections.Generic.HashSet<string>()
    let queue = System.Collections.Generic.Queue<string>()
    queue.Enqueue(start)
    visited.Add(start) |> ignore
    while queue.Count > 0 do
      let current = queue.Dequeue()
      match Map.tryFind current callGraph with
      | Some callees ->
        for callee in callees do
          match visited.Add(callee) with
          | true -> queue.Enqueue(callee)
          | false -> ()
      | None -> ()
    visited |> Seq.toList

  /// Compute transitive coverage: for every symbol reachable from a directly-tested
  /// symbol via the call graph, attribute those tests to the callee.
  let computeTransitiveCoverage
    (callGraph: Map<string, string array>)
    (directSymbolToTests: Map<string, TestId array>)
    : Map<string, TestId array> =
    let mutable result = System.Collections.Generic.Dictionary<string, System.Collections.Generic.HashSet<TestId>>()
    for kvp in directSymbolToTests do
      let symbol = kvp.Key
      let testIds = kvp.Value
      let reachable = reachableFrom callGraph symbol
      for reached in reachable do
        match result.TryGetValue(reached) with
        | true, existing ->
          for tid in testIds do existing.Add(tid) |> ignore
        | false, _ ->
          let hs = System.Collections.Generic.HashSet<TestId>()
          for tid in testIds do hs.Add(tid) |> ignore
          result.[reached] <- hs
    result
    |> Seq.map (fun kvp -> kvp.Key, kvp.Value |> Seq.toArray)
    |> Map.ofSeq

  /// Extract caller→callee edges from production (non-test) symbol uses.
  /// Uses the same line-range heuristic as buildFromSymbolUses: each definition
  /// "owns" lines until the next definition starts.
  let extractCallGraph
    (testModuleIdentifier: string)
    (uses: ExtractedSymbolUse array)
    : Map<string, string array> =
    let prodDefs =
      uses
      |> Array.filter (fun u ->
        u.UseKind = SymbolUseKind.Definition
        && not (u.FullName.Contains(testModuleIdentifier)))
      |> Array.sortBy (fun u -> u.StartLine)
    let defRanges =
      [| for i in 0 .. prodDefs.Length - 1 do
           let endLine =
             match i < prodDefs.Length - 1 with
             | true -> prodDefs.[i + 1].StartLine - 1
             | false -> System.Int32.MaxValue
           yield prodDefs.[i], prodDefs.[i].StartLine, endLine |]
    let nonTestRefs =
      uses
      |> Array.filter (fun u ->
        u.UseKind = SymbolUseKind.Reference
        && not (u.FullName.StartsWith("Microsoft.FSharp"))
        && not (u.FullName.Contains(testModuleIdentifier))
        && u.FullName.Contains("."))
    [| for (defUse, startLine, endLine) in defRanges do
         let callees =
           nonTestRefs
           |> Array.filter (fun u ->
             u.StartLine >= startLine
             && u.StartLine <= endLine
             && u.FullName <> defUse.FullName)
           |> Array.map (fun u -> u.FullName)
           |> Array.distinct
         match callees.Length > 0 with
         | true -> yield defUse.FullName, callees
         | false -> () |]
    |> Map.ofArray

  /// Build a direct dependency graph from extracted FCS symbol uses.
  /// `testModuleIdentifier` (e.g. ".Tests.") identifies test function namespaces.
  /// Returns an inverted index: production symbol → test IDs that reference it.
  let buildFromSymbolUses
    (testModuleIdentifier: string)
    (framework: TestFramework)
    (uses: ExtractedSymbolUse array)
    : TestDependencyGraph =
    let testDefs =
      uses
      |> Array.filter (fun u ->
        u.UseKind = SymbolUseKind.Definition
        && u.FullName.Contains(testModuleIdentifier)
        && not (u.FullName.EndsWith(testModuleIdentifier)))
      |> Array.sortBy (fun t -> t.StartLine)
    let testRanges =
      [| for i in 0 .. testDefs.Length - 1 do
           let endLine =
             match i < testDefs.Length - 1 with
             | true -> testDefs.[i + 1].StartLine - 1
             | false -> System.Int32.MaxValue
           yield testDefs.[i], testDefs.[i].StartLine, endLine |]
    let nonDefUses =
      uses
      |> Array.filter (fun u ->
        u.UseKind = SymbolUseKind.Reference
        && not (u.FullName.StartsWith("Microsoft.FSharp"))
        && u.FullName.Contains("."))
    let invertedIndex =
      [| for (testDef, startLine, endLine) in testRanges do
           let testId = TestId.create testDef.FullName framework
           let refs =
             nonDefUses
             |> Array.filter (fun u ->
               u.StartLine >= startLine
               && u.StartLine <= endLine
               && not (u.FullName.Contains(testModuleIdentifier)))
           for s in refs do
             yield s.FullName, testId |]
      |> Array.groupBy fst
      |> Array.map (fun (sym, pairs) -> sym, pairs |> Array.map snd |> Array.distinct)
      |> Map.ofArray
    let callGraph = extractCallGraph testModuleIdentifier uses
    { SymbolToTests = invertedIndex
      TransitiveCoverage = computeTransitiveCoverage callGraph invertedIndex
      PerFileIndex = Map.empty
      SourceVersion = 1 }

module LiveTesting =
  let private computeStatusForTest
    (state: LiveTestState)
    (category: TestCategory)
    (testId: TestId)
    : TestRunStatus =
    match Map.tryFind category state.RunPolicies with
    | Some RunPolicy.Disabled -> TestRunStatus.PolicyDisabled
    | _ ->
      let resultStatus =
        match Map.tryFind testId state.LastResults with
        | Some r ->
          match r.Result with
          | TestResult.Passed d -> Some (TestRunStatus.Passed d)
          | TestResult.Failed (f, d) -> Some (TestRunStatus.Failed (f, d))
          | TestResult.Skipped reason -> Some (TestRunStatus.Skipped reason)
          | TestResult.NotRun -> None
        | None -> None
      match Set.contains testId state.AffectedTests with
      | true ->
        let testSession = Map.tryFind testId state.TestSessionMap
        let sessionRunning = TestRunPhase.isSessionRunning testSession state.RunPhases
        match sessionRunning with
        | true ->
          resultStatus |> Option.defaultValue TestRunStatus.Running
        | false ->
          match resultStatus with
          | Some s -> TestRunStatus.Stale
          | None -> TestRunStatus.Queued
      | false ->
        resultStatus |> Option.defaultValue TestRunStatus.Detected

  let filterByPolicy
    (policies: Map<TestCategory, RunPolicy>)
    (trigger: RunTrigger)
    (tests: TestCase array)
    : TestCase array =
    tests
    |> Array.filter (fun test ->
      match Map.tryFind test.Category policies with
      | Some RunPolicy.Disabled -> false
      | Some RunPolicy.OnDemand ->
        match trigger with
        | RunTrigger.ExplicitRun -> true
        | _ -> false
      | Some RunPolicy.OnSaveOnly ->
        match trigger with
        | RunTrigger.ExplicitRun | RunTrigger.FileSave -> true
        | _ -> false
      | Some RunPolicy.OnEveryChange -> true
      | None -> true)

  let computeStatusEntriesWithHistory
    (previousStatuses: Map<TestId, TestRunStatus>)
    (state: LiveTestState)
    : TestStatusEntry array =
    state.DiscoveredTests
    |> Array.map (fun test ->
      let status = computeStatusForTest state test.Category test.Id
      let prevStatus =
        match Map.tryFind test.Id previousStatuses with
        | Some prev -> prev
        | None -> TestRunStatus.Detected
      { TestId = test.Id
        DisplayName = test.DisplayName
        FullName = test.FullName
        Origin = test.Origin
        Framework = test.Framework
        Category = test.Category
        CurrentPolicy =
          match Map.tryFind test.Category state.RunPolicies with
          | Some p -> p
          | None -> RunPolicy.OnEveryChange
        Status = status
        PreviousStatus = prevStatus })

  /// Merge incoming discovered tests with existing ones, keyed by TestId.
  /// Incoming tests take priority for collisions (e.g., FSI redefining a test).
  /// Empty incoming preserves existing tests (prevents wipe on FSI-only evals).
  let mergeDiscoveredTests (existing: TestCase array) (incoming: TestCase array) : TestCase array =
    match Array.isEmpty existing, Array.isEmpty incoming with
    | true, _ -> incoming
    | _, true -> existing
    | false, false ->
      let incomingById = incoming |> Array.map (fun t -> t.Id, t) |> Map.ofArray
      let merged =
        existing
        |> Array.fold (fun acc t ->
          match Map.containsKey t.Id acc with
          | true -> acc
          | false -> Map.add t.Id t acc) incomingById
      merged |> Map.values |> Seq.toArray

  /// Merge test results into state.
  /// Does NOT transition RunPhase or clear AffectedTests — those are managed by
  /// TestRunStarted (sets Running + AffectedTests) and TestRunCompleted (sets Idle + clears).
  /// This enables streaming results to update incrementally while run is in progress.
  /// Derived views like StatusEntries are finalized once at the app boundary so
  /// large streamed batches do not pay for duplicate full-state recomputation.
  let private resultDuration (result: TestRunResult) : System.TimeSpan =
    match result.Result with
    | TestResult.Passed duration -> duration
    | TestResult.Failed (_, duration) -> duration
    | _ -> System.TimeSpan.Zero

  let private collectEffectiveResultDelta
    (existing: Map<TestId, TestRunResult>)
    (batches: TestRunResult array list)
    : System.Collections.Generic.Dictionary<TestId, TestRunResult> * System.TimeSpan =
    let latestById = System.Collections.Generic.Dictionary<TestId, TestRunResult>()
    let mutable maxDuration = System.TimeSpan.Zero

    for batch in batches do
      for result in batch do
        let duration = resultDuration result
        match duration > maxDuration with
        | true -> maxDuration <- duration
        | false -> ()

        match result.Result with
        | TestResult.NotRun ->
          let alreadyKnown =
            latestById.ContainsKey result.TestId
            || Map.containsKey result.TestId existing

          match alreadyKnown with
          | true -> ()
          | false -> latestById[result.TestId] <- result
        | _ ->
          latestById[result.TestId] <- result

    latestById, maxDuration

  let private applyEffectiveResultDelta
    (existing: Map<TestId, TestRunResult>)
    (latestById: System.Collections.Generic.Dictionary<TestId, TestRunResult>)
    : Map<TestId, TestRunResult> =
    let mutable merged = existing

    for KeyValue(testId, result) in latestById do
      merged <- Map.add testId result merged

    merged

  let mergeResults (state: LiveTestState) (results: TestRunResult array) : LiveTestState =
    match Array.isEmpty results with
    | true -> state
    | false ->
      let latestById, maxDuration =
        collectEffectiveResultDelta state.LastResults [ results ]
      let newResults =
        applyEffectiveResultDelta state.LastResults latestById
      { state with
          LastResults = newResults
          History = RunHistory.PreviousRun maxDuration }

  let private statusEntrySlotsForChangedIds
    (state: LiveTestState)
    (changedIds: Set<TestId>)
    : Map<TestId, int> =
    let slots =
      match state.StatusEntrySlots.Count = state.StatusEntries.Length with
      | true -> state.StatusEntrySlots
      | false -> LiveTestState.buildStatusEntrySlots state.StatusEntries

    let coversChangedIds =
      changedIds
      |> Seq.forall (fun testId ->
        match Map.tryFind testId slots with
        | Some index when index >= 0 && index < state.StatusEntries.Length ->
          state.StatusEntries[index].TestId = testId
        | _ -> false)

    match coversChangedIds with
    | true -> slots
    | false -> LiveTestState.buildStatusEntrySlots state.StatusEntries

  let patchStatusEntriesForChangedIds
    (previous: LiveTestState)
    (updated: LiveTestState)
    (changedIds: Set<TestId>)
    : LiveTestState * TestStatusEntry array =
    match Set.isEmpty changedIds, Array.isEmpty previous.StatusEntries with
    | true, _ ->
      { updated with
          StatusEntries = previous.StatusEntries
          StatusEntrySlots = previous.StatusEntrySlots }, Array.empty
    | _, true ->
      updated, Array.empty
    | _ ->
      let slots = statusEntrySlotsForChangedIds previous changedIds
      let changedEntries = ResizeArray<TestStatusEntry>()
      let statusEntries = Array.copy previous.StatusEntries

      for testId in changedIds do
        match Map.tryFind testId slots with
        | Some index ->
          let entry = statusEntries[index]
          let policy =
            match Map.tryFind entry.Category updated.RunPolicies with
            | Some p -> p
            | None -> RunPolicy.OnEveryChange
          let updatedEntry =
            { entry with
                CurrentPolicy = policy
                Status = computeStatusForTest updated entry.Category entry.TestId
                PreviousStatus = entry.Status }
          statusEntries[index] <- updatedEntry
          changedEntries.Add updatedEntry
        | None -> ()

      { updated with
          StatusEntries = statusEntries
          StatusEntrySlots = slots
          StatusEntryIndex = LiveTestState.buildStatusEntryIndex statusEntries
          StatusEntriesProjection = StatusEntriesProjectionState.Materialized }, changedEntries.ToArray()

  let private patchBufferedStatusEntryIndexForChangedIds
    (previous: LiveTestState)
    (updated: LiveTestState)
    (changedIds: Set<TestId>)
    : LiveTestState * TestStatusEntry array =
    let previousIndex = LiveTestState.statusEntryIndex previous
    match Set.isEmpty changedIds, Map.isEmpty previousIndex with
    | true, _ ->
      { updated with
          StatusEntries = previous.StatusEntries
          StatusEntrySlots = previous.StatusEntrySlots
          StatusEntryIndex = previousIndex
          StatusEntriesProjection = previous.StatusEntriesProjection }, Array.empty
    | _, true ->
      updated, Array.empty
    | _ ->
      let changedEntries = ResizeArray<TestStatusEntry>()
      let mutable index = previousIndex

      for testId in changedIds do
        match Map.tryFind testId index with
        | Some entry ->
          let policy =
            match Map.tryFind entry.Category updated.RunPolicies with
            | Some p -> p
            | None -> RunPolicy.OnEveryChange
          let updatedEntry =
            { entry with
                CurrentPolicy = policy
                Status = computeStatusForTest updated entry.Category entry.TestId
                PreviousStatus = entry.Status }
          index <- Map.add testId updatedEntry index
          changedEntries.Add updatedEntry
        | None -> ()

      let projectionState =
        match changedEntries.Count = 0 with
        | true -> previous.StatusEntriesProjection
        | false -> StatusEntriesProjectionState.Deferred

      { updated with
          StatusEntries = previous.StatusEntries
          StatusEntrySlots = previous.StatusEntrySlots
          StatusEntryIndex = index
          StatusEntriesProjection = projectionState }, changedEntries.ToArray()

  /// Merge multiple streamed result batches, then patch only the affected status
  /// entries once. This keeps every raw result fact while avoiding repeated
  /// O(all discovered tests) scans when several batches are buffered together.
  let mergeBufferedResultsWithUpdatedStatusEntriesAndChangedEntries
    (state: LiveTestState)
    (batches: TestRunResult array list)
    : LiveTestState * TestStatusEntry array =
    let nonEmptyBatches =
      batches |> List.filter (fun batch -> not (Array.isEmpty batch))
    match nonEmptyBatches, Array.isEmpty state.StatusEntries with
    | [], _ -> state, Array.empty
    | _, true ->
      let latestById, maxDuration =
        collectEffectiveResultDelta state.LastResults nonEmptyBatches
      let merged =
        { state with
            LastResults = applyEffectiveResultDelta state.LastResults latestById
            History = RunHistory.PreviousRun maxDuration }
      match Array.isEmpty merged.DiscoveredTests with
      | true -> merged, Array.empty
      | false ->
        let statusEntries =
          computeStatusEntriesWithHistory Map.empty merged
        let changedIds =
          latestById
          |> Seq.map (fun (KeyValue(testId, _)) -> testId)
          |> Set.ofSeq
        let changedEntries : TestStatusEntry array =
          statusEntries
          |> Array.filter (fun entry -> Set.contains entry.TestId changedIds)
        LiveTestState.withStatusEntries statusEntries merged, changedEntries
    | _ ->
      let latestById, maxDuration =
        collectEffectiveResultDelta state.LastResults nonEmptyBatches
      let merged =
        { state with
            LastResults = applyEffectiveResultDelta state.LastResults latestById
            History = RunHistory.PreviousRun maxDuration }
      let changedIds =
        latestById
        |> Seq.map (fun (KeyValue(testId, _)) -> testId)
        |> Set.ofSeq
      patchBufferedStatusEntryIndexForChangedIds state merged changedIds

  let mergeBufferedResultsWithUpdatedStatusEntries
    (state: LiveTestState)
    (batches: TestRunResult array list)
    : LiveTestState =
    mergeBufferedResultsWithUpdatedStatusEntriesAndChangedEntries state batches
    |> fst

  /// Merge result facts, then patch only the affected status entries instead of
  /// rescanning every discovered test. This keeps streamed batches lossless while
  /// avoiding repeated O(all discovered tests) work for partial updates.
  let mergeResultsWithUpdatedStatusEntries (state: LiveTestState) (results: TestRunResult array) : LiveTestState =
    mergeBufferedResultsWithUpdatedStatusEntries state [ results ]

  let computeStatusEntries (state: LiveTestState) : TestStatusEntry array =
    computeStatusEntriesWithHistory Map.empty state

  let applyFailureNarrativeChanges
    (now: System.DateTimeOffset)
    (changedSymbols: string list)
    (changedFiles: string list)
    (previousNarratives: Map<TestId, FailureNarrative>)
    (changedEntries: TestStatusEntry array)
    (state: LiveTestState)
    : Map<TestId, FailureNarrative> =
    changedEntries
    |> Array.fold (fun acc entry ->
      match entry.Status with
      | TestRunStatus.Failed (failure, _) ->
        match Map.containsKey entry.TestId acc with
        | true -> acc
        | false ->
          let lastPassed =
            match entry.PreviousStatus with
            | TestRunStatus.Passed _ ->
              Map.tryFind entry.TestId state.LastResults
            | _ -> None
          let flakyClass =
            FlakyDetection.classifyFlakiness entry.TestId state.FlakyHistory state.LastResults
          let narrative =
            FailureNarrativeBuilder.buildNarrative now lastPassed changedSymbols changedFiles flakyClass failure
          Map.add entry.TestId narrative acc
      | TestRunStatus.Passed _ ->
        Map.remove entry.TestId acc
      | _ -> acc)
         previousNarratives

  /// Compute failure narratives for tests that transitioned to Failed.
  /// Only creates narratives for Passed→Failed transitions (new regressions).
  /// Preserves existing narratives for tests still failing.
  let computeFailureNarratives
    (now: System.DateTimeOffset)
    (changedSymbols: string list)
    (changedFiles: string list)
    (previousNarratives: Map<TestId, FailureNarrative>)
    (state: LiveTestState)
    : Map<TestId, FailureNarrative> =
    applyFailureNarrativeChanges
      now
      changedSymbols
      changedFiles
      previousNarratives
      (LiveTestState.orderedStatusEntries state)
      state

  let markAffected (testIds: TestId array) (state: LiveTestState) : LiveTestState =
    let affected = testIds |> Set.ofArray |> Set.union state.AffectedTests
    let previousStatuses =
      LiveTestState.orderedStatusEntries state
      |> Array.map (fun e -> e.TestId, e.Status)
      |> Map.ofArray
    let updatedState = { state with AffectedTests = affected }
    LiveTestState.withStatusEntries
      (computeStatusEntriesWithHistory previousStatuses updatedState)
      updatedState

  let annotationsForFile (filePath: string) (state: LiveTestState) : LineAnnotation array =
    let entryIndex = LiveTestState.statusEntryIndex state
    let tsAnnotations =
      state.SourceLocations
      |> Array.filter (fun sl -> sl.FilePath = filePath)
      |> Array.map (fun sl ->
        { Line = sl.Line
          Icon = GutterIcon.TestDiscovered
          Tooltip = sprintf "Test: %s (detected)" sl.AttributeName })
    let resultAnnotations =
      state.DiscoveredTests
      |> Array.choose (fun test ->
        match test.Origin with
        | TestOrigin.SourceMapped (f, line) when f = filePath ->
          match Map.tryFind test.Id entryIndex with
          | Some entry ->
            Some (StatusToGutter.toAnnotation line entry.DisplayName entry.Status)
          | None -> None
        | _ -> None)
    let resultLines = resultAnnotations |> Array.map (fun a -> a.Line) |> Set.ofArray
    let filtered = tsAnnotations |> Array.filter (fun a -> not (resultLines.Contains a.Line))
    Array.append filtered resultAnnotations
    |> Array.sortBy (fun a -> a.Line)

  /// Recompute cached editor annotations. Call in update path, not render path.
  let recomputeEditorAnnotations (activeFile: string option) (state: LiveTestState) : LineAnnotation array =
    match activeFile with
    | Some f when state.Activation = LiveTestingActivation.Active -> annotationsForFile f state
    | _ -> [||]

module SourceMapping =
  /// Extract the method/property name that should match tree-sitter's FunctionName.
  /// For "Namespace.Module.Tests.shouldAdd" → "shouldAdd"
  /// For "Namespace.Module.myTests/should add numbers" → "myTests" (Expecto hierarchical)
  let extractMethodName (fullName: string) =
    let slashIdx = fullName.IndexOf('/')
    match slashIdx > 0 with
    | true ->
      let prefix = fullName.Substring(0, slashIdx)
      let lastDot = prefix.LastIndexOf('.')
      match lastDot >= 0 with
      | true -> prefix.Substring(lastDot + 1)
      | false -> prefix
    | false ->
      let parts = fullName.Split('.')
      match parts.Length > 0 with
      | true -> parts.[parts.Length - 1]
      | false -> fullName

  /// Extract module name from test FullName for disambiguation.
  /// "SageFs.Tests.McpAdapterTests.tests/Adapter/..." → Some "McpAdapterTests"
  let extractModuleName (fullName: string) =
    let slashIdx = fullName.IndexOf('/')
    let prefix = if slashIdx > 0 then fullName.Substring(0, slashIdx) else fullName
    let parts = prefix.Split('.')
    match parts.Length >= 2 with
    | true -> Some parts.[parts.Length - 2]
    | false -> None

  /// Does this attribute name match the given test framework?
  let attributeMatchesFramework (framework: TestFramework) (attrName: string) =
    match framework with
    | TestFramework.Expecto -> attrName = "Tests" || attrName = "TestsAttribute"
    | TestFramework.XUnit ->
      attrName = "Fact" || attrName = "FactAttribute"
      || attrName = "Theory" || attrName = "TheoryAttribute"
    | TestFramework.NUnit ->
      attrName = "Test" || attrName = "TestAttribute"
      || attrName = "TestCase" || attrName = "TestCaseAttribute"
    | TestFramework.MSTest ->
      attrName = "TestMethod" || attrName = "TestMethodAttribute"
      || attrName = "DataTestMethod" || attrName = "DataTestMethodAttribute"
    | TestFramework.TUnit -> attrName = "Test" || attrName = "TestAttribute"
    | TestFramework.Unknown _ -> false

  /// Merge tree-sitter source locations into discovered tests.
  /// Matches by function name against the test's FullName suffix.
  /// When multiple locations share a function name (e.g. "tests"),
  /// uses the module name from FullName to disambiguate by file name.
  /// Tests already source-mapped are preserved unchanged.
  let mergeSourceLocations
    (locations: SourceTestLocation array)
    (tests: TestCase array)
    : TestCase array =
    match Array.isEmpty locations with
    | true -> tests
    | false ->
      let locationsByFuncName =
        locations
        |> Array.groupBy (fun loc -> loc.FunctionName)
        |> Map.ofArray
      tests |> Array.map (fun test ->
        match test.Origin with
        | TestOrigin.SourceMapped _ -> test
        | TestOrigin.ReflectionOnly ->
          let methodName = extractMethodName test.FullName
          match Map.tryFind methodName locationsByFuncName with
          | Some locs when locs.Length = 1 ->
            { test with Origin = TestOrigin.SourceMapped(locs.[0].FilePath, locs.[0].Line) }
          | Some locs ->
            // Multiple locations share this function name — disambiguate by module name
            let moduleName = extractModuleName test.FullName
            let matchingLoc =
              match moduleName with
              | Some modName ->
                locs |> Array.tryFind (fun loc ->
                  let fileName = System.IO.Path.GetFileNameWithoutExtension(loc.FilePath)
                  fileName = modName || fileName.EndsWith(modName))
              | None -> None
            let loc =
              matchingLoc
              |> Option.orElseWith (fun () ->
                locs |> Array.tryFind (fun loc -> attributeMatchesFramework test.Framework loc.AttributeName))
              |> Option.orElseWith (fun () -> locs |> Array.tryHead)
            match loc with
            | Some l -> { test with Origin = TestOrigin.SourceMapped(l.FilePath, l.Line) }
            | None -> test
          | None -> test)

  /// Map ReflectionOnly tests to source files using project file names.
  /// Matches the module name from the test FullName to file names in the project.
  /// E.g. "SageFs.Tests.McpAdapterTests.tests/..." → "McpAdapterTests.fs"
  let mapFromProjectFiles
    (sourceFiles: string array)
    (tests: TestCase array)
    : TestCase array =
    match Array.isEmpty sourceFiles with
    | true -> tests
    | false ->
      let filesByName =
        sourceFiles
        |> Array.map (fun f -> System.IO.Path.GetFileNameWithoutExtension(f), f)
        |> Map.ofArray
      tests |> Array.map (fun test ->
        match test.Origin with
        | TestOrigin.SourceMapped _ -> test
        | TestOrigin.ReflectionOnly ->
          match extractModuleName test.FullName with
          | Some modName ->
            match Map.tryFind modName filesByName with
            | Some filePath -> { test with Origin = TestOrigin.SourceMapped(filePath, 1) }
            | None -> test
          | None -> test)

module CoverageProjection =
  let symbolCoverage
    (graph: TestDependencyGraph)
    (results: Map<TestId, TestRunResult>)
    (symbol: string)
    : CoverageStatus =
    match Map.tryFind symbol graph.TransitiveCoverage with
    | None -> CoverageStatus.NotCovered
    | Some tests when Array.isEmpty tests -> CoverageStatus.NotCovered
    | Some tests ->
      let health =
        let allPass =
          tests |> Array.forall (fun tid ->
            match Map.tryFind tid results with
            | Some r ->
              match r.Result with
              | TestResult.Passed _ -> true
              | _ -> false
            | None -> false)
        match allPass with
        | true -> CoverageHealth.AllPassing
        | false -> CoverageHealth.SomeFailing
      CoverageStatus.Covered (tests.Length, health)

  let computeAll
    (graph: TestDependencyGraph)
    (results: Map<TestId, TestRunResult>)
    : Map<string, CoverageStatus> =
    graph.TransitiveCoverage
    |> Map.map (fun symbol tests ->
      match Array.isEmpty tests with
      | true -> CoverageStatus.NotCovered
      | false ->
        let health =
          let allPass =
            tests |> Array.forall (fun tid ->
              match Map.tryFind tid results with
              | Some r ->
                match r.Result with
                | TestResult.Passed _ -> true
                | _ -> false
              | None -> false)
          match allPass with
          | true -> CoverageHealth.AllPassing
          | false -> CoverageHealth.SomeFailing
        CoverageStatus.Covered (tests.Length, health))

module CoverageComputation =
  let computeLineCoverage (state: CoverageState) (file: string) (line: int) : LineCoverage =
    let matching =
      state.Slots
      |> Array.mapi (fun i slot -> (slot, state.Hits.[i]))
      |> Array.filter (fun (slot, _) -> slot.File = file && slot.Line = line)
    match Array.isEmpty matching with
    | true -> LineCoverage.NotCovered
    | false ->
      let total = matching.Length
      let covered = matching |> Array.filter snd |> Array.length
      match covered = total, covered = 0 with
      | true, _ -> LineCoverage.FullyCovered
      | _, true -> LineCoverage.NotCovered
      | false, false -> LineCoverage.PartiallyCovered (covered, total)

/// Per-test coverage correlation: which specific tests cover a given symbol or line
module CoverageCorrelation =
  let testsForSymbol
    (graph: TestDependencyGraph)
    (discoveredTests: TestCase array)
    (results: Map<TestId, TestRunResult>)
    (symbol: string)
    : CoverageDetail =
    match Map.tryFind symbol graph.TransitiveCoverage with
    | None -> CoverageDetail.NotCovered
    | Some testIds when Array.isEmpty testIds -> CoverageDetail.NotCovered
    | Some testIds ->
      let testLookup = discoveredTests |> Array.map (fun tc -> tc.Id, tc) |> Map.ofArray
      let infos =
        testIds |> Array.map (fun tid ->
          let name =
            match Map.tryFind tid testLookup with
            | Some tc -> tc.DisplayName
            | None -> TestId.value tid
          let result =
            match Map.tryFind tid results with
            | Some r -> Some r.Result
            | None -> None
          { TestId = tid; DisplayName = name; Result = result })
      CoverageDetail.Covered infos

  let testsForLine
    (annotations: CoverageAnnotation array)
    (graph: TestDependencyGraph)
    (discoveredTests: TestCase array)
    (results: Map<TestId, TestRunResult>)
    (file: string)
    (line: int)
    : CoverageDetail =
    annotations
    |> Array.tryFind (fun a -> a.FilePath = file && a.DefinitionLine = line)
    |> function
       | None -> CoverageDetail.NotCovered
       | Some ann -> testsForSymbol graph discoveredTests results ann.Symbol

module TestCycleTiming =
  let treeSitterMs (t: TestCycleTiming) : float =
    match t.Depth with
    | TestCycleDepth.TreeSitterOnly ts -> ts.TotalMilliseconds
    | TestCycleDepth.ThroughFcs (ts, _) -> ts.TotalMilliseconds
    | TestCycleDepth.ThroughExecution (ts, _, _) -> ts.TotalMilliseconds

  let fcsMs (t: TestCycleTiming) : float =
    match t.Depth with
    | TestCycleDepth.TreeSitterOnly _ -> 0.0
    | TestCycleDepth.ThroughFcs (_, fcs) -> fcs.TotalMilliseconds
    | TestCycleDepth.ThroughExecution (_, fcs, _) -> fcs.TotalMilliseconds

  let executionMs (t: TestCycleTiming) : float =
    match t.Depth with
    | TestCycleDepth.ThroughExecution (_, _, exec) -> exec.TotalMilliseconds
    | _ -> 0.0

  let totalMs (t: TestCycleTiming) : float =
    treeSitterMs t + fcsMs t + executionMs t

  /// Extract tree-sitter elapsed from any test cycle depth.
  let accumulatedTsElapsed (t: TestCycleTiming option) : System.TimeSpan =
    match t with
    | Some timing ->
      match timing.Depth with
      | TestCycleDepth.TreeSitterOnly ts
      | TestCycleDepth.ThroughFcs (ts, _)
      | TestCycleDepth.ThroughExecution (ts, _, _) -> ts
    | None -> System.TimeSpan.Zero

  /// Extract FCS elapsed from test cycle depth (Zero if FCS hasn't run).
  let accumulatedFcsElapsed (t: TestCycleTiming option) : System.TimeSpan =
    match t with
    | Some timing ->
      match timing.Depth with
      | TestCycleDepth.ThroughFcs (_, fcs)
      | TestCycleDepth.ThroughExecution (_, fcs, _) -> fcs
      | _ -> System.TimeSpan.Zero
    | None -> System.TimeSpan.Zero

  let toStatusBar (t: TestCycleTiming) : string =
    match t.Depth with
    | TestCycleDepth.TreeSitterOnly ts ->
      sprintf "TS:%.1fms" ts.TotalMilliseconds
    | TestCycleDepth.ThroughFcs (ts, fcs) ->
      sprintf "TS:%.1fms | FCS:%.0fms" ts.TotalMilliseconds fcs.TotalMilliseconds
    | TestCycleDepth.ThroughExecution (ts, fcs, exec) ->
      sprintf "TS:%.1fms | FCS:%.0fms | Run:%.0fms (%d)"
        ts.TotalMilliseconds fcs.TotalMilliseconds exec.TotalMilliseconds t.AffectedTests

module TestProviderDescriptions =
  let builtInDescriptions : ProviderDescription list = [
    ProviderDescription.AttributeBased {
      Name = TestFramework.XUnit; TestAttributes = ["Fact"; "Theory"]
      AssemblyMarker = "xunit.core"
    }
    ProviderDescription.AttributeBased {
      Name = TestFramework.XUnit; TestAttributes = ["Fact"; "Theory"]
      AssemblyMarker = "xunit.v3.core"
    }
    ProviderDescription.AttributeBased {
      Name = TestFramework.NUnit; TestAttributes = ["Test"; "TestCase"; "TestCaseSource"]
      AssemblyMarker = "nunit.framework"
    }
    ProviderDescription.AttributeBased {
      Name = TestFramework.MSTest; TestAttributes = ["TestMethod"; "DataTestMethod"]
      AssemblyMarker = "Microsoft.VisualStudio.TestPlatform.TestFramework"
    }
    ProviderDescription.AttributeBased {
      Name = TestFramework.TUnit; TestAttributes = ["Test"]
      AssemblyMarker = "TUnit.Core"
    }
    ProviderDescription.Custom {
      Name = TestFramework.Expecto; AssemblyMarker = "Expecto"
    }
  ]

  let detectProviders
    (assemblies: AssemblyInfo list)
    : ProviderDescription list =
    let refNames =
      assemblies
      |> List.collect (fun asm ->
        asm.ReferencedAssemblies
        |> Array.map (fun r -> r.Name)
        |> Array.toList)
      |> Set.ofList
    builtInDescriptions
    |> List.filter (fun desc ->
      let marker =
        match desc with
        | ProviderDescription.AttributeBased d -> d.AssemblyMarker
        | ProviderDescription.Custom d -> d.AssemblyMarker
      Set.contains marker refNames)

// --- Policy Filter ---

module PolicyFilter =
  let shouldRun (policy: RunPolicy) (trigger: RunTrigger) : bool =
    match policy, trigger with
    | RunPolicy.Disabled, _ -> false
    | RunPolicy.OnEveryChange, _ -> true
    | RunPolicy.OnSaveOnly, RunTrigger.FileSave -> true
    | RunPolicy.OnSaveOnly, RunTrigger.ExplicitRun -> true
    | RunPolicy.OnSaveOnly, RunTrigger.Keystroke -> false
    | RunPolicy.OnDemand, RunTrigger.ExplicitRun -> true
    | RunPolicy.OnDemand, _ -> false

  let filterTests
    (policies: Map<TestCategory, RunPolicy>)
    (trigger: RunTrigger)
    (tests: TestCase array)
    : TestCase array =
    tests
    |> Array.filter (fun tc ->
      let policy =
        policies
        |> Map.tryFind tc.Category
        |> Option.defaultValue RunPolicy.OnEveryChange
      shouldRun policy trigger)

// --- Staleness Tracking ---

module Staleness =
  let markStale
    (graph: TestDependencyGraph)
    (changedSymbols: string list)
    (state: LiveTestState)
    : LiveTestState =
    let affected = TestDependencyGraph.findAffected changedSymbols graph |> Set.ofArray
    match Set.isEmpty affected with
    | true -> state
    | false ->
      let previousStatuses =
        LiveTestState.orderedStatusEntries state
        |> Array.map (fun e -> e.TestId, e.Status)
        |> Map.ofArray
      let updatedState = { state with AffectedTests = Set.union state.AffectedTests affected }
      LiveTestState.withStatusEntries
        (LiveTesting.computeStatusEntriesWithHistory previousStatuses updatedState)
        updatedState

// --- Test Cycle Orchestrator ---

[<RequireQualifiedAccess>]
type TestCycleDecision =
  | Skip of reason: string
  | TreeSitterOnly
  | FullCycle of affectedTestIds: TestId array

/// Context for test prioritization including coverage weights and flaky classifications.
type PrioritizationContext = {
  LastResults: Map<TestId, TestRunResult>
  /// Coverage weight per test — popCount of intersection between test bitmap and changed-file mask.
  /// Higher values mean the test covers more probes in the changed file(s).
  CoverageWeights: Map<TestId, int>
  /// Flaky classifications per test — used to demote environmentally flaky failures.
  FlakyClassifications: Map<TestId, FlakyClassification>
}

module PrioritizationContext =
  let empty = {
    LastResults = Map.empty
    CoverageWeights = Map.empty
    FlakyClassifications = Map.empty
  }

  let fromLastResults (lastResults: Map<TestId, TestRunResult>) = {
    empty with LastResults = lastResults
  }

module TestPrioritization =
  let durationMs (r: TestResult) =
    match r with
    | TestResult.Passed d -> d.TotalMilliseconds
    | TestResult.Failed (_, d) -> d.TotalMilliseconds
    | TestResult.Skipped _ -> 0.0
    | TestResult.NotRun -> 0.0

  /// Compute the prioritization tier for a test, accounting for flaky demotion.
  /// Environmentally flaky failures are demoted from tier 0 to tier 2 (same as passed)
  /// so they don't steal attention from honest failures.
  let computeTier
    (flakyClassifications: Map<TestId, FlakyClassification>)
    (testId: TestId)
    (result: TestResult)
    : int =
    match result with
    | TestResult.Failed _ ->
      match Map.tryFind testId flakyClassifications with
      | Some (FlakyClassification.Environmental _) -> 2
      | _ -> 0
    | TestResult.Passed _ -> 2
    | TestResult.Skipped _ -> 3
    | TestResult.NotRun -> 4

  /// Build the lexicographic sort key: (tier, -coverageWeight, durationMs).
  /// Negated coverage weight ensures higher coverage sorts first within the same tier.
  let buildSortKey (ctx: PrioritizationContext) (tc: TestCase) : int * int * float =
    match Map.tryFind tc.Id ctx.LastResults with
    | Some result ->
      let tier = computeTier ctx.FlakyClassifications tc.Id result.Result
      let coverageWeight =
        -(ctx.CoverageWeights |> Map.tryFind tc.Id |> Option.defaultValue 0)
      (tier, coverageWeight, durationMs result.Result)
    | None -> (1, 0, 0.0)

  /// Sort tests: failed → new/unknown → fast-passed → slow-passed → skipped → not-run.
  /// Within each tier, tests covering more of the changed file run first.
  /// Within same coverage weight, fastest tests come first.
  /// Environmentally flaky failures are demoted to the passed tier.
  let prioritizeWithContext (ctx: PrioritizationContext) (tests: TestCase array) : TestCase array =
    tests |> Array.sortBy (buildSortKey ctx)

  /// Legacy overload for backward compatibility.
  /// Sort tests: failed → new/unknown → fast-passed → slow-passed → skipped → not-run.
  /// Within each tier, fastest tests come first.
  let prioritize (lastResults: Map<TestId, TestRunResult>) (tests: TestCase array) : TestCase array =
    prioritizeWithContext (PrioritizationContext.fromLastResults lastResults) tests

module TestCycleOrchestrator =
  let decide
    (state: LiveTestState)
    (trigger: RunTrigger)
    (changedSymbols: string list)
    (depGraph: TestDependencyGraph)
    : TestCycleDecision =
    match state.Activation = LiveTestingActivation.Inactive,
          TestRunPhase.isAnyRunning state.RunPhases,
          Array.isEmpty state.DiscoveredTests with
    | true, _, _ ->
      TestCycleDecision.Skip "Live testing disabled"
    | _, true, _ ->
      TestCycleDecision.Skip "Cycle already running"
    | _, _, true ->
      TestCycleDecision.TreeSitterOnly
    | false, false, false ->
      let affected = TestDependencyGraph.findAffected changedSymbols depGraph
      let affectedSet = Set.ofArray affected
      let filtered =
        state.DiscoveredTests
        |> Array.filter (fun tc -> affectedSet.Contains tc.Id)
        |> LiveTesting.filterByPolicy state.RunPolicies trigger
      match Array.isEmpty filtered with
      | true -> TestCycleDecision.TreeSitterOnly
      | false ->
        TestCycleDecision.FullCycle (filtered |> Array.map (fun tc -> tc.Id))

  let buildRunBatch
    (state: LiveTestState)
    (testIds: TestId array)
    : TestCase array =
    let idSet = testIds |> Set.ofArray
    state.DiscoveredTests
    |> Array.filter (fun tc -> Set.contains tc.Id idSet)

// --- Debounce + Cancellation (Phase 4 as-you-type) ---

type DebouncedOp<'a> = {
  Payload: 'a
  RequestedAt: DateTimeOffset
  DelayMs: int<ms>
  Generation: int64
}

type DebounceChannel<'a> = {
  CurrentGeneration: int64
  Pending: DebouncedOp<'a> option
  LastCompleted: DateTimeOffset option
}

module DebounceChannel =
  let empty<'a> : DebounceChannel<'a> = {
    CurrentGeneration = 0L
    Pending = None
    LastCompleted = None
  }

  let submit (payload: 'a) (delayMs: int<ms>) (now: DateTimeOffset) (ch: DebounceChannel<'a>) =
    let gen = ch.CurrentGeneration + 1L
    { ch with
        CurrentGeneration = gen
        Pending = Some { Payload = payload; RequestedAt = now; DelayMs = delayMs; Generation = gen } }

  let tryFire (now: DateTimeOffset) (ch: DebounceChannel<'a>) =
    match ch.Pending with
    | None -> None, ch
    | Some op ->
      let elapsed = toMs (now - op.RequestedAt)
      match op.Generation < ch.CurrentGeneration, elapsed >= float op.DelayMs * 1.0<ms> with
      | true, _ ->
        None, { ch with Pending = None }
      | _, true ->
        Some op.Payload, { ch with Pending = None; LastCompleted = Some now }
      | false, false ->
        None, ch

  let isStale (ch: DebounceChannel<'a>) =
    match ch.Pending with
    | Some op -> op.Generation < ch.CurrentGeneration
    | None -> false

type TestCycleDebounce = {
  TreeSitter: DebounceChannel<string>
  Fcs: DebounceChannel<string>
}

module TestCycleDebounce =
  let empty = {
    TreeSitter = DebounceChannel.empty
    Fcs = DebounceChannel.empty
  }

  let treeSitterDelayMs = 50<ms>

  let onKeystroke (content: string) (filePath: string) (fcsDelay: int<ms>) (now: DateTimeOffset) (db: TestCycleDebounce) =
    { db with
        TreeSitter = db.TreeSitter |> DebounceChannel.submit content treeSitterDelayMs now
        Fcs = db.Fcs |> DebounceChannel.submit filePath fcsDelay now }

  let onFileSave (filePath: string) (now: DateTimeOffset) (db: TestCycleDebounce) =
    { db with
        Fcs = db.Fcs |> DebounceChannel.submit filePath 50<ms> now }

  let tick (now: DateTimeOffset) (db: TestCycleDebounce) =
    let tsPayload, tsChannel = DebounceChannel.tryFire now db.TreeSitter
    let fcsPayload, fcsChannel = DebounceChannel.tryFire now db.Fcs
    // Return same reference when nothing changed (enables model reference equality skip)
    let db' =
      match obj.ReferenceEquals(tsChannel, db.TreeSitter) && obj.ReferenceEquals(fcsChannel, db.Fcs) with
      | true -> db
      | false -> { db with TreeSitter = tsChannel; Fcs = fcsChannel }
    (tsPayload, fcsPayload), db'

[<RequireQualifiedAccess>]
type TestCycleEffect =
  | RequestInitialDiscovery
  | ParseTreeSitter of content: string * filePath: string
  | RequestFcsTypeCheck of filePath: string * treeSitterElapsed: System.TimeSpan
  | RunAffectedTests of tests: TestCase array * trigger: RunTrigger * treeSitterElapsed: System.TimeSpan * fcsElapsed: System.TimeSpan * sessionId: string option * instrumentationMaps: InstrumentationMap array
  | RequestRebuild of tests: TestCase array * trigger: RunTrigger * treeSitterElapsed: System.TimeSpan * fcsElapsed: System.TimeSpan * sessionId: string option * instrumentationMaps: InstrumentationMap array

module TestCycleEffects =
  let fromTick
    (tsPayload: string option)
    (fcsPayload: string option)
    (filePath: string)
    (lastTiming: TestCycleTiming option)
    : TestCycleEffect list =
    [ match tsPayload with
      | Some content -> TestCycleEffect.ParseTreeSitter(content, filePath)
      | None -> ()
      match fcsPayload with
      | Some fp ->
        let tsElapsed = TestCycleTiming.accumulatedTsElapsed lastTiming
        TestCycleEffect.RequestFcsTypeCheck(fp, tsElapsed)
      | None -> () ]

  let afterTypeCheck
    (changedSymbols: string list)
    (changedFilePath: string)
    (trigger: RunTrigger)
    (depGraph: TestDependencyGraph)
    (state: LiveTestState)
    (lastTiming: TestCycleTiming option)
    (instrumentationMaps: Map<string, InstrumentationMap array>)
    : TestCycleEffect list =
    match state.Activation = LiveTestingActivation.Inactive with
    | true -> []
    | false ->
      let symbolAffected = TestDependencyGraph.findAffected changedSymbols depGraph
      // Compute coverage-affected across all sessions' maps
      let hasMaps = not (Map.isEmpty instrumentationMaps)
      let hasBitmaps = not (Map.isEmpty state.TestCoverageBitmaps)
      let coverageAffected =
        match hasMaps && hasBitmaps with
        | true ->
          instrumentationMaps
          |> Map.toArray
          |> Array.collect (fun (_, maps) ->
            CoverageBitmap.findCoverageAffected changedFilePath maps state.TestCoverageBitmaps)
        | false -> [||]
      let affected =
        Array.append symbolAffected coverageAffected |> Array.distinct
      // For compiled projects, when dep graph/coverage can't identify specific
      // affected tests, fall back to ALL discovered tests. The DLL is stale
      // and needs rebuilding regardless — we just can't narrow the test set.
      let isCompiledFile =
        changedFilePath.EndsWith(".fs", System.StringComparison.OrdinalIgnoreCase)
        && not (changedFilePath.EndsWith(".fsx", System.StringComparison.OrdinalIgnoreCase))
      // Fall back to all discovered tests when:
      // 1. changedSymbols changed but dep graph doesn't cover them (new code/not-yet-tracked symbols)
      // 2. FileSave on a compiled file where the dep graph is empty — FCS can't see main project
      //    symbols from the test session, so changedSymbols=[] even though the DLL is stale.
      // Do NOT fall back when:
      // - changedSymbols=[] on Keystroke (intermediate state while typing)
      // - changedSymbols=[] on FileSave but dep graph is non-empty (whitespace/comment edit to a
      //   file the dep graph covers — FCS correctly reports no semantic change)
      let isEmptyDepGraph =
        Map.isEmpty depGraph.SymbolToTests && Map.isEmpty depGraph.TransitiveCoverage
      let isSaveOrExplicit =
        trigger = RunTrigger.FileSave || trigger = RunTrigger.ExplicitRun
      let symbolsChanged = not (List.isEmpty changedSymbols)
      let effectiveAffected =
        match Array.isEmpty affected && isCompiledFile && (symbolsChanged || (isSaveOrExplicit && isEmptyDepGraph)) with
        | true -> state.DiscoveredTests |> Array.map (fun tc -> tc.Id)
        | false -> affected
      match Array.isEmpty effectiveAffected with
      | true -> []
      | false ->
        let affectedSet = Set.ofArray effectiveAffected
        let affectedTests =
          state.DiscoveredTests
          |> Array.filter (fun tc -> affectedSet.Contains tc.Id)
        let filtered =
          let allMaps =
            instrumentationMaps |> Map.values |> Seq.collect id |> Array.ofSeq
          let coverageWeights =
            CoverageBitmap.computeCoverageWeights [changedFilePath] allMaps state.TestCoverageBitmaps
          let flakyClassifications =
            affectedTests
            |> Array.choose (fun tc ->
              let c = FlakyDetection.classifyFlakiness tc.Id state.FlakyHistory state.LastResults
              match c with
              | FlakyClassification.Insufficient | FlakyClassification.Stable -> None
              | _ -> Some (tc.Id, c))
            |> Map.ofArray
          let ctx : PrioritizationContext = {
            LastResults = state.LastResults
            CoverageWeights = coverageWeights
            FlakyClassifications = flakyClassifications
          }
          PolicyFilter.filterTests state.RunPolicies trigger affectedTests
          |> TestPrioritization.prioritizeWithContext ctx
        match Array.isEmpty filtered with
        | true -> []
        | false ->
          let tsElapsed = TestCycleTiming.accumulatedTsElapsed lastTiming
          let fcsElapsed = TestCycleTiming.accumulatedFcsElapsed lastTiming
          // Group affected tests by session, emit one effect per session
          filtered
          |> Array.groupBy (fun tc ->
            match Map.tryFind tc.Id state.TestSessionMap with
            | Some sid -> sid
            | None -> "")
          |> Array.toList
          |> List.choose (fun (sid, groupTests) ->
            let targetSession = if System.String.IsNullOrEmpty sid then None else Some sid
            match TestRunPhase.isSessionRunning targetSession state.RunPhases with
            | true -> None
            | false ->
              let sessionMaps =
                match targetSession |> Option.bind (fun s -> Map.tryFind s instrumentationMaps) with
                | Some maps -> maps
                | None -> instrumentationMaps |> Map.values |> Seq.collect id |> Array.ofSeq
              let isCompiled =
                changedFilePath.EndsWith(".fs", System.StringComparison.OrdinalIgnoreCase)
                && not (changedFilePath.EndsWith(".fsx", System.StringComparison.OrdinalIgnoreCase))
              match isCompiled with
              | true -> Some (TestCycleEffect.RequestRebuild(groupTests, trigger, tsElapsed, fcsElapsed, targetSession, sessionMaps))
              | false -> Some (TestCycleEffect.RunAffectedTests(groupTests, trigger, tsElapsed, fcsElapsed, targetSession, sessionMaps)))

/// Adaptive debounce configuration.
type AdaptiveDebounceConfig = {
  BaseTreeSitterMs: float<ms>
  BaseFcsMs: float<ms>
  MaxFcsMs: float<ms>
  BackoffMultiplier: float
  ResetAfterSuccessCount: int
}

module AdaptiveDebounceConfig =
  let defaults = {
    BaseTreeSitterMs = 50.0<ms>
    BaseFcsMs = 300.0<ms>
    MaxFcsMs = 2000.0<ms>
    BackoffMultiplier = 1.5
    ResetAfterSuccessCount = 3
  }

/// Tracks FCS cancellation history for adaptive backoff.
type AdaptiveDebounce = {
  Config: AdaptiveDebounceConfig
  ConsecutiveFcsCancels: int
  ConsecutiveFcsSuccesses: int
  CurrentFcsDelayMs: float<ms>
}

module AdaptiveDebounce =
  let create (config: AdaptiveDebounceConfig) = {
    Config = config
    ConsecutiveFcsCancels = 0
    ConsecutiveFcsSuccesses = 0
    CurrentFcsDelayMs = config.BaseFcsMs
  }

  let createDefault () = create AdaptiveDebounceConfig.defaults

  let onFcsCanceled (ad: AdaptiveDebounce) =
    let newCancels = ad.ConsecutiveFcsCancels + 1
    let newDelay =
      ad.CurrentFcsDelayMs * ad.Config.BackoffMultiplier
      |> min ad.Config.MaxFcsMs
    { ad with
        ConsecutiveFcsCancels = newCancels
        ConsecutiveFcsSuccesses = 0
        CurrentFcsDelayMs = newDelay }

  let onFcsCompleted (ad: AdaptiveDebounce) =
    let newSuccesses = ad.ConsecutiveFcsSuccesses + 1
    match newSuccesses >= ad.Config.ResetAfterSuccessCount with
    | true ->
      { ad with
          ConsecutiveFcsCancels = 0
          ConsecutiveFcsSuccesses = 0
          CurrentFcsDelayMs = ad.Config.BaseFcsMs }
    | false ->
      { ad with
          ConsecutiveFcsCancels = 0
          ConsecutiveFcsSuccesses = newSuccesses }

  let currentFcsDelay (ad: AdaptiveDebounce) = ad.CurrentFcsDelayMs

/// Represents a resolved symbol reference from FCS.
type SymbolReference = {
  SymbolFullName: string
  UseKind: SymbolUseKind
  UsedInTestId: TestId option
  FilePath: string
  Line: int
}

/// Builds SymbolToTests from a list of symbol references using the line-range heuristic
/// in buildFromSymbolUses. Converts SymbolReference to ExtractedSymbolUse first.
module SymbolGraphBuilder =
  let toExtractedSymbolUse (sr: SymbolReference) : ExtractedSymbolUse =
    let parts = sr.SymbolFullName.Split('.')
    let displayName = if parts.Length > 0 then parts[parts.Length - 1] else sr.SymbolFullName
    { FullName = sr.SymbolFullName
      DisplayName = displayName
      UseKind = sr.UseKind
      StartLine = sr.Line
      EndLine = sr.Line }

  let buildIndex (testModuleIdentifier: string) (framework: TestFramework) (refs: SymbolReference list) : Map<string, TestId array> =
    let extracted = refs |> List.map toExtractedSymbolUse |> Array.ofList
    let graph = TestDependencyGraph.buildFromSymbolUses testModuleIdentifier framework extracted
    graph.SymbolToTests

  let updateGraph (testModuleIdentifier: string) (framework: TestFramework) (newRefs: SymbolReference list) (filePath: string) (graph: TestDependencyGraph) : TestDependencyGraph =
    let extracted = newRefs |> List.map toExtractedSymbolUse |> Array.ofList
    let newFileIndex = buildIndex testModuleIdentifier framework newRefs
    let updatedPerFile = Map.add filePath newFileIndex graph.PerFileIndex
    let merged = TestDependencyGraph.mergePerFileIndexes updatedPerFile
    let callGraph = TestDependencyGraph.extractCallGraph testModuleIdentifier extracted
    { graph with
        SymbolToTests = merged
        TransitiveCoverage = TestDependencyGraph.computeTransitiveCoverage callGraph merged
        PerFileIndex = updatedPerFile
        SourceVersion = graph.SourceVersion + 1 }

/// Result of comparing two sets of symbols — separates added from removed.
type SymbolChanges = {
  Added: string list
  Removed: string list
}

module SymbolChanges =
  let empty = { Added = []; Removed = [] }
  let allChanged (sc: SymbolChanges) = sc.Added @ sc.Removed
  let isEmpty (sc: SymbolChanges) = List.isEmpty sc.Added && List.isEmpty sc.Removed

/// Detects which symbols changed between two FCS analysis runs.
module SymbolDiff =
  let computeChanges (previousSymbols: Set<string>) (currentSymbols: Set<string>) : SymbolChanges =
    { Added = Set.difference currentSymbols previousSymbols |> Set.toList
      Removed = Set.difference previousSymbols currentSymbols |> Set.toList }

  let fromRefs (previousRefs: SymbolReference list) (currentRefs: SymbolReference list) : SymbolChanges =
    let prevSyms = previousRefs |> List.map (fun r -> r.SymbolFullName) |> Set.ofList
    let currSyms = currentRefs |> List.map (fun r -> r.SymbolFullName) |> Set.ofList
    computeChanges prevSyms currSyms

/// Caches per-file symbol analysis for efficient diff computation.
type FileAnalysisCache = {
  FileSymbols: Map<string, SymbolReference list>
}

module FileAnalysisCache =
  let empty = { FileSymbols = Map.empty }

  let symbolNames (refs: SymbolReference list) =
    refs |> List.map (fun r -> r.SymbolFullName) |> Set.ofList

  let update (filePath: string) (newRefs: SymbolReference list) (cache: FileAnalysisCache) : SymbolChanges * FileAnalysisCache =
    let prevNames =
      cache.FileSymbols
      |> Map.tryFind filePath
      |> Option.defaultValue []
      |> symbolNames
    let newNames = symbolNames newRefs
    let changes = SymbolDiff.computeChanges prevNames newNames
    let newCache = { FileSymbols = Map.add filePath newRefs cache.FileSymbols }
    changes, newCache

  let getFileSymbols (filePath: string) (cache: FileAnalysisCache) =
    cache.FileSymbols |> Map.tryFind filePath |> Option.defaultValue []

/// Result of an FCS type-check request.
/// Success carries the file and symbol references for dependency graph updates.
/// Failed carries diagnostics. Cancelled means the check was superseded.
[<RequireQualifiedAccess>]
type FcsTypeCheckResult =
  | Success of filePath: string * refs: SymbolReference list
  | Failed of filePath: string * errors: string list
  | Cancelled of filePath: string

type PendingRebuildState = {
  Tests: TestCase array
  Trigger: RunTrigger
  TreeSitterElapsed: System.TimeSpan
  FcsElapsed: System.TimeSpan
  SessionId: string option
  InstrumentationMaps: InstrumentationMap array
}

/// Wraps LiveTestState + TestCycleDebounce + TestDependencyGraph + adaptive
/// debounce + file analysis cache into a single state record for the Elm loop.
type LiveTestCycleState = {
  TestState: LiveTestState
  Debounce: TestCycleDebounce
  DepGraph: TestDependencyGraph
  ActiveFile: string option
  ChangedSymbols: string list
  LastTreeSitterResult: SourceTestLocation array option
  AnalysisCache: FileAnalysisCache
  AdaptiveDebounce: AdaptiveDebounce
  LastTrigger: RunTrigger
  LastTiming: TestCycleTiming option
  /// IL coverage instrumentation maps per session (sessionId → maps for that session's assemblies).
  InstrumentationMaps: Map<string, InstrumentationMap array>
  PendingRebuild: PendingRebuildState option
}

module LiveTestCycleState =
  let empty = {
    TestState = LiveTestState.empty
    Debounce = TestCycleDebounce.empty
    DepGraph = TestDependencyGraph.empty
    ActiveFile = None
    ChangedSymbols = []
    LastTreeSitterResult = None
    AnalysisCache = FileAnalysisCache.empty
    AdaptiveDebounce = AdaptiveDebounce.createDefault()
    LastTrigger = RunTrigger.Keystroke
    LastTiming = None
    InstrumentationMaps = Map.empty
    PendingRebuild = None
  }

  let liveTestingStatusBarForSession (activeSessionId: string) (state: LiveTestCycleState) : string =
    let timing =
      match state.LastTiming with
      | None -> ""
      | Some t -> TestCycleTiming.toStatusBar t
    let entries = LiveTestState.statusEntriesForSession activeSessionId state.TestState
    let statuses = entries |> Array.map (fun e -> e.Status)
    let summary = TestSummary.fromStatuses state.TestState.Activation statuses |> TestSummary.toStatusBar
    match timing, summary with
    | "", "Tests: none" -> ""
    | "", s -> s
    | t, "Tests: none" -> t
    | t, s -> sprintf "%s | %s" t s

  let liveTestingStatusBar (state: LiveTestCycleState) : string =
    liveTestingStatusBarForSession "" state

  let currentFcsDelay (s: LiveTestCycleState) =
    AdaptiveDebounce.currentFcsDelay s.AdaptiveDebounce

  let onKeystroke (content: string) (filePath: string) (now: DateTimeOffset) (s: LiveTestCycleState) =
    let fcsDelay = int (currentFcsDelay s / 1.0<ms>) * 1<ms>
    let db = s.Debounce |> TestCycleDebounce.onKeystroke content filePath fcsDelay now
    // When edits arrive while tests are running, mark phase as edited so in-flight results are stale.
    let ts = { s.TestState with RunPhases = s.TestState.RunPhases |> Map.map (fun _ p -> TestRunPhase.onEdit p) }
    { s with Debounce = db; TestState = ts; ActiveFile = Some filePath; LastTrigger = RunTrigger.Keystroke }

  let onFileSave (filePath: string) (now: DateTimeOffset) (s: LiveTestCycleState) =
    let db = s.Debounce |> TestCycleDebounce.onFileSave filePath now
    let ts = { s.TestState with RunPhases = s.TestState.RunPhases |> Map.map (fun _ p -> TestRunPhase.onEdit p) }
    { s with Debounce = db; TestState = ts; ActiveFile = Some filePath; LastTrigger = RunTrigger.FileSave }

  let onFcsComplete (filePath: string) (refs: SymbolReference list) (s: LiveTestCycleState) =
    let changes, newCache = FileAnalysisCache.update filePath refs s.AnalysisCache
    let newDepGraph =
      SymbolGraphBuilder.updateGraph
        LiveTestingDefaults.TestModuleIdentifier
        LiveTestingDefaults.Framework
        refs filePath s.DepGraph
    let newDebounce = AdaptiveDebounce.onFcsCompleted s.AdaptiveDebounce
    { s with
        DepGraph = newDepGraph
        ChangedSymbols = SymbolChanges.allChanged changes
        AnalysisCache = newCache
        AdaptiveDebounce = newDebounce }

  let onFcsCanceled (s: LiveTestCycleState) =
    { s with AdaptiveDebounce = AdaptiveDebounce.onFcsCanceled s.AdaptiveDebounce }

  /// Ticks the debounce channels and produces test cycle effects.
  /// Returns (effects, updatedState).
  /// Note: afterTypeCheck is NOT called here — it runs after FCS completes,
  /// not when the FCS request is emitted (avoids stale symbol data).
  let tick (now: DateTimeOffset) (s: LiveTestCycleState) =
    match s.ActiveFile with
    | None -> [], s
    | Some filePath ->
      let (tsPayload, fcsPayload), db = s.Debounce |> TestCycleDebounce.tick now
      let effects = TestCycleEffects.fromTick tsPayload fcsPayload filePath s.LastTiming
      // Return same reference when debounce didn't change (no-op tick)
      let s' =
        match obj.ReferenceEquals(db, s.Debounce) with
        | true -> s
        | false -> { s with Debounce = db }
      effects, s'

  /// Handles an FCS type-check result: updates state and produces effects.
  /// Success: updates symbol graph, analysis cache, adaptive debounce, then
  /// calls afterTypeCheck to emit RunAffectedTests if symbols changed.
  /// Failed: no-op (diagnostics handled elsewhere).
  /// Cancelled: updates adaptive debounce backoff.
  let handleFcsResult
    (result: FcsTypeCheckResult)
    (s: LiveTestCycleState)
    : TestCycleEffect list * LiveTestCycleState =
    match result with
    | FcsTypeCheckResult.Success (filePath, refs) ->
      let s1 = onFcsComplete filePath refs s
      let trigger = s.LastTrigger
      let effects = TestCycleEffects.afterTypeCheck s1.ChangedSymbols filePath trigger s1.DepGraph s1.TestState s1.LastTiming s1.InstrumentationMaps
      // When RequestRebuild is emitted, store the pending rebuild state on the model
      // so RebuildCompleted can retrieve the test set and trigger RunAffectedTests.
      let s2 =
        effects
        |> List.tryPick (fun e ->
          match e with
          | TestCycleEffect.RequestRebuild (tests, trigger, tsElapsed, fcsElapsed, sessionId, instrMaps) ->
            Some { Tests = tests
                   Trigger = trigger
                   TreeSitterElapsed = tsElapsed
                   FcsElapsed = fcsElapsed
                   SessionId = sessionId
                   InstrumentationMaps = instrMaps }
          | _ -> None)
        |> function
          | Some pending -> { s1 with PendingRebuild = Some pending }
          | None -> s1
      effects, s2
    | FcsTypeCheckResult.Failed _ ->
      [], s
    | FcsTypeCheckResult.Cancelled _ ->
      [], onFcsCanceled s

  /// When the hot-reload hook identifies affected tests (via method name matching),
  /// look up full TestCase objects, filter by RunPolicy, and produce a RunAffectedTests
  /// effect if any tests should run. This bridges the gap between discovery and execution
  /// for MCP-triggered evals (which don't go through the FCS type-check cycle).
  let triggerExecutionForAffected
    (affectedIds: TestId array)
    (trigger: RunTrigger)
    (targetSession: string option)
    (s: LiveTestCycleState)
    : TestCycleEffect list =
    match Array.isEmpty affectedIds
          || s.TestState.Activation = LiveTestingActivation.Inactive
          || TestRunPhase.isSessionRunning targetSession s.TestState.RunPhases with
    | true -> []
    | false ->
      let affectedIdSet = Set.ofArray affectedIds
      let affectedTests =
        s.TestState.DiscoveredTests
        |> Array.filter (fun tc -> affectedIdSet.Contains tc.Id)
      let filtered =
        PolicyFilter.filterTests s.TestState.RunPolicies trigger affectedTests
        |> TestPrioritization.prioritize s.TestState.LastResults
      match Array.isEmpty filtered with
      | true -> []
      | false ->
        let sessionMaps =
          match targetSession |> Option.bind (fun sid -> Map.tryFind sid s.InstrumentationMaps) with
          | Some maps -> maps
          | None -> s.InstrumentationMaps |> Map.values |> Seq.collect id |> Array.ofSeq
        [ TestCycleEffect.RunAffectedTests(filtered, trigger, System.TimeSpan.Zero, System.TimeSpan.Zero, targetSession, sessionMaps) ]

  /// Filter tests by optional criteria for explicit MCP-triggered runs.
  /// All filters are AND'd. None = no filter = match all.
  let filterTestsForExplicitRun
    (discoveredTests: TestCase array)
    (fileFilter: string option)
    (patternFilter: string option)
    (categoryFilter: TestCategory option)
    : TestCase array =
    discoveredTests
    |> Array.filter (fun tc ->
      let matchesFile =
        match fileFilter with
        | None -> true
        | Some f ->
          match tc.Origin with
          | TestOrigin.SourceMapped (file, _) -> file = f
          | TestOrigin.ReflectionOnly -> false
      let matchesPattern =
        match patternFilter with
        | None -> true
        | Some p -> tc.FullName.Contains p || tc.DisplayName.Contains p
      let matchesCategory =
        match categoryFilter with
        | None -> true
        | Some c -> tc.Category = c
      matchesFile && matchesPattern && matchesCategory)

// ============================================================
// Inline Feedback Read Model (CQRS)
// Universal FileAnnotations projected from LiveTestState
// ============================================================

[<RequireQualifiedAccess>]
type AnnotationLayer =
  | TestStatus
  | Coverage
  | CodeLens
  | InlineFailure

[<RequireQualifiedAccess>]
type FailurePresentation =
  | AssertionDiff of expected: string * actual: string
  | ExceptionMessage of message: string * relevantFrame: string
  | Timeout of after: TimeSpan
  | RawMessage of message: string

[<RequireQualifiedAccess>]
type CodeLensCommand =
  | RunTest
  | DebugTest
  | ShowHistory

[<RequireQualifiedAccess>]
type AnnotationFreshness =
  | Current
  | Stale
  | Running

type TestLineAnnotation = {
  Line: int
  TestId: TestId
  DisplayName: string
  Status: TestRunStatus
  Freshness: AnnotationFreshness
}

type CoverageLineAnnotation = {
  Line: int
  EndLine: int
  EndColumn: int
  Detail: CoverageStatus
  CoveringTestIds: TestId array
  BranchCoverage: LineCoverage option
}

type InlineFailure = {
  Line: int
  TestId: TestId
  TestName: string
  Failure: FailurePresentation
  Duration: TimeSpan
}

type TestCodeLens = {
  Line: int
  Label: string
  TestId: TestId
  Command: CodeLensCommand
}

/// Per-cell eval performance annotation for editor gutter sparklines.
type PerformanceAnnotation = {
  /// Line in the source file where the ;; boundary begins.
  Line: int
  /// Cell index in the eval session.
  CellIndex: int
  /// Recent eval durations in ms (most recent last), up to 10 entries.
  DurationsMs: float list
  /// Sparkline string (Unicode block chars) for gutter display.
  Sparkline: string
  /// P50 of recent durations.
  P50Ms: float
  /// P95 of recent durations.
  P95Ms: float
}

type FileAnnotations = {
  FilePath: string
  TestAnnotations: TestLineAnnotation array
  CoverageAnnotations: CoverageLineAnnotation array
  InlineFailures: InlineFailure array
  CodeLenses: TestCodeLens array
  PerformanceAnnotations: PerformanceAnnotation array
}

module FailurePresentation =
  let private cleanValue (s: string) = s.TrimEnd('.', ' ')

  /// Parse "Expected: X / Actual: Y" or NUnit's "Expected: X / But was: Y"
  let private tryParseLabeledPair (msg: string) =
    let lines =
      msg.Split([| '\n'; '\r' |], StringSplitOptions.RemoveEmptyEntries)
    let findPrefixed (prefixes: string list) =
      lines
      |> Array.tryPick (fun l ->
        let t = l.TrimStart()
        prefixes
        |> List.tryPick (fun prefix ->
          match t.StartsWith(prefix + ":", StringComparison.OrdinalIgnoreCase)
                || t.StartsWith(prefix + " :", StringComparison.OrdinalIgnoreCase) with
          | true ->
            let idx = t.IndexOf(':')
            match idx >= 0 with
            | true -> Some (t.Substring(idx + 1).Trim())
            | false -> None
          | false -> None))
    match findPrefixed ["expected"], findPrefixed ["actual"; "but was"] with
    | Some e, Some a -> Some (cleanValue e, cleanValue a)
    | _ -> None

  let private comparisonPattern =
    Regex(
      @"Expected (?:actual to be |string to contain )(.+?),?\s+(?:but was|was)\s+(.+?)\.?\s*$",
      RegexOptions.Compiled ||| RegexOptions.Singleline)

  let private lengthPattern =
    Regex(@"Expected length\s+(\S+),\s*was\s+(\S+?)\.?\s*$", RegexOptions.Compiled)

  let private fsCheckPattern =
    Regex(
      @"Falsifiable.*?Original:\s*\n(.+?)(?:\nShrunk:\s*\n(.+?))?$",
      RegexOptions.Compiled ||| RegexOptions.Singleline)

  /// Parse Expecto comparison prose: "Expected actual to be greater than X, but was Y"
  let private tryParseComparisonProse (msg: string) =
    let lines = msg.Split([| '\n'; '\r' |], StringSplitOptions.RemoveEmptyEntries)
    lines
    |> Array.tryPick (fun line ->
      let m = comparisonPattern.Match(line.Trim())
      match m.Success with
      | true -> Some (cleanValue m.Groups.[1].Value, cleanValue m.Groups.[2].Value)
      | false ->
        let ml = lengthPattern.Match(line.Trim())
        match ml.Success with
        | true -> Some (sprintf "length %s" (ml.Groups.[1].Value), cleanValue ml.Groups.[2].Value)
        | false -> None)

  /// Parse FsCheck "Falsifiable" messages with counterexamples
  let private tryParseFsCheck (msg: string) =
    let m = fsCheckPattern.Match(msg)
    match m.Success with
    | true ->
      let original = m.Groups.[1].Value.Trim()
      let shrunk =
        match m.Groups.[2].Success with
        | true -> m.Groups.[2].Value.Trim()
        | false -> ""
      match String.IsNullOrEmpty shrunk with
      | true -> Some (original, "(falsified)")
      | false -> Some (original, sprintf "(shrunk to %s)" shrunk)
    | false -> None

  /// Parse assertion diffs from all major .NET test frameworks:
  /// Expecto, xUnit, NUnit (Expected/But was), FsCheck (Falsifiable)
  let tryParseAssertionDiff (msg: string) =
    match String.IsNullOrWhiteSpace msg with
    | true -> None
    | false ->
      match tryParseLabeledPair msg with
      | Some pair -> Some pair
      | None ->
        match tryParseComparisonProse msg with
        | Some pair -> Some pair
        | None -> tryParseFsCheck msg

  let firstUserFrame (trace: string) =
    let lines =
      trace.Split([| '\n'; '\r' |], StringSplitOptions.RemoveEmptyEntries)
    lines
    |> Array.tryFind (fun l ->
      l.Contains(".fs:line") || l.Contains(".cs:line"))
    |> Option.defaultWith (fun () ->
      lines |> Array.tryHead |> Option.defaultValue "")
    |> fun s -> s.Trim()

  let fromTestFailure (f: TestFailure) =
    match f with
    | TestFailure.AssertionFailed msg ->
      match tryParseAssertionDiff msg with
      | Some(e, a) -> FailurePresentation.AssertionDiff(e, a)
      | None -> FailurePresentation.RawMessage msg
    | TestFailure.ExceptionThrown(msg, st) ->
      FailurePresentation.ExceptionMessage(msg, firstUserFrame st)
    | TestFailure.TimedOut after ->
      FailurePresentation.Timeout after

  let inlineLabel maxLen (fp: FailurePresentation) =
    let raw =
      match fp with
      | FailurePresentation.AssertionDiff(e, a) ->
        sprintf "expected %s, got %s" e a
      | FailurePresentation.ExceptionMessage(m, _) -> m
      | FailurePresentation.Timeout t ->
        sprintf "timed out after %ds" (int t.TotalSeconds)
      | FailurePresentation.RawMessage m -> m
    match raw.Length <= maxLen with
    | true -> raw
    | false -> raw.Substring(0, maxLen - 1) + "…"

module AnnotationFreshness =
  let fromPhaseAndResult
    (phase: TestRunPhase)
    (status: TestRunStatus)
    =
    match status with
    | TestRunStatus.Running
    | TestRunStatus.Queued -> AnnotationFreshness.Running
    | TestRunStatus.Stale -> AnnotationFreshness.Stale
    | _ ->
      match phase with
      | TestRunPhase.RunningButEdited _ -> AnnotationFreshness.Stale
      | _ -> AnnotationFreshness.Current

module TestCodeLens =
  let label (status: TestRunStatus) (name: string) =
    match status with
    | TestRunStatus.Passed d ->
      sprintf "✓ %s (%.0fms)" name d.TotalMilliseconds
    | TestRunStatus.Failed(f, d) ->
      let msg =
        match f with
        | TestFailure.AssertionFailed m -> m
        | TestFailure.ExceptionThrown(m, _) -> m
        | TestFailure.TimedOut t ->
          sprintf "timed out after %ds" (int t.TotalSeconds)
      let t =
        match msg.Length > 60 with
        | true -> msg.Substring(0, 59) + "…"
        | false -> msg
      sprintf "✗ %s: %s (%.0fms)" name t d.TotalMilliseconds
    | TestRunStatus.Running ->
      sprintf "● %s running…" name
    | TestRunStatus.Detected -> sprintf "◆ %s" name
    | TestRunStatus.Queued -> sprintf "◆ %s (queued)" name
    | TestRunStatus.Skipped r -> sprintf "○ %s: %s" name r
    | TestRunStatus.Stale -> sprintf "~ %s (stale)" name
    | TestRunStatus.PolicyDisabled ->
      sprintf "○ %s (disabled)" name

  let defaultCommand = function
    | TestRunStatus.Failed _ -> CodeLensCommand.DebugTest
    | _ -> CodeLensCommand.RunTest

module FileAnnotations =
  let empty path =
    { FilePath = path
      TestAnnotations = [||]
      CoverageAnnotations = [||]
      InlineFailures = [||]
      CodeLenses = [||]
      PerformanceAnnotations = [||] }

  let statusPriority = function
    | TestRunStatus.Failed _ -> 0
    | TestRunStatus.Running -> 1
    | TestRunStatus.Stale -> 2
    | TestRunStatus.Skipped _ -> 3
    | TestRunStatus.Detected -> 4
    | TestRunStatus.Queued -> 5
    | TestRunStatus.Passed _ -> 6
    | TestRunStatus.PolicyDisabled -> 7

  /// Build a lookup from (file, line) → (endLine, endColumn) from sequence points.
  /// When multiple SPs exist for the same line, picks the widest range.
  let private buildRangeLookup (maps: InstrumentationMap seq) : Map<string * int, int * int> =
    maps
    |> Seq.collect (fun m -> m.Slots)
    |> Seq.filter SequencePoint.hasRange
    |> Seq.groupBy (fun sp -> sp.File, sp.Line)
    |> Seq.map (fun (key, sps) ->
      let best = sps |> Seq.maxBy (fun sp -> sp.EndLine, sp.EndColumn)
      key, (best.EndLine, best.EndColumn))
    |> Map.ofSeq

  let project
    (filePath: string)
    (depGraph: TestDependencyGraph option)
    (state: LiveTestState)
    =
    let entries = LiveTestState.orderedStatusEntries state
    let fileEntries =
      entries
      |> Array.choose (fun e ->
        match e.Origin with
        | TestOrigin.SourceMapped(f, line) when f = filePath ->
          Some(e, line)
        | _ -> None)
    let testAnnotations =
      fileEntries
      |> Array.groupBy (fun (_, line) -> line)
      |> Array.map (fun (line, entries) ->
        let worst =
          entries
          |> Array.sortBy (fun (e, _) -> statusPriority e.Status)
          |> Array.head
          |> fst
        { Line = line
          TestId = worst.TestId
          DisplayName = worst.DisplayName
          Status = worst.Status
          Freshness =
            let testSession = Map.tryFind worst.TestId state.TestSessionMap
            let phase =
              match testSession with
              | Some sid -> state.RunPhases |> Map.tryFind sid |> Option.defaultValue Idle
              | None -> if TestRunPhase.isAnyRunning state.RunPhases then RunningButEdited RunGeneration.zero else Idle
            AnnotationFreshness.fromPhaseAndResult
              phase worst.Status })
      |> Array.sortBy (fun a -> a.Line)
    let codeLenses =
      fileEntries
      |> Array.map (fun (e, line) ->
        { Line = line
          Label = TestCodeLens.label e.Status e.DisplayName
          TestId = e.TestId
          Command = TestCodeLens.defaultCommand e.Status })
      |> Array.sortBy (fun c -> c.Line)
    let inlineFailures =
      fileEntries
      |> Array.choose (fun (e, line) ->
        match e.Status with
        | TestRunStatus.Failed(failure, duration) ->
          Some
            { Line = line
              TestId = e.TestId
              TestName = e.DisplayName
              Failure =
                FailurePresentation.fromTestFailure failure
              Duration = duration }
        | _ -> None)
      |> Array.sortBy (fun f -> f.Line)
    let coverageAnnotations =
      state.CoverageAnnotations
      |> Array.filter (fun ca -> ca.FilePath = filePath)
      |> Array.map (fun ca ->
        { Line = ca.DefinitionLine
          EndLine = 0
          EndColumn = 0
          Detail = ca.Status
          CoveringTestIds =
            match depGraph with
            | None -> [||]
            | Some g ->
              match Map.tryFind ca.Symbol g.SymbolToTests with
              | Some ids -> ids
              | None -> [||]
          BranchCoverage = None })
      |> Array.sortBy (fun c -> c.Line)
    { FilePath = filePath
      TestAnnotations = testAnnotations
      CoverageAnnotations = coverageAnnotations
      InlineFailures = inlineFailures
      CodeLenses = codeLenses
      PerformanceAnnotations = [||] }

  /// Synthesize coverage annotations from dep graph + analysis cache when
  /// no explicit CoverageUpdated events have been dispatched.
  let private synthesizeCoverage
    (filePath: string)
    (analysisCache: FileAnalysisCache)
    (depGraph: TestDependencyGraph)
    (lastResults: Map<TestId, TestRunResult>)
    : CoverageAnnotation array =
    match Map.tryFind filePath analysisCache.FileSymbols with
    | None -> [||]
    | Some refs ->
      refs
      |> List.choose (fun ref ->
        match Map.tryFind ref.SymbolFullName depGraph.SymbolToTests with
        | None -> None
        | Some [||] -> None
        | Some testIds ->
          let passCount = testIds |> Array.filter (fun tid -> match Map.tryFind tid lastResults with | Some { Result = TestResult.Passed _ } -> true | _ -> false) |> Array.length
          let failCount = testIds |> Array.filter (fun tid -> match Map.tryFind tid lastResults with | Some { Result = TestResult.Failed _ } -> true | _ -> false) |> Array.length
          let health = if failCount > 0 then CoverageHealth.SomeFailing else CoverageHealth.AllPassing
          let status = if passCount + failCount > 0 then CoverageStatus.Covered(passCount + failCount, health) else CoverageStatus.Pending
          Some { Symbol = ref.SymbolFullName; FilePath = filePath; DefinitionLine = ref.Line; Status = status; BranchCoverage = BranchCoverage.Unknown })
      |> Array.ofList

  /// Project file annotations with coverage synthesized from the dependency graph.
  /// Use this instead of `project` when the full cycle state is available.
  let projectWithCoverage (filePath: string) (cycleState: LiveTestCycleState) : FileAnnotations =
    let depGraph = Some cycleState.DepGraph
    let base' = project filePath depGraph cycleState.TestState
    let allMaps = cycleState.InstrumentationMaps |> Map.values |> Seq.collect id |> Array.ofSeq
    let lineCovMap = CoverageBitmap.computeLineCoverageForFile filePath allMaps cycleState.TestState.TestCoverageBitmaps
    let rangeLookup = buildRangeLookup allMaps
    let enrichWithRange (ca: CoverageLineAnnotation) =
      match Map.tryFind (filePath, ca.Line) rangeLookup with
      | Some (endL, endC) -> { ca with EndLine = endL; EndColumn = endC }
      | None -> ca
    let enrichWithBranch (anns: CoverageLineAnnotation array) =
      anns |> Array.map (fun ca ->
        let ca' = enrichWithRange ca
        match Map.tryFind ca'.Line lineCovMap with
        | Some lc -> { ca' with BranchCoverage = Some lc }
        | None -> ca')
    match base'.CoverageAnnotations.Length > 0 with
    | true ->
      { base' with CoverageAnnotations = enrichWithBranch base'.CoverageAnnotations }
    | false ->
      let synthesized = synthesizeCoverage filePath cycleState.AnalysisCache cycleState.DepGraph cycleState.TestState.LastResults
      let coverageLineAnnotations =
        synthesized
        |> Array.map (fun ca ->
          let endLine, endColumn =
            match Map.tryFind (filePath, ca.DefinitionLine) rangeLookup with
            | Some (el, ec) -> el, ec
            | None -> 0, 0
          { Line = ca.DefinitionLine
            EndLine = endLine
            EndColumn = endColumn
            Detail = ca.Status
            CoveringTestIds =
              match Map.tryFind ca.Symbol cycleState.DepGraph.SymbolToTests with
              | Some ids -> ids
              | None -> [||]
            BranchCoverage = Map.tryFind ca.DefinitionLine lineCovMap })
        |> Array.sortBy (fun c -> c.Line)
      { base' with CoverageAnnotations = coverageLineAnnotations }

  /// Resolve a file path query to a full path by checking test sources first,
  /// then production code files from instrumentation maps.
  let resolveFilePath (fileParam: string) (statusEntries: TestStatusEntry array) (instrMaps: Map<string, InstrumentationMap array>) : string option =
    let matchesParam (f: string) =
      f = fileParam
      || f.EndsWith(fileParam, System.StringComparison.OrdinalIgnoreCase)
      || f.EndsWith(
           System.IO.Path.DirectorySeparatorChar.ToString() + fileParam,
           System.StringComparison.OrdinalIgnoreCase)
    let testFile =
      statusEntries
      |> Array.choose (fun e ->
        match e.Origin with
        | TestOrigin.SourceMapped (f, _) -> Some f
        | _ -> None)
      |> Array.distinct
      |> Array.tryFind matchesParam
    match testFile with
    | Some _ -> testFile
    | None ->
      instrMaps
      |> Map.values |> Seq.collect id
      |> Seq.collect (fun m -> m.Slots |> Array.map (fun s -> s.File))
      |> Seq.distinct
      |> Seq.tryFind matchesParam

// --- Test Run Explainer (MCP "explain_test_run" / "query_test_coverage") ---

[<RequireQualifiedAccess>]
type TestTriggerReason =
  | SymbolCoverage of symbols: string list
  | NewTest
  | ExplicitRun
  | UnknownCoverage

type TestRunExplanation = {
  TestId: TestId
  DisplayName: string
  Reason: TestTriggerReason
  CoveringSymbols: string list
  Trigger: RunTrigger
  DurationMs: float option
  FlakyClassification: FlakyClassification
}

type RunCycleExplanation = {
  ChangedSymbols: string list
  Trigger: RunTrigger
  AffectedTests: TestRunExplanation array
  FilteredOutByPolicy: TestId array
  TotalDiscovered: int
}

module TestRunExplainer =

  let private durationMs (r: TestResult) =
    match r with
    | TestResult.Passed d -> Some d.TotalMilliseconds
    | TestResult.Failed (_, d) -> Some d.TotalMilliseconds
    | TestResult.Skipped _ | TestResult.NotRun -> None

  let explainTest
    (graph: TestDependencyGraph)
    (lastResults: Map<TestId, TestRunResult>)
    (flakyHistory: Map<TestId, ResultWindow>)
    (changedSymbols: string list)
    (trigger: RunTrigger)
    (test: TestCase)
    : TestRunExplanation =
    let coveringSymbols =
      changedSymbols
      |> List.filter (fun sym ->
        match Map.tryFind sym graph.TransitiveCoverage with
        | Some testIds -> testIds |> Array.contains test.Id
        | None -> false)
    let reason =
      match coveringSymbols with
      | _ :: _ -> TestTriggerReason.SymbolCoverage coveringSymbols
      | [] ->
        match Map.tryFind test.Id lastResults with
        | None -> TestTriggerReason.NewTest
        | Some _ -> TestTriggerReason.UnknownCoverage
    let dur =
      Map.tryFind test.Id lastResults
      |> Option.bind (fun r -> durationMs r.Result)
    let classification =
      FlakyDetection.classifyFlakiness test.Id flakyHistory lastResults
    { TestId = test.Id
      DisplayName = test.DisplayName
      Reason = reason
      CoveringSymbols = coveringSymbols
      Trigger = trigger
      DurationMs = dur
      FlakyClassification = classification }

  let explainSymbolChange
    (graph: TestDependencyGraph)
    (discoveredTests: TestCase array)
    (lastResults: Map<TestId, TestRunResult>)
    (flakyHistory: Map<TestId, ResultWindow>)
    (policies: Map<TestCategory, RunPolicy>)
    (changedSymbols: string list)
    (trigger: RunTrigger)
    : RunCycleExplanation =
    let affectedIds =
      TestDependencyGraph.findAffected changedSymbols graph |> Set.ofArray
    let affectedTests =
      discoveredTests
      |> Array.filter (fun tc -> affectedIds.Contains tc.Id)
    let filtered =
      PolicyFilter.filterTests policies trigger affectedTests
    let filteredSet = filtered |> Array.map (fun tc -> tc.Id) |> Set.ofArray
    let filteredOut =
      affectedTests
      |> Array.filter (fun tc -> not (filteredSet.Contains tc.Id))
      |> Array.map (fun tc -> tc.Id)
    let explanations =
      filtered
      |> Array.map (explainTest graph lastResults flakyHistory changedSymbols trigger)
    { ChangedSymbols = changedSymbols
      Trigger = trigger
      AffectedTests = explanations
      FilteredOutByPolicy = filteredOut
      TotalDiscovered = discoveredTests.Length }

  let queryTestCoverage
    (graph: TestDependencyGraph)
    (discoveredTests: TestCase array)
    (lastResults: Map<TestId, TestRunResult>)
    (symbol: string)
    : CoveringTestInfo array =
    match Map.tryFind symbol graph.TransitiveCoverage with
    | None -> [||]
    | Some testIds ->
      let testMap = discoveredTests |> Array.map (fun tc -> tc.Id, tc) |> Map.ofArray
      testIds
      |> Array.choose (fun tid ->
        match Map.tryFind tid testMap with
        | Some tc ->
          Some { TestId = tid
                 DisplayName = tc.DisplayName
                 Result = Map.tryFind tid lastResults |> Option.map (fun r -> r.Result) }
        | None -> None)

type SessionInvariantViolation = {
  Message: string
  RunPhaseKeys: Set<string>
  InstrumentationMapKeys: Set<string>
  TestSessionMapKeys: Set<string>
}

module SessionInvariant =
  /// Validates steady-state consistency of per-session maps.
  /// Returns None during initialization (when either map is empty).
  let validate (state: LiveTestState) (instrMaps: Map<string, InstrumentationMap array>) : SessionInvariantViolation option =
    let runPhaseKeys = state.RunPhases |> Map.keys |> Set.ofSeq
    let instrMapKeys = instrMaps |> Map.keys |> Set.ofSeq
    let sessionMapKeys = state.TestSessionMap |> Map.keys |> Set.ofSeq |> Set.map (fun (TestId.TestId tid) -> tid)
    match Set.isEmpty runPhaseKeys || Set.isEmpty instrMapKeys with
    | true -> None
    | false ->
      match runPhaseKeys <> instrMapKeys with
      | true ->
        Some {
          Message = sprintf "Session key mismatch: RunPhases has %A, InstrumentationMaps has %A" runPhaseKeys instrMapKeys
          RunPhaseKeys = runPhaseKeys
          InstrumentationMapKeys = instrMapKeys
          TestSessionMapKeys = sessionMapKeys
        }
      | false -> None
