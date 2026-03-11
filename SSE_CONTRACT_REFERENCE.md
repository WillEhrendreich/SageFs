# SSE Contract Compliance Tests - Complete Reference

## 1. SseSerializationBenchmarks.fs - Factory Functions

### File: C:\Code\Repos\SageFs\SageFs.Tests\SseSerializationBenchmarks.fs

#### mkTestSummary (Line 29-30)
```fsharp
let private mkTestSummary () : TestSummary =
  { Total = 247; Passed = 230; Failed = 12; Stale = 5; Running = 0; Disabled = 3; Enabled = true }
```

#### mkTestResultsBatch (Line 32-55)
```fsharp
let private mkTestResultsBatch (n: int) : TestResultsBatchPayload =
  let entries =
    [| for i in 1 .. n do
        let status =
          match i % 10 with
          | 0 -> TestRunStatus.Failed (
                    TestFailure.AssertionFailed (sprintf "Expected 42 but got %d" (i % 99)),
                    TimeSpan.FromMilliseconds(float (50 + i % 200)))
          | 1 -> TestRunStatus.Stale
          | _ -> TestRunStatus.Passed (TimeSpan.FromMilliseconds(float (20 + i % 100)))
        { TestStatusEntry.TestId = TestId.TestId (sprintf "SageFs.Tests.Module%d.test%d" (i % 20) i)
          DisplayName = sprintf "test %d should produce correct result for input %d" i (i * 7)
          FullName = sprintf "SageFs.Tests.Module%d.test %d" (i % 20) i
          Origin = TestOrigin.ReflectionOnly
          Framework = TestFramework.Expecto
          Category = TestCategory.Unit
          CurrentPolicy = RunPolicy.OnEveryChange
          Status = status
          PreviousStatus = TestRunStatus.Passed (TimeSpan.FromMilliseconds(30.0)) } |]
  { Generation = RunGeneration 7
    Freshness = ResultFreshness.Fresh
    Completion = BatchCompletion.Complete (n, n)
    Entries = entries
    Summary = mkTestSummary () }
```

#### mkFileAnnotations (Line 57-88)
```fsharp
let private mkFileAnnotations () : FileAnnotations =
  { FilePath = "C:\\Code\\Repos\\SageFs\\SageFs.Core\\Features\\LiveTesting.fs"
    TestAnnotations =
      [| for i in 1 .. 30 do
          let status =
            match i % 5 with
            | 0 -> TestRunStatus.Failed (
                      TestFailure.AssertionFailed "Expected true but got false",
                      TimeSpan.FromMilliseconds(float (20 + i * 3)))
            | _ -> TestRunStatus.Passed (TimeSpan.FromMilliseconds(float (20 + i * 3)))
          { TestLineAnnotation.Line = 10 + i * 5
            TestId = TestId.TestId (sprintf "SageFs.Tests.Mod.test%d" i)
            DisplayName = sprintf "test %d" i
            Status = status
            Freshness = AnnotationFreshness.Current } |]
    CoverageAnnotations =
      [| for i in 1 .. 50 do
          let detail =
            match i % 3 with
            | 0 -> CoverageStatus.NotCovered
            | _ -> CoverageStatus.Covered (i % 4 + 1, CoverageHealth.AllPassing)
          { CoverageLineAnnotation.Line = i
            EndLine = i
            EndColumn = 80
            Detail = detail
            CoveringTestIds =
              [| for j in 1 .. (i % 4) do
                  TestId.TestId (sprintf "test_%d" j) |]
            BranchCoverage = None } |]
    InlineFailures = [||]
    CodeLenses = [||]
    PerformanceAnnotations = [||] }
```

