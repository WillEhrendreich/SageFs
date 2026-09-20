module SageFs.Tests.LiveTestingEvalDecisionTests

/// Brief 4 (live-testing-asyoutype-plan.md) — the daemon eval-then-affected
/// WIRING decision. Pure tests only: `TestCycleEffects.redirectToEvalBuffer`
/// (the transform that retargets the compiled-DLL decision into an FSI eval)
/// and its composition with the pre-existing `decideAfterTypeCheck` (which
/// this brief deliberately leaves UNCHANGED — see
/// `LiveTestingAfterTypeCheckScenarioTests.fs`, still green, still asserting
/// the OLD `RunAffectedTests`/`RequestRebuild` shape for callers that don't
/// redirect).
open Expecto
open Expecto.Flip
open FsCheck
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

/// Drives `decideAfterTypeCheck` then `redirectToEvalBuffer` over its
/// effects, exactly as `LiveTestCycleState.handleFcsResult`'s `Success`
/// branch now does in production (SageFs/SageFsApp.fs).
let decideThenRedirect changedSymbols filePath trigger graph state content =
  let outcome =
    TestCycleEffects.decideAfterTypeCheck
      (FileSymbolDelta.ofChangedOnly changedSymbols) filePath trigger graph state None Map.empty
  TestCycleEffects.redirectToEvalBuffer content filePath outcome.Effects

