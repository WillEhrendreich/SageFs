namespace SageFs.Features.LiveTesting

open System
open System.IO
open System.Reflection
open System.Security.Cryptography
open System.Text

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

/// Configuration constants for the live testing pipeline.
[<RequireQualifiedAccess>]
module LiveTestingDefaults =
  /// Default test module identifier for detecting test functions by namespace.
  let [<Literal>] TestModuleIdentifier = ".Tests."
  /// Default framework string for Expecto-based projects.
  let [<Literal>] Framework = "expecto"

// --- Stable Test Identity ---

type TestId = private TestId of string

module TestId =
  /// 16 hex chars = 64 bits of entropy from SHA256. Collision probability
  /// is negligible for projects with < 10^9 tests (birthday bound ~2^32).
  let create (fullName: string) (framework: string) =
    let input = sprintf "%s|%s" fullName framework
    let bytes = Encoding.UTF8.GetBytes(input)
    let hash = SHA256.HashData(bytes)
    TestId(Convert.ToHexString(hash).Substring(0, 16))

  let value (TestId id) = id

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
  Framework: string
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
  Framework: string
  Category: TestCategory
  CurrentPolicy: RunPolicy
  Status: TestRunStatus
  PreviousStatus: TestRunStatus
}

// --- Provider Descriptions (pure data — stored in Elm model) ---

type AttributeProviderDescription = {
  Name: string
  TestAttributes: string list
  AssemblyMarker: string
}

