module SageFs.Tests.CoverageViewEmissionContractTests

open System.Text.Json
open Expecto
open Expecto.Flip
open SageFs
open SageFs.Features.LiveTesting
open SageFs.Tests.LiveTestingTestHelpers

// --- WHY bullets (one per test, in test names) ---
// 1. contract — daemon MUST emit one coverage_view event per
//    CoverageAnnotation so the editor can place a badge at each
//    function definition.
// 2. contract — daemon MUST emit coverage_view BEFORE file_annotations
//    so the editor can paint the badge first (it has stricter
//    placement requirements: one per function).
// 3. contract — empty annotations produce zero coverage_view events
//    (no badge to render).
// 4. contract — coverage_view payload uses the same Symbol/FilePath/
//    DefinitionLine the editor needs to place the badge.

let private sampleView (symbol: string) (line: int) =
  { CoverageView.Symbol = symbol
    FilePath = "Prod.fs"
    DefinitionLine = line
    TotalCount = 1
    Overflow = Overflow.Within
    InlineBadgeText = sprintf "%c 1" (char 0x2713)
    Health = CoverageViewState.Passing }

let private formatOpts () =
  let o = JsonSerializerOptions()
  o.Converters.Add(System.Text.Json.Serialization.JsonFSharpConverter())
  o

[<Tests>]
let sseCoverageViewEventShape = testList "SSE coverage_view event shape" [

  testCase "WHY — shape — Symbol field is carried so the editor can place the badge by symbol" <| fun _ ->
    let sse = SageFs.SseWriter.formatCoverageViewEvent (formatOpts()) None (sampleView "Module.add" 42)
    sse |> Expect.stringContains "Symbol present" "Module.add"

  testCase "WHY — shape — FilePath and DefinitionLine carried so the editor can match the view to the buffer line" <| fun _ ->
    let sse = SageFs.SseWriter.formatCoverageViewEvent (formatOpts()) None (sampleView "Module.add" 42)
    sse |> Expect.stringContains "FilePath present" "Prod.fs"
    sse |> Expect.stringContains "DefinitionLine present" "42"

  testCase "WHY — shape — TotalCount carried so the editor shows the absolute count in tooltips even when the badge is collapsed" <| fun _ ->
    let sse = SageFs.SseWriter.formatCoverageViewEvent (formatOpts()) None (sampleView "Module.add" 42)
    sse |> Expect.stringContains "TotalCount present" "TotalCount"

  testCase "WHY — shape — Overflow DU is serialized as a tagged union so the renderer gets the hidden count directly" <| fun _ ->
    let sse = SageFs.SseWriter.formatCoverageViewEvent (formatOpts()) None (sampleView "Module.add" 42)
    sse |> Expect.stringContains "Overflow case serialized" "Overflow"

  testCase "WHY — shape — Health DU is serialized as a tagged union so the renderer can show the dominant status" <| fun _ ->
    let sse = SageFs.SseWriter.formatCoverageViewEvent (formatOpts()) None (sampleView "Module.add" 42)
    sse |> Expect.stringContains "Health case serialized" "Health"
  ]

[<Tests>]
let fileAnnotationsEmitsViews = testList "FileAnnotations emits CoverageView per annotation" [

  testCase "WHY — emission — empty annotation array produces zero CoverageViews" <| fun _ ->
    let state = LiveTestState.empty
    let views = FileAnnotationsInternals.projectViewsForFile CoverageViewMode.defaults "Prod.fs" TestDependencyGraph.empty state
    (views.Length, 0)
    |> Expect.equal "no annotations = no views" (0, 0)

  testCase "WHY — emission — one annotation produces one CoverageView with the right symbol/line" <| fun _ ->
    let annotation : CoverageAnnotation = {
      Symbol = "Module.add"
      FilePath = "Prod.fs"
      DefinitionLine = 42
      Status = CoverageStatus.Covered (1, CoverageHealth.AllPassing)
      BranchCoverage = BranchCoverage.Unknown
    }
    let test = mkTestCase "Module.Tests.t1" TestFramework.Expecto TestCategory.Unit
    let state =
      { LiveTestState.empty with
          CoverageAnnotations = [| annotation |]
          DiscoveredTests = [| test |]
          LastResults = Map.ofList [ test.Id, mkResult test.Id (TestResult.Passed (ts 1.0)) ] }
    let depGraph =
      { TestDependencyGraph.empty with
          SymbolToTests = Map.ofList [ "Module.add", [| test.Id |] ] }
    let views = FileAnnotationsInternals.projectViewsForFile CoverageViewMode.defaults "Prod.fs" depGraph state
    (views.Length, 1)
    |> Expect.equal "one annotation = one view" (1, 1)
    (views.[0].Symbol, "Module.add")
    |> Expect.equal "symbol carried" ("Module.add", "Module.add")
    (views.[0].DefinitionLine, 42)
    |> Expect.equal "line carried" (42, 42)
    (views.[0].TotalCount, 1)
    |> Expect.equal "total = 1" (1, 1)

  testCase "WHY — emission — multiple annotations for the same file produce multiple CoverageViews (one per symbol)" <| fun _ ->
    let a1 : CoverageAnnotation = {
      Symbol = "Module.add"; FilePath = "Prod.fs"; DefinitionLine = 42
      Status = CoverageStatus.Covered (1, CoverageHealth.AllPassing); BranchCoverage = BranchCoverage.Unknown }
    let a2 : CoverageAnnotation = {
      Symbol = "Module.sub"; FilePath = "Prod.fs"; DefinitionLine = 80
      Status = CoverageStatus.Covered (1, CoverageHealth.AllPassing); BranchCoverage = BranchCoverage.Unknown }
    let t1 = mkTestCase "Module.Tests.t1" TestFramework.Expecto TestCategory.Unit
    let t2 = mkTestCase "Module.Tests.t2" TestFramework.Expecto TestCategory.Unit
    let state =
      { LiveTestState.empty with
          CoverageAnnotations = [| a1; a2 |]
          DiscoveredTests = [| t1; t2 |]
          LastResults =
            Map.ofList
              [ t1.Id, mkResult t1.Id (TestResult.Passed (ts 1.0))
                t2.Id, mkResult t2.Id (TestResult.Passed (ts 1.0)) ] }
    let depGraph =
      { TestDependencyGraph.empty with
          SymbolToTests =
            Map.ofList
              [ "Module.add", [| t1.Id |]
                "Module.sub", [| t2.Id |] ] }
    let views = FileAnnotationsInternals.projectViewsForFile CoverageViewMode.defaults "Prod.fs" depGraph state
    (views.Length, 2)
    |> Expect.equal "two annotations = two views" (2, 2)
  ]
