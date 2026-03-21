module SageFs.Tests.LiveTestingGraphTests

open System
open System.Reflection
open Expecto
open Expecto.Flip
open SageFs
open SageFs.Features.LiveTesting
open SageFs.Tests.LiveTestingTestHelpers
open SageFs.Measures

[<Tests>]
let affectedTestCycleTests = testList "affected-test cycle" [
  test "dep graph lookup finds affected tests" {
    let graph = {
      TestDependencyGraph.empty with
        SymbolToTests = Map.ofList [
          "MyModule.add", [| TestId.create "add-test" TestFramework.XUnit |]
          "MyModule.validate", [| TestId.create "validate-test" TestFramework.XUnit |]
        ]
        TransitiveCoverage = Map.ofList [
          "MyModule.add", [| TestId.create "add-test" TestFramework.XUnit |]
          "MyModule.validate", [| TestId.create "validate-test" TestFramework.XUnit |]
        ]
    }
    let affected = TestDependencyGraph.findAffected ["MyModule.add"] graph
    affected.Length |> Expect.equal "one affected test" 1
    affected.[0] |> Expect.equal "correct test" (TestId.create "add-test" TestFramework.XUnit)
  }
]

[<Tests>]
let policyFilterTests = testList "PolicyFilter" [
  test "OnEveryChange runs on all triggers" {
    PolicyFilter.shouldRun RunPolicy.OnEveryChange RunTrigger.Keystroke
    |> Expect.isTrue "keystroke"
    PolicyFilter.shouldRun RunPolicy.OnEveryChange RunTrigger.FileSave
    |> Expect.isTrue "save"
    PolicyFilter.shouldRun RunPolicy.OnEveryChange RunTrigger.ExplicitRun
    |> Expect.isTrue "explicit"
  }

  test "OnSaveOnly skips keystrokes" {
    PolicyFilter.shouldRun RunPolicy.OnSaveOnly RunTrigger.Keystroke
    |> Expect.isFalse "keystroke"
    PolicyFilter.shouldRun RunPolicy.OnSaveOnly RunTrigger.FileSave
    |> Expect.isTrue "save"
    PolicyFilter.shouldRun RunPolicy.OnSaveOnly RunTrigger.ExplicitRun
    |> Expect.isTrue "explicit"
  }

  test "OnDemand only on explicit" {
    PolicyFilter.shouldRun RunPolicy.OnDemand RunTrigger.Keystroke
    |> Expect.isFalse "keystroke"
    PolicyFilter.shouldRun RunPolicy.OnDemand RunTrigger.FileSave
    |> Expect.isFalse "save"
    PolicyFilter.shouldRun RunPolicy.OnDemand RunTrigger.ExplicitRun
    |> Expect.isTrue "explicit"
  }

  test "Disabled never runs" {
    PolicyFilter.shouldRun RunPolicy.Disabled RunTrigger.Keystroke
    |> Expect.isFalse "keystroke"
    PolicyFilter.shouldRun RunPolicy.Disabled RunTrigger.ExplicitRun
    |> Expect.isFalse "explicit"
  }

  test "filterTests respects category policies" {
    let policies = Map.ofList [
      TestCategory.Unit, RunPolicy.OnEveryChange
      TestCategory.Integration, RunPolicy.OnDemand
      TestCategory.Browser, RunPolicy.Disabled
    ]
    let tests = [|
      { Id = TestId.create "unit1" TestFramework.XUnit; FullName = "unit1"; DisplayName = "unit1"
        Origin = TestOrigin.ReflectionOnly; Labels = []; Framework = TestFramework.XUnit
        Category = TestCategory.Unit }
      { Id = TestId.create "int1" TestFramework.XUnit; FullName = "int1"; DisplayName = "int1"
        Origin = TestOrigin.ReflectionOnly; Labels = []; Framework = TestFramework.XUnit
        Category = TestCategory.Integration }
      { Id = TestId.create "browser1" TestFramework.XUnit; FullName = "browser1"; DisplayName = "browser1"
        Origin = TestOrigin.ReflectionOnly; Labels = []; Framework = TestFramework.XUnit
        Category = TestCategory.Browser }
    |]
    let filtered = PolicyFilter.filterTests policies RunTrigger.Keystroke tests
    filtered.Length |> Expect.equal "only unit on keystroke" 1
    filtered.[0].FullName |> Expect.equal "unit test" "unit1"
    let explicit = PolicyFilter.filterTests policies RunTrigger.ExplicitRun tests
    explicit.Length |> Expect.equal "unit + integration on explicit" 2
  }
]

// ============================================================
// Staleness Tests
// ============================================================

[<Tests>]
let prioritizationTests = testList "TestPrioritization" [
  test "failed tests come before passed tests" {
    let cases = [| mkTestCase "passed1" TestFramework.Expecto TestCategory.Unit; mkTestCase "failed1" TestFramework.Expecto TestCategory.Unit |]
    let results = Map.ofList [
      mkTestId "passed1" TestFramework.Expecto, mkResult (mkTestId "passed1" TestFramework.Expecto) (TestResult.Passed (ts 10.0))
      mkTestId "failed1" TestFramework.Expecto, mkResult (mkTestId "failed1" TestFramework.Expecto) (TestResult.Failed (TestFailure.AssertionFailed "bad", ts 10.0))
    ]
    let sorted = TestPrioritization.prioritize results cases
    sorted.[0].FullName |> Expect.equal "failed first" "failed1"
    sorted.[1].FullName |> Expect.equal "passed second" "passed1"
  }

  test "among passed, faster tests come first" {
    let cases = [| mkTestCase "slow" TestFramework.Expecto TestCategory.Unit; mkTestCase "fast" TestFramework.Expecto TestCategory.Unit |]
    let results = Map.ofList [
      mkTestId "slow" TestFramework.Expecto, mkResult (mkTestId "slow" TestFramework.Expecto) (TestResult.Passed (ts 500.0))
      mkTestId "fast" TestFramework.Expecto, mkResult (mkTestId "fast" TestFramework.Expecto) (TestResult.Passed (ts 5.0))
    ]
    let sorted = TestPrioritization.prioritize results cases
    sorted.[0].FullName |> Expect.equal "fast first" "fast"
    sorted.[1].FullName |> Expect.equal "slow second" "slow"
  }

  test "new tests (no results) run before passed tests" {
    let cases = [| mkTestCase "no.result" TestFramework.Expecto TestCategory.Unit; mkTestCase "has.result" TestFramework.Expecto TestCategory.Unit |]
    let results = Map.ofList [
      mkTestId "has.result" TestFramework.Expecto, mkResult (mkTestId "has.result" TestFramework.Expecto) (TestResult.Passed (ts 10.0))
    ]
    let sorted = TestPrioritization.prioritize results cases
    sorted.[0].FullName |> Expect.equal "new test first for faster feedback" "no.result"
    sorted.[1].FullName |> Expect.equal "passed test after" "has.result"
  }

  test "full priority: failed > new > passed > skipped > not-run" {
    let cases = [|
      mkTestCase "passed" TestFramework.Expecto TestCategory.Unit
      mkTestCase "unknown" TestFramework.Expecto TestCategory.Unit
      mkTestCase "failed" TestFramework.Expecto TestCategory.Unit
      mkTestCase "skipped" TestFramework.Expecto TestCategory.Unit
      mkTestCase "notrun" TestFramework.Expecto TestCategory.Unit
    |]
    let results = Map.ofList [
      mkTestId "passed" TestFramework.Expecto, mkResult (mkTestId "passed" TestFramework.Expecto) (TestResult.Passed (ts 10.0))
      mkTestId "failed" TestFramework.Expecto, mkResult (mkTestId "failed" TestFramework.Expecto) (TestResult.Failed (TestFailure.AssertionFailed "x", ts 10.0))
      mkTestId "skipped" TestFramework.Expecto, mkResult (mkTestId "skipped" TestFramework.Expecto) (TestResult.Skipped "reason")
      mkTestId "notrun" TestFramework.Expecto, mkResult (mkTestId "notrun" TestFramework.Expecto) TestResult.NotRun
    ]
    let sorted = TestPrioritization.prioritize results cases
    sorted |> Array.map (fun tc -> tc.FullName)
    |> Expect.equal "priority order" [| "failed"; "unknown"; "passed"; "skipped"; "notrun" |]
  }

  test "empty input returns empty" {
    TestPrioritization.prioritize Map.empty [||]
    |> Expect.isEmpty "no tests"
  }
]

[<Tests>]
let prioritizationPropertyTests = testList "TestPrioritization properties" [
  testProperty "result count is preserved" (fun (n: FsCheck.PositiveInt) ->
    let count = n.Get % 50 + 1
    let cases = Array.init count (fun i -> mkTestCase (sprintf "test%d" i) TestFramework.Expecto TestCategory.Unit)
    let sorted = TestPrioritization.prioritize Map.empty cases
    sorted.Length = cases.Length
  )

  testProperty "failed tests always precede passed tests in output" (fun (seed: int) ->
    let rng = System.Random(seed)
    let count = rng.Next(2, 20)
    let cases = Array.init count (fun i -> mkTestCase (sprintf "t%d" i) TestFramework.Expecto TestCategory.Unit)
    let results =
      cases
      |> Array.map (fun tc ->
        let r =
          if rng.Next(3) = 0
          then TestResult.Failed (TestFailure.AssertionFailed "x", ts (float (rng.Next(1, 1000))))
          else TestResult.Passed (ts (float (rng.Next(1, 1000))))
        tc.Id, mkResult tc.Id r)
      |> Map.ofArray
    let sorted = TestPrioritization.prioritize results cases
    let indexOfLastFailed =
      sorted |> Array.tryFindIndexBack (fun tc ->
        match Map.tryFind tc.Id results with
        | Some r -> match r.Result with TestResult.Failed _ -> true | _ -> false
        | None -> false)
    let indexOfFirstPassed =
      sorted |> Array.tryFindIndex (fun tc ->
        match Map.tryFind tc.Id results with
        | Some r -> match r.Result with TestResult.Passed _ -> true | _ -> false
        | None -> false)
    match indexOfLastFailed, indexOfFirstPassed with
    | Some lastFail, Some firstPass -> lastFail < firstPass
    | _ -> true
  )

  testProperty "among same-outcome, faster comes first" (fun (seed: int) ->
    let rng = System.Random(seed)
    let count = rng.Next(2, 20)
    let cases = Array.init count (fun i -> mkTestCase (sprintf "t%d" i) TestFramework.Expecto TestCategory.Unit)
    let results =
      cases
      |> Array.map (fun tc ->
        let dur = ts (float (rng.Next(1, 1000)))
        tc.Id, mkResult tc.Id (TestResult.Passed dur))
      |> Map.ofArray
    let sorted = TestPrioritization.prioritize results cases
    let durations =
      sorted |> Array.choose (fun tc ->
        match Map.tryFind tc.Id results with
        | Some r -> match r.Result with TestResult.Passed d -> Some d.TotalMilliseconds | _ -> None
        | None -> None)
    durations |> Array.pairwise |> Array.forall (fun (a, b) -> a <= b)
  )
]

// ============================================================
// Coverage-Weighted + Flaky-Demoted Prioritization Tests
// ============================================================

let mkFailedResult testId msg dur =
  mkResult testId (TestResult.Failed (TestFailure.AssertionFailed msg, dur))

let mkPassedResult testId dur =
  mkResult testId (TestResult.Passed dur)

[<Tests>]
let coverageWeightedTests = testList "TestPrioritization coverage-weighted" [
  test "tests covering changed file precede non-covering tests in same tier" {
    let tc1 = mkTestCase "covers-file" TestFramework.Expecto TestCategory.Unit
    let tc2 = mkTestCase "no-coverage" TestFramework.Expecto TestCategory.Unit
    let results = Map.ofList [
      tc1.Id, mkPassedResult tc1.Id (ts 100.0)
      tc2.Id, mkPassedResult tc2.Id (ts 50.0)
    ]
    let ctx : PrioritizationContext = {
      LastResults = results
      CoverageWeights = Map.ofList [ tc1.Id, 5 ]
      FlakyClassifications = Map.empty
    }
    let sorted = TestPrioritization.prioritizeWithContext ctx [| tc2; tc1 |]
    sorted.[0].FullName
    |> Expect.equal "covering test first despite being slower" "covers-file"
    sorted.[1].FullName
    |> Expect.equal "non-covering test second" "no-coverage"
  }

  test "higher coverage weight precedes lower within same tier" {
    let tc1 = mkTestCase "high-coverage" TestFramework.Expecto TestCategory.Unit
    let tc2 = mkTestCase "low-coverage" TestFramework.Expecto TestCategory.Unit
    let results = Map.ofList [
      tc1.Id, mkPassedResult tc1.Id (ts 100.0)
      tc2.Id, mkPassedResult tc2.Id (ts 50.0)
    ]
    let ctx : PrioritizationContext = {
      LastResults = results
      CoverageWeights = Map.ofList [ tc1.Id, 10; tc2.Id, 3 ]
      FlakyClassifications = Map.empty
    }
    let sorted = TestPrioritization.prioritizeWithContext ctx [| tc2; tc1 |]
    sorted.[0].FullName
    |> Expect.equal "higher coverage first" "high-coverage"
  }

  test "missing coverage data defaults to weight 0" {
    let tc1 = mkTestCase "has-coverage" TestFramework.Expecto TestCategory.Unit
    let tc2 = mkTestCase "no-coverage-data" TestFramework.Expecto TestCategory.Unit
    let results = Map.ofList [
      tc1.Id, mkPassedResult tc1.Id (ts 100.0)
      tc2.Id, mkPassedResult tc2.Id (ts 50.0)
    ]
    let ctx : PrioritizationContext = {
      LastResults = results
      CoverageWeights = Map.ofList [ tc1.Id, 5 ]
      FlakyClassifications = Map.empty
    }
    let sorted = TestPrioritization.prioritizeWithContext ctx [| tc2; tc1 |]
    sorted.[0].FullName
    |> Expect.equal "test with coverage weight runs first" "has-coverage"
  }

  test "coverage weight does not override tier — failed still before passed with coverage" {
    let tcFailed = mkTestCase "failed-no-cov" TestFramework.Expecto TestCategory.Unit
    let tcPassed = mkTestCase "passed-high-cov" TestFramework.Expecto TestCategory.Unit
    let results = Map.ofList [
      tcFailed.Id, mkFailedResult tcFailed.Id "bad" (ts 10.0)
      tcPassed.Id, mkPassedResult tcPassed.Id (ts 10.0)
    ]
    let ctx : PrioritizationContext = {
      LastResults = results
      CoverageWeights = Map.ofList [ tcPassed.Id, 100 ]
      FlakyClassifications = Map.empty
    }
    let sorted = TestPrioritization.prioritizeWithContext ctx [| tcPassed; tcFailed |]
    sorted.[0].FullName
    |> Expect.equal "failed test still first despite no coverage" "failed-no-cov"
  }

  test "empty coverage weights behaves like legacy prioritize" {
    let tc1 = mkTestCase "fast-pass" TestFramework.Expecto TestCategory.Unit
    let tc2 = mkTestCase "slow-pass" TestFramework.Expecto TestCategory.Unit
    let results = Map.ofList [
      tc1.Id, mkPassedResult tc1.Id (ts 5.0)
      tc2.Id, mkPassedResult tc2.Id (ts 500.0)
    ]
    let ctx : PrioritizationContext = {
      LastResults = results
      CoverageWeights = Map.empty
      FlakyClassifications = Map.empty
    }
    let sorted = TestPrioritization.prioritizeWithContext ctx [| tc2; tc1 |]
    sorted.[0].FullName |> Expect.equal "fast first" "fast-pass"
  }
]

