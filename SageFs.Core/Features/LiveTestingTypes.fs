namespace SageFs.Features.LiveTesting

open System
open System.Reflection
open System.Security.Cryptography
open System.Text

// --- Stable Test Identity ---

type TestId = private TestId of string

module TestId =
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

// --- Dependency Graph ---

type TestDependencyGraph = {
  SymbolToTests: Map<string, TestId array>
  TransitiveCoverage: Map<string, TestId array>
  SourceVersion: int
}

// --- Coverage ---

[<RequireQualifiedAccess>]
type CoverageStatus =
  | Covered of testCount: int * allPassing: bool
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

// --- Live Test State (Elm model aggregate) ---

type LiveTestState = {
  SourceLocations: SourceTestLocation array
  DiscoveredTests: TestCase array
  LastResults: Map<TestId, TestRunResult>
  StatusEntries: TestStatusEntry array
  CoverageAnnotations: CoverageAnnotation array
  IsRunning: bool
  History: RunHistory
  AffectedTests: Set<TestId>
  Enabled: bool
  ShowCoverage: bool
  RunPolicies: Map<TestCategory, RunPolicy>
  DetectedProviders: ProviderDescription list
}

module LiveTestState =
  let empty = {
    SourceLocations = Array.empty
    DiscoveredTests = Array.empty
    LastResults = Map.empty
    StatusEntries = Array.empty
    CoverageAnnotations = Array.empty
    IsRunning = false
    History = RunHistory.NeverRun
    AffectedTests = Set.empty
    Enabled = true
    ShowCoverage = true
    RunPolicies = RunPolicyDefaults.defaults
    DetectedProviders = []
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
    | CoverageStatus.Covered (_, true) -> GutterIcon.Covered
    | CoverageStatus.Covered (_, false) -> GutterIcon.NotCovered
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
      | CoverageStatus.Covered (n, true) -> sprintf "%s: covered by %d test(s), all passing" symbol n
      | CoverageStatus.Covered (n, false) -> sprintf "%s: covered by %d test(s), some failing" symbol n
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
    SourceVersion = 0
  }

  let findAffected (changedSymbols: string list) (graph: TestDependencyGraph) : TestId array =
    changedSymbols
    |> List.choose (fun sym -> Map.tryFind sym graph.SymbolToTests)
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

  let private computeStatusEntriesWithHistory
    (previousStatuses: Map<TestId, TestRunStatus>)
    (state: LiveTestState)
    : TestStatusEntry array =
    state.DiscoveredTests
    |> Array.map (fun test ->
      let status =
        match Map.tryFind test.Category state.RunPolicies with
        | Some RunPolicy.Disabled -> TestRunStatus.PolicyDisabled
        | _ ->
          if Set.contains test.Id state.AffectedTests && state.IsRunning then
            TestRunStatus.Running
          elif Set.contains test.Id state.AffectedTests then
            TestRunStatus.Queued
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
      let updatedState =
        { state with
            LastResults = newResults
            IsRunning = false
            AffectedTests = Set.empty
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
      let allPassing =
        tests |> Array.forall (fun tid ->
          match Map.tryFind tid results with
          | Some r ->
            match r.Result with
            | TestResult.Passed _ -> true
            | _ -> false
          | None -> false)
      CoverageStatus.Covered (tests.Length, allPassing)

  let computeAll
    (graph: TestDependencyGraph)
    (results: Map<TestId, TestRunResult>)
    : Map<string, CoverageStatus> =
    graph.TransitiveCoverage
    |> Map.map (fun symbol tests ->
      if Array.isEmpty tests then CoverageStatus.NotCovered
      else
        let allPassing =
          tests |> Array.forall (fun tid ->
            match Map.tryFind tid results with
            | Some r ->
              match r.Result with
              | TestResult.Passed _ -> true
              | _ -> false
            | None -> false)
        CoverageStatus.Covered (tests.Length, allPassing))

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

// --- Phase 4: As-You-Type Pipeline ---

[<RequireQualifiedAccess>]
type PipelineStage =
  | TreeSitter
  | FcsTypeCheck
  | TestDiscovery
  | TestExecution

type StageTiming = {
  Stage: PipelineStage
  Duration: TimeSpan
  Timestamp: DateTimeOffset
}

/// Manages CancellationTokenSource per pipeline stage.
/// Acquiring a new token for a stage cancels the previous one.
type DebounceController() =
  let sources =
    System.Collections.Concurrent.ConcurrentDictionary<PipelineStage, Threading.CancellationTokenSource>()

  member _.Acquire(stage: PipelineStage) : Threading.CancellationToken =
    let newCts = new Threading.CancellationTokenSource()
    let _ = sources.AddOrUpdate(stage, newCts, fun _ old ->
      old.Cancel()
      old.Dispose()
      newCts)
    newCts.Token

  member _.CancelAll() =
    for kvp in sources do
      kvp.Value.Cancel()
      kvp.Value.Dispose()
    sources.Clear()

  interface IDisposable with
    member this.Dispose() = this.CancelAll()

module DebouncedExecution =
  let execute (delayMs: int) (token: Threading.CancellationToken) (f: unit -> 'a) : Async<'a option> =
    async {
      do! Async.Sleep delayMs
      if token.IsCancellationRequested then return None
      else return Some (f ())
    }

  let timed (f: unit -> 'a) : 'a * TimeSpan =
    let sw = Diagnostics.Stopwatch.StartNew()
    let result = f ()
    sw.Stop()
    (result, sw.Elapsed)

// --- Traced Execution ---

module Traced =
  let execute (stage: PipelineStage) (f: unit -> 'a) : 'a * StageTiming =
    let sw = Diagnostics.Stopwatch.StartNew()
    let result = f ()
    sw.Stop()
    let timing = {
      Stage = stage
      Duration = sw.Elapsed
      Timestamp = DateTimeOffset.UtcNow
    }
    (result, timing)

  let executeAsync (stage: PipelineStage) (f: unit -> Async<'a>) : Async<'a * StageTiming> =
    async {
      let sw = Diagnostics.Stopwatch.StartNew()
      let! result = f ()
      sw.Stop()
      let timing = {
        Stage = stage
        Duration = sw.Elapsed
        Timestamp = DateTimeOffset.UtcNow
      }
      return (result, timing)
    }

// --- Pipeline Result ---

[<RequireQualifiedAccess>]
type PipelineResult<'a> =
  | Cancelled
  | Completed of result: 'a * timings: StageTiming list

module PipelineResult =
  let map (f: 'a -> 'b) (pr: PipelineResult<'a>) : PipelineResult<'b> =
    match pr with
    | PipelineResult.Cancelled -> PipelineResult.Cancelled
    | PipelineResult.Completed (result, timings) ->
      PipelineResult.Completed (f result, timings)

  let bind (f: 'a -> StageTiming list -> PipelineResult<'b>) (pr: PipelineResult<'a>) : PipelineResult<'b> =
    match pr with
    | PipelineResult.Cancelled -> PipelineResult.Cancelled
    | PipelineResult.Completed (result, timings) ->
      match f result timings with
      | PipelineResult.Cancelled -> PipelineResult.Cancelled
      | PipelineResult.Completed (result', newTimings) ->
        PipelineResult.Completed (result', newTimings)

  let toPipelineTiming
    (trigger: RunTrigger)
    (totalTests: int)
    (affectedTests: int)
    (timings: StageTiming list)
    : PipelineTiming =
    let findDuration stage =
      timings
      |> List.tryFind (fun t -> t.Stage = stage)
      |> Option.map (fun t -> t.Duration)
    let ts = findDuration PipelineStage.TreeSitter |> Option.defaultValue TimeSpan.Zero
    let depth =
      match findDuration PipelineStage.FcsTypeCheck, findDuration PipelineStage.TestExecution with
      | Some fcs, Some exec -> PipelineDepth.ThroughExecution (ts, fcs, exec)
      | Some fcs, None -> PipelineDepth.ThroughFcs (ts, fcs)
      | _ -> PipelineDepth.TreeSitterOnly ts
    {
      Depth = depth
      TotalTests = totalTests
      AffectedTests = affectedTests
      Trigger = trigger
      Timestamp = DateTimeOffset.UtcNow
    }

// --- Debounced Pipeline ---

module DebouncedPipeline =
  let runStage
    (stage: PipelineStage)
    (delayMs: int)
    (token: Threading.CancellationToken)
    (f: unit -> 'a)
    : Async<PipelineResult<'a>> =
    async {
      do! Async.Sleep delayMs
      if token.IsCancellationRequested then
        return PipelineResult.Cancelled
      else
        let (result, timing) = Traced.execute stage f
        return PipelineResult.Completed (result, [ timing ])
    }

  let andThen
    (stage: PipelineStage)
    (delayMs: int)
    (token: Threading.CancellationToken)
    (f: 'a -> 'b)
    (prev: PipelineResult<'a>)
    : Async<PipelineResult<'b>> =
    async {
      match prev with
      | PipelineResult.Cancelled -> return PipelineResult.Cancelled
      | PipelineResult.Completed (result, timings) ->
        if token.IsCancellationRequested then
          return PipelineResult.Cancelled
        else
          do! Async.Sleep delayMs
          if token.IsCancellationRequested then
            return PipelineResult.Cancelled
          else
            let (result', timing) = Traced.execute stage (fun () -> f result)
            return PipelineResult.Completed (result', timings @ [ timing ])
    }

// --- Pipeline History (Ring Buffer) ---

type PipelineHistory(capacity: int) =
  let buffer = Array.zeroCreate<PipelineTiming option> capacity
  let mutable head = 0
  let mutable count = 0

  member _.Add(timing: PipelineTiming) =
    buffer.[head] <- Some timing
    head <- (head + 1) % capacity
    if count < capacity then count <- count + 1

  member _.Latest : PipelineTiming option =
    if count = 0 then None
    else buffer.[(head - 1 + capacity) % capacity]

  member _.ToArray() : PipelineTiming array =
    if count = 0 then [||]
    else
      let result = Array.zeroCreate count
      for i in 0 .. count - 1 do
        let idx = (head - count + i + capacity) % capacity
        result.[i] <- buffer.[idx] |> Option.get
      result

  member _.Count = count

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
      let updatedResults =
        state.LastResults
        |> Map.map (fun testId result ->
          if Set.contains testId affected then
            { result with Result = TestResult.NotRun; Timestamp = DateTimeOffset.UtcNow }
          else result)
      let updatedState = { state with LastResults = updatedResults; AffectedTests = affected }
      { updatedState with StatusEntries = LiveTesting.computeStatusEntries updatedState }

// --- Pipeline Orchestrator ---

[<RequireQualifiedAccess>]
type PipelineDecision =
  | Skip of reason: string
  | TreeSitterOnly
  | FullPipeline of affectedTestIds: TestId array

module PipelineOrchestrator =
  let decide
    (state: LiveTestState)
    (trigger: RunTrigger)
    (changedSymbols: string list)
    (depGraph: TestDependencyGraph)
    : PipelineDecision =
    if not state.Enabled then
      PipelineDecision.Skip "Live testing disabled"
    elif state.IsRunning then
      PipelineDecision.Skip "Pipeline already running"
    elif Array.isEmpty state.DiscoveredTests then
      PipelineDecision.TreeSitterOnly
    else
      let affected = TestDependencyGraph.findAffected changedSymbols depGraph
      let filtered =
        state.DiscoveredTests
        |> Array.filter (fun tc -> affected |> Array.contains tc.Id)
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
  let fcsDelayMs = 300

  let onKeystroke (content: string) (filePath: string) (now: DateTimeOffset) (db: PipelineDebounce) =
    { db with
        TreeSitter = db.TreeSitter |> DebounceChannel.submit content treeSitterDelayMs now
        Fcs = db.Fcs |> DebounceChannel.submit filePath fcsDelayMs now }

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
  | RequestFcsTypeCheck of filePath: string
  | RunAffectedTests of testIds: TestId array * trigger: RunTrigger
  | NoOp

module PipelineEffects =
  let fromTick
    (tsPayload: string option)
    (fcsPayload: string option)
    (filePath: string)
    : PipelineEffect list =
    [ match tsPayload with
      | Some content -> PipelineEffect.ParseTreeSitter(content, filePath)
      | None -> ()
      match fcsPayload with
      | Some fp -> PipelineEffect.RequestFcsTypeCheck fp
      | None -> () ]

  let afterTypeCheck
    (changedSymbols: string list)
    (trigger: RunTrigger)
    (depGraph: TestDependencyGraph)
    (state: LiveTestState)
    : PipelineEffect =
    if not state.Enabled then PipelineEffect.NoOp
    else
      let affected = TestDependencyGraph.findAffected changedSymbols depGraph
      if Array.isEmpty affected then PipelineEffect.NoOp
      else
        let affectedTests =
          state.DiscoveredTests
          |> Array.filter (fun tc -> affected |> Array.contains tc.Id)
        let filtered =
          PolicyFilter.filterTests state.RunPolicies trigger affectedTests
          |> Array.map (fun tc -> tc.Id)
        if Array.isEmpty filtered then PipelineEffect.NoOp
        else PipelineEffect.RunAffectedTests(filtered, trigger)

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
  UsedInTestId: TestId option
  FilePath: string
  Line: int
}

/// Builds SymbolToTests from a list of symbol references.
module SymbolGraphBuilder =
  let buildIndex (refs: SymbolReference list) : Map<string, TestId array> =
    refs
    |> List.choose (fun r ->
      match r.UsedInTestId with
      | Some tid -> Some (r.SymbolFullName, tid)
      | None -> None)
    |> List.groupBy fst
    |> List.map (fun (sym, pairs) ->
      sym, pairs |> List.map snd |> List.distinct |> Array.ofList)
    |> Map.ofList

  let updateGraph (newRefs: SymbolReference list) (filePath: string) (graph: TestDependencyGraph) : TestDependencyGraph =
    let newIndex = buildIndex newRefs
    let merged =
      (graph.SymbolToTests, newIndex)
      ||> Map.fold (fun acc key value ->
        Map.add key value acc)
    { graph with
        SymbolToTests = merged
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
  }

  let onKeystroke (content: string) (filePath: string) (now: DateTimeOffset) (s: LiveTestPipelineState) =
    let db = s.Debounce |> PipelineDebounce.onKeystroke content filePath now
    { s with Debounce = db; ActiveFile = Some filePath }

  let onFileSave (filePath: string) (now: DateTimeOffset) (s: LiveTestPipelineState) =
    let db = s.Debounce |> PipelineDebounce.onFileSave filePath now
    { s with Debounce = db }

  let onFcsComplete (filePath: string) (refs: SymbolReference list) (s: LiveTestPipelineState) =
    let changes, newCache = FileAnalysisCache.update filePath refs s.AnalysisCache
    let newDepGraph = SymbolGraphBuilder.updateGraph refs filePath s.DepGraph
    let newDebounce = AdaptiveDebounce.onFcsCompleted s.AdaptiveDebounce
    { s with
        DepGraph = newDepGraph
        ChangedSymbols = SymbolChanges.allChanged changes
        AnalysisCache = newCache
        AdaptiveDebounce = newDebounce }

  let onFcsCanceled (s: LiveTestPipelineState) =
    { s with AdaptiveDebounce = AdaptiveDebounce.onFcsCanceled s.AdaptiveDebounce }

  let currentFcsDelay (s: LiveTestPipelineState) =
    AdaptiveDebounce.currentFcsDelay s.AdaptiveDebounce

  /// Ticks the debounce channels and produces pipeline effects.
  /// Returns (effects, updatedState).
  let tick (now: DateTimeOffset) (s: LiveTestPipelineState) =
    let (tsPayload, fcsPayload), db = s.Debounce |> PipelineDebounce.tick now
    let filePath = s.ActiveFile |> Option.defaultValue ""
    let effects = PipelineEffects.fromTick tsPayload fcsPayload filePath
    let fcsEffect =
      match fcsPayload with
      | Some _ ->
        PipelineEffects.afterTypeCheck s.ChangedSymbols RunTrigger.Keystroke s.DepGraph s.TestState
      | None -> PipelineEffect.NoOp
    let allEffects =
      effects @ [fcsEffect]
      |> List.filter (fun e -> e <> PipelineEffect.NoOp)
    allEffects, { s with Debounce = db }
