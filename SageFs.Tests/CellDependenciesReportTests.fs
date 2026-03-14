module SageFs.Tests.CellDependenciesReportTests

open Expecto
open Expecto.Flip
open SageFs.Features.CellDependencyGraph
open SageFs.Features.CellDependenciesReport

// ── Test helpers ──────────────────────────────────────────────

let private mkInfo id produces consumes =
  { Id = id; Source = $"// cell {id}"; Produces = produces; Consumes = consumes }

let private mkGraph cells edges : CellGraph =
  { Cells = cells |> List.map (fun (c: CellInfo) -> c.Id, c) |> Map.ofList
    Edges = edges }

/// Build a simple chain: cell0 → cell1 → cell2
let private chainGraph () =
  let c0 = mkInfo 0 ["x"] []
  let c1 = mkInfo 1 ["y"] ["x"]
  let c2 = mkInfo 2 ["z"] ["y"]
  mkGraph [c0; c1; c2] [(0,1); (1,2)]

/// Build a diamond: c0 → c1, c0 → c2, c1 → c3, c2 → c3
let private diamondGraph () =
  let c0 = mkInfo 0 ["a"] []
  let c1 = mkInfo 1 ["b"] ["a"]
  let c2 = mkInfo 2 ["c"] ["a"]
  let c3 = mkInfo 3 ["d"] ["b"; "c"]
  mkGraph [c0; c1; c2; c3] [(0,1); (0,2); (1,3); (2,3)]

// ── compose — graph shape ─────────────────────────────────────

[<Tests>]
let graphShapeTests =
  testList "CellDependenciesReport.compose graph shape" [

    testCase "empty graph produces empty report" <| fun _ ->
      let g = mkGraph [] []
      let r = CellDependenciesReport.compose g Set.empty
      r.TotalCells |> Expect.equal "0 cells" 0
      r.TotalEdges |> Expect.equal "0 edges" 0

    testCase "single cell no edges" <| fun _ ->
      let c = mkInfo 42 ["result"] []
      let g = mkGraph [c] []
      let r = CellDependenciesReport.compose g Set.empty
      r.TotalCells |> Expect.equal "1 cell" 1
      r.TotalEdges |> Expect.equal "0 edges" 0

    testCase "chain graph has correct cell and edge counts" <| fun _ ->
      let g = chainGraph ()
      let r = CellDependenciesReport.compose g Set.empty
      r.TotalCells |> Expect.equal "3 cells" 3
      r.TotalEdges |> Expect.equal "2 edges" 2

    testCase "nodes sorted by CellId ascending" <| fun _ ->
      let g = chainGraph ()
      let r = CellDependenciesReport.compose g Set.empty
      let ids = r.Nodes |> List.map (fun n -> n.Id)
      ids |> Expect.equal "sorted 0,1,2" [0; 1; 2]
  ]

// ── compose — upstream/downstream wiring ─────────────────────

[<Tests>]
let wiringsTests =
  testList "CellDependenciesReport.compose upstream/downstream" [

    testCase "cell 0 has downstream 1 in chain" <| fun _ ->
      let g = chainGraph ()
      let r = CellDependenciesReport.compose g Set.empty
      let node0 = r.Nodes |> List.find (fun n -> n.Id = 0)
      node0.DownstreamIds |> Expect.equal "downstream = [1]" [1]
      node0.UpstreamIds   |> Expect.isEmpty "no upstream for cell 0"

    testCase "cell 1 has upstream 0 and downstream 2 in chain" <| fun _ ->
      let g = chainGraph ()
      let r = CellDependenciesReport.compose g Set.empty
      let node1 = r.Nodes |> List.find (fun n -> n.Id = 1)
      node1.UpstreamIds   |> Expect.equal "upstream = [0]" [0]
      node1.DownstreamIds |> Expect.equal "downstream = [2]" [2]

    testCase "leaf cell has no downstream" <| fun _ ->
      let g = chainGraph ()
      let r = CellDependenciesReport.compose g Set.empty
      let node2 = r.Nodes |> List.find (fun n -> n.Id = 2)
      node2.DownstreamIds |> Expect.isEmpty "no downstream for leaf"

    testCase "diamond: cell 3 has two upstream sources" <| fun _ ->
      let g = diamondGraph ()
      let r = CellDependenciesReport.compose g Set.empty
      let node3 = r.Nodes |> List.find (fun n -> n.Id = 3)
      node3.UpstreamIds |> List.sort |> Expect.equal "upstream = [1;2]" [1; 2]

    testCase "diamond: cell 0 has two downstream consumers" <| fun _ ->
      let g = diamondGraph ()
      let r = CellDependenciesReport.compose g Set.empty
      let node0 = r.Nodes |> List.find (fun n -> n.Id = 0)
      node0.DownstreamIds |> List.sort |> Expect.equal "downstream = [1;2]" [1; 2]
  ]

