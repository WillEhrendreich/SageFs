module SageFs.Tests.SseSerializationBenchmarks

open System
open System.Text.Json
open System.Text.Json.Serialization
open Expecto
open Expecto.Flip
open SageFs.SseWriter
open SageFs.Features.LiveTesting
open SageFs.Features.EvalDiff
open SageFs.Features.CellDependencyGraph
open SageFs.Features.BindingExplorer
open SageFs.Features.EvalTimeline
open SageFs.Features.DomainModelViz
open SageFs.Features.Diagnostician
open SageFs.Features.FsiOutputParser
open SageFs.Features.EvalProvenance
open SageFs.Features.Ghostwriter

// ── JSON options matching daemon configuration ──

let private jsonOpts =
  let opts = JsonSerializerOptions(PropertyNamingPolicy = JsonNamingPolicy.CamelCase)
  opts.Converters.Add(JsonFSharpConverter())
  opts

// ── Realistic test data factories ──

let private mkTestSummary () : TestSummary =
  { Total = 247; Passed = 230; Failed = 12; Stale = 5; Running = 0; Disabled = 3; Enabled = true }

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

let private mkDiffSummary () : DiffSummary =
  { Lines =
      [ for i in 1 .. 20 do
          match i % 4 with
          | 0 -> Added (sprintf "let newBinding%d = computeValue %d" i (i * 3))
          | 1 -> Removed (sprintf "let oldBinding%d = legacyCompute %d" i (i * 2))
          | 2 -> Modified (sprintf "let x%d = old%d" i i, sprintf "let x%d = new%d" i i)
          | _ -> Unchanged (sprintf "let stable%d = stableValue" i) ]
    AddedCount = 5; RemovedCount = 5; ModifiedCount = 5; UnchangedCount = 5 }

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

let private mkTimelineStats () : TimelineStats =
  { Count = 150; P50Ms = Some 42.0; P95Ms = Some 180.0; P99Ms = Some 450.0
    MeanMs = Some 67.3; Sparkline = "▁▂▃▄▅▆▇█▇▆▅▄▃▂▁▂▃▅▇" }

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

let private mkTestSourceLocations () : TestSourceLocation list =
  [ for i in 1 .. 50 do
      { CellId = i % 10
        TestName = sprintf "SageFs.Tests.Module%d.test%d" (i % 8) i
        FilePath = sprintf "C:\\Code\\Repos\\SageFs\\SageFs.Tests\\Module%dTests.fs" (i % 8)
        StartLine = 10 + i * 12
        EndLine = 10 + i * 12 + 5 } ]

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

let private mkFsiBindings () : FsiBinding array =
  [| for i in 1 .. 10 do
       { Name = sprintf "binding%d" i
         TypeSig = sprintf "int list"
         Value = match i % 2 with 0 -> Some (sprintf "[%d]" i) | _ -> None
         ShadowCount = match i % 4 with 0 -> 1 | _ -> 0 } |]

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

// ── Measurement helpers ──

