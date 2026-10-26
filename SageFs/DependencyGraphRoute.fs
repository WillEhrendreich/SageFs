namespace SageFs

open SageFs.Features.LiveTesting

/// What `GET /api/dependency-graph` says: the tests that cover a symbol, for ONE session.
///
/// The route used to read `model.LiveTesting.DepGraph`, the primary cycle's name graph, whoever
/// asked. A session that was not primary was answered from another session's graph, and a session
/// whose tests the name graph cannot name (every Expecto `testCase` lambda) was answered with an
/// empty map. The answer now comes from the asked session's own cycle (`SageFsModel.cycleForSession`,
/// which falls back to an EMPTY cycle and never to the primary) through `SymbolTests.ofCycle`, and
/// it says in `State` whether an empty map will fill.
///
/// Pure: model in, JSON text out. The route resolves WHICH session is asked with the same rule as
/// `/api/live-testing/status` and passes the id in.
module DependencyGraphRoute =

  /// The session's own symbol-to-tests map, with the cycle it was read from.
  let read (sessionId: string) (model: SageFsModel) : LiveTestCycleState * SymbolTestMap =
    let cycle = SageFsModel.cycleForSession sessionId model
    cycle, SymbolTests.ofCycle cycle

  /// Every symbol with the number of tests covering it.
  let overview (sessionId: string) (model: SageFsModel) : string =
    let _, map = read sessionId model
    let symbols =
      map.SymbolToTests
      |> Map.toArray
      |> Array.map (fun (symbol, tests) -> {| Symbol = symbol; TestCount = tests.Length |})
    Json.serialize Json.standard
      {| Session = sessionId
         State = SymbolTestsState.toWire map.State
         Symbols = symbols
         TotalSymbols = symbols.Length |}

  /// The tests covering one symbol.
  let forSymbol (sessionId: string) (model: SageFsModel) (symbol: string) : string =
    let cycle, map = read sessionId model
    let tests =
      SymbolTests.testsFor cycle map symbol
      |> Array.map (fun row ->
        {| TestId = TestId.value row.TestId
           TestName = row.TestName
           Status = SymbolTestStatus.toWire row.Status |})
    Json.serialize Json.standard
      {| Session = sessionId
         State = SymbolTestsState.toWire map.State
         Symbol = symbol
         Tests = tests
         TotalSymbols = map.SymbolToTests.Count |}