#### mkDiffSummary (Line 90-98)
```fsharp
let private mkDiffSummary () : DiffSummary =
  { Lines =
      [ for i in 1 .. 20 do
          match i % 4 with
          | 0 -> Added (sprintf "let newBinding%d = computeValue %d" i (i * 3))
          | 1 -> Removed (sprintf "let oldBinding%d = legacyCompute %d" i (i * 2))
          | 2 -> Modified (sprintf "let x%d = old%d" i i, sprintf "let x%d = new%d" i i)
          | _ -> Unchanged (sprintf "let stable%d = stableValue" i) ]
    AddedCount = 5; RemovedCount = 5; ModifiedCount = 5; UnchangedCount = 5 }
```

#### mkCellGraph (Line 100-113)
```fsharp
let private mkCellGraph () : CellGraph =
  { Cells =
      [ for i in 0 .. 19 do
          i, { CellInfo.Id = i
               Source = sprintf "let binding%d = computeValue %d;;" i (i * 3)
               Produces = [ sprintf "binding_%d" i; sprintf "type_%d" i ]
               Consumes =
                 match i with
                 | 0 -> []
                 | n -> [ sprintf "binding_%d" (n - 1) ] } ]
      |> Map.ofList
    Edges =
      [ for i in 1 .. 19 do
          (i - 1, i) ] }
```

#### mkBindingScopeSnapshot (Line 115-126)
```fsharp
let private mkBindingScopeSnapshot () : BindingScopeSnapshot =
  let bindings =
    [ for i in 1 .. 40 do
        { BindingInfo.Name = sprintf "myBinding%d" i
          TypeSig = "int -> Result<string, SageFsError>"
          Value = match i % 5 with | 0 -> Some (sprintf "%d" (i * 42)) | _ -> None
          CellIndex = i % 10
          ShadowedBy = match i % 8 with | 0 -> [ i + 1 ] | _ -> []
          ReferencedIn = [ for j in 1 .. (i % 3) do i + j ] } ]
  { Bindings = bindings
    ActiveBindings = bindings |> List.filter (fun b -> b.ShadowedBy.IsEmpty) |> List.map (fun b -> b.Name, b) |> Map.ofList
    ShadowedBindings = bindings |> List.filter (fun b -> not b.ShadowedBy.IsEmpty) }
```

#### mkTimelineStats (Line 128-130)
```fsharp
let private mkTimelineStats () : TimelineStats =
  { Count = 150; P50Ms = Some 42.0; P95Ms = Some 180.0; P99Ms = Some 450.0
    MeanMs = Some 67.3; Sparkline = "▁▂▃▄▅▆▇█▇▆▅▄▃▂▁▂▃▅▇" }
```

#### mkFailureNarratives (Line 132-142)
```fsharp
let private mkFailureNarratives () : Map<TestId, FailureNarrative> =
  [ for i in 1 .. 5 do
      TestId.TestId (sprintf "SageFs.Tests.Mod.failTest%d" i),
      { LastPassedAt = Some (DateTimeOffset(2026, 3, 10, 14, 30, 0, TimeSpan.Zero))
        TimeSinceLastPass = Some (TimeSpan.FromMinutes(float (i * 15)))
        CausalChanges =
          [ CausalChange.SymbolChanged (sprintf "myFunction%d" i)
            CausalChange.FileChanged (sprintf "Module%d.fs" i) ]
        PropertyViolation = None
        Summary = sprintf "Test %d broke because myFunction%d changed return type from int to string" i i } ]
  |> Map.ofList
```

#### mkTestSourceLocations (Line 144-150)
```fsharp
let private mkTestSourceLocations () : TestSourceLocation list =
  [ for i in 1 .. 50 do
      { CellId = i % 10
        TestName = sprintf "SageFs.Tests.Module%d.test%d" (i % 8) i
        FilePath = sprintf "C:\\Code\\Repos\\SageFs\\SageFs.Tests\\Module%dTests.fs" (i % 8)
        StartLine = 10 + i * 12
        EndLine = 10 + i * 12 + 5 } ]
```

