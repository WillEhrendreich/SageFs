/// Phase 1a: TestCycleEffect Payload Records
///
/// WHY THIS EXISTS
/// ───────────────
/// TestCycleEffect.RunAffectedTests and TestCycleEffect.RequestRebuild share 6 of 7
/// positional parameters.  TestCycleEffect.RequestFcsTypeCheck has 5 positional params.
/// Positional tuples are:
///   • Unreadable at the call site  — which TimeSpan is tree-sitter vs FCS?
///   • Fragile across refactors     — adding a field silently shifts all positions
///   • Impossible to share           — no way to pass "the common test-run payload"
///
/// WHAT WE INTRODUCE
/// ─────────────────
/// 1. TestRunRequest record — the 6 fields shared by RunAffectedTests / RequestRebuild
/// 2. TypeCheckRequest record — the 5 fields of RequestFcsTypeCheck
/// 3. TestCycleEffect cases updated to carry these records
///    • RunAffectedTests of TestRunRequest
///    • RequestRebuild of generation:int64 * TestRunRequest
///    • RequestFcsTypeCheck of TypeCheckRequest
///
/// INVARIANTS UNDER TEST
/// ─────────────────────
/// • TestRunRequest.empty is the zero element — all fields default/empty
/// • TestRunRequest round-trips: construct → destructure → reconstruct = identity
/// • TypeCheckRequest.empty is the zero element
/// • TestCycleEffect cases carry exactly one payload record (no loose positional fields)
/// • decideAfterTypeCheck emits effects using TestRunRequest payloads
/// • fromTick emits TypeCheckRequest payloads
/// • storePendingRebuild/promoteQueuedRebuild use TestRunRequest payloads
///
/// WHAT THIS DOES NOT COVER
/// ────────────────────────
/// • LiveTestState sub-record decomposition (Phase 1b — separate test file)
/// • DiscoveryLifecycle DU (Phase 2)
/// • Cold-start race fix (Phase 3)
module SageFs.Tests.LiveTestingDecompositionTests

open System
open Expecto
open Expecto.Flip
open SageFs.Features.LiveTesting

// ─────────────────────────────────────────────────────────────────────
// Helpers
// ─────────────────────────────────────────────────────────────────────

let private mkTestId name fw = TestId.create name fw
let private mkTestCase name fw =
  { Id = TestId.create name fw
    FullName = name; DisplayName = name
    Origin = TestOrigin.ReflectionOnly
    Labels = []; Framework = fw; Category = TestCategory.Unit }

let private sampleTests = [|
  mkTestCase "Alpha.should_pass" TestFramework.Expecto
  mkTestCase "Beta.should_fail" TestFramework.Expecto
|]

let private sampleMaps : InstrumentationMap array = [||]

// ─────────────────────────────────────────────────────────────────────
// § 1  TestRunRequest record
// ─────────────────────────────────────────────────────────────────────

module TestRunRequestTests =

  /// TestRunRequest.empty should produce an unambiguous zero:
  /// no tests, Keystroke trigger, zero timings, no session, no maps.
  let ``TestRunRequest_empty is the zero element`` =
    test "TestRunRequest.empty is the zero element" {
      let z = TestRunRequest.empty
      z.Tests |> Expect.isEmpty "tests should be empty"
      z.Trigger |> Expect.equal "trigger should be Keystroke" RunTrigger.Keystroke
      z.TreeSitterElapsed |> Expect.equal "ts elapsed should be zero" TimeSpan.Zero
      z.FcsElapsed |> Expect.equal "fcs elapsed should be zero" TimeSpan.Zero
      z.SessionId |> Expect.isNone "session should be None"
      z.InstrumentationMaps |> Expect.isEmpty "maps should be empty"
    }

  /// Round-trip: construct a TestRunRequest, read every field, reconstruct → equal.
  let ``TestRunRequest round-trips through fields`` =
    test "TestRunRequest round-trips through fields" {
      let req = {
        Tests = sampleTests
        Trigger = RunTrigger.FileSave
        TreeSitterElapsed = TimeSpan.FromMilliseconds 42.0
        FcsElapsed = TimeSpan.FromMilliseconds 100.0
        SessionId = Some "abc12345"
        InstrumentationMaps = sampleMaps
      }
      let rebuilt = {
        Tests = req.Tests
        Trigger = req.Trigger
        TreeSitterElapsed = req.TreeSitterElapsed
        FcsElapsed = req.FcsElapsed
        SessionId = req.SessionId
        InstrumentationMaps = req.InstrumentationMaps
      }
      rebuilt |> Expect.equal "round-trip should preserve all fields" req
    }

  /// TestRunRequest carries named fields — no more guessing which TimeSpan is which.
  let ``TestRunRequest distinguishes tree-sitter from FCS elapsed`` =
    test "TestRunRequest distinguishes tree-sitter from FCS elapsed" {
      let req = {
        TestRunRequest.empty with
          TreeSitterElapsed = TimeSpan.FromMilliseconds 10.0
          FcsElapsed = TimeSpan.FromMilliseconds 200.0
      }
      (req.TreeSitterElapsed, req.FcsElapsed)
      |> Expect.isLessThan "tree-sitter should be shorter"
    }

  let tests = testList "TestRunRequest record" [
    ``TestRunRequest_empty is the zero element``
    ``TestRunRequest round-trips through fields``
    ``TestRunRequest distinguishes tree-sitter from FCS elapsed``
  ]

