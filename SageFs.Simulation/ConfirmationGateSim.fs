namespace SageFs.Simulation

open System
open SageFs
open SageFs.Affordances
open SageFs.ConfirmationPolicy
open SageFs.MemberTable

/// Deterministic Simulation Testing for the claim "a confirm-tier tool asks a person unless the caller created that
/// very session itself and nobody else has run code in it, and then it asks nobody".
///
/// Three callers (two connections and one member token) and three sessions. Sessions are made by a caller or by a
/// person, have code run in them by a caller or by a person at the dashboard, are stopped, and are forgotten by a
/// daemon restart (the creator record and the record of who ran code live in memory). Callers then call confirm-tier
/// tools on a session, and the question at each call is whether a person was asked.
///
/// The decision under test is the REAL `ConfirmationPolicy.decide`, fed the way the gate feeds it: the creator is the
/// recorded `MemberId` itself, the way `SessionOwners` holds it, and who ran code is the set of
/// `EvalSource`s, the way `SessionEvaluators` holds it, never the sim's own tables. The truth is an independent
/// bookkeeping that never calls `decide`: who made each session, who ran code in it, whether the daemon still
/// remembers. Presence (another agent working in it now) is not modelled here; `ConfirmationPolicyTests` pins it.
///
/// Same rules as the other simulations: chaos is data (a `Scenario` is an op list, a seed replays it), and twins put
/// back the bugs the invariants exist to catch.
module ConfirmationGateSim =

  [<RequireQualifiedAccess>]
  type Op =
    /// Caller `caller` (1 to 3) creates session `session` (1 to 3) over MCP. Ignored if it exists.
    | Creates of caller: int * session: int
    /// A person creates session `session`: no creator on record.
    | PersonCreates of session: int
    /// `runner` runs code in session `session`: 0 is a person at the dashboard, 1 to 3 a caller. Ignored if the
    /// session does not exist.
    | RunsCode of runner: int * session: int
    | Stops of session: int
    /// The daemon restarts: sessions survive, the record of who made them does not.
    | Restarts
    /// Caller `caller` calls confirm-tier tool number `tool` (an index into `tools`) on session `session`.
    | Calls of caller: int * tool: int * session: int

  type Scenario = { Seed: int; Ops: Op list }

  /// The confirm tier as the sim exercises it: the four one-session tools and the two that are not.
  let tools : ToolName list =
    [ ToolName.StopSession
      ToolName.ResetFsiSession
      ToolName.HardResetFsiSession
      ToolName.SwitchWorkflow
      ToolName.StopApp
      ToolName.ManageLocalData ]

  /// One call's end.
  type Snapshot =
    { Op: Op
      /// A person was asked (the real decision said `Required`). Only meaningful for `Calls`.
      Asked: bool
      /// Truth: the call acts on one session.
      OneSessionTool: bool
      /// Truth: the session exists, the caller made it, and the daemon still remembers.
      CallerMadeItAndItIsRemembered: bool
      /// Truth: nobody but the caller has run code in the session.
      NobodyElseRanCode: bool }

  type Trace =
    { Scenario: Scenario
      Snapshots: Snapshot list
      Reducer: string }

  [<RequireQualifiedAccess>]
  type Maker =
    | Person
    | Caller of int

  /// `RanCodeBy` holds runners as `Op.RunsCode` numbers them: 0 is a person, 1 to 3 a caller.
  type Session = { Maker: Maker; Remembered: bool; RanCodeBy: Set<int> }

  [<RequireQualifiedAccess>]
  type Policy =
    /// The real decision.
    | Real
    /// TWIN: the confirmation is skipped for every session, whoever made it.
    | SkipsEverySession
    /// TWIN: a session with no creator on record is taken to be the caller's own.
    | TreatsNoRecordAsOwn
    /// TWIN: every confirm-tier tool is treated as acting on one session.
    | TreatsEveryToolAsOneSession
    /// TWIN: who ran code in the session is never read, so the creator alone decides.
    | IgnoresWhoRanCode

  /// Callers 1 and 2 are connections, 3 is a member token. The token outranks the connection, so it is a different
  /// identity from both.
  let identity (caller: int) : MemberId =
    match caller with
    | 1 -> MemberId.ofConnectionHandle "handle-one"
    | 2 -> MemberId.ofConnectionHandle "handle-two"
    | _ -> MemberId.Capability "fingerprint-three"

  let sessionName (session: int) : string = sprintf "sim-%d" session

  let toolOf (index: int) : ToolName = List.item (abs index % List.length tools) tools

  /// Where code from runner `runner` comes from, as the daemon records it: a person at the dashboard, or a caller under
  /// its bound identity.
  let sourceOf (runner: int) : EvalSource =
    match runner with
    | 0 -> EvalSource.Dashboard
    | caller -> EvalSource.Member (identity caller)

  /// The decision, per policy. The twins differ from the real one only in what they feed it or what they ignore.
  let asks (policy: Policy) (tool: ToolName) (session: int) (caller: int) (record: SessionCreator) (ranCode: Set<EvalSource>) : bool =
    let target = TargetSession.Session (sessionName session)
    let creator =
      match record, policy with
      | SessionCreator.Recorded _, _ -> record
      | SessionCreator.Unrecorded, Policy.TreatsNoRecordAsOwn -> SessionCreator.Recorded (identity caller)
      | SessionCreator.Unrecorded, _ -> SessionCreator.Unrecorded
    let activity =
      match policy with
      | Policy.IgnoresWhoRanCode -> SessionActivity.quiet
      | Policy.Real
      | Policy.SkipsEverySession
      | Policy.TreatsNoRecordAsOwn
      | Policy.TreatsEveryToolAsOneSession -> { SessionActivity.quiet with RanCode = ranCode }
    let asked (confirmation: Confirmation) =
      match confirmation with
      | Confirmation.Required _ -> true
      | Confirmation.NotRequired _ -> false
    match policy with
    | Policy.SkipsEverySession -> false
    | Policy.TreatsEveryToolAsOneSession ->
      ConfirmationPolicy.decide ToolName.StopSession target creator activity (identity caller) |> asked
    | Policy.Real
    | Policy.TreatsNoRecordAsOwn
    | Policy.IgnoresWhoRanCode ->
      ConfirmationPolicy.decide tool target creator activity (identity caller) |> asked

  let runWith (name: string) (policy: Policy) (scenario: Scenario) : Trace =
    let quietStep (op: Op) =
      { Op = op; Asked = false; OneSessionTool = false; CallerMadeItAndItIsRemembered = false; NobodyElseRanCode = false }
    // The daemon's records: session name to the identity the creating call was bound to, and session name to every
    // source that ran code in it. Both live in memory, so a restart empties both and a stop drops the session's.
    let step ((sessions: Map<int, Session>, records: Map<string, MemberId>, ranCode: Map<string, Set<EvalSource>>), acc) (op: Op) =
      match op with
      | Op.Creates(caller, session) ->
        match Map.containsKey session sessions with
        | true -> (sessions, records, ranCode), quietStep op :: acc
        | false ->
          (Map.add session { Maker = Maker.Caller caller; Remembered = true; RanCodeBy = Set.empty } sessions,
           Map.add (sessionName session) (identity caller) records,
           ranCode),
          quietStep op :: acc
      | Op.PersonCreates session ->
        let next =
          match Map.containsKey session sessions with
          | true -> sessions
          | false -> Map.add session { Maker = Maker.Person; Remembered = false; RanCodeBy = Set.empty } sessions
        (next, records, ranCode), quietStep op :: acc
      | Op.RunsCode(runner, session) ->
        match Map.tryFind session sessions with
        | None -> (sessions, records, ranCode), quietStep op :: acc
        | Some s ->
          let name = sessionName session
          let sources = ranCode |> Map.tryFind name |> Option.defaultValue Set.empty
          (Map.add session { s with RanCodeBy = Set.add runner s.RanCodeBy } sessions,
           records,
           Map.add name (Set.add (sourceOf runner) sources) ranCode),
          quietStep op :: acc
      | Op.Stops session ->
        (Map.remove session sessions, Map.remove (sessionName session) records, Map.remove (sessionName session) ranCode),
        quietStep op :: acc
      | Op.Restarts ->
        (sessions |> Map.map (fun _ s -> { s with Remembered = false }), Map.empty, Map.empty),
        quietStep op :: acc
      | Op.Calls(caller, toolIndex, session) ->
        let tool = toolOf toolIndex
        let record =
          match Map.tryFind (sessionName session) records with
          | Some maker -> SessionCreator.Recorded maker
          | None -> SessionCreator.Unrecorded
        let truthOwn =
          match Map.tryFind session sessions with
          | Some { Maker = Maker.Caller maker; Remembered = true } -> maker = caller
          | Some _
          | None -> false
        let truthAlone =
          match Map.tryFind session sessions with
          | Some s -> Set.isSubset s.RanCodeBy (Set.singleton caller)
          | None -> false
        (sessions, records, ranCode),
        { Op = op
          Asked = asks policy tool session caller record (ranCode |> Map.tryFind (sessionName session) |> Option.defaultValue Set.empty)
          OneSessionTool = Set.contains tool singleSessionConfirmTools
          CallerMadeItAndItIsRemembered = truthOwn
          NobodyElseRanCode = truthAlone } :: acc
    let _, snapshots = scenario.Ops |> List.fold step ((Map.empty, Map.empty, Map.empty), [])
    { Scenario = scenario; Snapshots = List.rev snapshots; Reducer = name }

  /// The real decision.
  let run (scenario: Scenario) : Trace = runWith "real (ConfirmationPolicy.decide)" Policy.Real scenario

  /// TWIN: the confirmation is skipped for every session.
  let runSkipsEverySession (scenario: Scenario) : Trace = runWith "twin-skips-every-session" Policy.SkipsEverySession scenario

  /// TWIN: a session nobody recorded is taken as the caller's own.
  let runTreatsNoRecordAsOwn (scenario: Scenario) : Trace = runWith "twin-treats-no-record-as-own" Policy.TreatsNoRecordAsOwn scenario

  /// TWIN: every confirm-tier tool is treated as a one-session tool.
  let runTreatsEveryToolAsOneSession (scenario: Scenario) : Trace =
    runWith "twin-treats-every-tool-as-one-session" Policy.TreatsEveryToolAsOneSession scenario

  /// TWIN: who ran code in the session is ignored, so a session someone else worked in is spared.
  let runIgnoresWhoRanCode (scenario: Scenario) : Trace = runWith "twin-ignores-who-ran-code" Policy.IgnoresWhoRanCode scenario

