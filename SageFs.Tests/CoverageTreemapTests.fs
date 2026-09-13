module SageFs.Tests.CoverageTreemapTests

open System
open Expecto
open Expecto.Flip
open FsCheck
open FsCheck.FSharp
open SageFs.Features.Treemap

let private propConfig = { FsCheckConfig.defaultConfig with maxTest = 200 }

// ── squarify: pure layout ──────────────────────────────────────

[<Tests>]
let squarifyTests =
  testList "Treemap.squarify" [

    testCase "empty items yields no rectangles" <| fun _ ->
      squarify [] { X = 0.0; Y = 0.0; W = 100.0; H = 100.0 }
      |> Expect.equal "no items, no rects" []

    testCase "zero-width bounds yields no rectangles" <| fun _ ->
      squarify [ "a", 1.0 ] { X = 0.0; Y = 0.0; W = 0.0; H = 100.0 }
      |> Expect.equal "degenerate bounds, no rects" []

    testCase "single item fills the whole bounds" <| fun _ ->
      let bounds = { X = 0.0; Y = 0.0; W = 200.0; H = 100.0 }
      squarify [ "only", 5.0 ] bounds
      |> Expect.equal "one rect returned, matching bounds exactly" [ "only", bounds ]

    testCase "every item appears exactly once" <| fun _ ->
      let items = [ "a", 10.0; "b", 20.0; "c", 5.0; "d", 40.0 ]
      squarify items { X = 0.0; Y = 0.0; W = 300.0; H = 150.0 }
      |> List.map fst |> List.sort
      |> Expect.equal "same ids, once each" (items |> List.map fst |> List.sort)

    testCase "area is proportional to weight" <| fun _ ->
      let bounds = { X = 0.0; Y = 0.0; W = 400.0; H = 300.0 }
      let result = squarify [ "a", 30.0; "b", 10.0 ] bounds |> Map.ofList
      let areaA = Rect.area result.["a"]
      let areaB = Rect.area result.["b"]
      (areaA / areaB) |> Expect.floatClose "3x weight -> 3x area" (Accuracy.medium) 3.0

    testPropertyWithConfig propConfig "rectangles tile the whole bounds area" <|
      fun (weights: PositiveInt list) ->
        let ws = weights |> List.truncate 12 |> List.map (fun (PositiveInt w) -> float w)
        (ws.Length > 0) ==> lazy (
          let items = ws |> List.mapi (fun i w -> i, w)
          let bounds = { X = 0.0; Y = 0.0; W = 400.0; H = 300.0 }
          let result = squarify items bounds
          let totalArea = result |> List.sumBy (fun (_, r) -> Rect.area r)
          abs (totalArea - Rect.area bounds) < 0.5)

    testPropertyWithConfig propConfig "every rectangle stays within the bounds" <|
      fun (weights: PositiveInt list) ->
        let ws = weights |> List.truncate 12 |> List.map (fun (PositiveInt w) -> float w)
        (ws.Length > 0) ==> lazy (
          let items = ws |> List.mapi (fun i w -> i, w)
          let bounds = { X = 10.0; Y = 20.0; W = 400.0; H = 300.0 }
          let result = squarify items bounds
          result
          |> List.forall (fun (_, r) ->
            r.X >= bounds.X - 0.01 && r.Y >= bounds.Y - 0.01
            && r.X + r.W <= bounds.X + bounds.W + 0.01
            && r.Y + r.H <= bounds.Y + bounds.H + 0.01))
  ]

// ── Coverage hierarchy ───────────────────────────────────────────

let private mkFile filePath projectName probeCount coveredCount hasFailing symbols : FileCoverageFact =
  { FilePath = filePath
    ProjectName = projectName
    ProbeCount = probeCount
    CoveredCount = coveredCount
    HasFailingTest = hasFailing
    Symbols = symbols }

let private mkSymbol name line testCount passingCount failingCount : SymbolCoverageFact =
  { SymbolName = name; Line = line; TestCount = testCount; PassingCount = passingCount; FailingCount = failingCount }