// ─────────────────────────────────────────────────────────────────────
// § 2  TypeCheckRequest record
// ─────────────────────────────────────────────────────────────────────

module TypeCheckRequestTests =

  let ``TypeCheckRequest_empty is the zero element`` =
    test "TypeCheckRequest.empty is the zero element" {
      let z = TypeCheckRequest.empty
      z.SessionId |> Expect.isNone "session should be None"
      z.FilePath |> Expect.equal "file path should be empty" ""
      z.Content |> Expect.isNone "content should be None"
      z.AnalysisIdentity |> Expect.isNone "analysis identity should be None"
      z.TreeSitterElapsed |> Expect.equal "ts elapsed should be zero" TimeSpan.Zero
    }

  let ``TypeCheckRequest round-trips through fields`` =
    test "TypeCheckRequest round-trips through fields" {
      let req = {
        SessionId = Some "sess1234"
        FilePath = "/src/Foo.fs"
        Content = Some "let x = 1"
        AnalysisIdentity = Some (AnalysisIdentity.ofContent "let x = 1")
        TreeSitterElapsed = TimeSpan.FromMilliseconds 5.0
      }
      let rebuilt = {
        SessionId = req.SessionId
        FilePath = req.FilePath
        Content = req.Content
        AnalysisIdentity = req.AnalysisIdentity
        TreeSitterElapsed = req.TreeSitterElapsed
      }
      rebuilt |> Expect.equal "round-trip should preserve all fields" req
    }

  let tests = testList "TypeCheckRequest record" [
    ``TypeCheckRequest_empty is the zero element``
    ``TypeCheckRequest round-trips through fields``
  ]

// ─────────────────────────────────────────────────────────────────────
// § 3  TestCycleEffect carries payload records
// ─────────────────────────────────────────────────────────────────────

module TestCycleEffectPayloadTests =

  /// RunAffectedTests should carry a TestRunRequest, not loose positional params.
  let ``RunAffectedTests carries TestRunRequest`` =
    test "RunAffectedTests carries TestRunRequest" {
      let req = {
        Tests = sampleTests
        Trigger = RunTrigger.Keystroke
        TreeSitterElapsed = TimeSpan.FromMilliseconds 10.0
        FcsElapsed = TimeSpan.FromMilliseconds 50.0
        SessionId = None
        InstrumentationMaps = sampleMaps
      }
      let effect = TestCycleEffect.RunAffectedTests req
      match effect with
      | TestCycleEffect.RunAffectedTests r ->
        r.Tests |> Expect.hasLength "should carry 2 tests" 2
        r.Trigger |> Expect.equal "trigger" RunTrigger.Keystroke
        r.SessionId |> Expect.isNone "no session"
      | _ -> failtest "wrong effect case"
    }

  /// RequestRebuild should carry generation + TestRunRequest.
  let ``RequestRebuild carries generation and TestRunRequest`` =
    test "RequestRebuild carries generation and TestRunRequest" {
      let req = {
        Tests = sampleTests
        Trigger = RunTrigger.FileSave
        TreeSitterElapsed = TimeSpan.FromMilliseconds 10.0
        FcsElapsed = TimeSpan.FromMilliseconds 50.0
        SessionId = Some "abc12345"
        InstrumentationMaps = sampleMaps
      }
      let effect = TestCycleEffect.RequestRebuild(42L, req)
      match effect with
      | TestCycleEffect.RequestRebuild (gen, r) ->
        gen |> Expect.equal "generation" 42L
        r.Tests |> Expect.hasLength "should carry 2 tests" 2
        r.Trigger |> Expect.equal "trigger" RunTrigger.FileSave
        r.SessionId |> Expect.equal "session" (Some "abc12345")
      | _ -> failtest "wrong effect case"
    }

  /// RequestFcsTypeCheck should carry a TypeCheckRequest.
  let ``RequestFcsTypeCheck carries TypeCheckRequest`` =
    test "RequestFcsTypeCheck carries TypeCheckRequest" {
      let req = {
        SessionId = None
        FilePath = "/src/Foo.fs"
        Content = Some "let x = 1"
        AnalysisIdentity = None
        TreeSitterElapsed = TimeSpan.FromMilliseconds 5.0
      }
      let effect = TestCycleEffect.RequestFcsTypeCheck req
      match effect with
      | TestCycleEffect.RequestFcsTypeCheck r ->
        r.FilePath |> Expect.equal "file path" "/src/Foo.fs"
        r.Content |> Expect.equal "content" (Some "let x = 1")
        r.TreeSitterElapsed |> Expect.equal "ts elapsed" (TimeSpan.FromMilliseconds 5.0)
      | _ -> failtest "wrong effect case"
    }

  /// CancelRebuild and other cases are unchanged — only the big positional ones got records.
  let ``unchanged cases still work`` =
    test "unchanged cases still work" {
      let cancel = TestCycleEffect.CancelRebuild(Some "s", 7L)
      match cancel with
      | TestCycleEffect.CancelRebuild (sid, gen) ->
        sid |> Expect.equal "session" (Some "s")
        gen |> Expect.equal "generation" 7L
      | _ -> failtest "wrong case"

      let disco = TestCycleEffect.RequestInitialDiscovery
      match disco with
      | TestCycleEffect.RequestInitialDiscovery -> ()
      | _ -> failtest "wrong case"

      let parse = TestCycleEffect.ParseTreeSitter("code", "file.fs")
      match parse with
      | TestCycleEffect.ParseTreeSitter (c, f) ->
        c |> Expect.equal "content" "code"
        f |> Expect.equal "file" "file.fs"
      | _ -> failtest "wrong case"

      let register = TestCycleEffect.RegisterFileWatcher("s", "/dir")
      match register with
      | TestCycleEffect.RegisterFileWatcher (s, d) ->
        s |> Expect.equal "session" "s"
        d |> Expect.equal "dir" "/dir"
      | _ -> failtest "wrong case"

      let dispose = TestCycleEffect.DisposeFileWatcher("s", "/dir")
      match dispose with
      | TestCycleEffect.DisposeFileWatcher (s, d) ->
        s |> Expect.equal "session" "s"
        d |> Expect.equal "dir" "/dir"
      | _ -> failtest "wrong case"
    }

  let tests = testList "TestCycleEffect carries payload records" [
    ``RunAffectedTests carries TestRunRequest``
    ``RequestRebuild carries generation and TestRunRequest``
    ``RequestFcsTypeCheck carries TypeCheckRequest``
    ``unchanged cases still work``
  ]

