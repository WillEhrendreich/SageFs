module SageFs.Tests.ToolAnswerTests

// WHY — an analysis tool that answers "[]" or "No issues detected" when it never saw the data
// is saying zero about something nobody counted. These tests pin the closed answer that replaces
// it: a measurement, or a reason with a sentence and an action, and the decisions that pick
// between them.
open Expecto
open Expecto.Flip
open FsCheck
open FsCheck.FSharp
open Microsoft.FSharp.Reflection
open SageFs.Features.LiveTesting
open SageFs.Features.ToolAnswers

/// One value of every case of the reason, so each is checked. The count check below fails when a
/// case is added and not listed here.
let private everyReason : NotAvailableReason list =
  [ NotAvailableReason.NoSessionResolved "Two sessions share that directory."
    NotAvailableReason.SessionNotReady SessionReadiness.WarmingUp
    NotAvailableReason.SessionNotReady SessionReadiness.Faulted
    NotAvailableReason.SessionNotReady SessionReadiness.NotRoutable
    NotAvailableReason.NoEvalsYet
    NotAvailableReason.NoTestRunYet
    NotAvailableReason.NothingObservedYet
    NotAvailableReason.CellsNotInHistory [ 3; 7 ]
    NotAvailableReason.NoUsableCellIds
    NotAvailableReason.BindingNotInScope "threshold"
    NotAvailableReason.NeedsAWorkflow WorkflowKind.LiveTesting
    NotAvailableReason.NeedsLiveTesting
    NotAvailableReason.NoCoverageRecorded ]

let private genReason = Gen.elements everyReason

[<Tests>]
let reasonTests =
  testList "NotAvailableReason" [

    testCase "WHY — the list above covers every case, so a new case cannot skip these checks" <| fun _ ->
      let listed = everyReason |> List.map NotAvailableReason.token |> Set.ofList
      listed.Count
      |> Expect.equal "one token per union case" (FSharpType.GetUnionCases(typeof<NotAvailableReason>).Length)

    testCase "WHY — every case has its own token, because an agent branches on the token" <| fun _ ->
      let tokens = everyReason |> List.map NotAvailableReason.token |> List.distinct
      tokens
      |> Expect.hasLength "tokens do not collide" (FSharpType.GetUnionCases(typeof<NotAvailableReason>).Length)

    testPropertyWithConfig
      { FsCheckConfig.defaultConfig with maxTest = 100 }
      "WHY — every reason says what is missing and what to do, in plain sentences" <|
      Prop.forAll (Arb.fromGen genReason) (fun reason ->
        let describe = NotAvailableReason.describe reason
        let whatToDo = NotAvailableReason.whatToDo reason
        not (System.String.IsNullOrWhiteSpace describe)
        && not (System.String.IsNullOrWhiteSpace whatToDo)
        && describe.EndsWith "."
        && whatToDo.EndsWith ".")

    testPropertyWithConfig
      { FsCheckConfig.defaultConfig with maxTest = 100 }
      "WHY — no sentence uses an em dash, which the public voice does not" <|
      Prop.forAll (Arb.fromGen genReason) (fun reason ->
        let text = notAvailableText reason
        not (text.Contains "—"))

    testCase "WHY — the plain text names the token first, so a log line is greppable" <| fun _ ->
      notAvailableText NotAvailableReason.NoEvalsYet
      |> Expect.stringStarts "token leads" "Not available (NoEvalsYet)."

    testCase "WHY — what to do for a missing workflow names the exact switch_workflow target, because a guessed one is refused" <| fun _ ->
      NotAvailableReason.whatToDo (NotAvailableReason.NeedsAWorkflow WorkflowKind.LiveTesting)
      |> Expect.stringContains "target is the word switch_workflow takes" "target='livetesting'"

    testCase "WHY — what to do for an unmeasured test side names run_tests, the one way to measure it" <| fun _ ->
      NotAvailableReason.whatToDo NotAvailableReason.NoTestRunYet
      |> Expect.stringContains "run_tests" "run_tests"
  ]