[<Tests>]
let coverageTreemapTests =
  testList "CoverageTreemapNode" [

    testCase "empty facts yields an empty, uncovered solution root" <| fun _ ->
      let root = CoverageTreemapNode.build "MySolution" []
      root.Kind |> Expect.equal "root kind" CoverageNodeKind.Solution
      root.Children |> Expect.equal "no children" []
      root.Status |> Expect.equal "no data -> grey/uncovered" CoverageStatus.Uncovered

    testCase "a fully-covered file with no failures is green" <| fun _ ->
      let facts = [ mkFile "SageFs.Core/Foo.fs" "SageFs.Core" 10 10 false [] ]
      let root = CoverageTreemapNode.build "sol" facts
      let file = root |> CoverageTreemapNode.tryFind "SageFs.Core/Foo.fs" |> Option.get
      file.Status |> Expect.equal "covered file is green" CoverageStatus.Covered

    testCase "a file with zero covered probes is grey (the gap to see)" <| fun _ ->
      let facts = [ mkFile "SageFs.Core/Bar.fs" "SageFs.Core" 8 0 false [] ]
      let root = CoverageTreemapNode.build "sol" facts
      let file = root |> CoverageTreemapNode.tryFind "SageFs.Core/Bar.fs" |> Option.get
      file.Status |> Expect.equal "no coverage -> grey" CoverageStatus.Uncovered

    testCase "a file with no instrumented probes at all is grey" <| fun _ ->
      let facts = [ mkFile "SageFs.Core/Empty.fs" "SageFs.Core" 0 0 false [] ]
      let root = CoverageTreemapNode.build "sol" facts
      let file = root |> CoverageTreemapNode.tryFind "SageFs.Core/Empty.fs" |> Option.get
      file.Status |> Expect.equal "no probes -> grey" CoverageStatus.Uncovered

    testCase "a file touched by a failing test is red, even if mostly covered" <| fun _ ->
      let facts = [ mkFile "SageFs.Core/Baz.fs" "SageFs.Core" 10 9 true [] ]
      let root = CoverageTreemapNode.build "sol" facts
      let file = root |> CoverageTreemapNode.tryFind "SageFs.Core/Baz.fs" |> Option.get
      file.Status |> Expect.equal "failing test -> red" CoverageStatus.Failed

    testCase "project status rolls up to red when any file failed" <| fun _ ->
      let facts =
        [ mkFile "SageFs.Core/A.fs" "SageFs.Core" 5 5 false []
          mkFile "SageFs.Core/B.fs" "SageFs.Core" 5 1 true [] ]
      let root = CoverageTreemapNode.build "sol" facts
      let project = root |> CoverageTreemapNode.tryFind "SageFs.Core" |> Option.get
      project.Status |> Expect.equal "any failure taints the project red" CoverageStatus.Failed

    testCase "project status is grey only when every file is uncovered" <| fun _ ->
      let facts =
        [ mkFile "SageFs.Core/A.fs" "SageFs.Core" 5 0 false []
          mkFile "SageFs.Core/B.fs" "SageFs.Core" 5 0 false [] ]
      let root = CoverageTreemapNode.build "sol" facts
      let project = root |> CoverageTreemapNode.tryFind "SageFs.Core" |> Option.get
      project.Status |> Expect.equal "all-uncovered project is grey" CoverageStatus.Uncovered

    testCase "project status is green when some files covered and none failed" <| fun _ ->
      let facts =
        [ mkFile "SageFs.Core/A.fs" "SageFs.Core" 5 0 false []
          mkFile "SageFs.Core/B.fs" "SageFs.Core" 5 5 false [] ]
      let root = CoverageTreemapNode.build "sol" facts
      let project = root |> CoverageTreemapNode.tryFind "SageFs.Core" |> Option.get
      project.Status |> Expect.equal "partial-but-passing coverage is green" CoverageStatus.Covered

    testCase "solution groups files by project" <| fun _ ->
      let facts =
        [ mkFile "SageFs.Core/A.fs" "SageFs.Core" 1 1 false []
          mkFile "SageFs/B.fs" "SageFs" 1 1 false [] ]
      let root = CoverageTreemapNode.build "sol" facts
      root.Children |> List.map (fun c -> c.Id) |> List.sort
      |> Expect.equal "one project node per distinct ProjectName" [ "SageFs"; "SageFs.Core" ]

    testCase "symbol status: a failing test on the symbol is red" <| fun _ ->
      let facts =
        [ mkFile "SageFs.Core/A.fs" "SageFs.Core" 2 2 false
            [ mkSymbol "MyModule.f" 10 1 0 1 ] ]
      let root = CoverageTreemapNode.build "sol" facts
      let sym = root |> CoverageTreemapNode.tryFind "SageFs.Core/A.fs::MyModule.f" |> Option.get
      sym.Status |> Expect.equal "failing test on symbol -> red" CoverageStatus.Failed

    testCase "symbol status: passing tests -> green" <| fun _ ->
      let facts =
        [ mkFile "SageFs.Core/A.fs" "SageFs.Core" 2 2 false
            [ mkSymbol "MyModule.f" 10 1 1 0 ] ]
      let root = CoverageTreemapNode.build "sol" facts
      let sym = root |> CoverageTreemapNode.tryFind "SageFs.Core/A.fs::MyModule.f" |> Option.get
      sym.Status |> Expect.equal "passing test -> green" CoverageStatus.Covered

    testCase "symbol status: no tests at all -> grey" <| fun _ ->
      let facts =
        [ mkFile "SageFs.Core/A.fs" "SageFs.Core" 2 2 false
            [ mkSymbol "MyModule.untested" 20 0 0 0 ] ]
      let root = CoverageTreemapNode.build "sol" facts
      let sym = root |> CoverageTreemapNode.tryFind "SageFs.Core/A.fs::MyModule.untested" |> Option.get
      sym.Status |> Expect.equal "no covering test -> grey" CoverageStatus.Uncovered

    testCase "a failing symbol taints its file red even if IL coverage looks clean" <| fun _ ->
      let facts =
        [ mkFile "SageFs.Core/A.fs" "SageFs.Core" 10 10 false
            [ mkSymbol "MyModule.f" 10 1 0 1 ] ]
      let root = CoverageTreemapNode.build "sol" facts
      let file = root |> CoverageTreemapNode.tryFind "SageFs.Core/A.fs" |> Option.get
      file.Status |> Expect.equal "symbol failure outranks IL coverage" CoverageStatus.Failed

    testCase "tryFind returns None for an unknown id" <| fun _ ->
      let root = CoverageTreemapNode.build "sol" [ mkFile "SageFs.Core/A.fs" "SageFs.Core" 1 1 false [] ]
      root |> CoverageTreemapNode.tryFind "does-not-exist"
      |> Expect.isNone "unknown id"

    testCase "layoutChildren packs a node's children, not its grandchildren" <| fun _ ->
      let facts =
        [ mkFile "SageFs.Core/A.fs" "SageFs.Core" 4 4 false
            [ mkSymbol "M.f" 1 1 1 0; mkSymbol "M.g" 2 1 1 0 ]
          mkFile "SageFs.Core/B.fs" "SageFs.Core" 4 4 false [] ]
      let root = CoverageTreemapNode.build "sol" facts
      let project = root |> CoverageTreemapNode.tryFind "SageFs.Core" |> Option.get
      let laidOut = CoverageTreemapNode.layoutChildren { X = 0.0; Y = 0.0; W = 100.0; H = 100.0 } project
      laidOut |> List.map (fun (n, _) -> n.Kind) |> List.distinct
      |> Expect.equal "children are Files, not Symbols" [ CoverageNodeKind.File ]

    testCase "layoutChildren on a leaf (no children) yields nothing" <| fun _ ->
      let facts = [ mkFile "SageFs.Core/Leaf.fs" "SageFs.Core" 1 1 false [] ]
      let root = CoverageTreemapNode.build "sol" facts
      let file = root |> CoverageTreemapNode.tryFind "SageFs.Core/Leaf.fs" |> Option.get
      CoverageTreemapNode.layoutChildren { X = 0.0; Y = 0.0; W = 50.0; H = 50.0 } file
      |> Expect.equal "leaf has no children to lay out" []
  ]

// ── projectNameForFile ────────────────────────────────────────────

[<Tests>]
let projectNameForFileTests =
  testList "CoverageTreemapNode.projectNameForFile" [

    testCase "matches the longest containing project directory" <| fun _ ->
      let dirs = [ "SageFs", "/repo/SageFs"; "SageFs.Core", "/repo/SageFs.Core" ]
      CoverageTreemapNode.projectNameForFile dirs "/repo/SageFs.Core/Features/Foo.fs"
      |> Expect.equal "picks SageFs.Core, not the shorter SageFs prefix" "SageFs.Core"

    testCase "falls back to External for a file outside every known project" <| fun _ ->
      let dirs = [ "SageFs.Core", "/repo/SageFs.Core" ]
      CoverageTreemapNode.projectNameForFile dirs "/tmp/somewhere/else.fs"
      |> Expect.equal "unknown file -> External" "External"

    testCase "is case-insensitive and normalizes backslashes" <| fun _ ->
      let dirs = [ "SageFs.Core", "/repo/SageFs.Core" ]
      CoverageTreemapNode.projectNameForFile dirs "/REPO/sagefs.core\\Features\\Foo.fs"
      |> Expect.equal "case/slash-insensitive match" "SageFs.Core"
  ]
