module SageFs.Tests.NotebookExportTests

open Expecto
open Expecto.Flip
open FsCheck
open SageFs.Features.NotebookExport

[<Tests>]
let notebookExportTests = testList "NotebookExport" [

  testList "Property-based" [
    testPropertyWithConfig { FsCheckConfig.defaultConfig with maxTest = 100 }
      "CellMarker round-trips: parse(format(m)) = Some m"
      (fun (idx: PositiveInt) ->
        let meta = { Index = idx.Get; Label = None; Deps = []; Bindings = [] }
        let formatted = CellMarker.format meta
        let parsed = CellMarker.parse formatted
        parsed |> Expect.equal "should round-trip" (Some meta))

    testPropertyWithConfig { FsCheckConfig.defaultConfig with maxTest = 50 }
      "exported fsx contains all cell code"
      (fun () ->
        let header = { Project = "Test"; CellCount = 2; Timestamp = "now" }
        let cells = [
          { Metadata = { Index = 0; Label = None; Deps = []; Bindings = [] }
            Code = "let x = 1"; Output = None }
          { Metadata = { Index = 1; Label = None; Deps = []; Bindings = [] }
            Code = "let y = 2"; Output = None }
        ]
        let fsx = exportNotebook header cells
        fsx.Contains("let x = 1") |> Expect.isTrue "should contain cell 0 code"
        fsx.Contains("let y = 2") |> Expect.isTrue "should contain cell 1 code")
  ]

  testList "CellMarker" [
    testCase "format with all fields" <| fun () ->
      let meta = { Index = 0; Label = Some "setup"; Deps = [1; 2]; Bindings = ["x"; "y"] }
      let formatted = CellMarker.format meta
      formatted |> Expect.stringContains "has cell index" "@sagefs-cell[0]"
      formatted |> Expect.stringContains "has label" "label:setup"
      formatted |> Expect.stringContains "has deps" "deps:1,2"
      formatted |> Expect.stringContains "has bindings" "bindings:x,y"

    testCase "round-trip with deps and label" <| fun () ->
      let meta = { Index = 3; Label = Some "test"; Deps = [0; 1]; Bindings = ["a"; "b"] }
      CellMarker.parse (CellMarker.format meta)
      |> Expect.equal "should round-trip" (Some meta)

    testCase "parse rejects non-marker lines" <| fun () ->
      CellMarker.parse "let x = 1"
      |> Expect.isNone "should reject plain code"
  ]

  testList "export/import" [
    testCase "export produces valid fsx block comments" <| fun () ->
      let header = { Project = "MyProject"; CellCount = 1; Timestamp = "2026-01-01" }
      let cells = [
        { Metadata = { Index = 0; Label = None; Deps = []; Bindings = [] }
          Code = "let x = 42"; Output = Some "val x : int = 42" }
      ]
      let fsx = exportNotebook header cells
      fsx |> Expect.stringContains "has header" "@sagefs-notebook"
      fsx |> Expect.stringContains "has code" "let x = 42"
      fsx |> Expect.stringContains "has output" "Output:"

    testCase "import recovers cells from exported fsx" <| fun () ->
      let header = { Project = "Test"; CellCount = 2; Timestamp = "now" }
      let cells = [
        { Metadata = { Index = 0; Label = None; Deps = []; Bindings = [] }
          Code = "let x = 1"; Output = None }
        { Metadata = { Index = 1; Label = None; Deps = [0]; Bindings = ["x"] }
          Code = "let y = x + 1"; Output = Some "val y : int = 2" }
      ]
      let fsx = exportNotebook header cells
      let imported = importNotebook fsx
      imported |> List.length |> Expect.equal "should recover 2 cells" 2
      imported.[0].Code |> Expect.stringContains "first cell code" "let x = 1"

    testCase "import handles plain fsharp with no markers" <| fun () ->
      let fsx = "let x = 1\nlet y = 2"
      let imported = importNotebook fsx
      imported |> Expect.isEmpty "should produce no cells from plain fsx"
  ]
]
