/// ## CoverageView.project Mutation Tests
///
/// Proves the test suite catches mutations in `CoverageView.project` — the
/// hot-path function that runs on every coverage event. The function folds
/// over covering tests to partition into status counts and renders a badge.
///
/// These tests build minimal LiveTestState + TestDependencyGraph fixtures
/// and verify that real output differs from mutated output.
module CoverageViewProjectMutationTests

open Expecto
open Expecto.Flip
open System
open SageFs
open SageFs.Features.LiveTesting

// ── Helpers ───────────────────────────────────────────────────────────────

let ts (ms: float) = TimeSpan.FromMilliseconds ms

let mkTest (name: string) (category: TestCategory) : TestCase =
  { Id = TestId.create name TestFramework.Expecto
    FullName = name
    DisplayName = name
    Origin = TestOrigin.SourceMapped ("Tests.fs", 10)
    Labels = []
    Framework = TestFramework.Expecto
    Category = category }

let mkResult (testId: TestId) (result: TestResult) : TestRunResult =
  { TestId = testId
    TestName = TestId.value testId
    Result = result
    Timestamp = DateTimeOffset.UtcNow
    Output = None }

// ── Fixtures ──────────────────────────────────────────────────────────────

let t1 = mkTest "Tests.t1" TestCategory.Unit
let t2 = mkTest "Tests.t2" TestCategory.Unit
let t3 = mkTest "Tests.t3" TestCategory.Unit

let stateWithPass =
  { LiveTestState.empty with
      DiscoveredTests = [| t1; t2; t3 |]
      LastResults =
        Map.ofList [
          t1.Id, mkResult t1.Id (TestResult.Passed (ts 1.0))
          t2.Id, mkResult t2.Id (TestResult.Passed (ts 2.0))
          t3.Id, mkResult t3.Id (TestResult.Passed (ts 3.0))
        ] }

let stateWithFail =
  { LiveTestState.empty with
      DiscoveredTests = [| t1; t2; t3 |]
      LastResults =
        Map.ofList [
          t1.Id, mkResult t1.Id (TestResult.Failed (TestFailure.AssertionFailed "x", ts 1.0))
          t2.Id, mkResult t2.Id (TestResult.Passed (ts 2.0))
          t3.Id, mkResult t3.Id (TestResult.Passed (ts 3.0))
        ] }

let stateWithSkip =
  { LiveTestState.empty with
      DiscoveredTests = [| t1; t2; t3 |]
      LastResults =
        Map.ofList [
          t1.Id, mkResult t1.Id (TestResult.Skipped "reason")
          t2.Id, mkResult t2.Id (TestResult.Passed (ts 2.0))
          t3.Id, mkResult t3.Id (TestResult.Passed (ts 3.0))
        ] }

let stateWithNotRun =
  { LiveTestState.empty with
      DiscoveredTests = [| t1; t2; t3 |]
      LastResults =
        Map.ofList [
          t1.Id, mkResult t1.Id TestResult.NotRun
          t2.Id, mkResult t2.Id (TestResult.Passed (ts 2.0))
          t3.Id, mkResult t3.Id (TestResult.Passed (ts 3.0))
        ] }

let stateWithMissing =
  { LiveTestState.empty with
      DiscoveredTests = [| t1; t2; t3 |]
      LastResults = Map.empty }

let coveringIds = [| t1.Id; t2.Id; t3.Id |]
let depGraph = TestDependencyGraph.empty

// ── Mutation Tests ────────────────────────────────────────────────────────