// ─────────────────────────────────────────────────────────────────────
// § 4  Effect producers use payload records
// ─────────────────────────────────────────────────────────────────────

module EffectProducerTests =

  /// fromTick should emit RequestFcsTypeCheck with a TypeCheckRequest payload.
  let ``fromTick emits TypeCheckRequest payload`` =
    test "fromTick emits TypeCheckRequest payload" {
      let content = "let x = 1"
      let identity = AnalysisIdentity.ofContent content
      let effects =
        TestCycleEffects.fromTick
          None               // no tree-sitter payload
          (Some "Foo.fsx")   // FCS file path
          (Some content)
          (Some identity)
          "Foo.fsx"
          None               // no prior timing
      let fcsEffects =
        effects
        |> List.choose (fun e ->
          match e with
          | TestCycleEffect.RequestFcsTypeCheck req -> Some req
          | _ -> None)
      fcsEffects |> Expect.hasLength "should emit one FCS request" 1
      let req = fcsEffects.[0]
      req.FilePath |> Expect.equal "file path" "Foo.fsx"
      req.Content |> Expect.equal "content" (Some content)
      req.AnalysisIdentity |> Expect.equal "analysis identity" (Some identity)
      req.SessionId |> Expect.isNone "session should be None from fromTick"
    }

  /// decideAfterTypeCheck should emit RunAffectedTests with TestRunRequest payload
  /// when script tests are affected by a keystroke.
  let ``decideAfterTypeCheck emits TestRunRequest for script keystroke`` =
    test "decideAfterTypeCheck emits TestRunRequest for script keystroke" {
      let tc = mkTestCase "MyTest.test1" TestFramework.Expecto
      let depGraph =
        { TestDependencyGraph.empty with
            SymbolToTests = Map.ofList [ "mySymbol", [| tc.Id |] ]
            TransitiveCoverage = Map.ofList [ "mySymbol", [| tc.Id |] ] }
      let state =
        { LiveTestState.empty with
            Activation = LiveTestingActivation.Active
            DiscoveredTests = [| tc |] }
      let outcome =
        TestCycleEffects.decideAfterTypeCheck
          ["mySymbol"]
          "Script.fsx"
          RunTrigger.Keystroke
          depGraph
          state
          None
          Map.empty
      outcome.Effects |> Expect.isNonEmpty "should have effects"
      let runEffects =
        outcome.Effects
        |> List.choose (fun e ->
          match e with
          | TestCycleEffect.RunAffectedTests req -> Some req
          | _ -> None)
      runEffects |> Expect.hasLength "should emit one RunAffectedTests" 1
      let req = runEffects.[0]
      req.Tests |> Expect.hasLength "should carry 1 test" 1
      req.Trigger |> Expect.equal "trigger" RunTrigger.Keystroke
    }

  /// decideAfterTypeCheck should emit RequestRebuild with generation + TestRunRequest
  /// when compiled tests are affected by a file save.
  let ``decideAfterTypeCheck emits RequestRebuild for compiled file save`` =
    test "decideAfterTypeCheck emits RequestRebuild for compiled file save" {
      let tc = mkTestCase "MyTest.test1" TestFramework.Expecto
      let depGraph =
        { TestDependencyGraph.empty with
            SymbolToTests = Map.ofList [ "mySymbol", [| tc.Id |] ]
            TransitiveCoverage = Map.ofList [ "mySymbol", [| tc.Id |] ] }
      let state =
        { LiveTestState.empty with
            Activation = LiveTestingActivation.Active
            DiscoveredTests = [| tc |] }
      let outcome =
        TestCycleEffects.decideAfterTypeCheck
          ["mySymbol"]
          "Foo.fs"
          RunTrigger.FileSave
          depGraph
          state
          None
          Map.empty
      outcome.Effects |> Expect.isNonEmpty "should have effects"
      let rebuildEffects =
        outcome.Effects
        |> List.choose (fun e ->
          match e with
          | TestCycleEffect.RequestRebuild (gen, req) -> Some (gen, req)
          | _ -> None)
      rebuildEffects |> Expect.hasLength "should emit one RequestRebuild" 1
      let gen, req = rebuildEffects.[0]
      gen |> Expect.equal "generation should be 0 (assigned later by cycle state)" 0L
      req.Tests |> Expect.hasLength "should carry 1 test" 1
      req.Trigger |> Expect.equal "trigger" RunTrigger.FileSave
    }

  let tests = testList "Effect producers use payload records" [
    ``fromTick emits TypeCheckRequest payload``
    ``decideAfterTypeCheck emits TestRunRequest for script keystroke``
    ``decideAfterTypeCheck emits RequestRebuild for compiled file save``
  ]

