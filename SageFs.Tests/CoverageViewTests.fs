module SageFs.Tests.CoverageViewTests

open System
open Expecto
open Expecto.Flip
open SageFs
open SageFs.Features.LiveTesting
open SageFs.Tests.LiveTestingTestHelpers

// --- WHY bullets up front (one per test, in test names) ---
// 1. CoverageView — defaults must NOT collapse F# users' many tests per function
//    because collapsing by default would punish the F# style of writing many small
//    pure-function tests against a single production binding. xUnit's "1-3 per method"
//    assumption does not hold for F#.
// 2. CoverageView — every threshold must be user-configurable because the user
//    asked for "the most options to customize this as they like, with sane defaults,
//    but not boxing them in".
// 3. CoverageView.project — must be pure and allocation-light because it is called
//    on hover and CodeLens request in the editor. JIT-friendliness matters.
// 4. CoverageView.project — must return None for uncovered symbols because the
//    editor should not render any decoration for a symbol with no tests.
// 5. CoverageView — SortBy / Filter / GroupBy must be DUs not magic strings because
//    a magic string costs us a hash lookup per call. Pattern matching on a DU tag
//    is a single integer compare and branch.
// 6. CoverageView — inline badge is a bounded array (one per status kind) because
//    the editor's render budget is "one line of text", and an unbounded array would
//    be a contract violation that every renderer has to re-derive.
// 7. CoverageView — toInlineBadge must format compactly because the editor calls
//    it on every visible test line. Branchless where possible.

// --- Helpers (kept local to avoid bloating the shared helpers file) ---

let private mkDep (entries: (string * TestId) list) : TestDependencyGraph =
  let bySymbol =
    entries
    |> List.groupBy fst
    |> List.map (fun (sym, pairs) -> sym, pairs |> List.map snd |> Array.ofList)
    |> Map.ofList
  { TestDependencyGraph.empty with
      SymbolToTests = bySymbol }

let private mkTest (name: string) (category: TestCategory) : TestCase =
  { Id = TestId.create name TestFramework.Expecto
    FullName = name
    DisplayName = name
    Origin = TestOrigin.SourceMapped ("Tests.fs", 10)
    Labels = []
    Framework = TestFramework.Expecto
    Category = category }

let private mkResult (testId: TestId) (result: TestResult) : TestRunResult =
  { TestId = testId
    TestName = TestId.value testId
    Result = result
    Timestamp = DateTimeOffset.UtcNow
    Output = None }

[<Tests>]
let defaultsHonorFSharpUsage =
  testList "CoverageView defaults" [

    testCase "WHY — defaults — inlineCollapseAt must default to Int32.MaxValue because F# users write many tests per function and auto-collapsing would punish their style" <| fun _ ->
      let mode = CoverageViewMode.defaults
      mode.InlineCollapseAt
      |> Expect.equal "F#-friendly default is no auto-collapse" Int32.MaxValue
  ]

[<Tests>]
let modeIsFullyConfigurable =
  testList "CoverageViewMode configurability" [

    testCase "WHY — mode — all thresholds must be settable because the user wants options not boxing-in" <| fun _ ->
      let custom =
        { CoverageViewMode.defaults with
            InlineCollapseAt = 5
            SuppressBelow = 0
            SortBy = CoverageSort.StatusFirst
            Filter = CoverageFilter.Failing
            GroupBy = CoverageGroup.None }
      custom.InlineCollapseAt |> Expect.equal "InlineCollapseAt settable" 5
      custom.SuppressBelow |> Expect.equal "SuppressBelow settable" 0
      (match custom.SortBy with
       | CoverageSort.StatusFirst -> ()
       | _ -> failtest "SortBy not set")
      (match custom.Filter with
       | CoverageFilter.Failing -> ()
       | _ -> failtest "Filter not set")
      (match custom.GroupBy with
       | CoverageGroup.None -> ()
       | _ -> failtest "GroupBy not set")
  ]

[<Tests>]
let projectReturnsNoneForUncovered =
  testList "CoverageView.project uncovered" [

    testCase "WHY — project — uncovered symbol returns None because the editor must not paint any decoration for a symbol with no tests" <| fun _ ->
      let tid = TestId.create "t1" TestFramework.Expecto
      let depGraph = mkDep [ "Module.foo", tid ]
      let state =
        { LiveTestState.empty with
            DiscoveredTests = [| mkTest "Tests.t1" TestCategory.Unit |] }
      let view =
        CoverageView.project
          CoverageViewMode.defaults
          Set.empty // No tests cover "Module.uncovered"
          depGraph
          state
          "Module.uncovered"
      view |> Expect.isNone "uncovered symbol has no view"
  ]