/// Named invariants over a `ConfirmationGateSim.Trace`.
module ConfirmationGateInvariants =
  open ConfirmationGateSim

  [<RequireQualifiedAccess>]
  type Outcome =
    | Holds
    | Violated of message: string

  type Invariant =
    { Id: string
      Description: string
      Check: Trace -> Outcome }

  let calls (t: Trace) : (int * Snapshot) list =
    t.Snapshots
    |> List.mapi (fun i s -> i, s)
    |> List.filter (fun (_, s) ->
      match s.Op with
      | Op.Calls _ -> true
      | Op.Creates _
      | Op.PersonCreates _
      | Op.RunsCode _
      | Op.Stops _
      | Op.Restarts -> false)

  /// Truth: the one case where nobody should be asked.
  let ownAndAlone (s: Snapshot) : bool = s.OneSessionTool && s.CallerMadeItAndItIsRemembered && s.NobodyElseRanCode

  let firstViolation (t: Trace) (violates: int -> Snapshot -> string option) : Outcome =
    calls t
    |> List.tryPick (fun (i, s) -> violates i s)
    |> function
       | Some message -> Outcome.Violated(sprintf "%s (seed=%d, ops=%A)" message t.Scenario.Seed t.Scenario.Ops)
       | None -> Outcome.Holds

  /// nobody-is-spared-a-session-they-did-not-make: if no person was asked, the tool acts on one session and the caller
  /// made that session and the daemon still remembers.
  let nobodyIsSparedASessionTheyDidNotMake : Invariant =
    { Id = "nobody-is-spared-a-session-they-did-not-make"
      Description = "A call that asked nobody is a one-session tool on a session the caller made and the daemon still remembers."
      Check = fun t ->
        firstViolation t (fun i s ->
          match s.Asked || (s.OneSessionTool && s.CallerMadeItAndItIsRemembered) with
          | true -> None
          | false -> Some(sprintf "op #%d (%A) asked nobody, but the caller did not make a remembered session with a one-session tool" i s.Op)) }

  /// nobody-is-spared-a-session-someone-else-worked-in: if no person was asked, nobody but the caller ran code there.
  let nobodyIsSparedASessionSomeoneElseWorkedIn : Invariant =
    { Id = "nobody-is-spared-a-session-someone-else-worked-in"
      Description = "A call that asked nobody is on a session nobody but the caller has run code in."
      Check = fun t ->
        firstViolation t (fun i s ->
          match s.Asked || s.NobodyElseRanCode with
          | true -> None
          | false -> Some(sprintf "op #%d (%A) asked nobody, but someone else had run code in the session" i s.Op)) }

  /// asks-exactly-when-the-truth-says: a call asks a person unless it is the one case, so the rule is not an always-ask.
  let asksExactlyWhenTheTruthSays : Invariant =
    { Id = "asks-exactly-when-the-truth-says"
      Description = "A call asks a person exactly when it is not a one-session tool on a remembered session its caller made and alone ran code in."
      Check = fun t ->
        firstViolation t (fun i s ->
          let shouldAsk = not (ownAndAlone s)
          match s.Asked = shouldAsk with
          | true -> None
          | false -> Some(sprintf "op #%d (%A) asked=%b but the truth is %b" i s.Op s.Asked shouldAsk)) }

  let all : Invariant list =
    [ nobodyIsSparedASessionTheyDidNotMake; nobodyIsSparedASessionSomeoneElseWorkedIn; asksExactlyWhenTheTruthSays ]

  /// The invariants that failed for a trace, if any (id, message).
  let violations (t: Trace) : (string * string) list =
    all
    |> List.choose (fun inv ->
      match inv.Check t with
      | Outcome.Holds -> None
      | Outcome.Violated msg -> Some(inv.Id, msg))

