module SageFs.Tests.FlakyClassificationTests

open System
open Expecto
open Expecto.Flip
open SageFs.Features.LiveTesting

// --- Helpers ---

let private tid name = TestId.TestId name

let private mkWindow (outcomes: TestOutcome list) =
  let mutable w = ResultWindow.create 10
  for o in outcomes do
    w <- ResultWindow.add o w
  w

let private mkFlakyWindow () =
  // alternating pass/fail = flaky
  mkWindow [ TestOutcome.Pass; TestOutcome.Fail; TestOutcome.Pass; TestOutcome.Fail; TestOutcome.Pass; TestOutcome.Fail ]

let private mkStableWindow () =
  mkWindow [ TestOutcome.Pass; TestOutcome.Pass; TestOutcome.Pass; TestOutcome.Pass ]

let private mkResult testName (result: TestResult) : TestRunResult =
  { TestId = tid testName
    TestName = testName
    Result = result
    Timestamp = DateTimeOffset.UtcNow
    Output = None }

let private fsCheckMsg original shrunk =
  sprintf "Falsifiable, after 42 tests\nOriginal:\n%s\nShrunk:\n%s" original shrunk

let private fsCheckMsgNoShrink original =
  sprintf "Falsifiable, after 10 tests\nOriginal:\n%s" original

// --- isFsCheckFailure tests ---

[<Tests>]
let isFsCheckTests = testList "FlakyDetection.isFsCheckFailure" [

  testCase "detects FsCheck with shrunk counterexample" <| fun _ ->
    let msg = fsCheckMsg "(42, \"hello\")" "(0, \"\")"
    FlakyDetection.isFsCheckFailure msg
    |> Expect.isSome "should detect FsCheck"
    FlakyDetection.isFsCheckFailure msg
    |> Option.get
    |> Expect.equal "should return shrunk value" "(0, \"\")"

  testCase "detects FsCheck without shrunk (original only)" <| fun _ ->
    let msg = fsCheckMsgNoShrink "(99, true)"
    FlakyDetection.isFsCheckFailure msg
    |> Expect.isSome "should detect FsCheck"
    FlakyDetection.isFsCheckFailure msg
    |> Option.get
    |> Expect.equal "should return original" "(99, true)"

  testCase "returns None for non-FsCheck failure" <| fun _ ->
    FlakyDetection.isFsCheckFailure "Expected 42 but got 0"
    |> Expect.isNone "should not detect FsCheck"

  testCase "returns None for empty string" <| fun _ ->
    FlakyDetection.isFsCheckFailure ""
    |> Expect.isNone "should not detect FsCheck on empty"

  testCase "returns None for null-like whitespace" <| fun _ ->
    FlakyDetection.isFsCheckFailure "   "
    |> Expect.isNone "should not detect FsCheck on whitespace"

  testCase "returns None for assertion-style failure" <| fun _ ->
    FlakyDetection.isFsCheckFailure "should be 42.\nexpected: 42\n  actual: 0"
    |> Expect.isNone "should not match Expecto assertion"
]

// --- classifyFlakiness tests ---

[<Tests>]
let classifyFlakinessTests = testList "FlakyDetection.classifyFlakiness" [

  testCase "Insufficient when no history" <| fun _ ->
    FlakyDetection.classifyFlakiness (tid "t1") Map.empty Map.empty
    |> Expect.equal "no history = insufficient" FlakyClassification.Insufficient

  testCase "Stable when all passes" <| fun _ ->
    let history = Map.ofList [ tid "t1", mkStableWindow () ]
    FlakyDetection.classifyFlakiness (tid "t1") history Map.empty
    |> Expect.equal "consistent passes = stable" FlakyClassification.Stable

  testCase "Environmental when flaky without FsCheck" <| fun _ ->
    let history = Map.ofList [ tid "t1", mkFlakyWindow () ]
    let results =
      Map.ofList [
        tid "t1",
        mkResult "t1" (TestResult.Failed (TestFailure.AssertionFailed "timeout", TimeSpan.FromMilliseconds 100.0)) ]
    match FlakyDetection.classifyFlakiness (tid "t1") history results with
    | FlakyClassification.Environmental n ->
      (n, 0) |> Expect.isGreaterThan "should have flips"
    | other -> failtest (sprintf "expected Environmental, got %A" other)

  testCase "PropertyCounterexample when flaky with FsCheck message" <| fun _ ->
    let history = Map.ofList [ tid "t1", mkFlakyWindow () ]
    let msg = fsCheckMsg "(42, \"bad\")" "(0, \"\")"
    let results =
      Map.ofList [
        tid "t1",
        mkResult "t1" (TestResult.Failed (TestFailure.AssertionFailed msg, TimeSpan.FromMilliseconds 50.0)) ]
    match FlakyDetection.classifyFlakiness (tid "t1") history results with
    | FlakyClassification.PropertyCounterexample ce ->
      ce |> Expect.equal "should have shrunk counterexample" "(0, \"\")"
    | other -> failtest (sprintf "expected PropertyCounterexample, got %A" other)

  testCase "PropertyCounterexample with no shrink" <| fun _ ->
    let history = Map.ofList [ tid "t1", mkFlakyWindow () ]
    let msg = fsCheckMsgNoShrink "(99, true)"
    let results =
      Map.ofList [
        tid "t1",
        mkResult "t1" (TestResult.Failed (TestFailure.AssertionFailed msg, TimeSpan.FromMilliseconds 50.0)) ]
    match FlakyDetection.classifyFlakiness (tid "t1") history results with
    | FlakyClassification.PropertyCounterexample ce ->
      ce |> Expect.equal "should have original counterexample" "(99, true)"
    | other -> failtest (sprintf "expected PropertyCounterexample, got %A" other)

  testCase "Stable overrides FsCheck — consistent failure is not flaky" <| fun _ ->
    // All failures = stable (not flaky), even if the last message looks like FsCheck
    let allFails = mkWindow [ TestOutcome.Fail; TestOutcome.Fail; TestOutcome.Fail; TestOutcome.Fail ]
    let history = Map.ofList [ tid "t1", allFails ]
    let msg = fsCheckMsg "(1, 2)" "(0, 0)"
    let results =
      Map.ofList [
        tid "t1",
        mkResult "t1" (TestResult.Failed (TestFailure.AssertionFailed msg, TimeSpan.FromMilliseconds 10.0)) ]
    FlakyDetection.classifyFlakiness (tid "t1") history results
    |> Expect.equal "consistent failures = stable, not property counterexample" FlakyClassification.Stable

  testCase "Environmental when ExceptionThrown (not FsCheck)" <| fun _ ->
    let history = Map.ofList [ tid "t1", mkFlakyWindow () ]
    let results =
      Map.ofList [
        tid "t1",
        mkResult "t1"
          (TestResult.Failed
            (TestFailure.ExceptionThrown ("System.TimeoutException: timed out", "stack"), TimeSpan.FromMilliseconds 5000.0)) ]
    match FlakyDetection.classifyFlakiness (tid "t1") history results with
    | FlakyClassification.Environmental _ -> ()
    | other -> failtest (sprintf "expected Environmental, got %A" other)
]
