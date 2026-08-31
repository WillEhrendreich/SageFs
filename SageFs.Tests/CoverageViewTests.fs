module SageFs.Tests.CoverageViewTests

open System
open Expecto
open Expecto.Flip
open SageFs
open SageFs.Features.LiveTesting
open SageFs.Tests.LiveTestingTestHelpers

// ============================================================================
// CoverageView v2 - strict design contract
// ============================================================================
//
// Design principles (each test name has a WHY bullet):
//
// 1. NO Option in data modeling. CoverageView is a record; "absent" is
//    encoded by TotalCount = 0.
// 2. NO bool. HasOverflow becomes a DU: Overflow = Within | Overflow of
//    hidden:int.
// 3. NO mutable. Every function is a pure expression or a fold.
// 4. NO speculative fields. CoverageViewMode has only InlineCollapseAt.
// 5. Health is honest. CoverageViewState preserves the 5 status kinds.
// 6. ONE function. No project/projectForLine split. Returns CoverageView
//    directly. TotalCount = 0 means absent.
// 7. Hot path budget. 100 projections of 200 tests must complete in
//    <100ms (= <1ms per projection).

// --- Helpers ---

let private mkTest (name: string) (category: TestCategory) : TestCase =
  { Id = TestId.create name TestFramework.Expecto
    FullName = name
    DisplayName = name
    Origin = TestOrigin.SourceMapped ("Tests.fs", 10)
    Labels = []
    Framework = TestFramework.Expecto
    Category = category }

[<Tests>]
let v2Defaults =
  testList "CoverageView v2 - defaults" [

    testCase "WHY - defaults - InlineCollapseAt defaults to Int32.MaxValue because F# users have many tests per function and auto-collapse would punish their style" <| fun _ ->
      CoverageViewMode.defaults.InlineCollapseAt
      |> Expect.equal "F#-friendly default" Int32.MaxValue

    testCase "WHY - defaults - no other modes exist because the picker is a future concern and YAGNI" <| fun _ ->
      let fields = FSharp.Reflection.FSharpType.GetRecordFields(typeof<CoverageViewMode>)
                   |> Array.map (fun f -> f.Name)
                   |> Set.ofArray
      Set.contains "InlineCollapseAt" fields |> Expect.isTrue "InlineCollapseAt exists"
      (fields.Count, 1)
      |> Expect.isLessThanOrEqual "data model is minimal"
  ]

[<Tests>]
let v2HasOverflow =
  testList "CoverageView v2 - Overflow is a DU not a bool" [

    testCase "WHY - overflow - is a DU with Within | Overflow of hidden:int because the renderer needs the exact hidden count" <| fun _ ->
      let small = Overflow.Within
      let big = Overflow.Overflow 47
      let hiddenStr = function
        | Overflow.Within -> ""
        | Overflow.Overflow n -> sprintf "%c +%d" (char 0x2713) n
      (hiddenStr small, "")
      |> Expect.equal "within" ("", "")
      (hiddenStr big, sprintf "%c +47" (char 0x2713))
      |> Expect.equal "47 hidden" (sprintf "%c +47" (char 0x2713), sprintf "%c +47" (char 0x2713))
  ]

