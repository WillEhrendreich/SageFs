module SageFs.Tests.SessionScribeTests

open Expecto
open Expecto.Flip
open SageFs.Features.CellDependencyGraph
open SageFs.Features.SessionScribe

[<Tests>]
let deduplicationTests =
  testList "SessionScribe dedup" [

    testCase "keeps latest version of re-evaluated cell" <| fun _ ->
      let entries = [
        { ScribeEntry.CellId = 1; Code = "let x = 1"; Bindings = ["x"]; Dependencies = [] }
        { ScribeEntry.CellId = 1; Code = "let x = 42"; Bindings = ["x"]; Dependencies = [] }
      ]
      let deduped = SessionScribe.dedup entries
      deduped |> Expect.hasLength "one entry after dedup" 1
      deduped.[0].Code |> Expect.equal "latest code wins" "let x = 42"

    testCase "preserves distinct cells" <| fun _ ->
      let entries = [
        { ScribeEntry.CellId = 1; Code = "let x = 1"; Bindings = ["x"]; Dependencies = [] }
        { ScribeEntry.CellId = 2; Code = "let y = 2"; Bindings = ["y"]; Dependencies = [] }
      ]
      SessionScribe.dedup entries
      |> Expect.hasLength "both preserved" 2

    testCase "empty input produces empty output" <| fun _ ->
      SessionScribe.dedup []
      |> Expect.hasLength "empty" 0
  ]

[<Tests>]
let topologicalSortTests =
  testList "SessionScribe toposort" [

    testCase "independent cells preserve order" <| fun _ ->
      let entries = [
        { ScribeEntry.CellId = 1; Code = "let a = 1"; Bindings = ["a"]; Dependencies = [] }
        { ScribeEntry.CellId = 2; Code = "let b = 2"; Bindings = ["b"]; Dependencies = [] }
      ]
      let sorted = SessionScribe.toposort entries
      sorted |> List.map (fun e -> e.CellId)
      |> Expect.equal "preserve original order" [1; 2]

    testCase "dependent cell comes after producer" <| fun _ ->
      let entries = [
        { ScribeEntry.CellId = 2; Code = "let y = x + 1"; Bindings = ["y"]; Dependencies = [1] }
        { ScribeEntry.CellId = 1; Code = "let x = 5"; Bindings = ["x"]; Dependencies = [] }
      ]
      let sorted = SessionScribe.toposort entries
      let ids = sorted |> List.map (fun e -> e.CellId)
      let xIdx = ids |> List.findIndex (fun id -> id = 1)
      let yIdx = ids |> List.findIndex (fun id -> id = 2)
      (yIdx, xIdx) |> Expect.isGreaterThan "y should come after x"

    testCase "chain a→b→c sorted correctly" <| fun _ ->
      let entries = [
        { ScribeEntry.CellId = 3; Code = "let c = b + 1"; Bindings = ["c"]; Dependencies = [2] }
        { ScribeEntry.CellId = 1; Code = "let a = 1"; Bindings = ["a"]; Dependencies = [] }
        { ScribeEntry.CellId = 2; Code = "let b = a + 1"; Bindings = ["b"]; Dependencies = [1] }
      ]
      let sorted = SessionScribe.toposort entries
      sorted |> List.map (fun e -> e.CellId)
      |> Expect.equal "topological order" [1; 2; 3]

    testCase "empty input" <| fun _ ->
      SessionScribe.toposort []
      |> Expect.hasLength "empty" 0
  ]

[<Tests>]
let exportTests =
  testList "SessionScribe export" [

    testCase "export produces valid fsx" <| fun _ ->
      let entries = [
        { ScribeEntry.CellId = 1; Code = "let x = 42"; Bindings = ["x"]; Dependencies = [] }
        { ScribeEntry.CellId = 2; Code = "let y = x + 1"; Bindings = ["y"]; Dependencies = [1] }
      ]
      let fsx = SessionScribe.exportFsx "MyProject" entries
      fsx |> Expect.stringContains "has project name" "MyProject"
      fsx |> Expect.stringContains "has first cell" "let x = 42"
      fsx |> Expect.stringContains "has second cell" "let y = x + 1"

    testCase "export includes cell separators" <| fun _ ->
      let entries = [
        { ScribeEntry.CellId = 1; Code = "let a = 1"; Bindings = ["a"]; Dependencies = [] }
        { ScribeEntry.CellId = 2; Code = "let b = 2"; Bindings = ["b"]; Dependencies = [] }
      ]
      let fsx = SessionScribe.exportFsx "Test" entries
      fsx |> Expect.stringContains "has separator" ";;"

    testCase "export deduplicates and sorts" <| fun _ ->
      let entries = [
        { ScribeEntry.CellId = 2; Code = "let y = x + 1"; Bindings = ["y"]; Dependencies = [1] }
        { ScribeEntry.CellId = 1; Code = "let x = 1"; Bindings = ["x"]; Dependencies = [] }
        { ScribeEntry.CellId = 1; Code = "let x = 99"; Bindings = ["x"]; Dependencies = [] }
      ]
      let fsx = SessionScribe.exportFsx "Test" entries
      fsx |> Expect.stringContains "latest x value" "let x = 99"
      let xPos = fsx.IndexOf("let x = 99")
      let yPos = fsx.IndexOf("let y = x + 1")
      (yPos, xPos) |> Expect.isGreaterThan "y after x in export"

    testCase "empty session exports header only" <| fun _ ->
      let fsx = SessionScribe.exportFsx "Empty" []
      fsx |> Expect.stringContains "has project" "Empty"
  ]

[<Tests>]
let fromGraphTests =
  testList "SessionScribe fromGraph" [

    testCase "converts CellGraph to ScribeEntries" <| fun _ ->
      let cells = [
        { Id = 1; Source = "let x = 5"; Produces = ["x"]; Consumes = [] }
        { Id = 2; Source = "let y = x + 1"; Produces = ["y"]; Consumes = ["x"] }
      ]
      let graph = buildGraph cells
      let entries = SessionScribe.fromGraph graph
      entries |> Expect.hasLength "two entries" 2
      let e1 = entries |> List.find (fun e -> e.CellId = 1)
      e1.Bindings |> Expect.equal "x binding" ["x"]
      e1.Dependencies |> Expect.equal "no deps" []
      let e2 = entries |> List.find (fun e -> e.CellId = 2)
      e2.Dependencies |> Expect.contains "depends on cell 1" 1
  ]
