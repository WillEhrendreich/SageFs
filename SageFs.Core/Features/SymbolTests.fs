/// "Tests covering a symbol": the answer `GET /api/dependency-graph` gives, for ONE session.
///
/// The route used to read `TestDependencyGraph.SymbolToTests` of whichever cycle was primary. Two
/// things made that answer empty or wrong:
///
///   * The name graph decides what a test is by looking for `.Tests.` in a DEFINITION's full name,
///     and gives the test the id of that definition. An Expecto test is a `testCase "name" <| fun _`
///     lambda inside one `[<Tests>]` value, so the id it invents (the value's name) is never the id
///     discovery gave a test, and a module that is not called `*.Tests.*` yields no test at all.
///   * The route read the primary cycle, so a session that was not primary asked about its own
///     tests and was answered from another session's graph.
///
/// This module answers from one cycle (the caller picks the session's own) with two sources, both
/// naming only DISCOVERED tests:
///
///   * the name graph, with every id discovery does not know dropped;
///   * recorded coverage: a symbol that appears on a line of an analysed file is covered by the
///     tests whose own coverage reached that line. This needs no naming convention, only the type
///     check that produced the symbol's references and a test run that recorded coverage.
///
/// Slots that run while a module initializes are left out: they run once per process in whichever
/// test got there first, so no test's coverage can vouch for them.
namespace SageFs.Features.LiveTesting

/// Why the map is the size it is. A map that is empty because nothing has been analysed yet will
/// fill after the next type-check; one that is empty with coverage recorded will not.
[<RequireQualifiedAccess>]
type SymbolTestsState =
  /// No file of this session has been type-checked, and the name graph is empty.
  | NothingAnalysed
  /// Files are analysed and no test has recorded coverage at all.
  | NoCoverageRecorded
  /// Files are analysed and tests have coverage, but none of it matches the instrumentation the
  /// session holds now (another build's, or the session holds no instrumentation). A run against the
  /// built assembly records coverage that does.
  | CoverageOfAnotherBuild
  /// Files are analysed and coverage is recorded, and no symbol is reached by any test.
  | NothingReached
  | Mapped

module SymbolTestsState =
  let toWire (state: SymbolTestsState) : string =
    match state with
    | SymbolTestsState.NothingAnalysed -> "nothing_analysed"
    | SymbolTestsState.NoCoverageRecorded -> "no_coverage_recorded"
    | SymbolTestsState.CoverageOfAnotherBuild -> "coverage_of_another_build"
    | SymbolTestsState.NothingReached -> "nothing_reached"
    | SymbolTestsState.Mapped -> "mapped"

/// What the last run said about one test.
[<RequireQualifiedAccess>]
type SymbolTestStatus =
  | Passed
  | Failed
  | Other
  /// No result is recorded for the test.
  | Unknown

module SymbolTestStatus =
  let toWire (status: SymbolTestStatus) : string =
    match status with
    | SymbolTestStatus.Passed -> "passed"
    | SymbolTestStatus.Failed -> "failed"
    | SymbolTestStatus.Other -> "other"
    | SymbolTestStatus.Unknown -> "unknown"

type SymbolTestRow =
  { TestId: TestId
    TestName: string
    Status: SymbolTestStatus }

type SymbolTestMap =
  { State: SymbolTestsState
    SymbolToTests: Map<string, TestId array> }

