namespace SageFs.Features.LiveTesting

open SageFs
open System
open System.IO
open System.Numerics
open System.Reflection
open System.Security.Cryptography
open System.Text
open System.Text.RegularExpressions
open SageFs.Measures

type TestFramework = SageFs.TestFramework

module TestFramework =
  let toString = SageFs.TestFramework.toString
  let parse = SageFs.TestFramework.parse

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

[<Struct; RequireQualifiedAccess>]
type AnalysisIdentity = AnalysisIdentity of string

module AnalysisIdentity =
  let ofContent (content: string) =
    let bytes = Encoding.UTF8.GetBytes content
    let hash = SHA256.HashData bytes
    AnalysisIdentity.AnalysisIdentity(Convert.ToHexString(hash).Substring(0, 16))

  let value (AnalysisIdentity.AnalysisIdentity value) = value

module TriviaNormalization =
  let private normalize (content: string) =
    let sb = StringBuilder(content.Length)
    let len = content.Length
    let mutable i = 0
    let mutable blockDepth = 0
    let mutable inLineComment = false
    let mutable inString = false
    let mutable inVerbatimString = false
    let mutable inTripleString = false
    let mutable lineStart = 0
    let mutable atLineStart = true
    let mutable pendingSpace = false

    let inline startsWith (s: string) = i + s.Length <= len && content.AsSpan(i, s.Length).SequenceEqual(s.AsSpan())
    let inline newline () = atLineStart <- true; pendingSpace <- false; lineStart <- i + 1
    // F# is offside-sensitive: a token's column and whether two tokens touch
    // are meaning, so keep both. Only runs, blank lines and trailing space go.
    let inline token () =
      if atLineStart then sb.Append('\n').Append(' ', i - lineStart) |> ignore
      elif pendingSpace then sb.Append(' ') |> ignore
      atLineStart <- false; pendingSpace <- false

    while i < len do
      match inLineComment, blockDepth > 0, inTripleString, inVerbatimString, inString with
      | true, _, _, _, _ ->
        if content[i] = '\r' || content[i] = '\n' then inLineComment <- false
        if content[i] = '\n' then newline ()
        i <- i + 1
      | false, true, _, _, _ ->
        if startsWith "(*" then
          blockDepth <- blockDepth + 1
          i <- i + 2
        elif startsWith "*)" then
          blockDepth <- blockDepth - 1
          i <- i + 2
        else
          if content[i] = '\n' then newline ()
          i <- i + 1
      | false, false, true, _, _ ->
        if startsWith "\"\"\"" then
          sb.Append("\"\"\"") |> ignore
          inTripleString <- false
          i <- i + 3
        else
          sb.Append(content[i]) |> ignore
          i <- i + 1
      | false, false, false, true, _ ->
        if startsWith "\"\"" then
          sb.Append("\"\"") |> ignore
          i <- i + 2
        else
          let c = content[i]
          sb.Append(c) |> ignore
          i <- i + 1
          if c = '"' then
            inVerbatimString <- false
      | false, false, false, false, true ->
        let c = content[i]
        sb.Append(c) |> ignore
        i <- i + 1
        if c = '\\' && i < len then
          sb.Append(content[i]) |> ignore
          i <- i + 1
        elif c = '"' then
          inString <- false
      | false, false, false, false, false ->
        if startsWith "//" then
          inLineComment <- true
          i <- i + 2
        elif startsWith "(*" then
          blockDepth <- 1
          pendingSpace <- true
          i <- i + 2
        elif content[i] = '\n' then
          newline ()
          i <- i + 1
        elif Char.IsWhiteSpace content[i] then
          pendingSpace <- true
          i <- i + 1
        elif startsWith "@\"" then
          token ()
          sb.Append("@\"") |> ignore
          inVerbatimString <- true
          i <- i + 2
        elif startsWith "\"\"\"" then
          token ()
          sb.Append("\"\"\"") |> ignore
          inTripleString <- true
          i <- i + 3
        elif content[i] = '"' then
          token ()
          sb.Append('"') |> ignore
          inString <- true
          i <- i + 1
        else
          token ()
          sb.Append(content[i]) |> ignore
          i <- i + 1

    sb.ToString()

  let equivalent (previousContent: string) (currentContent: string) =
    normalize previousContent = normalize currentContent

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

/// Whether live testing is running tests as you edit. Paused, it still type-checks and keeps the
/// session's evaluated code current; it only holds the test runs back, and the tests it held back
/// show as stale until it is resumed. (Visual Studio's "pause", by hand.)
[<RequireQualifiedAccess>]
type LivePause =
  | Live
  | Paused

/// Which tests an automatic run may touch. An explicit run ignores it: asking for a test by name
/// always runs it. A pattern is a case-sensitive substring of the test's full name or display name,
/// the same match an explicit run's `pattern` uses. (Visual Studio's playlist and exclude set.)
[<RequireQualifiedAccess>]
type TestScope =
  | EveryTest
  /// Only tests matching one of these run automatically.
  | OnlyMatching of patterns: string list
  /// Every test except those matching one of these runs automatically.
  | AllExcept of patterns: string list

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

/// Why a requested test ended a run without a result of its own. This is not
/// a failure — nothing is known about the test's code, only that the run ended
/// before the test reported — so it must never be shown as one.
[<RequireQualifiedAccess>]
type NoResultReason =
  /// The worker closed the stream (EOF or `event: done`) without reporting it.
  | StreamEnded
  /// The worker went silent for longer than the run's inactivity window.
  | StreamStalled of after: TimeSpan
  /// The connection to the worker failed mid-run.
  | TransportFailed of message: string
  /// The run was cancelled — superseded by a newer run, or the daemon stopped.
  | RunCancelled

module NoResultReason =
  /// Plain-language cause, for run summaries and per-test detail.
  let describe (reason: NoResultReason) : string =
    match reason with
    | NoResultReason.StreamEnded -> "the worker ended the run before reporting it"
    | NoResultReason.StreamStalled after ->
      sprintf "the worker went silent for %.0fs" after.TotalSeconds
    | NoResultReason.TransportFailed message ->
      sprintf "the connection to the worker failed: %s" message
    | NoResultReason.RunCancelled -> "the run was cancelled before it reported"