[<Tests>]
let flakyDemotionTests = testList "TestPrioritization flaky demotion" [
  test "environmental flaky failure demoted below honest failure" {
    let tcHonest = mkTestCase "honest-fail" TestFramework.Expecto TestCategory.Unit
    let tcFlaky = mkTestCase "flaky-fail" TestFramework.Expecto TestCategory.Unit
    let results = Map.ofList [
      tcHonest.Id, mkFailedResult tcHonest.Id "real bug" (ts 10.0)
      tcFlaky.Id, mkFailedResult tcFlaky.Id "timing" (ts 10.0)
    ]
    let ctx : PrioritizationContext = {
      LastResults = results
      CoverageWeights = Map.empty
      FlakyClassifications = Map.ofList [
        tcFlaky.Id, FlakyClassification.Environmental 5
      ]
    }
    let sorted = TestPrioritization.prioritizeWithContext ctx [| tcFlaky; tcHonest |]
    sorted.[0].FullName
    |> Expect.equal "honest failure first" "honest-fail"
  }

  test "property counterexample NOT demoted — stays at tier 0" {
    let tcProp = mkTestCase "property-fail" TestFramework.Expecto TestCategory.Unit
    let tcPassed = mkTestCase "passed-test" TestFramework.Expecto TestCategory.Unit
    let results = Map.ofList [
      tcProp.Id, mkFailedResult tcProp.Id "FsCheck found counterexample" (ts 10.0)
      tcPassed.Id, mkPassedResult tcPassed.Id (ts 10.0)
    ]
    let ctx : PrioritizationContext = {
      LastResults = results
      CoverageWeights = Map.empty
      FlakyClassifications = Map.ofList [
        tcProp.Id, FlakyClassification.PropertyCounterexample "input=42"
      ]
    }
    let sorted = TestPrioritization.prioritizeWithContext ctx [| tcPassed; tcProp |]
    sorted.[0].FullName
    |> Expect.equal "property counterexample stays at highest priority" "property-fail"
  }

  test "flaky failure demoted to same tier as passed" {
    let tcFlaky = mkTestCase "flaky-env" TestFramework.Expecto TestCategory.Unit
    let tcPassed = mkTestCase "regular-pass" TestFramework.Expecto TestCategory.Unit
    let results = Map.ofList [
      tcFlaky.Id, mkFailedResult tcFlaky.Id "timeout" (ts 10.0)
      tcPassed.Id, mkPassedResult tcPassed.Id (ts 5.0)
    ]
    let ctx : PrioritizationContext = {
      LastResults = results
      CoverageWeights = Map.empty
      FlakyClassifications = Map.ofList [
        tcFlaky.Id, FlakyClassification.Environmental 3
      ]
    }
    let sorted = TestPrioritization.prioritizeWithContext ctx [| tcFlaky; tcPassed |]
    sorted.[0].FullName
    |> Expect.equal "faster passed test first (same tier after demotion)" "regular-pass"
  }

  test "non-flaky classification does not demote" {
    let tcStable = mkTestCase "stable-fail" TestFramework.Expecto TestCategory.Unit
    let tcPassed = mkTestCase "passed" TestFramework.Expecto TestCategory.Unit
    let results = Map.ofList [
      tcStable.Id, mkFailedResult tcStable.Id "real bug" (ts 10.0)
      tcPassed.Id, mkPassedResult tcPassed.Id (ts 10.0)
    ]
    let ctx : PrioritizationContext = {
      LastResults = results
      CoverageWeights = Map.empty
      FlakyClassifications = Map.ofList [
        tcStable.Id, FlakyClassification.Stable
      ]
    }
    let sorted = TestPrioritization.prioritizeWithContext ctx [| tcPassed; tcStable |]
    sorted.[0].FullName
    |> Expect.equal "stable failure not demoted" "stable-fail"
  }
]

[<Tests>]
let legacyCompatTests = testList "TestPrioritization legacy compat" [
  test "legacy prioritize gives same results as prioritizeWithContext with empty context" {
    let cases = [|
      mkTestCase "passed" TestFramework.Expecto TestCategory.Unit
      mkTestCase "unknown" TestFramework.Expecto TestCategory.Unit
      mkTestCase "failed" TestFramework.Expecto TestCategory.Unit
    |]
    let results = Map.ofList [
      (mkTestId "passed" TestFramework.Expecto), mkPassedResult (mkTestId "passed" TestFramework.Expecto) (ts 10.0)
      (mkTestId "failed" TestFramework.Expecto), mkFailedResult (mkTestId "failed" TestFramework.Expecto) "x" (ts 10.0)
    ]
    let legacy = TestPrioritization.prioritize results cases
    let ctx = PrioritizationContext.fromLastResults results
    let enhanced = TestPrioritization.prioritizeWithContext ctx cases
    let legacyOrder = legacy |> Array.map (fun tc -> tc.FullName)
    let enhancedOrder = enhanced |> Array.map (fun tc -> tc.FullName)
    enhancedOrder |> Expect.equal "same order as legacy" legacyOrder
  }
]

module CoverageWeightedPropertyTestsModule =
  open FsCheck
  open FsCheck.FSharp

  type LTResult = SageFs.Features.LiveTesting.TestResult

  [<Tests>]
  let coverageWeightedPropertyTests = testList "TestPrioritization coverage-weighted properties" [
    testProperty "coverage-weighted tests precede non-covering in same tier" <| fun () ->
      let g = gen {
        let! n = Gen.choose (2, 20)
        let cases = Array.init n (fun i -> mkTestCase (sprintf "t%d" i) TestFramework.Expecto TestCategory.Unit)
        let results =
          cases |> Array.mapi (fun idx tc ->
            tc.Id, mkPassedResult tc.Id (ts (float (idx + 1) * 10.0))
          ) |> Map.ofArray
        let! coveredIdx = Gen.choose (0, n - 1)
        let! weight = Gen.choose (1, 100)
        let weights = Map.ofList [ cases.[coveredIdx].Id, weight ]
        return cases, results, weights, coveredIdx
      }
      Prop.forAll (Arb.fromGen g) (fun (cases, results, weights, coveredIdx) ->
        let ctx : PrioritizationContext = {
          LastResults = results
          CoverageWeights = weights
          FlakyClassifications = Map.empty
        }
        let sorted = TestPrioritization.prioritizeWithContext ctx cases
        let coveredPos = sorted |> Array.findIndex (fun tc -> tc.Id = cases.[coveredIdx].Id)
        let uncoveredPositions =
          sorted |> Array.mapi (fun idx tc -> idx, tc)
          |> Array.filter (fun (_, tc) -> not (Map.containsKey tc.Id weights))
          |> Array.map fst
        match uncoveredPositions with
        | [||] -> true
        | positions ->
          let minUncoveredPos = Array.min positions
          coveredPos < minUncoveredPos
      )

    testProperty "flaky-environmental never at tier 0" <| fun () ->
      let g = gen {
        let! n = Gen.choose (1, 15)
        let cases = Array.init n (fun i -> mkTestCase (sprintf "t%d" i) TestFramework.Expecto TestCategory.Unit)
        let results =
          cases |> Array.map (fun tc ->
            tc.Id, mkFailedResult tc.Id "flaky" (ts 10.0)
          ) |> Map.ofArray
        let! envCount = Gen.choose (1, n)
        let envClassifications =
          cases.[0..envCount-1]
          |> Array.map (fun tc -> tc.Id, FlakyClassification.Environmental 3)
          |> Map.ofArray
        return cases, results, envClassifications
      }
      Prop.forAll (Arb.fromGen g) (fun (cases, results, flakyMap) ->
        let ctx : PrioritizationContext = {
          LastResults = results
          CoverageWeights = Map.empty
          FlakyClassifications = flakyMap
        }
        TestPrioritization.prioritizeWithContext ctx cases
        |> Array.forall (fun tc ->
          match Map.tryFind tc.Id flakyMap with
          | Some (FlakyClassification.Environmental _) ->
            match Map.tryFind tc.Id results with
            | Some runResult ->
              TestPrioritization.computeTier flakyMap tc.Id runResult.Result > 0
            | None -> true
          | _ -> true
        )
      )

    testProperty "result count preserved with coverage context" <| fun () ->
      let g = gen {
        let! n = Gen.choose (0, 50)
        let cases = Array.init n (fun i -> mkTestCase (sprintf "t%d" i) TestFramework.Expecto TestCategory.Unit)
        let! hasCoverage = Gen.elements [true; false]
        let weights =
          if hasCoverage then
            cases |> Array.choose (fun tc ->
              if abs (tc.FullName.GetHashCode()) % 2 = 0 then Some (tc.Id, abs (tc.FullName.GetHashCode() % 50) + 1)
              else None
            ) |> Map.ofArray
          else Map.empty
        let results =
          cases |> Array.map (fun tc ->
            tc.Id, mkPassedResult tc.Id (ts 10.0)
          ) |> Map.ofArray
        return cases, results, weights
      }
      Prop.forAll (Arb.fromGen g) (fun (cases, results, weights) ->
        let ctx : PrioritizationContext = {
          LastResults = results
          CoverageWeights = weights
          FlakyClassifications = Map.empty
        }
        let sorted = TestPrioritization.prioritizeWithContext ctx cases
        sorted.Length = cases.Length
      )
  ]

// Assembly Load Diagnostics Tests
// ============================================================

[<Tests>]
let symbolGraphTests = testList "SymbolGraphBuilder" [
  test "buildIndex maps production symbols to test functions via line-range heuristic" {
    let refs = [
      { SymbolFullName = "MyApp.Tests.test1"; UseKind = SymbolUseKind.Definition; UsedInTestId = None; FilePath = "F.fs"; Line = 1 }
      { SymbolFullName = "MyModule.parse"; UseKind = SymbolUseKind.Reference; UsedInTestId = None; FilePath = "F.fs"; Line = 5 }
      { SymbolFullName = "MyModule.format"; UseKind = SymbolUseKind.Reference; UsedInTestId = None; FilePath = "F.fs"; Line = 8 }
      { SymbolFullName = "MyApp.Tests.test2"; UseKind = SymbolUseKind.Definition; UsedInTestId = None; FilePath = "F.fs"; Line = 20 }
      { SymbolFullName = "MyModule.parse"; UseKind = SymbolUseKind.Reference; UsedInTestId = None; FilePath = "F.fs"; Line = 25 }
    ]
    let index = SymbolGraphBuilder.buildIndex ".Tests." (TestFramework.Unknown "fcs") refs
    index |> Map.find "MyModule.parse" |> Array.length |> Expect.equal "parse has 2 tests" 2
    index |> Map.find "MyModule.format" |> Array.length |> Expect.equal "format has 1 test" 1
  }

  test "buildIndex produces empty graph when no test definitions present" {
    let refs = [
      { SymbolFullName = "MyModule.helper"; UseKind = SymbolUseKind.Reference; UsedInTestId = None; FilePath = "F.fs"; Line = 5 }
    ]
    let index = SymbolGraphBuilder.buildIndex ".Tests." (TestFramework.Unknown "fcs") refs
    index |> Map.isEmpty |> Expect.isTrue "no test context means no entries"
  }

  test "buildIndex deduplicates test entries for same symbol" {
    let refs = [
      { SymbolFullName = "MyApp.Tests.test1"; UseKind = SymbolUseKind.Definition; UsedInTestId = None; FilePath = "F.fs"; Line = 1 }
      { SymbolFullName = "MyModule.parse"; UseKind = SymbolUseKind.Reference; UsedInTestId = None; FilePath = "F.fs"; Line = 5 }
      { SymbolFullName = "MyModule.parse"; UseKind = SymbolUseKind.Reference; UsedInTestId = None; FilePath = "F.fs"; Line = 8 }
    ]
    let index = SymbolGraphBuilder.buildIndex ".Tests." (TestFramework.Unknown "fcs") refs
    index |> Map.find "MyModule.parse" |> Array.length |> Expect.equal "deduped" 1
  }

  test "updateGraph merges new symbols into existing graph" {
    // Set up existing graph WITH PerFileIndex tracking (as produced by real updateGraph calls)
    let existingGraph =
      TestDependencyGraph.empty
      |> SymbolGraphBuilder.updateGraph ".Tests." (TestFramework.Unknown "fcs")
        [ { SymbolFullName = "MyApp.Tests.OldTest"; UseKind = SymbolUseKind.Definition; UsedInTestId = None; FilePath = "Old.fs"; Line = 1 }
          { SymbolFullName = "OldModule.fn"; UseKind = SymbolUseKind.Reference; UsedInTestId = None; FilePath = "Old.fs"; Line = 5 } ]
        "Old.fs"
    let newRefs = [
      { SymbolFullName = "MyApp.Tests.test2"; UseKind = SymbolUseKind.Definition; UsedInTestId = None; FilePath = "New.fs"; Line = 1 }
      { SymbolFullName = "NewModule.fn"; UseKind = SymbolUseKind.Reference; UsedInTestId = None; FilePath = "New.fs"; Line = 5 }
    ]
    let updated = SymbolGraphBuilder.updateGraph ".Tests." (TestFramework.Unknown "fcs") newRefs "New.fs" existingGraph
    updated.SymbolToTests |> Map.containsKey "OldModule.fn" |> Expect.isTrue "old preserved"
    updated.SymbolToTests |> Map.containsKey "NewModule.fn" |> Expect.isTrue "new added"
    updated.SourceVersion |> Expect.equal "version bumped" 2
  }

  test "updateGraph replaces same file's symbol entries on re-analysis" {
    // First analysis: FileA maps MyModule.fn → OldTest
    let graph1 =
      TestDependencyGraph.empty
      |> SymbolGraphBuilder.updateGraph ".Tests." (TestFramework.Unknown "fcs")
        [ { SymbolFullName = "MyApp.Tests.OldTest"; UseKind = SymbolUseKind.Definition; UsedInTestId = None; FilePath = "F.fs"; Line = 1 }
          { SymbolFullName = "MyModule.fn"; UseKind = SymbolUseKind.Reference; UsedInTestId = None; FilePath = "F.fs"; Line = 5 } ]
        "F.fs"
    // Re-analysis: FileA now maps MyModule.fn → test2 (different test)
    let newRefs = [
      { SymbolFullName = "MyApp.Tests.test2"; UseKind = SymbolUseKind.Definition; UsedInTestId = None; FilePath = "F.fs"; Line = 1 }
      { SymbolFullName = "MyModule.fn"; UseKind = SymbolUseKind.Reference; UsedInTestId = None; FilePath = "F.fs"; Line = 5 }
    ]
    let updated = SymbolGraphBuilder.updateGraph ".Tests." (TestFramework.Unknown "fcs") newRefs "F.fs" graph1
    let tests = updated.SymbolToTests |> Map.find "MyModule.fn"
    tests |> Array.length |> Expect.equal "replaced with new test" 1
  }

  test "empty refs produce empty index" {
    let index = SymbolGraphBuilder.buildIndex ".Tests." (TestFramework.Unknown "fcs") []
    index |> Map.isEmpty |> Expect.isTrue "empty"
  }
]

