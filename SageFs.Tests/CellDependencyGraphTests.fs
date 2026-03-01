module SageFs.Tests.CellDependencyGraphTests

open Expecto
open Expecto.Flip
open FsCheck
open SageFs.Features.CellDependencyGraph

[<Tests>]
let cellDepGraphTests = testList "CellDependencyGraph" [

  testList "Property-based" [
    testPropertyWithConfig { FsCheckConfig.defaultConfig with maxTest = 100 }
      "transitive stale is idempotent"
      (fun (seed: int) ->
        let cells = [
          { Id = 0; Source = "let x = 1"; Produces = ["x"]; Consumes = [] }
          { Id = 1; Source = "let y = x + 1"; Produces = ["y"]; Consumes = ["x"] }
          { Id = 2; Source = "let z = y + 1"; Produces = ["z"]; Consumes = ["y"] }
        ]
        let graph = buildGraph cells
        let stale1 = transitiveStale graph 0
        let stale2 = transitiveStale graph 0
        stale1 |> List.sort |> Expect.equal "idempotent" (stale2 |> List.sort))

    testPropertyWithConfig { FsCheckConfig.defaultConfig with maxTest = 50 }
      "consumer of binding is in stale set of producer"
      (fun () ->
        let cells = [
          { Id = 0; Source = "let a = 1"; Produces = ["a"]; Consumes = [] }
          { Id = 1; Source = "let b = a"; Produces = ["b"]; Consumes = ["a"] }
        ]
        let graph = buildGraph cells
        let stale = transitiveStale graph 0
        stale |> List.contains 1 |> Expect.isTrue "cell 1 should be stale")

    testPropertyWithConfig { FsCheckConfig.defaultConfig with maxTest = 50 }
      "isolated cell has empty stale set"
      (fun () ->
        let cells = [
          { Id = 0; Source = "let a = 1"; Produces = ["a"]; Consumes = [] }
          { Id = 1; Source = "let b = 2"; Produces = ["b"]; Consumes = [] }
        ]
        let graph = buildGraph cells
        transitiveStale graph 0
        |> Expect.isEmpty "isolated cell should have no stale dependents")
  ]

  testList "analyzeCell" [
    testCase "extracts val bindings from FSI output" <| fun () ->
      let cell = analyzeCell Map.empty 0 "let x = 1" "val x : int = 1"
      cell.Produces |> Expect.equal "should extract x" ["x"]

    testCase "detects consumed bindings" <| fun () ->
      let known = Map.ofList [("x", 0)]
      let cell = analyzeCell known 1 "let y = x + 1" "val y : int = 2"
      cell.Consumes |> Expect.equal "should consume x" ["x"]

    testCase "no self-reference in consumes" <| fun () ->
      let known = Map.ofList [("x", 0)]
      let cell = analyzeCell known 0 "let x = x + 1" "val x : int = 2"
      cell.Consumes |> Expect.isEmpty "should not self-reference"
  ]

  testList "buildGraph" [
    testCase "creates edges from producer to consumer" <| fun () ->
      let cells = [
        { Id = 0; Source = ""; Produces = ["x"]; Consumes = [] }
        { Id = 1; Source = ""; Produces = ["y"]; Consumes = ["x"] }
      ]
      let graph = buildGraph cells
      graph.Edges |> Expect.equal "should have edge 0→1" [(0, 1)]

    testCase "diamond dependency creates correct edges" <| fun () ->
      let cells = [
        { Id = 0; Source = ""; Produces = ["a"]; Consumes = [] }
        { Id = 1; Source = ""; Produces = ["b"]; Consumes = ["a"] }
        { Id = 2; Source = ""; Produces = ["c"]; Consumes = ["a"] }
        { Id = 3; Source = ""; Produces = ["d"]; Consumes = ["b"; "c"] }
      ]
      let graph = buildGraph cells
      graph.Edges |> List.sort |> Expect.equal "diamond edges"
        ([(0, 1); (0, 2); (1, 3); (2, 3)] |> List.sort)
  ]

  testList "transitiveStale" [
    testCase "finds all downstream in chain" <| fun () ->
      let cells = [
        { Id = 0; Source = ""; Produces = ["a"]; Consumes = [] }
        { Id = 1; Source = ""; Produces = ["b"]; Consumes = ["a"] }
        { Id = 2; Source = ""; Produces = ["c"]; Consumes = ["b"] }
      ]
      let graph = buildGraph cells
      transitiveStale graph 0 |> List.sort
      |> Expect.equal "should find 1 and 2" [1; 2]

    testCase "leaf node returns empty" <| fun () ->
      let cells = [
        { Id = 0; Source = ""; Produces = ["a"]; Consumes = [] }
        { Id = 1; Source = ""; Produces = ["b"]; Consumes = ["a"] }
      ]
      let graph = buildGraph cells
      transitiveStale graph 1
      |> Expect.isEmpty "leaf should have no dependents"

    testCase "isolated cell returns empty" <| fun () ->
      let cells = [
        { Id = 0; Source = ""; Produces = ["a"]; Consumes = [] }
      ]
      let graph = buildGraph cells
      transitiveStale graph 0
      |> Expect.isEmpty "isolated cell has no dependents"
  ]
]
