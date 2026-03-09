module SageFs.Tests.EvalRippleTests

open Expecto
open Expecto.Flip
open SageFs.Features
open SageFs.Features.EvalRipple

// Helper to build a simple graph for testing
let private mkCell id source produces consumes : CellInfo =
  { Id = id; Source = source; Produces = produces; Consumes = consumes }

let private mkGraph cells edges : CellGraph =
  { Cells = cells |> List.map (fun (c: CellInfo) -> c.Id, c) |> Map.ofList
    Edges = edges }

[<Tests>]
let ripplePlanTests =
  testList "EvalRipple plan" [

    testCase "no dependents yields empty plan" <| fun _ ->
      let g = mkGraph [ mkCell 1 "let x = 1" ["x"] [] ] []
      let plan = planRipple g (set [1])
      plan.Steps |> Expect.hasLength "no cascade" 0

    testCase "linear chain A->B->C" <| fun _ ->
      let g = mkGraph
                [ mkCell 1 "let a = 1" ["a"] []
                  mkCell 2 "let b = a" ["b"] ["a"]
                  mkCell 3 "let c = b" ["c"] ["b"] ]
                [ (1, 2); (2, 3) ]
      let plan = planRipple g (set [1])
      plan.Steps |> Expect.hasLength "two dependents" 2
      // topological order: 2 before 3
      plan.Steps.[0].CellId |> Expect.equal "first" 2
      plan.Steps.[1].CellId |> Expect.equal "second" 3

    testCase "diamond dependency" <| fun _ ->
      //   1
      //  / \
      // 2   3
      //  \ /
      //   4
      let g = mkGraph
                [ mkCell 1 "let a = 1" ["a"] []
                  mkCell 2 "let b = a" ["b"] ["a"]
                  mkCell 3 "let c = a" ["c"] ["a"]
                  mkCell 4 "let d = b + c" ["d"] ["b"; "c"] ]
                [ (1, 2); (1, 3); (2, 4); (3, 4) ]
      let plan = planRipple g (set [1])
      plan.Steps |> Expect.hasLength "three dependents" 3
      // cell 4 must come after both 2 and 3
      let idx4 = plan.Steps |> List.findIndex (fun s -> s.CellId = 4)
      let idx2 = plan.Steps |> List.findIndex (fun s -> s.CellId = 2)
      let idx3 = plan.Steps |> List.findIndex (fun s -> s.CellId = 3)
      (idx4 > idx2) |> Expect.isTrue "4 after 2"
      (idx4 > idx3) |> Expect.isTrue "4 after 3"

    testCase "all steps start as Pending" <| fun _ ->
      let g = mkGraph
                [ mkCell 1 "let a = 1" ["a"] []
                  mkCell 2 "let b = a" ["b"] ["a"] ]
                [ (1, 2) ]
      let plan = planRipple g (set [1])
      plan.Steps
      |> List.forall (fun s -> s.Status = Pending)
      |> Expect.isTrue "all pending"

    testCase "multiple changed cells union their dependents" <| fun _ ->
      let g = mkGraph
                [ mkCell 1 "let a = 1" ["a"] []
                  mkCell 2 "let b = 2" ["b"] []
                  mkCell 3 "let c = a" ["c"] ["a"]
                  mkCell 4 "let d = b" ["d"] ["b"] ]
                [ (1, 3); (2, 4) ]
      let plan = planRipple g (set [1; 2])
      plan.Steps |> Expect.hasLength "both dependents" 2
  ]

[<Tests>]
let rippleAdvanceTests =
  testList "EvalRipple advance" [

    testCase "advance marks step succeeded" <| fun _ ->
      let g = mkGraph
                [ mkCell 1 "let a = 1" ["a"] []
                  mkCell 2 "let b = a" ["b"] ["a"] ]
                [ (1, 2) ]
      let plan =
        planRipple g (set [1])
        |> advanceStep 2 (Ok "val b: int = 1")
      plan.Steps.[0].Status
      |> Expect.equal "succeeded" (Succeeded "val b: int = 1")

    testCase "advance marks step failed" <| fun _ ->
      let g = mkGraph
                [ mkCell 1 "let a = 1" ["a"] []
                  mkCell 2 "let b = a" ["b"] ["a"] ]
                [ (1, 2) ]
      let plan =
        planRipple g (set [1])
        |> advanceStep 2 (Error "type mismatch")
      plan.Steps.[0].Status
      |> Expect.equal "failed" (Failed "type mismatch")

    testCase "failure cascades to downstream as Skipped" <| fun _ ->
      let g = mkGraph
                [ mkCell 1 "let a = 1" ["a"] []
                  mkCell 2 "let b = a" ["b"] ["a"]
                  mkCell 3 "let c = b" ["c"] ["b"] ]
                [ (1, 2); (2, 3) ]
      let plan =
        planRipple g (set [1])
        |> advanceStep 2 (Error "boom")
      plan.Steps.[1].Status
      |> Expect.equal "skipped" (Skipped "upstream 2 failed")
  ]

[<Tests>]
let rippleRenderTests =
  testList "EvalRipple render" [

    testCase "render pending step" <| fun _ ->
      let step = { CellId = 2; Code = "let b = a"; Status = Pending }
      renderStep step |> Expect.stringContains "has pending" "⏳"

    testCase "render succeeded step" <| fun _ ->
      let step = { CellId = 2; Code = "let b = a"; Status = Succeeded "val b: int" }
      renderStep step |> Expect.stringContains "has check" "✅"

    testCase "render failed step" <| fun _ ->
      let step = { CellId = 2; Code = "let b = a"; Status = Failed "error" }
      renderStep step |> Expect.stringContains "has x" "❌"

    testCase "render skipped step" <| fun _ ->
      let step = { CellId = 2; Code = "let b = a"; Status = Skipped "upstream" }
      renderStep step |> Expect.stringContains "has skip" "⏭"

    testCase "summary counts statuses" <| fun _ ->
      let plan = {
        ChangedCells = set [1]
        Steps = [
          { CellId = 2; Code = "a"; Status = Succeeded "ok" }
          { CellId = 3; Code = "b"; Status = Failed "err" }
          { CellId = 4; Code = "c"; Status = Skipped "upstream" }
        ]
      }
      let s = summary plan
      s |> Expect.stringContains "has succeeded" "1 succeeded"
      s |> Expect.stringContains "has failed" "1 failed"
      s |> Expect.stringContains "has skipped" "1 skipped"
  ]