[<Tests>]
let symbolDiffTests = testList "SymbolDiff" [
  test "no changes returns empty" {
    let syms = Set.ofList ["A.fn"; "B.fn"]
    SymbolDiff.computeChanges syms syms |> SymbolChanges.isEmpty |> Expect.isTrue "no changes"
  }

  test "added symbol is detected" {
    let prev = Set.ofList ["A.fn"]
    let curr = Set.ofList ["A.fn"; "B.fn"]
    let sc = SymbolDiff.computeChanges prev curr
    sc.Added |> Expect.contains "B.fn added" "B.fn"
    sc.Removed |> Expect.isEmpty "nothing removed"
  }

  test "removed symbol is detected" {
    let prev = Set.ofList ["A.fn"; "B.fn"]
    let curr = Set.ofList ["A.fn"]
    let sc = SymbolDiff.computeChanges prev curr
    sc.Removed |> Expect.contains "B.fn removed" "B.fn"
    sc.Added |> Expect.isEmpty "nothing added"
  }

  test "both added and removed detected separately" {
    let prev = Set.ofList ["A.fn"; "B.fn"]
    let curr = Set.ofList ["A.fn"; "C.fn"]
    let sc = SymbolDiff.computeChanges prev curr
    sc.Added |> Expect.contains "C.fn added" "C.fn"
    sc.Removed |> Expect.contains "B.fn removed" "B.fn"
  }

  test "fromRefs computes diff from reference lists" {
    let t1 = TestId.create "t1" TestFramework.Expecto
    let prev = [
      { SymbolFullName = "A.fn"; UseKind = SymbolUseKind.Reference; UsedInTestId = Some t1; FilePath = "F.fs"; Line = 1 }
      { SymbolFullName = "B.fn"; UseKind = SymbolUseKind.Reference; UsedInTestId = Some t1; FilePath = "F.fs"; Line = 2 }
    ]
    let curr = [
      { SymbolFullName = "A.fn"; UseKind = SymbolUseKind.Reference; UsedInTestId = Some t1; FilePath = "F.fs"; Line = 1 }
      { SymbolFullName = "C.fn"; UseKind = SymbolUseKind.Reference; UsedInTestId = Some t1; FilePath = "F.fs"; Line = 3 }
    ]
    let sc = SymbolDiff.fromRefs prev curr
    sc.Added |> Expect.hasLength "one added" 1
    sc.Removed |> Expect.hasLength "one removed" 1
  }

  test "allChanged combines both lists" {
    let prev = Set.ofList ["A.fn"; "B.fn"]
    let curr = Set.ofList ["A.fn"; "C.fn"]
    let sc = SymbolDiff.computeChanges prev curr
    sc |> SymbolChanges.allChanged |> Expect.hasLength "two total" 2
  }

  test "empty previous means all current are added" {
    let curr = Set.ofList ["A.fn"; "B.fn"]
    let sc = SymbolDiff.computeChanges Set.empty curr
    sc.Added |> Expect.hasLength "all new" 2
    sc.Removed |> Expect.isEmpty "nothing removed"
  }
]

[<Tests>]
let fileAnalysisCacheTests = testList "FileAnalysisCache" [
  test "empty cache returns all symbols as added" {
    let t1 = TestId.create "t1" TestFramework.Expecto
    let refs = [
      { SymbolFullName = "A.fn"; UseKind = SymbolUseKind.Reference; UsedInTestId = Some t1; FilePath = "F.fs"; Line = 1 }
    ]
    let changes, _ = FileAnalysisCache.empty |> FileAnalysisCache.update "F.fs" refs
    changes.Added |> Expect.hasLength "one added" 1
    changes.Removed |> Expect.isEmpty "nothing removed"
  }

  test "second update with same symbols returns no changes" {
    let t1 = TestId.create "t1" TestFramework.Expecto
    let refs = [
      { SymbolFullName = "A.fn"; UseKind = SymbolUseKind.Reference; UsedInTestId = Some t1; FilePath = "F.fs"; Line = 1 }
    ]
    let _, cache1 = FileAnalysisCache.empty |> FileAnalysisCache.update "F.fs" refs
    let changes, _ = cache1 |> FileAnalysisCache.update "F.fs" refs
    changes |> SymbolChanges.isEmpty |> Expect.isTrue "no changes"
  }

  test "different file doesn't affect existing file's cache" {
    let t1 = TestId.create "t1" TestFramework.Expecto
    let refs1 = [{ SymbolFullName = "A.fn"; UseKind = SymbolUseKind.Reference; UsedInTestId = Some t1; FilePath = "F1.fs"; Line = 1 }]
    let refs2 = [{ SymbolFullName = "B.fn"; UseKind = SymbolUseKind.Reference; UsedInTestId = Some t1; FilePath = "F2.fs"; Line = 1 }]
    let _, cache1 = FileAnalysisCache.empty |> FileAnalysisCache.update "F1.fs" refs1
    let _, cache2 = cache1 |> FileAnalysisCache.update "F2.fs" refs2
    cache2 |> FileAnalysisCache.getFileSymbols "F1.fs" |> Expect.hasLength "F1 preserved" 1
    cache2 |> FileAnalysisCache.getFileSymbols "F2.fs" |> Expect.hasLength "F2 added" 1
  }

  test "modified file separates added and removed" {
    let t1 = TestId.create "t1" TestFramework.Expecto
    let refs1 = [
      { SymbolFullName = "A.fn"; UseKind = SymbolUseKind.Reference; UsedInTestId = Some t1; FilePath = "F.fs"; Line = 1 }
      { SymbolFullName = "B.fn"; UseKind = SymbolUseKind.Reference; UsedInTestId = Some t1; FilePath = "F.fs"; Line = 2 }
    ]
    let refs2 = [
      { SymbolFullName = "A.fn"; UseKind = SymbolUseKind.Reference; UsedInTestId = Some t1; FilePath = "F.fs"; Line = 1 }
      { SymbolFullName = "C.fn"; UseKind = SymbolUseKind.Reference; UsedInTestId = Some t1; FilePath = "F.fs"; Line = 3 }
    ]
    let _, cache1 = FileAnalysisCache.empty |> FileAnalysisCache.update "F.fs" refs1
    let changes, _ = cache1 |> FileAnalysisCache.update "F.fs" refs2
    changes.Added |> Expect.contains "C added" "C.fn"
    changes.Removed |> Expect.contains "B removed" "B.fn"
  }

  test "getFileSymbols returns empty for unknown file" {
    FileAnalysisCache.empty |> FileAnalysisCache.getFileSymbols "unknown.fs"
    |> Expect.isEmpty "empty for unknown"
  }
]

