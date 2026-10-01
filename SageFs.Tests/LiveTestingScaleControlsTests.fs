module SageFs.Tests.LiveTestingScaleControlsTests

open Expecto
open Expecto.Flip
open SageFs
open SageFs.Features.LiveTesting
open SageFs.Tests.LiveTestingTestHelpers

/// Scale controls for a big suite, against what Visual Studio's Live Unit Testing offers: pause,
/// and an include or exclude set (its playlist and its exclude list). Pause keeps the session's
/// evaluated code current and only holds the test runs back. The scope narrows what AUTOMATIC runs
/// touch; asking for a test by name always runs it.

let private mk name = mkTestCase name TestFramework.Expecto TestCategory.Unit

let private circle = mk "from C#/discriminated unions/circle area"
let private rectangle = mk "from C#/discriminated unions/rectangle area"
let private greet = mk "from C#/option/greet with Some"
let private everyTest = [| circle; rectangle; greet |]

let private graph =
  let all = everyTest |> Array.map (fun t -> t.Id)
  { TestDependencyGraph.empty with
      SymbolToTests = Map.ofList [ "Hello.area", all ]
      TransitiveCoverage = Map.ofList [ "Hello.area", all ] }

let private withState (pause: LivePause) (scope: TestScope) : LiveTestState =
  { LiveTestState.empty with
      Activation = LiveTestingActivation.Active
      Pause = pause
      Scope = scope
      DiscoveredTests = everyTest }

/// A body-only edit of the file the tests cover, the way the keystroke path sees it.
let private decide (trigger: RunTrigger) (state: LiveTestState) =
  TestCycleEffects.decideAfterTypeCheck
    { Changed = []; InFile = [ "Hello.area" ]; Lines = ChangedLines.NoBaseline }
    "Hello.fs"
    trigger
    graph
    state
    None
    Map.empty

let private selectedNames (outcome: AfterTypeCheckOutcome) =
  match outcome.Decision with
  | Some d -> d.Explanation.SelectedTests |> Array.sort
  | None -> failtest "expected a decision"

let private deferredNames (outcome: AfterTypeCheckOutcome) =
  match outcome.Decision with
  | Some d -> d.Explanation.DeferredTests |> Array.sort
  | None -> failtest "expected a decision"

[<Tests>]
let pauseTests =
  testList "scale controls: pause" [

    testCase "paused, an edit selects nothing and says it is paused, naming the tests it held back" <| fun _ ->
      let outcome = decide RunTrigger.Keystroke (withState LivePause.Paused TestScope.EveryTest)
      selectedNames outcome |> Expect.isEmpty "no test is selected"
      deferredNames outcome
      |> Expect.equal "every test the edit reaches is named as held back" (everyTest |> Array.map (fun t -> t.FullName) |> Array.sort)
      match outcome.Decision with
      | Some d ->
        d.Explanation.Precision |> Expect.equal "labeled as held back by policy" SelectionPrecision.SuppressedByPolicy
        d.Explanation.Reason.ToLowerInvariant() |> Expect.stringContains "the reason says why" "paused"
      | None -> failtest "expected a decision"

    testCase "paused, the edited buffer is still evaluated so the session stays current, and no test runs" <| fun _ ->
      let outcome = decide RunTrigger.Keystroke (withState LivePause.Paused TestScope.EveryTest)
      match TestCycleEffects.redirectToEvalBuffer (Some "let area = 1") "Hello.fs" outcome.Effects with
      | [ TestCycleEffect.EvalBufferThenRunAffected req ] ->
        req.Run.Tests |> Expect.isEmpty "the eval carries no tests to run"
      | other -> failtestf "expected the buffer evaluated with nothing to run, got %A" other

    testCase "live, the same edit selects every test it reaches" <| fun _ ->
      selectedNames (decide RunTrigger.Keystroke (withState LivePause.Live TestScope.EveryTest))
      |> Expect.equal "nothing held back" (everyTest |> Array.map (fun t -> t.FullName) |> Array.sort)

    testCase "an explicit run is not held back by a pause: asking for tests by name always runs them" <| fun _ ->
      selectedNames (decide RunTrigger.ExplicitRun (withState LivePause.Paused TestScope.EveryTest))
      |> Expect.isNonEmpty "an explicit run still selects"

    testCase "resuming runs exactly the tests that went stale while paused and the scope allows" <| fun _ ->
      let state =
        { withState LivePause.Live (TestScope.AllExcept [ "greet" ]) with
            AffectedTests = Set.ofList [ circle.Id; greet.Id ] }
      PolicyFilter.resumeSelection state
      |> Array.map (fun t -> t.Id)
      |> Expect.equal "circle went stale and is in scope; greet is out of scope; rectangle never went stale" [| circle.Id |]
  ]

