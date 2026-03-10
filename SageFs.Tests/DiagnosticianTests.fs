module SageFs.Tests.DiagnosticianTests

open System
open Expecto
open Expecto.Flip
open SageFs.Features
open SageFs.Features.CellDependencyGraph
open SageFs.Features.EvalProvenance
open SageFs.Features.EvalTimeline
open SageFs.Features.LiveTesting
open SageFs.Features.Diagnostician

// ── Test helpers ──────────────────────────────────────────────

let private mkTestId name = TestId.TestId name

let private mkNarrative causalChanges summary = {
  LastPassedAt = Some DateTimeOffset.UtcNow
  TimeSinceLastPass = Some (TimeSpan.FromMinutes 5.0)
  CausalChanges = causalChanges
  PropertyViolation = None
  Summary = summary
}

let private mkGraph cells edges = {
  Cells = cells |> List.map (fun (c: CellInfo) -> c.Id, c) |> Map.ofList
  Edges = edges
}

let private mkCell id source produces consumes = {
  Id = id
  Source = source
  Produces = produces
  Consumes = consumes
}

let private mkTimelineEntry cellId durationMs status = {
  CellId = cellId
  StartMs = 0L
  DurationMs = durationMs
  Status = status
}

let private mkScopeBinding name typeSig = {
  Name = name
  TypeSig = typeSig
  Value = None
}

let private emptyGraph = mkGraph [] []
let private emptyTimeline = { Entries = [] }

// ── Core composition tests ───────────────────────────────────

[<Tests>]
let compositionTests =
  testList "Diagnostician.compose" [

    testCase "no failures returns empty report with Info severity" <| fun _ ->
      let report = Diagnostician.compose emptyGraph [] [] emptyTimeline

      report.Failures
      |> Expect.isEmpty "should have no failures"

      report.Severity
      |> Expect.equal "should be Info" DiagnosticSeverity.Info

      report.Summary
      |> Expect.stringContains "should mention no issues" "No issues"

    testCase "single failure includes narrative and test name" <| fun _ ->
      let narrative = mkNarrative [] "test broke"
      let failures = [ mkTestId "test1", "parseExpression", narrative ]

      let report = Diagnostician.compose emptyGraph failures [] emptyTimeline

      report.Failures
      |> Expect.hasLength "should have 1 failure" 1

      report.Failures.[0].TestName
      |> Expect.equal "should preserve test name" "parseExpression"

      report.Failures.[0].Narrative.Summary
      |> Expect.equal "should preserve narrative" "test broke"

      report.Severity
      |> Expect.equal "should be Critical with failures" DiagnosticSeverity.Critical

    testCase "joins CausalChange.SymbolChanged to CellGraph producer" <| fun _ ->
      let cell0 = mkCell 0 "let tokenize x = x" [ "tokenize" ] []
      let cell1 = mkCell 1 "let parse = tokenize input" [ "parse" ] [ "tokenize" ]
      let graph = mkGraph [ cell0; cell1 ] [ (0, 1) ]

      let narrative =
        mkNarrative
          [ CausalChange.SymbolChanged "tokenize" ]
          "tokenize changed"

      let failures = [ mkTestId "t1", "test_parse", narrative ]

      let report = Diagnostician.compose graph failures [] emptyTimeline

      report.Failures.[0].CausalCells
      |> Expect.contains "should trace tokenize to cell 0" 0

    testCase "includes ripple plan when causal cells found" <| fun _ ->
      let cell0 = mkCell 0 "let x = 1" [ "x" ] []
      let cell1 = mkCell 1 "let y = x + 1" [ "y" ] [ "x" ]
      let cell2 = mkCell 2 "let z = y * 2" [ "z" ] [ "y" ]
      let graph = mkGraph [ cell0; cell1; cell2 ] [ (0, 1); (1, 2) ]

      let narrative =
        mkNarrative
          [ CausalChange.SymbolChanged "x" ]
          "x changed"

      let failures = [ mkTestId "t1", "test_z", narrative ]

      let report = Diagnostician.compose graph failures [] emptyTimeline

      report.RipplePlan
      |> Expect.isSome "should have a ripple plan"

      report.RipplePlan.Value.Steps
      |> Expect.isNonEmpty "should have ripple steps"

    testCase "affected cells include transitive dependents with staleness" <| fun _ ->
      let cell0 = mkCell 0 "let a = 1" [ "a" ] []
      let cell1 = mkCell 1 "let b = a" [ "b" ] [ "a" ]
      let cell2 = mkCell 2 "let c = b" [ "c" ] [ "b" ]
      let graph = mkGraph [ cell0; cell1; cell2 ] [ (0, 1); (1, 2) ]

      let narrative =
        mkNarrative
          [ CausalChange.SymbolChanged "a" ]
          "a changed"

      let failures = [ mkTestId "t1", "test_c", narrative ]

      let report = Diagnostician.compose graph failures [] emptyTimeline

      report.AffectedCells
      |> List.map fst
      |> Expect.contains "cell 1 should be affected" 1

      report.AffectedCells
      |> List.map fst
      |> Expect.contains "cell 2 should be transitively affected" 2

    testCase "includes ghostwriter suggestions from scope bindings" <| fun _ ->
      let bindings = [
        mkScopeBinding "data" "int list"
        mkScopeBinding "name" "string"
      ]

      let report =
        Diagnostician.compose emptyGraph [] bindings emptyTimeline

      report.SuggestedFixes
      |> Expect.isNonEmpty "should have suggestions from scope"

    testCase "detects performance anomaly when P95 > 3x P50" <| fun _ ->
      // Create timeline with mostly fast evals and a few slow spikes
      let entries = [
        // 15 fast entries (10ms each) — establishes low P50
        for i in 0..14 do
          mkTimelineEntry i 10L EvalStatus.Success
        // 5 slow entries (200ms each) — drives up P95
        for i in 15..19 do
          mkTimelineEntry i 200L EvalStatus.Success
      ]
      let timeline = { Entries = entries }

      let report = Diagnostician.compose emptyGraph [] [] timeline

      report.PerformanceContext
      |> Expect.isSome "should have performance context"

      // With no failures but a spike, severity should be Warning
      report.Severity
      |> Expect.equal "should detect anomaly as Warning" DiagnosticSeverity.Warning

    testCase "no P95 spike with uniform timings yields Info" <| fun _ ->
      let entries = [
        for i in 0..9 do
          mkTimelineEntry i 10L EvalStatus.Success
      ]
      let timeline = { Entries = entries }

      let report = Diagnostician.compose emptyGraph [] [] timeline

      report.Severity
      |> Expect.equal "should be Info when stable" DiagnosticSeverity.Info
  ]