[<Tests>]
let compositionTests = testList "compositionTests" [
  test "no-op edit with same symbols produces no affected tests" {
    let refs = [
      { SymbolFullName = "MyApp.Tests.test1"; UseKind = SymbolUseKind.Definition; UsedInTestId = None; FilePath = "Lib.fs"; Line = 1 }
      { SymbolFullName = "Lib.add"; UseKind = SymbolUseKind.Reference; UsedInTestId = None; FilePath = "Lib.fs"; Line = 5 }
      { SymbolFullName = "Lib.sub"; UseKind = SymbolUseKind.Reference; UsedInTestId = None; FilePath = "Lib.fs"; Line = 8 }
    ]
    let _, cache1 = FileAnalysisCache.empty |> FileAnalysisCache.update "Lib.fs" refs
    let changes, _ = cache1 |> FileAnalysisCache.update "Lib.fs" refs
    changes |> SymbolChanges.isEmpty |> Expect.isTrue "no symbol changes"
    let depGraph = {
      TestDependencyGraph.empty with
        SymbolToTests = SymbolGraphBuilder.buildIndex ".Tests." (TestFramework.Unknown "fcs") refs
        TransitiveCoverage = SymbolGraphBuilder.buildIndex ".Tests." (TestFramework.Unknown "fcs") refs
    }
    let affected = TestDependencyGraph.findAffected (SymbolChanges.allChanged changes) depGraph
    affected |> Expect.hasLength "no affected tests" 0
  }

  test "full cycle roundtrip: keystroke → debounce → FCS → affected tests" {
    let tc = mkTestCase "MyApp.Tests.test1" TestFramework.Expecto TestCategory.Unit
    let t0 = DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero)
    let refs = [
      { SymbolFullName = "MyApp.Tests.test1"; UseKind = SymbolUseKind.Definition; UsedInTestId = None; FilePath = "Lib.fs"; Line = 1 }
      { SymbolFullName = "Lib.add"; UseKind = SymbolUseKind.Reference; UsedInTestId = None; FilePath = "Lib.fs"; Line = 5 }
    ]
    let state = {
      LiveTestCycleState.empty with
        TestState = { LiveTestState.empty with DiscoveredTests = [|tc|]; Activation = LiveTestingActivation.Active }
    }
    let s1 = state |> LiveTestCycleState.onKeystroke "let x = 1" "File.fs" t0
    let effects30, s30 = s1 |> LiveTestCycleState.tick (t0.AddMilliseconds(30.0))
    effects30 |> Expect.isEmpty "nothing at 30ms"
    let effects51, s51 = s30 |> LiveTestCycleState.tick (t0.AddMilliseconds(51.0))
    effects51 |> List.exists (fun e -> match e with TestCycleEffect.ParseTreeSitter _ -> true | _ -> false)
    |> Expect.isTrue "TS fires at 51ms"
    let effects301, s301 = s51 |> LiveTestCycleState.tick (t0.AddMilliseconds(301.0))
    effects301 |> List.exists (fun e -> match e with TestCycleEffect.RequestFcsTypeCheck _ -> true | _ -> false)
    |> Expect.isTrue "FCS request fires at 301ms"
    // Phase 2: FCS completes → handleFcsResult → RequestRebuild (for .fs files)
    let fcsResult = FcsTypeCheckResult.Success ("File.fs", refs)
    let fcsEffects, _ = LiveTestCycleState.handleFcsResult fcsResult s301
    fcsEffects |> List.exists (fun e ->
      match e with
      | TestCycleEffect.RequestRebuild _ -> true
      | TestCycleEffect.RunAffectedTests _ -> true
      | _ -> false)
    |> Expect.isTrue "affected tests triggered after FCS"
  }

  test "burst typing coalesces debounce to single tree-sitter parse" {
    let t0 = DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero)
    let s0 = LiveTestCycleState.empty
    let s1 = s0 |> LiveTestCycleState.onKeystroke "l" "F.fs" t0
    let s2 = s1 |> LiveTestCycleState.onKeystroke "le" "F.fs" (t0.AddMilliseconds(20.0))
    let s3 = s2 |> LiveTestCycleState.onKeystroke "let" "F.fs" (t0.AddMilliseconds(40.0))
    let s4 = s3 |> LiveTestCycleState.onKeystroke "let " "F.fs" (t0.AddMilliseconds(60.0))
    let s5 = s4 |> LiveTestCycleState.onKeystroke "let x" "F.fs" (t0.AddMilliseconds(80.0))
    let effects100, s100 = s5 |> LiveTestCycleState.tick (t0.AddMilliseconds(100.0))
    effects100 |> Expect.isEmpty "nothing at 100ms (20ms after last keystroke)"
    let effects131, _ = s100 |> LiveTestCycleState.tick (t0.AddMilliseconds(131.0))
    let tsCount =
      effects131
      |> List.filter (fun e -> match e with TestCycleEffect.ParseTreeSitter _ -> true | _ -> false)
      |> List.length
    tsCount |> Expect.equal "exactly one TS parse" 1
    s5.ActiveFile |> Expect.equal "active file is F.fs" (Some "F.fs")
  }

  test "FCS cancel increases delay, successes reset it" {
    let ad0 = AdaptiveDebounce.createDefault ()
    ad0.CurrentFcsDelayMs |> Expect.equal "initial delay is 300" 300.0<ms>
    let ad1 = ad0 |> AdaptiveDebounce.onFcsCanceled
    ad1.CurrentFcsDelayMs |> Expect.equal "after 1 cancel: 450" 450.0<ms>
    ad1.ConsecutiveFcsCancels |> Expect.equal "1 cancel" 1
    let ad2 = ad1 |> AdaptiveDebounce.onFcsCanceled
    ad2.CurrentFcsDelayMs |> Expect.equal "after 2 cancels: 675" 675.0<ms>
    let ad3 = ad2 |> AdaptiveDebounce.onFcsCompleted
    ad3.ConsecutiveFcsSuccesses |> Expect.equal "1 success" 1
    ad3.CurrentFcsDelayMs |> Expect.equal "delay stays at 675" 675.0<ms>
    let ad4 = ad3 |> AdaptiveDebounce.onFcsCompleted
    let ad5 = ad4 |> AdaptiveDebounce.onFcsCompleted
    ad5.CurrentFcsDelayMs |> Expect.equal "reset to 300" 300.0<ms>
    ad5.ConsecutiveFcsCancels |> Expect.equal "cancels reset" 0
    ad5.ConsecutiveFcsSuccesses |> Expect.equal "successes reset" 0
  }

  test "symbol rename: cache detects removal+addition, graph finds affected" {
    let refsV1 = [
      { SymbolFullName = "MyApp.Tests.test1"; UseKind = SymbolUseKind.Definition; UsedInTestId = None; FilePath = "Lib.fs"; Line = 1 }
      { SymbolFullName = "Lib.add"; UseKind = SymbolUseKind.Reference; UsedInTestId = None; FilePath = "Lib.fs"; Line = 5 }
      { SymbolFullName = "Lib.oldFn"; UseKind = SymbolUseKind.Reference; UsedInTestId = None; FilePath = "Lib.fs"; Line = 8 }
    ]
    let refsV2 = [
      { SymbolFullName = "MyApp.Tests.test1"; UseKind = SymbolUseKind.Definition; UsedInTestId = None; FilePath = "Lib.fs"; Line = 1 }
      { SymbolFullName = "Lib.add"; UseKind = SymbolUseKind.Reference; UsedInTestId = None; FilePath = "Lib.fs"; Line = 5 }
      { SymbolFullName = "Lib.newFn"; UseKind = SymbolUseKind.Reference; UsedInTestId = None; FilePath = "Lib.fs"; Line = 8 }
    ]
    let _, cache1 = FileAnalysisCache.empty |> FileAnalysisCache.update "Lib.fs" refsV1
    let graph1 = TestDependencyGraph.empty |> SymbolGraphBuilder.updateGraph ".Tests." (TestFramework.Unknown "fcs") refsV1 "Lib.fs"
    let changes, _ = cache1 |> FileAnalysisCache.update "Lib.fs" refsV2
    changes.Added |> Expect.contains "newFn added" "Lib.newFn"
    changes.Removed |> Expect.contains "oldFn removed" "Lib.oldFn"
    let graph2 = graph1 |> SymbolGraphBuilder.updateGraph ".Tests." (TestFramework.Unknown "fcs") refsV2 "Lib.fs"
    let affectedByRemoved = TestDependencyGraph.findAffected changes.Removed graph1
    affectedByRemoved |> Expect.hasLength "t2 affected by removal" 1
    let affectedByAdded = TestDependencyGraph.findAffected changes.Added graph2
    affectedByAdded |> Expect.hasLength "t2 affected by addition" 1
  }

  test "policy filters affected tests by trigger type" {
    let unitTC = mkTestCase "UnitTest.test1" TestFramework.Expecto TestCategory.Unit
    let integTC = mkTestCase "IntegTest.test1" TestFramework.Expecto TestCategory.Integration
    let browserTC = mkTestCase "BrowserTest.test1" TestFramework.Expecto TestCategory.Browser
    let allTests = [|unitTC; integTC; browserTC|]
    let policies = RunPolicyDefaults.defaults
    let filtered = LiveTesting.filterByPolicy policies RunTrigger.Keystroke allTests
    filtered |> Array.length |> Expect.equal "only unit on keystroke" 1
    filtered.[0].Category |> Expect.equal "unit category" TestCategory.Unit
    let filteredExplicit = LiveTesting.filterByPolicy policies RunTrigger.ExplicitRun allTests
    filteredExplicit |> Array.length |> Expect.equal "all on explicit" 3
  }

  test "symbol change marks affected test results as stale" {
    let tc1 = mkTestCase "MyTest.test1" TestFramework.Expecto TestCategory.Unit
    let tc2 = mkTestCase "MyTest.test2" TestFramework.Expecto TestCategory.Unit
    let now = DateTimeOffset.UtcNow
    let passedResult = {
      TestId = tc1.Id
      TestName = tc1.FullName
      Result = TestResult.Passed(TimeSpan.FromMilliseconds(5.0))
      Timestamp = now
      Output = None
    }
    let state = {
      LiveTestState.empty with
        DiscoveredTests = [|tc1; tc2|]
        LastResults = Map.ofList [tc1.Id, passedResult]
        Activation = LiveTestingActivation.Active
    }
    let depGraph = {
      TestDependencyGraph.empty with
        SymbolToTests = Map.ofList ["Lib.add", [|tc1.Id|]]
        TransitiveCoverage = Map.ofList ["Lib.add", [|tc1.Id|]]
    }
    let stalified = Staleness.markStale depGraph ["Lib.add"] state
    stalified.AffectedTests |> Set.contains tc1.Id |> Expect.isTrue "tc1 is affected"
    stalified.AffectedTests |> Set.contains tc2.Id |> Expect.isFalse "tc2 not affected"
    match stalified.LastResults |> Map.tryFind tc1.Id with
    | Some r -> r.Result |> Expect.equal "tc1 result preserved as Passed" (TestResult.Passed(TimeSpan.FromMilliseconds(5.0)))
    | None -> failtest "tc1 result should still exist"
    let entry = stalified.StatusEntries |> Array.find (fun e -> e.TestId = tc1.Id)
    match entry.Status with
    | TestRunStatus.Stale -> ()
    | other -> failtestf "expected Stale status but got %A" other
  }

  test "file switch mid-cycle resets debounce timers" {
    let t0 = DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero)
    let s0 = LiveTestCycleState.empty
    let s1 = s0 |> LiveTestCycleState.onKeystroke "let x = 1" "File1.fs" t0
    s1.ActiveFile |> Expect.equal "active is File1" (Some "File1.fs")
    let s2 = s1 |> LiveTestCycleState.onKeystroke "let y = 2" "File2.fs" (t0.AddMilliseconds(30.0))
    s2.ActiveFile |> Expect.equal "active is File2" (Some "File2.fs")
    let effects51, s51 = s2 |> LiveTestCycleState.tick (t0.AddMilliseconds(51.0))
    effects51 |> Expect.isEmpty "no TS at 51ms (file switched)"
    let effects81, _ = s51 |> LiveTestCycleState.tick (t0.AddMilliseconds(81.0))
    let hasTS =
      effects81
      |> List.exists (fun e -> match e with TestCycleEffect.ParseTreeSitter _ -> true | _ -> false)
    hasTS |> Expect.isTrue "TS fires for File2 at 81ms"
  }

  test "FCS completes after new keystroke: debounce restarts" {
    let t0 = DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero)
    let s0 = LiveTestCycleState.empty
    let s1 = s0 |> LiveTestCycleState.onKeystroke "let x = 1" "File.fs" t0
    let effects1, s2 = s1 |> LiveTestCycleState.tick (t0.AddMilliseconds(51.0))
    let hasTS = effects1 |> List.exists (fun e -> match e with TestCycleEffect.ParseTreeSitter _ -> true | _ -> false)
    hasTS |> Expect.isTrue "TS fires after first keystroke"
    let s3 = s2 |> LiveTestCycleState.onKeystroke "let x = 2" "File.fs" (t0.AddMilliseconds(100.0))
    let effects2, s4 = s3 |> LiveTestCycleState.tick (t0.AddMilliseconds(352.0))
    let hasFCS = effects2 |> List.exists (fun e -> match e with TestCycleEffect.RequestFcsTypeCheck _ -> true | _ -> false)
    hasFCS |> Expect.isFalse "FCS should NOT fire - debounce restarted by keystroke2"
    let effects3, _ = s4 |> LiveTestCycleState.tick (t0.AddMilliseconds(401.0))
    let hasFCS2 = effects3 |> List.exists (fun e -> match e with TestCycleEffect.RequestFcsTypeCheck _ -> true | _ -> false)
    hasFCS2 |> Expect.isTrue "FCS fires after new debounce window"
  }

  test "cold start with empty cache: first keystroke produces TS parse" {
    let t0 = DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero)
    let s0 = LiveTestCycleState.empty
    let s1 = s0 |> LiveTestCycleState.onKeystroke "let x = 1" "File.fs" t0
    s1.AnalysisCache.FileSymbols |> Expect.isEmpty "cache should be empty on first keystroke"
    let effects, _ = s1 |> LiveTestCycleState.tick (t0.AddMilliseconds(51.0))
    let hasTS = effects |> List.exists (fun e -> match e with TestCycleEffect.ParseTreeSitter _ -> true | _ -> false)
    hasTS |> Expect.isTrue "TS fires on first keystroke (cold start)"
  }

  test "session dispose mid-cycle: state resets cleanly" {
    let t0 = DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero)
    let s0 = LiveTestCycleState.empty
    let s1 = s0 |> LiveTestCycleState.onKeystroke "let x = 1" "File.fs" t0
    let _, _ = s1 |> LiveTestCycleState.tick (t0.AddMilliseconds(51.0))
    let s3 = LiveTestCycleState.empty
    s3.ActiveFile |> Expect.isNone "no active file after reset"
    s3.AnalysisCache.FileSymbols |> Expect.isEmpty "no cache after reset"
    let s4 = s3 |> LiveTestCycleState.onKeystroke "let y = 1" "File2.fs" (t0.AddMilliseconds(200.0))
    s4.ActiveFile |> Expect.equal "new file" (Some "File2.fs")
    let effects, _ = s4 |> LiveTestCycleState.tick (t0.AddMilliseconds(251.0))
    let hasTS = effects |> List.exists (fun e -> match e with TestCycleEffect.ParseTreeSitter _ -> true | _ -> false)
    hasTS |> Expect.isTrue "TS fires after session reset"
  }
]

// --- Symbol graph wiring integration tests ---

[<Tests>]
let symbolGraphWiringTests = testList "symbol graph wiring integration" [
  test "afterTypeCheck with affected symbol produces RunAffectedTests effect" {
    let tid = TestId.create "Tests.affected_by_add" TestFramework.Expecto
    let testCase = {
      Id = tid; FullName = "Tests.affected_by_add"; DisplayName = "affected_by_add"
      Origin = TestOrigin.ReflectionOnly; Framework = TestFramework.Expecto
      Category = TestCategory.Unit; Labels = [] }
    let graph = {
      TestDependencyGraph.empty with
        SymbolToTests = Map.ofList [ "MyModule.add", [| tid |] ]
        TransitiveCoverage = Map.ofList [ "MyModule.add", [| tid |] ] }
    let ltState = {
      LiveTestState.empty with
        Activation = LiveTestingActivation.Active
        DiscoveredTests = [| testCase |]
        RunPolicies = RunPolicyDefaults.defaults }
    let effect =
      TestCycleEffects.afterTypeCheck
        [ "MyModule.add" ] "test.fs" RunTrigger.Keystroke graph ltState None Map.empty
    match effect with
    | [ TestCycleEffect.RequestRebuild (_, tests, _, _, _, _, _) ] ->
      tests |> Array.exists (fun t -> t.Id = tid)
      |> Expect.isTrue "should contain affected test"
    | other -> failtestf "expected single RequestRebuild for .fs file, got %A" other
  }

  test "handleFcsResult updates dep graph via onFcsComplete" {
    let tid = TestId.create "Tests.t1" TestFramework.Expecto
    let testCase = {
      Id = tid; FullName = "Tests.t1"; DisplayName = "t1"
      Origin = TestOrigin.ReflectionOnly; Framework = TestFramework.Expecto
      Category = TestCategory.Unit; Labels = [] }
    let pipeState = {
      LiveTestCycleState.empty with
        TestState = { LiveTestState.empty with
                        Activation = LiveTestingActivation.Active
                        DiscoveredTests = [| testCase |]
                        RunPolicies = RunPolicyDefaults.defaults } }
    let refs = [
      { SymbolFullName = "MyModule.add"; UseKind = SymbolUseKind.Reference
        UsedInTestId = Some tid; FilePath = "test.fs"; Line = 5 } ]
    let fcsResult = FcsTypeCheckResult.Success ("test.fs", refs)
    let _effects, updated = LiveTestCycleState.handleFcsResult fcsResult pipeState
    updated.DepGraph.SourceVersion
    |> fun v -> Expect.isGreaterThan "should increment version" (v, pipeState.DepGraph.SourceVersion)
  }

  test "triggerExecutionForAffected fallback path" {
    let tid = TestId.create "Tests.fallback" TestFramework.Expecto
    let testCase = {
      Id = tid; FullName = "Tests.fallback"; DisplayName = "fallback"
      Origin = TestOrigin.ReflectionOnly; Framework = TestFramework.Expecto
      Category = TestCategory.Unit; Labels = [] }
    let pipeState = {
      LiveTestCycleState.empty with
        TestState = { LiveTestState.empty with
                        Activation = LiveTestingActivation.Active
                        DiscoveredTests = [| testCase |]
                        RunPolicies = RunPolicyDefaults.defaults } }
    let effects =
      LiveTestCycleState.triggerExecutionForAffected
        [| tid |] RunTrigger.FileSave None pipeState
    effects
    |> List.exists (fun e ->
      match e with
      | TestCycleEffect.RunAffectedTests (tests, _, _, _, _, _) ->
        tests |> Array.exists (fun t -> t.Id = tid)
      | _ -> false)
    |> Expect.isTrue "should produce RunAffectedTests via fallback"
  }

  test "no effects when testing disabled" {
    let tid = TestId.create "Tests.disabled" TestFramework.Expecto
    let testCase = {
      Id = tid; FullName = "Tests.disabled"; DisplayName = "disabled"
      Origin = TestOrigin.ReflectionOnly; Framework = TestFramework.Expecto
      Category = TestCategory.Unit; Labels = [] }
    let graph = {
      TestDependencyGraph.empty with
        SymbolToTests = Map.ofList [ "M.func", [| tid |] ] }
    let ltState = {
      LiveTestState.empty with
        Activation = LiveTestingActivation.Inactive
        DiscoveredTests = [| testCase |]
        RunPolicies = RunPolicyDefaults.defaults }
    TestCycleEffects.afterTypeCheck [ "M.func" ] "test.fs" RunTrigger.Keystroke graph ltState None Map.empty
    |> Expect.isEmpty "no effect when disabled"
  }

  test "no effects when no symbols changed" {
    let tid = TestId.create "Tests.no_change" TestFramework.Expecto
    let testCase = {
      Id = tid; FullName = "Tests.no_change"; DisplayName = "no_change"
      Origin = TestOrigin.ReflectionOnly; Framework = TestFramework.Expecto
      Category = TestCategory.Unit; Labels = [] }
    let graph = {
      TestDependencyGraph.empty with
        SymbolToTests = Map.ofList [ "M.func", [| tid |] ] }
    let ltState = {
      LiveTestState.empty with
        Activation = LiveTestingActivation.Active
        DiscoveredTests = [| testCase |]
        RunPolicies = RunPolicyDefaults.defaults }
    TestCycleEffects.afterTypeCheck [] "test.fs" RunTrigger.Keystroke graph ltState None Map.empty
    |> Expect.isEmpty "no effect when no symbols"
  }
]

