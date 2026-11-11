/// `TestCycleEffects.decideAfterTypeCheck` used to answer `LiveTestingDecision option`, where `None` meant "live testing
/// is off" and nothing else. The answer is now a closed set that says which of four things happened, so a caller that
/// wants "no tests were selected" cannot mistake it for "nothing was judged".
module SageFs.Tests.AfterTypeCheckVerdictTests

open Expecto
open Expecto.Flip
open SageFs.Features.LiveTesting
open SageFs.Tests.LiveTestingTestHelpers

let circle = mkTestCase "from C#/discriminated unions/circle area" TestFramework.Expecto TestCategory.Unit
let rectangle = mkTestCase "from C#/discriminated unions/rectangle area" TestFramework.Expecto TestCategory.Unit
let everyTest = [| circle; rectangle |]

let graph : TestDependencyGraph =
  let all = everyTest |> Array.map (fun t -> t.Id)
  { TestDependencyGraph.empty with
      SymbolToTests = Map.ofList [ "Hello.area", all ]
      TransitiveCoverage = Map.ofList [ "Hello.area", all ] }

let stateWith (activation: LiveTestingActivation) (pause: LivePause) (tests: TestCase array) : LiveTestState =
  { LiveTestState.empty with
      Activation = activation
      Pause = pause
      DiscoveredTests = tests }

/// An edit to `path` that names the symbols the dependency graph knows, the way a body-only edit reaches the cycle.
let decide (path: string) (trigger: RunTrigger) (state: LiveTestState) : AfterTypeCheckOutcome =
  TestCycleEffects.decideAfterTypeCheck
    { Changed = []; InFile = [ "Hello.area" ]; Lines = ChangedLines.NoBaseline }
    path
    trigger
    graph
    state
    None
    Map.empty

let decisionOf (outcome: AfterTypeCheckOutcome) : LiveTestingDecision =
  match AfterTypeCheckVerdict.decision outcome.Verdict with
  | Some decision -> decision
  | None -> failtestf "expected a decision, the verdict was %A" outcome.Verdict

[<Tests>]
let verdictTests =
  testList "decideAfterTypeCheck verdict" [

    testCase "live testing off is Inactive, with no decision and no effect" <| fun _ ->
      let outcome = decide "Hello.fs" RunTrigger.Keystroke (stateWith LiveTestingActivation.Inactive LivePause.Live everyTest)
      outcome.Verdict |> Expect.equal "off is its own verdict" AfterTypeCheckVerdict.Inactive
      outcome.Effects |> Expect.isEmpty "nothing is asked for"
      AfterTypeCheckVerdict.decision outcome.Verdict |> Expect.isNone "no decision to record"

    testCase "an edit that reaches tests and runs them is Selected, carrying the decision" <| fun _ ->
      let outcome = decide "Hello.fs" RunTrigger.Keystroke (stateWith LiveTestingActivation.Active LivePause.Live everyTest)
      match outcome.Verdict with
      | AfterTypeCheckVerdict.Selected decision ->
        decision.Explanation.SelectedTests |> Expect.hasLength "both tests are selected" 2
      | other -> failtestf "expected Selected, got %A" other
      outcome.Effects |> Expect.isNonEmpty "a run is asked for"

    testCase "a pause holds the affected tests back: HeldBack, not NothingAffected" <| fun _ ->
      let outcome = decide "Hello.fs" RunTrigger.Keystroke (stateWith LiveTestingActivation.Active LivePause.Paused everyTest)
      match outcome.Verdict with
      | AfterTypeCheckVerdict.HeldBack decision ->
        decision.Explanation.Precision |> Expect.equal "recorded as held back by policy" SelectionPrecision.SuppressedByPolicy
        decision.Explanation.DeferredTests |> Expect.hasLength "the two tests are named as held back" 2
      | other -> failtestf "expected HeldBack, got %A" other

    testCase "a script edit that reaches no test is NothingAffected, with the decision that says so" <| fun _ ->
      let outcome = decide "notes.fsx" RunTrigger.Keystroke (stateWith LiveTestingActivation.Active LivePause.Live [||])
      match outcome.Verdict with
      | AfterTypeCheckVerdict.NothingAffected decision ->
        decision.Explanation.Precision |> Expect.equal "no test reached" SelectionPrecision.NoImpactedTests
      | other -> failtestf "expected NothingAffected, got %A" other

    testCase "every verdict but Inactive carries a decision, and it is the one LastDecision records" <| fun _ ->
      let live = stateWith LiveTestingActivation.Active LivePause.Live everyTest
      let paused = stateWith LiveTestingActivation.Active LivePause.Paused everyTest
      let verdicts =
        [ decide "Hello.fs" RunTrigger.Keystroke live
          decide "Hello.fs" RunTrigger.Keystroke paused
          decide "notes.fsx" RunTrigger.Keystroke (stateWith LiveTestingActivation.Active LivePause.Live [||]) ]
      for outcome in verdicts do
        decisionOf outcome |> ignore
  ]