[<Tests>]
let scopeTests =
  testList "scale controls: the include and exclude set" [

    testCase "an exclude set keeps the matching tests out of an automatic run and names them as deferred" <| fun _ ->
      let outcome = decide RunTrigger.Keystroke (withState LivePause.Live (TestScope.AllExcept [ "option" ]))
      selectedNames outcome
      |> Expect.equal "the option test is left out" ([| circle; rectangle |] |> Array.map (fun t -> t.FullName) |> Array.sort)
      deferredNames outcome |> Expect.equal "and named" [| greet.FullName |]

    testCase "an include set runs only the tests that match it" <| fun _ ->
      selectedNames (decide RunTrigger.Keystroke (withState LivePause.Live (TestScope.OnlyMatching [ "circle" ])))
      |> Expect.equal "only circle" [| circle.FullName |]

    testCase "an include set matches the display name too, and any one pattern is enough" <| fun _ ->
      selectedNames (decide RunTrigger.Keystroke (withState LivePause.Live (TestScope.OnlyMatching [ "circle"; "greet with Some" ])))
      |> Expect.equal "circle and greet" ([| circle; greet |] |> Array.map (fun t -> t.FullName) |> Array.sort)

    testCase "an explicit run ignores the scope" <| fun _ ->
      selectedNames (decide RunTrigger.ExplicitRun (withState LivePause.Live (TestScope.OnlyMatching [ "circle" ])))
      |> Expect.equal "every test, because the person asked" (everyTest |> Array.map (fun t -> t.FullName) |> Array.sort)

    testCase "an include set that matches nothing selects nothing and says so, never everything" <| fun _ ->
      let outcome = decide RunTrigger.Keystroke (withState LivePause.Live (TestScope.OnlyMatching [ "no such test" ]))
      selectedNames outcome |> Expect.isEmpty "nothing matches"
      match outcome.Decision with
      | Some d -> d.Explanation.Precision |> Expect.equal "held back by scope, said as policy" SelectionPrecision.SuppressedByPolicy
      | None -> failtest "expected a decision"

    testCase "TestScope.allows is the one rule: substring of the full name or the display name" <| fun _ ->
      TestScope.allows TestScope.EveryTest circle |> Expect.isTrue "every test"
      TestScope.allows (TestScope.OnlyMatching [ "rectangle" ]) rectangle |> Expect.isTrue "included"
      TestScope.allows (TestScope.OnlyMatching [ "rectangle" ]) circle |> Expect.isFalse "not included"
      TestScope.allows (TestScope.AllExcept [ "rectangle" ]) rectangle |> Expect.isFalse "excluded"
      TestScope.allows (TestScope.AllExcept [ "rectangle" ]) circle |> Expect.isTrue "not excluded"
      TestScope.allows (TestScope.AllExcept []) circle |> Expect.isTrue "an empty exclude set excludes nothing"
  ]

[<Tests>]
let reducerTests =
  let step event (model: SageFsModel, effects: SageFsEffect list) =
    let model', more = SageFsUpdate.update (SageFsMsg.Event event) model
    model', effects @ more

  let started () : SageFsModel * string =
    let model, sid = withKnownSession "/work/proj" (SageFsModel.initial ())
    let enabled, _ = (model, []) |> step TuiEvent.LiveTestingEnabled
    let discovered, _ = (enabled, []) |> step (TuiEvent.LiveDiscoveryMerged (sid, everyTest))
    discovered, sid

  testList "scale controls: what the model records" [

    testCase "pausing and resuming are recorded for the session" <| fun _ ->
      let model, sid = started ()
      let paused, _ = (model, []) |> step (TuiEvent.LivePauseChanged (Some sid, LivePause.Paused))
      (SageFsModel.cycleForSession sid paused).TestState.Pause |> Expect.equal "paused" LivePause.Paused
      let resumed, _ = (paused, []) |> step (TuiEvent.LivePauseChanged (Some sid, LivePause.Live))
      (SageFsModel.cycleForSession sid resumed).TestState.Pause |> Expect.equal "live again" LivePause.Live

    testCase "setting a scope is recorded for the session" <| fun _ ->
      let model, sid = started ()
      let scoped, _ = (model, []) |> step (TuiEvent.LiveScopeChanged (Some sid, TestScope.AllExcept [ "option" ]))
      (SageFsModel.cycleForSession sid scoped).TestState.Scope |> Expect.equal "the exclude set" (TestScope.AllExcept [ "option" ])

    testCase "resuming runs the tests that went stale while paused, and nothing when none did" <| fun _ ->
      let model, sid = started ()
      let paused, _ = (model, []) |> step (TuiEvent.LivePauseChanged (Some sid, LivePause.Paused))
      let none = (paused, []) |> step (TuiEvent.LivePauseChanged (Some sid, LivePause.Live))
      snd none |> Expect.isEmpty "nothing went stale, so nothing runs"
      let stale, _ = (paused, []) |> step (TuiEvent.AffectedTestsComputed ([| circle.Id |], [ "Hello.area" ]))
      let _, effects = (stale, []) |> step (TuiEvent.LivePauseChanged (Some sid, LivePause.Live))
      effects
      |> List.choose (function
        | SageFsEffect.TestCycle (TestCycleEffect.RunAffectedTests req)
        | SageFsEffect.TestCycle (TestCycleEffect.RunRequestedTests (req, _)) -> Some (req.Tests |> Array.map (fun t -> t.Id))
        | _ -> None)
      |> Expect.equal "the stale test runs" [ [| circle.Id |] ]
  ]