#### mkAnnotatedTransitions (Line 152-162)
```fsharp
let private mkAnnotatedTransitions () : AnnotatedTransition list =
  [ for i in 1 .. 12 do
      { FromState = sprintf "State%d" i
        ToState = sprintf "State%d" (i + 1)
        FunctionName = Some (sprintf "transition%d" i)
        IsErrorBranch = i % 4 = 0
        Health =
          match i % 3 with
          | 0 -> TransitionHealth.Passing
          | 1 -> TransitionHealth.Failing
          | _ -> TransitionHealth.Stale } ]
```

#### mkBindingValues (Line 164-173)
```fsharp
let private mkBindingValues () : BindingValue list =
  [ for i in 1 .. 10 do
      { Name = sprintf "val%d" i
        TypeSig = "int -> string"
        DisplayValue = sprintf "%d" (i * 42)
        IsTruncated = i % 3 = 0
        IsFunctionValue = i % 5 = 0
        CellIndex = i
        EvalDurationMs = float i * 1.5
        SourceLine = i } ]
```

#### mkFsiBindings (Line 175-180)
```fsharp
let private mkFsiBindings () : FsiBinding array =
  [| for i in 1 .. 10 do
       { Name = sprintf "binding%d" i
         TypeSig = sprintf "int list"
         Value = match i % 2 with 0 -> Some (sprintf "[%d]" i) | _ -> None
         ShadowCount = match i % 4 with 0 -> 1 | _ -> 0 } |]
```

#### mkDiagnosticReport (Line 182-200)
```fsharp
let private mkDiagnosticReport () : DiagnosticReport =
  { Failures =
      [ { TestId = TestId.TestId "diag-test-1"
          TestName = "should compute tax"
          Narrative =
            { LastPassedAt = Some (DateTimeOffset.UtcNow.AddMinutes -10.0)
              TimeSinceLastPass = Some (TimeSpan.FromMinutes 5.0)
              CausalChanges = [ CausalChange.SymbolChanged "computeTax"; CausalChange.FileChanged "Tax.fs" ]
              PropertyViolation = None
              Summary = "Expected 42 but got 41" }
          CausalCells = [ 0; 1 ]
          Staleness = Map.ofList [ 0, Staleness.Fresh ] } ]
    AffectedCells = [ 0, Staleness.Fresh ]
    RipplePlan = None
    SuggestedFixes =
      [ { Code = "let tax = amount * 0.10m"; Explanation = "Fix tax rate"; Confidence = 0.85 } ]
    PerformanceContext = Some (mkTimelineStats ())
    Severity = DiagnosticSeverity.Warning
    Summary = "1 failure in Tax module" }
```

---

## 2. SsePropertyTests.fs - Helper Functions

### File: C:\Code\Repos\SageFs\SageFs.Tests\SsePropertyTests.fs

#### extractDataPayload (Line 28-34)
```fsharp
let private extractDataPayload (sse: string) =
  sse.Split('\n')
  |> Array.choose (fun line ->
    match line.StartsWith("data: ") with
    | true -> Some (line.Substring(6))
    | false -> None)
  |> String.concat "\n"
```

#### countDataLines (Line 36-39)
```fsharp
let private countDataLines (sse: string) =
  sse.Split('\n')
  |> Array.filter (fun l -> l.StartsWith("data: "))
  |> Array.length
```

---

## 3. CellGraph Type Definition

### File: C:\Code\Repos\SageFs\SageFs.Core\Features\CellDependencyGraph.fs

#### CellGraph (Lines 12-15)
```fsharp
type CellGraph = {
  Cells: Map<CellId, CellInfo>
  Edges: (CellId * CellId) list
}
```

**Note:** CellGraph is different from CellDependencyGraph. CellGraph is the actual graph type used in the SSE formatters, while CellDependencyGraph is the module name. The CellGraph contains:
- **Cells**: Map of cell ID to CellInfo records containing cell source, produced bindings, and consumed bindings
- **Edges**: List of tuples representing directed edges from producer cell to consumer cell

---

## 4. RunGeneration, ResultFreshness, BatchCompletion Type Definitions

### File: C:\Code\Repos\SageFs\SageFs.Core\Features\LiveTestingTypes.fs

