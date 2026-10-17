namespace SageFs.Simulation

open System
open System.Threading.Tasks
open SageFs
open SageFs.SessionReaper

/// Deterministic Simulation Testing for the claim "the daemon stops a session an agent abandoned, and only that".
///
/// A machine has up to four sessions and three owner connections. Sessions are made by an agent (a ledger record) or by a
/// person (no record). Time advances in jumps that cross the bound, owners come and go, sessions are used, the daemon
/// restarts (a new `Reaper`, presence forgotten), and the reaper sweeps. The sweep is the REAL one: `Reaper.SweepAsync`
/// over real `SessionInfo` values and a stop function that removes the session, as the real stop path does.
///
/// Same design rules as the other simulations:
///   * Chaos is DATA: a `Scenario` is an ordered op list. Same ops, same trace.
///   * Ground truth is an independent fold that never reads the reaper: when each owner was last seen at a sweep, when each
///     session was last used, when the daemon last started. A session is abandoned when an agent made it, its owner is not
///     here, and both the owner's absence and the session's idleness reach the bound, counted from no earlier than the
///     daemon's start.
///   * Twins reintroduce the bugs the invariants exist to catch.
module SessionReaperSim =

  /// The bound every run uses, in minutes. Ops advance time in jumps either side of it.
  [<Literal>]
  let BoundMinutes = 720

  [<RequireQualifiedAccess>]
  type Op =
    | Advance of minutes: int
    /// An agent connection `owner` (1 to 3) creates session `session` (1 to 4). Ignored if the session exists.
    | AgentCreates of session: int * owner: int
    /// A person creates session `session`: no ledger record.
    | PersonCreates of session: int
    | OwnerArrives of owner: int
    | OwnerLeaves of owner: int
    /// Someone uses the session: its last activity is now.
    | Use of session: int
    /// The daemon restarts: a new reaper that knows nothing, and a tracker with nobody in it until they act again.
    | RestartDaemon
    | Sweep

  type Scenario = { Seed: int; Ops: Op list }

  [<RequireQualifiedAccess>]
  type MadeBy =
    | Person
    | Agent of owner: int

  type Sess = { Id: int; MadeBy: MadeBy; LastUse: DateTime }

  type World =
    { Now: DateTime
      DaemonStart: DateTime
      Reaper: Reaper
      Sessions: Map<int, Sess>
      /// Who the daemon's presence tracker has in it. Emptied by a restart.
      Connected: Set<int>
      /// Truth: when each owner was last seen connected at a sweep.
      TruthSeen: Map<int, DateTime> }

  /// One op's end.
  type Snapshot =
    { Op: Op
      Now: DateTime
      /// Sessions the reaper stopped in this op (Sweep only).
      Reaped: Set<int>
      /// Truth: sessions that are abandoned at this sweep.
      Due: Set<int>
      /// Truth: person-made sessions alive at this sweep.
      PersonMade: Set<int>
      /// Truth: sessions whose owner was connected at this sweep.
      OwnerHere: Set<int>
      /// Truth: the daemon has been up for less than the bound.
      InGrace: bool
      /// Sessions still alive after the op.
      Alive: Set<int> }

  type Trace =
    { Scenario: Scenario
      Snapshots: Snapshot list
      Reducer: string }

  [<RequireQualifiedAccess>]
  type private Policy =
    /// The real behaviour.
    | Real
    /// TWIN: the reaper is given the epoch as the daemon's start, so a restart no longer delays a reap.
    | ForgetsTheRestart
    /// TWIN: presence is ignored, so a connected owner looks gone.
    | IgnoresPresence
    /// TWIN: every session is treated as an agent's, so a person's idle session is reaped too.
    | TreatsEverySessionAsAgents

  let private t0 = DateTime(2026, 10, 17, 12, 0, 0, DateTimeKind.Utc)

  let private bound : TimeSpan = TimeSpan.FromMinutes (float BoundMinutes)

  let private sessionId (n: int) : string = sprintf "aa%06x" n

  let private connectionId (owner: int) : string = sprintf "conn-%d" owner

  let private info (sess: Sess) : WorkerProtocol.SessionInfo =
    { Id =
        (match WorkerProtocol.SessionId.validate (sessionId sess.Id) with
         | Result.Ok id -> id
         | Result.Error e -> failwithf "sim session id: %s" e)
      Name = None
      Projects = []
      WorkingDirectory = sprintf "/sim/s%d" sess.Id
      SolutionRoot = None
      CreatedAt = t0
      LastActivity = sess.LastUse
      Status = WorkerProtocol.SessionLifecycleStatus.Ready { Pid = 100; Port = None }
      Workflow = WorkflowTypes.SessionWorkflow.Interactive
      ActiveProject = None
      ProjectRoles = []
      App = AppRun.AppRunState.NotRunning
      Rebuild = LastRebuild.NeverRebuilt
      Reload = SessionReload.NoReloadYet
      Freshness = ReplFreshness.InSync }

  let private freshReaper (policy: Policy) (daemonStart: DateTime) : Reaper =
    match policy with
    | Policy.ForgetsTheRestart -> Reaper(DateTime.MinValue, Bound.After bound)
    | Policy.Real
    | Policy.IgnoresPresence
    | Policy.TreatsEverySessionAsAgents -> Reaper(daemonStart, Bound.After bound)

  let private initial (policy: Policy) : World =
    { Now = t0
      DaemonStart = t0
      Reaper = freshReaper policy t0
      Sessions = Map.empty
      Connected = Set.empty
      TruthSeen = Map.empty }

  /// The ledger the daemon reads: one record per agent-made session. The twin that treats every session as an agent's
  /// invents a record for a person's session too.
  let private makersOf (policy: Policy) (w: World) : Maker list =
    w.Sessions
    |> Map.toList
    |> List.choose (fun (id, s) ->
      match s.MadeBy, policy with
      | MadeBy.Agent owner, _ -> Some { SessionId = sessionId id; AgentName = sprintf "agent-%d" owner; ConnectionId = connectionId owner }
      | MadeBy.Person, Policy.TreatsEverySessionAsAgents -> Some { SessionId = sessionId id; AgentName = "invented"; ConnectionId = "conn-nobody" }
      | MadeBy.Person, _ -> None)

  /// Truth: the sessions that are abandoned right now, from the sim's own bookkeeping.
  let private due (w: World) : Set<int> =
    w.Sessions
    |> Map.toList
    |> List.choose (fun (id, s) ->
      match s.MadeBy with
      | MadeBy.Person -> None
      | MadeBy.Agent owner ->
        match Set.contains owner w.Connected with
        | true -> None
        | false ->
          let seen = Map.tryFind owner w.TruthSeen |> Option.defaultValue w.DaemonStart
          let goneSince = max seen w.DaemonStart
          let idleSince = max s.LastUse w.DaemonStart
          match w.Now - goneSince >= bound && w.Now - idleSince >= bound with
          | true -> Some id
          | false -> None)
    |> Set.ofList

  let private step (policy: Policy) (w: World) (op: Op) : World * Set<int> =
    match op with
    | Op.Advance minutes -> { w with Now = w.Now + TimeSpan.FromMinutes (float minutes) }, Set.empty
    | Op.AgentCreates(session, owner) ->
      match Map.containsKey session w.Sessions with
      | true -> w, Set.empty
      | false ->
        // Making a session is a tool call: the owner is here, and it is the session's first use.
        { w with
            Sessions = Map.add session { Id = session; MadeBy = MadeBy.Agent owner; LastUse = w.Now } w.Sessions
            Connected = Set.add owner w.Connected },
        Set.empty
    | Op.PersonCreates session ->
      match Map.containsKey session w.Sessions with
      | true -> w, Set.empty
      | false -> { w with Sessions = Map.add session { Id = session; MadeBy = MadeBy.Person; LastUse = w.Now } w.Sessions }, Set.empty
    | Op.OwnerArrives owner -> { w with Connected = Set.add owner w.Connected }, Set.empty
    | Op.OwnerLeaves owner -> { w with Connected = Set.remove owner w.Connected }, Set.empty
    | Op.Use session ->
      match Map.tryFind session w.Sessions with
      | None -> w, Set.empty
      | Some s -> { w with Sessions = Map.add session { s with LastUse = w.Now } w.Sessions }, Set.empty
    | Op.RestartDaemon ->
      { w with
          DaemonStart = w.Now
          Reaper = freshReaper policy w.Now
          Connected = Set.empty
          TruthSeen = Map.empty },
      Set.empty
    | Op.Sweep ->
      let owners = w.Sessions |> Map.toList |> List.choose (fun (_, s) -> match s.MadeBy with | MadeBy.Agent o -> Some o | MadeBy.Person -> None)
      // Truth: a sweep sees the connected owners of live sessions.
      let seen =
        owners
        |> List.filter (fun o -> Set.contains o w.Connected)
        |> List.fold (fun acc o -> Map.add o w.Now acc) w.TruthSeen
      let w' = { w with TruthSeen = seen }
      let isConnected (connection: string) =
        match policy with
        | Policy.IgnoresPresence -> false
        | _ -> w'.Connected |> Set.exists (fun o -> connectionId o = connection)
      let reapedIds = System.Collections.Generic.List<string>()
      let stop (sid: string) : Task<Result<string, SageFsError>> =
        reapedIds.Add sid
        Task.FromResult(Result.Ok "stopped")
      let sessions = w'.Sessions |> Map.toList |> List.map (snd >> info)
      let outcome = (w'.Reaper.SweepAsync(w'.Now, makersOf policy w', isConnected, stop, sessions)).Result
      match outcome with
      | SweepOutcome.AlreadySweeping -> failwith "the sim never overlaps sweeps"
      | SweepOutcome.Swept _ -> ()
      let gone = w'.Sessions |> Map.filter (fun id _ -> reapedIds.Contains(sessionId id) |> not)
      let removed = w'.Sessions |> Map.toList |> List.choose (fun (id, _) -> match reapedIds.Contains(sessionId id) with | true -> Some id | false -> None)
      { w' with Sessions = gone }, Set.ofList removed

  let private snapshot (op: Op) (before: World) (after: World) (reaped: Set<int>) : Snapshot =
    // `before` carries the sweep's truth: the sessions as they were when it started, with the owners it saw.
    let seenAtSweep =
      match op with
      | Op.Sweep ->
        before.Sessions
        |> Map.toList
        |> List.choose (fun (_, s) -> match s.MadeBy with | MadeBy.Agent o when Set.contains o before.Connected -> Some o | _ -> None)
        |> List.fold (fun acc o -> Map.add o before.Now acc) before.TruthSeen
      | _ -> before.TruthSeen
    let atSweep = { before with TruthSeen = seenAtSweep }
    let isSweep = match op with | Op.Sweep -> true | _ -> false
    { Op = op
      Now = after.Now
      Reaped = reaped
      Due = (match isSweep with | true -> due atSweep | false -> Set.empty)
      PersonMade =
        before.Sessions |> Map.toList |> List.choose (fun (id, s) -> match s.MadeBy with | MadeBy.Person -> Some id | MadeBy.Agent _ -> None) |> Set.ofList
      OwnerHere =
        before.Sessions
        |> Map.toList
        |> List.choose (fun (id, s) -> match s.MadeBy with | MadeBy.Agent o when Set.contains o before.Connected -> Some id | _ -> None)
        |> Set.ofList
      InGrace = (before.Now - before.DaemonStart) < bound
      Alive = after.Sessions |> Map.toList |> List.map fst |> Set.ofList }

  let private runWith (name: string) (policy: Policy) (scenario: Scenario) : Trace =
    let _, snapshots =
      scenario.Ops
      |> List.fold
        (fun (w, acc) op ->
          let w', reaped = step policy w op
          w', snapshot op w w' reaped :: acc)
        (initial policy, [])
    { Scenario = scenario; Snapshots = List.rev snapshots; Reducer = name }

  /// The real reaper.
  let run (scenario: Scenario) : Trace = runWith "real (Reaper.SweepAsync)" Policy.Real scenario

  /// TWIN: the daemon's start is forgotten.
  let runForgetsTheRestart (scenario: Scenario) : Trace = runWith "twin-forgets-the-restart" Policy.ForgetsTheRestart scenario

  /// TWIN: presence is ignored.
  let runIgnoresPresence (scenario: Scenario) : Trace = runWith "twin-ignores-presence" Policy.IgnoresPresence scenario

  /// TWIN: a person's session is treated as an agent's.
  let runTreatsEverySessionAsAgents (scenario: Scenario) : Trace =
    runWith "twin-treats-every-session-as-agents" Policy.TreatsEverySessionAsAgents scenario