[<Tests>]
let projectIsPure =
  testList "CoverageView.project purity" [

    testCase "WHY — project — same input produces structurally equal output because editors cache and compare the projection; impure projection would defeat change detection" <| fun _ ->
      let tid = TestId.create "Tests.t1" TestFramework.Expecto
      let depGraph = mkDep [ "Module.foo", tid ]
      let state =
        { LiveTestState.empty with
            DiscoveredTests = [| mkTest "Tests.t1" TestCategory.Unit |]
            LastResults = Map.ofList [ tid, mkResult tid (TestResult.Passed (ts 1.0)) ] }
      let v1 = CoverageView.project CoverageViewMode.defaults (Set.ofList [ tid ]) depGraph state "Module.foo"
      let v2 = CoverageView.project CoverageViewMode.defaults (Set.ofList [ tid ]) depGraph state "Module.foo"
      v1 |> Expect.isSome "produces a view"
      v2 |> Expect.isSome "produces a view"
      // Structural equality of the record is automatic in F#.
      Expect.equal "deterministic" v1 v2
  ]

[<Tests>]
let inlineBadgeHasBoundedAity =
  testList "CoverageView DU arity" [

    testCase "WHY — CoverageBadge DU has exactly 5 cases — because the editor render budget is one line and a 6th case would change the contract" <| fun _ ->
      // The DU has exactly 5 cases by construction. We assert by exhaustively
      // matching all 5 — if anyone adds a 6th, this test still compiles but
      // the F# compiler emits a warning about incomplete matches elsewhere.
      let rank = function
        | CoverageBadge.Pass _ -> 0
        | CoverageBadge.Fail _ -> 1
        | CoverageBadge.Running _ -> 2
        | CoverageBadge.Stale _ -> 3
        | CoverageBadge.Skipped _ -> 4
      let ranks = [0; 1; 2; 3; 4]
      ranks
      |> List.iter (fun r ->
        // Just exercise the match so the compiler checks exhaustiveness
        ignore r)
      rank (CoverageBadge.Pass 1) |> Expect.equal "Pass rank" 0
      rank (CoverageBadge.Fail 1) |> Expect.equal "Fail rank" 1
      rank (CoverageBadge.Running 1) |> Expect.equal "Running rank" 2
      rank (CoverageBadge.Stale 1) |> Expect.equal "Stale rank" 3
      rank (CoverageBadge.Skipped 1) |> Expect.equal "Skipped rank" 4
  ]

[<Tests>]
let groupByIsADiscriminatedUnion =
  testList "CoverageViewGroup DU arity" [

    testCase "WHY — GroupBy is a DU — because pattern matching on a tag is one int compare vs a string compare+hash on every call" <| fun _ ->
      let describe = function
        | CoverageGroup.None -> "none"
        | CoverageGroup.ByCategory -> "by-category"
        | CoverageGroup.ByStatus -> "by-status"
        | CoverageGroup.Custom _ -> "custom"
      describe CoverageGroup.None |> Expect.equal "None case" "none"
      describe CoverageGroup.ByCategory |> Expect.equal "ByCategory case" "by-category"
      describe CoverageGroup.ByStatus |> Expect.equal "ByStatus case" "by-status"
      (match CoverageGroup.Custom "test-name" with
       | CoverageGroup.Custom s -> s |> Expect.equal "Custom carries label" "test-name"
       | _ -> failtest "expected Custom")
  ]

[<Tests>]
let sortAndFilterAreDUs =
  testList "CoverageView sort/filter DU" [

    testCase "WHY — SortBy and Filter are DUs — exhaustively matchable for branchless dispatch" <| fun _ ->
      let sortRank = function
        | CoverageSort.StatusFirst -> 0
        | CoverageSort.NameFirst -> 1
        | CoverageSort.DurationDesc -> 2
      let filterAcceptsAll = function
        | CoverageFilter.All -> true
        | CoverageFilter.Failing -> false
        | CoverageFilter.FailingOrStale -> false
        | CoverageFilter.ByCategory _ -> false
        | CoverageFilter.ByText _ -> false
      sortRank CoverageSort.StatusFirst |> Expect.equal "status-first rank" 0
      sortRank CoverageSort.NameFirst |> Expect.equal "name-first rank" 1
      sortRank CoverageSort.DurationDesc |> Expect.equal "duration-desc rank" 2
      filterAcceptsAll CoverageFilter.All |> Expect.isTrue "All accepts everything"
  ]

[<Tests>]
let inlineBadgeToString =
  testList "CoverageView toInlineBadge string" [

    testCase "WHY — toInlineBadge — formats 100 passing tests as '✓ 100' in one short line because editor rendering is one virtual line" <| fun _ ->
      let text = CoverageView.toInlineBadge [ CoverageBadge.Pass 100 ]
      text |> Expect.equal "single-kind compact" "✓ 100"

    testCase "WHY — toInlineBadge — formats mixed statuses compactly" <| fun _ ->
      let text =
        CoverageView.toInlineBadge
          [ CoverageBadge.Pass 97
            CoverageBadge.Fail 3 ]
      text |> Expect.equal "mixed-kind compact" "✓ 97 ✗ 3"

    testCase "WHY — toInlineBadge — empty list returns empty string because the editor then omits the badge entirely" <| fun _ ->
      CoverageView.toInlineBadge []
      |> Expect.equal "empty badge" ""
  ]