// ─────────────────────────────────────────────────────────────────────
// § 5  Rebuild lifecycle uses TestRunRequest
// ─────────────────────────────────────────────────────────────────────

module RebuildLifecycleTests =

  /// promoteQueuedRebuild should emit RequestRebuild with generation + TestRunRequest.
  let ``promoteQueuedRebuild emits generation and TestRunRequest`` =
    test "promoteQueuedRebuild emits generation and TestRunRequest" {
      let tc = mkTestCase "Rebuild.test1" TestFramework.Expecto
      let identity = AnalysisIdentity.ofContent "let x = 1"
      let queued : QueuedRebuildState = {
        Tests = [| tc |]
        Trigger = RunTrigger.FileSave
        FilePath = "Foo.fs"
        AnalysisIdentity = Some identity
        TreeSitterElapsed = TimeSpan.FromMilliseconds 10.0
        FcsElapsed = TimeSpan.FromMilliseconds 100.0
        SessionId = Some "sess1234"
        InstrumentationMaps = [||]
      }
      let cycleState = {
        LiveTestCycleState.empty with
          QueuedRebuild = Some queued
          LatestAnalysisIdentity = Some identity
      }
      let effects, newState = LiveTestCycleState.promoteQueuedRebuild (Some "sess1234") cycleState
      effects |> Expect.hasLength "should emit one effect" 1
      match effects.[0] with
      | TestCycleEffect.RequestRebuild (gen, req) ->
        (gen, 0L) |> Expect.isGreaterThan "generation should be positive"
        req.Tests |> Expect.hasLength "one test" 1
        req.Trigger |> Expect.equal "trigger" RunTrigger.FileSave
        req.SessionId |> Expect.equal "session" (Some "sess1234")
      | other -> failtestf "expected RequestRebuild, got %A" other
      newState.QueuedRebuild |> Expect.isNone "queued should be consumed"
      newState.PendingRebuild |> Expect.isSome "pending should be set"
    }

  let tests = testList "Rebuild lifecycle uses TestRunRequest" [
    ``promoteQueuedRebuild emits generation and TestRunRequest``
  ]

// ═════════════════════════════════════════════════════════════════════
// Phase 1b: LiveTestState Sub-Record Extraction
// ═════════════════════════════════════════════════════════════════════
//
// WHY THIS EXISTS
// ───────────────
// LiveTestState had 27 flat fields — a "God record" where mutation analysis
// revealed clear co-mutation clusters:
//
//   Cluster A (StatusIndex): StatusEntries + StatusEntrySlots + StatusEntryIndex
//     + StatusEntriesProjection — always updated together via withStatusEntries.
//     Partial updates left Slots/Index out of sync (subtle bug class).
//
//   Cluster F (CachedViews): StateVersion + CachedTestSummary + FailureNarratives
//     + CachedEditorAnnotations — ONLY written by finalizeLiveTestingState, read by
//     SSE dedup/MCP tools/editor annotations. These are derived state that should
//     NEVER be set from business logic.
//
// WHAT WE INTRODUCE
// ─────────────────
// 1. TestStatusIndex record — atomic container for Entries + Slots + Index + Projection
//    • TestStatusIndex.fromEntries is the ONLY way to construct a populated index
//    • This makes partial updates impossible at the type level
//    • TestStatusIndex.empty is the zero element
//
// 2. CachedViews record — groups all finalize-only derived state
//    • StateVersion, TestSummary, FailureNarratives, EditorAnnotations
//    • Makes it explicit that these are projections, not source-of-truth
//    • CachedViews.empty is the zero element
//
// 3. LiveTestState.StatusIndex replaces 4 flat fields
// 4. LiveTestState.Cached replaces 4 flat fields
// 5. LiveTestState.withStatusEntries uses TestStatusIndex.fromEntries internally
//
// INVARIANTS UNDER TEST
// ─────────────────────
// • TestStatusIndex.empty has empty arrays and maps
// • TestStatusIndex.fromEntries produces consistent Entries/Slots/Index
// • TestStatusIndex.fromEntries is idempotent (fromEntries ∘ .Entries = identity)
// • Slots and Index always agree with Entries (no partial update risk)
// • CachedViews.empty has zero version, empty summary, no narratives, no annotations
// • LiveTestState.empty uses sub-record empties
// • LiveTestState.withStatusEntries delegates to TestStatusIndex.fromEntries
// • Accessor functions (statusEntryIndex, tryFindStatusEntry, orderedStatusEntries,
//   statusEntriesForSession) route through StatusIndex correctly
//
// WHAT THIS DOES NOT COVER
// ────────────────────────
// • Remaining sub-records: TestRegistry, RunTracker, TestResultStore,
//   CoverageState, LiveTestConfig (future Phase 1b work)
// • DiscoveryLifecycle DU (Phase 2)
// • Cold-start race fix (Phase 3)