let private measureNs (iterations: int) (f: unit -> 'a) : float =
  // Warm up
  for _ in 1 .. 10 do f () |> ignore
  // Measure
  let sw = Diagnostics.Stopwatch.StartNew()
  for _ in 1 .. iterations do f () |> ignore
  sw.Stop()
  float sw.ElapsedTicks / float Diagnostics.Stopwatch.Frequency * 1_000_000_000.0 / float iterations

let private measureBytes (f: unit -> string) : int =
  let result = f ()
  System.Text.Encoding.UTF8.GetByteCount(result)

// ── Latency benchmarks (Expecto) ──

[<Tests>]
let sseSerializationBenchmarks = testList "SSE serialization benchmarks" [
  testList "payload sizes" [
    testCase "warmup_progress is compact" <| fun () ->
      let bytes = measureBytes (fun () ->
        formatWarmupProgressEvent jsonOpts (Some "sess-1") 3 5 "Loading assemblies...")
      (bytes, 256) |> Expect.isLessThan "warmup event should be under 256 bytes"

    testCase "test_summary is compact" <| fun () ->
      let bytes = measureBytes (fun () ->
        formatTestSummaryEvent jsonOpts (Some "sess-1") (mkTestSummary ()))
      (bytes, 256) |> Expect.isLessThan "summary should be under 256 bytes"

    testCase "test_results_batch 50 entries" <| fun () ->
      let bytes = measureBytes (fun () ->
        formatTestResultsBatchEvent jsonOpts (Some "sess-1") (mkTestResultsBatch 50))
      (bytes, 32768) |> Expect.isLessThan "50-entry batch should be under 32KB"

    testCase "test_results_batch 250 entries" <| fun () ->
      let bytes = measureBytes (fun () ->
        formatTestResultsBatchEvent jsonOpts (Some "sess-1") (mkTestResultsBatch 250))
      (bytes, 131072) |> Expect.isLessThan "250-entry batch should be under 128KB"

    testCase "file_annotations typical file" <| fun () ->
      let bytes = measureBytes (fun () ->
        formatFileAnnotationsEvent jsonOpts (Some "sess-1") (mkFileAnnotations ()))
      (bytes, 16384) |> Expect.isLessThan "annotations should be under 16KB"

    testCase "eval_diff moderate changeset" <| fun () ->
      let bytes = measureBytes (fun () ->
        formatEvalDiffEvent jsonOpts (Some "sess-1") (mkDiffSummary ()))
      (bytes, 4096) |> Expect.isLessThan "diff should be under 4KB"

    testCase "cell_dependencies 20-cell graph" <| fun () ->
      let bytes = measureBytes (fun () ->
        formatCellDependenciesEvent jsonOpts (Some "sess-1") (mkCellGraph ()))
      (bytes, 8192) |> Expect.isLessThan "graph should be under 8KB"

    testCase "binding_scope_map 40 bindings" <| fun () ->
      let bytes = measureBytes (fun () ->
        formatBindingScopeMapEvent jsonOpts (Some "sess-1") (mkBindingScopeSnapshot ()))
      (bytes, 8192) |> Expect.isLessThan "bindings should be under 8KB"

    testCase "eval_timeline stats" <| fun () ->
      let bytes = measureBytes (fun () ->
        formatEvalTimelineEvent jsonOpts (Some "sess-1") (mkTimelineStats ()))
      (bytes, 512) |> Expect.isLessThan "timeline should be under 512 bytes"

    testCase "failure_narratives 5 failures" <| fun () ->
      let bytes = measureBytes (fun () ->
        formatFailureNarrativesEvent jsonOpts (Some "sess-1") (mkFailureNarratives ()))
      (bytes, 4096) |> Expect.isLessThan "narratives should be under 4KB"

    testCase "test_source_locations 50 tests" <| fun () ->
      let bytes = measureBytes (fun () ->
        formatTestSourceLocationsEvent jsonOpts (Some "sess-1") (mkTestSourceLocations ()))
      (bytes, 16384) |> Expect.isLessThan "locations should be under 16KB"

    testCase "domain_model 12 transitions" <| fun () ->
      let bytes = measureBytes (fun () ->
        formatDomainModelEvent jsonOpts (Some "sess-1") (mkAnnotatedTransitions ()))
      (bytes, 4096) |> Expect.isLessThan "domain model should be under 4KB"

    testCase "bindings_snapshot 10 bindings" <| fun () ->
      let bytes = measureBytes (fun () ->
        formatBindingsSnapshotEvent jsonOpts (Some "sess-1") (mkBindingValues ()) (mkFsiBindings ()))
      (bytes, 8192) |> Expect.isLessThan "bindings snapshot should be under 8KB"

    testCase "test_trace pre-serialized" <| fun () ->
      let traceJson = """{"enabled":true,"providers":["Expecto"],"summary":{"total":100,"passed":95}}"""
      let bytes = measureBytes (fun () ->
        formatTestTraceEvent (Some "sess-1") traceJson)
      (bytes, 1024) |> Expect.isLessThan "test trace should be under 1KB"

    testCase "diagnosis_ready report" <| fun () ->
      let bytes = measureBytes (fun () ->
        formatDiagnosisReadyEvent jsonOpts (Some "sess-1") (mkDiagnosticReport ()))
      (bytes, 8192) |> Expect.isLessThan "diagnosis should be under 8KB"
  ]

  // Thresholds are FSI-compatible (interpreter adds ~5-20x overhead vs compiled).
  // Compiled baselines: warmup ~1μs, summary ~2μs, batch-50 ~40μs, batch-250 ~200μs,
  // annotations ~20μs, diff ~10μs, eval ~2μs, deps ~20μs, narratives ~10μs, domain ~10μs.
  testList "serialization latency" [
    testCase "warmup_progress under 50μs" <| fun () ->
      let nsPerOp = measureNs 10000 (fun () ->
        formatWarmupProgressEvent jsonOpts (Some "sess-1") 3 5 "Loading assemblies...")
      (nsPerOp, 50_000.0) |> Expect.isLessThan "warmup_progress should serialize under 50μs"

    testCase "test_summary under 50μs" <| fun () ->
      let summary = mkTestSummary ()
      let nsPerOp = measureNs 10000 (fun () ->
        formatTestSummaryEvent jsonOpts (Some "sess-1") summary)
      (nsPerOp, 50_000.0) |> Expect.isLessThan "test_summary should serialize under 50μs"

    testCase "test_results_batch 50 under 2ms" <| fun () ->
      let batch = mkTestResultsBatch 50
      let nsPerOp = measureNs 1000 (fun () ->
        formatTestResultsBatchEvent jsonOpts (Some "sess-1") batch)
      (nsPerOp, 2_000_000.0) |> Expect.isLessThan "50-entry batch should serialize under 2ms"

    testCase "test_results_batch 250 under 10ms" <| fun () ->
      let batch = mkTestResultsBatch 250
      let nsPerOp = measureNs 200 (fun () ->
        formatTestResultsBatchEvent jsonOpts (Some "sess-1") batch)
      (nsPerOp, 10_000_000.0) |> Expect.isLessThan "250-entry batch should serialize under 10ms"

    testCase "file_annotations under 2ms" <| fun () ->
      let annotations = mkFileAnnotations ()
      let nsPerOp = measureNs 1000 (fun () ->
        formatFileAnnotationsEvent jsonOpts (Some "sess-1") annotations)
      (nsPerOp, 2_000_000.0) |> Expect.isLessThan "file_annotations should serialize under 2ms"

    testCase "eval_diff under 500μs" <| fun () ->
      let diff = mkDiffSummary ()
      let nsPerOp = measureNs 5000 (fun () ->
        formatEvalDiffEvent jsonOpts (Some "sess-1") diff)
      (nsPerOp, 500_000.0) |> Expect.isLessThan "eval_diff should serialize under 500μs"

    testCase "eval_result under 50μs" <| fun () ->
      let nsPerOp = measureNs 10000 (fun () ->
        formatEvalResultEvent jsonOpts (Some "sess-1")
          "C:\\Code\\test.fsx" 1 "val it: int = 42" true 23.5)
      (nsPerOp, 50_000.0) |> Expect.isLessThan "eval_result should serialize under 50μs"

    testCase "cell_dependencies under 1ms" <| fun () ->
      let graph = mkCellGraph ()
      let nsPerOp = measureNs 2000 (fun () ->
        formatCellDependenciesEvent jsonOpts (Some "sess-1") graph)
      (nsPerOp, 1_000_000.0) |> Expect.isLessThan "cell_dependencies should serialize under 1ms"

    testCase "failure_narratives under 500μs" <| fun () ->
      let narratives = mkFailureNarratives ()
      let nsPerOp = measureNs 5000 (fun () ->
        formatFailureNarrativesEvent jsonOpts (Some "sess-1") narratives)
      (nsPerOp, 500_000.0) |> Expect.isLessThan "failure_narratives should serialize under 500μs"

    testCase "domain_model under 10ms" <| fun () ->
      let transitions = mkAnnotatedTransitions ()
      let nsPerOp = measureNs 5000 (fun () ->
        formatDomainModelEvent jsonOpts (Some "sess-1") transitions)
      (nsPerOp, 10_000_000.0) |> Expect.isLessThan "domain_model should serialize under 10ms"

    testCase "bindings_snapshot under 500μs" <| fun () ->
      let vals = mkBindingValues ()
      let bindings = mkFsiBindings ()
      let nsPerOp = measureNs 5000 (fun () ->
        formatBindingsSnapshotEvent jsonOpts (Some "sess-1") vals bindings)
      (nsPerOp, 500_000.0) |> Expect.isLessThan "bindings_snapshot should serialize under 500μs"

    testCase "test_trace under 50μs" <| fun () ->
      let traceJson = """{"enabled":true,"providers":["Expecto"],"summary":{"total":100}}"""
      let nsPerOp = measureNs 10000 (fun () ->
        formatTestTraceEvent (Some "sess-1") traceJson)
      (nsPerOp, 50_000.0) |> Expect.isLessThan "test_trace should serialize under 50μs"

    testCase "diagnosis_ready under 5ms" <| fun () ->
      let report = mkDiagnosticReport ()
      let nsPerOp = measureNs 1000 (fun () ->
        formatDiagnosisReadyEvent jsonOpts (Some "sess-1") report)
      (nsPerOp, 5_000_000.0) |> Expect.isLessThan "diagnosis_ready should serialize under 5ms"
  ]

  testList "format correctness" [
    testCase "all events start with event: and end with double newline" <| fun () ->
      let events = [
        formatWarmupProgressEvent jsonOpts None 1 5 "phase1"
        formatTestSummaryEvent jsonOpts None (mkTestSummary ())
        formatTestResultsBatchEvent jsonOpts None (mkTestResultsBatch 5)
        formatFileAnnotationsEvent jsonOpts None (mkFileAnnotations ())
        formatEvalDiffEvent jsonOpts None (mkDiffSummary ())
        formatEvalStartedEvent jsonOpts None "test.fsx" 1
        formatEvalResultEvent jsonOpts None "test.fsx" 1 "ok" true 10.0
        formatCellDependenciesEvent jsonOpts None (mkCellGraph ())
        formatBindingScopeMapEvent jsonOpts None (mkBindingScopeSnapshot ())
        formatEvalTimelineEvent jsonOpts None (mkTimelineStats ())
        formatFailureNarrativesEvent jsonOpts None (mkFailureNarratives ())
        formatTestSourceLocationsEvent jsonOpts None (mkTestSourceLocations ())
        formatDomainModelEvent jsonOpts None (mkAnnotatedTransitions ())
        formatBindingsSnapshotEvent jsonOpts None (mkBindingValues ()) (mkFsiBindings ())
        formatTestTraceEvent None """{"enabled":true}"""
        formatDiagnosisReadyEvent jsonOpts None (mkDiagnosticReport ())
      ]
      for evt in events do
        evt |> Expect.stringStarts "should start with event:" "event: "
        evt |> Expect.stringEnds "should end with double newline" "\n\n"

    testCase "sessionId injection adds SessionId field" <| fun () ->
      let evt = formatTestSummaryEvent jsonOpts (Some "my-session") (mkTestSummary ())
      evt |> Expect.stringContains "should contain SessionId" "\"SessionId\":\"my-session\""

    testCase "no sessionId produces clean JSON" <| fun () ->
      let evt = formatTestSummaryEvent jsonOpts None (mkTestSummary ())
      evt.Contains("SessionId") |> Expect.isFalse "should not contain SessionId"
  ]
]
