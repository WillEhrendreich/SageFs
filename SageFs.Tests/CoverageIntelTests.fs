module SageFs.Tests.CoverageIntelTests

open System
open Expecto
open Expecto.Flip
open SageFs.Features
open SageFs.Features.LiveTesting
open SageFs.Features.CoverageIntel

// ── Test helpers ──────────────────────────────────────────────

let private mkTestId name = TestId.TestId name

let private mkNarrative causalChanges summary = {
  LastPassedAt = Some DateTimeOffset.UtcNow
  TimeSinceLastPass = Some (TimeSpan.FromMinutes 5.0)
  CausalChanges = causalChanges
  PropertyViolation = None
  Summary = summary
}

let private mkSequencePoint file line endLine branchId = {
  File = file
  Line = line
  Column = 0
  EndLine = endLine
  EndColumn = 0
  BranchId = branchId
}

let private mkInstrumentationMap (slots: SequencePoint array) = {
  Slots = slots
  TotalProbes = slots.Length
  TrackerTypeName = "TestTracker"
  HitsFieldName = "hits"
}

let private mkDepGraph symbolToTests transitiveCoverage = {
  SymbolToTests = symbolToTests |> Map.ofList
  TransitiveCoverage = transitiveCoverage |> Map.ofList
  PerFileIndex = Map.empty
  SourceVersion = 1
}

let private emptyDepGraph = mkDepGraph [] []
let private emptyBitmaps : Map<TestId, CoverageBitmap> = Map.empty

/// Create a CoverageBitmap with specific bits set.
let private mkBitmap (totalProbes: int) (hitIndices: int list) : CoverageBitmap =
  let bools = Array.init totalProbes (fun i -> hitIndices |> List.contains i)
  CoverageBitmap.ofBoolArray bools

// ── Verdict classification tests ─────────────────────────────

[<Tests>]
let verdictTests =
  testList "CoverageIntel.classifyVerdict" [

    testCase "100% → WellCovered" <| fun _ ->
      CoverageIntel.classifyVerdict 100.0
      |> Expect.equal "should be WellCovered" WellCovered

    testCase "80% → WellCovered" <| fun _ ->
      CoverageIntel.classifyVerdict 80.0
      |> Expect.equal "should be WellCovered" WellCovered

    testCase "79.9% → PartialBlindSpot" <| fun _ ->
      CoverageIntel.classifyVerdict 79.9
      |> Expect.equal "should be PartialBlindSpot" PartialBlindSpot

    testCase "40% → PartialBlindSpot" <| fun _ ->
      CoverageIntel.classifyVerdict 40.0
      |> Expect.equal "should be PartialBlindSpot" PartialBlindSpot

    testCase "39.9% → DiagnosticBlindSpot" <| fun _ ->
      CoverageIntel.classifyVerdict 39.9
      |> Expect.equal "should be DiagnosticBlindSpot" DiagnosticBlindSpot

    testCase "0% → DiagnosticBlindSpot" <| fun _ ->
      CoverageIntel.classifyVerdict 0.0
      |> Expect.equal "should be DiagnosticBlindSpot" DiagnosticBlindSpot
  ]

// ── Causal symbol extraction tests ───────────────────────────

[<Tests>]
let causalSymbolTests =
  testList "CoverageIntel.extractCausalSymbols" [

    testCase "SymbolChanged extracted" <| fun _ ->
      let narrative = mkNarrative [ CausalChange.SymbolChanged "parseExpr" ] ""
      CoverageIntel.extractCausalSymbols narrative
      |> Expect.equal "should extract symbol" [ "parseExpr" ]

    testCase "FileChanged and Unknown filtered out" <| fun _ ->
      let changes =
        [ CausalChange.SymbolChanged "foo"
          CausalChange.FileChanged "bar.fs"
          CausalChange.Unknown
          CausalChange.SymbolChanged "baz" ]
      let narrative = mkNarrative changes ""
      CoverageIntel.extractCausalSymbols narrative
      |> Expect.equal "should only have symbols" [ "foo"; "baz" ]

    testCase "empty changes → empty list" <| fun _ ->
      let narrative = mkNarrative [] ""
      CoverageIntel.extractCausalSymbols narrative
      |> Expect.isEmpty "should be empty"
  ]

