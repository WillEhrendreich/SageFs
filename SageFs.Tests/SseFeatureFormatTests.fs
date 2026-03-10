module SageFs.Tests.SseFeatureFormatTests

open Expecto
open Expecto.Flip
open System.Text.Json
open SageFs.Features

let private opts = JsonSerializerOptions(PropertyNamingPolicy = JsonNamingPolicy.CamelCase)

[<Tests>]
let sseFeatureFormatTests = testList "SSE Feature Formatters" [

  testCase "formatEvalDiffEvent produces valid SSE with event type" <| fun _ ->
    let summary = EvalDiff.diffLines (Some "old") (Some "new") |> EvalDiff.summarize
    let result = SageFs.SseWriter.formatEvalDiffEvent opts (Some "s1") summary
    result |> Expect.stringStarts "should start with event type" "event: eval_diff\n"

  testCase "formatEvalDiffEvent injects session id" <| fun _ ->
    let summary = EvalDiff.diffLines (Some "a") (Some "a") |> EvalDiff.summarize
    let result = SageFs.SseWriter.formatEvalDiffEvent opts (Some "my-session") summary
    result |> Expect.stringContains "should contain session id" "my-session"

  testCase "formatEvalDiffEvent without session id omits it" <| fun _ ->
    let summary = EvalDiff.diffLines (Some "a") (Some "b") |> EvalDiff.summarize
    let result = SageFs.SseWriter.formatEvalDiffEvent opts None summary
    (result.Contains("SessionId")) |> Expect.isFalse "should not contain SessionId"

  testCase "formatCellDependenciesEvent includes edges" <| fun _ ->
    let cells : CellDependencyGraph.CellInfo list = [
      { Id = 0; Source = "let x = 1"; Produces = ["x"]; Consumes = [] }
      { Id = 1; Source = "let y = x"; Produces = ["y"]; Consumes = ["x"] }
    ]
    let graph = CellDependencyGraph.buildGraph cells
    let result = SageFs.SseWriter.formatCellDependenciesEvent opts (Some "s1") graph
    result |> Expect.stringStarts "should start with event type" "event: cell_dependencies\n"
    result |> Expect.stringContains "should contain edge data" "from"

  testCase "formatBindingScopeMapEvent includes active count" <| fun _ ->
    let scope = BindingExplorer.buildScopeSnapshot [
      { CellIndex = 0; FsiOutput = "val x : int = 1"; Source = "let x = 1" }
    ]
    let result = SageFs.SseWriter.formatBindingScopeMapEvent opts (Some "s1") scope
    result |> Expect.stringStarts "should start with event type" "event: binding_scope_map\n"
    result |> Expect.stringContains "should contain activeCount" "activeCount"

  testCase "formatEvalTimelineEvent includes sparkline" <| fun _ ->
    let tl =
      EvalTimeline.TimelineState.empty
      |> EvalTimeline.TimelineState.record { CellId = 0; StartMs = 0L; DurationMs = 50L; Status = EvalTimeline.Success }
    let stats = EvalTimeline.timelineStats 10 tl
    let result = SageFs.SseWriter.formatEvalTimelineEvent opts (Some "s1") stats
    result |> Expect.stringStarts "should start with event type" "event: eval_timeline\n"
    result |> Expect.stringContains "should contain sparkline" "sparkline"

  testCase "formatEvalTimelineEvent includes percentiles" <| fun _ ->
    let tl =
      EvalTimeline.TimelineState.empty
      |> EvalTimeline.TimelineState.record { CellId = 0; StartMs = 0L; DurationMs = 50L; Status = EvalTimeline.Success }
      |> EvalTimeline.TimelineState.record { CellId = 1; StartMs = 100L; DurationMs = 200L; Status = EvalTimeline.Success }
    let stats = EvalTimeline.timelineStats 20 tl
    let result = SageFs.SseWriter.formatEvalTimelineEvent opts (Some "s1") stats
    result |> Expect.stringContains "should contain p50" "p50Ms"
    result |> Expect.stringContains "should contain p95" "p95Ms"

  testCase "all formatters end with double newline per SSE spec" <| fun _ ->
    let diff = EvalDiff.diffLines (Some "a") (Some "b") |> EvalDiff.summarize
    SageFs.SseWriter.formatEvalDiffEvent opts None diff
    |> Expect.stringEnds "diff should end with \\n\\n" "\n\n"

    let graph = CellDependencyGraph.buildGraph []
    SageFs.SseWriter.formatCellDependenciesEvent opts None graph
    |> Expect.stringEnds "dep should end with \\n\\n" "\n\n"

    let scope = BindingExplorer.buildScopeSnapshot []
    SageFs.SseWriter.formatBindingScopeMapEvent opts None scope
    |> Expect.stringEnds "scope should end with \\n\\n" "\n\n"

    EvalTimeline.timelineStats 10 EvalTimeline.TimelineState.empty
    |> SageFs.SseWriter.formatEvalTimelineEvent opts None
    |> Expect.stringEnds "timeline should end with \\n\\n" "\n\n"

  testCase "formatDomainModelEvent produces valid SSE with event type" <| fun _ ->
    let annots : DomainModelViz.AnnotatedTransition list = [
      { FromState = "Draft"; ToState = "Submitted"
        FunctionName = Some "submit"; IsErrorBranch = false
        Health = DomainModelViz.TransitionHealth.Passing }
    ]
    let result = SageFs.SseWriter.formatDomainModelEvent opts None annots
    result |> Expect.stringStarts "event type" "event: domain_model\n"

  testCase "formatDomainModelEvent injects session id" <| fun _ ->
    let annots : DomainModelViz.AnnotatedTransition list = [
      { FromState = "A"; ToState = "B"
        FunctionName = Some "f"; IsErrorBranch = false
        Health = DomainModelViz.TransitionHealth.Untested }
    ]
    let result = SageFs.SseWriter.formatDomainModelEvent opts (Some "s1") annots
    result |> Expect.stringContains "session id" "s1"

  testCase "formatDomainModelEvent without session id omits it" <| fun _ ->
    let result = SageFs.SseWriter.formatDomainModelEvent opts None []
    (result.Contains("SessionId")) |> Expect.isFalse "no session id"

  testCase "formatDomainModelEvent serializes health status" <| fun _ ->
    let annots : DomainModelViz.AnnotatedTransition list = [
      { FromState = "A"; ToState = "B"
        FunctionName = Some "f"; IsErrorBranch = false
        Health = DomainModelViz.TransitionHealth.Passing }
      { FromState = "B"; ToState = "C"
        FunctionName = None; IsErrorBranch = false
        Health = DomainModelViz.TransitionHealth.NotImplemented }
    ]
    let result = SageFs.SseWriter.formatDomainModelEvent opts None annots
    result |> Expect.stringContains "Passing" "Passing"
    result |> Expect.stringContains "NotImplemented" "NotImplemented"

  testCase "formatDomainModelEvent ends with double newline" <| fun _ ->
    SageFs.SseWriter.formatDomainModelEvent opts None []
    |> Expect.stringEnds "domain_model ends \\n\\n" "\n\n"

]

