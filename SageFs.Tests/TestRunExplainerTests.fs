module SageFs.Tests.TestRunExplainerTests

open System
open Expecto
open Expecto.Flip
open SageFs.Features.LiveTesting

// --- Helpers ---

let private mkTestCase (name: string) (cat: TestCategory) : TestCase =
  { Id = TestId.TestId name
    DisplayName = name
    FullName = sprintf "Tests.%s" name
    Origin = TestOrigin.SourceMapped ("Tests.fs", 10)
    Labels = []
    Framework = TestFramework.Expecto
    Category = cat }

let private mkGraph (symbolToTests: (string * string list) list) =
  let m =
    symbolToTests
    |> List.map (fun (sym, tests) ->
      sym, tests |> List.map (fun n -> TestId.TestId n) |> Array.ofList)
    |> Map.ofList
  TestDependencyGraph.fromDirect m

let private mkResult testName (result: TestResult) : TestRunResult =
  { TestId = TestId.TestId testName
    TestName = testName
    Result = result
    Timestamp = DateTimeOffset.UtcNow
    Output = None }

// --- Tests ---

[<Tests>]
let explainerTests = testList "TestRunExplainer" [

  testCase "explainTest: symbol coverage identifies covering symbols" <| fun _ ->
    let graph = mkGraph [ "MyModule.add", ["test_add"; "test_math"] ]
    let tc = mkTestCase "test_add" TestCategory.Unit
    let lastResults =
      Map.ofList [
        TestId.TestId "test_add",
        mkResult "test_add" (TestResult.Passed (TimeSpan.FromMilliseconds 42.0)) ]
    let result =
      TestRunExplainer.explainTest
        graph lastResults Map.empty ["MyModule.add"] RunTrigger.Keystroke tc
    result.CoveringSymbols
    |> Expect.equal "should have covering symbol" ["MyModule.add"]
    result.Reason
    |> Expect.equal "should be SymbolCoverage"
      (TestTriggerReason.SymbolCoverage ["MyModule.add"])
    result.DurationMs
    |> Expect.equal "should have cached duration" (Some 42.0)
    result.IsFlaky |> Expect.isFalse "should not be flaky"

  testCase "explainTest: new test with no prior results" <| fun _ ->
    let graph = mkGraph []
    let tc = mkTestCase "brand_new_test" TestCategory.Unit
    let result =
      TestRunExplainer.explainTest
        graph Map.empty Map.empty ["Foo.bar"] RunTrigger.FileSave tc
    result.Reason
    |> Expect.equal "should be NewTest" TestTriggerReason.NewTest
    result.DurationMs |> Expect.isNone "no prior duration"

  testCase "explainTest: unknown coverage when no symbol match but has prior" <| fun _ ->
    let graph = mkGraph [ "Other.func", ["other_test"] ]
    let tc = mkTestCase "test_add" TestCategory.Unit
    let lastResults =
      Map.ofList [
        TestId.TestId "test_add",
        mkResult "test_add" (TestResult.Passed (TimeSpan.FromMilliseconds 10.0)) ]
    let result =
      TestRunExplainer.explainTest
        graph lastResults Map.empty ["Unrelated.sym"] RunTrigger.Keystroke tc
    result.Reason
    |> Expect.equal "should be UnknownCoverage" TestTriggerReason.UnknownCoverage
    result.CoveringSymbols |> Expect.isEmpty "no covering symbols"

  testCase "explainTest: flaky test is flagged" <| fun _ ->
    let graph = mkGraph [ "MyModule.flaky", ["flaky_test"] ]
    let tc = mkTestCase "flaky_test" TestCategory.Unit
    let tid = TestId.TestId "flaky_test"
    let mutable w = ResultWindow.create 10
    for i in 0..5 do
      let outcome =
        match i % 2 with
        | 0 -> TestOutcome.Pass
        | _ -> TestOutcome.Fail
      w <- ResultWindow.add outcome w
    let flakyHistory = Map.ofList [ tid, w ]
    let result =
      TestRunExplainer.explainTest
        graph Map.empty flakyHistory ["MyModule.flaky"] RunTrigger.Keystroke tc
    result.IsFlaky |> Expect.isTrue "should be flagged as flaky"

  testCase "explainSymbolChange: finds affected tests via dep graph" <| fun _ ->
    let graph =
      mkGraph [
        "Calc.add", ["test_add"; "test_sum"]
        "Calc.mul", ["test_mul"] ]
    let tests = [|
      mkTestCase "test_add" TestCategory.Unit
      mkTestCase "test_sum" TestCategory.Unit
      mkTestCase "test_mul" TestCategory.Unit
      mkTestCase "test_unrelated" TestCategory.Unit |]
    let result =
      TestRunExplainer.explainSymbolChange
        graph tests Map.empty Map.empty Map.empty ["Calc.add"] RunTrigger.Keystroke
    result.AffectedTests.Length
    |> Expect.equal "should find 2 affected" 2
    result.TotalDiscovered
    |> Expect.equal "should know total" 4
    result.AffectedTests
    |> Array.map (fun e -> e.DisplayName) |> Array.sort
    |> Expect.equal "should be add and sum" [|"test_add"; "test_sum"|]

  testCase "explainSymbolChange: policy filters out disabled categories" <| fun _ ->
    let graph = mkGraph [ "Svc.call", ["test_int"; "test_unit"] ]
    let tests = [|
      mkTestCase "test_int" TestCategory.Integration
      mkTestCase "test_unit" TestCategory.Unit |]
    let policies = Map.ofList [ TestCategory.Integration, RunPolicy.Disabled ]
    let result =
      TestRunExplainer.explainSymbolChange
        graph tests Map.empty Map.empty policies ["Svc.call"] RunTrigger.Keystroke
    result.AffectedTests.Length
    |> Expect.equal "should only have unit test" 1
    result.AffectedTests.[0].DisplayName
    |> Expect.equal "should be unit test" "test_unit"
    result.FilteredOutByPolicy.Length
    |> Expect.equal "should filter out 1" 1

  testCase "queryTestCoverage: returns covering tests for symbol" <| fun _ ->
    let graph = mkGraph [ "Parser.parse", ["test_parse_ok"; "test_parse_err"] ]
    let tests = [|
      mkTestCase "test_parse_ok" TestCategory.Unit
      mkTestCase "test_parse_err" TestCategory.Unit |]
    let lastResults =
      Map.ofList [
        TestId.TestId "test_parse_ok",
        mkResult "test_parse_ok" (TestResult.Passed (TimeSpan.FromMilliseconds 5.0))
        TestId.TestId "test_parse_err",
        mkResult "test_parse_err"
          (TestResult.Failed (TestFailure.AssertionFailed "nope", TimeSpan.FromMilliseconds 12.0)) ]
    let result =
      TestRunExplainer.queryTestCoverage graph tests lastResults "Parser.parse"
    result.Length |> Expect.equal "should find 2 covering tests" 2

  testCase "queryTestCoverage: returns empty for unknown symbol" <| fun _ ->
    let graph = mkGraph [ "Known.sym", ["test1"] ]
    let tests = [| mkTestCase "test1" TestCategory.Unit |]
    let result =
      TestRunExplainer.queryTestCoverage graph tests Map.empty "Unknown.sym"
    result.Length |> Expect.equal "should be empty" 0

  testCase "explainTest: failed test duration is extracted" <| fun _ ->
    let graph = mkGraph [ "M.f", ["test_fail"] ]
    let tc = mkTestCase "test_fail" TestCategory.Unit
    let lastResults =
      Map.ofList [
        TestId.TestId "test_fail",
        mkResult "test_fail"
          (TestResult.Failed
            (TestFailure.AssertionFailed "bad", TimeSpan.FromMilliseconds 99.0)) ]
    let result =
      TestRunExplainer.explainTest
        graph lastResults Map.empty ["M.f"] RunTrigger.ExplicitRun tc
    result.DurationMs
    |> Expect.equal "should extract failed duration" (Some 99.0)

  testCase "explainTest: skipped test has no duration" <| fun _ ->
    let graph = mkGraph [ "M.g", ["test_skip"] ]
    let tc = mkTestCase "test_skip" TestCategory.Unit
    let lastResults =
      Map.ofList [
        TestId.TestId "test_skip",
        mkResult "test_skip" (TestResult.Skipped "not applicable") ]
    let result =
      TestRunExplainer.explainTest
        graph lastResults Map.empty ["M.g"] RunTrigger.Keystroke tc
    result.DurationMs |> Expect.isNone "skipped has no duration"
]