// ─────────────────────────────────────────────────────────────────────
// § 6  TestStatusIndex record — atomic denormalized index
// ─────────────────────────────────────────────────────────────────────

module TestStatusIndexTests =

  let private mkEntry name status =
    let tid = TestId.create name TestFramework.Expecto
    { TestId = tid; DisplayName = name; FullName = name
      Origin = TestOrigin.ReflectionOnly
      Framework = TestFramework.Expecto; Category = TestCategory.Unit
      CurrentPolicy = RunPolicy.OnEveryChange
      Status = status; PreviousStatus = TestRunStatus.Detected }

  /// TestStatusIndex.empty should be the zero element:
  /// no entries, no lookups, Materialized projection.
  let ``TestStatusIndex_empty is the zero element`` =
    test "TestStatusIndex.empty is the zero element" {
      let z = TestStatusIndex.empty
      z.Entries |> Expect.isEmpty "entries should be empty"
      z.Slots |> Expect.isEmpty "slots should be empty"
      z.Index |> Expect.isEmpty "index should be empty"
      z.Projection |> Expect.equal "projection should be Materialized" StatusEntriesProjectionState.Materialized
    }

  /// fromEntries is the ONLY constructor — it produces consistent Entries/Slots/Index.
  /// This is the core invariant that prevents the partial-update bugs we had before.
  let ``fromEntries produces consistent Entries Slots and Index`` =
    test "fromEntries produces consistent Entries, Slots, and Index" {
      let e1 = mkEntry "Test.alpha" (TestRunStatus.Passed TimeSpan.Zero)
      let e2 = mkEntry "Test.beta" (TestRunStatus.Failed (TestFailure.AssertionFailed "boom", TimeSpan.Zero))
      let e3 = mkEntry "Test.gamma" TestRunStatus.Detected
      let idx = TestStatusIndex.fromEntries [| e1; e2; e3 |]

      // Entries preserved
      idx.Entries |> Expect.hasLength "should have 3 entries" 3

      // Slots map every TestId → positional index
      idx.Slots |> Map.count |> Expect.equal "slots should have 3 entries" 3
      idx.Slots |> Map.find e1.TestId |> Expect.equal "alpha at position 0" 0
      idx.Slots |> Map.find e2.TestId |> Expect.equal "beta at position 1" 1
      idx.Slots |> Map.find e3.TestId |> Expect.equal "gamma at position 2" 2

      // Index map every TestId → entry with correct data
      idx.Index |> Map.count |> Expect.equal "index should have 3 entries" 3
      let found = idx.Index |> Map.find e2.TestId
      found.Status |> Expect.equal "beta should be Failed" e2.Status

      // Projection is always Materialized from fromEntries
      idx.Projection |> Expect.equal "projection should be Materialized" StatusEntriesProjectionState.Materialized
    }

  /// fromEntries is idempotent: applying it to its own Entries produces the same index.
  let ``fromEntries is idempotent`` =
    test "fromEntries is idempotent" {
      let e1 = mkEntry "Test.one" (TestRunStatus.Passed TimeSpan.Zero)
      let e2 = mkEntry "Test.two" TestRunStatus.Detected
      let idx1 = TestStatusIndex.fromEntries [| e1; e2 |]
      let idx2 = TestStatusIndex.fromEntries idx1.Entries

      idx2.Entries |> Expect.equal "entries should be identical" idx1.Entries
      idx2.Slots |> Expect.equal "slots should be identical" idx1.Slots
      idx2.Index |> Expect.equal "index should be identical" idx1.Index
    }

  /// buildSlots and buildIndex agree: every entry in Slots is findable in Index.
  let ``Slots and Index always agree with Entries`` =
    test "Slots and Index always agree with Entries" {
      let entries = [|
        mkEntry "A.test" (TestRunStatus.Passed TimeSpan.Zero)
        mkEntry "B.test" TestRunStatus.Detected
        mkEntry "C.test" TestRunStatus.Stale
      |]
      let idx = TestStatusIndex.fromEntries entries

      for entry in entries do
        // Every entry has a slot
        idx.Slots |> Map.containsKey entry.TestId
        |> Expect.isTrue (sprintf "%s should have a slot" entry.DisplayName)

        // Every entry's slot points to the correct entry
        let pos = idx.Slots |> Map.find entry.TestId
        idx.Entries.[pos].TestId |> Expect.equal (sprintf "%s slot maps correctly" entry.DisplayName) entry.TestId

        // Every entry is in the index
        let indexed = idx.Index |> Map.find entry.TestId
        indexed.Status |> Expect.equal (sprintf "%s index has correct status" entry.DisplayName) entry.Status
    }

  /// fromEntries with empty array produces the same structure as empty.
  let ``fromEntries empty array equals TestStatusIndex_empty`` =
    test "fromEntries with empty array equals TestStatusIndex.empty" {
      let idx = TestStatusIndex.fromEntries Array.empty
      idx |> Expect.equal "should equal empty" TestStatusIndex.empty
    }

  let tests = testList "TestStatusIndex record" [
    ``TestStatusIndex_empty is the zero element``
    ``fromEntries produces consistent Entries Slots and Index``
    ``fromEntries is idempotent``
    ``Slots and Index always agree with Entries``
    ``fromEntries empty array equals TestStatusIndex_empty``
  ]

