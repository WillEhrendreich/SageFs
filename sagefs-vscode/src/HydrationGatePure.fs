/// Which answer to a list fetch may still be delivered.
///
/// A `test_results_batch` frame carries a tally and no list, so the editor fetches the list and delivers the
/// same frame again with it. After a reconnect the stream can replay several such frames, each firing its own
/// fetch, and the answers land in any order: the last to land decided the generation, freshness and
/// completion the panel showed, even when it belonged to an older frame.
///
/// Each frame that owes a fetch takes a ticket for its session, and only the newest ticket's answer may
/// deliver. A newer frame has its own fetch coming, so dropping the older answer loses nothing.
///
/// No Fable dependency; tested under `dotnet fsi` (tests/HydrationGateContractTests.fsx).
module SageFs.Vscode.HydrationGatePure

/// The newest ticket taken for each session.
type Gate = Map<string, int>

let empty : Gate = Map.empty

/// Take the next ticket for a session. Tickets only grow.
let take (sessionId: string) (gate: Gate) : int * Gate =
  let ticket = (Map.tryFind sessionId gate |> Option.defaultValue 0) + 1
  ticket, Map.add sessionId ticket gate

/// Is this still the newest ticket taken for the session?
let isCurrent (sessionId: string) (ticket: int) (gate: Gate) : bool =
  Map.tryFind sessionId gate = Some ticket