// ── Summary narrative tests ──────────────────────────────────

[<Tests>]
let summarizeTests =
  testList "Diagnostician.summarize" [

    testCase "summary is ≤10 lines" <| fun _ ->
      // Create a report with many failures to stress the truncation
      let failures =
        [ for i in 0..9 ->
            { TestId = mkTestId (sprintf "t%d" i)
              TestName = sprintf "test_%d" i
              Narrative = mkNarrative [ CausalChange.SymbolChanged (sprintf "sym%d" i) ] ""
              CausalCells = [ i ]
              Staleness = Map.empty } ]

      let report =
        { DiagnosticReport.empty with
            Failures = failures
            Severity = Critical
            SuggestedFixes = [
              { Code = "fix1"; Explanation = "try this"; Confidence = 0.9 }
              { Code = "fix2"; Explanation = "or this"; Confidence = 0.7 }
            ] }

      let summary = Diagnostician.summarize report
      let lineCount = summary.Split('\n').Length

      lineCount <= 10
      |> Expect.isTrue "summary should be ≤10 lines"

    testCase "summary mentions all failed test names (up to 3)" <| fun _ ->
      let failures =
        [ for name in [ "alpha"; "beta"; "gamma" ] ->
            { TestId = mkTestId name
              TestName = name
              Narrative = mkNarrative [] ""
              CausalCells = []
              Staleness = Map.empty } ]

      let report =
        { DiagnosticReport.empty with
            Failures = failures
            Severity = Critical }

      let summary = Diagnostician.summarize report

      summary |> Expect.stringContains "should mention alpha" "alpha"
      summary |> Expect.stringContains "should mention beta" "beta"
      summary |> Expect.stringContains "should mention gamma" "gamma"

    testCase "summary includes causal change hints" <| fun _ ->
      let narrative =
        mkNarrative
          [ CausalChange.SymbolChanged "tokenize"
            CausalChange.FileChanged "Parser.fs" ]
          ""

      let failure =
        { TestId = mkTestId "t1"
          TestName = "test_parse"
          Narrative = narrative
          CausalCells = [ 0 ]
          Staleness = Map.empty }

      let report =
        { DiagnosticReport.empty with
            Failures = [ failure ]
            Severity = Critical }

      let summary = Diagnostician.summarize report

      summary |> Expect.stringContains "should mention tokenize" "tokenize"
      summary |> Expect.stringContains "should mention Parser.fs" "Parser.fs"

    testCase "empty failures produces positive message" <| fun _ ->
      let summary = Diagnostician.summarize DiagnosticReport.empty

      summary |> Expect.stringContains "should be positive" "No issues"
  ]

// ── Property-based tests ─────────────────────────────────────