#### RunGeneration (Line 655)
```fsharp
type RunGeneration = RunGeneration of int
```

#### ResultFreshness (Lines 670-673)
```fsharp
type ResultFreshness =
  | Fresh
  | StaleCodeEdited
  | StaleWrongGeneration
```

#### BatchCompletion (Lines 1545-1548)
```fsharp
type BatchCompletion =
  | Complete of requested: int * returned: int
  | Partial of requested: int * returned: int
  | Superseded
```

---

## 5. Annotation Type Definitions (FileAnnotations Components)

### File: C:\Code\Repos\SageFs\SageFs.Core\Features\LiveTestingTypes.fs

#### TestLineAnnotation (Lines 2947-2953)
```fsharp
type TestLineAnnotation = {
  Line: int
  TestId: TestId
  DisplayName: string
  Status: TestRunStatus
  Freshness: AnnotationFreshness
}
```

#### CoverageLineAnnotation (Lines 2955-2962)
```fsharp
type CoverageLineAnnotation = {
  Line: int
  EndLine: int
  EndColumn: int
  Detail: CoverageStatus
  CoveringTestIds: TestId array
  BranchCoverage: LineCoverage option
}
```

#### InlineFailure (Lines 2964-2970)
```fsharp
type InlineFailure = {
  Line: int
  TestId: TestId
  TestName: string
  Failure: FailurePresentation
  Duration: TimeSpan
}
```

#### TestCodeLens (Lines 2972-2977)
```fsharp
type TestCodeLens = {
  Line: int
  Label: string
  TestId: TestId
  Command: CodeLensCommand
}
```

#### PerformanceAnnotation (Lines 2980-2993)
```fsharp
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
```

---

## 6. TestStatusEntry Type Definition

### File: C:\Code\Repos\SageFs\SageFs.Core\Features\LiveTestingTypes.fs

#### TestStatusEntry (Lines 221-231)
```fsharp
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
```

---

## 7. TestFailure Type Definition

### File: C:\Code\Repos\SageFs\SageFs.Core\Features\LiveTestingTypes.fs

#### TestFailure (Lines 180-183)
```fsharp
type TestFailure =
  | AssertionFailed of message: string
  | ExceptionThrown of message: string * stackTrace: string
  | TimedOut of after: TimeSpan
```

---

## 8. DiffLine Type Definition

### File: C:\Code\Repos\SageFs\SageFs.Core\Features\EvalDiff.fs

#### DiffLine (Lines 3-7)
```fsharp
type DiffLine =
  | Unchanged of string
  | Added of string
  | Removed of string
  | Modified of old: string * current: string
```

---

## 9. BindingInfo Type Definition

### File: C:\Code\Repos\SageFs\SageFs.Core\Features\BindingExplorer.fs

#### BindingInfo (Lines 5-12)
```fsharp
type BindingInfo = {
  Name: string
  TypeSig: string
  Value: string option
  CellIndex: int
  ShadowedBy: int list
  ReferencedIn: int list
}
```

---

## 10. DiagnosticReport Type Definition

### File: C:\Code\Repos\SageFs\SageFs.Core\Features\Diagnostician.fs

#### DiagnosticReport (Lines 26-34)
```fsharp
type DiagnosticReport = {
  Failures: DiagnosedFailure list
  AffectedCells: (CellId * Staleness) list
  RipplePlan: RipplePlan option
  SuggestedFixes: Suggestion list
  PerformanceContext: TimelineStats option
  Severity: DiagnosticSeverity
  Summary: string
}
```

#### DiagnosedFailure (Lines 16-22)
```fsharp
type DiagnosedFailure = {
  TestId: TestId
  TestName: string
  Narrative: FailureNarrative
  CausalCells: CellId list
  Staleness: Map<CellId, Staleness>
}
```

#### DiagnosticSeverity (Lines 10-13)
```fsharp
type DiagnosticSeverity =
  | Info
  | Warning
  | Critical
```

