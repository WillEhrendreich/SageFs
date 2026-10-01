module SageFs.Tests.LiveTestingProvenanceTests

open System
open Expecto
open Expecto.Flip
open SageFs
open SageFs.Features.LiveTesting
open SageFs.Tests.LiveTestingTestHelpers

/// What produced each row's verdict. A keystroke's tests run against code the live session
/// EVALUATED; a real build can disagree. These drive the Elm reducer through a whole confirmation:
/// the evaluated run, the quiet window, the build, the run against the build, and what the rows say
/// at each step.

let private testA = mkTestCase "Sample.Tests.A" TestFramework.Expecto TestCategory.Unit
let private testB = mkTestCase "Sample.Tests.B" TestFramework.Expecto TestCategory.Unit
let private testC = mkTestCase "Sample.Tests.C" TestFramework.Expecto TestCategory.Unit
let private everyTest = [| testA; testB; testC |]
let private ids = everyTest |> Array.map (fun t -> t.Id)

let private edited = AnalysisIdentity.ofContent "let add a b = a + b + 1"

let private passed (tc: TestCase) = mkResult tc.Id (TestResult.Passed (ts 3.0))
let private failed (tc: TestCase) = mkResult tc.Id (TestResult.Failed (TestFailure.AssertionFailed "expected 7, got 8", ts 3.0))

/// A model with a known session, live testing on, and the three tests discovered.
let private started () : SageFsModel * string =
  let model, sid = withKnownSession "/work/proj" (SageFsModel.initial ())
  let step event m = fst (SageFsUpdate.update (SageFsMsg.Event event) m)
  model
  |> step TuiEvent.LiveTestingEnabled
  |> step (TuiEvent.LiveDiscoveryMerged (sid, everyTest)),
  sid

let private send (event: TuiEvent) (model: SageFsModel, effects: SageFsEffect list) : SageFsModel * SageFsEffect list =
  let model', more = SageFsUpdate.update (SageFsMsg.Event event) model
  model', effects @ more

let private rowsOf (sid: string) (model: SageFsModel) : TestStatusEntry array =
  (SageFsModel.cycleForSession sid model).TestState |> LiveTestState.orderedStatusEntries

let private provenanceOf (sid: string) (tc: TestCase) (model: SageFsModel) : ResultProvenance =
  rowsOf sid model |> Array.find (fun e -> e.TestId = tc.Id) |> fun e -> e.Provenance

/// The run of `ran` tests, reported as `results`, ending.
let private runs (sid: string) (results: TestRunResult array) (state: SageFsModel * SageFsEffect list) =
  state
  |> send (TuiEvent.TestRunStarted (results |> Array.map (fun r -> r.TestId), Some sid))
  |> send (TuiEvent.TestResultsBatch (Some sid, results))
  |> send (TuiEvent.TestRunCompleted (Some sid))

let private confirmEffects (effects: SageFsEffect list) : ConfirmationEffect list =
  effects |> List.choose (function SageFsEffect.Confirm (_, e) -> Some e | _ -> None)

/// An evaluated run of every test, finished, with `results`.
let private evaluatedRun (sid: string) (results: TestRunResult array) (model: SageFsModel) =
  (model, [])
  |> send (TuiEvent.EvaluatedRunBegan (sid, edited, ids))
  |> runs sid results

[<Tests>]
let provenanceTests =
  testList "result provenance: what code produced a row's verdict" [

    testCase "rows that ran after an eval say Evaluated" <| fun _ ->
      let model, sid = started ()
      let after, _ = evaluatedRun sid (everyTest |> Array.map passed) model
      for tc in everyTest do
        provenanceOf sid tc after |> Expect.equal (sprintf "%s ran against evaluated code" tc.DisplayName) ResultProvenance.Evaluated

    testCase "rows from a run no eval preceded say Compiled" <| fun _ ->
      let model, sid = started ()
      let after, _ = runs sid (everyTest |> Array.map passed) (model, [])
      for tc in everyTest do
        provenanceOf sid tc after |> Expect.equal (sprintf "%s ran against compiled binaries" tc.DisplayName) ResultProvenance.Compiled

    testCase "the basis of one run does not leak into the next" <| fun _ ->
      let model, sid = started ()
      let afterEval, _ = evaluatedRun sid (everyTest |> Array.map passed) model
      let afterExplicit, _ = runs sid [| passed testA |] (afterEval, [])
      provenanceOf sid testA afterExplicit |> Expect.equal "A was re-run with no eval before it" ResultProvenance.Compiled
      provenanceOf sid testB afterExplicit |> Expect.equal "B was not re-run and keeps what it had" ResultProvenance.Evaluated
  ]

