module SageFs.Tests.FeatureHookTests

open Expecto
open Expecto.Flip
open FsCheck
open FsCheck.FSharp
open System.Text.Json
open SageFs.Features
open SageFs.Features.FeatureHooks

let sseJsonOpts = JsonSerializerOptions(PropertyNamingPolicy = JsonNamingPolicy.CamelCase)

/// From-scratch binding scope of the retained history — the oracle the
/// indexed store must reproduce exactly.
let private fullScope (state: FeaturePushState) =
  state.EvalHistory
  |> List.rev
  |> List.map (fun e ->
    let cell : BindingExplorer.CellInput =
      { CellIndex = e.CellIndex; FsiOutput = e.Result; Source = e.Code }
    cell)
  |> BindingExplorer.buildScopeSnapshot

/// From-scratch dependency graph of the retained history (every retained
/// cell re-analyzed against the latest KnownBindings) — the other oracle.
let private fullGraph (state: FeaturePushState) =
  state.EvalHistory
  |> List.rev
  |> List.map (fun e -> CellDependencyGraph.analyzeCell state.KnownBindings e.CellIndex e.Code e.Result)
  |> CellDependencyGraph.buildGraph

let private run steps =
  steps |> List.fold (fun st (code, result) -> recordEval code result 5L st) FeaturePushState.empty

let private capOf cells =
  match EvalStore.HistoryCap.tryCreate cells with
  | Ok cap -> cap
  | Error reason -> failtest reason

let private expectScopeMatchesRebuild (state: FeaturePushState) =
  let expected = fullScope state
  let actual = scope state
  actual.Bindings |> Expect.equal "bindings match a rebuild" expected.Bindings
  actual.ActiveBindings |> Expect.equal "active bindings match a rebuild" expected.ActiveBindings
  actual.ShadowedBindings |> Expect.equal "shadowed bindings match a rebuild" expected.ShadowedBindings

let private expectGraphMatchesRebuild (state: FeaturePushState) =
  let expected = fullGraph state
  let actual = cellGraph state
  actual.Cells |> Expect.equal "cells match a rebuild" expected.Cells
  actual.Edges |> Expect.equal "edges match a rebuild" expected.Edges

// Cells that stress every reference rule: redefinition, shadowing, `it`,
// dotted access (`x.Length` does not consume x), primed and operator names,
// multi-binding cells, `mutable` (whose explorer name is "mutable m"),
// string/comment mentions and failed cells that bind nothing.
let private genName = Gen.elements [ "a"; "b"; "x"; "it"; "x'"; "(+.)" ]

let private genExpr =
  Gen.elements [ "1"; "a"; "b + a"; "x.Length"; "Foo.x"; "x'"; "a(+.)b"; "\"a\""; "it"; "mutable m"; "m"; "// b" ]