[<RequireQualifiedAccess>]
type TestResult =
  | Passed of duration: TimeSpan
  | Failed of failure: TestFailure * duration: TimeSpan
  | Skipped of reason: string
  | NotRun
  /// The test was requested in a run that ended before it reported.
  | NoResult of reason: NoResultReason

type TestRunResult = {
  TestId: TestId
  TestName: string
  Result: TestResult
  Timestamp: DateTimeOffset
  /// Captured console output (stdout) from the test execution.
  Output: string option
}

module TestRunResult =
  /// Build result entries for tests that never reported during a run that has
  /// ended (a stalled stream, or a transport failure). `receivedIds` are the
  /// tests that already streamed a result — they are EXCLUDED so nothing is
  /// double-reported (a Passed test must never be re-reported as a failure)
  /// or fabricated over a real outcome. `mk` builds the synthesized entry.
  let synthesizeMissing
    (tests: TestCase array)
    (receivedIds: Set<TestId>)
    (mk: TestCase -> TestRunResult)
    : TestRunResult array =
    tests
    |> Array.choose (fun tc ->
      match receivedIds.Contains tc.Id with
      | true -> None
      | false -> Some (mk tc))

  /// Mark every requested test that never reported with a truthful
  /// `NoResult reason` — so whichever way a run ends, every requested test is
  /// left terminal, and a reported outcome is never replaced or fabricated over.
  let neverReported
    (reason: NoResultReason)
    (at: DateTimeOffset)
    (tests: TestCase array)
    (receivedIds: Set<TestId>)
    : TestRunResult array =
    synthesizeMissing tests receivedIds (fun tc ->
      { TestId = tc.Id
        TestName = tc.FullName
        Result = TestResult.NoResult reason
        Timestamp = at
        Output = None })

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

/// Why a real build contradicted what the live eval said.
[<RequireQualifiedAccess>]
type BuildDisagreement =
  /// The project did not build. The message is the compiler's.
  | BuildFailed of message: string
  /// The build produced a different verdict for this test than the eval did.
  | ResultDiffers of evaluated: string * built: string
  /// The build or the run against it did not answer in time.
  | BuildUnanswered of waited: string

/// What code produced a test's current verdict. A keystroke's tests run against code the live session
/// EVALUATED, which is not what a compiler would have made of the same text.
[<RequireQualifiedAccess>]
type ResultProvenance =
  /// Ran against binaries a build produced (the session's start, an explicit run, a rebuild).
  | Compiled
  /// Ran against code evaluated in the live session. No real build has confirmed it.
  | Evaluated
  /// Evaluated, then re-run against a real build of the same content, and the two agreed.
  | VerifiedByBuild
  /// A real build of the same content contradicted the eval. Said loudly, with why.
  | BuildDisagrees of BuildDisagreement

module ResultProvenance =
  let toWireValue (provenance: ResultProvenance) : string =
    match provenance with
    | ResultProvenance.Compiled -> "compiled"
    | ResultProvenance.Evaluated -> "evaluated"
    | ResultProvenance.VerifiedByBuild -> "verified_by_build"
    | ResultProvenance.BuildDisagrees _ -> "build_disagrees"

  /// One sentence a row or a status line can show.
  let describe (provenance: ResultProvenance) : string =
    match provenance with
    | ResultProvenance.Compiled -> "ran against compiled binaries"
    | ResultProvenance.Evaluated -> "ran against code evaluated in the session; no real build has confirmed it yet"
    | ResultProvenance.VerifiedByBuild -> "evaluated, then confirmed by a real build"
    | ResultProvenance.BuildDisagrees (BuildDisagreement.BuildFailed message) ->
      sprintf "the real build failed, so this result is not confirmed: %s" message
    | ResultProvenance.BuildDisagrees (BuildDisagreement.ResultDiffers (evaluated, built)) ->
      sprintf "the real build disagrees: the eval said %s, the build says %s" evaluated built
    | ResultProvenance.BuildDisagrees (BuildDisagreement.BuildUnanswered waited) ->
      sprintf "the real build did not answer within %s, so this result is not confirmed" waited

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
  /// What code produced `Status`.
  Provenance: ResultProvenance
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

/// A hash of every line of one source file, as the file was when the assembly was
/// compiled. Kept only when the file's bytes matched the checksum the compiler wrote
/// into the PDB, so a line number in the map and a line number in this array are the
/// same line. A file whose checksum did not match has no entry: it is not known.
type SourceLineHashes = {
  File: string
  Lines: int64 array
}

/// What a map knows about the compiled code beyond where its sequence points are.
type MapSource = {
  /// Slots (indexes into the map's `Slots`) that run while a module or a type initializes.
  /// They run once per process, in whichever test touched the module first, so a test's own
  /// coverage cannot say whether it depends on them.
  StartupSlots: int array
  /// The compiled text of each source file that has sequence points, by line hash.
  Baselines: SourceLineHashes array
}

module MapSource =
  /// A map that knows nothing beyond its sequence points. Every consumer treats this as
  /// "cannot narrow by line", never as "nothing runs at startup".
  let none : MapSource = { StartupSlots = [||]; Baselines = [||] }

/// Maps instrumented sequence point slots to source locations.
/// Created once per assembly instrumentation, reused across test runs.
type InstrumentationMap = {
  Slots: SequencePoint array
  TotalProbes: int
  TrackerTypeName: string
  HitsFieldName: string
  Source: MapSource
}

module InstrumentationMap =
  let empty =
    { Slots = [||]
      TotalProbes = 0
      TrackerTypeName = "__SageFsCoverage"
      HitsFieldName = "Hits"
      Source = MapSource.none }

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
        HitsFieldName = "Hits"
        Source = MapSource.none }

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

/// Which lines of a file an edit changed, measured against the text the assembly was compiled
/// from. Line numbers in recorded coverage are only meaningful against that text.
[<RequireQualifiedAccess>]
type ChangedLines =
  /// The line count did not move and exactly these (1-based) lines differ from the compiled text.
  | InPlace of Set<int>
  /// Lines were inserted or removed, so a line number in recorded coverage no longer names the same line.
  | Shifted
  /// There is no compiled text to compare against (no baseline for this file).
  | NoBaseline

