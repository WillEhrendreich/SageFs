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

/// The name-delta shape every pre-existing scenario below exercises: the
/// type-check reported which symbol NAMES moved, and nothing about what the
/// file contains.
let nameDelta changed = FileSymbolDelta.ofChangedOnly changed

[<Tests>]
let tests =
  testList "Live testing afterTypeCheck scenarios" [
    // Rewrite `let add a b = a + b` to `a - b`. The signature is identical, so
    // the symbol-NAME set is identical, so the `Set<string>` difference that
    // produces `Changed` is EMPTY — and before the file's own symbol set was
    // threaded through, that empty delta was read as "no semantic change",
    // selected zero tests, and reported green on a real regression. Only the
    // cohort landing gate stood between a body-edit regression and master.
    testCase "a body-only edit on save still selects the tests that reach the edited file" <| fun _ ->
      let impacted = mkTest "Module.Tests.should_add" TestCategory.Unit
      let unrelated = mkTest "Other.Tests.should_greet" TestCategory.Unit
      let state =
        { LiveTestState.empty with
            Activation = LiveTestingActivation.Active
            DiscoveredTests = [| impacted; unrelated |] }
      // A populated graph — this is the steady state, not a cold start, and it
      // is exactly the case the old guard declined to fall back on.
      let graph =
        exactGraph [ "Module.add", [| impacted.Id |]
                     "Other.greet", [| unrelated.Id |] ]

      let outcome =
        TestCycleEffects.decideAfterTypeCheck
          { Changed = []; InFile = [ "Module.add" ]; Lines = ChangedLines.NoBaseline }
          "Module.fs"
          RunTrigger.FileSave
          graph
          state
          None
          Map.empty

      match outcome.Decision, outcome.Effects with
      | Some decision, [ TestCycleEffect.RequestRebuild(_, req) ] ->
        req.Tests
        |> Array.map (fun tc -> tc.Id)
        |> Expect.equal "only the tests reaching the edited file should be selected" [| impacted.Id |]
        decision.Explanation.Precision
        |> Expect.notEqual
          "a body-only edit must never be reported as 'no impacted tests' — that is the green lie"
          SelectionPrecision.NoImpactedTests
      | other -> failtestf "expected a rebuild-and-run covering the edited file, got %A" other

    // The other half of the contract: when the narrow DOES find the covering
    // test, selecting on file scope must not mean selecting everything (the
    // first test above pins that). When it finds nothing at all, it must not
    // mean selecting nothing either: no test the graph knows reaches the file,
    // but the graph may not have seen the test that does, so the whole
    // discovered set is queued and the decision says it widened.
    testCase "a body-only edit that no known test reaches widens to the discovered tests and says so" <| fun _ ->
      let unrelated = mkTest "Other.Tests.should_greet" TestCategory.Unit
      let state =
        { LiveTestState.empty with
            Activation = LiveTestingActivation.Active
            DiscoveredTests = [| unrelated |] }
      let graph = exactGraph [ "Other.greet", [| unrelated.Id |] ]

      let outcome =
        TestCycleEffects.decideAfterTypeCheck
          { Changed = []; InFile = [ "Module.add" ]; Lines = ChangedLines.NoBaseline }
          "Module.fs"
          RunTrigger.FileSave
          graph
          state
          None
          Map.empty

      outcome.Effects
      |> List.isEmpty
      |> Expect.isFalse "an unreached compiled file must not read as green"
      match outcome.Decision with
      | Some decision ->
        decision.Explanation.Precision
        |> Expect.equal "the widening is labeled" SelectionPrecision.ConservativeFallback
      | None -> failtest "expected a decision"

    // The keystroke cause is the as-you-type path: the type-check of a settled
    // buffer reports `Changed = []` for `a + b` -> `a - b` exactly as it does on
    // save, because the name set did not move. The save-only guard left this
    // path selecting nothing, so the pane stayed green on a real regression.
    testCase "a body-only keystroke edit selects the tests that reach the edited file" <| fun _ ->
      let impacted = mkTest "Module.Tests.should_add" TestCategory.Unit
      let unrelated = mkTest "Other.Tests.should_greet" TestCategory.Unit
      let state =
        { LiveTestState.empty with
            Activation = LiveTestingActivation.Active
            DiscoveredTests = [| impacted; unrelated |] }
      let graph =
        exactGraph [ "Module.add", [| impacted.Id |]
                     "Other.greet", [| unrelated.Id |] ]

      let outcome =
        TestCycleEffects.decideAfterTypeCheck
          { Changed = []; InFile = [ "Module.add" ]; Lines = ChangedLines.NoBaseline }
          "Module.fs"
          RunTrigger.Keystroke
          graph
          state
          None
          Map.empty

      match outcome.Decision, outcome.Effects with
      | Some decision, [ TestCycleEffect.RunAffectedTests req ] ->
        req.Tests
        |> Array.map (fun tc -> tc.Id)
        |> Expect.equal "only the tests reaching the edited file should be selected" [| impacted.Id |]
        decision.Explanation.Precision
        |> Expect.notEqual
          "a body-only keystroke edit must never be reported as 'no impacted tests'"
          SelectionPrecision.NoImpactedTests
      | other -> failtestf "expected a run covering the edited file on a keystroke, got %A" other

    // Selecting on file scope per keystroke does not thrash the runner,
    // because the run policy decides whether a keystroke may run: a category
    // set to save-only stays quiet, and says so instead of reading as green.
    testCase "a body-only keystroke edit honors a save-only run policy and says it deferred" <| fun _ ->
      let impacted = mkTest "Module.Tests.should_add" TestCategory.Unit
      let state =
        { LiveTestState.empty with
            Activation = LiveTestingActivation.Active
            RunPolicies = Map.ofList [ TestCategory.Unit, RunPolicy.OnSaveOnly ]
            DiscoveredTests = [| impacted |] }
      let graph = exactGraph [ "Module.add", [| impacted.Id |] ]

      let outcome =
        TestCycleEffects.decideAfterTypeCheck
          { Changed = []; InFile = [ "Module.add" ]; Lines = ChangedLines.NoBaseline }
          "Module.fs"
          RunTrigger.Keystroke
          graph
          state
          None
          Map.empty

      outcome.Effects
      |> Expect.isEmpty "a save-only category must not run on a keystroke"
      match outcome.Decision with
      | Some decision ->
        decision.Explanation.Precision
        |> Expect.equal "the silence is attributed to policy, not to 'no impacted tests'" SelectionPrecision.SuppressedByPolicy
      | None -> failtest "expected a decision explaining the deferral"

    testCase "when only the dependency graph explains the change, afterTypeCheck should report an exact decision so the user can trust the surgical rerun" <| fun _ ->
      let impacted = mkTest "Module.Tests.should_add" TestCategory.Unit
      let state =
        { LiveTestState.empty with
            Activation = LiveTestingActivation.Active
            DiscoveredTests = [| impacted |] }
      let graph = exactGraph [ "Module.add", [| impacted.Id |] ]

      let outcome =
        TestCycleEffects.decideAfterTypeCheck
          (nameDelta [ "Module.add" ])
          "Module.fs"
          RunTrigger.Keystroke
          graph
          state
          None
          Map.empty

      match outcome.Decision, outcome.Effects with
      | Some decision, [ TestCycleEffect.RunAffectedTests req ] ->
        decision.Explanation.Precision |> Expect.equal "pure graph selection should stay exact" SelectionPrecision.ExactDependencyMatch
        decision.Trust |> Expect.equal "exact decisions should keep exact trust" FreshnessTrust.FreshExact
        decision.Explanation.SelectedTests |> Expect.equal "the directly impacted test should stay visible" [| impacted.FullName |]
        req.Tests |> Array.map (fun tc -> tc.Id) |> Expect.equal "the run request should target only the impacted test" [| impacted.Id |]
      | other -> failtestf "expected exact decision plus run-affected effect for keystroke, got %A" other

    testCase "when coverage widens the selection beyond the symbol graph, afterTypeCheck should say approximation out loud so extra reruns are explained instead of feeling random" <| fun _ ->
      let symbolTest = mkTest "Module.Tests.should_add" TestCategory.Unit
      let coverageTest = mkTest "Module.Tests.should_guard_edges" TestCategory.Unit
      let state =
        { LiveTestState.empty with
            Activation = LiveTestingActivation.Active
            DiscoveredTests = [| symbolTest; coverageTest |]
            TestCoverageBitmaps = Map.ofList [ coverageTest.Id, CoverageBitmap.ofBoolArray [| true |] ]
            SessionDiscovery = Map.ofList [ "s", DiscoveryProgress.Completed ] }
      let graph = exactGraph [ "Module.add", [| symbolTest.Id |] ]
      let maps =
        [| { Slots = [| { File = "Module.fs"; Line = 1; Column = 0; EndLine = 1; EndColumn = 10; BranchId = 0 } |]
             TotalProbes = 1
             TrackerTypeName = "t"
             HitsFieldName = "h"; Source = MapSource.none } |]
      let outcome =
        TestCycleEffects.decideAfterTypeCheck
          (nameDelta [ "Module.add" ])
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
          (nameDelta [])
          "Compiled.fs"
          RunTrigger.FileSave
          TestDependencyGraph.empty
          state
          None
          Map.empty

      match outcome.Decision, outcome.Effects with
      | Some decision, [ TestCycleEffect.RequestRebuild (_, req) ] ->
        decision.Explanation.Precision |> Expect.equal "compiled fallback should be explicit" SelectionPrecision.ConservativeFallback
        decision.Trust |> Expect.equal "fallback should not claim exact trust" FreshnessTrust.FreshApproximate
        decision.Explanation.SelectedTests |> Array.sort |> Expect.equal "all discovered tests should be named in the fallback" ([| tc1.FullName; tc2.FullName |] |> Array.sort)
        req.Tests |> Array.map (fun tc -> tc.FullName) |> Array.sort |> Expect.equal "the rebuild request should carry every discovered test" ([| tc1.FullName; tc2.FullName |] |> Array.sort)
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
          (nameDelta [ "Architecture.Rule" ])
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
          (nameDelta [ "Unknown.symbol" ])
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

// ---------------------------------------------------------------------------
// The keystroke trivia gate. A keystroke whose buffer normalizes to the last
// one cancels the type-check and queues nothing, so the normalizer may only
// drop what F# ignores. F# is offside-sensitive: where a token sits on its
// line, and whether two tokens touch, changes what the program means.
// ---------------------------------------------------------------------------

let private triviaStart = System.DateTimeOffset(2026, 10, 1, 0, 0, 0, System.TimeSpan.Zero)

let private isFcsRequest effect =
  match effect with
  | TestCycleEffect.RequestFcsTypeCheck _ -> true
  | _ -> false

/// Types `previous`, lets its check fire (so nothing is pending), types `next`,
/// and says whether a type-check of `next` is requested.
let private checkRequestedAfterEdit (previous: string) (next: string) : bool =
  let s1 = LiveTestCycleState.empty |> LiveTestCycleState.onKeystroke previous "Lib.fs" triviaStart
  let _, s2 = s1 |> LiveTestCycleState.tick (triviaStart + DebounceClock.pastFcs)
  let editedAt = triviaStart + DebounceClock.pastFcs + DebounceClock.keyGap
  let s3 = s2 |> LiveTestCycleState.onKeystroke next "Lib.fs" editedAt
  let effects, _ = s3 |> LiveTestCycleState.tick (editedAt + DebounceClock.pastFcs)
  List.exists isFcsRequest effects

[<Tests>]
let triviaNormalizationTests =
  testList "Live testing keystroke trivia gate" [
    // Moving a line into or out of an offside block changes which expression
    // it belongs to. Dropping every whitespace character made these identical.
    testCase "moving a line into an offside block is a change and gets type-checked" <| fun _ ->
      checkRequestedAfterEdit
        "let f x =\n  if x then\n    printfn \"a\"\n  printfn \"b\"\n"
        "let f x =\n  if x then\n    printfn \"a\"\n    printfn \"b\"\n"
      |> Expect.isTrue "the second printfn now runs only when x is true"

    testCase "moving a line out of an offside block is a change and gets type-checked" <| fun _ ->
      checkRequestedAfterEdit
        "let f x =\n  if x then\n    printfn \"a\"\n    printfn \"b\"\n"
        "let f x =\n  if x then\n    printfn \"a\"\n  printfn \"b\"\n"
      |> Expect.isTrue "the second printfn now runs whatever x is"

    testCase "joining two lines is a change and gets type-checked" <| fun _ ->
      checkRequestedAfterEdit
        "let f g x =\n  g\n  x\n"
        "let f g x =\n  g x\n"
      |> Expect.isTrue "two statements became one application"

    testCase "a space between tokens is a change and gets type-checked" <| fun _ ->
      checkRequestedAfterEdit "let y = x -1\n" "let y = x - 1\n"
      |> Expect.isTrue "an application of x to -1 became a subtraction"

    // What F# really ignores still does not start a run.
    testCase "a comment, trailing spaces, blank lines and CRLF are still trivia" <| fun _ ->
      let previous = "let f x =\n  if x then\n    1\n  else\n    2\n"
      [ "let f x = // pick one\n  if x then\n    1\n  else\n    2\n"
        "let f x =\n  if x then   \n    1\n  else\n    2\n"
        "let f x =\n\n  if x then\n    1\n\n  else\n    2\n"
        "let f x =\r\n  if x then\r\n    1\r\n  else\r\n    2\r\n"
        "let f x =\n  if x   then\n    1\n  else\n    2\n"
        "let f x = (* why *)\n  if x then\n    1\n  else\n    2\n" ]
      |> List.iter (fun next ->
        checkRequestedAfterEdit previous next
        |> Expect.isFalse (sprintf "this edit moves no token, so no check: %A" next))

    // A real edit followed, inside its debounce window, by a comment keystroke:
    // the comment keystroke is trivia relative to the buffer BEFORE it, which was
    // never checked. Cancelling the pending check drops the real edit.
    testCase "a comment typed right after a real edit does not cancel that edit's check" <| fun _ ->
      let s1 = LiveTestCycleState.empty |> LiveTestCycleState.onKeystroke "let add a b = a + b\n" "Lib.fs" triviaStart
      let _, s2 = s1 |> LiveTestCycleState.tick (triviaStart + DebounceClock.pastFcs)
      let editedAt = triviaStart + DebounceClock.pastFcs + DebounceClock.keyGap
      let s3 = s2 |> LiveTestCycleState.onKeystroke "let add a b = a - b\n" "Lib.fs" editedAt
      let commentedAt = editedAt + DebounceClock.keyGap
      let s4 = s3 |> LiveTestCycleState.onKeystroke "let add a b = a - b // x\n" "Lib.fs" commentedAt
      let effects, _ = s4 |> LiveTestCycleState.tick (commentedAt + DebounceClock.pastFcs)
      effects
      |> List.exists isFcsRequest
      |> Expect.isTrue "the `+` to `-` edit must still be type-checked"
  ]

// ---------------------------------------------------------------------------
// The live loop must never turn "the narrow found nothing" into a green pane.
// These drive `LiveTestCycleState.handleFcsResult`, the function the keystroke
// path really calls, so `onFcsComplete` (name diff, graph update) runs first
// exactly as it does in the daemon.
// ---------------------------------------------------------------------------

let private libRefs : SymbolReference list =
  [ { SymbolFullName = "Lib.add"; UseKind = SymbolUseKind.Definition; UsedInTestId = None; FilePath = "Lib.fs"; Line = 1 } ]

/// A graph built the way the daemon builds one: from a type-checked test file,
/// through `SymbolGraphBuilder.updateGraph`, so it is registered per file and
/// survives the graph update that `handleFcsResult` does before it decides.
/// (A graph assigned straight to `SymbolToTests` is rebuilt from nothing by that
/// update, which is a cold start, not a populated graph.) It knows `Other.greet`
/// and nothing about `Lib.add`.
let private graphThatHasNotSeenLib : TestDependencyGraph =
  let otherTestFile : SymbolReference list =
    [ { SymbolFullName = "Other.Tests.greets"; UseKind = SymbolUseKind.Definition; UsedInTestId = None; FilePath = "Other.Tests.fs"; Line = 1 }
      { SymbolFullName = "Other.greet"; UseKind = SymbolUseKind.Reference; UsedInTestId = None; FilePath = "Other.Tests.fs"; Line = 2 } ]
  SymbolGraphBuilder.updateGraph
    LiveTestingDefaults.TestModuleIdentifier
    LiveTestingDefaults.Framework
    otherTestFile
    "Other.Tests.fs"
    TestDependencyGraph.empty

/// A session that has already type-checked `Lib.fs` once, so a second check of
/// the same symbols reports `Changed = []`: the body-only edit shape.
let private afterFirstCheck trigger (tests: TestCase array) (graph: TestDependencyGraph) : LiveTestCycleState =
  { LiveTestCycleState.empty with
      TestState =
        { LiveTestState.empty with
            Activation = LiveTestingActivation.Active
            DiscoveredTests = tests }
      DepGraph = graph
      AnalysisCache = { FileSymbols = Map.ofList [ "Lib.fs", libRefs ] }
      LastTrigger = trigger
      ActiveFile = Some "Lib.fs"
      LatestContent = Some "module Lib\nlet add a b = a - b" }

let private recheck (state: LiveTestCycleState) =
  LiveTestCycleState.handleFcsResult (FcsTypeCheckResult.Success ("Lib.fs", libRefs)) state

[<Tests>]
let noEmptyEscapeTests =
  testList "Live testing never reads an empty selection as green" [
    // Cold start. The dependency graph has seen no test file at all, so the
    // name delta (empty: same symbols) and the file-scope narrow (nothing in
    // the graph) both find nothing. On a save that already widens to every
    // discovered test; on a keystroke it selected nothing and said "no impacted
    // tests", which the pane shows as green on a real regression.
    testCase "a body-only keystroke edit with an empty dependency graph selects the discovered tests, not nothing" <| fun _ ->
      let t1 = mkTest "Lib.Tests.adds" TestCategory.Unit
      let t2 = mkTest "Other.Tests.greets" TestCategory.Unit
      let effects, state' =
        afterFirstCheck RunTrigger.Keystroke [| t1; t2 |] TestDependencyGraph.empty
        |> recheck
      match state'.TestState.LastDecision with
      | Some decision ->
        decision.Explanation.Precision
        |> Expect.equal "nothing could be narrowed, so the decision must say it widened" SelectionPrecision.ConservativeFallback
        decision.Explanation.SelectedTests
        |> Array.sort
        |> Expect.equal "every discovered test is selected" ([| t1.FullName; t2.FullName |] |> Array.sort)
        effects
        |> List.isEmpty
        |> Expect.isFalse "a run must actually be queued"
      | None -> failtest "expected a decision"

    // Same cold start, same edit, on save: the existing behavior, pinned so the
    // keystroke fix cannot regress it.
    testCase "the same body-only edit on save with an empty graph still widens to the discovered tests" <| fun _ ->
      let t1 = mkTest "Lib.Tests.adds" TestCategory.Unit
      let _, state' =
        afterFirstCheck RunTrigger.FileSave [| t1 |] TestDependencyGraph.empty
        |> recheck
      match state'.TestState.LastDecision with
      | Some decision ->
        decision.Explanation.SelectedTests |> Expect.equal "the one discovered test" [| t1.FullName |]
      | None -> failtest "expected a decision"

    // Policy still has the last word on a keystroke: a save-only category stays
    // quiet, and the decision names the policy instead of reading as "no impacted
    // tests".
    testCase "the widened keystroke selection still honors a save-only policy and says so" <| fun _ ->
      let t1 = mkTest "Lib.Tests.adds" TestCategory.Unit
      let seeded = afterFirstCheck RunTrigger.Keystroke [| t1 |] TestDependencyGraph.empty
      let state =
        { seeded with
            TestState = { seeded.TestState with RunPolicies = Map.ofList [ TestCategory.Unit, RunPolicy.OnSaveOnly ] } }
      let effects, state' = recheck state
      effects |> Expect.isEmpty "a save-only category must not run on a keystroke"
      match state'.TestState.LastDecision with
      | Some decision ->
        decision.Explanation.Precision
        |> Expect.equal "the silence is policy, not 'no impacted tests'" SelectionPrecision.SuppressedByPolicy
      | None -> failtest "expected a decision"

    // The graph is populated, but only with a test file that has nothing to do
    // with `Lib.fs`: the test that covers `Lib.add` has not been type-checked in
    // this session yet. The file-scope narrow finds nothing, on any trigger, and
    // a non-empty graph used to mean "do not widen". The landing gate would not
    // accept that (`AffectedTests.verificationTestSet` never returns empty for a
    // real diff against a real suite), so the live loop does not either.
    for trigger in [ RunTrigger.Keystroke; RunTrigger.FileSave; RunTrigger.ExplicitRun ] do
      testCase (sprintf "a body-only edit on %A that no known test reaches widens instead of reading green" trigger) <| fun _ ->
        let covering = mkTest "Lib.Tests.adds" TestCategory.Unit
        let other = mkTest "Other.Tests.greets" TestCategory.Unit
        Map.isEmpty graphThatHasNotSeenLib.SymbolToTests
        |> Expect.isFalse "the graph is populated: this is not a cold start"
        let effects, state' =
          afterFirstCheck trigger [| covering; other |] graphThatHasNotSeenLib
          |> recheck
        match state'.TestState.LastDecision with
        | Some decision ->
          decision.Explanation.Precision
          |> Expect.equal "the narrow found nothing, so the decision must say it widened" SelectionPrecision.ConservativeFallback
          decision.Explanation.SelectedTests
          |> Array.sort
          |> Expect.equal "the test the graph has not seen yet is among the selection" ([| covering.FullName; other.FullName |] |> Array.sort)
          decision.Explanation.Reason
          |> System.String.IsNullOrWhiteSpace
          |> Expect.isFalse "the decision says why it widened"
          effects
          |> List.isEmpty
          |> Expect.isFalse "a run must actually be queued"
        | None -> failtest "expected a decision"

    // A script is evaluated, not compiled, so there is no stale DLL to distrust
    // and nothing to widen: the floor is for compiled files only.
    testCase "a body-only edit to a script file with a populated graph does not widen" <| fun _ ->
      let other = mkTest "Other.Tests.greets" TestCategory.Unit
      let seeded = afterFirstCheck RunTrigger.Keystroke [| other |] graphThatHasNotSeenLib
      let state = { seeded with AnalysisCache = { FileSymbols = Map.ofList [ "Lib.fsx", libRefs ] } }
      let _, state' =
        LiveTestCycleState.handleFcsResult (FcsTypeCheckResult.Success ("Lib.fsx", libRefs)) state
      match state'.TestState.LastDecision with
      | Some decision ->
        decision.Explanation.Precision
        |> Expect.equal "a script no test reaches is genuinely unaffected" SelectionPrecision.NoImpactedTests
      | None -> failtest "expected a decision"
  ]