// ── Blind spot detection tests ───────────────────────────────

[<Tests>]
let blindSpotTests =
  testList "CoverageIntel.findBlindSpots" [

    testCase "no instrumentation maps → no blind spots" <| fun _ ->
      CoverageIntel.findBlindSpots "test.fs" [||] emptyBitmaps
      |> Expect.isEmpty "should be empty"

    testCase "all branches covered → no blind spots" <| fun _ ->
      let points = [|
        mkSequencePoint "test.fs" 10 10 0
        mkSequencePoint "test.fs" 20 20 1
      |]
      let maps = [| mkInstrumentationMap points |]
      let bitmaps =
        Map.ofList [ mkTestId "t1", mkBitmap 2 [ 0; 1 ] ]
      CoverageIntel.findBlindSpots "test.fs" maps bitmaps
      |> Expect.isEmpty "all covered, no blind spots"

    testCase "one uncovered branch → one blind spot" <| fun _ ->
      let points = [|
        mkSequencePoint "test.fs" 10 10 0
        mkSequencePoint "test.fs" 20 20 1
      |]
      let maps = [| mkInstrumentationMap points |]
      let bitmaps =
        Map.ofList [ mkTestId "t1", mkBitmap 2 [ 0 ] ]  // Only first hit
      let gaps = CoverageIntel.findBlindSpots "test.fs" maps bitmaps

      gaps |> Expect.hasLength "should have 1 gap" 1
      gaps.[0].Line |> Expect.equal "gap at line 20" 20
      gaps.[0].BranchId |> Expect.equal "branch 1" 1

    testCase "blind spots include nearest covered line" <| fun _ ->
      let points = [|
        mkSequencePoint "test.fs" 10 10 0
        mkSequencePoint "test.fs" 15 15 1
        mkSequencePoint "test.fs" 50 50 2
      |]
      let maps = [| mkInstrumentationMap points |]
      let bitmaps =
        Map.ofList [ mkTestId "t1", mkBitmap 3 [ 0 ] ]  // Only line 10 hit
      let gaps = CoverageIntel.findBlindSpots "test.fs" maps bitmaps

      gaps |> Expect.hasLength "should have 2 gaps" 2
      // Line 15 is closer to covered line 10 than line 50 is
      gaps.[0].NearestCoveredLine
      |> Expect.equal "nearest to line 15 is line 10" (Some 10)

    testCase "filters to specified file only" <| fun _ ->
      let points = [|
        mkSequencePoint "test.fs" 10 10 0
        mkSequencePoint "other.fs" 20 20 1
      |]
      let maps = [| mkInstrumentationMap points |]
      let bitmaps = Map.ofList [ mkTestId "t1", mkBitmap 2 [] ]
      let gaps = CoverageIntel.findBlindSpots "test.fs" maps bitmaps

      gaps |> Expect.hasLength "only test.fs gaps" 1
      gaps.[0].FilePath |> Expect.equal "file path" "test.fs"
  ]

// ── Correlated test discovery tests ──────────────────────────

[<Tests>]
let correlatedTests =
  testList "CoverageIntel.findCorrelatedTests" [

    testCase "finds tests covering same symbols" <| fun _ ->
      let depGraph = mkDepGraph [] [
        "parseExpr", [| mkTestId "t1"; mkTestId "t2"; mkTestId "t3" |]
      ]
      let result = CoverageIntel.findCorrelatedTests [ "parseExpr" ] depGraph (mkTestId "t1")

      result |> Expect.hasLength "should find 2 correlated" 2
      result |> Expect.contains "should have t2" (mkTestId "t2")
      result |> Expect.contains "should have t3" (mkTestId "t3")

    testCase "excludes the failing test itself" <| fun _ ->
      let depGraph = mkDepGraph [] [
        "foo", [| mkTestId "t1" |]
      ]
      CoverageIntel.findCorrelatedTests [ "foo" ] depGraph (mkTestId "t1")
      |> Expect.isEmpty "should exclude self"

    testCase "empty dep graph → empty" <| fun _ ->
      CoverageIntel.findCorrelatedTests [ "foo" ] emptyDepGraph (mkTestId "t1")
      |> Expect.isEmpty "should be empty"

    testCase "multiple symbols merge results" <| fun _ ->
      let depGraph = mkDepGraph [] [
        "foo", [| mkTestId "t2" |]
        "bar", [| mkTestId "t3" |]
      ]
      let result = CoverageIntel.findCorrelatedTests [ "foo"; "bar" ] depGraph (mkTestId "t1")

      result |> Expect.hasLength "should find 2" 2
  ]

