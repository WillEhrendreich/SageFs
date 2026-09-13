/// ## CoverageView.project Mutation Tests
///
/// Proves the test suite catches mutations in `CoverageView.project` — the
/// hot-path function that runs on every coverage event. The function folds
/// over covering tests to partition into status counts and renders a badge.
///
/// These tests build minimal LiveTestState + TestDependencyGraph fixtures
/// and assert the FULL resulting `CoverageView` record against the exact
/// expected value (not merely inequality with one hand-picked wrong field),
/// so a mutant that changes any field to any other wrong value is killed.
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

  // ── Absent handling — full record, since no covering tests is a distinct branch ──

  testCase "WHY — project_empty_coverage_is_fully_Absent — no covering tests means the whole Absent shape" <| fun () ->
    let expected : CoverageView =
      { Symbol = "Module.x"; FilePath = "Prod.fs"; DefinitionLine = 10
        TotalCount = 0; Overflow = Overflow.Within; InlineBadgeText = ""
        Health = CoverageViewState.Absent }
    CoverageView.project CoverageViewMode.defaults [||] depGraph stateWithPass "Prod.fs" 10 "Module.x"
    |> Expect.equal "empty coverage must produce the exact Absent CoverageView" expected

  // ── All-passing — full record ────────────────────────────────────────────

  testCase "WHY — project_all_passing_produces_exact_record — Passing health with a single ✓ badge" <| fun () ->
    let expected : CoverageView =
      { Symbol = "Module.x"; FilePath = "Prod.fs"; DefinitionLine = 10
        TotalCount = 3; Overflow = Overflow.Within; InlineBadgeText = "✓ 3"
        Health = CoverageViewState.Passing }
    CoverageView.project CoverageViewMode.defaults coveringIds depGraph stateWithPass "Prod.fs" 10 "Module.x"
    |> Expect.equal "3 passing tests must produce the exact Passing CoverageView" expected

  // ── Any failing — full record ────────────────────────────────────────────

  testCase "WHY — project_any_failing_produces_exact_record — Failing health with pass+fail badges in order" <| fun () ->
    let expected : CoverageView =
      { Symbol = "Module.x"; FilePath = "Prod.fs"; DefinitionLine = 10
        TotalCount = 3; Overflow = Overflow.Within; InlineBadgeText = "✓ 2 ✗ 1"
        Health = CoverageViewState.Failing }
    CoverageView.project CoverageViewMode.defaults coveringIds depGraph stateWithFail "Prod.fs" 10 "Module.x"
    |> Expect.equal "1 failing + 2 passing must produce the exact Failing CoverageView" expected

  // ── Skipped — full record ────────────────────────────────────────────────

  testCase "WHY — project_skipped_produces_exact_record — Skipped health with pass+skip badges" <| fun () ->
    let expected : CoverageView =
      { Symbol = "Module.x"; FilePath = "Prod.fs"; DefinitionLine = 10
        TotalCount = 3; Overflow = Overflow.Within; InlineBadgeText = "✓ 2 ⊘ 1"
        Health = CoverageViewState.Skipped }
    CoverageView.project CoverageViewMode.defaults coveringIds depGraph stateWithSkip "Prod.fs" 10 "Module.x"
    |> Expect.equal "1 skipped + 2 passing must produce the exact Skipped CoverageView" expected

  // ── NotRun = stale — full record ─────────────────────────────────────────

  testCase "WHY — project_notRun_produces_exact_record — NotRun counts as Stale with pass+stale badges" <| fun () ->
    let expected : CoverageView =
      { Symbol = "Module.x"; FilePath = "Prod.fs"; DefinitionLine = 10
        TotalCount = 3; Overflow = Overflow.Within; InlineBadgeText = "✓ 2 ~ 1"
        Health = CoverageViewState.Stale }
    CoverageView.project CoverageViewMode.defaults coveringIds depGraph stateWithNotRun "Prod.fs" 10 "Module.x"
    |> Expect.equal "1 NotRun + 2 passing must produce the exact Stale CoverageView" expected

  // ── Missing result = stale — full record ─────────────────────────────────

  testCase "WHY — project_missing_result_produces_exact_record — a covering id with no result is Stale for all three" <| fun () ->
    let expected : CoverageView =
      { Symbol = "Module.x"; FilePath = "Prod.fs"; DefinitionLine = 10
        TotalCount = 3; Overflow = Overflow.Within; InlineBadgeText = "~ 3"
        Health = CoverageViewState.Stale }
    CoverageView.project CoverageViewMode.defaults coveringIds depGraph stateWithMissing "Prod.fs" 10 "Module.x"
    |> Expect.equal "3 covering ids with no LastResults entry must produce the exact Stale CoverageView" expected

  // ── Symbol/FilePath/Line preservation — full record ──────────────────────

  testCase "WHY — project_preserves_symbol_file_and_line — the caller's identity fields pass through untouched" <| fun () ->
    let expected : CoverageView =
      { Symbol = "MyModule.myFunc"; FilePath = "MyFile.fs"; DefinitionLine = 42
        TotalCount = 3; Overflow = Overflow.Within; InlineBadgeText = "✓ 3"
        Health = CoverageViewState.Passing }
    CoverageView.project CoverageViewMode.defaults coveringIds depGraph stateWithPass "MyFile.fs" 42 "MyModule.myFunc"
    |> Expect.equal "Symbol, FilePath and DefinitionLine must be exactly what the caller passed in" expected

  // ── Overflow — full record ────────────────────────────────────────────────

  testCase "WHY — project_overflow_computed_from_mode — Overflow reflects InlineCollapseAt mode" <| fun () ->
    let mode = { InlineCollapseAt = 2 }
    let expected : CoverageView =
      { Symbol = "Module.x"; FilePath = "Prod.fs"; DefinitionLine = 10
        TotalCount = 3; Overflow = Overflow.Overflow 1; InlineBadgeText = "✓ 3"
        Health = CoverageViewState.Passing }
    CoverageView.project mode coveringIds depGraph stateWithPass "Prod.fs" 10 "Module.x"
    |> Expect.equal "3 tests with InlineCollapseAt=2 must overflow by exactly 1" expected
]