/// A stable hash of one source line. Stable across processes: the worker hashes the compiled
/// text, the daemon hashes the buffer, and the two have to agree.
module LineHash =
  /// FNV-1a over the UTF-16 code units of the line, without its line ending.
  let ofLine (line: string) : int64 =
    failwith "not implemented: LineHash.ofLine"

  /// One hash per line of `text`, split on '\n' with a trailing '\r' ignored.
  let ofText (text: string) : int64 array =
    failwith "not implemented: LineHash.ofText"

module LineEdit =
  /// Compare an edited buffer with the hashes of the compiled text.
  let between (baseline: int64 array) (edited: string) : ChangedLines =
    failwith "not implemented: LineEdit.between"

/// Why recorded coverage could not narrow an edit to the tests that run the changed lines.
[<RequireQualifiedAccess>]
type LineNarrowingRefusal =
  /// No test has a stored coverage bitmap that matches the current instrumentation.
  | NoBitmaps
  /// The edit inserted or removed lines, so recorded line numbers no longer line up.
  | EditShifted
  /// No compiled text for this file to measure the edit against.
  | NoBaseline
  /// The edit changed no line relative to the compiled text.
  | NothingChanged
  /// A changed line has no sequence point, so no recorded coverage speaks for it.
  | ChangedLineHasNoProbe of line: int
  /// A changed line runs when the module initializes, once per process, in whichever test got there first.
  | ChangedLineRunsAtStartup of line: int

[<RequireQualifiedAccess>]
type LineNarrowing =
  /// Exactly the tests whose own recorded coverage reaches a changed line, plus every test with no
  /// usable coverage (it is not known to be unaffected).
  | NarrowedTo of TestId array
  | Refused of LineNarrowingRefusal

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

  /// Pack the bitmap's words into a base64 string for the wire — 8 bytes per
  /// word before base64's 4:3 expansion, versus one JSON `true,`/`false,`
  /// token (5-6 bytes) per individual probe. This is the wire format;
  /// `Count` still has to travel alongside it (a corruption-shortened byte
  /// buffer would otherwise silently truncate the last word's probes).
  let toBase64 (bm: CoverageBitmap) : string =
    let bytes = Array.zeroCreate<byte> (bm.Bits.Length * 8)
    Buffer.BlockCopy(bm.Bits, 0, bytes, 0, bytes.Length)
    Convert.ToBase64String(bytes)

  /// Unpack a base64-encoded word buffer back into a CoverageBitmap. `count`
  /// is the true probe count (carried alongside the payload, not recovered
  /// from byte length — the last word can be padded with unused bits).
  let ofBase64 (count: int) (base64: string) : CoverageBitmap =
    match count = 0 with
    | true -> empty
    | false ->
      let bytes = Convert.FromBase64String(base64)
      let words = wordsNeeded count
      let bits = Array.zeroCreate<uint64> words
      Buffer.BlockCopy(bytes, 0, bits, 0, bytes.Length)
      { Bits = bits; Count = count }

  /// Check if two bitmaps have identical coverage (same size + same bits).
  /// SequenceEqual on a Span<uint64> is a BCL-vectorized memory comparison —
  /// unlike a hand-written while-loop with a data-dependent early exit (the
  /// previous implementation here), which the JIT does NOT auto-vectorize.
  let equivalent (a: CoverageBitmap) (b: CoverageBitmap) : bool =
    a.Count = b.Count
    && System.MemoryExtensions.SequenceEqual(ReadOnlySpan<uint64> a.Bits, ReadOnlySpan<uint64> b.Bits)

  /// Count number of set bits (covered probes).
  let popCount (bm: CoverageBitmap) : int =
    let mutable total = 0
    for w in bm.Bits do
      total <- total + (System.Numerics.BitOperations.PopCount(w) |> int)
    total

  /// Apply a bitwise op word-by-word using System.Numerics.Vector<uint64> —
  /// the JIT lowers this to real hardware SIMD instructions (SSE2/AVX2/NEON,
  /// whichever the running CPU has) — with a scalar remainder loop for the
  /// words left over when the word count isn't a multiple of the vector
  /// width. `a` and `b` must be the same length; callers check Count first.
  let inline private zipWords
    ([<InlineIfLambda>] vecOp: Vector<uint64> -> Vector<uint64> -> Vector<uint64>)
    ([<InlineIfLambda>] scalarOp: uint64 -> uint64 -> uint64)
    (a: uint64 array)
    (b: uint64 array)
    : uint64 array =
    let len = a.Length
    let result = Array.zeroCreate<uint64> len
    let width = Vector<uint64>.Count
    let mutable i = 0
    while i + width <= len do
      (vecOp (Vector<uint64>(a, i)) (Vector<uint64>(b, i))).CopyTo(result, i)
      i <- i + width
    while i < len do
      result.[i] <- scalarOp a.[i] b.[i]
      i <- i + 1
    result

  /// Bitwise AND — intersection of two coverage bitmaps.
  let intersect (a: CoverageBitmap) (b: CoverageBitmap) : CoverageBitmap =
    match a.Count <> b.Count with
    | true -> failwithf "CoverageBitmap.intersect size mismatch: a.Count=%d, b.Count=%d" a.Count b.Count
    | false -> ()
    { Bits = zipWords (&&&) (&&&) a.Bits b.Bits; Count = a.Count }

  /// Bitwise XOR — symmetric difference of two coverage bitmaps.
  let xorDiff (a: CoverageBitmap) (b: CoverageBitmap) : CoverageBitmap =
    match a.Count <> b.Count with
    | true -> failwithf "CoverageBitmap.xorDiff size mismatch: a.Count=%d, b.Count=%d" a.Count b.Count
    | false -> ()
    { Bits = zipWords (^^^) (^^^) a.Bits b.Bits; Count = a.Count }

  /// Bitwise OR — union of two coverage bitmaps.
  let union (a: CoverageBitmap) (b: CoverageBitmap) : CoverageBitmap =
    match a.Count = 0 && b.Count = 0, a.Count <> b.Count with
    | true, _ -> empty
    | _, true -> failwithf "CoverageBitmap.union size mismatch: a.Count=%d, b.Count=%d" a.Count b.Count
    | false, false ->
      { Bits = zipWords (|||) (|||) a.Bits b.Bits; Count = a.Count }

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

  /// Narrow an edit to the tests whose OWN recorded coverage reaches a changed line.
  /// Fails closed: anything that makes a line number untrustworthy, or a changed line unspoken for,
  /// is a refusal with its reason and the caller keeps its wider selection. A test with no
  /// bitmap of the current size is kept, because it is not known to be unaffected.
  let narrowByLines
    (filePath: string)
    (lines: ChangedLines)
    (maps: InstrumentationMap array)
    (bitmaps: Map<TestId, CoverageBitmap>)
    (discovered: TestId array)
    : LineNarrowing =
    failwith "not implemented: CoverageBitmap.narrowByLines"

  /// The tests whose own recorded coverage reaches `line` of `filePath`, by line, for every line
  /// that has a sequence point some test hit. Tests are in discovery order.
  let coveringTestsByLine
    (filePath: string)
    (maps: InstrumentationMap array)
    (bitmaps: Map<TestId, CoverageBitmap>)
    (discovered: TestId array)
    : Map<int, TestId array> =
    failwith "not implemented: CoverageBitmap.coveringTestsByLine"

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