---

## 11. All 16 formatXxxEvent Functions from SseWriter.fs

### File: C:\Code\Repos\SageFs\SageFs.Core\SseWriter.fs

#### 1. formatWarmupProgressEvent (Lines 75-85) - Event: "warmup_progress"
```fsharp
let formatWarmupProgressEvent (opts: JsonSerializerOptions) (sessionId: string option) (step: int) (total: int) (message: string) : string =
  let progress =
    match total with
    | 0 -> 0.0
    | t -> System.Math.Round(float step / float t, 3)
  let phase = deriveWarmupPhase step total
  let json =
    JsonSerializer.Serialize(
      {| Step = step; Total = total; Message = message; Progress = progress; Phase = phase |}, opts)
    |> injectSessionId sessionId
  formatSseEvent "warmup_progress" json
```
**JSON Properties**: Step, Total, Message, Progress, Phase, [SessionId] (optional)

#### 2. formatTestSummaryEvent (Lines 88-90) - Event: "test_summary"
```fsharp
let formatTestSummaryEvent (opts: JsonSerializerOptions) (sessionId: string option) (summary: Features.LiveTesting.TestSummary) : string =
  let json = JsonSerializer.Serialize(summary, opts) |> injectSessionId sessionId
  formatSseEvent "test_summary" json
```
**JSON Properties**: Total, Passed, Failed, Stale, Running, Disabled, Enabled, [SessionId] (optional)

#### 3. formatTestResultsBatchEvent (Lines 93-95) - Event: "test_results_batch"
```fsharp
let formatTestResultsBatchEvent (opts: JsonSerializerOptions) (sessionId: string option) (payload: Features.LiveTesting.TestResultsBatchPayload) : string =
  let json = JsonSerializer.Serialize(payload, opts) |> injectSessionId sessionId
  formatSseEvent "test_results_batch" json
```
**JSON Properties**: Generation, Freshness, Completion, Entries[], Summary, [SessionId] (optional)

#### 4. formatFileAnnotationsEvent (Lines 98-100) - Event: "file_annotations"
```fsharp
let formatFileAnnotationsEvent (opts: JsonSerializerOptions) (sessionId: string option) (annotations: Features.LiveTesting.FileAnnotations) : string =
  let json = JsonSerializer.Serialize(annotations, opts) |> injectSessionId sessionId
  formatSseEvent "file_annotations" json
```
**JSON Properties**: FilePath, TestAnnotations[], CoverageAnnotations[], InlineFailures[], CodeLenses[], PerformanceAnnotations[], [SessionId] (optional)

#### 5. formatFailureNarrativesEvent (Lines 103-116) - Event: "failure_narratives"
```fsharp
let formatFailureNarrativesEvent (opts: JsonSerializerOptions) (sessionId: string option) (narratives: Map<Features.LiveTesting.TestId, Features.LiveTesting.FailureNarrative>) : string =
  let payload =
    narratives
    |> Map.toArray
    |> Array.map (fun (tid, n) ->
      {| TestId = Features.LiveTesting.TestId.value tid; LastPassedAt = n.LastPassedAt; TimeSinceLastPass = n.TimeSinceLastPass
         CausalChanges = n.CausalChanges |> List.map (fun c ->
           match c with
           | Features.LiveTesting.CausalChange.SymbolChanged s -> {| Kind = "symbol"; Name = s |}
           | Features.LiveTesting.CausalChange.FileChanged f -> {| Kind = "file"; Name = f |}
           | Features.LiveTesting.CausalChange.Unknown -> {| Kind = "unknown"; Name = "" |})
         PropertyViolation = n.PropertyViolation; Summary = n.Summary |})
  let json = JsonSerializer.Serialize(payload, opts) |> injectSessionId sessionId
  formatSseEvent "failure_narratives" json
```
**JSON Properties**: Array of {TestId, LastPassedAt, TimeSinceLastPass, CausalChanges[], PropertyViolation, Summary}, [SessionId] (optional)

