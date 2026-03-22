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

// ─────────────────────────────────────────────────────────────────────
// Root
// ─────────────────────────────────────────────────────────────────────

[<Tests>]
let allTests =
  testList "Phase 1a — TestCycleEffect Payload Records" [
    TestRunRequestTests.tests
    TypeCheckRequestTests.tests
    TestCycleEffectPayloadTests.tests
    EffectProducerTests.tests
    RebuildLifecycleTests.tests
  ]