[<RequireQualifiedAccess>]
type SelectionPrecision =
  | ExactDependencyMatch
  | CoverageApproximation
  /// Narrowed to the tests whose own recorded coverage reaches the lines the edit changed.
  | LineCoverageNarrowing
  | ConservativeFallback
  | NoImpactedTests
  | SuppressedByPolicy

[<RequireQualifiedAccess>]
type FreshnessTrust =
  | FreshExact
  | FreshApproximate
  | StaleAwaitingRerun
  | Suppressed

[<RequireQualifiedAccess>]
type RerunCause =
  | KeystrokeBuffered of filePath: string
  | FileSaved of filePath: string
  | ExplicitRunRequested of filePath: string

type SelectionExplanation = {
  Cause: RerunCause
  Precision: SelectionPrecision
  ChangedSymbols: string list
  SelectedTests: string array
  DeferredTests: string array
  Reason: string
}

type LiveTestingDecision = {
  Explanation: SelectionExplanation
  Trust: FreshnessTrust
}

module LiveTestingDecision =
  let precisionToWireValue = function
    | SelectionPrecision.ExactDependencyMatch -> "exact_dependency_match"
    | SelectionPrecision.CoverageApproximation -> "coverage_approximation"
    | SelectionPrecision.LineCoverageNarrowing -> "line_coverage_narrowing"
    | SelectionPrecision.ConservativeFallback -> "conservative_fallback"
    | SelectionPrecision.NoImpactedTests -> "no_impacted_tests"
    | SelectionPrecision.SuppressedByPolicy -> "suppressed_by_policy"

  let trustToWireValue = function
    | FreshnessTrust.FreshExact -> "fresh_exact"
    | FreshnessTrust.FreshApproximate -> "fresh_approximate"
    | FreshnessTrust.StaleAwaitingRerun -> "stale_awaiting_rerun"
    | FreshnessTrust.Suppressed -> "suppressed"

  let causeToWireValue = function
    | RerunCause.KeystrokeBuffered _ -> "keystroke_buffered"
    | RerunCause.FileSaved _ -> "file_saved"
    | RerunCause.ExplicitRunRequested _ -> "explicit_run_requested"

  let causeFilePath = function
    | RerunCause.KeystrokeBuffered filePath
    | RerunCause.FileSaved filePath
    | RerunCause.ExplicitRunRequested filePath -> filePath

  let trustFromPrecision = function
    | SelectionPrecision.ExactDependencyMatch -> FreshnessTrust.FreshExact
    | SelectionPrecision.CoverageApproximation
    | SelectionPrecision.LineCoverageNarrowing
    | SelectionPrecision.ConservativeFallback -> FreshnessTrust.FreshApproximate
    | SelectionPrecision.NoImpactedTests -> FreshnessTrust.StaleAwaitingRerun
    | SelectionPrecision.SuppressedByPolicy -> FreshnessTrust.Suppressed

  let fromSelection
    (cause: RerunCause)
    (precision: SelectionPrecision)
    (changedSymbols: string list)
    (selectedTests: string array)
    (deferredTests: string array)
    (reason: string) =
    { Explanation =
        { Cause = cause
          Precision = precision
          ChangedSymbols = changedSymbols
          SelectedTests = selectedTests
          DeferredTests = deferredTests
          Reason = reason }
      Trust = trustFromPrecision precision }

  let toWireModel (decision: LiveTestingDecision) =
    {| Cause = causeToWireValue decision.Explanation.Cause
       FilePath = causeFilePath decision.Explanation.Cause
       Precision = precisionToWireValue decision.Explanation.Precision
       Trust = trustToWireValue decision.Trust
       ChangedSymbols = decision.Explanation.ChangedSymbols
       SelectedTests = decision.Explanation.SelectedTests
       DeferredTests = decision.Explanation.DeferredTests
       Reason = decision.Explanation.Reason |}

  let statusBarHint (decision: LiveTestingDecision) =
    match decision.Explanation.Precision with
    | SelectionPrecision.ExactDependencyMatch ->
      sprintf "why: exact (%d selected)" decision.Explanation.SelectedTests.Length
    | SelectionPrecision.CoverageApproximation ->
      sprintf "why: coverage widened (%d selected)" decision.Explanation.SelectedTests.Length
    | SelectionPrecision.LineCoverageNarrowing ->
      sprintf "why: line coverage (%d selected)" decision.Explanation.SelectedTests.Length
    | SelectionPrecision.ConservativeFallback ->
      sprintf "why: fallback rebuild (%d selected)" decision.Explanation.SelectedTests.Length
    | SelectionPrecision.SuppressedByPolicy ->
      sprintf "why: deferred by policy (%d)" decision.Explanation.DeferredTests.Length
    | SelectionPrecision.NoImpactedTests ->
      "why: no impacted tests"

// --- Run Generation & Phase (replaces IsRunning: bool) ---

[<Struct>]
type RunGeneration = RunGeneration of int

module RunGeneration =
  let zero = RunGeneration 0
  let next (RunGeneration n) = RunGeneration (n + 1)
  let value (RunGeneration n) = n

/// Caller-chosen identity for one explicitly requested test run, so a caller
/// that must know THE result of ITS run (cohort landing verification) can find
/// it again. Generations are a shared counter, so "the next generation" is
/// ambiguous under concurrency; a request id is not.
type RunRequestId = RunRequestId of System.Guid

module RunRequestId =
  let fresh () = RunRequestId (System.Guid.NewGuid())
  let value (RunRequestId value) = value