[<Tests>]
let propertyTests =
  testList "Diagnostician properties" [

    testCase "compose is pure — same inputs produce same output" <| fun _ ->
      let cell0 = mkCell 0 "let x = 1" [ "x" ] []
      let graph = mkGraph [ cell0 ] []
      let narrative = mkNarrative [ CausalChange.SymbolChanged "x" ] "changed"
      let failures = [ mkTestId "t1", "test1", narrative ]
      let scope = [ mkScopeBinding "x" "int" ]
      let timeline = { Entries = [ mkTimelineEntry 0 10L EvalStatus.Success ] }

      let r1 = Diagnostician.compose graph failures scope timeline
      let r2 = Diagnostician.compose graph failures scope timeline

      r1.Failures.Length
      |> Expect.equal "failure count should be deterministic" r2.Failures.Length

      r1.Severity
      |> Expect.equal "severity should be deterministic" r2.Severity

      r1.Summary
      |> Expect.equal "summary should be deterministic" r2.Summary

    testCase "summary always non-empty for any valid report" <| fun _ ->
      let report = DiagnosticReport.empty
      report.Summary
      |> Expect.isNotEmpty "empty report still has summary"

      let withFailure =
        { report with
            Failures = [
              { TestId = mkTestId "t"
                TestName = "t"
                Narrative = mkNarrative [] ""
                CausalCells = []
                Staleness = Map.empty } ]
            Severity = Critical }

      (Diagnostician.summarize withFailure)
      |> Expect.isNotEmpty "failure report has summary"

    testCase "severity escalates: 0 failures Info, 1+ failures Critical" <| fun _ ->
      let noFailures = Diagnostician.compose emptyGraph [] [] emptyTimeline
      noFailures.Severity
      |> Expect.equal "no failures → Info" DiagnosticSeverity.Info

      let oneFailure =
        let n = mkNarrative [] ""
        Diagnostician.compose emptyGraph [ mkTestId "t1", "test", n ] [] emptyTimeline
      oneFailure.Severity
      |> Expect.equal "1 failure → Critical" DiagnosticSeverity.Critical
  ]

// ── Integration: multi-failure scenario ──────────────────────

[<Tests>]
let integrationTests =
  testList "Diagnostician integration" [

    testCase "multi-failure with ripple, provenance, and suggestions" <| fun _ ->
      // Build a 4-cell chain: a → b → c → d
      let cells = [
        mkCell 0 "let a = 1" [ "a" ] []
        mkCell 1 "let b = a + 1" [ "b" ] [ "a" ]
        mkCell 2 "let c = b * 2" [ "c" ] [ "b" ]
        mkCell 3 "let d = c |> string" [ "d" ] [ "c" ]
      ]
      let graph = mkGraph cells [ (0, 1); (1, 2); (2, 3) ]

      // Two tests fail, both caused by `a` changing
      let narrative1 =
        mkNarrative [ CausalChange.SymbolChanged "a" ] "a changed"
      let narrative2 =
        mkNarrative [ CausalChange.SymbolChanged "b" ] "b changed"

      let failures = [
        mkTestId "t1", "test_c_value", narrative1
        mkTestId "t2", "test_d_output", narrative2
      ]

      // Scope has a list binding → should trigger Ghostwriter suggestions
      let scope = [ mkScopeBinding "items" "int list" ]

      // Timeline with some entries
      let timeline = {
        Entries = [
          for i in 0..4 do
            mkTimelineEntry i 15L EvalStatus.Success
        ]
      }

      let report = Diagnostician.compose graph failures scope timeline

      // Verify all pieces came together
      report.Failures
      |> Expect.hasLength "should have 2 failures" 2

      report.Failures.[0].CausalCells
      |> Expect.isNonEmpty "first failure should have causal cells"

      report.RipplePlan
      |> Expect.isSome "should have ripple plan"

      report.AffectedCells
      |> Expect.isNonEmpty "should have affected cells"

      report.SuggestedFixes
      |> Expect.isNonEmpty "should have suggestions"

      report.PerformanceContext
      |> Expect.isSome "should have performance context"

      report.Severity
      |> Expect.equal "should be Critical" DiagnosticSeverity.Critical

      report.Summary
      |> Expect.isNotEmpty "should have a summary"

      // Summary should mention the test names
      report.Summary
      |> Expect.stringContains "should mention test_c_value" "test_c_value"

      report.Summary
      |> Expect.stringContains "should mention test_d_output" "test_d_output"
  ]

// ── All tests ────────────────────────────────────────────────

[<Tests>]
let allDiagnosticianTests =
  testList "Diagnostician" [
    compositionTests
    summarizeTests
    propertyTests
    integrationTests
  ]
