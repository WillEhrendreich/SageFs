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

let private tryRecordFieldValue (fieldName: string) (value: obj) =
  value.GetType().GetProperty(fieldName, BindingFlags.Public ||| BindingFlags.Instance)
  |> Option.ofObj
  |> Option.map (fun p -> p.GetValue(value))

let private tryOptionValue (value: obj) =
  match isNull value with
  | true -> None
  | false ->
      let ty = value.GetType()
      let case, fields = FSharpValue.GetUnionFields(value, ty)
      match case.Name with
      | "Some" -> fields |> Array.tryHead
      | "None" -> None
      | other -> failtestf "expected option value, got %s" other

let private tryQueuedRebuildValue (state: LiveTestCycleState) =
  tryRecordFieldValue "QueuedRebuild" (box state)
  |> Option.bind tryOptionValue

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

let private pendingRebuildFor (generation: int64) (tests: TestCase array) (trigger: RunTrigger) =
  { Generation = generation
    Tests = tests
    Trigger = trigger
    FilePath = "Foo.fs"
    AnalysisIdentity = None
    TreeSitterElapsed = ts 11.0
    FcsElapsed = ts 29.0
    SessionId = Some "test-session"
    InstrumentationMaps = [||] }

let private pendingRebuildWithProvenance
  (generation: int64)
  (tests: TestCase array)
  (trigger: RunTrigger)
  (sessionId: string option)
  (filePath: string)
  (analysisIdentity: AnalysisIdentity option)
  =
  let values =
    FSharpType.GetRecordFields(typeof<PendingRebuildState>)
    |> Array.map (fun field ->
      match field.Name with
      | "Generation" -> box generation
      | "Tests" -> box tests
      | "Trigger" -> box trigger
      | "TreeSitterElapsed" -> box (ts 11.0)
      | "FcsElapsed" -> box (ts 29.0)
      | "SessionId" -> box sessionId
      | "InstrumentationMaps" -> box (Array.empty<InstrumentationMap>)
      | "FilePath" -> box filePath
      | "AnalysisIdentity" -> box analysisIdentity
      | other -> failtestf "unexpected PendingRebuildState field %s" other)
  FSharpValue.MakeRecord(typeof<PendingRebuildState>, values) :?> PendingRebuildState