type TestRunIdentity = {
  SessionId: string option
  Generation: RunGeneration
  RequestId: RunRequestId
}

/// Where an explicitly requested run stands. Durable in the model, because a
/// waiter re-evaluates only on (batched) model-changed notifications and can
/// miss a short-lived phase entirely.
[<RequireQualifiedAccess>]
type RequestedRunStatus =
  /// Generation allocated; the worker has not started this run yet.
  | Pending
  /// The worker started this run (`TestRunPhase.Running` on its generation).
  | Running
  /// The run's completion arrived.
  | Completed

/// An explicitly requested run: its one generation (allocated at request time, never
/// re-bumped), its status, and the tests it named, so a later read can list every one.
type RequestedRun =
  { RequestedSession: string
    RequestedGeneration: RunGeneration
    RequestedStatus: RequestedRunStatus
    RequestedTests: TestId list }

[<Struct>]
type TestRunPhase =
  | Idle
  | Running of generation: RunGeneration
  | RunningButEdited of editedGeneration: RunGeneration

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

module TestRunIdentity =
  let matches (left: TestRunIdentity) (right: TestRunIdentity) =
    left.SessionId = right.SessionId
    && left.Generation = right.Generation
    && left.RequestId = right.RequestId

  let acceptsPhase (incoming: TestRunIdentity) (phase: TestRunPhase) =
    match phase with
    | TestRunPhase.Running generation
    | TestRunPhase.RunningButEdited generation -> incoming.Generation = generation
    | TestRunPhase.Idle -> false

  let phaseFor (identity: TestRunIdentity) (phases: Map<string, TestRunPhase>) =
    match identity.SessionId with
    | Some sessionId -> phases |> Map.tryFind sessionId
    | None ->
      match phases |> Map.tryFindKey (fun _ phase -> TestRunPhase.isRunning phase) with
      | Some sessionId -> phases |> Map.tryFind sessionId
      | None -> Some TestRunPhase.Idle

  let accepts (incoming: TestRunIdentity) (phases: Map<string, TestRunPhase>) =
    match phaseFor incoming phases with
    | Some phase -> acceptsPhase incoming phase
    | None -> false

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
    | TestResult.Skipped _ | TestResult.NotRun | TestResult.NoResult _ -> TestOutcome.Pass

  let recordResult
    (testId: TestId)
    (result: TestResult)
    (history: Map<TestId, ResultWindow>)
    : Map<TestId, ResultWindow> =
    match result with
    // A test that never reported says nothing about its stability — recording
    // it as a pass or a fail would fabricate evidence either way.
    | TestResult.NoResult _ -> history
    | TestResult.Passed _ | TestResult.Failed _ | TestResult.Skipped _ | TestResult.NotRun ->
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

/// Denormalized status index — StatusEntries, lookup-by-id maps, and projection state
/// are always updated atomically via `TestStatusIndex.fromEntries` to maintain consistency.
/// This eliminates the previous risk of partial updates leaving Slots/Index out of sync.
type TestStatusIndex = {
  Entries: TestStatusEntry array
  Slots: Map<TestId, int>
  Index: Map<TestId, TestStatusEntry>
  Projection: StatusEntriesProjectionState
}

/// Pre-computed cached views — only written by `finalizeLiveTestingState`, read by
/// SSE dedup, MCP tools, and editor annotation providers.  Grouping these makes it
/// clear that they are derived state and should never be set from business logic.
type CachedViews = {
  /// Monotonic version counter — incremented on every test state change.
  /// Used by SseDedupKey for O(1) change detection.
  StateVersion: int64
  /// Pre-computed test summary — avoids O(n log n) filtering in dedup key hot path.
  TestSummary: TestSummary
  /// Enriched failure context for tests that recently transitioned Passed→Failed.
  FailureNarratives: Map<TestId, FailureNarrative>
  /// Pre-computed editor gutter annotations.
  EditorAnnotations: LineAnnotation array
}

/// Where one session's test discovery stands.
[<RequireQualifiedAccess>]
type DiscoveryProgress =
  | NotRequested
  | InProgress
  | Failed of reason: string
  | Completed

/// What is holding the next test run back.
[<RequireQualifiedAccess>]
type CompileBlock =
  | NoCompileErrors
  /// The saved file failed to type-check.
  | CompileErrors of file: string * errorCount: int
  /// The rebuild that re-runs the tests failed: build errors, or the worker never came back.
  | RebuildFailed of reason: string