// --- Optimistic gutter transition tests ---

[<Tests>]
let optimisticGutterTests = testList "optimistic gutter transitions" [
  test "TestRunStarted marks affected tests as Running via status entries" {
    let tid = TestId.create "Tests.optimistic_run" TestFramework.Expecto
    let tests = [|
      { Id = tid; FullName = "Tests.optimistic_run"; DisplayName = "optimistic_run"
        Origin = TestOrigin.SourceMapped ("editor", 5)
        Labels = []; Framework = TestFramework.Expecto; Category = TestCategory.Unit }
    |]
    let model0 = (SageFsModel.initial())
    let model1 = { model0 with
                    LiveTesting = { model0.LiveTesting with
                                      TestState = { model0.LiveTesting.TestState with
                                                      Activation = LiveTestingActivation.Active
                                                      DiscoveredTests = tests } } }
    let model2, _ = SageFsUpdate.update (SageFsMsg.Event (SageFsEvent.TestRunStarted ([| tid |], Some "s"))) model1
    let entry =
      model2.LiveTesting.TestState.StatusEntries
      |> Array.tryFind (fun e -> e.TestId = tid)
    match entry with
    | Some e -> e.Status |> Expect.equal "should be Running" TestRunStatus.Running
    | None -> failtest "expected status entry for affected test"
  }

  test "non-affected tests stay Detected while others run" {
    let tidA = TestId.create "Tests.affected" TestFramework.Expecto
    let tidB = TestId.create "Tests.unaffected" TestFramework.Expecto
    let tests = [|
      { Id = tidA; FullName = "Tests.affected"; DisplayName = "affected"
        Origin = TestOrigin.SourceMapped ("editor", 5)
        Labels = []; Framework = TestFramework.Expecto; Category = TestCategory.Unit }
      { Id = tidB; FullName = "Tests.unaffected"; DisplayName = "unaffected"
        Origin = TestOrigin.SourceMapped ("editor", 10)
        Labels = []; Framework = TestFramework.Expecto; Category = TestCategory.Unit }
    |]
    let model0 = (SageFsModel.initial())
    let model1 = { model0 with
                    LiveTesting = { model0.LiveTesting with
                                      TestState = { model0.LiveTesting.TestState with
                                                      Activation = LiveTestingActivation.Active
                                                      DiscoveredTests = tests } } }
    let model2, _ = SageFsUpdate.update (SageFsMsg.Event (SageFsEvent.TestRunStarted ([| tidA |], Some "s"))) model1
    let entryB =
      model2.LiveTesting.TestState.StatusEntries
      |> Array.tryFind (fun e -> e.TestId = tidB)
    match entryB with
    | Some e -> e.Status |> Expect.equal "should still be Detected" TestRunStatus.Detected
    | None -> failtest "expected status entry for unaffected test"
  }

  test "TestRunStarted keeps previous Passed status visible (streaming shows available result)" {
    let tid = TestId.create "Tests.prev_passed" TestFramework.Expecto
    let tests = [|
      { Id = tid; FullName = "Tests.prev_passed"; DisplayName = "prev_passed"
        Origin = TestOrigin.SourceMapped ("editor", 5)
        Labels = []; Framework = TestFramework.Expecto; Category = TestCategory.Unit }
    |]
    let dur = System.TimeSpan.FromMilliseconds 10.0
    let result = {
      TestId = tid; TestName = "Tests.prev_passed"
      Result = TestResult.Passed dur; Timestamp = System.DateTimeOffset.UtcNow; Output = None }
    let model0 = (SageFsModel.initial())
    let model1 = { model0 with
                    LiveTesting = { model0.LiveTesting with
                                      TestState = { model0.LiveTesting.TestState with
                                                      Activation = LiveTestingActivation.Active
                                                      DiscoveredTests = tests
                                                      LastResults = Map.ofList [ tid, result ] } } }
    let model2, _ = SageFsUpdate.update (SageFsMsg.Event (SageFsEvent.TestsDiscovered ("test-session", tests))) model1
    let model3, _ = SageFsUpdate.update (SageFsMsg.Event (SageFsEvent.TestRunStarted ([| tid |], Some "test-session"))) model2
    let entry =
      model3.LiveTesting.TestState.StatusEntries
      |> Array.tryFind (fun e -> e.TestId = tid)
    match entry with
    | Some e ->
      // Streaming: previous result stays visible during run
      match e.Status with
      | TestRunStatus.Passed _ -> ()
      | other -> failtestf "expected Passed (streaming), got %A" other
      match e.PreviousStatus with
      | TestRunStatus.Passed _ -> ()
      | other -> failtestf "expected previous Passed, got %A" other
    | None -> failtest "expected status entry"
  }

  test "effect handler dispatches TestRunStarted before async execution" {
    let tid = TestId.create "Tests.sync_dispatch" TestFramework.Expecto
    let tests = [|
      { Id = tid; FullName = "Tests.sync_dispatch"; DisplayName = "sync_dispatch"
        Origin = TestOrigin.ReflectionOnly; Framework = TestFramework.Expecto
        Category = TestCategory.Unit; Labels = [] }
    |]
    let model0 = (SageFsModel.initial())
    let model1 = { model0 with
                    LiveTesting = { model0.LiveTesting with
                                      TestState = { model0.LiveTesting.TestState with
                                                      Activation = LiveTestingActivation.Active
                                                      DiscoveredTests = tests } } }
    let model2, _ = SageFsUpdate.update (SageFsMsg.Event (SageFsEvent.TestRunStarted ([| tid |], Some "s"))) model1
    TestRunPhase.isAnyRunning model2.LiveTesting.TestState.RunPhases
    |> Expect.isTrue "phase should be Running after TestRunStarted dispatch"
  }
]

// --- SSE enrichment round-trip tests ---

[<Tests>]
let sseEnrichmentTests = testList "SSE enrichment round-trip" [
  test "after mergeResults, compute enriched batch from state" {
    let tid = TestId.create "Tests.round_trip" TestFramework.Expecto
    let testCase = {
      Id = tid; FullName = "Tests.round_trip"; DisplayName = "round_trip"
      Origin = TestOrigin.ReflectionOnly; Framework = TestFramework.Expecto
      Category = TestCategory.Unit; Labels = [] }
    let dur = System.TimeSpan.FromMilliseconds 42.0
    let result = {
      TestId = tid; TestName = "Tests.round_trip"
      Result = TestResult.Passed dur; Timestamp = System.DateTimeOffset.UtcNow; Output = None }
    let phase, gen = TestRunPhase.startRun RunGeneration.zero
    let state = {
      LiveTestState.empty with
        Activation = LiveTestingActivation.Active
        DiscoveredTests = [| testCase |]
        RunPhases = Map.ofList ["s", phase]
        LastGeneration = gen
        AffectedTests = Set.ofList [tid] }
    let merged = LiveTesting.mergeResults state [| result |]
    let entries = LiveTesting.computeStatusEntries merged
    let freshness = ResultFreshness.Fresh
    let batch =
      let completion = TestResultsBatchPayload.deriveCompletion freshness 1 entries.Length
      TestResultsBatchPayload.create gen freshness completion merged.Activation entries None
    batch.Generation |> Expect.equal "generation matches" gen
    batch.Freshness |> Expect.equal "fresh results" ResultFreshness.Fresh
    batch.Entries.Length |> Expect.equal "one entry" 1
    match batch.Entries.[0].Status with
    | TestRunStatus.Passed d -> d |> Expect.equal "correct duration" dur
    | other -> failtestf "expected Passed, got %A" other
    batch.Summary.Passed |> Expect.equal "summary shows passed" 1
  }

  test "stale results after edit produce StaleCodeEdited" {
    let tid = TestId.create "Tests.stale_edit" TestFramework.Expecto
    let testCase = {
      Id = tid; FullName = "Tests.stale_edit"; DisplayName = "stale_edit"
      Origin = TestOrigin.ReflectionOnly; Framework = TestFramework.Expecto
      Category = TestCategory.Unit; Labels = [] }
    let phase, gen = TestRunPhase.startRun RunGeneration.zero
    let editedPhase = TestRunPhase.onEdit phase
    let state = {
      LiveTestState.empty with
        Activation = LiveTestingActivation.Active
        DiscoveredTests = [| testCase |]
        RunPhases = Map.ofList ["s", editedPhase]
        LastGeneration = gen
        AffectedTests = Set.ofList [tid] }
    let result = {
      TestId = tid; TestName = "Tests.stale_edit"
      Result = TestResult.Passed (System.TimeSpan.FromMilliseconds 10.0)
      Timestamp = System.DateTimeOffset.UtcNow; Output = None }
    let merged = LiveTesting.mergeResults state [| result |]
    let entries = LiveTesting.computeStatusEntries merged
    let _newPhase, freshness = TestRunPhase.onResultsArrived gen editedPhase
    let batch =
      let completion = TestResultsBatchPayload.deriveCompletion freshness 1 entries.Length
      TestResultsBatchPayload.create gen freshness completion merged.Activation entries None
    batch.Freshness |> Expect.equal "stale code edited" ResultFreshness.StaleCodeEdited
  }
]

// --- FCS Dependency Graph Integration Tests ---