module SymbolTests =

  /// A symbol worth listing: qualified, and not the F# core library's operators and types.
  let listable (name: string) : bool =
    name.Contains "." && not (name.StartsWith "Microsoft.FSharp")

  let nameIndex (discovered: Set<TestId>) (graph: TestDependencyGraph) : Map<string, TestId array> =
    graph.SymbolToTests
    |> Map.map (fun _ ids -> ids |> Array.filter discovered.Contains)
    |> Map.filter (fun _ ids -> not (Array.isEmpty ids))

  /// Whether the line numbers of `file`'s references can be read against recorded coverage. The
  /// references of the file being edited come from the buffer; coverage lines come from the text
  /// that was compiled. They are the same lines only when the buffer has the compiled text's shape.
  let linesAgree (cycle: LiveTestCycleState) (merged: InstrumentationMap) (file: string) : bool =
    match cycle.ActiveFile, cycle.LatestContent with
    | Some active, Some content when active = file ->
      match merged.Source.Baselines |> Array.tryFind (fun b -> b.File = file) with
      | None -> false
      | Some baseline ->
        match LineEdit.between baseline.Lines content with
        | ChangedLines.InPlace _ -> true
        | ChangedLines.Shifted -> false
        | ChangedLines.NoBaseline -> false
    | _ -> true

  /// For one file, a function from a line to the tests whose own coverage reached that line.
  let reachOfFile
    (merged: InstrumentationMap)
    (startup: Set<int>)
    (tests: (TestId * CoverageBitmap) array)
    (file: string)
    : int -> TestId array =
    let slots =
      [| for i in 0 .. merged.Slots.Length - 1 do
           match merged.Slots.[i].File = file && not (startup.Contains i) with
           | true -> yield i
           | false -> () |]
    let memo = System.Collections.Generic.Dictionary<int, TestId array>()
    fun line ->
      match memo.TryGetValue line with
      | true, found -> found
      | false, _ ->
        let atLine =
          slots
          |> Array.filter (fun i ->
            let sp = merged.Slots.[i]
            sp.Line <= line && line <= max sp.Line sp.EndLine)
        let found =
          tests
          |> Array.filter (fun (_, bitmap) -> atLine |> Array.exists (fun i -> CoverageBitmap.isSet i bitmap))
          |> Array.map fst
        memo.[line] <- found
        found

  /// The tests of this cycle that recorded coverage against the current instrumentation.
  let usableBitmaps (cycle: LiveTestCycleState) (merged: InstrumentationMap) (discovered: TestCase array) =
    match merged.TotalProbes with
    | 0 -> [||]
    | total ->
      discovered
      |> Array.choose (fun tc ->
        match Map.tryFind tc.Id cycle.TestState.TestCoverageBitmaps with
        | Some bitmap when bitmap.Count = total -> Some (tc.Id, bitmap)
        | Some _ -> None
        | None -> None)

  let coverageIndex
    (cycle: LiveTestCycleState)
    (merged: InstrumentationMap)
    (tests: (TestId * CoverageBitmap) array)
    : Map<string, Set<TestId>> =
    let startup = Set.ofArray merged.Source.StartupSlots
    cycle.AnalysisCache.FileSymbols
    |> Map.toSeq
    |> Seq.filter (fun (file, _) -> linesAgree cycle merged file)
    |> Seq.collect (fun (file, refs) ->
      let reach = reachOfFile merged startup tests file
      refs
      |> Seq.filter (fun r -> listable r.SymbolFullName)
      |> Seq.map (fun r -> r.SymbolFullName, reach r.Line))
    |> Seq.fold
      (fun (acc: Map<string, Set<TestId>>) (symbol, reached) ->
        match Array.isEmpty reached with
        | true -> acc
        | false ->
          let known = acc |> Map.tryFind symbol |> Option.defaultValue Set.empty
          acc |> Map.add symbol (Set.union known (Set.ofArray reached)))
      Map.empty

  /// The symbol-to-tests map of one session's cycle.
  let ofCycle (cycle: LiveTestCycleState) : SymbolTestMap =
    let discovered = cycle.TestState.DiscoveredTests
    let discoveredIds = discovered |> Array.map (fun tc -> tc.Id)
    let discoveredSet = Set.ofArray discoveredIds
    let order = discoveredIds |> Array.mapi (fun i id -> id, i) |> Map.ofArray
    let merged = cycle.InstrumentationMaps |> Map.values |> Seq.collect id |> Array.ofSeq |> InstrumentationMap.merge
    let bitmaps = usableBitmaps cycle merged discovered
    let byName = nameIndex discoveredSet cycle.DepGraph
    let byCoverage = coverageIndex cycle merged bitmaps
    let symbols = Set.union (byName |> Map.keys |> Set.ofSeq) (byCoverage |> Map.keys |> Set.ofSeq)
    let combined =
      symbols
      |> Seq.map (fun symbol ->
        let named = byName |> Map.tryFind symbol |> Option.defaultValue [||] |> Set.ofArray
        let covered = byCoverage |> Map.tryFind symbol |> Option.defaultValue Set.empty
        symbol, Set.union named covered |> Set.toArray |> Array.sortBy (fun id -> Map.find id order))
      |> Map.ofSeq
    let state =
      match Map.isEmpty combined with
      | false -> SymbolTestsState.Mapped
      | true ->
        match Map.isEmpty cycle.AnalysisCache.FileSymbols && Map.isEmpty cycle.DepGraph.SymbolToTests with
        | true -> SymbolTestsState.NothingAnalysed
        | false ->
          let recorded = discovered |> Array.exists (fun tc -> cycle.TestState.TestCoverageBitmaps.ContainsKey tc.Id)
          match Array.isEmpty bitmaps, recorded with
          | false, _ -> SymbolTestsState.NothingReached
          | true, true -> SymbolTestsState.CoverageOfAnotherBuild
          | true, false -> SymbolTestsState.NoCoverageRecorded
    { State = state; SymbolToTests = combined }

  /// The tests that cover one symbol, each with its name and what the last run said about it.
  let testsFor (cycle: LiveTestCycleState) (map: SymbolTestMap) (symbol: string) : SymbolTestRow array =
    let names = cycle.TestState.DiscoveredTests |> Array.map (fun tc -> tc.Id, tc.FullName) |> Map.ofArray
    let results = cycle.TestState.LastResults
    map.SymbolToTests
    |> Map.tryFind symbol
    |> Option.defaultValue [||]
    |> Array.map (fun id ->
      let status, name =
        match Map.tryFind id results with
        | Some r ->
          let status =
            match r.Result with
            | TestResult.Passed _ -> SymbolTestStatus.Passed
            | TestResult.Failed _ -> SymbolTestStatus.Failed
            | TestResult.Skipped _ -> SymbolTestStatus.Other
            | TestResult.NotRun -> SymbolTestStatus.Other
            | TestResult.NoResult _ -> SymbolTestStatus.Other
          status, r.TestName
        | None -> SymbolTestStatus.Unknown, Map.tryFind id names |> Option.defaultValue (TestId.value id)
      { TestId = id; TestName = name; Status = status })