// ── Composition tests ────────────────────────────────────────

[<Tests>]
let composeTests =
  testList "CoverageIntel.composeForFailure" [

    testCase "test with 100% branch coverage → WellCovered" <| fun _ ->
      let points = [|
        mkSequencePoint "src.fs" 10 10 0
        mkSequencePoint "src.fs" 20 20 1
        mkSequencePoint "src.fs" 30 30 2
      |]
      let maps = [| mkInstrumentationMap points |]
      let bitmaps =
        Map.ofList [ mkTestId "t1", mkBitmap 3 [ 0; 1; 2 ] ]
      let narrative = mkNarrative [ CausalChange.SymbolChanged "parseExpr" ] "broke"

      let report = CoverageIntel.composeForFailure
                     (mkTestId "t1") "parseExpr test" narrative
                     [ "src.fs" ] maps bitmaps emptyDepGraph

      report.Verdict |> Expect.equal "should be WellCovered" WellCovered
      report.CoveragePercent |> Expect.equal "100%" 100.0
      report.BlindSpots |> Expect.isEmpty "no blind spots"

    testCase "test with 0% coverage → DiagnosticBlindSpot" <| fun _ ->
      let points = [|
        mkSequencePoint "src.fs" 10 10 0
        mkSequencePoint "src.fs" 20 20 1
      |]
      let maps = [| mkInstrumentationMap points |]
      let narrative = mkNarrative [ CausalChange.SymbolChanged "broken" ] "all bad"

      let report = CoverageIntel.composeForFailure
                     (mkTestId "t1") "broken test" narrative
                     [ "src.fs" ] maps emptyBitmaps emptyDepGraph

      report.Verdict |> Expect.equal "should be DiagnosticBlindSpot" DiagnosticBlindSpot
      report.CoveragePercent |> Expect.equal "0%" 0.0
      report.BlindSpots |> Expect.hasLength "2 blind spots" 2

    testCase "partial coverage → lists specific blind spot branches" <| fun _ ->
      let points = [|
        mkSequencePoint "src.fs" 10 10 0
        mkSequencePoint "src.fs" 20 20 1
        mkSequencePoint "src.fs" 30 30 2
        mkSequencePoint "src.fs" 40 40 3
        mkSequencePoint "src.fs" 50 50 4
      |]
      let maps = [| mkInstrumentationMap points |]
      // Hit 3 of 5 = 60% → PartialBlindSpot
      let bitmaps =
        Map.ofList [ mkTestId "t1", mkBitmap 5 [ 0; 1; 2 ] ]
      let narrative = mkNarrative [ CausalChange.SymbolChanged "x" ] ""

      let report = CoverageIntel.composeForFailure
                     (mkTestId "t1") "partial test" narrative
                     [ "src.fs" ] maps bitmaps emptyDepGraph

      report.Verdict |> Expect.equal "should be PartialBlindSpot" PartialBlindSpot
      report.CoveredBranches |> Expect.equal "3 covered" 3
      report.TotalBranches |> Expect.equal "5 total" 5
      report.BlindSpots |> Expect.hasLength "2 gaps" 2

    testCase "correlated failures found via dep graph" <| fun _ ->
      let maps = [| mkInstrumentationMap [||] |]
      let depGraph = mkDepGraph [] [
        "parseExpr", [| mkTestId "t1"; mkTestId "t2"; mkTestId "t3" |]
      ]
      let narrative = mkNarrative [ CausalChange.SymbolChanged "parseExpr" ] ""

      let report = CoverageIntel.composeForFailure
                     (mkTestId "t1") "my test" narrative
                     [] maps emptyBitmaps depGraph

      report.CorrelatedFailures |> Expect.hasLength "2 correlated" 2

    testCase "empty instrumentation → graceful empty report" <| fun _ ->
      let narrative = mkNarrative [ CausalChange.SymbolChanged "x" ] ""

      let report = CoverageIntel.composeForFailure
                     (mkTestId "t1") "empty test" narrative
                     [] [||] emptyBitmaps emptyDepGraph

      report.TotalBranches |> Expect.equal "0 branches" 0
      report.CoveragePercent |> Expect.equal "0%" 0.0
      report.BlindSpots |> Expect.isEmpty "no spots"
  ]

