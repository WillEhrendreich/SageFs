/// What "SageFs: Show Tests Covering a Symbol" says when the daemon's answer has no symbols.
///
/// `GET /api/dependency-graph?session=<id>` names, in `State`, why a map is the size it is: nothing
/// analysed yet (the next type-check fills it), files analysed with no coverage recorded (a test run
/// fills it), coverage of another build (a run against the built assembly fills it), or coverage
/// recorded with no symbol reached (it will not fill by itself). The command
/// used to say "No dependency graph available yet" for all three.
///
/// No Fable dependency; tested under `dotnet fsi` (tests/SymbolTestsContractTests.fsx).
module SageFs.Vscode.SymbolTestsPure

/// The daemon's reason a symbol-to-tests map is the size it is.
type SymbolTestsState =
  | NothingAnalysed
  | NoCoverageRecorded
  | CoverageOfAnotherBuild
  | NothingReached
  | Mapped
  /// A reply that states no reason, or one this extension does not know.
  | StateNotStated

let parseState (wire: string) : SymbolTestsState =
  match wire with
  | "nothing_analysed" -> NothingAnalysed
  | "no_coverage_recorded" -> NoCoverageRecorded
  | "coverage_of_another_build" -> CoverageOfAnotherBuild
  | "nothing_reached" -> NothingReached
  | "mapped" -> Mapped
  | _ -> StateNotStated

/// The sentence shown when the map has no symbols.
let emptyMessage (state: SymbolTestsState) : string =
  match state with
  | NothingAnalysed ->
    "No test file has been analysed in this session yet. Edit or save a file with live testing on, then ask again."
  | NoCoverageRecorded ->
    "Files are analysed but no test has recorded coverage yet. Run the tests, then ask again."
  | CoverageOfAnotherBuild ->
    "Coverage was recorded against a different build than the one this session holds. Run the tests against the built assembly, then ask again."
  | NothingReached ->
    "Coverage is recorded and no test reaches an analysed symbol."
  | Mapped
  | StateNotStated ->
    "No symbol-to-tests map is available for this session yet."
