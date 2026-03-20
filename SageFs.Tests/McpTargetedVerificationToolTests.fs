module SageFs.Tests.McpTargetedVerificationToolTests

open Expecto
open Expecto.Flip
open SageFs
open SageFs.McpTools
open SageFs.Features.LiveTesting

[<Tests>]
let tests =
  testList "MCP targeted verification support" [
    testCase "run_tests exact prefix selects one exact discovered test" <| fun _ ->
      let discovered =
        [| { TestCase.Id = TestId.TestId "Tests.UserPreferences.guard"
             FullName = "Tests.UserPreferences.guard"
             DisplayName = "guard"
             Origin = TestOrigin.ReflectionOnly
             Labels = []
             Framework = TestFramework.Expecto
             Category = TestCategory.Unit } |]
      let state =
        { LiveTestState.empty with
            Activation = LiveTestingActivation.Active
            DiscoveredTests = discovered }
      match selectTestsForExplicitRun state (Some "exact:Tests.UserPreferences.guard") None with
      | Ok tests -> tests.Length |> Expect.equal "should run exactly one test" 1
      | Error other -> failtestf "expected exact match, got %A" other

    testCase "run_tests exact prefix reports a clear miss" <| fun _ ->
      let state =
        { LiveTestState.empty with
            Activation = LiveTestingActivation.Active
            DiscoveredTests = [||] }
      match selectTestsForExplicitRun state (Some "exact:Tests.UserPreferences.guard") None with
      | Error (RunTestsResult.NoExactTestMatched (name, _)) ->
        name |> Expect.equal "should preserve exact name" "Tests.UserPreferences.guard"
      | other -> failtestf "expected exact miss, got %A" other
  ]