// ── Batch compose tests ──────────────────────────────────────

[<Tests>]
let batchComposeTests =
  testList "CoverageIntel.compose" [

    testCase "composes multiple failures" <| fun _ ->
      let maps = [| mkInstrumentationMap [||] |]
      let failures = [
        mkTestId "t1", "test one", mkNarrative [ CausalChange.SymbolChanged "a" ] ""
        mkTestId "t2", "test two", mkNarrative [ CausalChange.SymbolChanged "b" ] ""
      ]
      let resolver _ = []

      let reports = CoverageIntel.compose failures resolver maps emptyBitmaps emptyDepGraph

      reports |> Expect.hasLength "should have 2 reports" 2
      reports.[0].TestName |> Expect.equal "first name" "test one"
      reports.[1].TestName |> Expect.equal "second name" "test two"

    testCase "empty failures → empty list" <| fun _ ->
      CoverageIntel.compose [] (fun _ -> []) [||] emptyBitmaps emptyDepGraph
      |> Expect.isEmpty "should be empty"
  ]

// ── Summarize tests ──────────────────────────────────────────

[<Tests>]
let summarizeTests =
  testList "CoverageIntel.summarize" [

    testCase "WellCovered summary mentions focus on logic" <| fun _ ->
      let report = {
        CoverageIntelReport.empty (mkTestId "t1") "my test" with
          Verdict = WellCovered
          CoveragePercent = 95.0
          CoveredBranches = 19
          TotalBranches = 20
      }
      let text = CoverageIntel.summarize report
      text |> Expect.stringContains "has icon" "✅"
      text |> Expect.stringContains "mentions logic" "logic"

    testCase "DiagnosticBlindSpot summary says write tests" <| fun _ ->
      let report = {
        CoverageIntelReport.empty (mkTestId "t1") "bad test" with
          Verdict = DiagnosticBlindSpot
          CoveragePercent = 10.0
      }
      let text = CoverageIntel.summarize report
      text |> Expect.stringContains "has icon" "🔴"
      text |> Expect.stringContains "says write tests" "Write tests"

    testCase "PartialBlindSpot summary shows gap count" <| fun _ ->
      let report = {
        CoverageIntelReport.empty (mkTestId "t1") "partial" with
          Verdict = PartialBlindSpot
          BlindSpots = [
            { FilePath = "x.fs"; Line = 10; EndLine = 10; BranchId = 0; NearestCoveredLine = None }
            { FilePath = "x.fs"; Line = 20; EndLine = 20; BranchId = 1; NearestCoveredLine = None }
          ]
      }
      let text = CoverageIntel.summarize report
      text |> Expect.stringContains "has icon" "⚠️"
      text |> Expect.stringContains "count" "2"

    testCase "correlated tests shown in summary" <| fun _ ->
      let report = {
        CoverageIntelReport.empty (mkTestId "t1") "test" with
          Verdict = WellCovered
          CorrelatedFailures = [ mkTestId "t2"; mkTestId "t3" ]
      }
      let text = CoverageIntel.summarize report
      text |> Expect.stringContains "shows correlated" "2 other test"
  ]

// ── Property tests ───────────────────────────────────────────