type LiveTestState = {
  SourceLocations: SourceTestLocation array
  DiscoveredTests: TestCase array
  LastResults: Map<TestId, TestRunResult>
  /// Denormalized status entries, slots, index, and projection state — always updated atomically.
  StatusIndex: TestStatusIndex
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
  AssemblyLoadErrors: AssemblyLoadError list
  FlakyHistory: Map<TestId, ResultWindow>
  /// Per-test packed coverage bitmaps from IL probe hits, keyed by TestId.
  /// All tests in the same batch share the same bitmap (conservative: any test might have hit any probe).
  TestCoverageBitmaps: Map<TestId, CoverageBitmap>
  /// Pre-computed cached views — derived state updated by finalizeLiveTestingState.
  Cached: CachedViews
  /// Timestamp of the most recent TestsDiscovered event merge. Used by run_tests to detect
  /// whether discovery completed after a hot-reload before proceeding with stale test list.
  LastDiscoveryTime: System.DateTimeOffset
  /// Monotonic discovery generation: bumped on every meaningful TestsDiscovered
  /// merge so downstream clients can treat discovery as REPLACEMENT state — a
  /// snapshot tagged with an older generation is stale and must be rejected,
  /// and a re-discovery that removed renamed/deleted tests is observable.
  DiscoveryGeneration: int64
  /// Where each session's test discovery stands; a session missing here was never asked.
  SessionDiscovery: Map<string, DiscoveryProgress>
  LastDecision: LiveTestingDecision option
  /// Explicitly requested runs by request id (bounded; see
  /// `LiveTestState.maxTrackedRequests`). The run's generation is allocated
  /// ONCE, here, at request time.
  RunRequests: Map<RunRequestId, RequestedRun>
  /// The generation of the run that produced each test's `LastResults` entry:
  /// the session's running generation when the result ARRIVED. Absent when the
  /// result arrived with no run in flight. A result from some other run can
  /// never be mistaken for this run's, which is what makes a landing verdict
  /// attributable (roast F17 / cohort landing DST).
  ResultGenerations: Map<TestId, RunGeneration>
  /// What code produced each test's `LastResults` entry. A test with no entry ran against compiled binaries.
  Provenances: Map<TestId, ResultProvenance>
  /// Whether automatic runs are held back. See `LivePause`.
  Pause: LivePause
  /// Which tests automatic runs may touch. See `TestScope`.
  Scope: TestScope
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
      // Actionable zero-state (roast UX-2): a bare "found zero tests" is a
      // dead-end. Name the likely causes and the concrete recovery action so a
      // stuck user can get unstuck instead of concluding the feature is broken.
      "Live testing found zero tests. If this project has tests: (1) the test \
       assembly may not be loaded yet — run hard_reset_fsi_session with \
       rebuild:true to rebuild and reload, which re-runs discovery; (2) confirm \
       the project references a supported framework (Expecto, xUnit, NUnit, \
       MSTest, TUnit) and exposes public tests (Expecto [<Tests>] values, or \
       [<Fact>]/[<Test>]/[<TestMethod>] methods); (3) for a background \
       (non-active) session, switch to it and re-enable live testing to force a \
       fresh discovery pass."
    | LiveTestDiscoveryState.ReadyWithTests count ->
      sprintf "Live testing discovered %d tests." count

module TestStatusIndex =
  let empty = {
    Entries = Array.empty
    Slots = Map.empty
    Index = Map.empty
    Projection = StatusEntriesProjectionState.Materialized
  }

  let buildSlots (entries: TestStatusEntry array) : Map<TestId, int> =
    entries
    |> Array.mapi (fun index entry -> entry.TestId, index)
    |> Map.ofArray

  let buildIndex (entries: TestStatusEntry array) : Map<TestId, TestStatusEntry> =
    entries
    |> Array.map (fun entry -> entry.TestId, entry)
    |> Map.ofArray

  let fromEntries (entries: TestStatusEntry array) : TestStatusIndex =
    { Entries = entries
      Slots = buildSlots entries
      Index = buildIndex entries
      Projection = StatusEntriesProjectionState.Materialized }

module CachedViews =
  let empty = {
    StateVersion = 0L
    TestSummary = { Total = 0; Passed = 0; Failed = 0; Stale = 0; Running = 0; Disabled = 0; Enabled = true }
    FailureNarratives = Map.empty
    EditorAnnotations = Array.empty
  }

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
    RunRequests = Map.empty
    ResultGenerations = Map.empty
    Provenances = Map.empty
    Pause = LivePause.Live
    Scope = TestScope.EveryTest
    SourceLocations = Array.empty
    DiscoveredTests = Array.empty
    LastResults = Map.empty
    StatusIndex = TestStatusIndex.empty
    CoverageAnnotations = Array.empty
    RunPhases = Map.empty
    LastGeneration = RunGeneration.zero
    History = RunHistory.NeverRun
    AffectedTests = Set.empty
    Activation = LiveTestingActivation.Inactive
    CoverageDisplay = CoverageVisibility.Shown
    RunPolicies = RunPolicyDefaults.defaults
    DetectedProviders = []
    AssemblyLoadErrors = []
    FlakyHistory = Map.empty
    TestCoverageBitmaps = Map.empty
    Cached = CachedViews.empty
    LastDiscoveryTime = System.DateTimeOffset.MinValue
    DiscoveryGeneration = 0L
    SessionDiscovery = Map.empty
    LastDecision = None
  }

  let statusEntryIndex (state: LiveTestState) : Map<TestId, TestStatusEntry> =
    match Map.isEmpty state.StatusIndex.Index, Array.isEmpty state.StatusIndex.Entries with
    | true, false -> TestStatusIndex.buildIndex state.StatusIndex.Entries
    | _ -> state.StatusIndex.Index

  let tryFindStatusEntry (testId: TestId) (state: LiveTestState) =
    statusEntryIndex state
    |> Map.tryFind testId

  let orderedStatusEntries (state: LiveTestState) : TestStatusEntry array =
    match state.StatusIndex.Projection with
    | StatusEntriesProjectionState.Materialized -> state.StatusIndex.Entries
    | StatusEntriesProjectionState.Deferred ->
      let index = statusEntryIndex state
      match Map.isEmpty index with
      | true -> state.StatusIndex.Entries
      | false ->
        state.DiscoveredTests
        |> Array.choose (fun test -> Map.tryFind test.Id index)

  /// The session this cycle's `LiveTestState` belongs to, derived from
  /// `SessionDiscovery` (populated exclusively by that session's own
  /// `TestsDiscovered` merge — see `SageFsApp.fs`'s `TestsDiscovered`
  /// handler, which always routes into the target session's own cycle).
  /// A correctly-routed cycle carries exactly one key here; an empty or
  /// not-yet-attributed cycle returns `None`.
  let ownerSessionId (state: LiveTestState) : string option =
    state.SessionDiscovery |> Map.toList |> List.tryHead |> Option.map fst

  /// Every session now owns its own `LiveTestState` (routed by
  /// `SageFsApp.fs`'s per-session cycle resolution — see
  /// `SageFsModel.cycleForSession`), so a state handed to this function
  /// already belongs wholly to one session: there is nothing left to
  /// filter. `sessionId` is kept for source compatibility with existing
  /// callers (some of which still pass "" for "give me everything").
  let statusEntriesForSession (_sessionId: string) (state: LiveTestState) : TestStatusEntry array =
    orderedStatusEntries state

  let withStatusEntries (entries: TestStatusEntry array) (state: LiveTestState) : LiveTestState =
    { state with StatusIndex = TestStatusIndex.fromEntries entries }

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

  // Exhaustive over TestRunStatus: the old catch-all dropped
  // Detected/Queued/Skipped, so three detected tests reported Total=3 with
  // every other counter 0 — a claim with nothing behind it.
  type private Bucket = Passed | Failed | Stale | Running | Disabled

  let private bucketOf (status: TestRunStatus) =
    match status with
    | TestRunStatus.Passed _ -> Bucket.Passed
    | TestRunStatus.Failed _ -> Bucket.Failed
    | TestRunStatus.Stale
    | TestRunStatus.Detected
    | TestRunStatus.Skipped _ -> Bucket.Stale
    | TestRunStatus.Running
    | TestRunStatus.Queued -> Bucket.Running
    | TestRunStatus.PolicyDisabled -> Bucket.Disabled

  let private applyStatusCountDelta
    (delta: int)
    (status: TestRunStatus)
    (summary: TestSummary)
    : TestSummary =
    match bucketOf status with
    | Bucket.Passed -> { summary with Passed = summary.Passed + delta }
    | Bucket.Failed -> { summary with Failed = summary.Failed + delta }
    | Bucket.Stale -> { summary with Stale = summary.Stale + delta }
    | Bucket.Running -> { summary with Running = summary.Running + delta }
    | Bucket.Disabled -> { summary with Disabled = summary.Disabled + delta }

  let fromStatuses (activation: LiveTestingActivation) (statuses: TestRunStatus array) : TestSummary =
    let mutable passed = 0
    let mutable failed = 0
    let mutable stale = 0
    let mutable running = 0
    let mutable disabled = 0
    for s in statuses do
      match bucketOf s with
      | Bucket.Passed -> passed <- passed + 1
      | Bucket.Failed -> failed <- failed + 1
      | Bucket.Stale -> stale <- stale + 1
      | Bucket.Running -> running <- running + 1
      | Bucket.Disabled -> disabled <- disabled + 1
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
          // The test's run ended before it reported: there is no current
          // result, so it is neither running nor waiting to run — it is stale.
          | TestResult.NoResult _ -> Some TestRunStatus.Stale
        | None -> None
      match Set.contains testId state.AffectedTests with
      | true ->
        // `state` belongs wholly to one session (see `LiveTestState.ownerSessionId`),
        // so "is the owning session running" is just "is anything in this cycle running."
        let sessionRunning = TestRunPhase.isAnyRunning state.RunPhases
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
        PreviousStatus = prevStatus
        Provenance = ResultProvenance.Compiled })

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
      match state.StatusIndex.Slots.Count = state.StatusIndex.Entries.Length with
      | true -> state.StatusIndex.Slots
      | false -> TestStatusIndex.buildSlots state.StatusIndex.Entries

    let coversChangedIds =
      changedIds
      |> Seq.forall (fun testId ->
        match Map.tryFind testId slots with
        | Some index when index >= 0 && index < state.StatusIndex.Entries.Length ->
          state.StatusIndex.Entries[index].TestId = testId
        | _ -> false)

    match coversChangedIds with
    | true -> slots
    | false -> TestStatusIndex.buildSlots state.StatusIndex.Entries

  let patchStatusEntriesForChangedIds
    (previous: LiveTestState)
    (updated: LiveTestState)
    (changedIds: Set<TestId>)
    : LiveTestState * TestStatusEntry array =
    match Set.isEmpty changedIds, Array.isEmpty previous.StatusIndex.Entries with
    | true, _ ->
      { updated with
          StatusIndex = { updated.StatusIndex with Entries = previous.StatusIndex.Entries; Slots = previous.StatusIndex.Slots } }, Array.empty
    | _, true ->
      updated, Array.empty
    | _ ->
      let slots = statusEntrySlotsForChangedIds previous changedIds
      let changedEntries = ResizeArray<TestStatusEntry>()
      let statusEntries = Array.copy previous.StatusIndex.Entries

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
          StatusIndex =
            { Entries = statusEntries
              Slots = slots
              Index = TestStatusIndex.buildIndex statusEntries
              Projection = StatusEntriesProjectionState.Materialized } }, changedEntries.ToArray()

  let private patchBufferedStatusEntryIndexForChangedIds
    (previous: LiveTestState)
    (updated: LiveTestState)
    (changedIds: Set<TestId>)
    : LiveTestState * TestStatusEntry array =
    let previousIndex = LiveTestState.statusEntryIndex previous
    match Set.isEmpty changedIds, Map.isEmpty previousIndex with
    | true, _ ->
      { updated with
          StatusIndex =
            { Entries = previous.StatusIndex.Entries
              Slots = previous.StatusIndex.Slots
              Index = previousIndex
              Projection = previous.StatusIndex.Projection } }, Array.empty
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
        | true -> previous.StatusIndex.Projection
        | false -> StatusEntriesProjectionState.Deferred

      { updated with
          StatusIndex =
            { Entries = previous.StatusIndex.Entries
              Slots = previous.StatusIndex.Slots
              Index = index
              Projection = projectionState } }, changedEntries.ToArray()

  /// Merge multiple streamed result batches, then patch only the affected status
  /// entries once. This keeps every raw result fact while avoiding repeated
  /// O(all discovered tests) scans when several batches are buffered together.
  let mergeBufferedResultsWithUpdatedStatusEntriesAndChangedEntries
    (state: LiveTestState)
    (batches: TestRunResult array list)
    : LiveTestState * TestStatusEntry array =
    let nonEmptyBatches =
      batches |> List.filter (fun batch -> not (Array.isEmpty batch))
    match nonEmptyBatches, Array.isEmpty state.StatusIndex.Entries with
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
  let builtInDescriptions : ProviderDescription list =
    TestProviderCatalog.all
    |> List.map (fun capability ->
      match capability.Framework with
      | TestFramework.Expecto ->
        ProviderDescription.Custom {
          Name = capability.Framework
          AssemblyMarker = capability.AssemblyMarkers.Head
        }
      | _ ->
        ProviderDescription.AttributeBased {
          Name = capability.Framework
          TestAttributes = capability.ExecutableAttributes
          AssemblyMarker = capability.AssemblyMarkers.Head
        })

  let detectProviders
    (assemblies: AssemblyInfo list)
    : ProviderDescription list =
    let evidence =
      assemblies
      |> List.collect (fun asm ->
        asm.ReferencedAssemblies
        |> Array.map (fun reference -> reference.Name)
        |> Array.toList)
      |> fun referenced ->
        { ReferencedAssemblies = referenced
          LoadedAssemblies = assemblies |> List.map (fun asm -> asm.Name) }
    TestProviderCatalog.detect evidence
    |> List.choose (fun availability ->
      match availability with
      | ProviderAvailability.Referenced capability
      | ProviderAvailability.Loaded capability ->
        let description =
          builtInDescriptions
          |> List.tryFind (fun desc ->
            let marker =
              match desc with
              | ProviderDescription.AttributeBased value -> value.AssemblyMarker
              | ProviderDescription.Custom value -> value.AssemblyMarker
            marker = capability.AssemblyMarkers.Head)
        description
      )