[<Tests>]
let answerTests =
  testList "ToolAnswer" [

    testCase "WHY — a measurement carries its body and names nothing unmeasured by default" <| fun _ ->
      match ToolAnswer.measured (Rendered.Json "[]") with
      | ToolAnswer.Measured m ->
        m.Unmeasured |> Expect.isEmpty "nothing left out"
        m.Body |> Expect.equal "the body is kept" (Rendered.Json "[]")
      | other -> failtestf "expected Measured, got %A" other

    testCase "WHY — the wire token of each answer kind is fixed" <| fun _ ->
      ToolAnswer.token (ToolAnswer.measured (Rendered.Prose "x")) |> Expect.equal "measured" "Measured"
      ToolAnswer.token (ToolAnswer<Measurement>.NotAvailable NotAvailableReason.NoEvalsYet) |> Expect.equal "not available" "NotAvailable"
  ]

[<Tests>]
let readinessTests =
  testList "Readiness" [

    testCase "WHY — no recorded eval is NoEvalsYet, not an empty answer" <| fun _ ->
      Readiness.evalsRecorded 0 |> Expect.equal "refused" (Error NotAvailableReason.NoEvalsYet)

    testCase "WHY — one recorded eval is enough to read the cell history" <| fun _ ->
      Readiness.evalsRecorded 1 |> Expect.equal "allowed" (Ok ())

    testCase "WHY — no recorded test result is NoTestRunYet" <| fun _ ->
      Readiness.testsRecorded 0 |> Expect.equal "refused" (Error NotAvailableReason.NoTestRunYet)

    testCase "WHY — diagnose with neither evals nor test results has nothing to say" <| fun _ ->
      Readiness.diagnosable 0 0 |> Expect.equal "refused" (Error NotAvailableReason.NothingObservedYet)

    testCase "WHY — diagnose with only evals names the tests as unmeasured, so it cannot claim they are fine" <| fun _ ->
      Readiness.diagnosable 4 0 |> Expect.equal "tests unmeasured" (Ok [ UnmeasuredScope.Tests ])

    testCase "WHY — diagnose with only test results names the cells as unmeasured" <| fun _ ->
      Readiness.diagnosable 0 2 |> Expect.equal "cells unmeasured" (Ok [ UnmeasuredScope.Cells ])

    testCase "WHY — diagnose with both measures everything" <| fun _ ->
      Readiness.diagnosable 4 2 |> Expect.equal "nothing unmeasured" (Ok [])

    testCase "WHY — recorded coverage is enough whatever the switches say" <| fun _ ->
      Readiness.coverageRecorded LiveTestingActivation.Inactive WorkflowKind.Interactive CoverageEvidence.Recorded
      |> Expect.equal "allowed" (Ok ())

    testCase "WHY — no coverage in a session outside the live-testing workflow needs that workflow" <| fun _ ->
      Readiness.coverageRecorded LiveTestingActivation.Inactive WorkflowKind.Interactive CoverageEvidence.NotRecorded
      |> Expect.equal "needs the workflow" (Error (NotAvailableReason.NeedsAWorkflow WorkflowKind.LiveTesting))

    testCase "WHY — no coverage in a hot-reload session also needs the live-testing workflow" <| fun _ ->
      Readiness.coverageRecorded LiveTestingActivation.Inactive WorkflowKind.HotReload CoverageEvidence.NotRecorded
      |> Expect.equal "needs the workflow" (Error (NotAvailableReason.NeedsAWorkflow WorkflowKind.LiveTesting))

    testCase "WHY — no coverage in the live-testing workflow with the feature off says live testing is off" <| fun _ ->
      Readiness.coverageRecorded LiveTestingActivation.Inactive WorkflowKind.LiveTesting CoverageEvidence.NotRecorded
      |> Expect.equal "needs live testing" (Error NotAvailableReason.NeedsLiveTesting)

    testCase "WHY — no coverage with live testing on says no instrumented run has recorded any yet" <| fun _ ->
      Readiness.coverageRecorded LiveTestingActivation.Active WorkflowKind.LiveTesting CoverageEvidence.NotRecorded
      |> Expect.equal "no coverage yet" (Error NotAvailableReason.NoCoverageRecorded)
  ]