[<Tests>]
let propertyTests =
  testList "CoverageIntel properties" [

    testCase "BlindSpots + CoveredBranches = TotalBranches" <| fun _ ->
      let points = [|
        mkSequencePoint "f.fs" 1 1 0
        mkSequencePoint "f.fs" 2 2 1
        mkSequencePoint "f.fs" 3 3 2
        mkSequencePoint "f.fs" 4 4 3
      |]
      let maps = [| mkInstrumentationMap points |]
      let bitmaps = Map.ofList [ mkTestId "t1", mkBitmap 4 [ 0; 2 ] ]
      let narrative = mkNarrative [ CausalChange.SymbolChanged "x" ] ""

      let report = CoverageIntel.composeForFailure
                     (mkTestId "t1") "prop test" narrative
                     [ "f.fs" ] maps bitmaps emptyDepGraph

      (report.BlindSpots.Length + report.CoveredBranches)
      |> Expect.equal "gaps + covered = total" report.TotalBranches

    testCase "CoveragePercent = CoveredBranches / TotalBranches * 100" <| fun _ ->
      let points = [|
        mkSequencePoint "f.fs" 1 1 0
        mkSequencePoint "f.fs" 2 2 1
        mkSequencePoint "f.fs" 3 3 2
        mkSequencePoint "f.fs" 4 4 3
        mkSequencePoint "f.fs" 5 5 4
      |]
      let maps = [| mkInstrumentationMap points |]
      let bitmaps = Map.ofList [ mkTestId "t1", mkBitmap 5 [ 0; 1 ] ]
      let narrative = mkNarrative [ CausalChange.SymbolChanged "x" ] ""

      let report = CoverageIntel.composeForFailure
                     (mkTestId "t1") "prop test" narrative
                     [ "f.fs" ] maps bitmaps emptyDepGraph

      let expected = float report.CoveredBranches / float report.TotalBranches * 100.0
      report.CoveragePercent
      |> Expect.equal "percent matches formula" expected
  ]

// ── FsCheck property tests ────────────────────────────────────

[<Tests>]
let fsCheckPropertyTests = testList "CoverageIntel FsCheck properties" [

  testProperty "compose: result count equals failure count" <| fun (names: string list) ->
    let distinct = names |> List.distinct |> List.truncate 20
    let failures =
      distinct |> List.map (fun n -> mkTestId n, n, mkNarrative [] "")
    let results =
      CoverageIntel.compose failures (fun _ -> []) [||] emptyBitmaps emptyDepGraph
    results |> Expect.hasLength "one report per failure" distinct.Length

  testProperty "classifyVerdict: any percent in [80,100] → WellCovered" <| fun (n: int) ->
    let p = float (abs (n % 21)) + 80.0
    CoverageIntel.classifyVerdict p
    |> Expect.equal "≥80 → WellCovered" WellCovered

  testProperty "classifyVerdict: any percent in [0,39] → DiagnosticBlindSpot" <| fun (n: int) ->
    let p = float (abs (n % 40))
    CoverageIntel.classifyVerdict p
    |> Expect.equal "<40 → DiagnosticBlindSpot" DiagnosticBlindSpot

  testProperty "findCorrelatedTests: result contains no duplicates" <| fun (symbols: string list) ->
    let distinct = symbols |> List.distinct |> List.truncate 10
    let depGraph =
      mkDepGraph [] (distinct |> List.map (fun s -> s, [| mkTestId "other" |]))
    let result = CoverageIntel.findCorrelatedTests distinct depGraph (mkTestId "self")
    result |> List.distinct
    |> Expect.equal "no duplicates in correlated failures" result

  testProperty "compose is deterministic: same inputs → same test names" <| fun (n: int) ->
    let count = abs (n % 10) + 1
    let failures =
      List.init count (fun i ->
        mkTestId (sprintf "t%d" i), sprintf "test %d" i, mkNarrative [] "")
    let run () =
      CoverageIntel.compose failures (fun _ -> []) [||] emptyBitmaps emptyDepGraph
      |> List.map (fun r -> r.TestName)
    run () |> Expect.equal "deterministic across runs" (run ())
]

// ── Tier 1 fallback: SymbolToTests ───────────────────────────