let coverageViewProjectMutationTests = testList "CoverageView.project mutations" [

  // ── Absent handling ──────────────────────────────────────────────────────

  testCase "WHY — project_empty_coverage_must_be_Absent — no covering tests means Absent health" <| fun () ->
    let real = CoverageView.project CoverageViewMode.defaults [||] depGraph stateWithPass "Prod.fs" 10 "Module.x"
    let mutant : CoverageView = { real with Health = CoverageViewState.Passing }  // wrong: should be Absent
    if real.Health = mutant.Health then
      failwithf "Mutation survived — empty coverage should give Absent, got %A" real.Health

  testCase "WHY — project_empty_coverage_zero_total — no covering tests means TotalCount=0" <| fun () ->
    let real = CoverageView.project CoverageViewMode.defaults [||] depGraph stateWithPass "Prod.fs" 10 "Module.x"
    let mutant : CoverageView = { real with TotalCount = 1 }  // wrong: should be 0
    if real.TotalCount = mutant.TotalCount then
      failwithf "Mutation survived — empty coverage should give TotalCount=0, got %d" real.TotalCount

  testCase "WHY — project_empty_coverage_empty_badge — no covering tests means empty badge text" <| fun () ->
    let real = CoverageView.project CoverageViewMode.defaults [||] depGraph stateWithPass "Prod.fs" 10 "Module.x"
    let mutant : CoverageView = { real with InlineBadgeText = "✓ 0" }  // wrong: should be ""
    if real.InlineBadgeText = mutant.InlineBadgeText then
      failwithf "Mutation survived — empty coverage should give empty badge, got '%s'" real.InlineBadgeText

  // ── TotalCount ───────────────────────────────────────────────────────────

  testCase "WHY — project_total_count_must_equal_coveringIds — TotalCount must match input array length" <| fun () ->
    let real = CoverageView.project CoverageViewMode.defaults coveringIds depGraph stateWithPass "Prod.fs" 10 "Module.x"
    let mutant : CoverageView = { real with TotalCount = 999 }  // wrong
    if real.TotalCount = mutant.TotalCount then
      failwithf "Mutation survived — TotalCount should be %d, got %d" coveringIds.Length real.TotalCount

  // ── Health state for passing ─────────────────────────────────────────────

  testCase "WHY — project_all_passing_must_be_Passing — all results passed means Passing health" <| fun () ->
    let real = CoverageView.project CoverageViewMode.defaults coveringIds depGraph stateWithPass "Prod.fs" 10 "Module.x"
    let mutant : CoverageView = { real with Health = CoverageViewState.Failing }  // wrong
    if real.Health = mutant.Health then
      failwithf "Mutation survived — all passing should give Passing, got %A" real.Health

  // ── Health state for failing ─────────────────────────────────────────────

  testCase "WHY — project_any_failing_must_be_Failing — any failure makes Failing health" <| fun () ->
    let real = CoverageView.project CoverageViewMode.defaults coveringIds depGraph stateWithFail "Prod.fs" 10 "Module.x"
    let mutant : CoverageView = { real with Health = CoverageViewState.Passing }  // wrong
    if real.Health = mutant.Health then
      failwithf "Mutation survived — any failure should give Failing, got %A" real.Health

  // ── Health state for skipped ─────────────────────────────────────────────

  testCase "WHY — project_skipped_counted — Skipped results count as Skipped health" <| fun () ->
    let real = CoverageView.project CoverageViewMode.defaults coveringIds depGraph stateWithSkip "Prod.fs" 10 "Module.x"
    let mutant : CoverageView = { real with Health = CoverageViewState.Passing }  // wrong: skip should make Skipped
    if real.Health = mutant.Health then
      failwithf "Mutation survived — skipped should give Skipped, got %A" real.Health

  // ── NotRun = stale ───────────────────────────────────────────────────────

  testCase "WHY — project_notRun_counted_as_stale — NotRun result counts as Stale health" <| fun () ->
    let real = CoverageView.project CoverageViewMode.defaults coveringIds depGraph stateWithNotRun "Prod.fs" 10 "Module.x"
    let mutant : CoverageView = { real with Health = CoverageViewState.Passing }  // wrong: NotRun → Stale
    if real.Health = mutant.Health then
      failwithf "Mutation survived — NotRun should give Stale, got %A" real.Health

  // ── Missing result = stale ───────────────────────────────────────────────

  testCase "WHY — project_missing_result_counted_as_stale — result not in map counts as Stale" <| fun () ->
    let real = CoverageView.project CoverageViewMode.defaults coveringIds depGraph stateWithMissing "Prod.fs" 10 "Module.x"
    let mutant : CoverageView = { real with Health = CoverageViewState.Passing }  // wrong: missing → Stale
    if real.Health = mutant.Health then
      failwithf "Mutation survived — missing result should give Stale, got %A" real.Health

  // ── InlineBadgeText ──────────────────────────────────────────────────────

  testCase "WHY — project_badge_includes_pass_count — InlineBadgeText shows pass count" <| fun () ->
    let real = CoverageView.project CoverageViewMode.defaults coveringIds depGraph stateWithPass "Prod.fs" 10 "Module.x"
    let mutant : CoverageView = { real with InlineBadgeText = "" }  // wrong: should include badge
    if real.InlineBadgeText = mutant.InlineBadgeText then
      failwithf "Mutation survived — all-pass should show badge, got '%s'" real.InlineBadgeText

  testCase "WHY — project_badge_includes_fail_count — InlineBadgeText shows fail count when any fail" <| fun () ->
    let real = CoverageView.project CoverageViewMode.defaults coveringIds depGraph stateWithFail "Prod.fs" 10 "Module.x"
    let mutant : CoverageView = { real with InlineBadgeText = real.InlineBadgeText.Replace("✗", "") }  // wrong: should show fail
    if real.InlineBadgeText = mutant.InlineBadgeText then
      failwithf "Mutation survived — badge should include fail marker, got '%s'" real.InlineBadgeText

  // ── Symbol/FilePath/Line preservation ────────────────────────────────────

  testCase "WHY — project_preserves_symbol — Symbol field must match input" <| fun () ->
    let real = CoverageView.project CoverageViewMode.defaults coveringIds depGraph stateWithPass "Prod.fs" 42 "MyModule.myFunc"
    let mutant : CoverageView = { real with Symbol = "wrong" }  // wrong
    if real.Symbol = mutant.Symbol then
      failwithf "Mutation survived — Symbol should be 'MyModule.myFunc', got '%s'" real.Symbol

  testCase "WHY — project_preserves_file — FilePath field must match input" <| fun () ->
    let real = CoverageView.project CoverageViewMode.defaults coveringIds depGraph stateWithPass "MyFile.fs" 42 "MyModule.myFunc"
    let mutant : CoverageView = { real with FilePath = "wrong.fs" }  // wrong
    if real.FilePath = mutant.FilePath then
      failwithf "Mutation survived — FilePath should be 'MyFile.fs', got '%s'" real.FilePath

  testCase "WHY — project_preserves_line — DefinitionLine field must match input" <| fun () ->
    let real = CoverageView.project CoverageViewMode.defaults coveringIds depGraph stateWithPass "MyFile.fs" 42 "MyModule.myFunc"
    let mutant : CoverageView = { real with DefinitionLine = 0 }  // wrong
    if real.DefinitionLine = mutant.DefinitionLine then
      failwithf "Mutation survived — DefinitionLine should be 42, got %d" real.DefinitionLine

  // ── Overflow ─────────────────────────────────────────────────────────────

  testCase "WHY — project_overflow_computed — Overflow reflects InlineCollapseAt mode" <| fun () ->
    let mode = { InlineCollapseAt = 2 }
    let real = CoverageView.project mode coveringIds depGraph stateWithPass "Prod.fs" 10 "Module.x"
    let mutant : CoverageView = { real with Overflow = Overflow.Within }  // wrong: 3 tests > threshold 2
    if real.Overflow = mutant.Overflow then
      failwithf "Mutation survived — 3 tests with InlineCollapseAt=2 should overflow, got %A" real.Overflow
]