[<Tests>]
let fcsGraphTests = testList "FCS dependency graph builder" [

  test "builds inverted index from symbol uses" {
    let uses = [|
      { FullName = "MyApp.Math.add"; DisplayName = "add"; UseKind = SymbolUseKind.Definition; StartLine = 2; EndLine = 2 }
      { FullName = "MyApp.Math.multiply"; DisplayName = "multiply"; UseKind = SymbolUseKind.Definition; StartLine = 3; EndLine = 3 }
      { FullName = "MyApp.Tests.addTest"; DisplayName = "addTest"; UseKind = SymbolUseKind.Definition; StartLine = 5; EndLine = 5 }
      { FullName = "MyApp.Tests.mulTest"; DisplayName = "mulTest"; UseKind = SymbolUseKind.Definition; StartLine = 7; EndLine = 7 }
      { FullName = "MyApp.Math.add"; DisplayName = "add"; UseKind = SymbolUseKind.Reference; StartLine = 5; EndLine = 5 }
      { FullName = "MyApp.Math.multiply"; DisplayName = "multiply"; UseKind = SymbolUseKind.Reference; StartLine = 7; EndLine = 7 }
    |]
    let graph = TestDependencyGraph.buildFromSymbolUses ".Tests." (TestFramework.Unknown "fcs") uses
    graph.SymbolToTests.Count
    |> Expect.equal "should have 2 production symbols" 2
    let addAffected = TestDependencyGraph.findAffected ["MyApp.Math.add"] graph
    // With transitive coverage, multiply calls add (line-range heuristic),
    // so changing add affects both addTest (direct) and mulTest (transitive via multiply→add)
    addAffected.Length
    |> Expect.equal "add should affect 2 tests (direct + transitive)" 2
    let mulAffected = TestDependencyGraph.findAffected ["MyApp.Math.multiply"] graph
    mulAffected.Length
    |> Expect.equal "multiply should affect 1 test" 1
  }

  test "test calling multiple production functions maps to all" {
    let uses = [|
      { FullName = "MyApp.Math.add"; DisplayName = "add"; UseKind = SymbolUseKind.Definition; StartLine = 2; EndLine = 2 }
      { FullName = "MyApp.Math.multiply"; DisplayName = "multiply"; UseKind = SymbolUseKind.Definition; StartLine = 3; EndLine = 3 }
      { FullName = "MyApp.Tests.combinedTest"; DisplayName = "combinedTest"; UseKind = SymbolUseKind.Definition; StartLine = 5; EndLine = 5 }
      { FullName = "MyApp.Math.add"; DisplayName = "add"; UseKind = SymbolUseKind.Reference; StartLine = 6; EndLine = 6 }
      { FullName = "MyApp.Math.multiply"; DisplayName = "multiply"; UseKind = SymbolUseKind.Reference; StartLine = 7; EndLine = 7 }
    |]
    let graph = TestDependencyGraph.buildFromSymbolUses ".Tests." (TestFramework.Unknown "fcs") uses
    let combinedId = TestId.create "MyApp.Tests.combinedTest" (TestFramework.Unknown "fcs")
    TestDependencyGraph.findAffected ["MyApp.Math.add"] graph
    |> Array.contains combinedId
    |> Expect.isTrue "add should affect combinedTest"
    TestDependencyGraph.findAffected ["MyApp.Math.multiply"] graph
    |> Array.contains combinedId
    |> Expect.isTrue "multiply should affect combinedTest"
  }

  test "unused production symbol has no affected tests" {
    let uses = [|
      { FullName = "MyApp.Math.unused"; DisplayName = "unused"; UseKind = SymbolUseKind.Definition; StartLine = 2; EndLine = 2 }
      { FullName = "MyApp.Tests.someTest"; DisplayName = "someTest"; UseKind = SymbolUseKind.Definition; StartLine = 5; EndLine = 5 }
    |]
    let graph = TestDependencyGraph.buildFromSymbolUses ".Tests." (TestFramework.Unknown "fcs") uses
    TestDependencyGraph.findAffected ["MyApp.Math.unused"] graph
    |> Array.length
    |> Expect.equal "unused should affect 0 tests" 0
  }

  test "multiple changed symbols finds union of affected tests" {
    let uses = [|
      { FullName = "MyApp.Math.add"; DisplayName = "add"; UseKind = SymbolUseKind.Definition; StartLine = 2; EndLine = 2 }
      { FullName = "MyApp.Math.sub"; DisplayName = "sub"; UseKind = SymbolUseKind.Definition; StartLine = 3; EndLine = 3 }
      { FullName = "MyApp.Tests.addTest"; DisplayName = "addTest"; UseKind = SymbolUseKind.Definition; StartLine = 5; EndLine = 5 }
      { FullName = "MyApp.Tests.subTest"; DisplayName = "subTest"; UseKind = SymbolUseKind.Definition; StartLine = 8; EndLine = 8 }
      { FullName = "MyApp.Math.add"; DisplayName = "add"; UseKind = SymbolUseKind.Reference; StartLine = 6; EndLine = 6 }
      { FullName = "MyApp.Math.sub"; DisplayName = "sub"; UseKind = SymbolUseKind.Reference; StartLine = 9; EndLine = 9 }
    |]
    let graph = TestDependencyGraph.buildFromSymbolUses ".Tests." (TestFramework.Unknown "fcs") uses
    TestDependencyGraph.findAffected ["MyApp.Math.add"; "MyApp.Math.sub"] graph
    |> Array.length
    |> Expect.equal "should find 2 distinct tests" 2
  }

  test "empty symbol uses produces empty graph" {
    let graph = TestDependencyGraph.buildFromSymbolUses ".Tests." (TestFramework.Unknown "fcs") [||]
    graph.SymbolToTests.Count
    |> Expect.equal "empty uses produces empty graph" 0
  }

  test "FSharp stdlib uses are excluded from graph" {
    let uses = [|
      { FullName = "MyApp.Tests.test1"; DisplayName = "test1"; UseKind = SymbolUseKind.Definition; StartLine = 1; EndLine = 1 }
      { FullName = "Microsoft.FSharp.Core.ExtraTopLevelOperators.printfn"; DisplayName = "printfn"; UseKind = SymbolUseKind.Reference; StartLine = 2; EndLine = 2 }
      { FullName = "MyApp.Logic.doThing"; DisplayName = "doThing"; UseKind = SymbolUseKind.Reference; StartLine = 3; EndLine = 3 }
    |]
    let graph = TestDependencyGraph.buildFromSymbolUses ".Tests." (TestFramework.Unknown "fcs") uses
    graph.SymbolToTests.Count
    |> Expect.equal "only production symbols, not stdlib" 1
    graph.SymbolToTests |> Map.containsKey "Microsoft.FSharp.Core.ExtraTopLevelOperators.printfn"
    |> Expect.isFalse "should not contain FSharp stdlib"
  }

  test "orchestrator uses FCS graph for affected test selection" {
    let uses = [|
      { FullName = "MyApp.Math.add"; DisplayName = "add"; UseKind = SymbolUseKind.Definition; StartLine = 2; EndLine = 2 }
      { FullName = "MyApp.Tests.addTest"; DisplayName = "addTest"; UseKind = SymbolUseKind.Definition; StartLine = 5; EndLine = 5 }
      { FullName = "MyApp.Tests.otherTest"; DisplayName = "otherTest"; UseKind = SymbolUseKind.Definition; StartLine = 8; EndLine = 8 }
      { FullName = "MyApp.Math.add"; DisplayName = "add"; UseKind = SymbolUseKind.Reference; StartLine = 6; EndLine = 6 }
    |]
    let graph = TestDependencyGraph.buildFromSymbolUses ".Tests." (TestFramework.Unknown "fcs") uses
    let addTestCase = {
      Id = TestId.create "MyApp.Tests.addTest" (TestFramework.Unknown "fcs")
      FullName = "MyApp.Tests.addTest"; DisplayName = "addTest"
      Origin = TestOrigin.SourceMapped ("test.fsx", 5)
      Labels = []; Framework = TestFramework.Unknown "fcs"; Category = TestCategory.Unit
    }
    let otherTestCase = {
      Id = TestId.create "MyApp.Tests.otherTest" (TestFramework.Unknown "fcs")
      FullName = "MyApp.Tests.otherTest"; DisplayName = "otherTest"
      Origin = TestOrigin.SourceMapped ("test.fsx", 8)
      Labels = []; Framework = TestFramework.Unknown "fcs"; Category = TestCategory.Unit
    }
    let state = { LiveTestState.empty with
                    DiscoveredTests = [| addTestCase; otherTestCase |]
                    Activation = LiveTestingActivation.Active }
    let stateWithEntries = { state with StatusEntries = LiveTesting.computeStatusEntries state }
    match TestCycleOrchestrator.decide stateWithEntries RunTrigger.Keystroke ["MyApp.Math.add"] "test.fs" graph with
    | TestCycleDecision.Explained explained ->
      let testIds = explained.Explanation.SelectedTests
      testIds.Length
      |> Expect.equal "should only run addTest, not otherTest" 1
      testIds.[0]
      |> Expect.equal "should be addTest" addTestCase.FullName
    | TestCycleDecision.FullCycle testIds ->
      testIds.Length
      |> Expect.equal "should only run addTest, not otherTest" 1
      testIds.[0]
      |> Expect.equal "should be addTest" addTestCase.Id
    | other -> failtest (sprintf "Expected FullCycle, got %A" other)
  }

  test "coverage projection with built graph" {
    let uses = [|
      { FullName = "MyApp.Math.add"; DisplayName = "add"; UseKind = SymbolUseKind.Definition; StartLine = 2; EndLine = 2 }
      { FullName = "MyApp.Math.unused"; DisplayName = "unused"; UseKind = SymbolUseKind.Definition; StartLine = 3; EndLine = 3 }
      { FullName = "MyApp.Tests.addTest"; DisplayName = "addTest"; UseKind = SymbolUseKind.Definition; StartLine = 5; EndLine = 5 }
      { FullName = "MyApp.Math.add"; DisplayName = "add"; UseKind = SymbolUseKind.Reference; StartLine = 6; EndLine = 6 }
    |]
    let graph = TestDependencyGraph.buildFromSymbolUses ".Tests." (TestFramework.Unknown "fcs") uses
    let addTestId = TestId.create "MyApp.Tests.addTest" (TestFramework.Unknown "fcs")
    let results = Map.ofList [
      addTestId, {
        TestId = addTestId; TestName = "addTest"
        Result = TestResult.Passed (TimeSpan.FromMilliseconds 5.0)
        Timestamp = DateTimeOffset.UtcNow; Output = None
      }
    ]
    match CoverageProjection.symbolCoverage graph results "MyApp.Math.add" with
    | CoverageStatus.Covered (count, health) ->
      count |> Expect.equal "add covered by 1 test" 1
      health |> Expect.equal "addTest passes" CoverageHealth.AllPassing
    | other -> failtest (sprintf "Expected Covered for add, got %A" other)
    match CoverageProjection.symbolCoverage graph results "MyApp.Math.unused" with
    | CoverageStatus.NotCovered -> ()
    | other -> failtest (sprintf "Expected NotCovered for unused, got %A" other)
  }
]

// --- Multi-File Merge Tests ---
let mkSymRef fullName line isDef : SymbolReference =
  { SymbolFullName = fullName
    UseKind = if isDef then SymbolUseKind.Definition else SymbolUseKind.Reference
    UsedInTestId = None
    FilePath = "test.fs"
    Line = line }

[<Tests>]
let perFileMergeTests = testList "per-file merge correctness" [
  test "updateGraph merges TestIds across files, not overwrite" {
    let fileARefs = [
      mkSymRef "MyApp.Tests.test1" 10 true
      mkSymRef "MyApp.Prod.foo" 12 false
    ]
    let fileBRefs = [
      mkSymRef "MyApp.Tests.test2" 10 true
      mkSymRef "MyApp.Prod.foo" 12 false
    ]

    let graph1 =
      TestDependencyGraph.empty
      |> SymbolGraphBuilder.updateGraph ".Tests." (TestFramework.Unknown "fcs") fileARefs "FileA.fs"

    let test1Id = TestId.create "MyApp.Tests.test1" (TestFramework.Unknown "fcs")
    TestDependencyGraph.findAffected ["MyApp.Prod.foo"] graph1
    |> Expect.contains "FileA should map Prod.foo to test1" test1Id

    let graph2 =
      graph1
      |> SymbolGraphBuilder.updateGraph ".Tests." (TestFramework.Unknown "fcs") fileBRefs "FileB.fs"

    let test2Id = TestId.create "MyApp.Tests.test2" (TestFramework.Unknown "fcs")
    let affected = TestDependencyGraph.findAffected ["MyApp.Prod.foo"] graph2

    affected |> Expect.contains "Should still have test1 from FileA" test1Id
    affected |> Expect.contains "Should have test2 from FileB" test2Id
  }

  test "re-analyzing same file replaces old entries" {
    let refsV1 = [
      mkSymRef "MyApp.Tests.test1" 10 true
      mkSymRef "MyApp.Prod.foo" 12 false
    ]
    let refsV2 = [
      mkSymRef "MyApp.Tests.test1" 10 true
      mkSymRef "MyApp.Prod.bar" 12 false
    ]

    let graph1 =
      TestDependencyGraph.empty
      |> SymbolGraphBuilder.updateGraph ".Tests." (TestFramework.Unknown "fcs") refsV1 "FileA.fs"
    let graph2 =
      graph1
      |> SymbolGraphBuilder.updateGraph ".Tests." (TestFramework.Unknown "fcs") refsV2 "FileA.fs"

    let test1Id = TestId.create "MyApp.Tests.test1" (TestFramework.Unknown "fcs")
    TestDependencyGraph.findAffected ["MyApp.Prod.foo"] graph2
    |> Expect.isEmpty "foo should be gone after FileA re-analysis"

    TestDependencyGraph.findAffected ["MyApp.Prod.bar"] graph2
    |> Expect.contains "bar should map to test1" test1Id
  }

  test "removing a file's refs clears only that file" {
    let fileARefs = [
      mkSymRef "MyApp.Tests.test1" 10 true
      mkSymRef "MyApp.Prod.foo" 12 false
    ]
    let fileBRefs = [
      mkSymRef "MyApp.Tests.test2" 10 true
      mkSymRef "MyApp.Prod.foo" 12 false
    ]

    let graph =
      TestDependencyGraph.empty
      |> SymbolGraphBuilder.updateGraph ".Tests." (TestFramework.Unknown "fcs") fileARefs "FileA.fs"
      |> SymbolGraphBuilder.updateGraph ".Tests." (TestFramework.Unknown "fcs") fileBRefs "FileB.fs"

    let graphAfter =
      graph |> SymbolGraphBuilder.updateGraph ".Tests." (TestFramework.Unknown "fcs") [] "FileA.fs"

    let test2Id = TestId.create "MyApp.Tests.test2" (TestFramework.Unknown "fcs")
    let affected = TestDependencyGraph.findAffected ["MyApp.Prod.foo"] graphAfter

    affected |> Expect.hasLength "only FileB's test2 remains" 1
    affected |> Expect.contains "test2 should still be there" test2Id
  }

  test "PerFileIndex tracks contributions" {
    let fileARefs = [
      mkSymRef "MyApp.Tests.test1" 10 true
      mkSymRef "MyApp.Prod.foo" 12 false
    ]

    let graph =
      TestDependencyGraph.empty
      |> SymbolGraphBuilder.updateGraph ".Tests." (TestFramework.Unknown "fcs") fileARefs "FileA.fs"

    graph.PerFileIndex
    |> Map.containsKey "FileA.fs"
    |> Expect.isTrue "PerFileIndex should contain FileA.fs"
  }

  test "multiple symbols across multiple files" {
    let fileARefs = [
      mkSymRef "MyApp.Tests.testA" 10 true
      mkSymRef "MyApp.Prod.foo" 12 false
      mkSymRef "MyApp.Prod.bar" 14 false
    ]
    let fileBRefs = [
      mkSymRef "MyApp.Tests.testB" 10 true
      mkSymRef "MyApp.Prod.bar" 12 false
      mkSymRef "MyApp.Prod.baz" 14 false
    ]

    let graph =
      TestDependencyGraph.empty
      |> SymbolGraphBuilder.updateGraph ".Tests." (TestFramework.Unknown "fcs") fileARefs "FileA.fs"
      |> SymbolGraphBuilder.updateGraph ".Tests." (TestFramework.Unknown "fcs") fileBRefs "FileB.fs"

    let testAId = TestId.create "MyApp.Tests.testA" (TestFramework.Unknown "fcs")
    let testBId = TestId.create "MyApp.Tests.testB" (TestFramework.Unknown "fcs")

    TestDependencyGraph.findAffected ["MyApp.Prod.foo"] graph
    |> Expect.equal "foo maps to testA only" [| testAId |]

    let barAffected = TestDependencyGraph.findAffected ["MyApp.Prod.bar"] graph
    barAffected |> Expect.contains "bar maps to testA" testAId
    barAffected |> Expect.contains "bar maps to testB" testBId

    TestDependencyGraph.findAffected ["MyApp.Prod.baz"] graph
    |> Expect.equal "baz maps to testB only" [| testBId |]
  }
]

[<Tests>]
let projectAssemblyDiscoveryTests = testList "Project assembly initial discovery" [
  test "afterReload on SageFs.Tests assembly discovers tests" {
    let asm = System.Reflection.Assembly.LoadFrom(resolveTestDll "SageFs.Tests.dll")
    let result = LiveTestingHook.afterReload BuiltInExecutors.builtIn asm []
    Expect.isGreaterThan
      "should discover at least 100 tests"
      (result.DiscoveredTests.Length, 100)
  }

  test "afterReload detects expecto provider from test assembly" {
    let asm = System.Reflection.Assembly.LoadFrom(resolveTestDll "SageFs.Tests.dll")
    let result = LiveTestingHook.afterReload BuiltInExecutors.builtIn asm []
    result.DetectedProviders
    |> List.exists (fun p ->
      match p with
      | ProviderDescription.Custom c -> c.Name = TestFramework.Expecto
      | _ -> false)
    |> Expect.isTrue "should detect expecto provider"
  }

  test "all discovered tests have framework=expecto" {
    let asm = System.Reflection.Assembly.LoadFrom(resolveTestDll "SageFs.Tests.dll")
    let result = LiveTestingHook.afterReload BuiltInExecutors.builtIn asm []
    result.DiscoveredTests
    |> Array.iter (fun t ->
      t.Framework
      |> Expect.equal "framework should be expecto" TestFramework.Expecto)
  }

  test "merging FSI + project results deduplicates providers" {
    let fsiResult = LiveTestHookResult.empty
    let projResult = {
      DetectedProviders =
        [ ProviderDescription.Custom
            { Name = TestFramework.Expecto; AssemblyMarker = "Expecto" } ]
      DiscoveredTests =
        [| { Id = TestId.create "test1" TestFramework.Expecto
             FullName = "test1"
             DisplayName = "t1"
             Origin = TestOrigin.ReflectionOnly
             Labels = []
             Framework = TestFramework.Expecto
             Category = TestCategory.Unit } |]
      AffectedTestIds = [||]
      RunTest = LiveTestHookResult.noOp
    }
    let allResults = [fsiResult; projResult]
    let mergedProviders =
      allResults
      |> List.collect (fun r -> r.DetectedProviders)
      |> List.distinctBy (fun p ->
        match p with
        | ProviderDescription.AttributeBased a -> a.Name
        | ProviderDescription.Custom c -> c.Name)
    let mergedTests =
      allResults
      |> List.map (fun r -> r.DiscoveredTests)
      |> Array.concat
    mergedProviders.Length
    |> Expect.equal "one distinct provider" 1
    mergedTests.Length
    |> Expect.equal "one test from project" 1
  }
]