[<Tests>]
let fallbackTierTests =
  testList "CoverageIntel.findCorrelatedTests fallback tiers" [

    testCase "Tier 1: falls back to SymbolToTests when TransitiveCoverage misses" <| fun _ ->
      let symbolToTests = [ "parseExpr", [| mkTestId "t2"; mkTestId "t3" |] ]
      let depGraph = mkDepGraph symbolToTests []
      let result = CoverageIntel.findCorrelatedTests [ "parseExpr" ] depGraph (mkTestId "t1")
      result |> Expect.hasLength "should find 2 from SymbolToTests" 2
      result |> Expect.contains "should have t2" (mkTestId "t2")
      result |> Expect.contains "should have t3" (mkTestId "t3")

    testCase "TransitiveCoverage is preferred over SymbolToTests" <| fun _ ->
      let s2t = [ "foo", [| mkTestId "s1" |] ]
      let tc = [ "foo", [| mkTestId "t1"; mkTestId "t2" |] ]
      let depGraph = mkDepGraph s2t tc
      let result = CoverageIntel.findCorrelatedTests [ "foo" ] depGraph (mkTestId "self")
      result |> Expect.hasLength "should use transitive" 2
      result |> Expect.contains "should have t1" (mkTestId "t1")
      result |> Expect.contains "should have t2" (mkTestId "t2")

    testCase "SymbolToTests excludes the failing test" <| fun _ ->
      let s2t = [ "foo", [| mkTestId "t1"; mkTestId "t2" |] ]
      let depGraph = mkDepGraph s2t []
      CoverageIntel.findCorrelatedTests [ "foo" ] depGraph (mkTestId "t1")
      |> Expect.equal "should only have t2" [ mkTestId "t2" ]

    testCase "symbol in neither map → empty list" <| fun _ ->
      let s2t = [ "other", [| mkTestId "t2" |] ]
      let tc = [ "different", [| mkTestId "t3" |] ]
      let depGraph = mkDepGraph s2t tc
      CoverageIntel.findCorrelatedTests [ "missing" ] depGraph (mkTestId "t1")
      |> Expect.isEmpty "should be empty when symbol not in either map"
  ]

// ── Tier 2 fallback: bitmap proximity coverage ───────────────

