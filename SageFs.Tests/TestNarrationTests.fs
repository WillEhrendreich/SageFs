module SageFs.Tests.TestNarrationTests

open System
open Expecto
open Expecto.Flip
open SageFs.Features.LiveTesting
open SageFs.Features


[<Tests>]
let narrateFailureTests =
  testList "TestNarration failure stories" [

    testCase "assertion failure tells what went wrong" <| fun _ ->
      let story =
        TestNarration.narrateFailure
          "MyModule.addNumbers"
          (TestFailure.AssertionFailed "expected 42 but got 41")
          TestTimeouts.testElapsedOther
          None
      story |> Expect.stringContains "names the test" "addNumbers"
      story |> Expect.stringContains "includes expected" "expected 42 but got 41"

    testCase "exception failure includes type and message" <| fun _ ->
      let story =
        TestNarration.narrateFailure
          "Parser.tokenize"
          (TestFailure.ExceptionThrown ("NullReferenceException: Object reference not set", "at Parser.tokenize()"))
          TestTimeouts.testElapsed
          None
      story |> Expect.stringContains "has exception" "NullReferenceException"

    testCase "timeout failure shows duration" <| fun _ ->
      let story =
        TestNarration.narrateFailure
          "Network.fetchData"
          (TestFailure.TimedOut TestTimeouts.testTimeLimit)
          TestTimeouts.testTimeLimit
          None
      story |> Expect.stringContains "mentions timeout" "timed out"

    testCase "narrative with causal changes identifies culprit" <| fun _ ->
      let narrative = {
        FailureNarrative.empty with
          CausalChanges = [ CausalChange.SymbolChanged "Calculator.add" ]
          TimeSinceLastPass = Some TestTimeouts.timeSinceLastPass
      }
      let story =
        TestNarration.narrateFailure
          "CalculatorTests.addTest"
          (TestFailure.AssertionFailed "expected 4 but got 3")
          TestTimeouts.testElapsed
          (Some narrative)
      story |> Expect.stringContains "identifies culprit" "Calculator.add"

    testCase "narrative with property violation explains algebra" <| fun _ ->
      let narrative = {
        FailureNarrative.empty with
          PropertyViolation = Some {
            PropertyName = Some "addition is commutative"
            ShrunkCounterexample = "a=1, b=-1"
            AlgebraicCategory = Some "commutativity"
          }
      }
      let story =
        TestNarration.narrateFailure
          "MathProps.commutativity"
          (TestFailure.AssertionFailed "Falsifiable")
          TestTimeouts.testElapsedOther
          (Some narrative)
      story |> Expect.stringContains "mentions property" "commutativity"
      story |> Expect.stringContains "shows counterexample" "a=1, b=-1"
  ]

[<Tests>]
let narrateResultTests =
  testList "TestNarration result summaries" [

    testCase "passed test gets brief celebration" <| fun _ ->
      let story = TestNarration.narrateResult "MyTest.works" (TestResult.Passed TestTimeouts.testElapsed)
      story |> Expect.stringContains "mentions pass" "passed"

    testCase "skipped test explains why" <| fun _ ->
      let story = TestNarration.narrateResult "OldTest.legacy" (TestResult.Skipped "requires database")
      story |> Expect.stringContains "shows reason" "requires database"

    testCase "not-run test says so" <| fun _ ->
      let story = TestNarration.narrateResult "NewTest.pending" TestResult.NotRun
      story |> Expect.stringContains "not run" "not yet run"
  ]

[<Tests>]
let narrateStatusTests =
  testList "TestNarration status labels" [

    testCase "status label for each TestRunStatus" <| fun _ ->
      TestNarration.statusLabel TestRunStatus.Detected |> Expect.equal "detected" "Detected"
      TestNarration.statusLabel TestRunStatus.Queued |> Expect.equal "queued" "Queued"
      TestNarration.statusLabel TestRunStatus.Running |> Expect.equal "running" "Running"
      TestNarration.statusLabel TestRunStatus.Stale |> Expect.equal "stale" "Stale"
      TestNarration.statusLabel TestRunStatus.PolicyDisabled |> Expect.equal "disabled" "Disabled by policy"

    testCase "status label for passed includes timing" <| fun _ ->
      let label = TestNarration.statusLabel (TestRunStatus.Passed FixtureDurations.roundTripDuration)
      label |> Expect.stringContains "has passed" "Passed"
      label |> Expect.stringContains "has timing" (string (int FixtureDurations.roundTripDuration.TotalMilliseconds))

    testCase "status label for failed includes reason" <| fun _ ->
      let label = TestNarration.statusLabel (TestRunStatus.Failed (TestFailure.AssertionFailed "bad", TestTimeouts.testElapsed))
      label |> Expect.stringContains "has failed" "Failed"
  ]

[<Tests>]
let densityTests =
  testList "TestNarration density modes" [

    testCase "minimal narration is brief" <| fun _ ->
      let story =
        TestNarration.narrateAtDensity
          NarrationDetail.Minimal
          "MyTest.works"
          (TestResult.Failed (TestFailure.AssertionFailed "expected 1 got 2", TestTimeouts.testElapsed))
          None
      story.Length < 80 |> Expect.isTrue "should be short"

    testCase "full narration is detailed" <| fun _ ->
      let narrative = {
        FailureNarrative.empty with
          CausalChanges = [ CausalChange.SymbolChanged "Foo.bar" ]
          TimeSinceLastPass = Some TestTimeouts.timeSinceLastPass
      }
      let story =
        TestNarration.narrateAtDensity
          NarrationDetail.Full
          "MyTest.fails"
          (TestResult.Failed (TestFailure.AssertionFailed "expected 1 got 2", TestTimeouts.testElapsed))
          (Some narrative)
      story |> Expect.stringContains "has culprit" "Foo.bar"
  ]