// ─────────────────────────────────────────────────────────────────────
// § 7  CachedViews record — derived state grouping
// ─────────────────────────────────────────────────────────────────────

module CachedViewsTests =

  /// CachedViews.empty should produce a coherent zero:
  /// version 0, empty summary, no narratives, no annotations.
  let ``CachedViews_empty is the zero element`` =
    test "CachedViews.empty is the zero element" {
      let z = CachedViews.empty
      z.StateVersion |> Expect.equal "version should be 0" 0L
      z.TestSummary.Total |> Expect.equal "total should be 0" 0
      z.TestSummary.Passed |> Expect.equal "passed should be 0" 0
      z.TestSummary.Failed |> Expect.equal "failed should be 0" 0
      z.TestSummary.Stale |> Expect.equal "stale should be 0" 0
      z.TestSummary.Running |> Expect.equal "running should be 0" 0
      z.TestSummary.Disabled |> Expect.equal "disabled should be 0" 0
      z.TestSummary.Enabled |> Expect.isTrue "should be enabled"
      z.FailureNarratives |> Expect.isEmpty "narratives should be empty"
      z.EditorAnnotations |> Expect.isEmpty "annotations should be empty"
    }

  /// CachedViews fields are independently updatable via record update syntax.
  /// This proves the nested update path works: { state.Cached with StateVersion = x }.
  let ``CachedViews supports nested record update`` =
    test "CachedViews supports nested record update" {
      let c0 = CachedViews.empty
      let c1 = { c0 with StateVersion = 42L }
      c1.StateVersion |> Expect.equal "version should be 42" 42L
      c1.TestSummary |> Expect.equal "summary unchanged" c0.TestSummary
      c1.FailureNarratives |> Expect.isEmpty "narratives unchanged"
      c1.EditorAnnotations |> Expect.isEmpty "annotations unchanged"
    }

  /// Multiple CachedViews fields can be updated in one expression.
  let ``CachedViews multi-field update`` =
    test "CachedViews multi-field update preserves unmentioned fields" {
      let c = {
        CachedViews.empty with
          StateVersion = 10L
          TestSummary = { Total = 5; Passed = 3; Failed = 2; Stale = 0; Running = 0; Disabled = 0; Enabled = true }
      }
      c.StateVersion |> Expect.equal "version" 10L
      c.TestSummary.Total |> Expect.equal "total" 5
      c.FailureNarratives |> Expect.isEmpty "narratives still empty"
      c.EditorAnnotations |> Expect.isEmpty "annotations still empty"
    }

  let tests = testList "CachedViews record" [
    ``CachedViews_empty is the zero element``
    ``CachedViews supports nested record update``
    ``CachedViews multi-field update``
  ]

// ─────────────────────────────────────────────────────────────────────
// § 8  LiveTestState uses sub-records correctly
// ─────────────────────────────────────────────────────────────────────

