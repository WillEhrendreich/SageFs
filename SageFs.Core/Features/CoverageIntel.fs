module SageFs.Features.CoverageIntel

open SageFs.Features.LiveTesting

/// How well-covered is the code related to a test failure?
type CoverageVerdict =
  /// ≥80% branch coverage — failure is in tested code
  | WellCovered
  /// 40–79% — some gaps near the failure site
  | PartialBlindSpot
  /// <40% — "write tests before debugging"
  | DiagnosticBlindSpot

/// An uncovered branch in code related to a failing test.
type BranchGap = {
  FilePath: string
  Line: int
  EndLine: int
  BranchId: int
  NearestCoveredLine: int option
}

/// Coverage intelligence for a single test failure.
type CoverageIntelReport = {
  TestId: TestId
  TestName: string
  CausalSymbols: string list
  CoveredBranches: int
  TotalBranches: int
  CoveragePercent: float
  BlindSpots: BranchGap list
  CorrelatedFailures: TestId list
  Verdict: CoverageVerdict
}

module CoverageIntelReport =
  let empty testId testName = {
    TestId = testId
    TestName = testName
    CausalSymbols = []
    CoveredBranches = 0
    TotalBranches = 0
    CoveragePercent = 0.0
    BlindSpots = []
    CorrelatedFailures = []
    Verdict = DiagnosticBlindSpot
  }

