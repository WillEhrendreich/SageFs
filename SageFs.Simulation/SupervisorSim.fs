namespace SageFs.Simulation

open System

/// Phase 3 (B6) DST: "supervise the supervisor" — the SessionManager mailbox's
/// own restart-from-last-good semantics (`SessionManager.fs:1740-1832`:
/// `superviseStep` fail-closes per command, and the outer `supervise ()`
/// restarts `loop` from `lastGoodState.Value` if anything still escapes).
///
/// This is a MODEL of that shape, not an import of the production actor — per
/// the brief, the real mailbox loop is not extractable without a Core change,
/// so (exactly as Phase 1 modeled the RestartPolicy control flow rather than
/// wiring up the daemon) the checkpoint-before-each-command / restart-on-fault
/// discipline is faithfully reproduced here in miniature:
///
///   * `LastGood` mirrors `lastGoodState.Value` — the state captured just
///     before the loop begins processing the *next* command
///     (`SessionManager.fs:897-898`, `lastGoodState.Value <- state` runs
///     before `inbox.Receive()`).
///   * A `Poison` command models a handler exception. The real mailbox
///     absorbs it (either per-command via `superviseStep`'s guarded
///     try/with, or — if that guard itself is bypassed — via the outer
///     `supervise ()` restarting from `lastGoodState.Value`); either way the
///     net effect the roast cared about is the same: a fault never mutates
///     `Sessions` beyond what was already checkpointed, and the fault itself
///     is swallowed rather than propagating.
///   * `Cancel` models `OperationCanceledException` — the one case the real
///     supervisor does NOT restart from (`:? OperationCanceledException -> ()`
///     stops the supervisor outright) — an absorbing, terminal phase.
///
/// A second, `stepNoSupervisor`, reducer models the ABSENCE of this
/// supervision — an unsupervised mailbox whose loop simply dies on the first
/// unhandled exception, orphaning every session it was holding. The
/// `sessions-preserved-across-fault` invariant (`SupervisorInvariants.fs`)
/// HOLDS against the real (supervised) reducer and is VIOLATED against the
/// no-supervisor twin — the same twin-reducer teeth proof Phase 2 used for the
/// pid race.
module SupervisorSim =

  /// One simulated mailbox command touching the supervised session registry.
  [<RequireQualifiedAccess>]
  type SupCmd =
    /// A session is created/registered.
    | Register of id: int
    /// A session is stopped/removed.
    | Drop of id: int
    /// A handler throws while processing the in-flight command.
    | Poison
    /// The supervisor is cancelled (e.g. daemon shutdown) — terminal.
    | Cancel

  /// The supervisor loop's phase, as a DU so "running-but-cancelled" is
  /// unrepresentable — mirrors the real loop's cancellation being a distinct
  /// absorbing exit, never just another fault to restart from.
  [<RequireQualifiedAccess>]
  type SupPhase =
    | Running
    | Cancelled

  /// `Sessions` is the live registry; `LastGood` is the last checkpoint taken
  /// BEFORE the currently in-flight command started processing — exactly
  /// `lastGoodState.Value` at the top of `loop`. `LastGood` only ever advances
  /// on a successful Register/Drop; a Poison never advances it, so a restart
  /// after a Poison can only ever fall back to what was already committed.
  type SupState =
    { Sessions: Set<int>
      LastGood: Set<int>
      Phase: SupPhase }

  let initial : SupState =
    { Sessions = Set.empty; LastGood = Set.empty; Phase = SupPhase.Running }

  /// A fully-specified, replayable scenario. Same seed + same commands =>
  /// the identical trace.
  type SupScenario =
    { Seed: int
      Commands: SupCmd list }

  /// One folded step: the command and the state before/after it.
  type SupStep =
    { Index: int
      Command: SupCmd
      StateBefore: SupState
      StateAfter: SupState }

  type SupTrace =
    { Scenario: SupScenario
      Steps: SupStep list
      /// Which reducer produced this trace, for reporting (the real
      /// supervised model vs the no-supervisor twin).
      Reducer: string
      /// Number of Poison commands processed while still Running (before any
      /// Cancel) — harness bookkeeping threaded identically by both reducers,
      /// independently re-derivable from `Steps` (see
      /// `SupervisorInvariants.faultCountMatches`).
      FaultCount: int }

  // ── Reducers ──────────────────────────────────────────────────────────────

  /// The FAITHFUL model: Register/Drop commit and advance the checkpoint;
  /// Poison is absorbed without mutating Sessions beyond the last checkpoint
  /// (the in-flight command is lost, nothing already committed is); Cancel is
  /// terminal/absorbing.
  let stepReal (before: SupState) (cmd: SupCmd) : SupState =
    match before.Phase with
    | SupPhase.Cancelled -> before // terminal: absorbing
    | SupPhase.Running ->
      match cmd with
      | SupCmd.Register id ->
        let sessions' = Set.add id before.Sessions
        { before with Sessions = sessions'; LastGood = sessions' }
      | SupCmd.Drop id ->
        let sessions' = Set.remove id before.Sessions
        { before with Sessions = sessions'; LastGood = sessions' }
      | SupCmd.Poison ->
        // The handler threw; the supervised loop restarts from the last
        // checkpoint. The poisoned command itself never committed, so
        // Sessions falls back to exactly what was already good.
        { before with Sessions = before.LastGood }
      | SupCmd.Cancel ->
        { before with Phase = SupPhase.Cancelled }

  /// The TWIN: models an UNSUPERVISED mailbox — a handler exception kills the
  /// loop outright and every session it was holding is orphaned (nothing to
  /// restart from, no checkpoint honored).
  let stepNoSupervisor (before: SupState) (cmd: SupCmd) : SupState =
    match before.Phase with
    | SupPhase.Cancelled -> before
    | SupPhase.Running ->
      match cmd with
      | SupCmd.Register id ->
        let sessions' = Set.add id before.Sessions
        { before with Sessions = sessions'; LastGood = sessions' }
      | SupCmd.Drop id ->
        let sessions' = Set.remove id before.Sessions
        { before with Sessions = sessions'; LastGood = sessions' }
      | SupCmd.Poison ->
        // UNSUPERVISED: the loop dies and everything it held is gone — the
        // exact failure mode "supervise the supervisor" (SessionManager.fs
        // :1820-1830) was built to prevent.
        { before with Sessions = Set.empty; LastGood = Set.empty }
      | SupCmd.Cancel ->
        { before with Phase = SupPhase.Cancelled }

  /// Fold a scenario through a chosen step function, producing its trace and
  /// the independently-tracked fault count (Poison commands processed while
  /// still Running).
  let private runWith (name: string) (step: SupState -> SupCmd -> SupState) (scenario: SupScenario) : SupTrace =
    let folder (state, faults, steps) (idx, cmd) =
      let after = step state cmd
      let faults' =
        match cmd, state.Phase with
        | SupCmd.Poison, SupPhase.Running -> faults + 1
        | _ -> faults
      let st = { Index = idx; Command = cmd; StateBefore = state; StateAfter = after }
      (after, faults', st :: steps)
    let (_, faultCount, revSteps) =
      scenario.Commands
      |> List.mapi (fun i c -> (i, c))
      |> List.fold folder (initial, 0, [])
    { Scenario = scenario; Steps = List.rev revSteps; Reducer = name; FaultCount = faultCount }

  /// Run through the faithful supervised model — the subject under test.
  let run (scenario: SupScenario) : SupTrace = runWith "real-supervise-last-good" stepReal scenario

  /// Run through the no-supervisor twin — used to prove the invariant has
  /// teeth (it must FAIL here).
  let runNoSupervisor (scenario: SupScenario) : SupTrace = runWith "no-supervisor-drops-state" stepNoSupervisor scenario

  // ── Generators (seeded, dependency-free; mirrors Generators.fs/MgrGenerators.fs) ──

  /// A general scenario: a mixed stream of Register/Drop/Poison, with an
  /// occasional trailing Cancel so `cancel-is-terminal` gets exercised too.
  /// Pure function of the seed — `fromSeed n` replays identically forever.
  let fromSeed (seed: int) : SupScenario =
    let rnd = Random(seed)
    let mutable nextId = 1
    let cmds = ResizeArray<SupCmd>()
    let n = rnd.Next(3, 20)
    for _ in 1 .. n do
      match rnd.Next(0, 10) with
      | 0 | 1 | 2 | 3 ->
        let id = nextId
        nextId <- nextId + 1
        cmds.Add(SupCmd.Register id)
      | 4 | 5 when nextId > 1 -> cmds.Add(SupCmd.Drop(rnd.Next(1, nextId)))
      | 6 | 7 -> cmds.Add(SupCmd.Poison)
      | _ -> () // filler: no command this tick, keeps the distribution varied
    if rnd.Next(0, 3) = 0 then cmds.Add(SupCmd.Cancel)
    { Seed = seed; Commands = List.ofSeq cmds }

  // ── Named canonical worked scenarios ────────────────────────────────────

  /// Two sessions register, then a Poison hits. Real: the checkpoint
  /// preserves both, and a later Register still lands. No-supervisor twin:
  /// the Poison wipes everything — both prior sessions are orphaned.
  let registerThenPoison : SupScenario =
    { Seed = -301
      Commands =
        [ SupCmd.Register 1
          SupCmd.Register 2
          SupCmd.Poison
          SupCmd.Register 3 ] }

  /// A fault immediately after the very first registration — the minimal
  /// case where there is exactly one checkpointed session to lose or keep.
  let singleRegisterThenPoison : SupScenario =
    { Seed = -302
      Commands = [ SupCmd.Register 1; SupCmd.Poison ] }

  /// After Cancel, further commands must not mutate Sessions under either
  /// reducer — the control that shows `cancel-is-terminal` is not vacuous.
  let registerCancelThenRegister : SupScenario =
    { Seed = -303
      Commands = [ SupCmd.Register 1; SupCmd.Cancel; SupCmd.Register 2; SupCmd.Poison ] }

  /// A clean, fault-free lifecycle — holds under BOTH reducers, the control
  /// that shows the invariant is not just always green by accident.
  let cleanLifecycle : SupScenario =
    { Seed = -304
      Commands = [ SupCmd.Register 1; SupCmd.Register 2; SupCmd.Drop 1; SupCmd.Register 3; SupCmd.Cancel ] }
