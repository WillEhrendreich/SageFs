module SageFs.Tests.LiveTestingDecisionScenarioTests

open Expecto
open Expecto.Flip
open SageFs.Features.LiveTesting

let unitTest fullName =
  { Id = TestId.create fullName TestFramework.Expecto
    FullName = fullName
    DisplayName = fullName
    Origin = TestOrigin.ReflectionOnly
    Labels = []
    Framework = TestFramework.Expecto
    Category = TestCategory.Unit }

[<Tests>]
let tests =
  testList "Live testing decision scenarios" [
    testCase "when a changed symbol maps directly to a small set of unit tests, the decision should preserve an exact rerun explanation so developers know the system stayed surgical" <| fun _ ->
      let selected = [| "Module.Tests.should_add" |]
      let decision =
        LiveTestingDecision.fromSelection
          (RerunCause.KeystrokeBuffered "src/Module.fs")
          SelectionPrecision.ExactDependencyMatch
          [ "Module.add" ]
          selected
          [||]
          "Changed symbol mapped directly to impacted unit tests."

      decision.Explanation.Precision
      |> Expect.equal "exact graph matches should stay exact" SelectionPrecision.ExactDependencyMatch
      decision.Trust
      |> Expect.equal "exact graph matches should earn exact trust" FreshnessTrust.FreshExact
      decision.Explanation.SelectedTests
      |> Expect.equal "exact decisions should keep the impacted test set small and explicit" selected

    testCase "when dependency analysis cannot localize a compiled-file change, the decision should say conservative fallback so broad reruns are never mistaken for precision" <| fun _ ->
      let selected =
        [| "Module.Tests.should_add"
           "Module.Tests.should_validate" |]
      let decision =
        LiveTestingDecision.fromSelection
          (RerunCause.FileSaved "src/Module.fs")
          SelectionPrecision.ConservativeFallback
          [ "Module.newSymbol" ]
          selected
          [||]
          "Compiled-file change could not be narrowed, so all discovered tests were rerun conservatively."

      decision.Explanation.Precision
      |> Expect.equal "fallback must stay explicit in the model" SelectionPrecision.ConservativeFallback
      decision.Trust
      |> Expect.equal "fallback remains useful but should not claim exact trust" FreshnessTrust.FreshApproximate

    testCase "when policy intentionally defers execution, the decision should preserve the affected tests as deferred so silence is not confused with success" <| fun _ ->
      let deferred = [| "Architecture.Tests.should_enforce_boundaries" |]
      let decision =
        LiveTestingDecision.fromSelection
          (RerunCause.KeystrokeBuffered "src/Architecture.fs")
          SelectionPrecision.SuppressedByPolicy
          [ "Architecture.Rule" ]
          [||]
          deferred
          "Architecture tests are save-only, so the edit marks them stale instead of rerunning immediately."

      decision.Explanation.SelectedTests
      |> Expect.equal "policy suppression should not pretend anything reran" [||]
      decision.Explanation.DeferredTests
      |> Expect.equal "policy suppression should preserve the tests that still need attention" deferred
      decision.Trust
      |> Expect.equal "suppressed ambient work should not look fresh" FreshnessTrust.Suppressed

    testCase "when no impacted tests are found, the decision should say so plainly so an empty rerun is distinguishable from a failed analysis" <| fun _ ->
      let decision =
        LiveTestingDecision.fromSelection
          (RerunCause.FileSaved "src/WhitespaceOnly.fs")
          SelectionPrecision.NoImpactedTests
          []
          [||]
          [||]
          "No semantically affected tests were identified for this save."

      decision.Explanation.Precision
      |> Expect.equal "absence of impacted tests should be explicit" SelectionPrecision.NoImpactedTests
      decision.Trust
      |> Expect.equal "no impacted tests should not masquerade as fresh verification" FreshnessTrust.StaleAwaitingRerun
  ]
