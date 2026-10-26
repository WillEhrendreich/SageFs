namespace SageFs.Simulation

open SageFs.Features.LiveTesting

/// Deterministic Simulation Testing for "which session's tests does `GET /api/dependency-graph`
/// answer with".
///
/// Folds the REAL `SymbolTests.ofCycle` over the cycle of the session being asked about, never a
/// reimplemented candidate. The world is a small fixed pool of sessions, each with a cycle of its own
/// holding its own discovered tests and symbols, one of them primary. A scenario is a seed plus an
/// ordered op list that analyses a file of a session (its type-check lands in the session's cycle),
/// runs a session's tests (their coverage lands in the session's cycle), moves the primary and asks.
///
/// The thing under test is that the primary session is NOT the answer. The defect this guards read
/// the primary cycle's graph whoever asked, so a session that was not primary was answered from
/// another session's graph, or with an empty map.
///
/// Same design as `LiveTestFileRoutingSim`:
///   * Chaos is DATA. Same seed, same ops, identical trace, forever. No clock, no IO.
///   * The oracle is INDEPENDENT of the code under test: expected symbols and tests come from the
///     sim's own bookkeeping of which session analysed and ran, never from `ofCycle`.
///   * A TWIN reintroduces the wrong shape (`runPrimaryReader`: every session is answered from the
///     primary's cycle) so the invariants prove they have teeth.
module SymbolTestsRoutingSim =

  [<RequireQualifiedAccess>]
  type SymbolOp =
    /// The session's test file is type-checked: its symbol references land in its cycle.
    | Analyse of sessionIdx: int
    /// The session's tests run: their coverage lands in its cycle.
    | Run of sessionIdx: int
    | MakePrimary of sessionIdx: int
    /// Ask the route about the session.
    | Ask of sessionIdx: int

  type Scenario = { Seed: int; Ops: SymbolOp list }

  /// How far a session had got when it was asked about, from the sim's own bookkeeping.
  [<RequireQualifiedAccess>]
  type Progress =
    | Untouched
    | AnalysedOnly
    | RanOnly
    | AnalysedAndRan

  let poolSize = 4

  let framework = LiveTestingDefaults.Framework

  let fileOf (i: int) = sprintf "/sim/sym/s%d/CalcTests.fs" i

  let testOf (i: int) (k: int) : TestCase =
    let name = sprintf "S%d/test%d" i k
    { Id = TestId.create name framework
      FullName = name
      DisplayName = name
      Origin = TestOrigin.ReflectionOnly
      Labels = []
      Framework = framework
      Category = TestCategory.Unit }

  let testsOf (i: int) : TestCase array = [| testOf i 1; testOf i 2 |]

  /// The two symbols a session's file mentions: one on the line only its first test runs, one on the
  /// line only its second test runs.
  let symbolsOf (i: int) : string array = [| sprintf "Lib%d.f1" i; sprintf "Lib%d.f2" i |]

  /// The tests a session's coverage ties to each of its symbols.
  let expectedTests (i: int) : Map<string, Set<TestId>> =
    let tests = testsOf i
    let symbols = symbolsOf i
    Map.ofList [ symbols.[0], Set.singleton tests.[0].Id; symbols.[1], Set.singleton tests.[1].Id ]

  let sequencePoint (i: int) (line: int) : SequencePoint =
    { File = fileOf i; Line = line; Column = 1; EndLine = line; EndColumn = 40; BranchId = 0 }

  /// Slot 0 runs when the module initializes; slots 1 and 2 are the bodies of the two tests.
  let mapOf (i: int) : InstrumentationMap =
    { InstrumentationMap.empty with
        Slots = [| sequencePoint i 5; sequencePoint i 7; sequencePoint i 9 |]
        TotalProbes = 3
        Source = { StartupSlots = [| 0 |]; Baselines = [||] } }

  let referenceAt (i: int) (symbol: string) (line: int) : SymbolReference =
    { SymbolFullName = symbol
      UseKind = SymbolUseKind.Reference
      UsedInTestId = None
      FilePath = fileOf i
      Line = line }

  let coverageOf (i: int) : Map<TestId, CoverageBitmap> =
    let tests = testsOf i
    Map.ofList
      [ tests.[0].Id, CoverageBitmap.ofBoolArray [| true; true; false |]
        tests.[1].Id, CoverageBitmap.ofBoolArray [| true; false; true |] ]

  let analysisOf (i: int) : FileAnalysisCache =
    let symbols = symbolsOf i
    { FileSymbols = Map.ofList [ fileOf i, [ referenceAt i symbols.[0] 7; referenceAt i symbols.[1] 9 ] ] }

  /// A session's cycle at a given point of progress. Discovery has always happened.
  let cycleAt (i: int) (progress: Progress) : LiveTestCycleState =
    let empty = LiveTestCycleState.empty
    let discovered = { empty.TestState with DiscoveredTests = testsOf i }
    let ran = { discovered with TestCoverageBitmaps = coverageOf i }
    let mapped = Map.ofList [ sprintf "s%d" i, [| mapOf i |] ]
    match progress with
    | Progress.Untouched -> { empty with TestState = discovered }
    | Progress.AnalysedOnly -> { empty with TestState = discovered; AnalysisCache = analysisOf i }
    | Progress.RanOnly -> { empty with TestState = ran; InstrumentationMaps = mapped }
    | Progress.AnalysedAndRan ->
      { empty with TestState = ran; InstrumentationMaps = mapped; AnalysisCache = analysisOf i }

  /// A router: given who is primary, every session's cycle, and who is asked, what is the answer?
  type Router = int -> (int -> LiveTestCycleState) -> int -> SymbolTestMap

  /// The subject under test, composed as `DependencyGraphRoute.read` composes it: the ASKED
  /// session's own cycle.
  let real : Router = fun _ cycles asked -> SymbolTests.ofCycle (cycles asked)

  /// TWIN: the original defect. Every session is answered from the primary's cycle.
  let primaryReader : Router = fun primary cycles _ -> SymbolTests.ofCycle (cycles primary)

  /// One ask's outcome, with the expectation the sim recorded from its own bookkeeping.
  type AskDecision =
    { Asked: int
      Primary: int
      Progress: Progress
      Got: SymbolTestMap }

  type SimState =
    { Primary: int
      Analysed: Set<int>
      Ran: Set<int>
      /// Most recent first while folding; reversed to op order in the `Trace`.
      Decisions: AskDecision list }

  let initial : SimState =
    { Primary = 0; Analysed = Set.empty; Ran = Set.empty; Decisions = [] }

  let progressOf (state: SimState) (i: int) : Progress =
    match state.Analysed.Contains i, state.Ran.Contains i with
    | false, false -> Progress.Untouched
    | true, false -> Progress.AnalysedOnly
    | false, true -> Progress.RanOnly
    | true, true -> Progress.AnalysedAndRan

  let rec go (router: Router) (state: SimState) (ops: SymbolOp list) : SimState =
    match ops with
    | [] -> state
    | op :: rest ->
      let state' =
        match op with
        | SymbolOp.Analyse i -> { state with Analysed = state.Analysed.Add i }
        | SymbolOp.Run i -> { state with Ran = state.Ran.Add i }
        | SymbolOp.MakePrimary i -> { state with Primary = i }
        | SymbolOp.Ask i ->
          let decision =
            { Asked = i
              Primary = state.Primary
              Progress = progressOf state i
              Got = router state.Primary (fun j -> cycleAt j (progressOf state j)) i }
          { state with Decisions = decision :: state.Decisions }
      go router state' rest

  type Trace = { Scenario: Scenario; Final: SimState; Router: string }

  let runWith (name: string) (router: Router) (scenario: Scenario) : Trace =
    let final = go router initial scenario.Ops
    { Scenario = scenario; Final = { final with Decisions = List.rev final.Decisions }; Router = name }

  /// Through the real `SymbolTests.ofCycle` over the asked session's cycle.
  let run (scenario: Scenario) : Trace = runWith "real (SymbolTests.ofCycle over the asked session's cycle)" real scenario

  /// Through the primary-reader twin: proves the invariants catch the original defect.
  let runPrimaryReader (scenario: Scenario) : Trace = runWith "twin-primary-reader" primaryReader scenario
