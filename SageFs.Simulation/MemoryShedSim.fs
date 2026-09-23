namespace SageFs.Simulation

open System
open SageFs
open SageFs.MemorySupervisor

/// Deterministic Simulation Testing for `MemorySupervisor`: a seeded lifecycle
/// of sessions being created, touched, faulted, hung and stopped, while the
/// machine's own memory pressure rises and falls, folded through the REAL
/// `MemorySupervisor.step` — same rules as the other DST harnesses here:
/// chaos is data (a seeded event list), the real decision function is the
/// subject, and a twin (a daemon that never reaps dead sessions) shows the
/// invariants actually have teeth: they FAIL against the twin, which is the
/// whole point of running them against it.
///
/// The model tracks exactly what the real daemon must: each session's
/// attributed bytes (retained state — recent output, adaptive bindings — the
/// thing Job 2 frees), whether its worker is alive, and the machine's total
/// memory picture. `DaemonRssBytes` in the derived `MachineStats` is the SUM
/// of every session's bytes STILL IN THE MODEL — a dead session's bytes stay
/// counted until something reaps it, exactly mirroring the real leak this
/// whole effort exists to close.
module MemoryShedSim =

  [<RequireQualifiedAccess>]
  type SimEvent =
    /// A client asks to create a session. Refused (not added) if the policy
    /// says `RefusingAdmission` at the moment of the request.
    | CreateSessionRequest of id: string * bytes: int64
    /// The session did something — resets its idle clock.
    | Touch of id: string
    | SetUserActive of id: string * active: bool
    /// The worker crashed (or exhausted its restart backoff) — no live
    /// worker, nothing coming back on its own. Its bytes stay attributed
    /// until something reaps it.
    | Fault of id: string
    /// `WorkerHealthProbe` reached its miss threshold and killed a hung
    /// worker — same end state as `Fault` for this model's purposes (see
    /// `WorkerHealthProbeTests.fs` for the probe's own decision logic,
    /// modeled here only by its OUTCOME, the same way `SupervisorSim`
    /// models the real mailbox loop's shape rather than importing it).
    | WorkerHungDetected of id: string
    | PassMinutes of int
    /// The daemon's periodic evaluation tick: run the policy and apply
    /// whatever it prescribes.
    | Tick
    /// Memory pressure from OUTSIDE this daemon rising or falling (another
    /// process on the box growing, or exiting).
    | ExternalMemoryDelta of bytes: int64

  type Scenario = { Seed: int; Thresholds: Thresholds; MachineTotal: int64; Events: SimEvent list }

  /// Which reducer applies a `Decision`'s actions to the model.
  [<RequireQualifiedAccess>]
  type SupervisorBehavior =
    | Real
    /// TWIN: applies StopIdleSessions and refusals normally, but NEVER
    /// reaps a dead session — its bytes stay attributed forever. This is
    /// the exact regression Job 2 exists to close: a faulted or stopped
    /// session's retained state (output, adaptive bindings) never freed.
    | NeverReapTwin

  type SessionModel = {
    Id: string
    Bytes: int64
    IsDead: bool
    LastActiveAt: TimeSpan
    IsUserActive: bool
  }

  /// One decision plus the EXACT inputs `MemorySupervisor.step` was called
  /// with — `LevelBefore`/`Machine`/`PreSessions` together are the full
  /// input to `step` (`Thresholds` is constant across a scenario). An
  /// invariant that wants to say "these two decisions saw identical inputs"
  /// has to compare all three: `PreSessions` alone can be equal (same
  /// session ids/statuses) while `Machine` differs (bytes/other-usage
  /// changed) or `LevelBefore` differs (a PRIOR decision's own output
  /// changed the level) — either one alone made an earlier version of
  /// `steady-state-ticks-dont-flap` fire a false positive.
  type DecisionRecord =
    { AtStep: int
      Clock: TimeSpan
      LevelBefore: MemoryPressure
      Machine: MachineStats
      PreSessions: SessionSnapshot list
      Decision: Decision }

  type State = {
    Step: int
    Clock: TimeSpan
    Sessions: Map<string, SessionModel>
    MachineTotal: int64
    OtherUsage: int64
    Level: MemoryPressure
    /// Every decision ever produced, oldest first — the trace invariants
    /// walk to check reap/stop/refuse targeting.
    Decisions: DecisionRecord list
    Refusals: (TimeSpan * string * string) list // clock, requested id, reason
    Admitted: Set<string>
  }

  let private thresholdsOf (scenario: Scenario) = scenario.Thresholds

  let initial (machineTotal: int64) : State = {
    Step = 0
    Clock = TimeSpan.Zero
    Sessions = Map.empty
    MachineTotal = machineTotal
    OtherUsage = 0L
    Level = MemoryPressure.Normal
    Decisions = []
    Refusals = []
    Admitted = Set.empty
  }

  /// A session touched at exactly `clock` (idleFor = 0) reads as freshly
  /// Active, not Idle — Idle only ever means "no activity for some stretch."
  let private snapshotOf (clock: TimeSpan) (sessions: Map<string, SessionModel>) : SessionSnapshot list =
    sessions
    |> Map.toList
    |> List.map (fun (id, s) ->
      let status =
        if s.IsDead then SessionMemoryStatus.Dead
        elif s.LastActiveAt = clock then SessionMemoryStatus.Active
        else SessionMemoryStatus.Idle(clock - s.LastActiveAt)
      { Id = id; Status = status; IsUserActive = s.IsUserActive })

  let private totalBytes (sessions: Map<string, SessionModel>) =
    sessions |> Map.toList |> List.sumBy (fun (_, s) -> s.Bytes)

  let private machineStatsOf (s: State) : MachineStats =
    let daemonRss = totalBytes s.Sessions
    let available = max 0L (s.MachineTotal - s.OtherUsage - daemonRss)
    { DaemonRssBytes = daemonRss; MachineAvailableBytes = available; MachineTotalBytes = s.MachineTotal }

  /// Evaluate the policy against the CURRENT state and apply its actions,
  /// per `behavior`. Returns the new state and the `Decision` that drove it.
  let private evaluateAndApply (behavior: SupervisorBehavior) (t: Thresholds) (s: State) : State * Decision =
    let pre = snapshotOf s.Clock s.Sessions
    let machine = machineStatsOf s
    let decision = step t s.Level machine pre
    let applyReap sessions =
      match behavior with
      | SupervisorBehavior.NeverReapTwin -> sessions
      | SupervisorBehavior.Real ->
        decision.Actions
        |> List.fold
          (fun acc action ->
            match action with
            | ShedAction.ReapDeadSessions ids -> ids |> List.fold (fun m id -> Map.remove id m) acc
            | _ -> acc)
          sessions
    let applyStop sessions =
      decision.Actions
      |> List.fold
        (fun acc action ->
          match action with
          | ShedAction.StopIdleSessions ids -> ids |> List.fold (fun m id -> Map.remove id m) acc
          | _ -> acc)
        sessions
    let sessions' = s.Sessions |> applyReap |> applyStop
    let record =
      { AtStep = s.Step; Clock = s.Clock; LevelBefore = s.Level; Machine = machine; PreSessions = pre; Decision = decision }
    { s with Sessions = sessions'; Level = decision.Level; Decisions = s.Decisions @ [ record ] }, decision

  let step (behavior: SupervisorBehavior) (t: Thresholds) (s: State) (ev: SimEvent) : State =
    let s = { s with Step = s.Step + 1 }
    match ev with
    | SimEvent.PassMinutes m -> { s with Clock = s.Clock + TimeSpan.FromMinutes(float m) }
    | SimEvent.ExternalMemoryDelta delta -> { s with OtherUsage = max 0L (s.OtherUsage + delta) }
    | SimEvent.Touch id ->
      match Map.tryFind id s.Sessions with
      | None -> s
      | Some session -> { s with Sessions = Map.add id { session with LastActiveAt = s.Clock } s.Sessions }
    | SimEvent.SetUserActive(id, active) ->
      match Map.tryFind id s.Sessions with
      | None -> s
      | Some session -> { s with Sessions = Map.add id { session with IsUserActive = active } s.Sessions }
    | SimEvent.Fault id
    | SimEvent.WorkerHungDetected id ->
      match Map.tryFind id s.Sessions with
      | None -> s
      | Some session -> { s with Sessions = Map.add id { session with IsDead = true } s.Sessions }
    | SimEvent.Tick ->
      let s', _ = evaluateAndApply behavior t s
      s'
    | SimEvent.CreateSessionRequest(id, bytes) ->
      let s', decision = evaluateAndApply behavior t s
      let refused =
        decision.Actions
        |> List.tryPick (function ShedAction.RefuseNewSessions reason -> Some reason | _ -> None)
      match refused with
      | Some reason -> { s' with Refusals = s'.Refusals @ [ (s'.Clock, id, reason) ] }
      | None ->
        let session : SessionModel = { Id = id; Bytes = bytes; IsDead = false; LastActiveAt = s'.Clock; IsUserActive = false }
        { s' with Sessions = Map.add id session s'.Sessions; Admitted = Set.add id s'.Admitted }

  /// Every state, oldest first, including the initial one — mirrors the
  /// other DST harnesses' `trace` shape.
  let trace (behavior: SupervisorBehavior) (scenario: Scenario) : State list =
    scenario.Events
    |> List.scan (step behavior (thresholdsOf scenario)) (initial scenario.MachineTotal)

  /// A pure function of `seed`: replaying a seed gives the identical trace.
  /// Sweeps creation, activity, faulting, hanging, ticking and external
  /// pressure changes so a sweep of seeds reliably exercises every event
  /// kind. A generous mix of `Tick`s is baked in deliberately — the policy
  /// only ever acts when the daemon evaluates it, exactly like the real
  /// periodic sweep / admission check.
  let scenarioOf (seed: int) : Scenario =
    let rng = Random seed
    let machineTotal = 40_000_000_000L + int64 (rng.Next(0, 25)) * 1_000_000_000L
    let n = 40 + rng.Next 80
    // Every CreateSessionRequest mints a FRESH id, never reused — exactly
    // like production, where session ids are unique hex tokens
    // (WorkerProtocol.SessionId) that are never recycled. Reusing a small
    // fixed pool of names here would let a `CreateSessionRequest` for an id
    // land in the SAME step that reaps that very id (it was Dead a moment
    // before) — a coincidence production can't have, and one that made
    // `dead-memory-is-released` misfire on a brand new, legitimately
    // admitted session that merely shared a name with an old, correctly
    // reaped one.
    let created = ResizeArray<string>()
    let mutable nextId = 1
    let pickCreated (rng: Random) : string option =
      match created.Count with
      | 0 -> None
      | n -> Some created.[rng.Next n]
    let events =
      [ for _ in 1 .. n ->
          match rng.Next 10 with
          | 0 | 1 ->
            let id = sprintf "s%d" nextId
            nextId <- nextId + 1
            created.Add id
            SimEvent.CreateSessionRequest(id, int64 (1 + rng.Next 8) * 1_000_000_000L)
          | 2 -> match pickCreated rng with Some id -> SimEvent.Touch id | None -> SimEvent.Tick
          | 3 -> match pickCreated rng with Some id -> SimEvent.SetUserActive(id, rng.Next 2 = 0) | None -> SimEvent.Tick
          | 4 -> match pickCreated rng with Some id -> SimEvent.Fault id | None -> SimEvent.Tick
          | 5 -> match pickCreated rng with Some id -> SimEvent.WorkerHungDetected id | None -> SimEvent.Tick
          | 6 | 7 -> SimEvent.PassMinutes(1 + rng.Next 40)
          | 8 -> SimEvent.ExternalMemoryDelta(int64 (rng.Next(-10, 11)) * 1_000_000_000L)
          | _ -> SimEvent.Tick ]
    // Guarantee at least one Tick after the last mutating event so the
    // "eventually reaped" invariant has something to check against —
    // otherwise a scenario could end mid-fault with no evaluation at all.
    { Seed = seed; Thresholds = defaultThresholds; MachineTotal = machineTotal; Events = events @ [ SimEvent.Tick ] }
