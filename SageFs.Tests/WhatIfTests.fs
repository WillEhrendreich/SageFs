module SageFs.Tests.WhatIfTests

open Expecto
open Expecto.Flip
open SageFs.Features
open SageFs.Features.CellDependencyGraph
open SageFs.Features.WhatIf

let private mkCell id source produces consumes : CellInfo =
  { Id = id; Source = source; Produces = produces; Consumes = consumes }

let private mkGraph cells edges : CellGraph =
  { Cells = cells |> List.map (fun (c: CellInfo) -> c.Id, c) |> Map.ofList
    Edges = edges }

[<Tests>]
let whatIfOverrideTests =
  testList "WhatIf overrides" [

    testCase "create override for binding" <| fun _ ->
      let ov = createOverride "taxRate" "0.20" "0.25" "float"
      ov.BindingName |> Expect.equal "name" "taxRate"
      ov.OriginalValue |> Expect.equal "original" "0.20"
      ov.OverrideValue |> Expect.equal "override" "0.25"
      ov.TypeSig |> Expect.equal "type" "float"

    testCase "format override as let binding" <| fun _ ->
      let ov = createOverride "rate" "0.20" "0.25" "float"
      let code = formatOverrideAsLet ov
      code |> Expect.equal "let binding" "let rate = 0.25"

    testCase "format override display" <| fun _ ->
      let ov = createOverride "n" "10" "100" "int"
      let s = formatOverride ov
      s |> Expect.stringContains "has name" "n"
      s |> Expect.stringContains "has arrow" "→"
      s |> Expect.stringContains "has new" "100"
  ]

[<Tests>]
let whatIfPlanTests =
  testList "WhatIf plan" [

    testCase "plan identifies affected cells" <| fun _ ->
      let g = mkGraph
                [ mkCell 1 "let rate = 0.20" ["rate"] []
                  mkCell 2 "let tax = price * rate" ["tax"] ["rate"]
                  mkCell 3 "let total = price + tax" ["total"] ["tax"] ]
                [ (1, 2); (2, 3) ]
      let ov = createOverride "rate" "0.20" "0.25" "float"
      let plan = planWhatIf g ov
      plan.AffectedCells |> Expect.hasLength "two downstream" 2

    testCase "plan includes ripple steps in order" <| fun _ ->
      let g = mkGraph
                [ mkCell 1 "let x = 1" ["x"] []
                  mkCell 2 "let y = x + 1" ["y"] ["x"]
                  mkCell 3 "let z = y * 2" ["z"] ["y"] ]
                [ (1, 2); (2, 3) ]
      let ov = createOverride "x" "1" "10" "int"
      let plan = planWhatIf g ov
      plan.RippleSteps |> Expect.hasLength "two steps" 2
      plan.RippleSteps.[0].CellId |> Expect.equal "y first" 2
      plan.RippleSteps.[1].CellId |> Expect.equal "z second" 3

    testCase "unrelated cells not affected" <| fun _ ->
      let g = mkGraph
                [ mkCell 1 "let a = 1" ["a"] []
                  mkCell 2 "let b = 2" ["b"] []
                  mkCell 3 "let c = a" ["c"] ["a"] ]
                [ (1, 3) ]
      let ov = createOverride "b" "2" "99" "int"
      let plan = planWhatIf g ov
      plan.AffectedCells |> Expect.hasLength "none affected" 0
  ]

[<Tests>]
let whatIfDiffTests =
  testList "WhatIf diff" [

    testCase "diff result shows change" <| fun _ ->
      let d = {
        Override = createOverride "x" "1" "10" "int"
        OriginalOutputs = [ (2, "val y: int = 2"); (3, "val z: int = 4") ]
        NewOutputs = [ (2, "val y: int = 11"); (3, "val z: int = 22") ]
      }
      let s = formatDiff d
      s |> Expect.stringContains "shows original" "2"
      s |> Expect.stringContains "shows new" "11"

    testCase "diff with no changes" <| fun _ ->
      let d = {
        Override = createOverride "x" "1" "1" "int"
        OriginalOutputs = [ (2, "val y: int = 2") ]
        NewOutputs = [ (2, "val y: int = 2") ]
      }
      let s = formatDiff d
      s |> Expect.stringContains "no change marker" "unchanged"

    testCase "summary of what-if scenario" <| fun _ ->
      let ov = createOverride "rate" "0.20" "0.25" "float"
      let plan = {
        Override = ov
        AffectedCells = [2; 3]
        RippleSteps = [
          { CellId = 2; Code = "let tax = price * rate"; Status = Pending }
          { CellId = 3; Code = "let total = price + tax"; Status = Pending }
        ]
      }
      let s = formatPlanSummary plan
      s |> Expect.stringContains "has binding" "rate"
      s |> Expect.stringContains "has count" "2"
  ]