#### 6. formatTestSourceLocationsEvent (Lines 119-122) - Event: "test_source_locations"
```fsharp
let formatTestSourceLocationsEvent (opts: JsonSerializerOptions) (sessionId: string option) (locations: Features.LiveTesting.TestSourceLocation list) : string =
  let payload = {| Locations = locations |}
  let json = JsonSerializer.Serialize(payload, opts) |> injectSessionId sessionId
  formatSseEvent "test_source_locations" json
```
**JSON Properties**: Locations[], [SessionId] (optional)

#### 7. formatBindingsSnapshotEvent (Lines 204-215) - Event: "bindings_snapshot"
```fsharp
let formatBindingsSnapshotEvent
  (opts: JsonSerializerOptions)
  (sessionId: string option)
  (bindingValues: Features.FsiOutputParser.BindingValue list)
  (bindings: FsiBinding array)
  : string =
  let json =
    JsonSerializer.Serialize(
      {| Bindings = bindings; BindingValues = bindingValues |},
      opts)
    |> injectSessionId sessionId
  formatSseEvent "bindings_snapshot" json
```
**JSON Properties**: Bindings[], BindingValues[], [SessionId] (optional)

#### 8. formatTestTraceEvent (Lines 218-220) - Event: "test_trace"
```fsharp
let formatTestTraceEvent (sessionId: string option) (traceJson: string) : string =
  let json = injectSessionId sessionId traceJson
  formatSseEvent "test_trace" json
```
**JSON Properties**: Pre-serialized traceJson, [SessionId] (optional if injected)

#### 9. formatEvalDiffEvent (Lines 225-238) - Event: "eval_diff"
```fsharp
let formatEvalDiffEvent (opts: JsonSerializerOptions) (sessionId: string option) (summary: Features.EvalDiff.DiffSummary) : string =
  let payload =
    {| Lines = summary.Lines |> List.map (fun l ->
         match l with
         | Features.EvalDiff.Added s -> {| Kind = "added"; Text = s; OldText = "" |}
         | Features.EvalDiff.Removed s -> {| Kind = "removed"; Text = ""; OldText = s |}
         | Features.EvalDiff.Modified (o, n) -> {| Kind = "modified"; Text = n; OldText = o |}
         | Features.EvalDiff.Unchanged s -> {| Kind = "unchanged"; Text = s; OldText = "" |})
       Added = summary.AddedCount
       Removed = summary.RemovedCount
       Modified = summary.ModifiedCount
       Unchanged = summary.UnchangedCount |}
  let json = JsonSerializer.Serialize(payload, opts) |> injectSessionId sessionId
  formatSseEvent "eval_diff" json
```
**JSON Properties**: Lines[], Added, Removed, Modified, Unchanged, [SessionId] (optional)

#### 10. formatEvalStartedEvent (Lines 242-248) - Event: "eval_started"
```fsharp
let formatEvalStartedEvent (opts: JsonSerializerOptions) (sessionId: string option) (filePath: string) (blockStartLine: int) : string =
  let json =
    JsonSerializer.Serialize(
      {| filePath = filePath
         blockStartLine = blockStartLine |}, opts)
    |> injectSessionId sessionId
  formatSseEvent "eval_started" json
```
**JSON Properties**: filePath, blockStartLine, [SessionId] (optional)

#### 11. formatEvalResultEvent (Lines 252-261) - Event: "eval_result"
```fsharp
let formatEvalResultEvent (opts: JsonSerializerOptions) (sessionId: string option) (filePath: string) (blockStartLine: int) (output: string) (success: bool) (durationMs: float) : string =
  let json =
    JsonSerializer.Serialize(
      {| filePath = filePath
         blockStartLine = blockStartLine
         output = output
         success = success
         durationMs = durationMs |}, opts)
    |> injectSessionId sessionId
  formatSseEvent "eval_result" json
```
**JSON Properties**: filePath, blockStartLine, output, success, durationMs, [SessionId] (optional)

