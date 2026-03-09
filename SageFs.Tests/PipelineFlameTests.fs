module SageFs.Tests.PipelineFlameTests

open Expecto
open Expecto.Flip
open SageFs
open SageFs.EvalPipeline
open SageFs.Measures

[<Tests>]
let flameRenderTests =
  testList "PipelineFlame render" [

    testCase "Minimal density returns empty string" <| fun _ ->
      let trace = {
        Result = Ok "hello"
        Stages = [
          { Name = "Parse"; ElapsedMs = 1.0<ms>; Outcome = StageOutcome.Succeeded }
          { Name = "TypeCheck"; ElapsedMs = 3.0<ms>; Outcome = StageOutcome.Succeeded }
        ]
      }
      PipelineFlame.render UiDensity.Minimal trace
      |> Expect.equal "minimal should be empty" ""

    testCase "Normal density returns railway string" <| fun _ ->
      let trace = {
        Result = Ok 42
        Stages = [
          { Name = "Parse"; ElapsedMs = 1.2<ms>; Outcome = StageOutcome.Succeeded }
          { Name = "Eval"; ElapsedMs = 2.5<ms>; Outcome = StageOutcome.Succeeded }
        ]
      }
      let result = PipelineFlame.render UiDensity.Normal trace
      result |> Expect.stringContains "should contain Parse" "Parse"
      result |> Expect.stringContains "should contain checkmark" "✓"

    testCase "Full density returns flame bars" <| fun _ ->
      let trace = {
        Result = Ok ()
        Stages = [
          { Name = "Parse"; ElapsedMs = 2.0<ms>; Outcome = StageOutcome.Succeeded }
          { Name = "TypeCheck"; ElapsedMs = 8.0<ms>; Outcome = StageOutcome.Succeeded }
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
          { Name = "Parse"; ElapsedMs = 1.0<ms>; Outcome = StageOutcome.Succeeded }
          { Name = "Eval"; ElapsedMs = 0.5<ms>; Outcome = StageOutcome.Failed (SageFsError.EvalFailed "boom") }
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
        { Name = "Fast"; ElapsedMs = 1.0<ms>; Outcome = StageOutcome.Succeeded }
        { Name = "Slow"; ElapsedMs = 9.0<ms>; Outcome = StageOutcome.Succeeded }
      ]
      let bars = PipelineFlame.buildBars 20 stages
      let fastBar = bars |> List.find (fun b -> b.Name = "Fast")
      let slowBar = bars |> List.find (fun b -> b.Name = "Slow")
      (slowBar.Width, fastBar.Width) |> Expect.isGreaterThan "slow should be wider"

    testCase "single stage gets full width" <| fun _ ->
      let stages = [
        { Name = "Only"; ElapsedMs = 5.0<ms>; Outcome = StageOutcome.Succeeded }
      ]
      let bars = PipelineFlame.buildBars 20 stages
      bars.[0].Width |> Expect.equal "should be full width" 20

    testCase "bars have at least width 1" <| fun _ ->
      let stages = [
        { Name = "Tiny"; ElapsedMs = 0.001<ms>; Outcome = StageOutcome.Succeeded }
        { Name = "Big"; ElapsedMs = 100.0<ms>; Outcome = StageOutcome.Succeeded }
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
          { Name = "A"; ElapsedMs = 1.5<ms>; Outcome = StageOutcome.Succeeded }
          { Name = "B"; ElapsedMs = 2.5<ms>; Outcome = StageOutcome.Succeeded }
        ]
      }
      PipelineFlame.summary trace
      |> Expect.stringContains "should include total" "4.0ms"

    testCase "summary shows pass for successful trace" <| fun _ ->
      let trace = {
        Result = Ok "ok"
        Stages = [{ Name = "Run"; ElapsedMs = 1.0<ms>; Outcome = StageOutcome.Succeeded }]
      }
      PipelineFlame.summary trace
      |> Expect.stringContains "should say passed" "✓"

    testCase "summary shows fail for error trace" <| fun _ ->
      let trace = {
        Result = Error (SageFsError.EvalFailed "x")
        Stages = [{ Name = "Run"; ElapsedMs = 1.0<ms>; Outcome = StageOutcome.Failed (SageFsError.EvalFailed "x") }]
      }
      PipelineFlame.summary trace
      |> Expect.stringContains "should say failed" "✗"
  ]
