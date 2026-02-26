module SageFs.Tests.SseDedupKeyTests

open System
open Expecto
open Expecto.Flip
open SageFs
open SageFs.Features.LiveTesting

/// Helper to create a TestStatusEntry
let makeEntry (i: int) (status: TestRunStatus) : TestStatusEntry =
  { TestId = TestId.TestId (sprintf "test-%d" i)
    DisplayName = sprintf "Test %d" i
    FullName = sprintf "Namespace.Test%d" i
    Origin = TestOrigin.ReflectionOnly
    Framework = "Expecto"
    Category = TestCategory.Unit
    CurrentPolicy = RunPolicy.OnEveryChange
    Status = status
    PreviousStatus = TestRunStatus.Queued }

let baseModel = SageFsModel.initial

let withTests (count: int) (model: SageFsModel) =
  let entries =
    [| for i in 1..count ->
        makeEntry i (TestRunStatus.Passed (TimeSpan.FromMilliseconds 10.0)) |]
  let ts = { model.LiveTesting.TestState with StatusEntries = entries }
  { model with LiveTesting = { model.LiveTesting with TestState = ts } }

let withActivation (activation: LiveTestingActivation) (model: SageFsModel) =
  let ts = { model.LiveTesting.TestState with Activation = activation }
  { model with LiveTesting = { model.LiveTesting with TestState = ts } }

let withRunPhase (phase: TestRunPhase) (model: SageFsModel) =
  let ts = { model.LiveTesting.TestState with RunPhase = phase }
  { model with LiveTesting = { model.LiveTesting with TestState = ts } }

let withGeneration (gen: int) (model: SageFsModel) =
  let ts = { model.LiveTesting.TestState with LastGeneration = RunGeneration gen }
  { model with LiveTesting = { model.LiveTesting with TestState = ts } }

[<Tests>]
let tests = testList "SseDedupKey" [
  testList "detects test state changes" [
    testCase "key changes when tests added" <| fun () ->
      let before = SseDedupKey.fromModel baseModel
      let after = baseModel |> withTests 5 |> SseDedupKey.fromModel
      (before <> after)
      |> Expect.isTrue "must detect test count change"

    testCase "key changes when activation toggled" <| fun () ->
      let before = SseDedupKey.fromModel baseModel
      let after =
        baseModel
        |> withActivation LiveTestingActivation.Inactive
        |> SseDedupKey.fromModel
      (before <> after)
      |> Expect.isTrue "must detect activation change"

    testCase "key changes when run phase changes" <| fun () ->
      let before = SseDedupKey.fromModel baseModel
      let after =
        baseModel
        |> withRunPhase (TestRunPhase.Running (RunGeneration 1))
        |> SseDedupKey.fromModel
      (before <> after)
      |> Expect.isTrue "must detect run phase change"

    testCase "key changes when generation advances" <| fun () ->
      let before = SseDedupKey.fromModel baseModel
      let after = baseModel |> withGeneration 42 |> SseDedupKey.fromModel
      (before <> after)
      |> Expect.isTrue "must detect generation change"

    testCase "key changes when test status changes from passed to failed" <| fun () ->
      let modelPassed = baseModel |> withTests 3
      let modelFailed =
        let entries =
          [| makeEntry 1 (TestRunStatus.Passed (TimeSpan.FromMilliseconds 10.0))
             makeEntry 2 (TestRunStatus.Failed (
              TestFailure.AssertionFailed "oops",
              TimeSpan.FromMilliseconds 20.0))
             makeEntry 3 (TestRunStatus.Passed (TimeSpan.FromMilliseconds 10.0)) |]
        let ts = { baseModel.LiveTesting.TestState with StatusEntries = entries }
        { baseModel with LiveTesting = { baseModel.LiveTesting with TestState = ts } }
      let before = SseDedupKey.fromModel modelPassed
      let after = SseDedupKey.fromModel modelFailed
      (before <> after)
      |> Expect.isTrue "must detect pass to fail transition"
  ]

  testList "preserves existing behavior" [
    testCase "key stable when nothing changes" <| fun () ->
      let a = SseDedupKey.fromModel baseModel
      let b = SseDedupKey.fromModel baseModel
      a |> Expect.equal "same model = same key" b

    testCase "key changes when output added" <| fun () ->
      let before = SseDedupKey.fromModel baseModel
      let withOutput =
        { baseModel with
            RecentOutput =
              [{ Kind = OutputKind.Info
                 Text = "hello"
                 Timestamp = DateTime.UtcNow
                 SessionId = "" }] }
      let after = SseDedupKey.fromModel withOutput
      (before <> after)
      |> Expect.isTrue "must still detect output changes"
  ]
]