[<Tests>]
let v2ProjectIsTotal =
  testList "CoverageView v2 - single project function, no Option" [

    testCase "WHY - project - returns CoverageView directly (not Option) because absent is encoded by TotalCount = 0" <| fun _ ->
      let test = mkTest "Tests.t1" TestCategory.Unit
      let state = { LiveTestState.empty with DiscoveredTests = [| test |] }
      let view = CoverageView.project CoverageViewMode.defaults [||] TestDependencyGraph.empty state "Prod.fs" 999 "Module.x"
      (view.TotalCount, 0)
      |> Expect.equal "no covering tests" (0, 0)
      (view.InlineBadgeText, "")
      |> Expect.equal "no badge" ("", "")

    testCase "WHY - project - line with one passing test produces TotalCount=1 and InlineBadge=Pass 1" <| fun _ ->
      let test = mkTest "Tests.t1" TestCategory.Unit
      let state =
        { LiveTestState.empty with
            DiscoveredTests = [| test |]
            LastResults = Map.ofList [ test.Id, mkResult test.Id (TestResult.Passed (ts 1.0)) ] }
      let view = CoverageView.project CoverageViewMode.defaults [| test.Id |] TestDependencyGraph.empty state "Prod.fs" 10 "Module.x"
      (view.TotalCount, 1)
      |> Expect.equal "1" (1, 1)
      (view.InlineBadgeText, sprintf "%c 1" (char 0x2713))
      |> Expect.equal "Pass 1" (sprintf "%c 1" (char 0x2713), sprintf "%c 1" (char 0x2713))

    testCase "WHY - project - line with one failing test produces TotalCount=1 and InlineBadge=Fail 1" <| fun _ ->
      let test = mkTest "Tests.t1" TestCategory.Unit
      let state =
        { LiveTestState.empty with
            DiscoveredTests = [| test |]
            LastResults = Map.ofList [ test.Id, mkResult test.Id (TestResult.Failed (TestFailure.AssertionFailed "x", ts 1.0)) ] }
      let view = CoverageView.project CoverageViewMode.defaults [| test.Id |] TestDependencyGraph.empty state "Prod.fs" 10 "Module.x"
      (view.TotalCount, 1)
      |> Expect.equal "1" (1, 1)
      (view.InlineBadgeText, sprintf "%c 1" (char 0x2717))
      |> Expect.equal "Fail 1" (sprintf "%c 1" (char 0x2717), sprintf "%c 1" (char 0x2717))
  ]

[<Tests>]
let v2HealthIsHonest =
  testList "CoverageView v2 - Health is honest about test status" [

    testCase "WHY - health - Stale tests are reported as Stale, not AllPassing" <| fun _ ->
      let test = mkTest "Tests.t1" TestCategory.Unit
      let state =
        { LiveTestState.empty with
            DiscoveredTests = [| test |]
            LastResults = Map.ofList [ test.Id, { TestId = test.Id; TestName = "t1"; Result = TestResult.NotRun; Timestamp = DateTimeOffset.UtcNow; Output = None } ] }
      let view = CoverageView.project CoverageViewMode.defaults [| test.Id |] TestDependencyGraph.empty state "Prod.fs" 10 "Module.x"
      (match view.Health with
       | CoverageViewState.Passing -> failtest "NotRun must NOT collapse to Passing"
       | _ -> ())
  ]

[<Tests>]
let v2HotPath =
  testList "CoverageView v2 - hot path is tight" [

    testCase "WHY - hot-path - 100 projections of 200 tests must complete in <100ms (<1ms each)" <| fun _ ->
      let tests =
        [for i in 1..200 -> mkTest (sprintf "Tests.t%d" i) TestCategory.Unit]
        |> List.toArray
      let coveringIds = tests |> Array.map (fun t -> t.Id)
      let state = { LiveTestState.empty with DiscoveredTests = tests }
      for _ in 1..5 do
        CoverageView.project CoverageViewMode.defaults coveringIds TestDependencyGraph.empty state "Prod.fs" 10 "Module.x" |> ignore
      let sw = System.Diagnostics.Stopwatch.StartNew()
      for _ in 1..100 do
        CoverageView.project CoverageViewMode.defaults coveringIds TestDependencyGraph.empty state "Prod.fs" 10 "Module.x" |> ignore
      sw.Stop()
      (sw.Elapsed.TotalMilliseconds, 100.0)
      |> Expect.isLessThan "100 projections of 200 tests must complete in <100ms"
  ]