module LiveTestStateSubRecordTests =

  let private mkEntry name status =
    let tid = TestId.create name TestFramework.Expecto
    { TestId = tid; DisplayName = name; FullName = name
      Origin = TestOrigin.ReflectionOnly
      Framework = TestFramework.Expecto; Category = TestCategory.Unit
      CurrentPolicy = RunPolicy.OnEveryChange
      Status = status; PreviousStatus = TestRunStatus.Detected }

  let private mkTestCase' name =
    { Id = TestId.create name TestFramework.Expecto
      FullName = name; DisplayName = name
      Origin = TestOrigin.ReflectionOnly
      Labels = []; Framework = TestFramework.Expecto; Category = TestCategory.Unit }

  /// LiveTestState.empty should use TestStatusIndex.empty and CachedViews.empty internally.
  let ``LiveTestState_empty uses sub-record empties`` =
    test "LiveTestState.empty uses sub-record empties" {
      let s = LiveTestState.empty
      s.StatusIndex |> Expect.equal "StatusIndex should be TestStatusIndex.empty" TestStatusIndex.empty
      s.Cached |> Expect.equal "Cached should be CachedViews.empty" CachedViews.empty
    }

  /// withStatusEntries should atomically update the StatusIndex sub-record.
  /// This is the primary mutation path — it delegates to TestStatusIndex.fromEntries.
  let ``withStatusEntries atomically updates StatusIndex`` =
    test "withStatusEntries atomically updates StatusIndex" {
      let e1 = mkEntry "Test.alpha" (TestRunStatus.Passed TimeSpan.Zero)
      let e2 = mkEntry "Test.beta" TestRunStatus.Detected
      let state = LiveTestState.empty |> LiveTestState.withStatusEntries [| e1; e2 |]

      state.StatusIndex.Entries |> Expect.hasLength "should have 2 entries" 2
      state.StatusIndex.Slots |> Map.count |> Expect.equal "should have 2 slots" 2
      state.StatusIndex.Index |> Map.count |> Expect.equal "should have 2 index entries" 2
      state.StatusIndex.Projection |> Expect.equal "should be Materialized" StatusEntriesProjectionState.Materialized
    }

  /// withStatusEntries should not touch Cached — those are only set by finalize.
  let ``withStatusEntries does not modify Cached`` =
    test "withStatusEntries does not modify Cached" {
      let state =
        { LiveTestState.empty with
            Cached = { CachedViews.empty with StateVersion = 99L } }
      let e = mkEntry "T" (TestRunStatus.Passed TimeSpan.Zero)
      let updated = state |> LiveTestState.withStatusEntries [| e |]
      updated.Cached.StateVersion |> Expect.equal "Cached should be untouched" 99L
    }

  /// statusEntryIndex should route through StatusIndex.Index.
  let ``statusEntryIndex reads from StatusIndex`` =
    test "statusEntryIndex reads from StatusIndex" {
      let e1 = mkEntry "Find.me" (TestRunStatus.Passed TimeSpan.Zero)
      let state = LiveTestState.empty |> LiveTestState.withStatusEntries [| e1 |]
      let index = LiveTestState.statusEntryIndex state
      index |> Map.containsKey e1.TestId |> Expect.isTrue "should find entry by TestId"
    }

  /// tryFindStatusEntry should find an entry that was set via withStatusEntries.
  let ``tryFindStatusEntry finds entries in StatusIndex`` =
    test "tryFindStatusEntry finds entries in StatusIndex" {
      let e1 = mkEntry "Lookup.test" (TestRunStatus.Failed (TestFailure.AssertionFailed "nope", TimeSpan.Zero))
      let state = LiveTestState.empty |> LiveTestState.withStatusEntries [| e1 |]
      let found = LiveTestState.tryFindStatusEntry e1.TestId state
      found |> Expect.isSome "should find the entry"
      found.Value.Status |> Expect.equal "should have Failed status" e1.Status
    }

  /// tryFindStatusEntry should return None for an unknown TestId.
  let ``tryFindStatusEntry returns None for unknown TestId`` =
    test "tryFindStatusEntry returns None for unknown TestId" {
      let state = LiveTestState.empty
      let phantom = TestId.create "Ghost.test" TestFramework.Expecto
      let found = LiveTestState.tryFindStatusEntry phantom state
      found |> Expect.isNone "should not find phantom"
    }

  /// orderedStatusEntries with Materialized projection returns Entries as-is.
  let ``orderedStatusEntries returns Entries when Materialized`` =
    test "orderedStatusEntries returns Entries when Materialized" {
      let e1 = mkEntry "A" (TestRunStatus.Passed TimeSpan.Zero)
      let e2 = mkEntry "B" TestRunStatus.Detected
      let state = LiveTestState.empty |> LiveTestState.withStatusEntries [| e1; e2 |]
      let ordered = LiveTestState.orderedStatusEntries state
      ordered |> Expect.hasLength "should have 2" 2
      ordered.[0].DisplayName |> Expect.equal "first should be A" "A"
      ordered.[1].DisplayName |> Expect.equal "second should be B" "B"
    }

  /// statusEntriesForSession filters through the StatusIndex correctly.
  let ``statusEntriesForSession uses StatusIndex`` =
    test "statusEntriesForSession filters through StatusIndex" {
      let e1 = mkEntry "SessionA.test" (TestRunStatus.Passed TimeSpan.Zero)
      let e2 = mkEntry "SessionB.test" (TestRunStatus.Passed TimeSpan.Zero)
      let state =
        LiveTestState.empty
        |> LiveTestState.withStatusEntries [| e1; e2 |]
        |> fun s -> { s with TestSessionMap = Map.ofList [ e1.TestId, "sessA"; e2.TestId, "sessB" ] }
      let sessA = LiveTestState.statusEntriesForSession "sessA" state
      sessA |> Expect.hasLength "should have 1 entry for sessA" 1
      sessA.[0].TestId |> Expect.equal "should be the sessA entry" e1.TestId
    }

  /// Nested record update on LiveTestState.Cached works for finalize-style updates.
  let ``nested Cached update works on LiveTestState`` =
    test "nested Cached update works on LiveTestState" {
      let state = LiveTestState.empty
      let updated =
        { state with
            Cached = {
              state.Cached with
                StateVersion = 7L
                TestSummary = { Total = 10; Passed = 8; Failed = 2; Stale = 0; Running = 0; Disabled = 0; Enabled = true }
            } }
      updated.Cached.StateVersion |> Expect.equal "version" 7L
      updated.Cached.TestSummary.Total |> Expect.equal "total" 10
      updated.Cached.TestSummary.Passed |> Expect.equal "passed" 8
      updated.Cached.FailureNarratives |> Expect.isEmpty "narratives still empty"
      updated.Cached.EditorAnnotations |> Expect.isEmpty "annotations still empty"
      // StatusIndex should be untouched
      updated.StatusIndex |> Expect.equal "StatusIndex unchanged" TestStatusIndex.empty
    }

  let tests = testList "LiveTestState sub-record integration" [
    ``LiveTestState_empty uses sub-record empties``
    ``withStatusEntries atomically updates StatusIndex``
    ``withStatusEntries does not modify Cached``
    ``statusEntryIndex reads from StatusIndex``
    ``tryFindStatusEntry finds entries in StatusIndex``
    ``tryFindStatusEntry returns None for unknown TestId``
    ``orderedStatusEntries returns Entries when Materialized``
    ``statusEntriesForSession uses StatusIndex``
    ``nested Cached update works on LiveTestState``
  ]