[<Tests>]
let tests =
  testList "LiveTestingEvalDecisionTests — Brief 4 eval-then-affected redirect" [

    testCase "redirectToEvalBuffer: a RunAffectedTests effect for a compiled .fs file with known content becomes EvalBufferThenRunAffected, carrying the same Tests/Trigger/SessionId and the given FilePath/Content" <| fun _ ->
      let tc = mkTest "Module.Tests.should_add" TestCategory.Unit
      let req =
        { TestRunRequest.empty with
            Tests = [| tc |]
            Trigger = RunTrigger.Keystroke
            SessionId = Some "sess-1" }
      let effects = [ TestCycleEffect.RunAffectedTests req ]

      let redirected = TestCycleEffects.redirectToEvalBuffer (Some "let x = 1") "Module.fs" effects

      match redirected with
      | [ TestCycleEffect.EvalBufferThenRunAffected evalReq ] ->
        evalReq.FilePath |> Expect.equal "the changed file path is carried through" "Module.fs"
        evalReq.Content |> Expect.equal "the known buffer content is carried through" "let x = 1"
        evalReq.Run |> Expect.equal "the original TestRunRequest is carried through unchanged" req
      | other -> failtestf "expected exactly one EvalBufferThenRunAffected effect, got %A" other

    testCase "redirectToEvalBuffer: a RequestRebuild effect for a compiled .fs file with known content ALSO becomes EvalBufferThenRunAffected (FileSave/ExplicitRun no longer rebuilds on the live-edit path)" <| fun _ ->
      let tc = mkTest "Module.Tests.should_add" TestCategory.Unit
      let req =
        { TestRunRequest.empty with
            Tests = [| tc |]
            Trigger = RunTrigger.FileSave
            SessionId = Some "sess-1" }
      let effects = [ TestCycleEffect.RequestRebuild(3L, req) ]

      let redirected = TestCycleEffects.redirectToEvalBuffer (Some "let x = 1") "Module.fs" effects

      match redirected with
      | [ TestCycleEffect.EvalBufferThenRunAffected evalReq ] ->
        evalReq.Run |> Expect.equal "the rebuild's TestRunRequest survives the redirect intact" req
      | other -> failtestf "expected RequestRebuild to redirect to EvalBufferThenRunAffected, got %A" other

    testCase "redirectToEvalBuffer: an .fsx file is NEVER redirected, even with known content — it passes through unchanged" <| fun _ ->
      let tc = mkTest "Script.Tests.should_run" TestCategory.Unit
      let req = { TestRunRequest.empty with Tests = [| tc |] }
      let effects = [ TestCycleEffect.RunAffectedTests req ]

      let redirected = TestCycleEffects.redirectToEvalBuffer (Some "1 + 1") "Script.fsx" effects

      redirected |> Expect.equal "an .fsx effect list is returned byte-for-byte unchanged" effects

    testCase "redirectToEvalBuffer: a compiled .fs file with NO known content (content = None) passes through unchanged — never fabricates an eval from nothing" <| fun _ ->
      let tc = mkTest "Module.Tests.should_add" TestCategory.Unit
      let req = { TestRunRequest.empty with Tests = [| tc |] }
      let effects = [ TestCycleEffect.RequestRebuild(0L, req) ]

      let redirected = TestCycleEffects.redirectToEvalBuffer None "Module.fs" effects

      redirected |> Expect.equal "with no content known, the original effect is untouched" effects

    testCase "redirectToEvalBuffer: a decision with no run effect (e.g. suppressed/no-impact) stays effect-free" <| fun _ ->
      let redirected = TestCycleEffects.redirectToEvalBuffer (Some "code") "Module.fs" []
      redirected |> Expect.isEmpty "an empty effect list has nothing to redirect"

    testCase "WHY — Keystroke on a compiled .fs file with type-check OK produces EvalBufferThenRunAffected, NOT RunAffectedTests on the compiled DLL and NOT RequestRebuild — this is the exact bug (proof #1) the brief fixes" <| fun _ ->
      let impacted = mkTest "Module.Tests.should_add" TestCategory.Unit
      let state =
        { LiveTestState.empty with
            Activation = LiveTestingActivation.Active
            DiscoveredTests = [| impacted |] }
      let graph = exactGraph [ "Module.add", [| impacted.Id |] ]

      let redirected =
        decideThenRedirect [ "Module.add" ] "Module.fs" RunTrigger.Keystroke graph state (Some "module M\nlet add x = x")

      match redirected with
      | [ TestCycleEffect.EvalBufferThenRunAffected req ] ->
        req.FilePath |> Expect.equal "carries the edited file" "Module.fs"
        req.Run.Tests |> Array.map (fun tc -> tc.Id)
        |> Expect.equal "the affected set is exactly the coverage/graph-selected subset decideAfterTypeCheck chose — not the whole suite" [| impacted.Id |]
      | other -> failtestf "expected EvalBufferThenRunAffected for a Keystroke on a compiled file, got %A" other

    testCase "WHY — FileSave on a compiled .fs file with type-check OK produces EvalBufferThenRunAffected too, NOT RequestRebuild — RequestRebuild is retired from the live-edit path (proof #2)" <| fun _ ->
      let impacted = mkTest "Module.Tests.should_add" TestCategory.Unit
      let state =
        { LiveTestState.empty with
            Activation = LiveTestingActivation.Active
            DiscoveredTests = [| impacted |] }
      let graph = exactGraph [ "Module.add", [| impacted.Id |] ]

      let redirected =
        decideThenRedirect [ "Module.add" ] "Module.fs" RunTrigger.FileSave graph state (Some "module M\nlet add x = x")

      match redirected with
      | [ TestCycleEffect.EvalBufferThenRunAffected req ] ->
        req.Run.Trigger |> Expect.equal "the FileSave trigger is preserved" RunTrigger.FileSave
      | other -> failtestf "expected EvalBufferThenRunAffected for a FileSave on a compiled file, got %A" other

    testCase "WHY — an .fsx / script file is unaffected by this brief: Keystroke still yields plain RunAffectedTests, never an eval-buffer redirect" <| fun _ ->
      let impacted = mkTest "Script.Tests.should_run" TestCategory.Unit
      let state =
        { LiveTestState.empty with
            Activation = LiveTestingActivation.Active
            DiscoveredTests = [| impacted |] }
      let graph = exactGraph [ "Script.run", [| impacted.Id |] ]

      let redirected =
        decideThenRedirect [ "Script.run" ] "Script.fsx" RunTrigger.Keystroke graph state (Some "1 + 1")

      match redirected with
      | [ TestCycleEffect.RunAffectedTests req ] ->
        req.Tests |> Array.map (fun tc -> tc.Id) |> Expect.equal "affected selection is unchanged for scripts" [| impacted.Id |]
      | other -> failtestf "expected plain RunAffectedTests for an .fsx file, got %A" other

    testPropertyWithConfig
      { FsCheckConfig.defaultConfig with maxTest = 100 }
      "property: redirectToEvalBuffer never changes the number of effects, and only ever turns a RunAffectedTests/RequestRebuild into EvalBufferThenRunAffected for a compiled file with known content" <| fun (hasContent: bool) (isFsx: bool) ->
        let tc = mkTest "Module.Tests.should_add" TestCategory.Unit
        let req = { TestRunRequest.empty with Tests = [| tc |] }
        let filePath = match isFsx with true -> "Module.fsx" | false -> "Module.fs"
        let content = match hasContent with true -> Some "code" | false -> None
        let original = [ TestCycleEffect.RunAffectedTests req ]

        let redirected = TestCycleEffects.redirectToEvalBuffer content filePath original

        let countPreserved = redirected.Length = original.Length
        let shouldRedirect = hasContent && not isFsx
        let didRedirect =
          match redirected with
          | [ TestCycleEffect.EvalBufferThenRunAffected _ ] -> true
          | _ -> false
        countPreserved && (didRedirect = shouldRedirect)
  ]
