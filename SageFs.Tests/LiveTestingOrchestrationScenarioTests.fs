module SageFs.Tests.LiveTestingOrchestrationScenarioTests

open Expecto
open Expecto.Flip
open SageFs.Features.LiveTesting

let mkTest fullName category =
  { Id = TestId.create fullName TestFramework.Expecto
    FullName = fullName
    DisplayName = fullName
    Origin = TestOrigin.ReflectionOnly
    Labels = []
    Framework = TestFramework.Expecto
    Category = category }

[<Tests>]
let tests =
  testList "Live testing orchestration scenarios" [
    testCase "when a keystroke touches a unit-tested symbol, orchestration should explain that only the impacted unit tests reran so the inner loop stays surgical" <| fun _ ->
      let unitTest = mkTest "Module.Tests.should_add" TestCategory.Unit
      let state =
        { LiveTestState.empty with
            DiscoveredTests = [| unitTest |]
            Activation = LiveTestingActivation.Active }
      let graph = TestDependencyGraph.fromDirect (Map.ofList [ "Module.add", [| unitTest.Id |] ])

      match TestCycleOrchestrator.decide state RunTrigger.Keystroke [ "Module.add" ] "src/Module.fs" graph with
      | TestCycleDecision.Explained decision ->
        decision.Explanation.Precision |> Expect.equal "the rerun should stay exact" SelectionPrecision.ExactDependencyMatch
        decision.Explanation.SelectedTests |> Expect.equal "only the directly impacted test should rerun" [| unitTest.FullName |]
        decision.Trust |> Expect.equal "exact orchestration decisions should keep exact trust" FreshnessTrust.FreshExact
      | other -> failtestf "expected explained decision, got %A" other

    testCase "when a keystroke affects only save-only tests, orchestration should explain the deferral so stale quietness is not mistaken for correctness" <| fun _ ->
      let archTest = mkTest "Architecture.Tests.should_hold" TestCategory.Architecture
      let state =
        { LiveTestState.empty with
            DiscoveredTests = [| archTest |]
            Activation = LiveTestingActivation.Active }
      let graph = TestDependencyGraph.fromDirect (Map.ofList [ "Architecture.Rule", [| archTest.Id |] ])

      match TestCycleOrchestrator.decide state RunTrigger.Keystroke [ "Architecture.Rule" ] "src/Architecture.fs" graph with
      | TestCycleDecision.Explained decision ->
        decision.Explanation.Precision |> Expect.equal "the model should say policy suppression explicitly" SelectionPrecision.SuppressedByPolicy
        decision.Explanation.DeferredTests |> Expect.equal "the deferred architecture test should remain visible" [| archTest.FullName |]
        decision.Trust |> Expect.equal "policy suppression should not masquerade as fresh truth" FreshnessTrust.Suppressed
      | other -> failtestf "expected explained suppression, got %A" other
  ]