#### 12. formatCellDependenciesEvent (Lines 264-271) - Event: "cell_dependencies"
```fsharp
let formatCellDependenciesEvent (opts: JsonSerializerOptions) (sessionId: string option) (graph: Features.CellDependencyGraph.CellGraph) : string =
  let payload =
    {| Nodes = graph.Cells |> Map.values |> Seq.map (fun c ->
         {| Id = c.Id; Produces = c.Produces; Consumes = c.Consumes |})
         |> Array.ofSeq
       Edges = graph.Edges |> List.map (fun (f, t) -> {| From = f; To = t |}) |}
  let json = JsonSerializer.Serialize(payload, opts) |> injectSessionId sessionId
  formatSseEvent "cell_dependencies" json
```
**JSON Properties**: Nodes[], Edges[], [SessionId] (optional)

#### 13. formatBindingScopeMapEvent (Lines 274-282) - Event: "binding_scope_map"
```fsharp
let formatBindingScopeMapEvent (opts: JsonSerializerOptions) (sessionId: string option) (snapshot: Features.BindingExplorer.BindingScopeSnapshot) : string =
  let payload =
    {| Bindings = snapshot.Bindings |> List.map (fun b ->
         {| Name = b.Name; TypeSig = b.TypeSig; CellIndex = b.CellIndex
            ShadowedBy = b.ShadowedBy; ReferencedIn = b.ReferencedIn |})
       ActiveCount = snapshot.ActiveBindings.Count
       ShadowedCount = snapshot.ShadowedBindings.Length |}
  let json = JsonSerializer.Serialize(payload, opts) |> injectSessionId sessionId
  formatSseEvent "binding_scope_map" json
```
**JSON Properties**: Bindings[], ActiveCount, ShadowedCount, [SessionId] (optional)

#### 14. formatEvalTimelineEvent (Lines 285-294) - Event: "eval_timeline"
```fsharp
let formatEvalTimelineEvent (opts: JsonSerializerOptions) (sessionId: string option) (stats: Features.EvalTimeline.TimelineStats) : string =
  let payload =
    {| Count = stats.Count
       P50Ms = stats.P50Ms
       P95Ms = stats.P95Ms
       P99Ms = stats.P99Ms
       MeanMs = stats.MeanMs
       Sparkline = stats.Sparkline |}
  let json = JsonSerializer.Serialize(payload, opts) |> injectSessionId sessionId
  formatSseEvent "eval_timeline" json
```
**JSON Properties**: Count, P50Ms, P95Ms, P99Ms, MeanMs, Sparkline, [SessionId] (optional)

#### 15. formatDomainModelEvent (Lines 298-309) - Event: "domain_model"
```fsharp
let formatDomainModelEvent (opts: JsonSerializerOptions) (sessionId: string option) (annotations: Features.DomainModelViz.AnnotatedTransition list) : string =
  let payload =
    {| Transitions =
         annotations |> List.map (fun a ->
           {| FromState = a.FromState
              ToState = a.ToState
              FunctionName = a.FunctionName
              IsErrorBranch = a.IsErrorBranch
              Health = sprintf "%A" a.Health |})
         |> List.toArray |}
  let json = JsonSerializer.Serialize(payload, opts) |> injectSessionId sessionId
  formatSseEvent "domain_model" json
```
**JSON Properties**: Transitions[], [SessionId] (optional)