/// Pure composition: joins FailureNarrative + InstrumentationMaps + CoverageBitmaps
/// + TestDependencyGraph into coverage intelligence per failure.
module CoverageIntel =

  /// Classify coverage percentage into a verdict.
  let classifyVerdict (percent: float) : CoverageVerdict =
    match percent with
    | p when p >= 80.0 -> WellCovered
    | p when p >= 40.0 -> PartialBlindSpot
    | _ -> DiagnosticBlindSpot

  /// Extract causal symbol names from a FailureNarrative.
  let extractCausalSymbols (narrative: FailureNarrative) : string list =
    narrative.CausalChanges
    |> List.choose (fun change ->
      match change with
      | CausalChange.SymbolChanged symbol -> Some symbol
      | _ -> None)

  /// Find sequence points in instrumentation maps that belong to files
  /// where causal symbols live, filtering to a specific file.
  let private findCausalSequencePoints
    (filePath: string)
    (maps: InstrumentationMap array)
    : SequencePoint array =
    maps
    |> Array.collect (fun m ->
      m.Slots
      |> Array.filter (fun sp -> sp.File = filePath))

  /// Compute which sequence points are covered by ANY test's bitmap.
  let private computeCoveredPoints
    (maps: InstrumentationMap array)
    (bitmaps: Map<TestId, CoverageBitmap>)
    (points: SequencePoint array)
    : Set<int> =
    // Build a global slot index → whether hit by any test
    let bmValues = bitmaps |> Map.values |> Seq.toArray
    let allHits =
      match bmValues with
      | [||] -> CoverageBitmap.empty
      | _ ->
        bmValues
        |> Array.reduce (fun acc bm ->
          match acc.Count = bm.Count with
          | true -> CoverageBitmap.union acc bm
          | false -> acc) // Skip mismatched bitmaps
    // For each map, check which of our target points are covered
    let mutable covered = Set.empty
    let mutable globalOffset = 0
    for m in maps do
      for sp in points do
        // Find this sp's index within this map
        m.Slots
        |> Array.tryFindIndex (fun s ->
          s.File = sp.File && s.Line = sp.Line && s.BranchId = sp.BranchId)
        |> Option.iter (fun localIdx ->
          let globalIdx = globalOffset + localIdx
          match CoverageBitmap.isSet globalIdx allHits with
          | true -> covered <- covered |> Set.add sp.BranchId
          | false -> ())
      globalOffset <- globalOffset + m.TotalProbes
    covered

  /// Find the nearest covered line to a given uncovered line.
  let private findNearestCoveredLine
    (uncoveredLine: int)
    (coveredLines: int list)
    : int option =
    match coveredLines with
    | [] -> None
    | lines ->
      lines
      |> List.minBy (fun l -> abs (l - uncoveredLine))
      |> Some

  /// Identify blind spots (uncovered branches) in causal code.
  let findBlindSpots
    (filePath: string)
    (maps: InstrumentationMap array)
    (bitmaps: Map<TestId, CoverageBitmap>)
    : BranchGap list =
    let points = findCausalSequencePoints filePath maps
    match points with
    | [||] -> []
    | _ ->
      let coveredBranchIds = computeCoveredPoints maps bitmaps points
      let coveredLines =
        points
        |> Array.filter (fun sp -> coveredBranchIds |> Set.contains sp.BranchId)
        |> Array.map (fun sp -> sp.Line)
        |> Array.distinct
        |> Array.toList
      points
      |> Array.filter (fun sp -> coveredBranchIds |> Set.contains sp.BranchId |> not)
      |> Array.map (fun sp -> {
        FilePath = sp.File
        Line = sp.Line
        EndLine = sp.EndLine
        BranchId = sp.BranchId
        NearestCoveredLine = findNearestCoveredLine sp.Line coveredLines
      })
      |> Array.toList

  /// Find tests that also cover the same blind-spot regions (correlated failures).
  let findCorrelatedTests
    (causalSymbols: string list)
    (depGraph: TestDependencyGraph)
    (excludeTestId: TestId)
    : TestId list =
    causalSymbols
    |> List.collect (fun symbol ->
      match depGraph.TransitiveCoverage |> Map.tryFind symbol with
      | Some testIds ->
        testIds
        |> Array.filter (fun tid -> tid <> excludeTestId)
        |> Array.toList
      | None -> [])
    |> List.distinct

  /// Compose a CoverageIntelReport for a single test failure.
  let composeForFailure
    (testId: TestId)
    (testName: string)
    (narrative: FailureNarrative)
    (causalFiles: string list)
    (maps: InstrumentationMap array)
    (bitmaps: Map<TestId, CoverageBitmap>)
    (depGraph: TestDependencyGraph)
    : CoverageIntelReport =
    let causalSymbols = extractCausalSymbols narrative

    let allBlindSpots =
      causalFiles
      |> List.collect (fun fp -> findBlindSpots fp maps bitmaps)

    let allPoints =
      causalFiles
      |> List.sumBy (fun fp ->
        findCausalSequencePoints fp maps |> Array.length)

    let coveredCount = allPoints - allBlindSpots.Length
    let percent =
      match allPoints with
      | 0 -> 0.0
      | total -> float coveredCount / float total * 100.0

    let correlated = findCorrelatedTests causalSymbols depGraph testId

    {
      TestId = testId
      TestName = testName
      CausalSymbols = causalSymbols
      CoveredBranches = coveredCount
      TotalBranches = allPoints
      CoveragePercent = percent
      BlindSpots = allBlindSpots
      CorrelatedFailures = correlated
      Verdict = classifyVerdict percent
    }

  /// Compose coverage intelligence for multiple test failures.
  let compose
    (failures: (TestId * string * FailureNarrative) list)
    (causalFileResolver: string list -> string list)
    (maps: InstrumentationMap array)
    (bitmaps: Map<TestId, CoverageBitmap>)
    (depGraph: TestDependencyGraph)
    : CoverageIntelReport list =
    failures
    |> List.map (fun (testId, testName, narrative) ->
      let causalSymbols = extractCausalSymbols narrative
      let causalFiles = causalFileResolver causalSymbols
      composeForFailure testId testName narrative causalFiles maps bitmaps depGraph)

  /// Summarize a CoverageIntelReport as human-readable text.
  let summarize (report: CoverageIntelReport) : string =
    let icon =
      match report.Verdict with
      | WellCovered -> "✅"
      | PartialBlindSpot -> "⚠️"
      | DiagnosticBlindSpot -> "🔴"
    let header = sprintf "%s %s — %.0f%% branch coverage (%d/%d)"
                   icon report.TestName report.CoveragePercent
                   report.CoveredBranches report.TotalBranches
    let lines = ResizeArray<string>()
    lines.Add header
    match report.Verdict with
    | DiagnosticBlindSpot ->
      lines.Add "  ⚡ Write tests before debugging — related code is largely untested"
    | PartialBlindSpot ->
      lines.Add(sprintf "  ⚡ %d uncovered branch%s near failure site"
        report.BlindSpots.Length
        (match report.BlindSpots.Length with 1 -> "" | _ -> "es"))
    | WellCovered ->
      lines.Add "  Failure is in well-tested code — focus on logic, not coverage"
    match report.CorrelatedFailures with
    | [] -> ()
    | corr ->
      lines.Add(sprintf "  🔗 %d other test%s cover the same code"
        corr.Length (match corr.Length with 1 -> "" | _ -> "s"))
    match report.BlindSpots |> List.truncate 3 with
    | [] -> ()
    | gaps ->
      gaps |> List.iter (fun g ->
        lines.Add(sprintf "  📍 Uncovered: %s:%d (branch %d)" g.FilePath g.Line g.BranchId))
    lines |> String.concat "\n"