type CustomProviderDescription = {
  Name: string
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

type CoverageAnnotation = {
  Symbol: string
  FilePath: string
  DefinitionLine: int
  Status: CoverageStatus
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
  BranchId: int
}

type CoverageState = {
  Slots: SequencePoint array
  Hits: bool array
}

// --- Pipeline Timing ---

[<RequireQualifiedAccess>]
type PipelineDepth =
  | TreeSitterOnly of treeSitter: TimeSpan
  | ThroughFcs of treeSitter: TimeSpan * fcs: TimeSpan
  | ThroughExecution of treeSitter: TimeSpan * fcs: TimeSpan * execution: TimeSpan

type PipelineTiming = {
  Depth: PipelineDepth
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
  | Covered
  | NotCovered

[<Struct>]
type LineAnnotation = {
  Line: int
  Icon: GutterIcon
  Tooltip: string
}

// --- Test Summary ---

type TestSummary = {
  Total: int
  Passed: int
  Failed: int
  Stale: int
  Running: int
  Disabled: int
}

// --- Run Generation & Phase (replaces IsRunning: bool) ---

[<Struct>]
type RunGeneration = RunGeneration of int

module RunGeneration =
  let zero = RunGeneration 0
  let next (RunGeneration n) = RunGeneration (n + 1)
  let value (RunGeneration n) = n

type TestRunPhase =
  | Idle
  | Running of generation: RunGeneration
  | RunningButEdited of generation: RunGeneration

/// Why did results arrive the way they did?
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

type LiveTestState = {
  SourceLocations: SourceTestLocation array
  DiscoveredTests: TestCase array
  LastResults: Map<TestId, TestRunResult>
  StatusEntries: TestStatusEntry array
  CoverageAnnotations: CoverageAnnotation array
  RunPhase: TestRunPhase
  LastGeneration: RunGeneration
  History: RunHistory
  AffectedTests: Set<TestId>
  Activation: LiveTestingActivation
  CoverageDisplay: CoverageVisibility
  RunPolicies: Map<TestCategory, RunPolicy>
  DetectedProviders: ProviderDescription list
  CachedEditorAnnotations: LineAnnotation array
  AssemblyLoadErrors: AssemblyLoadError list
}

module LiveTestState =
  let empty = {
    SourceLocations = Array.empty
    DiscoveredTests = Array.empty
    LastResults = Map.empty
    StatusEntries = Array.empty
    CoverageAnnotations = Array.empty
    RunPhase = Idle
    LastGeneration = RunGeneration.zero
    History = RunHistory.NeverRun
    AffectedTests = Set.empty
    Activation = LiveTestingActivation.Active
    CoverageDisplay = CoverageVisibility.Shown
    RunPolicies = RunPolicyDefaults.defaults
    DetectedProviders = []
    CachedEditorAnnotations = Array.empty
    AssemblyLoadErrors = []
  }

// --- Gutter Rendering Pure Functions ---

module GutterIcon =
  let toChar = function
    | GutterIcon.TestDiscovered -> '\u25C6'
    | GutterIcon.TestPassed -> '\u2713'
    | GutterIcon.TestFailed -> '\u2717'
    | GutterIcon.TestRunning -> '\u27F3'
    | GutterIcon.TestSkipped -> '\u25CB'
    | GutterIcon.Covered -> '\u258E'
    | GutterIcon.NotCovered -> '\u00B7'

  let toColorIndex = function
    | GutterIcon.TestDiscovered -> 33uy
    | GutterIcon.TestPassed -> 34uy
    | GutterIcon.TestFailed -> 160uy
    | GutterIcon.TestRunning -> 75uy
    | GutterIcon.TestSkipped -> 242uy
    | GutterIcon.Covered -> 34uy
    | GutterIcon.NotCovered -> 160uy

  let toLabel = function
    | GutterIcon.TestDiscovered -> "TestDiscovered"
    | GutterIcon.TestPassed -> "TestPassed"
    | GutterIcon.TestFailed -> "TestFailed"
    | GutterIcon.TestRunning -> "TestRunning"
    | GutterIcon.TestSkipped -> "TestSkipped"
    | GutterIcon.Covered -> "Covered"
    | GutterIcon.NotCovered -> "NotCovered"

  let parseLabel = function
    | "TestDiscovered" -> Some GutterIcon.TestDiscovered
    | "TestPassed" -> Some GutterIcon.TestPassed
    | "TestFailed" -> Some GutterIcon.TestFailed
    | "TestRunning" -> Some GutterIcon.TestRunning
    | "TestSkipped" -> Some GutterIcon.TestSkipped
    | "Covered" -> Some GutterIcon.Covered
    | "NotCovered" -> Some GutterIcon.NotCovered
    | _ -> None

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
  let empty = { Total = 0; Passed = 0; Failed = 0; Stale = 0; Running = 0; Disabled = 0 }

  let fromStatuses (statuses: TestRunStatus array) : TestSummary =
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
      Disabled = disabled }

  let toStatusBar (s: TestSummary) : string =
    if s.Total = 0 then "Tests: none"
    elif s.Failed > 0 then
      sprintf "Tests: %d/%d \u2717%d" s.Passed s.Total s.Failed
    elif s.Running > 0 then
      sprintf "Tests: %d/%d \u27F3%d" s.Passed s.Total s.Running
    elif s.Stale > 0 then
      sprintf "Tests: %d/%d \u25CF%d" s.Passed s.Total s.Stale
    else
      sprintf "Tests: %d/%d \u2713" s.Passed s.Total

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
    (entries: TestStatusEntry array)
    : TestResultsBatchPayload =
    let summary =
      entries
      |> Array.map (fun e -> e.Status)
      |> TestSummary.fromStatuses
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
      if returned >= requested then
        BatchCompletion.Complete(requested, returned)
      else
        BatchCompletion.Partial(requested, returned)

  let isEmpty (p: TestResultsBatchPayload) =
    Array.isEmpty p.Entries

module CategoryDetection =
  let categorize
    (labels: string list)
    (fullName: string)
    (_framework: string)
    (referencedAssemblies: string array)
    : TestCategory =
    let labelLower = labels |> List.map (fun l -> l.ToLowerInvariant())
    if labelLower |> List.exists (fun l -> l.Contains "integration") then
      TestCategory.Integration
    elif labelLower |> List.exists (fun l -> l.Contains "browser" || l.Contains "e2e" || l.Contains "ui") then
      TestCategory.Browser
    elif labelLower |> List.exists (fun l -> l.Contains "benchmark") then
      TestCategory.Benchmark
    elif labelLower |> List.exists (fun l -> l.Contains "property") then
      TestCategory.Property
    elif labelLower |> List.exists (fun l -> l.Contains "architecture" || l.Contains "arch") then
      TestCategory.Architecture
    elif referencedAssemblies |> Array.exists (fun a -> a.Contains "Microsoft.Playwright") then
      TestCategory.Browser
    elif referencedAssemblies |> Array.exists (fun a -> a.Contains "BenchmarkDotNet") then
      TestCategory.Benchmark
    elif fullName.ToLowerInvariant().Contains "integration" then
      TestCategory.Integration
    elif fullName.ToLowerInvariant().Contains "browser" || fullName.ToLowerInvariant().Contains "e2e" then
      TestCategory.Browser
    else
      TestCategory.Unit

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
  let private reachableFrom (callGraph: Map<string, string array>) (start: string) : string list =
    let visited = System.Collections.Generic.HashSet<string>()
    let queue = System.Collections.Generic.Queue<string>()
    queue.Enqueue(start)
    visited.Add(start) |> ignore
    while queue.Count > 0 do
      let current = queue.Dequeue()
      match Map.tryFind current callGraph with
      | Some callees ->
        for callee in callees do
          if visited.Add(callee) then
            queue.Enqueue(callee)
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

  /// Build a direct dependency graph from extracted FCS symbol uses.
  /// `testModuleIdentifier` (e.g. ".Tests.") identifies test function namespaces.
  /// Returns an inverted index: production symbol → test IDs that reference it.
  let buildFromSymbolUses
    (testModuleIdentifier: string)
    (framework: string)
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
             if i < testDefs.Length - 1 then testDefs.[i + 1].StartLine - 1
             else System.Int32.MaxValue
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
    { SymbolToTests = invertedIndex
      TransitiveCoverage = invertedIndex
      PerFileIndex = Map.empty
      SourceVersion = 1 }

module LiveTesting =
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
      let status =
        match Map.tryFind test.Category state.RunPolicies with
        | Some RunPolicy.Disabled -> TestRunStatus.PolicyDisabled
        | _ ->
          if Set.contains test.Id state.AffectedTests && TestRunPhase.isRunning state.RunPhase then
            TestRunStatus.Running
          elif Set.contains test.Id state.AffectedTests then
            match Map.tryFind test.Id state.LastResults with
            | Some r when r.Result <> TestResult.NotRun -> TestRunStatus.Stale
            | _ -> TestRunStatus.Queued
          else
            match Map.tryFind test.Id state.LastResults with
            | Some r ->
              match r.Result with
              | TestResult.Passed d -> TestRunStatus.Passed d
              | TestResult.Failed (f, d) -> TestRunStatus.Failed (f, d)
              | TestResult.Skipped reason -> TestRunStatus.Skipped reason
              | TestResult.NotRun -> TestRunStatus.Detected
            | None -> TestRunStatus.Detected
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
    if Array.isEmpty existing then incoming
    elif Array.isEmpty incoming then existing
    else
      let incomingById = incoming |> Array.map (fun t -> t.Id, t) |> Map.ofArray
      let merged =
        existing
        |> Array.fold (fun acc t ->
          if Map.containsKey t.Id acc then acc
          else Map.add t.Id t acc) incomingById
      merged |> Map.values |> Seq.toArray

  let mergeResults (state: LiveTestState) (results: TestRunResult array) : LiveTestState =
    if Array.isEmpty results then state
    else
      let newResults =
        results
        |> Array.fold (fun acc r -> Map.add r.TestId r acc) state.LastResults
      let maxDuration =
        results
        |> Array.map (fun r ->
          match r.Result with
          | TestResult.Passed d -> d
          | TestResult.Failed (_, d) -> d
          | _ -> System.TimeSpan.Zero)
        |> Array.max
      let previousStatuses =
        state.StatusEntries
        |> Array.map (fun e -> e.TestId, e.Status)
        |> Map.ofArray
      // Use RunPhase to determine if results are stale.
      // RunningButEdited means code changed mid-run — keep AffectedTests so status shows Stale.
      let newPhase, freshness =
        TestRunPhase.onResultsArrived state.LastGeneration state.RunPhase
      let alreadyStale =
        match freshness with
        | Fresh -> false
        | StaleCodeEdited | StaleWrongGeneration -> true
      let updatedState =
        { state with
            LastResults = newResults
            RunPhase = newPhase
            AffectedTests = if alreadyStale then state.AffectedTests else Set.empty
            History = RunHistory.PreviousRun maxDuration }
      { updatedState with StatusEntries = computeStatusEntriesWithHistory previousStatuses updatedState }

  let computeStatusEntries (state: LiveTestState) : TestStatusEntry array =
    computeStatusEntriesWithHistory Map.empty state

  let markAffected (testIds: TestId array) (state: LiveTestState) : LiveTestState =
    let affected = testIds |> Set.ofArray |> Set.union state.AffectedTests
    let previousStatuses =
      state.StatusEntries
      |> Array.map (fun e -> e.TestId, e.Status)
      |> Map.ofArray
    let updatedState = { state with AffectedTests = affected }
    { updatedState with StatusEntries = computeStatusEntriesWithHistory previousStatuses updatedState }

  let annotationsForFile (filePath: string) (state: LiveTestState) : LineAnnotation array =
    let tsAnnotations =
      state.SourceLocations
      |> Array.filter (fun sl -> sl.FilePath = filePath)
      |> Array.map (fun sl ->
        { Line = sl.Line
          Icon = GutterIcon.TestDiscovered
          Tooltip = sprintf "Test: %s (detected)" sl.AttributeName })
    let resultAnnotations =
      state.StatusEntries
      |> Array.choose (fun e ->
        match e.Origin with
        | TestOrigin.SourceMapped (f, line) when f = filePath ->
          Some (StatusToGutter.toAnnotation line e.DisplayName e.Status)
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
    if slashIdx > 0 then
      let prefix = fullName.Substring(0, slashIdx)
      let lastDot = prefix.LastIndexOf('.')
      if lastDot >= 0 then prefix.Substring(lastDot + 1)
      else prefix
    else
      let parts = fullName.Split('.')
      if parts.Length > 0 then parts.[parts.Length - 1]
      else fullName

  /// Does this attribute name match the given test framework?
  let attributeMatchesFramework (framework: string) (attrName: string) =
    match framework with
    | "expecto" -> attrName = "Tests" || attrName = "TestsAttribute"
    | "xunit" ->
      attrName = "Fact" || attrName = "FactAttribute"
      || attrName = "Theory" || attrName = "TheoryAttribute"
    | "nunit" ->
      attrName = "Test" || attrName = "TestAttribute"
      || attrName = "TestCase" || attrName = "TestCaseAttribute"
    | "mstest" ->
      attrName = "TestMethod" || attrName = "TestMethodAttribute"
      || attrName = "DataTestMethod" || attrName = "DataTestMethodAttribute"
    | "tunit" -> attrName = "Test" || attrName = "TestAttribute"
    | _ -> false

  /// Merge tree-sitter source locations into discovered tests.
  /// Matches by function name against the test's FullName suffix.
  /// Tests already source-mapped are preserved unchanged.
  let mergeSourceLocations
    (locations: SourceTestLocation array)
    (tests: TestCase array)
    : TestCase array =
    if Array.isEmpty locations then tests
    else
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
          | Some locs ->
            let matchingLoc =
              locs
              |> Array.tryFind (fun loc -> attributeMatchesFramework test.Framework loc.AttributeName)
              |> Option.orElseWith (fun () -> locs |> Array.tryHead)
            match matchingLoc with
            | Some loc -> { test with Origin = TestOrigin.SourceMapped(loc.FilePath, loc.Line) }
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
        if allPass then CoverageHealth.AllPassing else CoverageHealth.SomeFailing
      CoverageStatus.Covered (tests.Length, health)

  let computeAll
    (graph: TestDependencyGraph)
    (results: Map<TestId, TestRunResult>)
    : Map<string, CoverageStatus> =
    graph.TransitiveCoverage
    |> Map.map (fun symbol tests ->
      if Array.isEmpty tests then CoverageStatus.NotCovered
      else
        let health =
          let allPass =
            tests |> Array.forall (fun tid ->
              match Map.tryFind tid results with
              | Some r ->
                match r.Result with
                | TestResult.Passed _ -> true
                | _ -> false
              | None -> false)
          if allPass then CoverageHealth.AllPassing else CoverageHealth.SomeFailing
        CoverageStatus.Covered (tests.Length, health))

module CoverageComputation =
  let computeLineCoverage (state: CoverageState) (file: string) (line: int) : LineCoverage =
    let matching =
      state.Slots
      |> Array.mapi (fun i slot -> (slot, state.Hits.[i]))
      |> Array.filter (fun (slot, _) -> slot.File = file && slot.Line = line)
    if Array.isEmpty matching then LineCoverage.NotCovered
    else
      let total = matching.Length
      let covered = matching |> Array.filter snd |> Array.length
      if covered = total then LineCoverage.FullyCovered
      elif covered = 0 then LineCoverage.NotCovered
      else LineCoverage.PartiallyCovered (covered, total)

module PipelineTiming =
  let treeSitterMs (t: PipelineTiming) : float =
    match t.Depth with
    | PipelineDepth.TreeSitterOnly ts -> ts.TotalMilliseconds
    | PipelineDepth.ThroughFcs (ts, _) -> ts.TotalMilliseconds
    | PipelineDepth.ThroughExecution (ts, _, _) -> ts.TotalMilliseconds

  let fcsMs (t: PipelineTiming) : float =
    match t.Depth with
    | PipelineDepth.TreeSitterOnly _ -> 0.0
    | PipelineDepth.ThroughFcs (_, fcs) -> fcs.TotalMilliseconds
    | PipelineDepth.ThroughExecution (_, fcs, _) -> fcs.TotalMilliseconds

  let executionMs (t: PipelineTiming) : float =
    match t.Depth with
    | PipelineDepth.ThroughExecution (_, _, exec) -> exec.TotalMilliseconds
    | _ -> 0.0

  let totalMs (t: PipelineTiming) : float =
    treeSitterMs t + fcsMs t + executionMs t

  /// Extract tree-sitter elapsed from any pipeline depth.
  let accumulatedTsElapsed (t: PipelineTiming option) : System.TimeSpan =
    match t with
    | Some timing ->
      match timing.Depth with
      | PipelineDepth.TreeSitterOnly ts
      | PipelineDepth.ThroughFcs (ts, _)
      | PipelineDepth.ThroughExecution (ts, _, _) -> ts
    | None -> System.TimeSpan.Zero

  /// Extract FCS elapsed from pipeline depth (Zero if FCS hasn't run).
  let accumulatedFcsElapsed (t: PipelineTiming option) : System.TimeSpan =
    match t with
    | Some timing ->
      match timing.Depth with
      | PipelineDepth.ThroughFcs (_, fcs)
      | PipelineDepth.ThroughExecution (_, fcs, _) -> fcs
      | _ -> System.TimeSpan.Zero
    | None -> System.TimeSpan.Zero

  let toStatusBar (t: PipelineTiming) : string =
    match t.Depth with
    | PipelineDepth.TreeSitterOnly ts ->
      sprintf "TS:%.1fms" ts.TotalMilliseconds
    | PipelineDepth.ThroughFcs (ts, fcs) ->
      sprintf "TS:%.1fms | FCS:%.0fms" ts.TotalMilliseconds fcs.TotalMilliseconds
    | PipelineDepth.ThroughExecution (ts, fcs, exec) ->
      sprintf "TS:%.1fms | FCS:%.0fms | Run:%.0fms (%d)"
        ts.TotalMilliseconds fcs.TotalMilliseconds exec.TotalMilliseconds t.AffectedTests

module TestProviderDescriptions =
  let builtInDescriptions : ProviderDescription list = [
    ProviderDescription.AttributeBased {
      Name = "xunit"; TestAttributes = ["Fact"; "Theory"]
      AssemblyMarker = "xunit.core"
    }
    ProviderDescription.AttributeBased {
      Name = "nunit"; TestAttributes = ["Test"; "TestCase"; "TestCaseSource"]
      AssemblyMarker = "nunit.framework"
    }
    ProviderDescription.AttributeBased {
      Name = "mstest"; TestAttributes = ["TestMethod"; "DataTestMethod"]
      AssemblyMarker = "Microsoft.VisualStudio.TestPlatform.TestFramework"
    }
    ProviderDescription.AttributeBased {
      Name = "tunit"; TestAttributes = ["Test"]
      AssemblyMarker = "TUnit.Core"
    }
    ProviderDescription.Custom {
      Name = "expecto"; AssemblyMarker = "Expecto"
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
    if Set.isEmpty affected then state
    else
      let previousStatuses =
        state.StatusEntries
        |> Array.map (fun e -> e.TestId, e.Status)
        |> Map.ofArray
      let updatedState = { state with AffectedTests = Set.union state.AffectedTests affected }
      { updatedState with StatusEntries = LiveTesting.computeStatusEntriesWithHistory previousStatuses updatedState }

// --- Pipeline Orchestrator ---

[<RequireQualifiedAccess>]
type PipelineDecision =
  | Skip of reason: string
  | TreeSitterOnly
  | FullPipeline of affectedTestIds: TestId array

module TestPrioritization =
  let private durationMs (r: TestResult) =
    match r with
    | TestResult.Passed d -> d.TotalMilliseconds
    | TestResult.Failed (_, d) -> d.TotalMilliseconds
    | TestResult.Skipped _ -> 0.0
    | TestResult.NotRun -> 0.0

  /// Sort tests: failed-first, then fastest-first within same outcome.
  /// Tests without results go last.
  let prioritize (lastResults: Map<TestId, TestRunResult>) (tests: TestCase array) : TestCase array =
    tests
    |> Array.sortBy (fun tc ->
      match Map.tryFind tc.Id lastResults with
      | Some result ->
        let failedOrder =
          match result.Result with
          | TestResult.Failed _ -> 0
          | TestResult.Skipped _ -> 1
          | TestResult.Passed _ -> 2
          | TestResult.NotRun -> 3
        (failedOrder, durationMs result.Result)
      | None -> (4, 0.0))

module PipelineOrchestrator =
  let decide
    (state: LiveTestState)
    (trigger: RunTrigger)
    (changedSymbols: string list)
    (depGraph: TestDependencyGraph)
    : PipelineDecision =
    if state.Activation = LiveTestingActivation.Inactive then
      PipelineDecision.Skip "Live testing disabled"
    elif TestRunPhase.isRunning state.RunPhase then
      PipelineDecision.Skip "Pipeline already running"
    elif Array.isEmpty state.DiscoveredTests then
      PipelineDecision.TreeSitterOnly
    else
      let affected = TestDependencyGraph.findAffected changedSymbols depGraph
      let affectedSet = Set.ofArray affected
      let filtered =
        state.DiscoveredTests
        |> Array.filter (fun tc -> affectedSet.Contains tc.Id)
        |> LiveTesting.filterByPolicy state.RunPolicies trigger
      if Array.isEmpty filtered then
        PipelineDecision.TreeSitterOnly
      else
        PipelineDecision.FullPipeline (filtered |> Array.map (fun tc -> tc.Id))

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
  DelayMs: int
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

  let submit (payload: 'a) (delayMs: int) (now: DateTimeOffset) (ch: DebounceChannel<'a>) =
    let gen = ch.CurrentGeneration + 1L
    { ch with
        CurrentGeneration = gen
        Pending = Some { Payload = payload; RequestedAt = now; DelayMs = delayMs; Generation = gen } }

  let tryFire (now: DateTimeOffset) (ch: DebounceChannel<'a>) =
    match ch.Pending with
    | None -> None, ch
    | Some op ->
      let elapsed = (now - op.RequestedAt).TotalMilliseconds
      if op.Generation < ch.CurrentGeneration then
        None, { ch with Pending = None }
      elif elapsed >= float op.DelayMs then
        Some op.Payload, { ch with Pending = None; LastCompleted = Some now }
      else
        None, ch

  let isStale (ch: DebounceChannel<'a>) =
    match ch.Pending with
    | Some op -> op.Generation < ch.CurrentGeneration
    | None -> false

type PipelineDebounce = {
  TreeSitter: DebounceChannel<string>
  Fcs: DebounceChannel<string>
}

module PipelineDebounce =
  let empty = {
    TreeSitter = DebounceChannel.empty
    Fcs = DebounceChannel.empty
  }

  let treeSitterDelayMs = 50

  let onKeystroke (content: string) (filePath: string) (fcsDelay: int) (now: DateTimeOffset) (db: PipelineDebounce) =
    { db with
        TreeSitter = db.TreeSitter |> DebounceChannel.submit content treeSitterDelayMs now
        Fcs = db.Fcs |> DebounceChannel.submit filePath fcsDelay now }

  let onFileSave (filePath: string) (now: DateTimeOffset) (db: PipelineDebounce) =
    { db with
        Fcs = db.Fcs |> DebounceChannel.submit filePath 50 now }

  let tick (now: DateTimeOffset) (db: PipelineDebounce) =
    let tsPayload, tsChannel = DebounceChannel.tryFire now db.TreeSitter
    let fcsPayload, fcsChannel = DebounceChannel.tryFire now db.Fcs
    (tsPayload, fcsPayload), { db with TreeSitter = tsChannel; Fcs = fcsChannel }

[<RequireQualifiedAccess>]
type PipelineEffect =
  | ParseTreeSitter of content: string * filePath: string
  | RequestFcsTypeCheck of filePath: string * treeSitterElapsed: System.TimeSpan
  | RunAffectedTests of tests: TestCase array * trigger: RunTrigger * treeSitterElapsed: System.TimeSpan * fcsElapsed: System.TimeSpan

module PipelineEffects =
  let fromTick
    (tsPayload: string option)
    (fcsPayload: string option)
    (filePath: string)
    (lastTiming: PipelineTiming option)
    : PipelineEffect list =
    [ match tsPayload with
      | Some content -> PipelineEffect.ParseTreeSitter(content, filePath)
      | None -> ()
      match fcsPayload with
      | Some fp ->
        let tsElapsed = PipelineTiming.accumulatedTsElapsed lastTiming
        PipelineEffect.RequestFcsTypeCheck(fp, tsElapsed)
      | None -> () ]

  let afterTypeCheck
    (changedSymbols: string list)
    (trigger: RunTrigger)
    (depGraph: TestDependencyGraph)
    (state: LiveTestState)
    (lastTiming: PipelineTiming option)
    : PipelineEffect option =
    if state.Activation = LiveTestingActivation.Inactive then None
    else
      let affected = TestDependencyGraph.findAffected changedSymbols depGraph
      if Array.isEmpty affected then None
      else
        let affectedSet = Set.ofArray affected
        let affectedTests =
          state.DiscoveredTests
          |> Array.filter (fun tc -> affectedSet.Contains tc.Id)
        let filtered =
          PolicyFilter.filterTests state.RunPolicies trigger affectedTests
          |> TestPrioritization.prioritize state.LastResults
        if Array.isEmpty filtered then None
        else
          let tsElapsed = PipelineTiming.accumulatedTsElapsed lastTiming
          let fcsElapsed = PipelineTiming.accumulatedFcsElapsed lastTiming
          Some (PipelineEffect.RunAffectedTests(filtered, trigger, tsElapsed, fcsElapsed))

/// Adaptive debounce configuration.
type AdaptiveDebounceConfig = {
  BaseTreeSitterMs: float
  BaseFcsMs: float
  MaxFcsMs: float
  BackoffMultiplier: float
  ResetAfterSuccessCount: int
}

module AdaptiveDebounceConfig =
  let defaults = {
    BaseTreeSitterMs = 50.0
    BaseFcsMs = 300.0
    MaxFcsMs = 2000.0
    BackoffMultiplier = 1.5
    ResetAfterSuccessCount = 3
  }

/// Tracks FCS cancellation history for adaptive backoff.
type AdaptiveDebounce = {
  Config: AdaptiveDebounceConfig
  ConsecutiveFcsCancels: int
  ConsecutiveFcsSuccesses: int
  CurrentFcsDelayMs: float
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
    if newSuccesses >= ad.Config.ResetAfterSuccessCount then
      { ad with
          ConsecutiveFcsCancels = 0
          ConsecutiveFcsSuccesses = 0
          CurrentFcsDelayMs = ad.Config.BaseFcsMs }
    else
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
  let private toExtractedSymbolUse (sr: SymbolReference) : ExtractedSymbolUse =
    let parts = sr.SymbolFullName.Split('.')
    let displayName = if parts.Length > 0 then parts[parts.Length - 1] else sr.SymbolFullName
    { FullName = sr.SymbolFullName
      DisplayName = displayName
      UseKind = sr.UseKind
      StartLine = sr.Line
      EndLine = sr.Line }

  let buildIndex (testModuleIdentifier: string) (framework: string) (refs: SymbolReference list) : Map<string, TestId array> =
    let extracted = refs |> List.map toExtractedSymbolUse |> Array.ofList
    let graph = TestDependencyGraph.buildFromSymbolUses testModuleIdentifier framework extracted
    graph.SymbolToTests

  let updateGraph (testModuleIdentifier: string) (framework: string) (newRefs: SymbolReference list) (filePath: string) (graph: TestDependencyGraph) : TestDependencyGraph =
    let newFileIndex = buildIndex testModuleIdentifier framework newRefs
    let updatedPerFile = Map.add filePath newFileIndex graph.PerFileIndex
    let merged = TestDependencyGraph.mergePerFileIndexes updatedPerFile
    { graph with
        SymbolToTests = merged
        TransitiveCoverage = merged
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

  let private symbolNames (refs: SymbolReference list) =
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

/// Wraps LiveTestState + PipelineDebounce + TestDependencyGraph + adaptive
/// debounce + file analysis cache into a single state record for the Elm loop.
type LiveTestPipelineState = {
  TestState: LiveTestState
  Debounce: PipelineDebounce
  DepGraph: TestDependencyGraph
  ActiveFile: string option
  ChangedSymbols: string list
  LastTreeSitterResult: SourceTestLocation array option
  AnalysisCache: FileAnalysisCache
  AdaptiveDebounce: AdaptiveDebounce
  LastTrigger: RunTrigger
  LastTiming: PipelineTiming option
}

module LiveTestPipelineState =
  let empty = {
    TestState = LiveTestState.empty
    Debounce = PipelineDebounce.empty
    DepGraph = TestDependencyGraph.empty
    ActiveFile = None
    ChangedSymbols = []
    LastTreeSitterResult = None
    AnalysisCache = FileAnalysisCache.empty
    AdaptiveDebounce = AdaptiveDebounce.createDefault()
    LastTrigger = RunTrigger.Keystroke
    LastTiming = None
  }

  let liveTestingStatusBar (state: LiveTestPipelineState) : string =
    let timing =
      match state.LastTiming with
      | None -> ""
      | Some t -> PipelineTiming.toStatusBar t
    let statuses = state.TestState.StatusEntries |> Array.map (fun e -> e.Status)
    let summary = TestSummary.fromStatuses statuses |> TestSummary.toStatusBar
    match timing, summary with
    | "", "Tests: none" -> ""
    | "", s -> s
    | t, "Tests: none" -> t
    | t, s -> sprintf "%s | %s" t s

  let currentFcsDelay (s: LiveTestPipelineState) =
    AdaptiveDebounce.currentFcsDelay s.AdaptiveDebounce

  let onKeystroke (content: string) (filePath: string) (now: DateTimeOffset) (s: LiveTestPipelineState) =
    let fcsDelay = int (currentFcsDelay s)
    let db = s.Debounce |> PipelineDebounce.onKeystroke content filePath fcsDelay now
    // When edits arrive while tests are running, mark phase as edited so in-flight results are stale.
    let ts = { s.TestState with RunPhase = TestRunPhase.onEdit s.TestState.RunPhase }
    { s with Debounce = db; TestState = ts; ActiveFile = Some filePath; LastTrigger = RunTrigger.Keystroke }

  let onFileSave (filePath: string) (now: DateTimeOffset) (s: LiveTestPipelineState) =
    let db = s.Debounce |> PipelineDebounce.onFileSave filePath now
    let ts = { s.TestState with RunPhase = TestRunPhase.onEdit s.TestState.RunPhase }
    { s with Debounce = db; TestState = ts; ActiveFile = Some filePath; LastTrigger = RunTrigger.FileSave }

  let onFcsComplete (filePath: string) (refs: SymbolReference list) (s: LiveTestPipelineState) =
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

  let onFcsCanceled (s: LiveTestPipelineState) =
    { s with AdaptiveDebounce = AdaptiveDebounce.onFcsCanceled s.AdaptiveDebounce }

  /// Ticks the debounce channels and produces pipeline effects.
  /// Returns (effects, updatedState).
  /// Note: afterTypeCheck is NOT called here — it runs after FCS completes,
  /// not when the FCS request is emitted (avoids stale symbol data).
  let tick (now: DateTimeOffset) (s: LiveTestPipelineState) =
    match s.ActiveFile with
    | None -> [], s
    | Some filePath ->
      let (tsPayload, fcsPayload), db = s.Debounce |> PipelineDebounce.tick now
      let effects = PipelineEffects.fromTick tsPayload fcsPayload filePath s.LastTiming
      effects, { s with Debounce = db }

  /// Handles an FCS type-check result: updates state and produces effects.
  /// Success: updates symbol graph, analysis cache, adaptive debounce, then
  /// calls afterTypeCheck to emit RunAffectedTests if symbols changed.
  /// Failed: no-op (diagnostics handled elsewhere).
  /// Cancelled: updates adaptive debounce backoff.
  let handleFcsResult
    (result: FcsTypeCheckResult)
    (s: LiveTestPipelineState)
    : PipelineEffect list * LiveTestPipelineState =
    match result with
    | FcsTypeCheckResult.Success (filePath, refs) ->
      let s1 = onFcsComplete filePath refs s
      let trigger = s.LastTrigger
      let effect = PipelineEffects.afterTypeCheck s1.ChangedSymbols trigger s1.DepGraph s1.TestState s1.LastTiming
      let effects = effect |> Option.toList
      effects, s1
    | FcsTypeCheckResult.Failed _ ->
      [], s
    | FcsTypeCheckResult.Cancelled _ ->
      [], onFcsCanceled s

  /// When the hot-reload hook identifies affected tests (via method name matching),
  /// look up full TestCase objects, filter by RunPolicy, and produce a RunAffectedTests
  /// effect if any tests should run. This bridges the gap between discovery and execution
  /// for MCP-triggered evals (which don't go through the FCS type-check pipeline).
  let triggerExecutionForAffected
    (affectedIds: TestId array)
    (trigger: RunTrigger)
    (s: LiveTestPipelineState)
    : PipelineEffect list =
    if Array.isEmpty affectedIds || s.TestState.Activation = LiveTestingActivation.Inactive then []
    else
      let affectedIdSet = Set.ofArray affectedIds
      let affectedTests =
        s.TestState.DiscoveredTests
        |> Array.filter (fun tc -> affectedIdSet.Contains tc.Id)
      let filtered =
        PolicyFilter.filterTests s.TestState.RunPolicies trigger affectedTests
        |> TestPrioritization.prioritize s.TestState.LastResults
      if Array.isEmpty filtered then []
      else
        [ PipelineEffect.RunAffectedTests(filtered, trigger, System.TimeSpan.Zero, System.TimeSpan.Zero) ]

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