#### 16. formatDiagnosisReadyEvent (Lines 313-340) - Event: "diagnosis_ready"
```fsharp
let formatDiagnosisReadyEvent (opts: JsonSerializerOptions) (sessionId: string option) (report: Features.Diagnostician.DiagnosticReport) : string =
  let extractCausalSymbols (changes: Features.LiveTesting.CausalChange list) =
    changes
    |> List.choose (function
      | Features.LiveTesting.CausalChange.SymbolChanged s -> Some s
      | _ -> None)
    |> List.toArray
  let payload =
    {| Severity = sprintf "%A" report.Severity
       FailureCount = report.Failures.Length
       AffectedCells = report.AffectedCells
       SuggestionCount = report.SuggestedFixes.Length
       TopSuggestions =
         report.SuggestedFixes
         |> List.truncate 3
         |> List.map (fun s -> {| Code = s.Code; Explanation = s.Explanation; Confidence = s.Confidence |})
       Failures =
         report.Failures
         |> List.map (fun f ->
           {| TestName = f.TestName
              CausalSymbols = extractCausalSymbols f.Narrative.CausalChanges |})
         |> List.toArray
       Performance =
         report.PerformanceContext
         |> Option.map (fun s -> {| Sparkline = s.Sparkline; P50Ms = s.P50Ms; P95Ms = s.P95Ms |})
       Summary = report.Summary |}
  let json = JsonSerializer.Serialize(payload, opts) |> injectSessionId sessionId
  formatSseEvent "diagnosis_ready" json
```
**JSON Properties**: Severity, FailureCount, AffectedCells, SuggestionCount, TopSuggestions[], Failures[], Performance, Summary, [SessionId] (optional)

---

## Summary Table of All 16 SSE Events

| # | Event Type | Formatter Function | Line | Data Payload Type | Key Properties |
|---|---|---|---|---|---|
| 1 | warmup_progress | formatWarmupProgressEvent | 75 | Anonymous record | Step, Total, Message, Progress, Phase |
| 2 | test_summary | formatTestSummaryEvent | 88 | TestSummary | Total, Passed, Failed, Stale, Running, Disabled, Enabled |
| 3 | test_results_batch | formatTestResultsBatchEvent | 93 | TestResultsBatchPayload | Generation, Freshness, Completion, Entries[], Summary |
| 4 | file_annotations | formatFileAnnotationsEvent | 98 | FileAnnotations | FilePath, TestAnnotations[], CoverageAnnotations[], InlineFailures[], CodeLenses[], PerformanceAnnotations[] |
| 5 | failure_narratives | formatFailureNarrativesEvent | 103 | Transformed array | TestId, LastPassedAt, TimeSinceLastPass, CausalChanges[], PropertyViolation, Summary |
| 6 | test_source_locations | formatTestSourceLocationsEvent | 119 | Anonymous record | Locations[] |
| 7 | bindings_snapshot | formatBindingsSnapshotEvent | 204 | Anonymous record | Bindings[], BindingValues[] |
| 8 | test_trace | formatTestTraceEvent | 218 | Pre-serialized JSON | (pre-serialized) |
| 9 | eval_diff | formatEvalDiffEvent | 225 | Anonymous record | Lines[], Added, Removed, Modified, Unchanged |
| 10 | eval_started | formatEvalStartedEvent | 242 | Anonymous record | filePath, blockStartLine |
| 11 | eval_result | formatEvalResultEvent | 252 | Anonymous record | filePath, blockStartLine, output, success, durationMs |
| 12 | cell_dependencies | formatCellDependenciesEvent | 264 | Anonymous record | Nodes[], Edges[] |
| 13 | binding_scope_map | formatBindingScopeMapEvent | 274 | Anonymous record | Bindings[], ActiveCount, ShadowedCount |
| 14 | eval_timeline | formatEvalTimelineEvent | 285 | Anonymous record | Count, P50Ms, P95Ms, P99Ms, MeanMs, Sparkline |
| 15 | domain_model | formatDomainModelEvent | 298 | Anonymous record | Transitions[] |
| 16 | diagnosis_ready | formatDiagnosisReadyEvent | 313 | Anonymous record | Severity, FailureCount, AffectedCells, SuggestionCount, TopSuggestions[], Failures[], Performance, Summary |

**All formatters:**
- Accept: JsonSerializerOptions, optional sessionId (string option), type-specific payload
- Return: string formatted as SSE event (starts with "event: TYPE\ndata: JSON\n\n")
- Session ID injection via injectSessionId adds "SessionId" property to JSON when Some value provided