[<Tests>]
let v2InlineBadge =
  testList "CoverageView v2 - InlineBadgeText is computed once at projection" [

    testCase "WHY - inline - empty covering set produces empty string" <| fun _ ->
      let test = mkTest "Tests.t1" TestCategory.Unit
      let state = { LiveTestState.empty with DiscoveredTests = [| test |] }
      let view = CoverageView.project CoverageViewMode.defaults [||] TestDependencyGraph.empty state "Prod.fs" 10 "Module.x"
      (view.InlineBadgeText, "")
      |> Expect.equal "no badge" ("", "")

    testCase "WHY - inline - 100 Pass tests formats as one entry" <| fun _ ->
      let tests =
        [for i in 1..100 -> mkTest (sprintf "Tests.t%d" i) TestCategory.Unit]
        |> List.toArray
      let state =
        { LiveTestState.empty with
            DiscoveredTests = tests
            LastResults =
              tests
              |> Array.map (fun t -> t.Id, mkResult t.Id (TestResult.Passed (ts 1.0)))
              |> Map.ofArray }
      let view = CoverageView.project CoverageViewMode.defaults (tests |> Array.map (fun t -> t.Id)) TestDependencyGraph.empty state "Prod.fs" 10 "Module.x"
      (view.InlineBadgeText, sprintf "%c 100" (char 0x2713))
      |> Expect.equal "100 passes" (sprintf "%c 100" (char 0x2713), sprintf "%c 100" (char 0x2713))

    testCase "WHY - inline - mixed statuses fit on one line in stable order Pass Fail Stale Skipped" <| fun _ ->
      // 5 Pass, 2 Fail, 4 Skipped, 3 NotRun (Stale), 1 Skipped (running)
      // = 5 Pass, 2 Fail, 5 Skipped, 3 Stale
      let pCount, fCount, kCount, sCount, rCount = 5, 2, 4, 3, 1
      let mkTestPair (kind: string) (r: TestResult) =
        let t = mkTest ("Tests." + kind + "_" + string (abs (System.DateTime.UtcNow.Ticks.GetHashCode() + kind.GetHashCode()))) TestCategory.Unit
        t, r
      let pTests = [for _ in 1..pCount -> mkTestPair "p" (TestResult.Passed (ts 1.0))]
      let fTests = [for _ in 1..fCount -> mkTestPair "f" (TestResult.Failed (TestFailure.AssertionFailed "x", ts 1.0))]
      let kTests = [for _ in 1..kCount -> mkTestPair "k" (TestResult.Skipped "policy")]
      let sTests = [for _ in 1..sCount -> mkTestPair "s" TestResult.NotRun]
      let rTests = [for _ in 1..rCount -> mkTestPair "r" (TestResult.Skipped "running-policy")]
      let all = pTests @ fTests @ kTests @ sTests @ rTests
      let testArray = all |> List.map fst |> List.toArray
      let state =
        { LiveTestState.empty with
            DiscoveredTests = testArray
            LastResults =
              all
              |> List.map (fun (t, r) -> t.Id, mkResult t.Id r)
              |> Map.ofList }
      let view = CoverageView.project CoverageViewMode.defaults (testArray |> Array.map (fun t -> t.Id)) TestDependencyGraph.empty state "Prod.fs" 10 "Module.x"
      // Stable order: Pass(5), Fail(2), Stale(3), Skipped(5)
      let expected =
        sprintf "%c 5 %c 2 ~ 3 %c 5"
          (char 0x2713) (char 0x2717) (char 0x2298)
      (view.InlineBadgeText, expected)
      |> Expect.equal "stable order one line" (expected, expected)
  ]

[<Tests>]
let v2SetIsInternal =
  testList "CoverageView v2 - caller does not build Sets" [

    testCase "WHY - project - accepts TestId array directly, not Set" <| fun _ ->
      let test = mkTest "Tests.t1" TestCategory.Unit
      let state = { LiveTestState.empty with DiscoveredTests = [| test |] }
      let view = CoverageView.project CoverageViewMode.defaults [| test.Id |] TestDependencyGraph.empty state "Prod.fs" 10 "Module.x"
      (view.TotalCount, 1)
      |> Expect.equal "1 test" (1, 1)
  ]