[<Tests>]
let mergeDiscoveredTestsTests = testList "LiveTesting.mergeDiscoveredTests" [
  test "empty incoming preserves existing tests" {
    let existing = [|
      { Id = TestId.create "test1" TestFramework.Expecto; FullName = "test1"; DisplayName = "t1"
        Origin = TestOrigin.ReflectionOnly; Labels = []; Framework = TestFramework.Expecto
        Category = TestCategory.Unit } |]
    let result = LiveTesting.mergeDiscoveredTests existing [||]
    result.Length |> Expect.equal "keeps existing" 1
  }

  test "empty existing uses incoming" {
    let incoming = [|
      { Id = TestId.create "test1" TestFramework.Expecto; FullName = "test1"; DisplayName = "t1"
        Origin = TestOrigin.ReflectionOnly; Labels = []; Framework = TestFramework.Expecto
        Category = TestCategory.Unit } |]
    let result = LiveTesting.mergeDiscoveredTests [||] incoming
    result.Length |> Expect.equal "takes incoming" 1
  }

  test "incoming overrides existing with same TestId" {
    let existing = [|
      { Id = TestId.create "test1" TestFramework.Expecto; FullName = "test1"; DisplayName = "old"
        Origin = TestOrigin.ReflectionOnly; Labels = []; Framework = TestFramework.Expecto
        Category = TestCategory.Unit } |]
    let incoming = [|
      { Id = TestId.create "test1" TestFramework.Expecto; FullName = "test1"; DisplayName = "new"
        Origin = TestOrigin.ReflectionOnly; Labels = []; Framework = TestFramework.Expecto
        Category = TestCategory.Unit } |]
    let result = LiveTesting.mergeDiscoveredTests existing incoming
    result.Length |> Expect.equal "one merged test" 1
    result.[0].DisplayName |> Expect.equal "incoming wins" "new"
  }

  test "disjoint tests are unioned" {
    let existing = [|
      { Id = TestId.create "test1" TestFramework.Expecto; FullName = "test1"; DisplayName = "t1"
        Origin = TestOrigin.ReflectionOnly; Labels = []; Framework = TestFramework.Expecto
        Category = TestCategory.Unit } |]
    let incoming = [|
      { Id = TestId.create "test2" TestFramework.Expecto; FullName = "test2"; DisplayName = "t2"
        Origin = TestOrigin.ReflectionOnly; Labels = []; Framework = TestFramework.Expecto
        Category = TestCategory.Unit } |]
    let result = LiveTesting.mergeDiscoveredTests existing incoming
    result.Length |> Expect.equal "both tests present" 2
  }

  test "second eval with empty incoming does not wipe tests" {
    // Simulates: first eval discovers 3454 tests, second eval discovers 0 from FSI
    let projectTests = [|
      { Id = TestId.create "proj.test1" TestFramework.Expecto; FullName = "proj.test1"
        DisplayName = "t1"; Origin = TestOrigin.ReflectionOnly; Labels = []
        Framework = TestFramework.Expecto; Category = TestCategory.Unit }
      { Id = TestId.create "proj.test2" TestFramework.Expecto; FullName = "proj.test2"
        DisplayName = "t2"; Origin = TestOrigin.ReflectionOnly; Labels = []
        Framework = TestFramework.Expecto; Category = TestCategory.Unit } |]
    let afterFirstEval = LiveTesting.mergeDiscoveredTests [||] projectTests
    // Second eval: FSI dynamic assembly has no tests
    let afterSecondEval = LiveTesting.mergeDiscoveredTests afterFirstEval [||]
    afterSecondEval.Length
    |> Expect.equal "project tests survive second eval" 2
  }
]

[<Tests>]
let affectedExecutionTriggerTests = testList "AffectedTestsComputed execution trigger" [
  let mkState tests =
    { LiveTestState.empty with
        DiscoveredTests = tests
        Activation = LiveTestingActivation.Active
        RunPolicies = RunPolicyDefaults.defaults }
  let mkCycleState tests =
    { LiveTestCycleState.empty with
        TestState = mkState tests }
  let tc1 = {
    Id = TestId.create "ns.t1" TestFramework.Expecto
    FullName = "ns.t1"; DisplayName = "t1"
    Origin = TestOrigin.ReflectionOnly
    Labels = []; Framework = TestFramework.Expecto; Category = TestCategory.Unit
  }
  let tc2 = {
    Id = TestId.create "ns.t2" TestFramework.Expecto
    FullName = "ns.t2"; DisplayName = "t2"
    Origin = TestOrigin.ReflectionOnly
    Labels = []; Framework = TestFramework.Expecto; Category = TestCategory.Integration
  }
  let tc3 = {
    Id = TestId.create "ns.t3" TestFramework.Expecto
    FullName = "ns.t3"; DisplayName = "t3"
    Origin = TestOrigin.ReflectionOnly
    Labels = []; Framework = TestFramework.Expecto; Category = TestCategory.Unit
  }

  test "empty affected IDs produce no effects" {
    let ps = mkCycleState [| tc1; tc2 |]
    let effects = LiveTestCycleState.triggerExecutionForAffected [||] RunTrigger.FileSave None ps
    effects
    |> Expect.isEmpty "should produce no effects for empty affected IDs"
  }

  test "non-empty affected IDs produce RunAffectedTests effect" {
    let ps = mkCycleState [| tc1; tc2; tc3 |]
    let effects = LiveTestCycleState.triggerExecutionForAffected [| tc1.Id; tc3.Id |] RunTrigger.FileSave None ps
    effects
    |> List.length
    |> Expect.equal "should produce exactly one effect" 1
  }

  test "only unit tests run on FileSave when policy is OnEveryChange" {
    let ps = mkCycleState [| tc1; tc2; tc3 |]
    let effects = LiveTestCycleState.triggerExecutionForAffected [| tc1.Id; tc2.Id |] RunTrigger.FileSave None ps
    match effects with
    | [ TestCycleEffect.RunAffectedTests (tests, _, _, _, _, _) ] ->
      tests
      |> Array.length
      |> Expect.equal "should only include unit test" 1
      tests.[0].Category
      |> Expect.equal "should be unit category" TestCategory.Unit
    | _ -> failtest "expected exactly one RunAffectedTests effect"
  }

  test "ExplicitRun trigger runs integration tests too" {
    let ps = mkCycleState [| tc1; tc2; tc3 |]
    let effects = LiveTestCycleState.triggerExecutionForAffected [| tc1.Id; tc2.Id |] RunTrigger.ExplicitRun None ps
    match effects with
    | [ TestCycleEffect.RunAffectedTests (tests, _, _, _, _, _) ] ->
      tests
      |> Array.length
      |> Expect.equal "should include both unit and integration" 2
    | _ -> failtest "expected exactly one RunAffectedTests effect"
  }

  test "disabled live testing produces no effects" {
    let ps = { mkCycleState [| tc1; tc2 |] with
                 TestState = { (mkState [| tc1; tc2 |]) with Activation = LiveTestingActivation.Inactive } }
    let effects = LiveTestCycleState.triggerExecutionForAffected [| tc1.Id |] RunTrigger.FileSave None ps
    effects
    |> Expect.isEmpty "should produce no effects when disabled"
  }

  test "affected IDs not in discovered tests are ignored" {
    let ps = mkCycleState [| tc1 |]
    let unknownId = TestId.create "unknown" TestFramework.Expecto
    let effects = LiveTestCycleState.triggerExecutionForAffected [| unknownId |] RunTrigger.FileSave None ps
    effects
    |> Expect.isEmpty "unknown test IDs should not produce effects"
  }
]

// ── findAffectedTests fix + filterTestsForExplicitRun + findAllTestIds tests ──

[<Tests>]
let findAffectedTestsFixTests =
  let mkTC id name fw =
    { Id = TestId.create name fw; FullName = name
      DisplayName = name.Split('.').[name.Split('.').Length - 1]
      Origin = TestOrigin.ReflectionOnly; Labels = []; Framework = fw
      Category = TestCategory.Unit }
  let sampleTests = [|
    mkTC "t1" "MyModule.Tests.test_add" TestFramework.Expecto
    mkTC "t2" "MyModule.Tests.test_sub" TestFramework.Expecto
    mkTC "t3" "Other.Tests.test_mul" TestFramework.XUnit
  |]
  testList "findAffectedTests fixed semantics" [
    test "empty method names returns empty array" {
      LiveTestingHook.findAffectedTests sampleTests []
      |> Array.length
      |> Expect.equal "nothing changed = no affected tests" 0
    }
    test "matching method name returns affected test" {
      LiveTestingHook.findAffectedTests sampleTests ["test_add"]
      |> Array.length
      |> Expect.equal "one match" 1
    }
    test "multiple matching methods return multiple tests" {
      LiveTestingHook.findAffectedTests sampleTests ["test_add"; "test_mul"]
      |> Array.length
      |> Expect.equal "two matches" 2
    }
    test "non-matching method falls back to all tests" {
      // Conservative fallback: non-empty methods but no match → run everything
      LiveTestingHook.findAffectedTests sampleTests ["nonexistent"]
      |> Array.length
      |> Expect.equal "falls back to all tests" 3
    }
  ]

[<Tests>]
let findAllTestIdsTests =
  let mkTC id name fw =
    { Id = TestId.create name fw; FullName = name
      DisplayName = name.Split('.').[name.Split('.').Length - 1]
      Origin = TestOrigin.ReflectionOnly; Labels = []; Framework = fw
      Category = TestCategory.Unit }
  let sampleTests = [|
    mkTC "t1" "A.test1" TestFramework.XUnit
    mkTC "t2" "B.test2" TestFramework.XUnit
  |]
  testList "findAllTestIds" [
    test "returns all test IDs" {
      LiveTestingHook.findAllTestIds sampleTests
      |> Array.length
      |> Expect.equal "all tests" 2
    }
    test "empty discovered returns empty" {
      LiveTestingHook.findAllTestIds [||]
      |> Array.length
      |> Expect.equal "no tests" 0
    }
  ]

[<Tests>]
let filterTestsForExplicitRunTests =
  let mkTC id name fw =
    { Id = TestId.create name fw; FullName = name
      DisplayName = name.Split('.').[name.Split('.').Length - 1]
      Origin = TestOrigin.ReflectionOnly; Labels = []; Framework = fw
      Category = TestCategory.Unit }
  let sampleTests = [|
    mkTC "t1" "MyModule.Tests.test_add" TestFramework.Expecto
    mkTC "t2" "MyModule.Tests.test_sub" TestFramework.Expecto
    mkTC "t3" "Other.Tests.test_mul" TestFramework.XUnit
  |]
  testList "filterTestsForExplicitRun" [
    test "no filters returns all" {
      LiveTestCycleState.filterTestsForExplicitRun sampleTests None None None
      |> Array.length
      |> Expect.equal "all tests" 3
    }
    test "pattern filter matches by FullName" {
      LiveTestCycleState.filterTestsForExplicitRun sampleTests None (Some "test_add") None
      |> Array.length
      |> Expect.equal "pattern match" 1
    }
    test "category filter" {
      let mixedTests = [|
        mkTC "t1" "Unit.test1" TestFramework.XUnit
        { mkTC "t2" "Integration.test2" TestFramework.XUnit with Category = TestCategory.Integration }
      |]
      LiveTestCycleState.filterTestsForExplicitRun mixedTests None None (Some TestCategory.Unit)
      |> Array.length
      |> Expect.equal "only unit" 1
    }
    test "file filter works for SourceMapped" {
      let mappedTests = [|
        { mkTC "t1" "Test1" TestFramework.XUnit with Origin = TestOrigin.SourceMapped ("foo.fs", 10) }
        { mkTC "t2" "Test2" TestFramework.XUnit with Origin = TestOrigin.SourceMapped ("bar.fs", 20) }
        mkTC "t3" "Test3" TestFramework.XUnit
      |]
      LiveTestCycleState.filterTestsForExplicitRun mappedTests (Some "foo.fs") None None
      |> Array.length
      |> Expect.equal "only foo.fs" 1
    }
    test "combined filters intersect" {
      let mixedTests = [|
        mkTC "t1" "MyModule.add_test" TestFramework.XUnit
        { mkTC "t2" "MyModule.db_test" TestFramework.XUnit with Category = TestCategory.Integration }
        mkTC "t3" "Other.add_test" TestFramework.XUnit
      |]
      LiveTestCycleState.filterTestsForExplicitRun mixedTests None (Some "add_test") (Some TestCategory.Unit)
      |> Array.length
      |> Expect.equal "pattern + category intersection" 2
    }
  ]

