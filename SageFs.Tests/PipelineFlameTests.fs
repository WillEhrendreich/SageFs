module SageFs.Tests.PipelineFlameTests

open Expecto
open Expecto.Flip
open SageFs
open SageFs.EvalPipeline
open SageFs.Measures

/// A completed pipeline stage that took `elapsedMs` milliseconds. Every case states its own times,
/// because the cases are about how the stage times compare: bar widths, the total, which is slowest.
let private stage (name: string) (elapsedMs: float) (outcome: StageOutcome) : CompletedStage =
  { Name = name; ElapsedMs = elapsedMs * 1.0<ms>; Outcome = outcome }

[<Tests>]
let flameRenderTests =
  testList "PipelineFlame render" [

    testCase "Minimal density returns empty string" <| fun _ ->
      let trace = {
        Result = Ok "hello"
        Stages = [
          stage "Parse" 1.0 StageOutcome.Succeeded
          stage "TypeCheck" 3.0 StageOutcome.Succeeded
        ]
      }
      PipelineFlame.render UiDensity.Minimal trace
      |> Expect.equal "minimal should be empty" ""

    testCase "Normal density returns railway string" <| fun _ ->
      let trace = {
        Result = Ok 42
        Stages = [
          stage "Parse" 1.2 StageOutcome.Succeeded
          stage "Eval" 2.5 StageOutcome.Succeeded
        ]
      }
      let result = PipelineFlame.render UiDensity.Normal trace
      result |> Expect.stringContains "should contain Parse" "Parse"
      result |> Expect.stringContains "should contain checkmark" "✓"

    testCase "Full density returns flame bars" <| fun _ ->
      let trace = {
        Result = Ok ()
        Stages = [
          stage "Parse" 2.0 StageOutcome.Succeeded
          stage "TypeCheck" 8.0 StageOutcome.Succeeded
        ]
      }
      let result = PipelineFlame.render UiDensity.Full trace
      result |> Expect.stringContains "should contain bar char" "█"
      result |> Expect.stringContains "should contain Parse" "Parse"
      result |> Expect.stringContains "should contain TypeCheck" "TypeCheck"

    testCase "Full density failed stage uses different bar" <| fun _ ->
      let trace = {
        Result = Error (SageFsError.EvalFailed "boom")
        Stages = [
          stage "Parse" 1.0 StageOutcome.Succeeded
          stage "Eval" 0.5 (StageOutcome.Failed (SageFsError.EvalFailed "boom"))
        ]
      }
      let result = PipelineFlame.render UiDensity.Full trace
      result |> Expect.stringContains "should contain fail marker" "✗"

    testCase "empty trace renders gracefully" <| fun _ ->
      let trace = { Result = Ok (); Stages = [] }
      PipelineFlame.render UiDensity.Full trace
      |> Expect.stringContains "empty trace message" "empty"

    testCase "Normal density empty trace" <| fun _ ->
      let trace = { Result = Ok (); Stages = [] }
      PipelineFlame.render UiDensity.Normal trace
      |> Expect.stringContains "should say empty" "empty"
  ]

[<Tests>]
let flameBarTests =
  testList "PipelineFlame bars" [

    testCase "bar width proportional to stage time" <| fun _ ->
      let stages = [
        stage "Fast" 1.0 StageOutcome.Succeeded
        stage "Slow" 9.0 StageOutcome.Succeeded
      ]
      let bars = PipelineFlame.buildBars 20 stages
      let fastBar = bars |> List.find (fun b -> b.Name = "Fast")
      let slowBar = bars |> List.find (fun b -> b.Name = "Slow")
      (slowBar.Width, fastBar.Width) |> Expect.isGreaterThan "slow should be wider"

    testCase "single stage gets full width" <| fun _ ->
      let stages = [
        stage "Only" 5.0 StageOutcome.Succeeded
      ]
      let bars = PipelineFlame.buildBars 20 stages
      bars.[0].Width |> Expect.equal "should be full width" 20

    testCase "bars have at least width 1" <| fun _ ->
      let stages = [
        stage "Tiny" 0.001 StageOutcome.Succeeded
        stage "Big" 100.0 StageOutcome.Succeeded
      ]
      let bars = PipelineFlame.buildBars 20 stages
      bars |> List.iter (fun b ->
        (b.Width, 1) |> Expect.isGreaterThanOrEqual "bar width must be >= 1")

    testCase "no stages produces no bars" <| fun _ ->
      PipelineFlame.buildBars 20 []
      |> Expect.equal "no bars for empty stages" []
  ]

[<Tests>]
let flameSummaryTests =
  testList "PipelineFlame summary" [

    testCase "summary includes total time" <| fun _ ->
      let trace = {
        Result = Ok 42
        Stages = [
          stage "A" 1.5 StageOutcome.Succeeded
          stage "B" 2.5 StageOutcome.Succeeded
        ]
      }
      PipelineFlame.summary trace
      |> Expect.stringContains "should include total" "4.0ms"

    testCase "summary shows pass for successful trace" <| fun _ ->
      let trace = {
        Result = Ok "ok"
        Stages = [stage "Run" 1.0 StageOutcome.Succeeded]
      }
      PipelineFlame.summary trace
      |> Expect.stringContains "should say passed" "✓"

    testCase "summary shows fail for error trace" <| fun _ ->
      let trace = {
        Result = Error (SageFsError.EvalFailed "x")
        Stages = [stage "Run" 1.0 (StageOutcome.Failed (SageFsError.EvalFailed "x"))]
      }
      PipelineFlame.summary trace
      |> Expect.stringContains "should say failed" "✗"
  ]