[<Tests>]
let confirmationFlowTests =
  testList "result provenance: confirming an evaluated run against a real build" [

    testCase "an evaluated run that finishes opens the quiet window and nothing else: the verdict is not held back" <| fun _ ->
      let model, sid = started ()
      let _, effects = evaluatedRun sid (everyTest |> Array.map passed) model
      confirmEffects effects
      |> List.filter (function ConfirmationEffect.StartQuietWindow -> true | _ -> false)
      |> List.length
      |> Expect.equal "one quiet window" 1
      confirmEffects effects
      |> List.exists (function ConfirmationEffect.StartBuild _ -> true | _ -> false)
      |> Expect.isFalse "no build yet: the editing may not be done"

    testCase "the quiet window ending starts one build of the evaluated content" <| fun _ ->
      let model, sid = started ()
      let state = evaluatedRun sid (everyTest |> Array.map passed) model
      let _, effects = state |> send (TuiEvent.BuildConfirmation (sid, ConfirmationEvent.QuietElapsed))
      confirmEffects effects
      |> List.choose (function ConfirmationEffect.StartBuild (_, c) -> Some c.Content | _ -> None)
      |> Expect.equal "a build of the content that was evaluated" [ edited ]

    testCase "a build that works and agrees ends with every row VerifiedByBuild" <| fun _ ->
      let model, sid = started ()
      let results = everyTest |> Array.map passed
      let state = evaluatedRun sid results model |> send (TuiEvent.BuildConfirmation (sid, ConfirmationEvent.QuietElapsed))
      let generation =
        confirmEffects (snd state)
        |> List.pick (function ConfirmationEffect.StartBuild (g, _) -> Some g | _ -> None)
      let built = state |> send (TuiEvent.BuildConfirmation (sid, ConfirmationEvent.BuildFinished (generation, BuildAnswer.Built)))
      confirmEffects (snd built)
      |> List.exists (function ConfirmationEffect.RunAgainstBuild (g, tests) -> g = generation && List.sort tests = List.sort (List.ofArray ids) | _ -> false)
      |> Expect.isTrue "every evaluated test is run against the build"
      let after, _ = built |> runs sid results
      for tc in everyTest do
        provenanceOf sid tc after |> Expect.equal (sprintf "%s agreed with the build" tc.DisplayName) ResultProvenance.VerifiedByBuild

    testCase "a build that says red where the eval said green marks that row BuildDisagrees, loudly, with both verdicts" <| fun _ ->
      let model, sid = started ()
      let state = evaluatedRun sid (everyTest |> Array.map passed) model |> send (TuiEvent.BuildConfirmation (sid, ConfirmationEvent.QuietElapsed))
      let generation =
        confirmEffects (snd state)
        |> List.pick (function ConfirmationEffect.StartBuild (g, _) -> Some g | _ -> None)
      let builtResults = [| passed testA; failed testB; passed testC |]
      let after, _ =
        state
        |> send (TuiEvent.BuildConfirmation (sid, ConfirmationEvent.BuildFinished (generation, BuildAnswer.Built)))
        |> runs sid builtResults
      match provenanceOf sid testB after with
      | ResultProvenance.BuildDisagrees (BuildDisagreement.ResultDiffers (evaluated, built)) ->
        evaluated |> Expect.notEqual "the eval and the build said different things" built
      | other -> failtestf "expected B to disagree, got %A" other
      provenanceOf sid testA after |> Expect.equal "A still agrees" ResultProvenance.VerifiedByBuild

    testCase "a build that fails marks every row BuildDisagrees with the compiler's message, and runs nothing" <| fun _ ->
      let model, sid = started ()
      let state = evaluatedRun sid (everyTest |> Array.map passed) model |> send (TuiEvent.BuildConfirmation (sid, ConfirmationEvent.QuietElapsed))
      let generation =
        confirmEffects (snd state)
        |> List.pick (function ConfirmationEffect.StartBuild (g, _) -> Some g | _ -> None)
      let after, effects = state |> send (TuiEvent.BuildConfirmation (sid, ConfirmationEvent.BuildFinished (generation, BuildAnswer.DidNotBuild "FS0039: The value 'main' is not defined")))
      for tc in everyTest do
        match provenanceOf sid tc after with
        | ResultProvenance.BuildDisagrees (BuildDisagreement.BuildFailed message) ->
          message |> Expect.stringContains "the compiler's message is on the row" "FS0039"
        | other -> failtestf "expected %s to say the build failed, got %A" tc.DisplayName other
      confirmEffects effects
      |> List.exists (function ConfirmationEffect.RunAgainstBuild _ -> true | _ -> false)
      |> Expect.isFalse "no run against a build that does not exist"

    testCase "a newer buffer while the build runs abandons the build, and its answer lands on no row" <| fun _ ->
      let model, sid = started ()
      let state = evaluatedRun sid (everyTest |> Array.map passed) model |> send (TuiEvent.BuildConfirmation (sid, ConfirmationEvent.QuietElapsed))
      let generation =
        confirmEffects (snd state)
        |> List.pick (function ConfirmationEffect.StartBuild (g, _) -> Some g | _ -> None)
      let newer = AnalysisIdentity.ofContent "let add a b = a + b + 2"
      let abandoned = state |> send (TuiEvent.BuildConfirmation (sid, ConfirmationEvent.ContentEdited newer))
      confirmEffects (snd abandoned)
      |> List.exists (function ConfirmationEffect.AbandonBuild g -> g = generation | _ -> false)
      |> Expect.isTrue "the build of older text is abandoned"
      let late, _ = abandoned |> send (TuiEvent.BuildConfirmation (sid, ConfirmationEvent.BuildFinished (generation, BuildAnswer.DidNotBuild "too late to matter")))
      for tc in everyTest do
        provenanceOf sid tc late |> Expect.equal (sprintf "%s is untouched by an answer about older text" tc.DisplayName) ResultProvenance.Evaluated
  ]