// --- Scope ---

module TestScope =
  /// Whether an automatic run may touch this test.
  let allows (scope: TestScope) (test: TestCase) : bool =
    true

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

  /// What resuming runs: the tests that went stale while paused, that the scope allows.
  let resumeSelection (state: LiveTestState) : TestCase array =
    [||]

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
  | Explained of decision: LiveTestingDecision

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
    | TestResult.NoResult _ -> 0.0

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
    // Never reported last time: as unknown as a new test, so run it early.
    | TestResult.NoResult _ -> 1

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
  let causeFromTrigger filePath trigger =
    match trigger with
    | RunTrigger.Keystroke -> RerunCause.KeystrokeBuffered filePath
    | RunTrigger.FileSave -> RerunCause.FileSaved filePath
    | RunTrigger.ExplicitRun -> RerunCause.ExplicitRunRequested filePath

  let decide
    (state: LiveTestState)
    (trigger: RunTrigger)
    (changedSymbols: string list)
    (changedFilePath: string)
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
      | true when Array.isEmpty affected ->
        TestCycleDecision.Explained (
          LiveTestingDecision.fromSelection
            (causeFromTrigger changedFilePath trigger)
            SelectionPrecision.NoImpactedTests
            changedSymbols
            [||]
            [||]
            "No semantically affected tests were identified for this change.")
      | true ->
        let deferred =
          state.DiscoveredTests
          |> Array.filter (fun tc -> affectedSet.Contains tc.Id)
          |> Array.map (fun tc -> tc.FullName)
        TestCycleDecision.Explained (
          LiveTestingDecision.fromSelection
            (causeFromTrigger changedFilePath trigger)
            SelectionPrecision.SuppressedByPolicy
            changedSymbols
            [||]
            deferred
            "Affected tests were intentionally deferred by the current run policy.")
      | false ->
        TestCycleDecision.Explained (
          LiveTestingDecision.fromSelection
            (causeFromTrigger changedFilePath trigger)
            SelectionPrecision.ExactDependencyMatch
            changedSymbols
            (filtered |> Array.map (fun tc -> tc.FullName))
            [||]
            "Changed symbols mapped directly to impacted tests in the dependency graph.")

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

  /// Cancel any pending operation by bumping the generation so tryFire will discard it.
  let cancel (ch: DebounceChannel<'a>) =
    match ch.Pending with
    | None -> ch
    | Some _ -> { ch with CurrentGeneration = ch.CurrentGeneration + 1L }

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

  let treeSitterDelayMs = int Timeouts.liveTestTreeSitterDebounce.TotalMilliseconds * 1<ms>

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

  /// Whether a tick could fire anything: some channel holds a pending operation.
  /// The test-cycle tick exists only to fire these, so without one it has
  /// nothing to do and the daemon's timer can idle.
  let hasPending (db: TestCycleDebounce) =
    db.TreeSitter.Pending.IsSome || db.Fcs.Pending.IsSome

/// Shared payload for RunAffectedTests / RequestRebuild —
/// the 6 fields that both effect cases always carry together.
type TestRunRequest = {
  Tests: TestCase array
  Trigger: RunTrigger
  TreeSitterElapsed: System.TimeSpan
  FcsElapsed: System.TimeSpan
  SessionId: string option
  InstrumentationMaps: InstrumentationMap array
}

module TestRunRequest =
  let empty = {
    Tests = [||]
    Trigger = RunTrigger.Keystroke
    TreeSitterElapsed = System.TimeSpan.Zero
    FcsElapsed = System.TimeSpan.Zero
    SessionId = None
    InstrumentationMaps = [||]
  }

/// Payload for RequestFcsTypeCheck — the 5 fields that define a type-check request.
type TypeCheckRequest = {
  SessionId: string option
  FilePath: string
  Content: string option
  AnalysisIdentity: AnalysisIdentity option
  TreeSitterElapsed: System.TimeSpan
}

module TypeCheckRequest =
  let empty = {
    SessionId = None
    FilePath = ""
    Content = None
    AnalysisIdentity = None
    TreeSitterElapsed = System.TimeSpan.Zero
  }

/// Payload for `TestCycleEffect.EvalBufferThenRunAffected` — a sibling of
/// `TestRunRequest` (live-testing-asyoutype-plan.md §2/Brief 4) rather than
/// an extension of it, so every EXISTING `TestRunRequest` construction site
/// (RunAffectedTests/RequestRebuild) is untouched. `FilePath`/`Content` are
/// the debounced, type-check-passing buffer that must be identity-preserving
/// eval'd (Brief 3's `WorkerMessage.EvalLiveTestFile`) before `Run.Tests`
/// (already coverage/graph-selected by `decideAfterTypeCheck`) can be run
/// against FRESH code instead of the stale compiled DLL.
type EvalThenRunRequest = {
  FilePath: string
  Content: string
  Run: TestRunRequest
}

[<RequireQualifiedAccess>]
type TestCycleEffect =
  | RequestInitialDiscovery
  | ParseTreeSitter of content: string * filePath: string
  | RequestFcsTypeCheck of TypeCheckRequest
  | RunAffectedTests of TestRunRequest
  /// An explicitly requested run whose generation was allocated at request
  /// time (`RequestedRuns.request`). The performer starts it WITH that
  /// generation (`TuiEvent.TestRunStartedAt`) instead of bumping a new one, so
  /// the run's identity survives from request to results.
  | RunRequestedTests of request: TestRunRequest * generation: RunGeneration
  /// Identity-preserving eval of the edited buffer, then run of the tests
  /// `Run.Tests` already selected as affected — replaces the compiled-DLL
  /// decision for a compiled `.fs` file on BOTH Keystroke and FileSave (see
  /// `TestCycleEffects.redirectToEvalBuffer`). `.fsx`/expression paths, and
  /// any compiled-file decision made without known buffer content, are left
  /// as plain `RunAffectedTests`/`RequestRebuild` — unchanged.
  | EvalBufferThenRunAffected of EvalThenRunRequest
  | CancelRebuild of sessionId: string option * generation: int64
  | RequestRebuild of generation: int64 * TestRunRequest
  | RegisterFileWatcher of sessionId: string * directory: string
  | DisposeFileWatcher of sessionId: string * directory: string

