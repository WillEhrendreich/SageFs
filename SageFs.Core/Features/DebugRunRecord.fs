namespace SageFs.Features.LiveTesting

open System

// A test run under a debugger is a real run of the code, so its verdict belongs on the test's row, where every client reads
// it, without a normal run. This is the pure half of recording it: what the record is, which results are verdicts, and what
// the state looks like after one lands. The reducer arm in SageFsApp.fs applies the plan and refreshes the cached views.
//
// What a debug verdict deliberately does not do:
//   * It is not a flake sample. A test stopped at a breakpoint for ten minutes says nothing about stability.
//   * It is not a line in the run summary, so no "N passed" appears later under an unrelated run.
//   * It is not a discovery, so no client sweeps its rows on it.
//   * It carries no duration. The host's clock ran through every breakpoint, so the number would be a lie either way.

/// One debug run that finished with a verdict.
type DebugRunRecord =
  { /// The session whose host held the test. The record lands in that session's cycle and no other. (The field names are
    /// this record's own: `Session`, `Ticket` and `Result` are fields of records the daemon builds all over, and a record
    /// expression takes the latest type that has the name.)
    OwnerSession: string
    /// The ticket the host issued for the hold. A ticket records once.
    HoldTicket: string
    Verdict: TestRunResult
    /// Compiled when the test's code had a PDB, Evaluated when an eval in the session defined it.
    CodeBasis: ResultProvenance }

/// The tickets already recorded for a session, newest first. A late continue after the hold ended still answers, so the
/// daemon sees the same ended hold more than once. Bounded: the host holds one test at a time, so a ticket old enough to
/// fall off the end can no longer be asked about.
type DebugTicketLog = private DebugTicketLog of recorded: int * tickets: string list

module DebugTicketLog =
  /// How many tickets are remembered.
  [<Literal>]
  let capacity = 64

  let empty = DebugTicketLog (0, [])

  let contains (ticket: string) (DebugTicketLog (_, tickets)) : bool = List.contains ticket tickets

  /// The ticket, newest first, with the oldest dropped past the capacity.
  let add (ticket: string) (DebugTicketLog (recorded, tickets)) : DebugTicketLog =
    DebugTicketLog (recorded + 1, ticket :: tickets |> List.truncate capacity)

  let toList (DebugTicketLog (_, tickets)) : string list = tickets

  /// How many debug runs have ever been recorded. It only goes up, which is what lets a change that moves no generation
  /// and no count still be told from the state before it.
  let recorded (DebugTicketLog (count, _)) : int = count

module DebugRunRecord =

  /// The verdict without its duration.
  let withoutDuration (result: TestResult) : TestResult =
    match result with
    | TestResult.Passed _ -> TestResult.Passed TimeSpan.Zero
    | TestResult.Failed (failure, _) -> TestResult.Failed (failure, TimeSpan.Zero)
    | other -> other

  /// The record for what the host reported, or `None` when the host did not give a verdict (it does not know the test, or
  /// the run ended before it reported). Nothing is fabricated over a row.
  let create
    (session: string)
    (ticket: string)
    (testId: TestId)
    (testName: string)
    (result: TestResult)
    (basis: ResultProvenance)
    (at: DateTimeOffset)
    : DebugRunRecord option =
    match result with
    | TestResult.NotRun
    | TestResult.NoResult _ -> None
    | TestResult.Passed _
    | TestResult.Failed _
    | TestResult.Skipped _ ->
      Some
        { OwnerSession = session
          HoldTicket = ticket
          Verdict =
            { TestId = testId
              TestName = testName
              Result = withoutDuration result
              Timestamp = at
              Output = None }
          CodeBasis = basis }

  /// What recording does to the state: the ids whose rows change, the change to the test state, and the log with the ticket
  /// in it. `None` when the ticket is already recorded.
  let plan
    (record: DebugRunRecord)
    (log: DebugTicketLog)
    : (Set<TestId> * (LiveTestState -> LiveTestState) * DebugTicketLog) option =
    match DebugTicketLog.contains record.HoldTicket log with
    | true -> None
    | false ->
      let id = record.Verdict.TestId
      let update (state: LiveTestState) : LiveTestState =
        { state with
            LastResults = Map.add id record.Verdict state.LastResults
            Provenances = Map.add id record.CodeBasis state.Provenances }
      Some (Set.singleton id, update, DebugTicketLog.add record.HoldTicket log)
