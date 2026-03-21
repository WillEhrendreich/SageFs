module SageFs.Tests.LiveTestingAfterTypeCheckScenarioTests

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

let exactGraph graphEntries =
  { TestDependencyGraph.empty with
      SymbolToTests = Map.ofList graphEntries
      TransitiveCoverage = Map.ofList graphEntries }

[<Tests>]
let tests =
  testList "Live testing afterTypeCheck scenarios" [
    testCase "when only the dependency graph explains the change, afterTypeCheck should report an exact decision so the user can trust the surgical rerun" <| fun _ ->
      let impacted = mkTest "Module.Tests.should_add" TestCategory.Unit
      let state =
        { LiveTestState.empty with
            Activation = LiveTestingActivation.Active
            DiscoveredTests = [| impacted |] }
      let graph = exactGraph [ "Module.add", [| impacted.Id |] ]

      let outcome =
        TestCycleEffects.decideAfterTypeCheck
          [ "Module.add" ]
          "Module.fs"
          RunTrigger.Keystroke
          graph
          state
          None
          Map.empty

      match outcome.Decision, outcome.Effects with
      | Some decision, [ TestCycleEffect.RequestRebuild (_, tests, _, _, _, _, _) ] ->
        decision.Explanation.Precision |> Expect.equal "pure graph selection should stay exact" SelectionPrecision.ExactDependencyMatch
        decision.Trust |> Expect.equal "exact decisions should keep exact trust" FreshnessTrust.FreshExact
        decision.Explanation.SelectedTests |> Expect.equal "the directly impacted test should stay visible" [| impacted.FullName |]
        tests |> Array.map (fun tc -> tc.Id) |> Expect.equal "the rebuild request should target only the impacted test" [| impacted.Id |]
      | other -> failtestf "expected exact decision plus rebuild effect, got %A" other

    testCase "when coverage widens the selection beyond the symbol graph, afterTypeCheck should say approximation out loud so extra reruns are explained instead of feeling random" <| fun _ ->
      let symbolTest = mkTest "Module.Tests.should_add" TestCategory.Unit
      let coverageTest = mkTest "Module.Tests.should_guard_edges" TestCategory.Unit
      let state =
        { LiveTestState.empty with
            Activation = LiveTestingActivation.Active
            DiscoveredTests = [| symbolTest; coverageTest |]
            TestCoverageBitmaps = Map.ofList [ coverageTest.Id, CoverageBitmap.ofBoolArray [| true |] ]
            TestSessionMap = Map.ofList [ symbolTest.Id, "s"; coverageTest.Id, "s" ] }
      let graph = exactGraph [ "Module.add", [| symbolTest.Id |] ]
      let maps =
        [| { Slots = [| { File = "Module.fs"; Line = 1; Column = 0; EndLine = 1; EndColumn = 10; BranchId = 0 } |]
             TotalProbes = 1
             TrackerTypeName = "t"
             HitsFieldName = "h" } |]
      let outcome =
        TestCycleEffects.decideAfterTypeCheck
          [ "Module.add" ]
          "Module.fs"
          RunTrigger.Keystroke
          graph
          state
          None
          (Map.ofList [ "s", maps ])

      match outcome.Decision with
      | Some decision ->
        decision.Explanation.Precision |> Expect.equal "coverage widening should stay visible" SelectionPrecision.CoverageApproximation
        decision.Trust |> Expect.equal "coverage widening should be useful but approximate" FreshnessTrust.FreshApproximate
        decision.Explanation.SelectedTests |> Array.sort |> Expect.equal "both symbol and coverage-selected tests should be named" ([| symbolTest.FullName; coverageTest.FullName |] |> Array.sort)
        outcome.Effects.Length |> Expect.equal "the widened set should still produce one session-local effect" 1
      | None -> failtest "expected a coverage approximation decision"

    testCase "when a compiled file changes but the graph cannot explain it, afterTypeCheck should admit the fallback and queue every discovered test behind rebuild" <| fun _ ->
      let tc1 = mkTest "Compiled.Tests.should_build_a" TestCategory.Unit
      let tc2 = mkTest "Compiled.Tests.should_build_b" TestCategory.Unit
      let state =
        { LiveTestState.empty with
            Activation = LiveTestingActivation.Active
            DiscoveredTests = [| tc1; tc2 |] }

      let outcome =
        TestCycleEffects.decideAfterTypeCheck
          []
          "Compiled.fs"
          RunTrigger.FileSave
          TestDependencyGraph.empty
          state
          None
          Map.empty

      match outcome.Decision, outcome.Effects with
      | Some decision, [ TestCycleEffect.RequestRebuild (_, tests, _, _, _, _, _) ] ->
        decision.Explanation.Precision |> Expect.equal "compiled fallback should be explicit" SelectionPrecision.ConservativeFallback
        decision.Trust |> Expect.equal "fallback should not claim exact trust" FreshnessTrust.FreshApproximate
        decision.Explanation.SelectedTests |> Array.sort |> Expect.equal "all discovered tests should be named in the fallback" ([| tc1.FullName; tc2.FullName |] |> Array.sort)
        tests |> Array.map (fun tc -> tc.FullName) |> Array.sort |> Expect.equal "the rebuild request should carry every discovered test" ([| tc1.FullName; tc2.FullName |] |> Array.sort)
      | other -> failtestf "expected conservative fallback rebuild, got %A" other

    testCase "when run policy suppresses every impacted test, afterTypeCheck should explain the silence so stale calm is not mistaken for correctness" <| fun _ ->
      let archTest = mkTest "Architecture.Tests.should_hold" TestCategory.Architecture
      let state =
        { LiveTestState.empty with
            Activation = LiveTestingActivation.Active
            DiscoveredTests = [| archTest |] }
      let graph = exactGraph [ "Architecture.Rule", [| archTest.Id |] ]

      let outcome =
        TestCycleEffects.decideAfterTypeCheck
          [ "Architecture.Rule" ]
          "Architecture.fs"
          RunTrigger.Keystroke
          graph
          state
          None
          Map.empty

      match outcome.Decision with
      | Some decision ->
        decision.Explanation.Precision |> Expect.equal "policy suppression should stay visible" SelectionPrecision.SuppressedByPolicy
        decision.Trust |> Expect.equal "suppression should not look fresh" FreshnessTrust.Suppressed
        decision.Explanation.DeferredTests |> Expect.equal "the deferred test should remain named" [| archTest.FullName |]
        outcome.Effects |> Expect.isEmpty "suppressed ambient work should not execute"
      | None -> failtest "expected a suppression decision"

    testCase "when nothing is impacted, afterTypeCheck should explain that no rerun was warranted so the absence of work reads as intent rather than a dropped event" <| fun _ ->
      let state =
        { LiveTestState.empty with
            Activation = LiveTestingActivation.Active
            DiscoveredTests = [| mkTest "Module.Tests.should_exist" TestCategory.Unit |] }

      let outcome =
        TestCycleEffects.decideAfterTypeCheck
          [ "Unknown.symbol" ]
          "Module.fsx"
          RunTrigger.Keystroke
          TestDependencyGraph.empty
          state
          None
          Map.empty

      match outcome.Decision with
      | Some decision ->
        decision.Explanation.Precision |> Expect.equal "the model should say no tests were impacted" SelectionPrecision.NoImpactedTests
        decision.Trust |> Expect.equal "no impacted tests should remain stale until the next meaningful run" FreshnessTrust.StaleAwaitingRerun
        outcome.Effects |> Expect.isEmpty "no impacted tests means no work"
      | None -> failtest "expected a no-impacted decision"
  ]
