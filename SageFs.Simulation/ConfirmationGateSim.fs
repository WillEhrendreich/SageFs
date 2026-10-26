namespace SageFs.Simulation

open System
open SageFs
open SageFs.Affordances
open SageFs.ConfirmationPolicy
open SageFs.MemberTable

/// Deterministic Simulation Testing for the claim "a confirm-tier tool asks a person unless the caller created that
/// very session itself, and then it asks nobody".
///
/// Three callers (two connections and one member token) and three sessions. Sessions are made by a caller or by a
/// person, stopped, and forgotten by a daemon restart (the creator record lives in memory). Callers then call
/// confirm-tier tools on a session, and the question at each call is whether a person was asked.
///
/// The decision under test is the REAL `ConfirmationPolicy.decide`, fed the way the gate feeds it: the creator is the
/// recorded `MemberId` itself, the way `McpTools.sessionOwners` holds it, never the sim's own table. The truth is an
/// independent bookkeeping that never calls `decide`: who made each session, whether the daemon still remembers.
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
    | Stops of session: int
    /// The daemon restarts: sessions survive, the record of who made them does not.
    | Restarts
    /// Caller `caller` calls confirm-tier tool number `tool` (an index into `tools`) on session `session`.
    | Calls of caller: int * tool: int * session: int

  type Scenario = { Seed: int; Ops: Op list }

  /// The confirm tier as the sim exercises it: the five one-session tools and the one that is not.
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
      CallerMadeItAndItIsRemembered: bool }

  type Trace =
    { Scenario: Scenario
      Snapshots: Snapshot list
      Reducer: string }

  [<RequireQualifiedAccess>]
  type Maker =
    | Person
    | Caller of int

  type Session = { Maker: Maker; Remembered: bool }

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

  /// Callers 1 and 2 are connections, 3 is a member token. The token outranks the connection, so it is a different
  /// identity from both.
  let identity (caller: int) : MemberId =
    match caller with
    | 1 -> MemberId.ofConnectionHandle "handle-one"
    | 2 -> MemberId.ofConnectionHandle "handle-two"
    | _ -> MemberId.Capability "fingerprint-three"

  let sessionName (session: int) : string = sprintf "sim-%d" session

  let toolOf (index: int) : ToolName = List.item (abs index % List.length tools) tools

  /// The decision, per policy. The twins differ from the real one only in what they feed it or what they ignore.
  let asks (policy: Policy) (tool: ToolName) (session: int) (caller: int) (record: SessionCreator) : bool =
    let target = TargetSession.Session (sessionName session)
    let creator =
      match record, policy with
      | SessionCreator.Recorded _, _ -> record
      | SessionCreator.Unrecorded, Policy.TreatsNoRecordAsOwn -> SessionCreator.Recorded (identity caller)
      | SessionCreator.Unrecorded, _ -> SessionCreator.Unrecorded
    match policy with
    | Policy.SkipsEverySession -> false
    | Policy.TreatsEveryToolAsOneSession ->
      match ConfirmationPolicy.decide ToolName.StopSession target creator (identity caller) with
      | Confirmation.Required _ -> true
      | Confirmation.NotRequired _ -> false
    | Policy.Real
    | Policy.TreatsNoRecordAsOwn ->
      match ConfirmationPolicy.decide tool target creator (identity caller) with
      | Confirmation.Required _ -> true
      | Confirmation.NotRequired _ -> false

  let runWith (name: string) (policy: Policy) (scenario: Scenario) : Trace =
    // The daemon's creator record: session name to the identity the creating call was bound to.
    let step ((sessions: Map<int, Session>, records: Map<string, MemberId>), acc) (op: Op) =
      match op with
      | Op.Creates(caller, session) ->
        match Map.containsKey session sessions with
        | true -> (sessions, records), { Op = op; Asked = false; OneSessionTool = false; CallerMadeItAndItIsRemembered = false } :: acc
        | false ->
          (Map.add session { Maker = Maker.Caller caller; Remembered = true } sessions,
           Map.add (sessionName session) (identity caller) records),
          { Op = op; Asked = false; OneSessionTool = false; CallerMadeItAndItIsRemembered = false } :: acc
      | Op.PersonCreates session ->
        let next =
          match Map.containsKey session sessions with
          | true -> sessions
          | false -> Map.add session { Maker = Maker.Person; Remembered = false } sessions
        (next, records), { Op = op; Asked = false; OneSessionTool = false; CallerMadeItAndItIsRemembered = false } :: acc
      | Op.Stops session ->
        (Map.remove session sessions, Map.remove (sessionName session) records),
        { Op = op; Asked = false; OneSessionTool = false; CallerMadeItAndItIsRemembered = false } :: acc
      | Op.Restarts ->
        (sessions |> Map.map (fun _ s -> { s with Remembered = false }), Map.empty),
        { Op = op; Asked = false; OneSessionTool = false; CallerMadeItAndItIsRemembered = false } :: acc
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
        (sessions, records),
        { Op = op
          Asked = asks policy tool session caller record
          OneSessionTool = Set.contains tool singleSessionConfirmTools
          CallerMadeItAndItIsRemembered = truthOwn } :: acc
    let _, snapshots = scenario.Ops |> List.fold step ((Map.empty, Map.empty), [])
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
      | Op.Stops _
      | Op.Restarts -> false)

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

  /// asks-exactly-when-the-truth-says: a call asks a person unless it is the one case, so the rule is not an always-ask.
  let asksExactlyWhenTheTruthSays : Invariant =
    { Id = "asks-exactly-when-the-truth-says"
      Description = "A call asks a person exactly when it is not a one-session tool on a remembered session its caller made."
      Check = fun t ->
        firstViolation t (fun i s ->
          let shouldAsk = not (s.OneSessionTool && s.CallerMadeItAndItIsRemembered)
          match s.Asked = shouldAsk with
          | true -> None
          | false -> Some(sprintf "op #%d (%A) asked=%b but the truth is %b" i s.Op s.Asked shouldAsk)) }

  let all : Invariant list = [ nobodyIsSparedASessionTheyDidNotMake; asksExactlyWhenTheTruthSays ]

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