[<Tests>]
let bitmapFallbackTests =
  testList "CoverageIntel.findNearbyBitmapCoverage" [

    testCase "finds tests with bitmap coverage within ±10 lines" <| fun _ ->
      let points = [|
        mkSequencePoint "src.fs" 15 15 0
        mkSequencePoint "src.fs" 50 50 1
      |]
      let maps = [| mkInstrumentationMap points |]
      let bitmaps = Map.ofList [
        mkTestId "t1", mkBitmap 2 [ 0 ]
        mkTestId "t2", mkBitmap 2 [ 1 ]
      ]
      let result =
        CoverageIntel.findNearbyBitmapCoverage "src.fs" 20 maps bitmaps (mkTestId "self")
      result |> Expect.hasLength "should find 1 test near line 20" 1
      result |> Expect.contains "should have t1" (mkTestId "t1")

    testCase "line 11 away is not included (outside ±10)" <| fun _ ->
      let points = [|
        mkSequencePoint "src.fs" 31 31 0
      |]
      let maps = [| mkInstrumentationMap points |]
      let bitmaps = Map.ofList [ mkTestId "t1", mkBitmap 1 [ 0 ] ]
      CoverageIntel.findNearbyBitmapCoverage "src.fs" 20 maps bitmaps (mkTestId "self")
      |> Expect.isEmpty "11 lines away should be excluded"

    testCase "line exactly 10 away is included" <| fun _ ->
      let points = [|
        mkSequencePoint "src.fs" 30 30 0
      |]
      let maps = [| mkInstrumentationMap points |]
      let bitmaps = Map.ofList [ mkTestId "t1", mkBitmap 1 [ 0 ] ]
      CoverageIntel.findNearbyBitmapCoverage "src.fs" 20 maps bitmaps (mkTestId "self")
      |> Expect.hasLength "10 lines away is within window" 1

    testCase "excludes the specified test ID" <| fun _ ->
      let points = [| mkSequencePoint "src.fs" 20 20 0 |]
      let maps = [| mkInstrumentationMap points |]
      let bitmaps = Map.ofList [ mkTestId "self", mkBitmap 1 [ 0 ] ]
      CoverageIntel.findNearbyBitmapCoverage "src.fs" 20 maps bitmaps (mkTestId "self")
      |> Expect.isEmpty "should exclude self"

    testCase "multiple tests covering nearby lines all returned" <| fun _ ->
      let points = [|
        mkSequencePoint "src.fs" 18 18 0
        mkSequencePoint "src.fs" 22 22 1
      |]
      let maps = [| mkInstrumentationMap points |]
      let bitmaps = Map.ofList [
        mkTestId "t1", mkBitmap 2 [ 0 ]
        mkTestId "t2", mkBitmap 2 [ 1 ]
        mkTestId "t3", mkBitmap 2 [ 0; 1 ]
      ]
      CoverageIntel.findNearbyBitmapCoverage "src.fs" 20 maps bitmaps (mkTestId "self")
      |> Expect.hasLength "should find all 3 tests" 3

    testCase "no bitmaps → empty" <| fun _ ->
      let points = [| mkSequencePoint "src.fs" 20 20 0 |]
      let maps = [| mkInstrumentationMap points |]
      CoverageIntel.findNearbyBitmapCoverage "src.fs" 20 maps Map.empty (mkTestId "self")
      |> Expect.isEmpty "no bitmaps means no coverage"

    testCase "no nearby sequence points in file → empty" <| fun _ ->
      let points = [| mkSequencePoint "other.fs" 20 20 0 |]
      let maps = [| mkInstrumentationMap points |]
      let bitmaps = Map.ofList [ mkTestId "t1", mkBitmap 1 [ 0 ] ]
      CoverageIntel.findNearbyBitmapCoverage "src.fs" 20 maps bitmaps (mkTestId "self")
      |> Expect.isEmpty "different file should not match"

    testCase "empty maps → empty" <| fun _ ->
      let bitmaps = Map.ofList [ mkTestId "t1", mkBitmap 0 [] ]
      CoverageIntel.findNearbyBitmapCoverage "src.fs" 20 [||] bitmaps (mkTestId "self")
      |> Expect.isEmpty "no maps means no probes"
  ]

// ── Tier 2 wired into composeForFailure ──────────────────────

[<Tests>]
let bitmapFallbackCompositionTests =
  testList "CoverageIntel.composeForFailure bitmap fallback" [

    testCase "bitmap fallback finds correlated tests when graph has no mapping" <| fun _ ->
      let points = [|
        mkSequencePoint "src.fs" 10 10 0
        mkSequencePoint "src.fs" 15 15 1
      |]
      let maps = [| mkInstrumentationMap points |]
      let bitmaps = Map.ofList [
        mkTestId "t1", mkBitmap 2 [ 0; 1 ]
        mkTestId "t2", mkBitmap 2 [ 0 ]
      ]
      let narrative = mkNarrative [ CausalChange.SymbolChanged "unmapped" ] ""

      let report = CoverageIntel.composeForFailure
                     (mkTestId "t1") "my test" narrative
                     [ "src.fs" ] maps bitmaps emptyDepGraph

      report.CorrelatedFailures |> Expect.hasLength "bitmap fallback found t2" 1
      report.CorrelatedFailures |> Expect.contains "should have t2" (mkTestId "t2")

    testCase "graph correlations preferred — bitmap not needed" <| fun _ ->
      let maps = [| mkInstrumentationMap [||] |]
      let depGraph = mkDepGraph [] [
        "foo", [| mkTestId "t1"; mkTestId "t2" |]
      ]
      let narrative = mkNarrative [ CausalChange.SymbolChanged "foo" ] ""

      let report = CoverageIntel.composeForFailure
                     (mkTestId "t1") "my test" narrative
                     [] maps emptyBitmaps depGraph

      report.CorrelatedFailures |> Expect.hasLength "graph found t2" 1
  ]