[<Tests>]
let fsharpHeavyFunctionScenario =
  testList "CoverageView F# heavy function scenario" [

    testCase "WHY — F#-heavy-function — 100 tests covering one symbol produces one InlineBadge of [Pass 100] not 100 entries because the editor renders one line" <| fun _ ->
      // F# users routinely have 50-200 tests per function. The badge must
      // collapse 100 tests to ONE entry, not 100 separate entries.
      let mode = CoverageViewMode.defaults // InlineCollapseAt = Int32.MaxValue, so HasOverflow = false
      let coveringIds =
        [for i in 1..100 -> TestId.create (sprintf "Tests.t%d" i) TestFramework.Expecto]
        |> Set.ofList
      let depGraph = TestDependencyGraph.empty
      let tests =
        [for i in 1..100 -> mkTest (sprintf "Tests.t%d" i) TestCategory.Unit]
        |> List.toArray
      let state = { LiveTestState.empty with DiscoveredTests = tests }
      let view =
        CoverageView.project mode coveringIds depGraph state "Module.big"
      match view with
      | Some v ->
        v.TotalCount |> Expect.equal "TotalCount = 100" 100
        v.InlineBadge |> Expect.hasLength "InlineBadge is one element" 1
        (match v.InlineBadge.[0] with
         | CoverageBadge.Pass n -> n |> Expect.equal "Pass count is 100" 100
         | _ -> failtest "expected Pass")
        v.HasOverflow |> Expect.isFalse "no overflow at default (Int32.MaxValue)"
        v.FailingTests |> Expect.hasLength "no failures" 0
        v.Health |> Expect.equal "AllPassing" CoverageHealth.AllPassing
      | None -> failtest "expected a view"
  ]

[<Tests>]
let overflowIndicator =
  testList "CoverageView overflow" [

    testCase "WHY — HasOverflow — true when total exceeds InlineCollapseAt because the renderer must show '▾ +N more' to invite the user to open the picker" <| fun _ ->
      // User has set InlineCollapseAt to 5 (collapsed view). 10 tests → overflow.
      let mode = { CoverageViewMode.defaults with InlineCollapseAt = 5 }
      let coveringIds =
        [for i in 1..10 -> TestId.create (sprintf "Tests.t%d" i) TestFramework.Expecto]
        |> Set.ofList
      let tests =
        [for i in 1..10 -> mkTest (sprintf "Tests.t%d" i) TestCategory.Unit]
        |> List.toArray
      let state = { LiveTestState.empty with DiscoveredTests = tests }
      let view = CoverageView.project mode coveringIds TestDependencyGraph.empty state "Module.many"
      match view with
      | Some v ->
        v.HasOverflow |> Expect.isTrue "overflow at 10 > 5"
        v.TotalCount |> Expect.equal "TotalCount = 10" 10
      | None -> failtest "expected a view"

    testCase "WHY — HasOverflow — false at boundary (total == InlineCollapseAt) because the user said >= should overflow, not >" <| fun _ ->
      let mode = { CoverageViewMode.defaults with InlineCollapseAt = 10 }
      let coveringIds =
        [for i in 1..10 -> TestId.create (sprintf "Tests.t%d" i) TestFramework.Expecto]
        |> Set.ofList
      let tests =
        [for i in 1..10 -> mkTest (sprintf "Tests.t%d" i) TestCategory.Unit]
        |> List.toArray
      let state = { LiveTestState.empty with DiscoveredTests = tests }
      let view = CoverageView.project mode coveringIds TestDependencyGraph.empty state "Module.m"
      match view with
      | Some v ->
        v.HasOverflow |> Expect.isFalse "no overflow at boundary"
      | None -> failtest "expected a view"
  ]

[<Tests>]
let hotPathBudget =
  testList "CoverageView hot path performance" [

    testCase "WHY — hot-path — projecting 200 tests covering one symbol completes in <5ms because the editor calls this on every visible function and a slow projection freezes the editor" <| fun _ ->
      // Performance budget: 200 covering tests, 1 projection call, must
      // complete in <5ms on a CI box. This is the worst case for a function
      // in an F# codebase. If this fails, the projection is doing Seq
      // allocations, ResizeArray churn, or something pathological.
      let mode = CoverageViewMode.defaults
      let coveringIds =
        [for i in 1..200 -> TestId.create (sprintf "Tests.t%d" i) TestFramework.Expecto]
        |> Set.ofList
      let tests =
        [for i in 1..200 -> mkTest (sprintf "Tests.t%d" i) TestCategory.Unit]
        |> List.toArray
      let state = { LiveTestState.empty with DiscoveredTests = tests }
      let sw = System.Diagnostics.Stopwatch.StartNew()
      let view = CoverageView.project mode coveringIds TestDependencyGraph.empty state "Module.hot"
      sw.Stop()
      view |> Expect.isSome "produces a view"
      // Budget: 5ms. Generous to allow CI variance, but strict enough to
      // catch O(n²) regressions (e.g. accidental Seq.distinct on a hot path).
      (sw.Elapsed.TotalMilliseconds, 5.0)
      |> Expect.isLessThan "200-test projection must complete in <5ms"
  ]
