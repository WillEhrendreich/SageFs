module SageFs.Tests.FeatureHookTests

open Expecto
open Expecto.Flip
open System.Text.Json
open SageFs.Features.FeatureHooks

let sseJsonOpts = JsonSerializerOptions(PropertyNamingPolicy = JsonNamingPolicy.CamelCase)

[<Tests>]
let featureHookTests = testList "Feature Hook Computation" [

  testList "computeEvalDiffPush" [
    test "first eval pushes diff with Added lines" {
      let state = FeaturePushState.empty |> recordEval "let x = 1" "val x: int = 1" 50L
      let _, sse = computeEvalDiffPush sseJsonOpts (Some "s1") "val x: int = 1" state
      sse |> Expect.isSome "should push SSE"
      let s = sse.Value
      s |> Expect.stringContains "should contain eval_diff" "eval_diff"
      s |> Expect.stringContains "should contain added" "added"
    }

    test "unchanged output pushes Unchanged lines" {
      let state =
        { FeaturePushState.empty with LastOutputText = "val x: int = 1" }
        |> recordEval "let x = 1" "val x: int = 1" 50L
      let _, sse = computeEvalDiffPush sseJsonOpts (Some "s1") "val x: int = 1" state
      sse |> Expect.isSome "should push (first time)"
      let s = sse.Value
      s |> Expect.stringContains "should contain unchanged" "unchanged"
    }

    test "modified output pushes Modified lines" {
      let state =
        { FeaturePushState.empty with LastOutputText = "val x: int = 1" }
        |> recordEval "let x = 2" "val x: int = 2" 50L
      let _, sse = computeEvalDiffPush sseJsonOpts (Some "s1") "val x: int = 2" state
      sse |> Expect.isSome "should push"
      let s = sse.Value
      s |> Expect.stringContains "should contain modified" "modified"
    }
  ]

  testList "computeCellDepsPush" [
    test "pushes graph after eval" {
      let state =
        FeaturePushState.empty
        |> recordEval "let x = 1" "val x: int = 1" 50L
      let _, sse = computeCellDepsPush sseJsonOpts (Some "s1") state
      sse |> Expect.isSome "should push"
      let s = sse.Value
      s |> Expect.stringContains "should contain cell_dependencies" "cell_dependencies"
      s |> Expect.stringContains "should contain nodes" "nodes"
    }
  ]

  testList "computeBindingScopePush" [
    test "pushes scope snapshot after eval" {
      let state =
        FeaturePushState.empty
        |> recordEval "let x = 1" "val x: int = 1" 50L
      let _, sse = computeBindingScopePush sseJsonOpts (Some "s1") state
      sse |> Expect.isSome "should push"
      let s = sse.Value
      s |> Expect.stringContains "should contain binding_scope_map" "binding_scope_map"
      s |> Expect.stringContains "should contain bindings" "bindings"
    }
  ]

  testList "computeEvalTimelinePush" [
    test "pushes timeline after eval" {
      let state =
        FeaturePushState.empty
        |> recordEval "let x = 1" "val x: int = 1" 50L
      let _, sse = computeEvalTimelinePush sseJsonOpts (Some "s1") state
      sse |> Expect.isSome "should push"
      let s = sse.Value
      s |> Expect.stringContains "should contain eval_timeline" "eval_timeline"
      s |> Expect.stringContains "should contain sparkline" "sparkline"
    }
  ]

  testList "Dedup" [
    test "third identical EvalDiff call is deduped" {
      let state =
        FeaturePushState.empty
        |> recordEval "let x = 1" "val x: int = 1" 50L
      let s1, d1 = computeEvalDiffPush sseJsonOpts (Some "s1") "val x: int = 1" state
      d1 |> Expect.isSome "first should fire (Added)"
      let s2, d2 = computeEvalDiffPush sseJsonOpts (Some "s1") "val x: int = 1" s1
      d2 |> Expect.isSome "second should fire (Unchanged vs Added)"
      let _, d3 = computeEvalDiffPush sseJsonOpts (Some "s1") "val x: int = 1" s2
      d3 |> Expect.isNone "third should be deduped"
    }

    test "second identical call is deduped for deps/scope/timeline" {
      let state =
        FeaturePushState.empty
        |> recordEval "let x = 1" "val x: int = 1" 50L
      let s1, _ = computeCellDepsPush sseJsonOpts (Some "s1") state
      let s2, _ = computeBindingScopePush sseJsonOpts (Some "s1") s1
      let s3, _ = computeEvalTimelinePush sseJsonOpts (Some "s1") s2
      let _, d1 = computeCellDepsPush sseJsonOpts (Some "s1") s3
      let _, d2 = computeBindingScopePush sseJsonOpts (Some "s1") s3
      let _, d3 = computeEvalTimelinePush sseJsonOpts (Some "s1") s3
      d1 |> Expect.isNone "cell deps should be deduped"
      d2 |> Expect.isNone "binding scope should be deduped"
      d3 |> Expect.isNone "eval timeline should be deduped"
    }
  ]
]
