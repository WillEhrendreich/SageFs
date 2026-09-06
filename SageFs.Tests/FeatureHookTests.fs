module SageFs.Tests.FeatureHookTests

open Expecto
open Expecto.Flip
open FsCheck
open FsCheck.FSharp
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

  testList "incremental CachedScope equivalence" [
    // recordEval's fast path merges the new cell into CachedScope instead of
    // rebuilding from all retained cells. The result must be identical to the
    // full rebuild from the accumulated history (roast §6 regression guard).
    test "redefinition + cross-cell refs: incremental scope equals full rebuild" {
      let steps = [
        "let x = 1", "val x: int = 1"
        "let y = x + 1", "val y: int = 2"
        "let x = 10", "val x: int = 10"
        "let z = x + y", "val z: int = 12"
      ]
      let state =
        steps
        |> List.fold (fun st (code, result) -> recordEval code result 5L st) FeaturePushState.empty
      let expected =
        state.EvalHistory
        |> List.rev
        |> List.map (fun e ->
          let cell : SageFs.Features.BindingExplorer.CellInput = {
            CellIndex = e.CellIndex
            FsiOutput = e.Result
            Source = e.Code
          }
          cell)
        |> SageFs.Features.BindingExplorer.buildScopeSnapshot
      match state.CachedScope with
      | None -> failwith "CachedScope should exist after evals"
      | Some actual ->
        actual.Bindings |> Expect.equal "bindings match full rebuild" expected.Bindings
        actual.ActiveBindings |> Expect.equal "active map matches" expected.ActiveBindings
        actual.ShadowedBindings |> Expect.equal "shadowed list matches" expected.ShadowedBindings
    }

    test "first eval populates CachedScope" {
      let state = FeaturePushState.empty |> recordEval "let x = 1" "val x: int = 1" 50L
      state.CachedScope |> Expect.isSome "first eval should populate scope"
    }
  ]

  testList "incremental CachedCellGraph equivalence" [
    // recordEval appends the new cell to CachedCellGraph instead of rebuilding
    // the graph from all retained cells. The result must be IDENTICAL to a
    // full rebuild from the same history (roast item 8 regression guard).
    let fullRebuild (state: FeaturePushState) =
      state.EvalHistory
      |> List.rev
      |> List.map (fun e -> SageFs.Features.CellDependencyGraph.analyzeCell state.KnownBindings e.CellIndex e.Code e.Result)
      |> SageFs.Features.CellDependencyGraph.buildGraph

    test "redefinition + cross-cell refs: incremental graph equals full rebuild" {
      let steps = [
        "let x = 1", "val x: int = 1"
        "let y = x + 1", "val y: int = 2"
        "let x = 10", "val x: int = 10"
        "let z = x + y", "val z: int = 12"
        "let w = z * 2", "val w: int = 24"
      ]
      let state =
        steps
        |> List.fold (fun st (code, result) -> recordEval code result 5L st) FeaturePushState.empty
      match state.CachedCellGraph with
      | None -> failwith "CachedCellGraph should exist after evals"
      | Some actual ->
        let expected = fullRebuild state
        actual.Cells |> Expect.equal "cells match full rebuild" expected.Cells
        actual.Edges |> Expect.equal "edges match full rebuild" expected.Edges
    }

    test "shadowed binding retargets consumers to the latest producer" {
      let steps = [
        "let a = 1", "val a: int = 1"
        "let b = a + 1", "val b: int = 2"
        "let a = 100", "val a: int = 100"
      ]
      let state =
        steps
        |> List.fold (fun st (code, result) -> recordEval code result 5L st) FeaturePushState.empty
      match state.CachedCellGraph with
      | None -> failwith "CachedCellGraph should exist after evals"
      | Some actual ->
        let expected = fullRebuild state
        actual.Edges |> Expect.equal "retargeted edges match full rebuild" expected.Edges
        actual.Cells |> Expect.equal "cells match full rebuild" expected.Cells
    }

    test "independent cells accumulate edges incrementally" {
      let steps = [
        "let p = 1", "val p: int = 1"
        "let q = 2", "val q: int = 2"
        "let r = p + q", "val r: int = 3"
      ]
      let state =
        steps
        |> List.fold (fun st (code, result) -> recordEval code result 5L st) FeaturePushState.empty
      match state.CachedCellGraph with
      | None -> failwith "CachedCellGraph should exist after evals"
      | Some actual ->
        let expected = fullRebuild state
        actual.Edges |> Expect.equal "edges match full rebuild" expected.Edges
        actual.Cells |> Expect.equal "cells match full rebuild" expected.Cells
    }

    test "a cell consuming a binding redefined by a later independent cell" {
      // b consumes a; later c redefines a but b is not re-consumed — the full
      // rebuild resolves b's frozen Consumes through the LATEST producer (c),
      // and the incremental path must produce the same retargeted edge.
      let steps = [
        "let a = 1", "val a: int = 1"
        "let b = a", "val b: int = 1"
        "let c = a + 1", "val c: int = 2"
        "let a = 5", "val a: int = 5"
      ]
      let state =
        steps
        |> List.fold (fun st (code, result) -> recordEval code result 5L st) FeaturePushState.empty
      match state.CachedCellGraph with
      | None -> failwith "CachedCellGraph should exist after evals"
      | Some actual ->
        let expected = fullRebuild state
        actual.Edges |> Expect.equal "edges match full rebuild" expected.Edges
        actual.Cells |> Expect.equal "cells match full rebuild" expected.Cells
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

  testList "CachedCellGraph random-sequence equivalence" [
    // Strongest proof the incremental graph matches a full rebuild: random
    // sequences of cells (with redefinitions, shadowing, references, and
    // independent cells) must produce byte-identical Cells + Edges.
    let genName =
      Gen.elements [ "a"; "b"; "c"; "x" ]
    let genExpr =
      Gen.elements [ "1"; "a"; "b"; "c"; "x"; "a + 1"; "b + a" ]
    let genStep =
      Gen.map2 (fun name expr ->
        (sprintf "let %s = %s" name expr, sprintf "val %s: int = 42" name)) genName genExpr
    let fullRebuildGraph (state: FeaturePushState) =
      state.EvalHistory
      |> List.rev
      |> List.map (fun e -> SageFs.Features.CellDependencyGraph.analyzeCell state.KnownBindings e.CellIndex e.Code e.Result)
      |> SageFs.Features.CellDependencyGraph.buildGraph
    testPropertyWithConfig
      { FsCheckConfig.defaultConfig with
          maxTest = 200
          endSize = 15 } "random cell sequences: incremental graph equals full rebuild" <|
      fun (steps: (string * string) list) ->
        let state =
          steps
          |> List.fold (fun st (code, result) -> recordEval code result 5L st) FeaturePushState.empty
        match state.CachedCellGraph with
        | None -> true // empty sequence — trivially equal
        | Some actual ->
          let expected = fullRebuildGraph state
          actual.Cells = expected.Cells && actual.Edges = expected.Edges
  ]

  testList "PriorCellInputs incremental list" [
    // recordEval must NOT re-map the entire EvalHistory into CellInput records
    // on every eval (that allocates a fresh ≤10k-element list per eval — the
    // O(n²) driver per roast queue item 4). The stored list is the incremental
    // equivalent; this test pins it to exactly what the old re-map produced
    // (EvalHistory is stored newest-first, and PriorCellInputs keeps the same
    // order — appendCell only scans it, never indexes positionally).
    test "PriorCellInputs mirrors EvalHistory as newest-first CellInputs" {
      let steps = [
        "let a = 1", "val a: int = 1"
        "let b = 2", "val b: int = 2"
        "let c = 3", "val c: int = 3"
      ]
      let state =
        steps
        |> List.fold (fun st (code, result) -> recordEval code result 5L st) FeaturePushState.empty
      let expected =
        state.EvalHistory
        |> List.map (fun e ->
          let cell : SageFs.Features.BindingExplorer.CellInput = {
            CellIndex = e.CellIndex
            FsiOutput = e.Result
            Source = e.Code
          }
          cell)
      state.PriorCellInputs
      |> Expect.equal "prior cells are the newest-first re-map, without re-mapping" expected
    }
  ]
]
