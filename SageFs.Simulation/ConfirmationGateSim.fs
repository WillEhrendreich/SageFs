namespace SageFs.Simulation

open System
open SageFs
open SageFs.Affordances
open SageFs.ConfirmationPolicy

/// Deterministic Simulation Testing for the claim "only manage_local_data asks a person; stop_session,
/// reset_fsi_session, hard_reset_fsi_session, switch_workflow and stop_app never do, for any session".
///
/// Three callers (two connections and one member token) and three sessions. Sessions are made by a caller or by a
/// person, have code run in them by a caller or by a person at the dashboard, are stopped, and are forgotten by a
/// daemon restart (the creator record lives in memory). Callers then call tools on a session, and the question at
/// each call is whether a person was asked. Who made the session and who ran code in it are the facts the old gate
/// read to decide; the real decision reads none of them, and the twins that do read them are the bugs the invariants
/// exist to catch.
///
/// The decision under test is the REAL `ConfirmationPolicy.decide`. The truth is an independent bookkeeping that
/// never calls it: which tool was called, and who made and worked in the session.
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
    /// Caller `caller` calls tool number `tool` (an index into `tools`) on session `session`.
    | Calls of caller: int * tool: int * session: int

  type Scenario = { Seed: int; Ops: Op list }

  /// The tools the sim exercises: the five that are normal steps of everyday use, then the one that asks.
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
      /// A person was asked (the decision said `Required`). Only meaningful for `Calls`.
      Asked: bool
      /// Truth: the tool is `manage_local_data`.
      IsLocalData: bool
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
    /// TWIN: nobody is asked about anything, `manage_local_data` included.
    | SkipsEveryTool
    /// TWIN: `manage_local_data` is spared when the caller made the session and nobody else ran code in it, the way
    /// the old gate spared the session tools.
    | SparesTheCreator
    /// TWIN: the old gate. The five session tools ask unless the caller made the session and nobody else ran code in it.
    | AsksAboutSessionTools

  let sessionName (session: int) : string = sprintf "sim-%d" session

  let toolOf (index: int) : ToolName = List.item (abs index % List.length tools) tools

  /// The decision, per policy. The twins differ from the real one only in what they read or ignore.
  let asks (policy: Policy) (tool: ToolName) (ownAndAlone: bool) : bool =
    let asked (confirmation: Confirmation) =
      match confirmation with
      | Confirmation.Required _ -> true
      | Confirmation.NotRequired -> false
    match policy with
    | Policy.Real -> ConfirmationPolicy.decide tool |> asked
    | Policy.SkipsEveryTool -> false
    | Policy.SparesTheCreator -> ConfirmationPolicy.decide tool |> asked && not ownAndAlone
    | Policy.AsksAboutSessionTools -> (ConfirmationPolicy.decide tool |> asked) || not ownAndAlone

  let runWith (name: string) (policy: Policy) (scenario: Scenario) : Trace =
    let quietStep (op: Op) =
      { Op = op; Asked = false; IsLocalData = false; CallerMadeItAndItIsRemembered = false; NobodyElseRanCode = false }
    let step (sessions: Map<int, Session>, acc) (op: Op) =
      match op with
      | Op.Creates(caller, session) ->
        match Map.containsKey session sessions with
        | true -> sessions, quietStep op :: acc
        | false ->
          Map.add session { Maker = Maker.Caller caller; Remembered = true; RanCodeBy = Set.empty } sessions, quietStep op :: acc
      | Op.PersonCreates session ->
        let next =
          match Map.containsKey session sessions with
          | true -> sessions
          | false -> Map.add session { Maker = Maker.Person; Remembered = false; RanCodeBy = Set.empty } sessions
        next, quietStep op :: acc
      | Op.RunsCode(runner, session) ->
        match Map.tryFind session sessions with
        | None -> sessions, quietStep op :: acc
        | Some s -> Map.add session { s with RanCodeBy = Set.add runner s.RanCodeBy } sessions, quietStep op :: acc
      | Op.Stops session -> Map.remove session sessions, quietStep op :: acc
      | Op.Restarts -> sessions |> Map.map (fun _ s -> { s with Remembered = false }), quietStep op :: acc
      | Op.Calls(caller, toolIndex, session) ->
        let tool = toolOf toolIndex
        let truthOwn =
          match Map.tryFind session sessions with
          | Some { Maker = Maker.Caller maker; Remembered = true } -> maker = caller
          | Some _
          | None -> false
        let truthAlone =
          match Map.tryFind session sessions with
          | Some s -> Set.isSubset s.RanCodeBy (Set.singleton caller)
          | None -> false
        sessions,
        { Op = op
          Asked = asks policy tool (truthOwn && truthAlone)
          IsLocalData = (tool = ToolName.ManageLocalData)
          CallerMadeItAndItIsRemembered = truthOwn
          NobodyElseRanCode = truthAlone } :: acc
    let _, snapshots = scenario.Ops |> List.fold step (Map.empty, [])
    { Scenario = scenario; Snapshots = List.rev snapshots; Reducer = name }

  /// The real decision.
  let run (scenario: Scenario) : Trace = runWith "real (ConfirmationPolicy.decide)" Policy.Real scenario

  /// TWIN: nobody is asked about anything.
  let runSkipsEveryTool (scenario: Scenario) : Trace = runWith "twin-skips-every-tool" Policy.SkipsEveryTool scenario

  /// TWIN: manage_local_data is spared for the creator of a session nobody else worked in.
  let runSparesTheCreator (scenario: Scenario) : Trace = runWith "twin-spares-the-creator" Policy.SparesTheCreator scenario

  /// TWIN: the old gate, which asks about the session tools unless the caller made the session and worked in it alone.
  let runAsksAboutSessionTools (scenario: Scenario) : Trace =
    runWith "twin-asks-about-session-tools" Policy.AsksAboutSessionTools scenario

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

  let firstViolation (t: Trace) (violates: int -> Snapshot -> string option) : Outcome =
    calls t
    |> List.tryPick (fun (i, s) -> violates i s)
    |> function
       | Some message -> Outcome.Violated(sprintf "%s (seed=%d, ops=%A)" message t.Scenario.Seed t.Scenario.Ops)
       | None -> Outcome.Holds

  /// normal-workflow-tools-never-ask: a call to one of the five everyday tools asks nobody, whatever the session's
  /// maker, its record, or who has run code in it.
  let normalWorkflowToolsNeverAsk : Invariant =
    { Id = "normal-workflow-tools-never-ask"
      Description = "A call to stop_session, reset_fsi_session, hard_reset_fsi_session, switch_workflow or stop_app asks nobody."
      Check = fun t ->
        firstViolation t (fun i s ->
          match s.IsLocalData || not s.Asked with
          | true -> None
          | false -> Some(sprintf "op #%d (%A) asked a person about an everyday tool" i s.Op)) }

  /// local-data-always-asks: a call to manage_local_data asks a person, even on a session its caller made and alone
  /// worked in, because it clears stores that belong to no session.
  let localDataAlwaysAsks : Invariant =
    { Id = "local-data-always-asks"
      Description = "A call to manage_local_data asks a person every time."
      Check = fun t ->
        firstViolation t (fun i s ->
          match not s.IsLocalData || s.Asked with
          | true -> None
          | false ->
            Some(
              sprintf
                "op #%d (%A) asked nobody about manage_local_data (caller made the session: %b, nobody else ran code: %b)"
                i s.Op s.CallerMadeItAndItIsRemembered s.NobodyElseRanCode
            )) }

  let all : Invariant list = [ normalWorkflowToolsNeverAsk; localDataAlwaysAsks ]

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

  /// Caller `caller` calls every tool in `tools`, in order, on `session`.
  let private callsEveryTool (caller: int) (session: int) : Op list =
    [ for tool in 0 .. List.length tools - 1 -> Op.Calls(caller, tool, session) ]

  /// What a call to each tool, in `tools` order, should ask: the five everyday tools nobody, manage_local_data a person.
  let expectedAsks : bool list = tools |> List.map (fun tool -> tool = ToolName.ManageLocalData)

  /// Caller 1 makes session 1 and calls every tool on it.
  let ownSession : Scenario =
    { Seed = 900; Ops = Op.Creates(1, 1) :: callsEveryTool 1 1 }

  /// Caller 1 makes session 1, caller 2 calls every tool on it.
  let someoneElsesSession : Scenario =
    { Seed = 901; Ops = Op.Creates(1, 1) :: callsEveryTool 2 1 }

  /// A person makes session 1, caller 1 calls every tool on it.
  let aPersonsSession : Scenario =
    { Seed = 902; Ops = Op.PersonCreates 1 :: callsEveryTool 1 1 }

  /// Caller 1 makes session 1, the daemon restarts and forgets who made it, caller 1 calls every tool on it.
  let afterARestart : Scenario =
    { Seed = 903; Ops = [ Op.Creates(1, 1); Op.Restarts ] @ callsEveryTool 1 1 }

  /// Caller 1 makes session 1, a person runs code in it at the dashboard, caller 1 calls every tool on it.
  let aPersonRanCodeInIt : Scenario =
    { Seed = 904; Ops = [ Op.Creates(1, 1); Op.RunsCode(0, 1) ] @ callsEveryTool 1 1 }

  /// Caller 1 makes session 1, caller 2 runs code in it, caller 1 calls every tool on it.
  let anotherCallerRanCodeInIt : Scenario =
    { Seed = 905; Ops = [ Op.Creates(1, 1); Op.RunsCode(2, 1) ] @ callsEveryTool 1 1 }

  /// Caller 3 (a token) makes session 1, runs code in it, and calls every tool on it: the one case the old gate spared.
  let ownCodeOnly : Scenario =
    { Seed = 906; Ops = [ Op.Creates(3, 1); Op.RunsCode(3, 1) ] @ callsEveryTool 3 1 }

  /// Calls on a session that was never made, or was stopped.
  let noSuchSession : Scenario =
    { Seed = 907; Ops = [ Op.Creates(1, 1); Op.Stops 1 ] @ callsEveryTool 1 1 }