/// Seeded, dependency-free generators for confirmation-gate scenarios. Same seed, identical scenario.
module ConfirmationGateGenerators =
  open ConfirmationGateSim

  let fromSeed (seed: int) : Scenario =
    let rnd = Random(seed)
    let n = rnd.Next(6, 40)
    let ops =
      [ for _ in 1 .. n ->
          match rnd.Next(0, 12) with
          | 0 | 1 -> Op.Creates(rnd.Next(1, 4), rnd.Next(1, 4))
          | 2 -> Op.PersonCreates(rnd.Next(1, 4))
          | 3 -> Op.Stops(rnd.Next(1, 4))
          | 4 -> Op.Restarts
          | 5 | 6 -> Op.RunsCode(rnd.Next(0, 4), rnd.Next(1, 4))
          | _ -> Op.Calls(rnd.Next(1, 4), rnd.Next(0, 6), rnd.Next(1, 4)) ]
    { Seed = seed; Ops = ops }

  /// Caller 1 makes session 1 and resets it: nobody is asked.
  let ownSession : Scenario =
    { Seed = 900; Ops = [ Op.Creates(1, 1); Op.Calls(1, 2, 1) ] }

  /// Caller 2 resets the session caller 1 made.
  let someoneElsesSession : Scenario =
    { Seed = 901; Ops = [ Op.Creates(1, 1); Op.Calls(2, 2, 1) ] }

  /// A person made session 1, and caller 1 stops it.
  let aPersonsSession : Scenario =
    { Seed = 902; Ops = [ Op.PersonCreates 1; Op.Calls(1, 0, 1) ] }

  /// Caller 1 made session 1, the daemon restarted, and caller 1 resets it: the daemon forgot, so a person is asked.
  let afterARestart : Scenario =
    { Seed = 903; Ops = [ Op.Creates(1, 1); Op.Restarts; Op.Calls(1, 2, 1) ] }

  /// Caller 3 (a token) made session 1 and clears local data: that tool is not about one session.
  let localDataIsNeverSpared : Scenario =
    { Seed = 904; Ops = [ Op.Creates(3, 1); Op.Calls(3, 5, 1) ] }

  /// Caller 1 made session 1 and stops its app: the app may have users SageFs cannot see.
  let stopAppIsNeverSpared : Scenario =
    { Seed = 905; Ops = [ Op.Creates(1, 1); Op.Calls(1, 4, 1) ] }

  /// Caller 1 made session 1 and ran code in it, then resets it: still nobody else's work, so nobody is asked.
  let ownCodeOnly : Scenario =
    { Seed = 906; Ops = [ Op.Creates(1, 1); Op.RunsCode(1, 1); Op.Calls(1, 2, 1) ] }

  /// Caller 1 made session 1, a person ran code in it from the dashboard, and caller 1 resets it: a person is asked.
  let aPersonRanCodeInIt : Scenario =
    { Seed = 907; Ops = [ Op.Creates(1, 1); Op.RunsCode(0, 1); Op.Calls(1, 2, 1) ] }

  /// Caller 1 made session 1, caller 2 ran code in it, and caller 1 stops it: a person is asked.
  let anotherCallerRanCodeInIt : Scenario =
    { Seed = 908; Ops = [ Op.Creates(1, 1); Op.RunsCode(2, 1); Op.Calls(1, 0, 1) ] }

  /// The session is stopped and made again: who ran code in the old one does not follow it.
  let aStoppedSessionForgetsWhoRanCode : Scenario =
    { Seed = 909; Ops = [ Op.Creates(1, 1); Op.RunsCode(0, 1); Op.Stops 1; Op.Creates(1, 1); Op.Calls(1, 2, 1) ] }