// ─────────────────────────────────────────────────────────────────────
// § 9  Property-based: TestStatusIndex consistency
// ─────────────────────────────────────────────────────────────────────

module TestStatusIndexPropertyTests =

  open FsCheck

  /// Property: Slots count always equals Entries length after fromEntries.
  let ``Slots count equals Entries length`` =
    testProperty "Slots count equals Entries length" (fun (PositiveInt n) ->
      let count = min n 20  // keep it small
      let entries =
        List.init count (fun i ->
          let name = sprintf "Prop.test%d" i
          let tid = TestId.create name TestFramework.Expecto
          { TestId = tid; DisplayName = name; FullName = name
            Origin = TestOrigin.ReflectionOnly
            Framework = TestFramework.Expecto; Category = TestCategory.Unit
            CurrentPolicy = RunPolicy.OnEveryChange
            Status = TestRunStatus.Detected; PreviousStatus = TestRunStatus.Detected })
        |> Array.ofList
      let idx = TestStatusIndex.fromEntries entries
      Map.count idx.Slots = entries.Length
    )

  /// Property: Index count always equals Entries length after fromEntries.
  let ``Index count equals Entries length`` =
    testProperty "Index count equals Entries length" (fun (PositiveInt n) ->
      let count = min n 20
      let entries =
        List.init count (fun i ->
          let name = sprintf "Prop.test%d" i
          let tid = TestId.create name TestFramework.Expecto
          { TestId = tid; DisplayName = name; FullName = name
            Origin = TestOrigin.ReflectionOnly
            Framework = TestFramework.Expecto; Category = TestCategory.Unit
            CurrentPolicy = RunPolicy.OnEveryChange
            Status = TestRunStatus.Detected; PreviousStatus = TestRunStatus.Detected })
        |> Array.ofList
      let idx = TestStatusIndex.fromEntries entries
      Map.count idx.Index = entries.Length
    )

  /// Property: every Slot position is a valid index into Entries.
  let ``every Slot position is a valid Entries index`` =
    testProperty "every Slot position is a valid Entries index" (fun (PositiveInt n) ->
      let count = min n 20
      let entries =
        List.init count (fun i ->
          let name = sprintf "Bound.test%d" i
          let tid = TestId.create name TestFramework.Expecto
          { TestId = tid; DisplayName = name; FullName = name
            Origin = TestOrigin.ReflectionOnly
            Framework = TestFramework.Expecto; Category = TestCategory.Unit
            CurrentPolicy = RunPolicy.OnEveryChange
            Status = (TestRunStatus.Passed TimeSpan.Zero); PreviousStatus = TestRunStatus.Detected })
        |> Array.ofList
      let idx = TestStatusIndex.fromEntries entries
      idx.Slots
      |> Map.forall (fun _tid pos -> pos >= 0 && pos < entries.Length)
    )

  /// Property: fromEntries is idempotent for any entry set.
  let ``fromEntries is idempotent for any entries`` =
    testProperty "fromEntries is idempotent" (fun (PositiveInt n) ->
      let count = min n 20
      let entries =
        List.init count (fun i ->
          let name = sprintf "Idem.test%d" i
          let tid = TestId.create name TestFramework.Expecto
          { TestId = tid; DisplayName = name; FullName = name
            Origin = TestOrigin.ReflectionOnly
            Framework = TestFramework.Expecto; Category = TestCategory.Unit
            CurrentPolicy = RunPolicy.OnEveryChange
            Status = TestRunStatus.Detected; PreviousStatus = TestRunStatus.Detected })
        |> Array.ofList
      let idx1 = TestStatusIndex.fromEntries entries
      let idx2 = TestStatusIndex.fromEntries idx1.Entries
      idx1.Entries = idx2.Entries
      && idx1.Slots = idx2.Slots
      && idx1.Index = idx2.Index
      && idx1.Projection = idx2.Projection
    )

  let tests = testList "TestStatusIndex properties" [
    ``Slots count equals Entries length``
    ``Index count equals Entries length``
    ``every Slot position is a valid Entries index``
    ``fromEntries is idempotent for any entries``
  ]

// ─────────────────────────────────────────────────────────────────────
// Root
// ─────────────────────────────────────────────────────────────────────

[<Tests>]
let allTests =
  testList "LiveTestState Decomposition" [
    testList "Phase 1a — TestCycleEffect Payload Records" [
      TestRunRequestTests.tests
      TypeCheckRequestTests.tests
      TestCycleEffectPayloadTests.tests
      EffectProducerTests.tests
      RebuildLifecycleTests.tests
    ]
    testList "Phase 1b — LiveTestState Sub-Record Extraction" [
      TestStatusIndexTests.tests
      CachedViewsTests.tests
      LiveTestStateSubRecordTests.tests
      TestStatusIndexPropertyTests.tests
    ]
  ]
