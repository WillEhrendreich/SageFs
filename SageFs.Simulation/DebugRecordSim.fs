namespace SageFs.Simulation

open System
open SageFs
open SageFs.DebugTestRequest
open SageFs.Features.LiveTesting
open SageFs.HostAgent.TestDebug

/// Deterministic Simulation Testing for the daemon's half of debugging a test: what happens to a verdict between the host
/// ending a hold and a row changing. The world has two sessions, each with its own host (the REAL `TestDebug.step` slot) and
/// its own ticket log (what the cycle keeps), and three tests. Events: an editor begins a hold on a session, releases the
/// latest one with or without a debugger, asks again about a ticket it already asked about (any session's), lets an expiry
/// fire, or the host of a session ends. Every continue goes through the daemon's own steps: `HeldTable`,
/// `DebugTestRequest.verdictFor` and `DebugRunRecord.plan`, the real code, folded over a seeded list of events.
///
/// Three invariants say what must hold whatever the order: a verdict lands only for a hold that ended attached, a ticket is
/// recorded at most once, and a verdict lands in the session whose host held the test. Three twins break one each (record on
/// every continue, record a failure for a hold that never ran, resolve the session by the daemon's default), and each must
/// FAIL the invariant built to catch it.
module DebugRecordSim =

  [<RequireQualifiedAccess>]
  type SimEvent =
    /// The editor asks session `session` to hold test `test`.
    | Begin of session: int * test: int
    /// The editor releases the session's latest ticket, with a debugger attached or not. An attached release runs the test
    /// to its end at once; a release with none ends the hold at once without running it.
    | Release of session: int * attached: bool
    /// The editor asks again about one of the tickets issued so far, in whichever session issued it.
    | Ask of pick: int
    /// The session's latest hold expires.
    | Expire of session: int
    /// The session's host ends.
    | HostEnds of session: int

  [<RequireQualifiedAccess>]
  type Behavior =
    | Real
    /// Every answer that ended a hold records, whether or not that ticket was recorded before.
    | RecordsEveryContinueTwin
    /// A hold that ended without running is recorded as a failed test.
    | FabricatesFailureTwin
    /// The verdict goes to the daemon's default session whatever session held the test.
    | DefaultSessionTwin

  /// One verdict that reached a session's row.
  type Landed =
    { Session: int
      Ticket: string
      TestId: TestId }

  type State =
    { Holds: Map<int, Hold>
      /// Per session, the tickets it issued, oldest first.
      Issued: Map<int, string list>
      /// Which session began each ticket.
      Began: Map<string, int>
      /// Every hold the host ended, with how: what the host knows, which the daemon's answer must agree with.
      HostEnded: (int * string * DebugEnd) list
      Table: HeldTable
      Logs: Map<int, DebugTicketLog>
      Landed: Landed list }

  let sessions = [ 0; 1 ]

  let sessionText (session: int) : string = sprintf "sim-session-%d" session

  /// The session the daemon falls back on when a request names none.
  let defaultSession = 0

  let bound = TimeSpan.FromTicks 1L

  let private testOf (index: int) : TestCase =
    let name = sprintf "sim.test.%d" (abs index % 3)
    { Id = TestId.create name TestFramework.Expecto
      FullName = name
      DisplayName = name
      NamePath = []
      Origin = TestOrigin.ReflectionOnly
      Labels = []
      Framework = TestFramework.Expecto
      Category = TestCategory.Unit }

  let private passedResult : TestResult = TestResult.Passed(TimeSpan.FromTicks 1L)
  let private failedResult : TestResult = TestResult.Failed(TestFailure.AssertionFailed "sim failure", TimeSpan.FromTicks 1L)

  let initial : State =
    { Holds = sessions |> List.map (fun s -> s, Hold.Idle) |> Map.ofList
      Issued = sessions |> List.map (fun s -> s, []) |> Map.ofList
      Began = Map.empty
      HostEnded = []
      Table = HeldTable.empty
      Logs = sessions |> List.map (fun s -> s, DebugTicketLog.empty) |> Map.ofList
      Landed = [] }

  let private target (session: int) (ticket: string) (test: TestCase) (symbols: SymbolSupport) : DebugTarget =
    { Pid = 4000 + session
      Ticket = DebugTicket ticket
      TestId = test.Id
      TestName = test.FullName
      Symbols = symbols
      Access = AttachAccess.Open
      HoldFor = TimeSpan.FromTicks 1L }

  /// Apply one reducer event to a session's host, and record how a hold ended if this event ended one.
  let private hostStep (session: int) (s: State) (event: HoldEvent) : State * HoldEffect =
    let before = s.Holds.[session]
    let after, effect = step bound before event
    let ended =
      match before, after with
      | (Hold.Holding(t, _) | Hold.Awaiting(t, _) | Hold.Running t), Hold.Ended(ticket, how) when t = ticket ->
        let (DebugTicket text) = ticket
        [ session, text, how ]
      | _ -> []
    { s with Holds = Map.add session after s.Holds; HostEnded = s.HostEnded @ ended }, effect

  let private latestTicket (session: int) (s: State) : string =
    match List.tryLast s.Issued.[session] with
    | Some ticket -> ticket
    | None -> "sim-never-issued"

  /// The result a record carries for a hold that did not run, in the twin that fabricates one.
  let private fabricated (ended: DebugEnd) : DebugEnd =
    match ended with
    | DebugEnd.Attached _ -> ended
    | _ -> DebugEnd.Attached failedResult

  /// The daemon answers a continue: ask the host, and when the hold ended, record what the daemon records.
  let private daemonAnswers (behavior: Behavior) (session: int) (ticket: string) (s: State) : State =
    match observe (DebugTicket ticket) s.Holds.[session] with
    | DebugProgress.StillRunning -> s
    | DebugProgress.Ended ended ->
      let ended = match behavior with Behavior.FabricatesFailureTwin -> fabricated ended | _ -> ended
      let tableSession = match behavior with Behavior.DefaultSessionTwin -> defaultSession | _ -> session
      let table, record = verdictFor s.Table (sessionText session) ticket ended DateTimeOffset.UtcNow
      // The twin that records on every continue neither takes the hold out of the table nor keeps a ticket log.
      let s =
        match behavior with
        | Behavior.RecordsEveryContinueTwin -> s
        | _ -> { s with Table = table }
      match record with
      | None -> s
      | Some record ->
        let landsIn = match behavior with Behavior.DefaultSessionTwin -> tableSession | _ -> session
        let log =
          match behavior with
          | Behavior.RecordsEveryContinueTwin -> DebugTicketLog.empty
          | _ -> s.Logs.[landsIn]
        match DebugRunRecord.plan record log with
        | None -> s
        | Some (_, _, log') ->
          let landed = { Session = landsIn; Ticket = ticket; TestId = record.Verdict.TestId }
          { s with Logs = Map.add landsIn log' s.Logs; Landed = s.Landed @ [ landed ] }

  let step (behavior: Behavior) (s: State) (event: SimEvent) : State =
    match event with
    | SimEvent.Begin(session, test) ->
      let ticket = sprintf "s%d-t%d" session (s.Issued.[session].Length + 1)
      let testCase = testOf test
      let stepped, effect = hostStep session s (HoldEvent.Begin(DebugTicket ticket, testCase))
      match effect with
      | HoldEffect.RefuseOpen _ -> stepped
      | _ ->
        // Eval-defined tests are the ones whose record says Evaluated.
        let symbols = match abs test % 2 with | 0 -> SymbolSupport.CompiledWithSymbols | _ -> SymbolSupport.DefinedByEval
        { stepped with
            Issued = Map.add session (s.Issued.[session] @ [ ticket ]) stepped.Issued
            Began = Map.add ticket session stepped.Began
            Table = HeldTable.add (sessionText session) (target session ticket testCase symbols) stepped.Table }
    | SimEvent.Release(session, attached) ->
      let ticket = latestTicket session s
      let presence = match attached with | true -> DebuggerPresence.DebuggerAttached | false -> DebuggerPresence.NoDebugger
      let released, effect = hostStep session s (HoldEvent.Release(DebugTicket ticket, presence))
      let settled =
        match effect with
        | HoldEffect.StartTest(t, test) ->
          // A function of the test's name, so a seed decides it (string hashes are not stable between runs).
          let result = match (test.FullName |> Seq.sumBy int) % 2 with | 0 -> passedResult | _ -> failedResult
          hostStep session released (HoldEvent.Finished(t, result)) |> fst
        | HoldEffect.AwaitAttach t -> hostStep session released (HoldEvent.AttachWindowSpent t) |> fst
        | _ -> released
      daemonAnswers behavior session ticket settled
    | SimEvent.Ask pick ->
      let all = sessions |> List.collect (fun session -> s.Issued.[session] |> List.map (fun t -> session, t))
      match all with
      | [] -> s
      | _ ->
        let session, ticket = List.item (abs pick % all.Length) all
        daemonAnswers behavior session ticket s
    | SimEvent.Expire session ->
      let ticket = latestTicket session s
      hostStep session s (HoldEvent.Expire(DebugTicket ticket)) |> fst
    | SimEvent.HostEnds session -> hostStep session s (HoldEvent.HostEnding "the host process ended") |> fst

  type Scenario = { Seed: int; Events: SimEvent list }

  let trace (behavior: Behavior) (scenario: Scenario) : State list = scenario.Events |> List.scan (step behavior) initial

  let final (behavior: Behavior) (scenario: Scenario) : State = trace behavior scenario |> List.last

  /// A pure function of the seed: begins on either session, releases with and without a debugger, repeated asks, expiries
  /// and dying hosts, in any order, then asks about every ticket twice so the settled holds are answered more than once.
  let scenarioOf (seed: int) : Scenario =
    let rng = Random seed
    let n = 10 + rng.Next 40
    let events =
      [ for _ in 1 .. n ->
          match rng.Next 20 with
          | 0 | 1 | 2 | 3 | 4 -> SimEvent.Begin(rng.Next 2, rng.Next 6)
          | 5 | 6 | 7 | 8 -> SimEvent.Release(rng.Next 2, true)
          | 9 | 10 -> SimEvent.Release(rng.Next 2, false)
          | 11 | 12 | 13 | 14 | 15 -> SimEvent.Ask(rng.Next 1000)
          | 16 | 17 -> SimEvent.Expire(rng.Next 2)
          | 18 -> SimEvent.HostEnds(rng.Next 2)
          | _ -> SimEvent.Begin(rng.Next 2, rng.Next 6) ]
    { Seed = seed; Events = events @ [ for i in 0 .. 30 -> SimEvent.Ask i ] @ [ for i in 0 .. 30 -> SimEvent.Ask i ] }

  /// A hold on session 1, released with a debugger so its test runs, and asked about again: the shape the
  /// record-on-every-continue twin gets wrong.
  let repeatedContinue : Scenario =
    { Seed = 0; Events = [ SimEvent.Begin(1, 0); SimEvent.Release(1, true); SimEvent.Ask 0; SimEvent.Ask 0 ] }

  /// A hold released with no debugger, so its test never ran: the shape the fabricating twin gets wrong.
  let releasedWithoutDebugger : Scenario =
    { Seed = 0; Events = [ SimEvent.Begin(0, 0); SimEvent.Release(0, false); SimEvent.Ask 0 ] }

  /// A hold on session 1 that runs: the shape the default-session twin gets wrong.
  let secondSessionRuns : Scenario =
    { Seed = 0; Events = [ SimEvent.Begin(1, 0); SimEvent.Release(1, true) ] }

  /// An expiry before any release, then a release: the hold ended without a debugger and never ran.
  let expiredThenReleased : Scenario =
    { Seed = 0; Events = [ SimEvent.Begin(0, 0); SimEvent.Expire 0; SimEvent.Release(0, true); SimEvent.Ask 0 ] }

  /// The host ends with the test held: no verdict.
  let hostEndsHolding : Scenario =
    { Seed = 0; Events = [ SimEvent.Begin(0, 0); SimEvent.HostEnds 0; SimEvent.Ask 0 ] }

module DebugRecordSimInvariants =
  open DebugRecordSim

  [<RequireQualifiedAccess>]
  type Outcome =
    | Holds
    | Violated of message: string

  type Invariant = { Id: string; Description: string; Check: State list -> Outcome }

  let private lastState (states: State list) = List.last states

  let private firstViolation (found: string option) : Outcome =
    match found with
    | Some message -> Outcome.Violated message
    | None -> Outcome.Holds

  /// a-verdict-is-backed-by-an-attached-end: a verdict reaches a row only for a hold the host ended attached.
  let verdictNeedsAttachedEnd : Invariant =
    { Id = "a-verdict-is-backed-by-an-attached-end"
      Description = "Every verdict that landed has a hold the host ended with the test run under a debugger, for the same ticket."
      Check = fun states ->
        let s = lastState states
        s.Landed
        |> List.tryFind (fun landed ->
          not (s.HostEnded |> List.exists (fun (session, ticket, how) ->
            session = landed.Session && ticket = landed.Ticket && (match how with DebugEnd.Attached _ -> true | _ -> false))))
        |> Option.map (fun landed -> sprintf "a verdict landed for %s in session %d with no attached end behind it" landed.Ticket landed.Session)
        |> firstViolation }

  /// a-ticket-records-at-most-once: however often the editor asks, a ticket is recorded once.
  let ticketRecordsOnce : Invariant =
    { Id = "a-ticket-records-at-most-once"
      Description = "No (session, ticket) pair appears twice among the verdicts that landed."
      Check = fun states ->
        (lastState states).Landed
        |> List.countBy (fun landed -> landed.Session, landed.Ticket)
        |> List.tryFind (fun (_, count) -> count > 1)
        |> Option.map (fun ((session, ticket), count) -> sprintf "%s in session %d recorded %d times" ticket session count)
        |> firstViolation }

  /// a-verdict-lands-in-the-session-that-held-the-test: never another session's row.
  let verdictLandsInHoldingSession : Invariant =
    { Id = "a-verdict-lands-in-the-session-that-held-the-test"
      Description = "A verdict's session is the session that began its ticket."
      Check = fun states ->
        let s = lastState states
        s.Landed
        |> List.tryFind (fun landed -> Map.tryFind landed.Ticket s.Began <> Some landed.Session)
        |> Option.map (fun landed -> sprintf "%s began in session %A and landed in session %d" landed.Ticket (Map.tryFind landed.Ticket s.Began) landed.Session)
        |> firstViolation }

  let all : Invariant list = [ verdictNeedsAttachedEnd; ticketRecordsOnce; verdictLandsInHoldingSession ]

  /// Every invariant a trace breaks, with its message.
  let violations (states: State list) : (string * string) list =
    all
    |> List.choose (fun invariant ->
      match invariant.Check states with
      | Outcome.Holds -> None
      | Outcome.Violated message -> Some(invariant.Id, message))