let private activeModelWithPending pending =
  { SageFsModel.initial() with
      LiveTesting =
        { LiveTestCycleState.empty with
            TestState = { LiveTestState.empty with Activation = LiveTestingActivation.Active }
            NextRebuildGeneration = pending.Generation
            PendingRebuild = Some pending } }

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

    test "TestCycleEffect has CancelRebuild case" {
      // WHY: Once rebuild work is in-flight, the model needs a first-class
      // way to say "that rebuild intent is no longer true." Without an
      // explicit cancel effect, invalidation is only implicit runtime magic.
      hasUnionCase<TestCycleEffect> "CancelRebuild"
      |> Expect.isTrue
          "TestCycleEffect must have CancelRebuild so stale rebuild work can be invalidated explicitly"
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

    test "LiveTestCycleState has QueuedRebuild field" {
      // WHY: `RunningButEdited` says stale work is in flight, but it does
      // not say what the latest owed compiled rebuild actually is. The
      // queued rebuild intent makes that business fact explicit.
      hasRecordField<LiveTestCycleState> "QueuedRebuild"
      |> Expect.isTrue
          "LiveTestCycleState must carry the latest queued rebuild intent so edits during a run are replayable instead of inferable"
    }

    test "PendingRebuildState has Generation field" {
      // WHY: Cancellation is only a best-effort optimization. The model
      // still needs a semantic identity for each rebuild so stale
      // completions can be rejected even if an older async path runs late.
      hasRecordField<PendingRebuildState> "Generation"
      |> Expect.isTrue
          "PendingRebuildState must carry Generation so rebuild completions can be matched to the right request"
    }

    test "PendingRebuildState has FilePath field" {
      // WHY: A pending rebuild without a source file is just a bag of tests.
      // The model should remember which compiled file triggered the rebuild.
      hasRecordField<PendingRebuildState> "FilePath"
      |> Expect.isTrue
          "PendingRebuildState must carry the source file path so rebuild intent keeps its provenance"
    }

    test "PendingRebuildState has AnalysisIdentity field" {
      // WHY: Rebuild coalescing should be keyed to content truth, not just
      // test lists. The pending rebuild must remember which analyzed content
      // identity it came from.
      hasRecordField<PendingRebuildState> "AnalysisIdentity"
      |> Expect.isTrue
          "PendingRebuildState must carry analysis identity so equivalent rebuild checks can stay content-aware"
    }

    test "SageFsMsg.RebuildCompleted carries generation identity" {
      // WHY: If RebuildCompleted doesn't carry which rebuild finished, the
      // Elm model can't distinguish the latest rebuild from an older stale
      // completion that raced in after supersession.
      let rebuildCompletedFields =
        FSharpType.GetUnionCases(typeof<SageFsMsg>)
        |> Array.find (fun uc -> uc.Name = "RebuildCompleted")
        |> fun uc -> uc.GetFields()

      rebuildCompletedFields.Length
      |> Expect.equal
          "RebuildCompleted should carry target session, rebuild generation, and result"
          3
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

      let pendingTests = [| sampleTestCase |]
      let pending = pendingRebuildFor 1L pendingTests RunTrigger.FileSave
      let model =
        { SageFsModel.initial() with
            LiveTesting =
              { LiveTestCycleState.empty with
                  PendingRebuild = Some pending } }

      let model', effects =
        SageFsUpdate.update (SageFsMsg.RebuildCompleted (None, pending.Generation, Ok ())) model

      match effects with
      | [ SageFsEffect.TestCycle (TestCycleEffect.RunAffectedTests req) ] ->
        req.Tests
        |> Expect.equal "successful rebuild should run the saved test set" pendingTests
        req.Trigger
        |> Expect.equal "saved trigger should flow into RunAffectedTests" pending.Trigger
        req.TreeSitterElapsed
        |> Expect.equal "tree-sitter timing should be preserved" pending.TreeSitterElapsed
        req.FcsElapsed
        |> Expect.equal "fcs timing should be preserved" pending.FcsElapsed
        req.SessionId
        |> Expect.equal "session ownership should be preserved" pending.SessionId
        req.InstrumentationMaps
        |> Expect.equal "instrumentation maps should be preserved" pending.InstrumentationMaps
      | other ->
        failtestf "expected one RunAffectedTests effect after successful rebuild, got %A" other

      model'.LiveTesting.PendingRebuild
      |> Expect.isNone "PendingRebuild should be cleared after successful rebuild"
    }

    test "RebuildCompleted(Error) surfaces build diagnostic without running tests" {
      // WHY: Running tests against code that failed to compile is worse
      // than useless — it produces misleading results. Build errors must
      // be surfaced to the user and no RunAffectedTests effect should fire.
      hasUnionCase<SageFsMsg> "RebuildCompleted"
      |> Expect.isTrue
          "RebuildCompleted msg must exist — build errors must surface without running tests"

      let pending = pendingRebuildFor 1L [| sampleTestCase |] RunTrigger.FileSave
      let model =
        { SageFsModel.initial() with
            LiveTesting =
              { LiveTestCycleState.empty with
                  PendingRebuild = Some pending } }

      let model', effects =
        SageFsUpdate.update
          (SageFsMsg.RebuildCompleted (None, pending.Generation, Error "syntax error at line 5"))
          model

      effects
      |> Expect.isEmpty "build failure must NOT trigger test execution"

      model'.LiveTesting.PendingRebuild
      |> Expect.isNone "PendingRebuild should be cleared after failed rebuild"
    }

    test "RebuildCompleted(Ok) with no PendingRebuild is ignored as stale" {
      // WHY: Rebuild completions are asynchronous. If the model no longer has
      // a pending rebuild when a completion arrives, that result is stale and
      // must not trigger test execution against an older binary.
      hasUnionCase<SageFsMsg> "RebuildCompleted"
      |> Expect.isTrue
          "RebuildCompleted msg must exist — stale completions must be ignored safely"

      let model = SageFsModel.initial()
      let model', effects =
        SageFsUpdate.update (SageFsMsg.RebuildCompleted (None, 1L, Ok ())) model

      effects
      |> Expect.isEmpty "stale rebuild completion must NOT trigger test execution"

      model'.LiveTesting.PendingRebuild
      |> Expect.isNone "stale rebuild completion should leave PendingRebuild empty"
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
          | TestCycleEffect.RequestRebuild (_, req) -> Some req.Tests
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

      let pending = pendingRebuildFor 1L [| sampleTestCase |] RunTrigger.FileSave
      let model = activeModelWithPending pending

      let model', _ =
        SageFsUpdate.update
          (SageFsMsg.FileContentChanged ("Foo.fs", "let x = 42"))
          model

      model'.LiveTesting.PendingRebuild
      |> Expect.isNone "PendingRebuild should be cancelled when new edit arrives"

      model'.LiveTesting.ActiveFile
      |> Expect.equal "the fresh edit should become the active file" (Some "Foo.fs")

      model'.LiveTesting.Debounce.Fcs.Pending.IsSome
      |> Expect.isTrue "the fresh edit should restart the debounce pipeline"
    }

    test "new FileContentChanged during PendingRebuild emits CancelRebuild for the pending generation" {
      // WHY: Clearing PendingRebuild in memory is not enough. The impure
      // rebuild work must be told explicitly which rebuild intent is dead.
      let pending = pendingRebuildFor 1L [| sampleTestCase |] RunTrigger.FileSave
      let model = activeModelWithPending pending

      let _model', effects =
        SageFsUpdate.update
          (SageFsMsg.FileContentChanged ("Foo.fs", "let x = 42"))
          model

      let cancelFields =
        effects
        |> List.tryPick (function
          | SageFsEffect.TestCycle effect when unionCaseNameOf effect = "CancelRebuild" ->
              Some (FSharpValue.GetUnionFields(effect, typeof<TestCycleEffect>) |> snd)
          | _ ->
              None)

      cancelFields
      |> Expect.isSome
          "invalidating a pending rebuild should emit an explicit CancelRebuild effect"

      let cancelFields = cancelFields |> Option.get
      cancelFields.[0]
      |> Expect.equal "CancelRebuild should target the same session" (box pending.SessionId)
      cancelFields.[1]
      |> Expect.equal "CancelRebuild should carry the invalidated generation" (box pending.Generation)
    }

    test "onFileSave during PendingRebuild cancels the stale rebuild" {
      // WHY: Save events are also edits to compiled artifacts. A save that
      // lands while a rebuild is in-flight invalidates that rebuild just as
      // surely as a keystroke does.
      let pending = pendingRebuildFor 1L [| sampleTestCase |] RunTrigger.Keystroke
      let now = DateTimeOffset.UtcNow
      let state =
        { LiveTestCycleState.empty with
            NextRebuildGeneration = pending.Generation
            PendingRebuild = Some pending }

      let state' =
        state |> LiveTestCycleState.onFileSave "Foo.fs" now

      state'.PendingRebuild
      |> Expect.isNone "PendingRebuild should be cancelled when a save arrives"

      state'.LastTrigger
      |> Expect.equal "save should still update the trigger" RunTrigger.FileSave
    }

    test "cancelled rebuild does not emit RebuildCompleted effects" {
      // WHY: A cancelled build's result is irrelevant and stale. If the
      // RebuildCompleted message for a cancelled rebuild is not ignored,
      // it would trigger test execution against wrong code.
      hasUnionCase<SageFsMsg> "RebuildCompleted"
      |> Expect.isTrue
          "RebuildCompleted must exist to test cancellation semantics"

      let pending = pendingRebuildFor 1L [| sampleTestCase |] RunTrigger.FileSave
      let model = activeModelWithPending pending

      let cancelledModel, _ =
        SageFsUpdate.update
          (SageFsMsg.FileContentChanged ("Foo.fs", "let x = 42"))
          model

      let model', effects =
        SageFsUpdate.update
          (SageFsMsg.RebuildCompleted (None, pending.Generation, Ok ()))
          cancelledModel

      effects
      |> Expect.isEmpty
          "stale RebuildCompleted after cancellation must NOT trigger test execution"

      model'.LiveTesting.PendingRebuild
      |> Expect.isNone "the cancelled pipeline should stay cancelled after stale completion"
    }

    test "equivalent failed type-check fallback preserves the current pending rebuild generation" {
      // WHY: Repeating the same failed type-check while the same rebuild is
      // already pending should not mint a brand-new generation and restart
      // identical compilation work.
      let state =
        { LiveTestCycleState.empty with
            TestState = activeStateWith [| sampleTestCase |]
            LastTrigger = RunTrigger.FileSave }

      let pendingGeneration state =
        state.PendingRebuild
        |> Expect.isSome "fallback rebuild should store a pending rebuild"
        let pending = state.PendingRebuild.Value
        pending
        |> fun pending ->
            tryRecordFieldValue "Generation" pending
            |> Option.map (fun value -> value :?> int64)

      let _, state1 =
        LiveTestCycleState.handleFcsResult
          (FcsTypeCheckResult.Failed ("Foo.fs", ["syntax error"]))
          state

      let gen1 =
        pendingGeneration state1
        |> Expect.isSome "first pending rebuild should expose a generation"
      let gen1 = pendingGeneration state1 |> Option.get

      let effects2, state2 =
        LiveTestCycleState.handleFcsResult
          (FcsTypeCheckResult.Failed ("Foo.fs", ["syntax error"]))
          state1

      let gen2 =
        pendingGeneration state2
        |> Expect.isSome "equivalent fallback rebuild should keep exposing a generation"
      let gen2 = pendingGeneration state2 |> Option.get

      effects2
      |> List.exists (fun effect -> unionCaseNameOf effect = "RequestRebuild")
      |> Expect.isFalse
          "equivalent failed type-check should not emit a replacement RequestRebuild while the same rebuild is already pending"

      gen2
      |> Expect.equal
          "equivalent failed type-check should preserve the original pending rebuild generation"
          gen1
    }

    test "equivalent successful type-check fallback preserves the current pending rebuild generation" {
      // WHY: Duplicate successful FCS completions for unchanged compiled-file
      // content should not replace an equivalent pending rebuild with a newer
      // generation just because analysis completed twice.
      let state =
        { LiveTestCycleState.empty with
            TestState = activeStateWith [| sampleTestCase |]
            LastTrigger = RunTrigger.FileSave }

      let pendingGeneration state =
        state.PendingRebuild
        |> Expect.isSome "fallback rebuild should store a pending rebuild"
        let pending = state.PendingRebuild.Value
        pending
        |> fun pending ->
            tryRecordFieldValue "Generation" pending
            |> Option.map (fun value -> value :?> int64)

      let _, state1 =
        LiveTestCycleState.handleFcsResult
          (FcsTypeCheckResult.Success ("Foo.fs", []))
          state

      let gen1 =
        pendingGeneration state1
        |> Expect.isSome "first pending rebuild should expose a generation"
      let gen1 = pendingGeneration state1 |> Option.get

      let effects2, state2 =
        LiveTestCycleState.handleFcsResult
          (FcsTypeCheckResult.Success ("Foo.fs", []))
          state1

      let gen2 =
        pendingGeneration state2
        |> Expect.isSome "equivalent successful fallback should keep exposing a generation"
      let gen2 = pendingGeneration state2 |> Option.get

      effects2
      |> List.exists (fun effect -> unionCaseNameOf effect = "RequestRebuild")
      |> Expect.isFalse
          "equivalent successful type-check should not emit a replacement RequestRebuild while the same rebuild is already pending"

      gen2
      |> Expect.equal
          "equivalent successful type-check should preserve the original pending rebuild generation"
          gen1
    }

    test "failed type-check fallback records the file path and current analysis identity on the pending rebuild" {
      // WHY: A pending rebuild should name the exact file and content truth it
      // came from so later coalescing can distinguish stale work from fresh work.
      let content = "module Foo\nlet value = 1"
      let identity = AnalysisIdentity.ofContent content
      let state =
        { LiveTestCycleState.empty with
            TestState = activeStateWith [| sampleTestCase |]
            ActiveFile = Some "Foo.fs"
            LatestContent = Some content
            LatestAnalysisIdentity = Some identity
            LastTrigger = RunTrigger.FileSave }

      let _, state' =
        LiveTestCycleState.handleFcsResult
          (FcsTypeCheckResult.Failed ("Foo.fs", ["syntax error"]))
          state

      let pending =
        state'.PendingRebuild
        |> Expect.wantSome "failed type-check fallback should still create a pending rebuild"

      let pendingFilePath =
        tryRecordFieldValue "FilePath" pending
        |> Expect.wantSome "pending rebuild should carry the source file path"
      let pendingFilePath = pendingFilePath :?> string

      let pendingAnalysisIdentity =
        tryRecordFieldValue "AnalysisIdentity" pending
        |> Expect.wantSome "pending rebuild should carry the current analysis identity"

      pendingFilePath
      |> Expect.equal "pending rebuild should remember which file failed type-checking" "Foo.fs"

      pendingAnalysisIdentity
      |> Expect.equal
          "pending rebuild should preserve the current analysis identity"
          (box (Some identity))
    }

    test "equivalent rebuild suppression does not collapse fresh content identity into the older pending rebuild" {
      // WHY: Two rebuild intents can target the same tests, trigger, and
      // session while still representing different source truth. Fresh content
      // must mint a new rebuild instead of being swallowed by older pending work.
      let oldContent = "module Foo\nlet value = 1"
      let newContent = "module Foo\nlet value = 2"
      let oldIdentity = AnalysisIdentity.ofContent oldContent
      let newIdentity = AnalysisIdentity.ofContent newContent
      let pending =
        pendingRebuildWithProvenance
          1L
          [| sampleTestCase |]
          RunTrigger.FileSave
          None
          "Foo.fs"
          (Some oldIdentity)
      let state =
        { LiveTestCycleState.empty with
            TestState = activeStateWith [| sampleTestCase |]
            ActiveFile = Some "Foo.fs"
            LatestContent = Some newContent
            LatestAnalysisIdentity = Some newIdentity
            LastTrigger = RunTrigger.FileSave
            NextRebuildGeneration = pending.Generation
            PendingRebuild = Some pending }

      let effects, state' =
        LiveTestCycleState.handleFcsResult
          (FcsTypeCheckResult.Failed ("Foo.fs", ["syntax error after fresh edit"]))
          state

      effects
      |> List.exists (fun effect -> unionCaseNameOf effect = "RequestRebuild")
      |> Expect.isTrue
          "fresh content identity should emit a replacement RequestRebuild instead of being treated as equivalent pending work"

      let nextGeneration =
        state'.PendingRebuild
        |> Expect.wantSome "fresh content identity should replace the pending rebuild"
        |> fun nextPending -> nextPending.Generation

      (nextGeneration > pending.Generation)
      |> Expect.isTrue
          "fresh content identity should mint a newer rebuild generation than the stale pending rebuild"
    }

    test "compiled edit during an in-flight test run stores the latest rebuild intent instead of dropping it" {
      // WHY: When compiled tests are already running, a fresh edit should
      // still produce owed rebuild work. Dropping that intent means the
      // newest code can finish the old run and then never get tested.
      let generation = RunGeneration.next RunGeneration.zero
      let content = "module Foo\nlet value = 1"
      let identity = AnalysisIdentity.ofContent content
      let state =
        { LiveTestCycleState.empty with
            TestState =
              { activeStateWith [| sampleTestCase |] with
                  RunPhases = Map.ofList [ "test-session", Running generation ]
                  LastGeneration = generation
                  TestSessionMap = Map.ofList [ sampleTestCase.Id, "test-session" ] }
            ActiveFile = Some "Foo.fs"
            LatestContent = Some content
            LatestAnalysisIdentity = Some identity
            LastTrigger = RunTrigger.FileSave }

      let effects, state' =
        LiveTestCycleState.handleFcsResult
          (FcsTypeCheckResult.Failed ("Foo.fs", [ "syntax error" ]))
          state

      effects
      |> Expect.isEmpty
          "running compiled tests should defer the rebuild instead of emitting it immediately"

      state'.PendingRebuild
      |> Expect.isNone
          "deferring rebuild work must not pretend a rebuild is already in flight"

      let queued =
        tryQueuedRebuildValue state'
        |> Expect.wantSome "running compiled tests should store the latest queued rebuild intent"

      tryRecordFieldValue "FilePath" queued
      |> Expect.wantSome "queued rebuild should remember which file triggered it"
      |> fun value -> value :?> string
      |> Expect.equal "queued rebuild should keep the triggering file path" "Foo.fs"

      tryRecordFieldValue "AnalysisIdentity" queued
      |> Expect.wantSome "queued rebuild should preserve the current content identity"
      |> Expect.equal "queued rebuild should point at the current analyzed content" (box (Some identity))

      tryRecordFieldValue "SessionId" queued
      |> Expect.wantSome "queued rebuild should preserve session ownership"
      |> Expect.equal "queued rebuild should target the running session that owes fresh work" (box (Some "test-session"))
    }

    test "multiple edits during one running session keep only the newest compiled rebuild intent" {
      // WHY: A queued rebuild describes the latest analyzed code we still
      // owe a run for. The next edit invalidates that intent until fresh
      // analysis arrives, and the replacement intent must describe the new
      // content instead of replaying the older one.
      let generation = RunGeneration.next RunGeneration.zero
      let content1 = "module Foo\nlet value = 1"
      let content2 = "module Foo\nlet value = 2"
      let identity1 = AnalysisIdentity.ofContent content1
      let identity2 = AnalysisIdentity.ofContent content2
      let state =
        { LiveTestCycleState.empty with
            TestState =
              { activeStateWith [| sampleTestCase |] with
                  RunPhases = Map.ofList [ "test-session", Running generation ]
                  LastGeneration = generation
                  TestSessionMap = Map.ofList [ sampleTestCase.Id, "test-session" ] }
            ActiveFile = Some "Foo.fs"
            LatestContent = Some content1
            LatestAnalysisIdentity = Some identity1
            LastTrigger = RunTrigger.FileSave }

      let _, afterFirstAnalysis =
        LiveTestCycleState.handleFcsResult
          (FcsTypeCheckResult.Failed ("Foo.fs", [ "first syntax error" ]))
          state

      let firstQueued =
        tryQueuedRebuildValue afterFirstAnalysis
        |> Expect.wantSome "first analysis should queue rebuild work for the first content version"

      tryRecordFieldValue "AnalysisIdentity" firstQueued
      |> Expect.wantSome "first queued rebuild should preserve the first content identity"
      |> Expect.equal "first queued rebuild should describe the first analyzed content" (box (Some identity1))

      let afterSecondEdit =
        afterFirstAnalysis
        |> LiveTestCycleState.onFileSaveWithContent content2 "Foo.fs" DateTimeOffset.UtcNow

      tryQueuedRebuildValue afterSecondEdit
      |> Expect.isNone
          "a fresh edit should clear the stale queued rebuild until fresh analysis for the newer content arrives"

      let effects, afterSecondAnalysis =
        LiveTestCycleState.handleFcsResult
          (FcsTypeCheckResult.Failed ("Foo.fs", [ "second syntax error" ]))
          afterSecondEdit

      effects
      |> Expect.isEmpty
          "the newer content should still defer rebuild execution while the old run is in flight"

      let secondQueued =
        tryQueuedRebuildValue afterSecondAnalysis
        |> Expect.wantSome "fresh analysis should replace the stale queued rebuild with the newest intent"

      tryRecordFieldValue "AnalysisIdentity" secondQueued
      |> Expect.wantSome "replacement queued rebuild should preserve the new content identity"
      |> Expect.equal "replacement queued rebuild should describe the latest analyzed content" (box (Some identity2))
    }

    test "distinct rebuild intents still receive increasing generations" {
      // WHY: Coalescing only applies to semantically identical work. Once a
      // fresh save invalidates the pending rebuild, the next rebuild intent
      // must still get a higher generation.
      let now = DateTimeOffset.UtcNow
      let state =
        { LiveTestCycleState.empty with
            TestState = activeStateWith [| sampleTestCase |]
            LastTrigger = RunTrigger.FileSave }

      let pendingGeneration state =
        state.PendingRebuild
        |> Expect.isSome "fallback rebuild should store a pending rebuild"
        let pending = state.PendingRebuild.Value
        pending
        |> fun pending ->
            tryRecordFieldValue "Generation" pending
            |> Option.map (fun value -> value :?> int64)

      let _, state1 =
        LiveTestCycleState.handleFcsResult
          (FcsTypeCheckResult.Failed ("Foo.fs", ["syntax error"]))
          state

      let gen1 =
        pendingGeneration state1
        |> Expect.isSome "first pending rebuild should expose a generation"
      let gen1 = pendingGeneration state1 |> Option.get

      let state2 =
        state1
        |> LiveTestCycleState.onFileSave "Foo.fs" now

      let _, state3 =
        LiveTestCycleState.handleFcsResult
          (FcsTypeCheckResult.Failed ("Foo.fs", ["syntax error after a fresh save"]))
          state2

      let gen2 =
        pendingGeneration state3
        |> Expect.isSome "distinct rebuild intent should expose a generation"
      let gen2 = pendingGeneration state3 |> Option.get

      gen1
      |> Expect.equal "first rebuild generation should start at 1" 1L

      (gen2 > gen1)
      |> Expect.isTrue "newer distinct rebuild intents should still get a higher generation"
    }

    test "RebuildCompleted with stale generation is ignored while newer PendingRebuild remains" {
      // WHY: Cancellation reduces wasted work, but stale completions can still
      // arrive late. Generation is the semantic guard that keeps an older
      // rebuild from consuming the current pending rebuild.
      let pending = pendingRebuildFor 2L [| sampleTestCase |] RunTrigger.FileSave
      let model = activeModelWithPending pending

      let model', effects =
        SageFsUpdate.update
          (SageFsMsg.RebuildCompleted (None, 1L, Ok ()))
          model

      effects
      |> Expect.isEmpty "stale rebuild completion must not trigger test execution"

      model'.LiveTesting.PendingRebuild
      |> Expect.isSome "newer pending rebuild should remain in place"

      model'.LiveTesting.PendingRebuild.Value.Generation
      |> Expect.equal "pending rebuild generation should stay unchanged" 2L
    }
  ]
]