// ── compose — staleness propagation ──────────────────────────

[<Tests>]
let stalenessTests =
  testList "CellDependenciesReport.compose staleness" [

    testCase "no changed cells → all fresh" <| fun _ ->
      let g = chainGraph ()
      let r = CellDependenciesReport.compose g Set.empty
      r.TotalStale   |> Expect.equal "0 stale" 0
      r.StaleCellIds |> Expect.isEmpty "empty stale list"

    testCase "changing cell 0 marks 0,1,2 all stale in chain" <| fun _ ->
      let g = chainGraph ()
      let r = CellDependenciesReport.compose g (Set.singleton 0)
      r.TotalStale   |> Expect.equal "3 stale" 3
      r.StaleCellIds |> List.sort |> Expect.equal "stale = [0;1;2]" [0; 1; 2]

    testCase "changing leaf cell 2 only marks 2 stale" <| fun _ ->
      let g = chainGraph ()
      let r = CellDependenciesReport.compose g (Set.singleton 2)
      r.TotalStale   |> Expect.equal "1 stale" 1
      r.StaleCellIds |> Expect.equal "stale = [2]" [2]

    testCase "stale cells have Staleness = StaleFrom" <| fun _ ->
      let g = chainGraph ()
      let r = CellDependenciesReport.compose g (Set.singleton 0)
      r.Nodes |> List.forall (fun n -> CellFreshness.isStale n.Staleness) |> Expect.isTrue "all stale in chain"

    testCase "diamond: changing root marks all stale" <| fun _ ->
      let g = diamondGraph ()
      let r = CellDependenciesReport.compose g (Set.singleton 0)
      r.TotalStale |> Expect.equal "4 stale" 4

    testCase "stale cell lists its stale upstream causes" <| fun _ ->
      let g = chainGraph ()
      let r = CellDependenciesReport.compose g (Set.singleton 0)
      let node1 = r.Nodes |> List.find (fun n -> n.Id = 1)
      CellFreshness.causes node1.Staleness |> Expect.equal "cause = [0]" [0]

    testCase "fresh cell has Fresh staleness" <| fun _ ->
      let g = chainGraph ()
      let r = CellDependenciesReport.compose g Set.empty
      r.Nodes |> List.forall (fun n -> n.Staleness = CellFreshness.Fresh) |> Expect.isTrue "all fresh"
  ]

// ── summarize ─────────────────────────────────────────────────

[<Tests>]
let summarizeTests =
  testList "CellDependenciesReport.summarize" [

    testCase "summary contains cell count" <| fun _ ->
      let g = chainGraph ()
      let r = CellDependenciesReport.compose g Set.empty
      r.Summary |> Expect.stringContains "cell count" "3"

    testCase "summary says 'all fresh' when no stale" <| fun _ ->
      let g = chainGraph ()
      let r = CellDependenciesReport.compose g Set.empty
      r.Summary |> Expect.stringContains "all fresh" "fresh"

    testCase "summary mentions stale count when stale" <| fun _ ->
      let g = chainGraph ()
      let r = CellDependenciesReport.compose g (Set.singleton 0)
      r.Summary |> Expect.stringContains "stale indicator" "stale"

    testCase "empty graph summary is stable" <| fun _ ->
      let g = mkGraph [] []
      let r = CellDependenciesReport.compose g Set.empty
      r.Summary |> Expect.stringContains "zero cells" "0"
  ]

// ── Produces / Consumes passthrough ──────────────────────────

[<Tests>]
let bindingTests =
  testList "CellDependenciesReport.compose binding passthrough" [

    testCase "Produces list preserved in node" <| fun _ ->
      let c = mkInfo 0 ["foo"; "bar"] []
      let g = mkGraph [c] []
      let r = CellDependenciesReport.compose g Set.empty
      r.Nodes.[0].Produces |> Expect.equal "produces" ["foo"; "bar"]

    testCase "Consumes list preserved in node" <| fun _ ->
      let c = mkInfo 1 ["baz"] ["foo"; "bar"]
      let g = mkGraph [c] []
      let r = CellDependenciesReport.compose g Set.empty
      r.Nodes.[0].Consumes |> Expect.equal "consumes" ["foo"; "bar"]
  ]

[<Tests>]
let allTests =
  testList "CellDependenciesReport" [
    graphShapeTests
    wiringsTests
    stalenessTests
    summarizeTests
    bindingTests
  ]
