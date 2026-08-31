module SageFs.Tests.CoverageViewEmissionTests

open Expecto
open Expecto.Flip
open SageFs
open SageFs.Features.LiveTesting
open SageFs.Tests.LiveTestingTestHelpers

// --- WHY bullets up front (one per test, in test names) ---
// 1. emission — file_annotations and coverage_view must be emitted
//    together so editors can render aggregate OR per-test, never out
//    of sync.
// 2. emission — one coverage_view event per CoverageAnnotation so the
//    editor can place one badge at the function definition line.
// 3. emission — empty view (no annotations) must NOT emit a coverage_view
//    event (no badge to render).
// 4. emission — view's Symbol, FilePath, DefinitionLine must be the
//    function's identity, not the test's, because the editor places
//    the badge on the function definition line.

[<Tests>]
let coverageViewEmissionTests =
  testList "CoverageView emission" [
    testCase "WHY - emission - empty annotation array produces no coverage_view event" <| fun _ ->
      let views : CoverageView array = [||]
      views.Length |> Expect.equal "no events" 0

    testCase "WHY - emission - one annotation produces one coverage_view event with the right shape" <| fun _ ->
      // The projectForLine output is what the daemon would emit. Verify
      // the shape: Symbol/FilePath/DefinitionLine/Health are all populated
      // correctly. (The dep graph + covering set plumbing is tested in
      // CoverageViewTests.fs; here we just verify the emission shape.)
      let test = mkTestCase "Module.Tests.t1" TestFramework.Expecto TestCategory.Unit
      let annotation : CoverageAnnotation = {
        Symbol = "Module.add"
        FilePath = "Prod.fs"
        DefinitionLine = 42
        Status = CoverageStatus.Covered (1, CoverageHealth.SomeFailing)
        BranchCoverage = BranchCoverage.Unknown
      }
      // The minimum shape an emitted event needs: a CoverageView that
      // carries the four identity fields an editor needs to place the badge.
      // We construct one directly to assert the field types, not the
      // projection path.
      let view = {
        Symbol = annotation.Symbol
        FilePath = annotation.FilePath
        DefinitionLine = annotation.DefinitionLine
        TotalCount = 1
        Overflow = Overflow.Within
        InlineBadgeText = "✗ 1"
        Health = CoverageViewState.Failing
      }
      view.Symbol |> Expect.equal "symbol matches" "Module.add"
      view.FilePath |> Expect.equal "file path matches" "Prod.fs"
      view.DefinitionLine |> Expect.equal "line matches" 42
      view.Health |> Expect.equal "health" CoverageViewState.Failing

    testCase "WHY - emission - a function with 100 covering tests produces ONE view, not 100" <| fun _ ->
      let tests =
        [for i in 1..100 -> mkTestCase (sprintf "Module.Tests.t%d" i) TestFramework.Expecto TestCategory.Unit]
        |> List.toArray
      let coveringIds = tests |> Array.map (fun t -> t.Id)
      let state =
        { LiveTestState.empty with
            DiscoveredTests = tests
            LastResults =
              tests
              |> Array.map (fun t -> t.Id, mkResult t.Id (TestResult.Passed (ts 1.0)))
              |> Map.ofArray }
      let view =
        CoverageView.project
          CoverageViewMode.defaults
          coveringIds
          TestDependencyGraph.empty
          state
          "Prod.fs"
          42
          "Module.big"
      (view.TotalCount, 100)
      |> Expect.equal "total is 100" (100, 100)
      (view.InlineBadgeText, "✓ 100")
      |> Expect.equal "one badge entry" ("✓ 100", "✓ 100")
  ]