let private genStep =
  Gen.oneof [
    Gen.map2 (fun n e -> sprintf "let %s = %s" n e, sprintf "val %s: int = 42" n) genName genExpr
    Gen.map3 (fun n1 n2 e -> sprintf "let %s, %s = %s, 2" n1 n2 e, sprintf "val %s: int = 1\nval %s: int = 2" n1 n2) genName genName genExpr
    Gen.map (fun e -> e, "val it: int = 3") genExpr
    Gen.map (fun e -> e, "error FS0039: The value or constructor is not defined.") genExpr
    Gen.map (fun e -> sprintf "let mutable m = %s" e, "val mutable m: int = 1") genExpr
  ]

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

  testList "indexed scope equals a rebuild" [
    test "redefinition + cross-cell refs" {
      run [
        "let x = 1", "val x: int = 1"
        "let y = x + 1", "val y: int = 2"
        "let x = 10", "val x: int = 10"
        "let z = x + y", "val z: int = 12"
      ]
      |> expectScopeMatchesRebuild
    }

    test "the first eval's binding is in scope" {
      let state = FeaturePushState.empty |> recordEval "let x = 1" "val x: int = 1" 50L
      (scope state).ActiveBindings |> Map.containsKey "x" |> Expect.isTrue "x is active after its eval"
    }

    test "no evals: an empty scope and an empty graph" {
      (scope FeaturePushState.empty).Bindings |> Expect.isEmpty "no bindings before any eval"
      (cellGraph FeaturePushState.empty).Cells |> Expect.isEmpty "no cells before any eval"
    }
  ]

  testList "indexed graph equals a rebuild" [
    test "redefinition + cross-cell refs" {
      run [
        "let x = 1", "val x: int = 1"
        "let y = x + 1", "val y: int = 2"
        "let x = 10", "val x: int = 10"
        "let z = x + y", "val z: int = 12"
        "let w = z * 2", "val w: int = 24"
      ]
      |> expectGraphMatchesRebuild
    }

    test "shadowed binding retargets consumers to the latest producer" {
      run [
        "let a = 1", "val a: int = 1"
        "let b = a + 1", "val b: int = 2"
        "let a = 100", "val a: int = 100"
      ]
      |> expectGraphMatchesRebuild
    }

    test "independent cells accumulate edges" {
      run [
        "let p = 1", "val p: int = 1"
        "let q = 2", "val q: int = 2"
        "let r = p + q", "val r: int = 3"
      ]
      |> expectGraphMatchesRebuild
    }

    test "a cell consuming a binding redefined by a later independent cell" {
      run [
        "let a = 1", "val a: int = 1"
        "let b = a", "val b: int = 1"
        "let c = a + 1", "val c: int = 2"
        "let a = 5", "val a: int = 5"
      ]
      |> expectGraphMatchesRebuild
    }
  ]

  testList "incremental caches agree with a from-scratch rebuild" [
    test "an older cell that mentions a name defined later depends on the defining cell" {
      // The user evaluates `y + 1` before defining y (it fails), then defines
      // y. A rebuild links the failed cell to y's producer; the old append
      // path only retargeted names that already had a producer, so it missed it.
      let state =
        run [
          "y + 1", "error FS0039: The value or constructor 'y' is not defined."
          "let y = 1", "val y: int = 1"
        ]
      expectGraphMatchesRebuild state
      (cellGraph state).Edges |> Expect.equal "the failed cell depends on y's producer" [ (1, 0) ]
    }

    test "a cell binding two names shadows an older same-named binding once" {
      let state =
        run [
          "let a = 1", "val a: int = 1"
          "let a, b = 2, 3", "val a: int = 2\nval b: int = 3"
        ]
      expectScopeMatchesRebuild state
      let first = (scope state).Bindings |> List.head
      first.ShadowedBy |> Expect.equal "shadowed by cell 1 exactly once" [ 1 ]
    }

    testPropertyWithConfig
      { FsCheckConfig.defaultConfig with maxTest = 300 }
      "random eval sequences under a small cap: indexed scope and graph equal a rebuild of the retained cells" <|
      Prop.forAll (Arb.fromGen (Gen.zip (Gen.choose (1, 6)) (Gen.listOf genStep))) (fun (capCells, steps) ->
        let state =
          steps
          |> List.fold (fun st (code, result) -> recordEval code result 5L st) (FeaturePushState.withCap (capOf capCells))
        let kept = min capCells steps.Length
        state.EvalHistory
        |> List.rev
        |> List.map (fun e -> e.CellIndex, e.Code, e.Result)
        |> Expect.equal "exactly the newest cells are retained, oldest first"
             (steps
              |> List.mapi (fun i (code, result) -> i, code, result)
              |> List.skip (steps.Length - kept))
        state.NextCellIndex |> Expect.equal "cell ids never repeat" steps.Length
        state.KnownBindings
        |> Expect.equal "known bindings remember the newest producer of every name ever bound"
             (steps
              |> List.mapi (fun i (_, result) -> i, result)
              |> List.fold (fun known (i, result) ->
                CellDependencyGraph.producedNames result |> List.fold (fun k name -> Map.add name i k) known) Map.empty)
        expectScopeMatchesRebuild state
        expectGraphMatchesRebuild state)
  ]

  testList "history cap" [
    test "the standard cap keeps the newest 10,000 cells and evicts the oldest on eval 10,001" {
      let state =
        [ 0 .. MaxEvalHistory ]
        |> List.fold (fun st i -> recordEval (sprintf "let v%d = %d" i i) (sprintf "val v%d: int = %d" i i) 1L st) FeaturePushState.empty
      EvalStore.count state.History |> Expect.equal "history is at the cap" MaxEvalHistory
      EvalStore.oldestId state.History |> Expect.equal "cell 0 was evicted" 1
      state.NextCellIndex |> Expect.equal "ids keep counting" (MaxEvalHistory + 1)
      (scope state).ActiveBindings |> Map.containsKey "v0" |> Expect.isFalse "the evicted cell's binding left the scope"
      (scope state).ActiveBindings |> Map.count |> Expect.equal "every retained binding is active" MaxEvalHistory
    }

    test "a cap below one cell is refused with the reason" {
      EvalStore.HistoryCap.tryCreate 0
      |> Result.mapError (fun reason -> reason.Contains "at least 1 cell")
      |> Expect.equal "zero cells is not a history" (Error true)
    }

    test "recentEvals returns the newest cells, oldest first" {
      let state =
        [ 0 .. 24 ]
        |> List.fold (fun st i -> recordEval (sprintf "%d" i) (sprintf "val it: int = %d" i) 1L st) FeaturePushState.empty
      recentEvals 20 state
      |> List.map (fun e -> e.CellIndex)
      |> Expect.equal "the 20 most recent evals, newest last" [ 5 .. 24 ]
      recentEvals 20 (FeaturePushState.empty |> recordEval "1" "val it: int = 1" 1L)
      |> List.map (fun e -> e.CellIndex)
      |> Expect.equal "fewer evals than asked for returns them all" [ 0 ]
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