[<Tests>]
let flakyDetectionTests = testList "FlakyDetection" [

  testProperty "ResultWindow.count never exceeds windowSize" <| fun (windowSize: FsCheck.PositiveInt) ->
    let ws = max 2 windowSize.Get
    let w = ResultWindow.create ws
    let outcomes = [| TestOutcome.Pass; TestOutcome.Fail; TestOutcome.Pass; TestOutcome.Fail; TestOutcome.Pass; TestOutcome.Fail; TestOutcome.Pass; TestOutcome.Fail; TestOutcome.Pass; TestOutcome.Fail; TestOutcome.Pass |]
    (outcomes |> Array.fold (fun acc o -> ResultWindow.add o acc) w).Count <= ws

  testProperty "ResultWindow.toList length equals min(adds, windowSize)" <| fun (windowSize: FsCheck.PositiveInt) (additions: FsCheck.PositiveInt) ->
    let ws = max 2 windowSize.Get |> min 50
    let adds = max 1 additions.Get |> min 100
    let w = ResultWindow.create ws
    let outcomes = [| for i in 1..adds -> if i % 2 = 0 then TestOutcome.Pass else TestOutcome.Fail |]
    (outcomes |> Array.fold (fun acc o -> ResultWindow.add o acc) w |> ResultWindow.toList).Length = min adds ws

  test "insufficient when count < minSamples" {
    ResultWindow.create 10 |> ResultWindow.add TestOutcome.Pass |> ResultWindow.add TestOutcome.Fail
    |> TestStability.assess 3 2
    |> Expect.equal "2 samples = Insufficient" TestStability.Insufficient
  }

  test "stable when no flips" {
    ResultWindow.create 10
    |> ResultWindow.add TestOutcome.Pass |> ResultWindow.add TestOutcome.Pass
    |> ResultWindow.add TestOutcome.Pass |> ResultWindow.add TestOutcome.Pass
    |> TestStability.assess 3 2
    |> Expect.equal "all Pass = Stable" TestStability.Stable
  }

  test "flaky when flips exceed threshold" {
    ResultWindow.create 10
    |> ResultWindow.add TestOutcome.Pass |> ResultWindow.add TestOutcome.Fail
    |> ResultWindow.add TestOutcome.Pass |> ResultWindow.add TestOutcome.Fail
    |> TestStability.assess 3 2
    |> function
       | TestStability.Flaky n when n >= 2 -> ()
       | other -> failwithf "expected Flaky with >= 2 flips, got %A" other
  }

  test "stable transitions back from flaky when consistent" {
    let w =
      ResultWindow.create 5
      |> ResultWindow.add TestOutcome.Pass |> ResultWindow.add TestOutcome.Fail
      |> ResultWindow.add TestOutcome.Pass |> ResultWindow.add TestOutcome.Fail
      |> ResultWindow.add TestOutcome.Pass
    match TestStability.assess 3 2 w with
    | TestStability.Flaky _ -> ()
    | other -> failwithf "expected Flaky before, got %A" other
    w |> ResultWindow.add TestOutcome.Pass |> ResultWindow.add TestOutcome.Pass
      |> ResultWindow.add TestOutcome.Pass |> ResultWindow.add TestOutcome.Pass
      |> ResultWindow.add TestOutcome.Pass
    |> TestStability.assess 3 2
    |> Expect.equal "stabilized" TestStability.Stable
  }

  testProperty "empty window is always Insufficient" <| fun (windowSize: FsCheck.PositiveInt) ->
    let ws = max 2 windowSize.Get |> min 50
    ResultWindow.create ws |> TestStability.assess 3 2 = TestStability.Insufficient

  testProperty "all same outcome is never Flaky" <| fun (windowSize: FsCheck.PositiveInt) (additions: FsCheck.PositiveInt) ->
    let ws = max 2 windowSize.Get |> min 50
    let adds = max 3 additions.Get |> min 100
    [1..adds] |> List.fold (fun acc _ -> ResultWindow.add TestOutcome.Pass acc) (ResultWindow.create ws)
    |> TestStability.assess 3 2
    |> function TestStability.Flaky _ -> false | _ -> true

  testProperty "countFlips is symmetric" <| fun () ->
    let w1 = ResultWindow.create 4
              |> ResultWindow.add TestOutcome.Pass |> ResultWindow.add TestOutcome.Fail
              |> ResultWindow.add TestOutcome.Pass |> ResultWindow.add TestOutcome.Fail
    let w2 = ResultWindow.create 4
              |> ResultWindow.add TestOutcome.Fail |> ResultWindow.add TestOutcome.Pass
              |> ResultWindow.add TestOutcome.Fail |> ResultWindow.add TestOutcome.Pass
    ResultWindow.countFlips w1 = ResultWindow.countFlips w2

  test "outcomeOf maps Passed to Pass" {
    FlakyDetection.outcomeOf (TestResult.Passed (TimeSpan.FromMilliseconds 10.0))
    |> Expect.equal "Passed→Pass" TestOutcome.Pass
  }

  test "outcomeOf maps Failed to Fail" {
    FlakyDetection.outcomeOf (TestResult.Failed (TestFailure.AssertionFailed "msg", TimeSpan.FromMilliseconds 10.0))
    |> Expect.equal "Failed→Fail" TestOutcome.Fail
  }

  test "recordResult creates window for new test" {
    let tid = TestId.create "t1" TestFramework.Expecto
    let updated = FlakyDetection.recordResult tid (TestResult.Passed (TimeSpan.FromMilliseconds 5.0)) Map.empty
    Expect.isTrue "should have entry" (Map.containsKey tid updated)
    (Map.find tid updated).Count |> Expect.equal "1 result" 1
  }

  test "assessTest returns Insufficient for unknown test" {
    FlakyDetection.assessTest (TestId.create "unknown" TestFramework.Expecto) Map.empty
    |> Expect.equal "unknown = Insufficient" TestStability.Insufficient
  }

  test "GutterIcon.TestFlaky has correct char and color" {
    GutterIcon.toChar GutterIcon.TestFlaky |> Expect.equal "flaky char" '\u2248'
    GutterIcon.toColorIndex GutterIcon.TestFlaky |> Expect.equal "flaky color" 214uy
  }

  test "GutterIcon.TestFlaky roundtrips through label" {
    GutterIcon.toLabel GutterIcon.TestFlaky |> GutterIcon.parseLabel
    |> Expect.equal "roundtrip" (Some GutterIcon.TestFlaky)
  }

  // ── Failure Narratives ──────────────────────────────────────────────

  test "FailureNarrative.empty has sensible defaults" {
    FailureNarrative.empty.Summary |> Expect.equal "default msg" "Test failed"
    FailureNarrative.empty.CausalChanges |> Expect.isEmpty "no changes"
    FailureNarrative.empty.LastPassedAt |> Expect.isNone "no lastPassed"
    FailureNarrative.empty.PropertyViolation |> Expect.isNone "no violation"
  }

  test "buildNarrative with last passed time" {
    let now = DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero)
    let pass : TestRunResult =
      { TestId = TestId.TestId "x"; TestName = "t"
        Result = TestResult.Passed (TimeSpan.FromMilliseconds 50.0)
        Timestamp = now.AddMinutes(-5.0); Output = None }
    let n =
      FailureNarrativeBuilder.buildNarrative now (Some pass) [] []
        FlakyClassification.Stable (TestFailure.AssertionFailed "bad")
    n.LastPassedAt |> Expect.isSome "should have lastPassedAt"
    n.TimeSinceLastPass |> Expect.isSome "should have timeSinceLastPass"
    (n.TimeSinceLastPass.Value.TotalMinutes, 4.0)
    |> Expect.isGreaterThan "should be >= 4 min"
    n.Summary.Contains("minutes ago") |> Expect.isTrue "should mention time"
  }

  test "buildNarrative for never-passed test" {
    let now = DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero)
    let n =
      FailureNarrativeBuilder.buildNarrative now None [] []
        FlakyClassification.Stable (TestFailure.AssertionFailed "always fails")
    n.LastPassedAt |> Expect.isNone "no lastPassedAt"
    n.TimeSinceLastPass |> Expect.isNone "no timeSinceLastPass"
    n.Summary.Contains("never passed") |> Expect.isTrue "should say never passed"
    n.CausalChanges |> Expect.equal "should be Unknown" [ CausalChange.Unknown ]
  }

  test "buildNarrative with single symbol change" {
    let now = DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero)
    let pass : TestRunResult =
      { TestId = TestId.TestId "x"; TestName = "t"
        Result = TestResult.Passed (TimeSpan.FromMilliseconds 1.0)
        Timestamp = now.AddMinutes(-2.0); Output = None }
    let n =
      FailureNarrativeBuilder.buildNarrative now (Some pass)
        [ "MyModule.calculate" ] []
        FlakyClassification.Stable (TestFailure.AssertionFailed "wrong")
    n.CausalChanges
    |> Expect.equal "symbol change" [ CausalChange.SymbolChanged "MyModule.calculate" ]
    n.Summary.Contains("MyModule.calculate") |> Expect.isTrue "should mention symbol"
  }

  test "buildNarrative with multiple symbol changes" {
    let now = DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero)
    let pass : TestRunResult =
      { TestId = TestId.TestId "x"; TestName = "t"
        Result = TestResult.Passed (TimeSpan.FromMilliseconds 1.0)
        Timestamp = now.AddMinutes(-1.0); Output = None }
    let n =
      FailureNarrativeBuilder.buildNarrative now (Some pass)
        [ "A.foo"; "B.bar"; "C.baz" ] []
        FlakyClassification.Stable (TestFailure.AssertionFailed "wrong")
    n.CausalChanges.Length |> Expect.equal "3 changes" 3
    n.Summary.Contains("3 symbols changed") |> Expect.isTrue "should mention count"
  }

  test "buildNarrative with file changes when no symbols" {
    let now = DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero)
    let pass : TestRunResult =
      { TestId = TestId.TestId "x"; TestName = "t"
        Result = TestResult.Passed (TimeSpan.FromMilliseconds 1.0)
        Timestamp = now.AddMinutes(-1.0); Output = None }
    let n =
      FailureNarrativeBuilder.buildNarrative now (Some pass)
        [] [ "/src/MyModule.fs" ]
        FlakyClassification.Stable (TestFailure.AssertionFailed "wrong")
    n.CausalChanges
    |> Expect.equal "file change" [ CausalChange.FileChanged "/src/MyModule.fs" ]
    n.Summary.Contains("MyModule.fs") |> Expect.isTrue "should mention file"
  }

  test "buildNarrative with FsCheck property violation" {
    let now = DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero)
    let pass : TestRunResult =
      { TestId = TestId.TestId "x"; TestName = "t"
        Result = TestResult.Passed (TimeSpan.FromMilliseconds 1.0)
        Timestamp = now.AddMinutes(-1.0); Output = None }
    let n =
      FailureNarrativeBuilder.buildNarrative now (Some pass) [ "combine" ] []
        (FlakyClassification.PropertyCounterexample """("", "abc")""")
        (TestFailure.AssertionFailed
          "Falsifiable, after 47 tests\nProperty: associativity of combine\nOriginal:\n(\"abc\", \"\", \"xyz\")\nShrunk:\n(\"\", \"abc\")")
    n.PropertyViolation |> Expect.isSome "should have property violation"
    let pv = n.PropertyViolation.Value
    pv.ShrunkCounterexample |> Expect.equal "counterexample" """("", "abc")"""
    pv.AlgebraicCategory |> Expect.equal "detect associativity" (Some "associativity")
    n.Summary.Contains("associativity") |> Expect.isTrue "should mention violation"
  }

  test "buildNarrative no property violation for stable failure" {
    let now = DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero)
    let n =
      FailureNarrativeBuilder.buildNarrative now None [] []
        FlakyClassification.Stable (TestFailure.AssertionFailed "regular")
    n.PropertyViolation |> Expect.isNone "no property violation"
  }

  test "buildNarrative with timeout failure" {
    let now = DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero)
    let pass : TestRunResult =
      { TestId = TestId.TestId "x"; TestName = "t"
        Result = TestResult.Passed (TimeSpan.FromMilliseconds 1.0)
        Timestamp = now.AddMinutes(-30.0); Output = None }
    let n =
      FailureNarrativeBuilder.buildNarrative now (Some pass)
        [] [ "/src/Slow.fs" ] FlakyClassification.Stable
        (TestFailure.TimedOut (TimeSpan.FromSeconds 5.0))
    n.Summary.Contains("minutes ago") |> Expect.isTrue "should mention time"
    n.CausalChanges
    |> Expect.equal "file change" [ CausalChange.FileChanged "/src/Slow.fs" ]
  }

  test "extractPropertyName from FsCheck message" {
    let names = [ "associativity of combine"; "identity element"; "commutativity" ]
    for name in names do
      let msg = sprintf "Falsifiable, after 10 tests\nProperty: %s\nOriginal:\n42" name
      FailureNarrativeBuilder.extractPropertyName msg
      |> Expect.equal (sprintf "extract '%s'" name) (Some name)
  }

  test "extractPropertyName returns None for non-FsCheck" {
    FailureNarrativeBuilder.extractPropertyName "expected 42 but got 0"
    |> Expect.isNone "assertion"
    FailureNarrativeBuilder.extractPropertyName ""
    |> Expect.isNone "empty"
    FailureNarrativeBuilder.extractPropertyName "   "
    |> Expect.isNone "whitespace"
  }

  test "detectAlgebraicCategory matches known categories" {
    let cases = [
      "associativity of foo", Some "associativity"
      "commutativity check", Some "commutativity"
      "identity element", Some "identity"
      "idempotence of f", Some "idempotence"
      "distributivity law", Some "distributivity"
      "inverse property", Some "inverse"
      "absorption law", Some "absorption"
      "closure under op", Some "closure"
      "random property", None
    ]
    for name, expected in cases do
      FailureNarrativeBuilder.detectAlgebraicCategory name
      |> Expect.equal (sprintf "category for '%s'" name) expected
  }

  test "buildNarrative is pure — same inputs same output" {
    let now = DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero)
    let pass : TestRunResult =
      { TestId = TestId.TestId "x"; TestName = "t"
        Result = TestResult.Passed (TimeSpan.FromMilliseconds 1.0)
        Timestamp = now.AddMinutes(-10.0); Output = None }
    let n1 = FailureNarrativeBuilder.buildNarrative now (Some pass) [ "A.f" ] [] FlakyClassification.Stable (TestFailure.AssertionFailed "x")
    let n2 = FailureNarrativeBuilder.buildNarrative now (Some pass) [ "A.f" ] [] FlakyClassification.Stable (TestFailure.AssertionFailed "x")
    n1.Summary |> Expect.equal "deterministic summary" n2.Summary
    n1.CausalChanges |> Expect.equal "deterministic changes" n2.CausalChanges
  }

  test "buildNarrative time bucketing hours" {
    let now = DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero)
    let pass : TestRunResult =
      { TestId = TestId.TestId "x"; TestName = "t"
        Result = TestResult.Passed (TimeSpan.FromMilliseconds 1.0)
        Timestamp = now.AddHours(-3.0); Output = None }
    let n = FailureNarrativeBuilder.buildNarrative now (Some pass) [] [] FlakyClassification.Stable (TestFailure.AssertionFailed "x")
    n.Summary.Contains("hours ago") |> Expect.isTrue "should say hours"
  }

  test "buildNarrative time bucketing days" {
    let now = DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero)
    let pass : TestRunResult =
      { TestId = TestId.TestId "x"; TestName = "t"
        Result = TestResult.Passed (TimeSpan.FromMilliseconds 1.0)
        Timestamp = now.AddDays(-5.0); Output = None }
    let n = FailureNarrativeBuilder.buildNarrative now (Some pass) [] [] FlakyClassification.Stable (TestFailure.AssertionFailed "x")
    n.Summary.Contains("days ago") |> Expect.isTrue "should say days"
  }

  test "buildNarrative mixed symbol and file changes" {
    let now = DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero)
    let pass : TestRunResult =
      { TestId = TestId.TestId "x"; TestName = "t"
        Result = TestResult.Passed (TimeSpan.FromMilliseconds 1.0)
        Timestamp = now.AddMinutes(-1.0); Output = None }
    let n =
      FailureNarrativeBuilder.buildNarrative now (Some pass)
        [ "A.f" ] [ "/x.fs" ] FlakyClassification.Stable
        (TestFailure.AssertionFailed "x")
    n.CausalChanges.Length |> Expect.equal "2 changes" 2
    n.Summary.Contains("1 symbols and 1 files changed")
    |> Expect.isTrue "should mention both"
  }
]
