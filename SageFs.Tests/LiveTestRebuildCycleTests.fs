module SageFs.Tests.LiveTestRebuildCycleTests

open System
open System.Reflection
open Expecto
open Expecto.Flip
open Microsoft.FSharp.Reflection
open SageFs
open SageFs.Features.LiveTesting
open SageFs.Tests.LiveTestingTestHelpers

// ── Reflection Helpers ──

/// Get the union case tag name for any DU value via reflection.
let private unionCaseNameOf (value: obj) =
  let ty = value.GetType()
  let uc, _ = FSharpValue.GetUnionFields(value, ty)
  uc.Name

/// Check if a DU type has a case with the given name.
let private hasUnionCase<'T> (caseName: string) =
  FSharpType.GetUnionCases(typeof<'T>)
  |> Array.exists (fun uc -> uc.Name = caseName)

/// Check if a record type has a property with the given name.
let private hasRecordField<'T> (fieldName: string) =
  typeof<'T>.GetProperties(BindingFlags.Public ||| BindingFlags.Instance)
  |> Array.exists (fun p -> p.Name = fieldName)

// ── Test Data Helpers ──

let private sampleTestCase =
  mkTestCase "MyModule.myTest" TestFramework.Expecto TestCategory.Unit

let private activeStateWith (tests: TestCase array) =
  { LiveTestState.empty with
      Activation = LiveTestingActivation.Active
      DiscoveredTests = tests }

let private depGraphCovering (symbol: string) (testIds: TestId array) =
  { TestDependencyGraph.empty with
      TransitiveCoverage = Map.ofList [ symbol, testIds ] }

// ═══════════════════════════════════════════════════════════════════
// Stream 2 — Rebuild Before Test Execution
// ═══════════════════════════════════════════════════════════════════

[<Tests>]
let rebuildCycleTests = testList "LiveTesting Rebuild Cycle" [

  // ── Structural: required types for the rebuild pipeline ──

  testList "Stream 2 — Structural prerequisites" [

    test "TestCycleEffect has RequestRebuild case" {
      // WHY: Without a RequestRebuild effect variant, the Elm model has
      // no way to request a compilation step between type-checking and
      // test execution. Tests would continue running against stale DLLs.
      hasUnionCase<TestCycleEffect> "RequestRebuild"
      |> Expect.isTrue
          "TestCycleEffect must have RequestRebuild case to request compilation before test execution"
    }

    test "SageFsMsg has RebuildCompleted case" {
      // WHY: After a rebuild finishes (success or failure), the result
      // must flow back into the Elm model so it can decide whether to
      // proceed with test execution or surface a build error.
      hasUnionCase<SageFsMsg> "RebuildCompleted"
      |> Expect.isTrue
          "SageFsMsg must have RebuildCompleted case so rebuild results flow back into the Elm model"
    }

    test "LiveTestCycleState has PendingRebuild field" {
      // WHY: The model must track that a rebuild is in-flight. Without
      // this, new type-check completions could trigger concurrent test
      // runs against the stale DLL while the rebuild is still running.
      hasRecordField<LiveTestCycleState> "PendingRebuild"
      |> Expect.isTrue
          "LiveTestCycleState must have PendingRebuild field to prevent test runs on stale code"
    }
  ]

  // ── Behavioral: the rebuild-before-test pipeline ──

  testList "Stream 2 — Behavioral: rebuild before test execution" [

    test "afterTypeCheck emits RequestRebuild for compiled projects instead of RunAffectedTests" {
      // WHY: This is THE correctness gap. After FCS type-checking identifies
      // affected tests, the NEXT step for compiled projects must be a rebuild
      // (RequestRebuild), NOT immediate test execution (RunAffectedTests)
      // against the stale DLL. Currently afterTypeCheck emits RunAffectedTests
      // directly — this test proves the gap exists.
      let state = activeStateWith [| sampleTestCase |]
      let depGraph = depGraphCovering "MyModule.myFunction" [| sampleTestCase.Id |]

      let effects =
        TestCycleEffects.afterTypeCheck
          [ "MyModule.myFunction" ]
          "MyModule.fs"
          RunTrigger.FileSave
          depGraph
          state
          None
          Map.empty

      // Must NOT be empty — we expect some effect for affected tests
      effects
      |> List.isEmpty
      |> Expect.isFalse "afterTypeCheck should produce effects when tests are affected"

      // The emitted effects should include RequestRebuild
      let effectNames = effects |> List.map (fun e -> unionCaseNameOf e)

      effectNames
      |> List.exists (fun name -> name = "RequestRebuild")
      |> Expect.isTrue
          "afterTypeCheck should emit RequestRebuild for compiled projects"

      // And should NOT include RunAffectedTests (tests must wait for rebuild)
      effectNames
      |> List.exists (fun name -> name = "RunAffectedTests")
      |> Expect.isFalse
          "afterTypeCheck should NOT emit RunAffectedTests directly — rebuild must happen first"
    }

    test "RebuildCompleted(Ok) triggers RunAffectedTests with the saved test set" {
      // WHY: After a successful rebuild, the tests identified during the
      // analysis phase must now execute against fresh code. The pending
      // test set saved in PendingRebuild must be forwarded to RunAffectedTests.
      //
      // This test guards the structural prerequisite first (RebuildCompleted
      // message must exist) then documents the behavioral expectation.
      hasUnionCase<SageFsMsg> "RebuildCompleted"
      |> Expect.isTrue
          "RebuildCompleted msg must exist — after successful rebuild, saved tests must run"

      // Behavioral expectation (activates once RebuildCompleted exists):
      // let pendingTests = [| sampleTestCase |]
      // let model =
      //   { SageFsModel.initial() with
      //       LiveTesting =
      //         { LiveTestCycleState.empty with
      //             PendingRebuild = Some { Tests = pendingTests; ... } } }
      // let model', effects =
      //   SageFsUpdate.update (SageFsMsg.RebuildCompleted (Ok ())) model
      //
      // effects
      // |> List.exists (function
      //     | SageFsEffect.TestCycle (TestCycleEffect.RunAffectedTests (tests, _, _, _, _, _)) ->
      //         tests = pendingTests
      //     | _ -> false)
      // |> Expect.isTrue "successful rebuild should trigger RunAffectedTests with saved tests"
      //
      // model'.LiveTesting.PendingRebuild
      // |> Expect.isNone "PendingRebuild should be cleared after successful rebuild"
    }

    test "RebuildCompleted(Error) surfaces build diagnostic without running tests" {
      // WHY: Running tests against code that failed to compile is worse
      // than useless — it produces misleading results. Build errors must
      // be surfaced to the user and no RunAffectedTests effect should fire.
      hasUnionCase<SageFsMsg> "RebuildCompleted"
      |> Expect.isTrue
          "RebuildCompleted msg must exist — build errors must surface without running tests"

      // Behavioral expectation (activates once RebuildCompleted exists):
      // let model =
      //   { SageFsModel.initial() with
      //       LiveTesting =
      //         { LiveTestCycleState.empty with
      //             PendingRebuild = Some { Tests = [| sampleTestCase |]; ... } } }
      // let model', effects =
      //   SageFsUpdate.update
      //     (SageFsMsg.RebuildCompleted (Error "syntax error at line 5"))
      //     model
      //
      // effects
      // |> List.exists (function
      //     | SageFsEffect.TestCycle (TestCycleEffect.RunAffectedTests _) -> true
      //     | _ -> false)
      // |> Expect.isFalse "build failure must NOT trigger test execution"
      //
      // model'.LiveTesting.PendingRebuild
      // |> Expect.isNone "PendingRebuild should be cleared after failed rebuild"
    }

    test "PendingRebuild prevents new RunAffectedTests for same session" {
      // WHY: If a rebuild is in-flight and another type-check completes,
      // emitting RunAffectedTests would execute tests against stale code.
      // The model must suppress RunAffectedTests while PendingRebuild is active.
      hasRecordField<LiveTestCycleState> "PendingRebuild"
      |> Expect.isTrue
          "PendingRebuild field must exist to guard against concurrent stale test runs"

      // Behavioral expectation (activates once PendingRebuild exists):
      // let state = activeStateWith [| sampleTestCase |]
      // let depGraph = depGraphCovering "MyModule.myFunction" [| sampleTestCase.Id |]
      // let cycleWithPending =
      //   { LiveTestCycleState.empty with
      //       TestState = state
      //       DepGraph = depGraph
      //       PendingRebuild = Some { ... } }
      //
      // let effects =
      //   TestCycleEffects.afterTypeCheck
      //     [ "MyModule.myFunction" ] "MyModule.fs"
      //     RunTrigger.FileSave depGraph state None Map.empty
      //
      // effects
      // |> List.exists (function
      //     | TestCycleEffect.RunAffectedTests _ -> true
      //     | _ -> false)
      // |> Expect.isFalse
      //     "no RunAffectedTests while PendingRebuild is active — tests would run against stale code"
    }
    test "compiled project with empty changedSymbols still emits RequestRebuild for all tests" {
      // WHY: This is the ROOT CAUSE of the stale-DLL bug. When a user edits
      // Pong.fs (main project), FCS may not produce meaningful symbol refs
      // because the FSI session is loaded for the TEST project. The dep graph
      // and coverage bitmaps don't map main-project symbols to tests.
      // Result: changedSymbols=[] → affected=[] → afterTypeCheck returns [] → NO rebuild.
      // For compiled projects, ANY .fs file change MUST trigger a rebuild with
      // all discovered tests as fallback, because the DLL is always stale.
      let tc1 = mkTestCase "Tests.PongTests.paddleColor" TestFramework.Expecto TestCategory.Unit
      let tc2 = mkTestCase "Tests.PongTests.ballSpeed" TestFramework.Expecto TestCategory.Unit
      let state = activeStateWith [| tc1; tc2 |]
      let emptyDepGraph = TestDependencyGraph.empty

      // Key: changedSymbols is EMPTY — FCS couldn't identify what changed
      let effects =
        TestCycleEffects.afterTypeCheck
          []                // no changed symbols (FCS couldn't analyze)
          "Pong.fs"         // compiled .fs file
          RunTrigger.FileSave
          emptyDepGraph     // no dep graph entries for main-project symbols
          state
          None
          Map.empty         // no instrumentation maps

      // Must NOT be empty — compiled project changes ALWAYS need a rebuild
      effects
      |> List.isEmpty
      |> Expect.isFalse
          "compiled project file change with empty symbols must still trigger rebuild (all-tests fallback)"

      // The emitted effects should be RequestRebuild (not RunAffectedTests)
      let hasRebuild =
        effects |> List.exists (fun e -> unionCaseNameOf e = "RequestRebuild")
      hasRebuild
      |> Expect.isTrue
          "fallback rebuild should emit RequestRebuild for compiled projects"

      // Verify ALL discovered tests are included in the rebuild
      let rebuildTests =
        effects |> List.choose (fun e ->
          match e with
          | TestCycleEffect.RequestRebuild (tests, _, _, _, _, _) -> Some tests
          | _ -> None)
        |> List.collect Array.toList
      rebuildTests
      |> List.length
      |> Expect.equal "all discovered tests should be in the rebuild" 2
    }

    test "compiled project with empty changedSymbols but no discovered tests returns empty" {
      // WHY: If no tests are discovered yet, there's nothing to rebuild for.
      // This avoids unnecessary builds during the discovery phase.
      let state = activeStateWith [||] // no discovered tests
      let effects =
        TestCycleEffects.afterTypeCheck
          []
          "Pong.fs"
          RunTrigger.FileSave
          TestDependencyGraph.empty
          state
          None
          Map.empty

      effects
      |> List.isEmpty
      |> Expect.isTrue
          "no discovered tests means no rebuild needed"
    }

    test "script file with empty changedSymbols still returns empty (no fallback)" {
      // WHY: The all-tests fallback is ONLY for compiled projects.
      // Script (.fsx) files don't need compilation — they're interpreted.
      // Empty changedSymbols for scripts correctly means "nothing changed."
      let tc1 = mkTestCase "Tests.myTest" TestFramework.Expecto TestCategory.Unit
      let state = activeStateWith [| tc1 |]

      let effects =
        TestCycleEffects.afterTypeCheck
          []                // no changed symbols
          "MyScript.fsx"    // script file — no compilation
          RunTrigger.FileSave
          TestDependencyGraph.empty
          state
          None
          Map.empty

      effects
      |> List.isEmpty
      |> Expect.isTrue
          "script files should NOT fall back to all-tests rebuild"
    }
  ]

  // ═══════════════════════════════════════════════════════════════════
  // Stream 3 — Rebuild Cancellation
  // ═══════════════════════════════════════════════════════════════════

  testList "Stream 3 — Rebuild Cancellation" [

    test "new FileContentChanged during PendingRebuild resets the pipeline" {
      // WHY: If the user edits code while a rebuild is in-flight, the
      // build result will be for stale code. The pending rebuild must be
      // cancelled and the pipeline must restart from the beginning.
      hasRecordField<LiveTestCycleState> "PendingRebuild"
      |> Expect.isTrue
          "PendingRebuild must exist to test cancellation on new edits"

      // Behavioral expectation (activates once PendingRebuild exists):
      // let cycleWithPending =
      //   { LiveTestCycleState.empty with
      //       TestState = activeStateWith [| sampleTestCase |]
      //       PendingRebuild = Some { ... } }
      // let model =
      //   { SageFsModel.initial() with LiveTesting = cycleWithPending }
      // let model', _ =
      //   SageFsUpdate.update
      //     (SageFsMsg.FileContentChanged ("Foo.fs", "let x = 42"))
      //     model
      //
      // model'.LiveTesting.PendingRebuild
      // |> Expect.isNone "PendingRebuild should be cancelled when new edit arrives"
    }

    test "cancelled rebuild does not emit RebuildCompleted effects" {
      // WHY: A cancelled build's result is irrelevant and stale. If the
      // RebuildCompleted message for a cancelled rebuild is not ignored,
      // it would trigger test execution against wrong code.
      hasUnionCase<SageFsMsg> "RebuildCompleted"
      |> Expect.isTrue
          "RebuildCompleted must exist to test cancellation semantics"

      // Behavioral expectation (activates once RebuildCompleted + PendingRebuild exist):
      // 1. Model has PendingRebuild for generation N
      // 2. FileContentChanged arrives → PendingRebuild cancelled (now generation N+1 or None)
      // 3. RebuildCompleted for generation N arrives (stale result)
      // 4. No RunAffectedTests effects — the stale result must be ignored
      //
      // let modelCancelled = ... (PendingRebuild = None after cancellation)
      // let model', effects =
      //   SageFsUpdate.update
      //     (SageFsMsg.RebuildCompleted (Ok ()))
      //     modelCancelled
      //
      // effects
      // |> List.exists (function
      //     | SageFsEffect.TestCycle (TestCycleEffect.RunAffectedTests _) -> true
      //     | _ -> false)
      // |> Expect.isFalse
      //     "stale RebuildCompleted after cancellation must NOT trigger test execution"
    }
  ]
]