// ── DiagnosisReady per-failure payload helpers ─────────────────────────────────
let private buildFailure (testName: string) (symbols: string list) : Diagnostician.DiagnosedFailure = {
  TestId = LiveTesting.TestId.TestId testName
  TestName = testName
  Narrative = {
    LastPassedAt = None
    TimeSinceLastPass = None
    CausalChanges = symbols |> List.map LiveTesting.CausalChange.SymbolChanged
    PropertyViolation = None
    Summary = sprintf "%s changed" (String.concat ", " symbols)
  }
  CausalCells = []
  Staleness = Map.empty
}

[<Tests>]
let sseDiagnosisReadyTests = testList "SSE DiagnosisReady formatter" [

  testCase "formatDiagnosisReadyEvent includes failures array key" <| fun _ ->
    let result = SageFs.SseWriter.formatDiagnosisReadyEvent opts None Diagnostician.DiagnosticReport.empty
    result |> Expect.stringContains "should contain failures key" "failures"

  testCase "formatDiagnosisReadyEvent includes per-failure test name" <| fun _ ->
    let report = { Diagnostician.DiagnosticReport.empty with
                     Failures = [ buildFailure "MyModule.shouldBoundaryCheck" ["threshold"] ] }
    let result = SageFs.SseWriter.formatDiagnosisReadyEvent opts (Some "s1") report
    result |> Expect.stringContains "should contain test name" "MyModule.shouldBoundaryCheck"

  testCase "formatDiagnosisReadyEvent includes causal symbols per failure" <| fun _ ->
    let report = { Diagnostician.DiagnosticReport.empty with
                     Failures = [ buildFailure "SomeTest" ["alpha"; "beta"] ] }
    let result = SageFs.SseWriter.formatDiagnosisReadyEvent opts None report
    result |> Expect.stringContains "should contain first symbol" "alpha"
    result |> Expect.stringContains "should contain second symbol" "beta"

  testCase "formatDiagnosisReadyEvent fileChanged excluded from causalSymbols" <| fun _ ->
    let failure = {
      buildFailure "SomeTest" [] with
        Narrative = { (buildFailure "SomeTest" []).Narrative with
                        CausalChanges = [ LiveTesting.CausalChange.FileChanged "/foo/bar.fs"
                                          LiveTesting.CausalChange.SymbolChanged "myFn" ] } }
    let report = { Diagnostician.DiagnosticReport.empty with Failures = [failure] }
    let result = SageFs.SseWriter.formatDiagnosisReadyEvent opts None report
    result |> Expect.stringContains "symbol survives" "myFn"
    (result.Contains("foo/bar.fs")) |> Expect.isFalse "file path excluded from causalSymbols"

  testCase "formatDiagnosisReadyEvent ends with double newline" <| fun _ ->
    SageFs.SseWriter.formatDiagnosisReadyEvent opts None Diagnostician.DiagnosticReport.empty
    |> Expect.stringEnds "diagnosis_ready ends \\n\\n" "\n\n"
]
