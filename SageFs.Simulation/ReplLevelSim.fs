namespace SageFs.Simulation

open SageFs
open SageFs.EvalActorDecision
open SageFs.Features.ReloadOutcome

/// Deterministic Simulation Testing for the session bringing its own FSI host up to the patched build.
///
/// One world holds both halves of the protocol, because the races are between them. The worker has an eval actor (the
/// REAL `EvalActorDecision.decide` answers a `Level`), a test-run gate (the REAL `TestRunGate` transitions) and the
/// handler that asks the gate and then the actor. The daemon holds the freshness state (the REAL `ReplFreshness`
/// transitions) and judges every answer by the pid it was asked of (the REAL `WorkerPostReady.RefetchGate.judgeReply`).
///
/// Chaos is DATA: a scenario is a seed and an ordered list of ops, each one atomic step of one party. The same seed gives the
/// same trace. Nothing in the fold reads a clock or does IO.
///
/// The invariants, each in `ReplLevelInvariants`:
///   L1  a level never resets the host while an eval is running or cancelling, or while a test run is on it
///   L2  a test run that arrives while a level is pending or under way is held and then served, never dropped and never run
///       on the host that is about to be reset
///   L3  a level never resets a host that has run an eval since it was built, or that an init script or a buffer's tests
///       defined something in
///   L4  an answer to a level ask is applied only if the worker it was asked of still serves the session
///   L5  the state is `InSync` only when the host was built at or after the latest save
///
/// Each twin breaks one of them, so a green run of the real fold means something: `runCancelsFirst` (today's
/// `HardResetSession`: cancel the running eval, then reset), `runCheckThenAct` (read the facts, act later),
/// `runAdmitsDuringLevel` (the gate lets a run in while a level is under way), `runIgnoresEvals`, `runNoPidJudge`
/// (applies a stale answer) and `runLevelsBlindly` (an answer clears the state although a later save reopened the gap).
module ReplLevelSim =

  /// One scripted step. Each is atomic: the real worker's handler and the actor are separate steps, which is where the races are.
  [<RequireQualifiedAccess>]
  type Op =
    /// A user eval starts on the host (the actor's `Submit`).
    | Submit
    /// The running eval finishes (the actor's `Finished`).
    | EvalFinished
    | Cancel
    /// A test run arrives at the worker's gate.
    | RunStart
    | RunEnd
    /// An `EvalLiveTestFile` defined tests in the host.
    | DefineDynamicTests
    /// The init script ran in the host and defined values (the worker started with one).
    | RunInitScript
    /// A metadata delta lands in the app.
    | Save
    /// The manager decides whether to ask the worker to level, and sends the ask.
    | DaemonAsks
    /// The worker's handler takes the oldest ask and asks the gate.
    | LevelBegin
    /// The actor handles the level message.
    | LevelAct
    /// The oldest answer reaches the manager.
    | AnswerArrives
    /// The worker is replaced by a new one (a new pid).
    | WorkerSwap
    /// Something else resets the worker's FSI host in place (a hard reset from an editor).
    | HostRebuilt

  type Scenario = { Seed: int; Ops: Op list }

  /// Where the worker's level handler is.
  [<RequireQualifiedAccess>]
  type Stage =
    | NoLevel
    /// The gate said runs are in flight; the level starts when the last leaves.
    | WaitingForRuns of askedPid: int
    /// The gate is `Leveling` (or, in a twin, the facts were read): the actor may act.
    | ReadyToAct of askedPid: int * readAtBegin: LevelVerdict option

  /// What the actor saw when it handled a level, taken at the moment it handled it.
  type ResetRecord =
    { ActivityAtAct: SessionActivity
      RunsOnHost: int
      EvalsSinceHost: int
      InitScriptDefined: bool
      DynamicTestsDefined: bool
      Decision: EvalDecision }

  /// One answer applied by the manager.
  type AppliedRecord =
    { AskedPid: int
      ServingPid: int
      Answer: LevelAnswer }

  type World =
    { // -- the worker
      WorkerPid: int
      Generation: SessionGeneration
      Activity: SessionActivity
      EvalsSinceHost: int
      InitScriptDefined: bool
      DynamicTestsDefined: bool
      Gate: GateStanding
      /// Runs admitted and not ended, with the host generation each was admitted on.
      RunsOnHost: SessionGeneration list
      /// Runs the gate held, waiting for a level to end.
      Held: int
      Stage: Stage
      /// Asks on their way to the worker, tagged with the pid they were sent to.
      Asks: int list
      /// Answers on their way to the manager, tagged with the pid that gave them.
      Answers: (int * LevelAnswer) list
      // -- the daemon
      Freshness: ReplFreshness
      LevelInFlight: LevelFlight
      // -- ground truth
      Saves: int
      HostBuiltAt: int
      RunsStarted: int
      RunsServed: int
      RunsDropped: int
      // -- what the invariants read
      Resets: ResetRecord list
      Applied: AppliedRecord list
      /// A run that was on the host when a reset replaced it.
      RunsKilledByReset: int }

  let initial : World =
    { WorkerPid = 100
      Generation = SessionGeneration.initial
      Activity = SessionActivity.Idle
      EvalsSinceHost = 0
      InitScriptDefined = false
      DynamicTestsDefined = false
      Gate = GateStanding.Idle
      RunsOnHost = []
      Held = 0
      Stage = Stage.NoLevel
      Asks = []
      Answers = []
      Freshness = ReplFreshness.InSync
      LevelInFlight = LevelFlight.NoLevelInFlight
      Saves = 0
      HostBuiltAt = 0
      RunsStarted = 0
      RunsServed = 0
      RunsDropped = 0
      Resets = []
      Applied = []
      RunsKilledByReset = 0 }

  /// The parts of the protocol a twin may swap. The real one is `real`.
  type Policy =
    { Name: string
      /// The gate's `enter`.
      Enter: GateStanding -> GateStanding * GateEntry
      /// What the actor decides when it handles a level, given its phase and the facts it reads. The real one is `decide`.
      DecideLevel: EvalPhase -> LevelOwned -> EvalDecision
      /// Whether the level handler moves the gate (`requestLevel`) before asking the actor. A twin that only reads it does not.
      GuardsWithGate: bool
      /// Whether the actor re-reads its facts when it handles the level, or acts on what the handler read earlier.
      ReReadsAtAct: bool
      /// Whether the manager judges an answer by the pid it was asked of.
      JudgesByPid: bool
      /// The state change an applied answer makes.
      ApplyAnswer: ReplFreshness -> LevelAnswer -> ReplFreshness
      /// Whether the runs a level held are served when it ends.
      ServesHeld: bool }

  let private servingStatus (pid: int) : SageFs.WorkerProtocol.SessionLifecycleStatus =
    SageFs.WorkerProtocol.SessionLifecycleStatus.Ready { Pid = pid; Port = None }

  let private deltaPending (n: int) : SessionReload =
    SessionReload.Finished
      { Case = ReloadCase.PatchPending
        Patched = 1
        Considered = 1
        Message = "m"
        SuggestedAction = ""
        Mechanism = PatchMechanism.MetadataDelta
        Declarations = [ sprintf "D.f%d" n ]
        Callers = SageFs.Features.CallerState.CallersState.CallersCurrent }

  let private ownedOf (w: World) : LevelOwned =
    { Holds =
        { EvalsSinceHost = w.EvalsSinceHost
          InitScript = (match w.InitScriptDefined with | true -> InitScript.Defined "init.fsx" | false -> InitScript.Absent) }
      Outside =
        { DynamicTests = (match w.DynamicTestsDefined with | true -> DynamicTests.Defined | false -> DynamicTests.Absent)
          TestRuns = TestRunGate.runsOf w.Gate } }

  let private phaseOf (w: World) : EvalPhase = EvalPhase.Active w.Activity

  /// The host replaced by a fresh one: nothing it held survives, and a run that was on it is lost.
  let private resetHost (w: World) : World =
    { w with
        Generation = SessionGeneration.next w.Generation
        Activity = SessionActivity.Idle
        EvalsSinceHost = 0
        InitScriptDefined = false
        DynamicTestsDefined = false
        RunsOnHost = []
        HostBuiltAt = w.Saves }

  /// The held runs enter again once the level has ended, in arrival order. Under the real gate every one is admitted.
  let private serveHeld (policy: Policy) (w: World) : World =
    let rec go (remaining: int) (w: World) =
      match remaining with
      | 0 -> w
      | _ ->
        let gate, entry = policy.Enter w.Gate
        match entry with
        | GateEntry.Admitted ->
          go (remaining - 1) { w with Gate = gate; Held = w.Held - 1; RunsOnHost = w.Generation :: w.RunsOnHost; RunsServed = w.RunsServed + 1 }
        | GateEntry.Held -> w
    go w.Held w

  let private answer (pid: int) (a: LevelAnswer) (w: World) : World = { w with Answers = w.Answers @ [ pid, a ] }

  /// What the actor does with a level it was asked to handle, and what the manager hears. Records what the actor saw.
  let private act (policy: Policy) (askedPid: int) (readAtBegin: LevelVerdict option) (w: World) : World =
    let owned = ownedOf w
    let decision =
      match policy.ReReadsAtAct, readAtBegin with
      | false, Some LevelVerdict.Level -> EvalDecision.RunLevel
      | false, Some (LevelVerdict.Deferred blockers) -> EvalDecision.DeferLevel blockers
      | _ -> policy.DecideLevel (phaseOf w) owned
    let record =
      { ActivityAtAct = w.Activity
        RunsOnHost = List.length w.RunsOnHost
        EvalsSinceHost = w.EvalsSinceHost
        InitScriptDefined = w.InitScriptDefined
        DynamicTestsDefined = w.DynamicTestsDefined
        Decision = decision }
    let w = { w with Resets = record :: w.Resets }
    let w =
      match decision with
      | EvalDecision.RunLevel ->
        // A run on the host a level replaces is lost by the level. (A caller's own hard reset is the caller's choice, and not counted.)
        let killed = List.length w.RunsOnHost
        let w = resetHost { w with RunsKilledByReset = w.RunsKilledByReset + killed }
        answer askedPid LevelAnswer.Leveled w
      | EvalDecision.DeferLevel blockers -> answer askedPid (LevelAnswer.LevelDeferred blockers) w
      | _ -> answer askedPid (LevelAnswer.LevelFailed "unexpected decision") w
    let w = { w with Gate = TestRunGate.finishLevel w.Gate; Stage = Stage.NoLevel }
    match policy.ServesHeld with
    | true -> serveHeld policy w
    | false -> w

  let private step (policy: Policy) (w: World) (op: Op) : World =
    match op with
    | Op.Submit ->
      match decide w.Generation (phaseOf w) EvalInput.Submit with
      | EvalDecision.RunEval -> { w with Activity = SessionActivity.Evaluating }
      | _ -> w
    | Op.EvalFinished ->
      match w.Activity with
      | SessionActivity.Evaluating
      | SessionActivity.Cancelling -> { w with Activity = activityAfterFinished w.Activity; EvalsSinceHost = w.EvalsSinceHost + 1 }
      | SessionActivity.Idle
      | SessionActivity.HostCrashed _ -> w
    | Op.Cancel ->
      match decide w.Generation (phaseOf w) EvalInput.Cancel with
      | EvalDecision.AckCancel -> { w with Activity = applyCancelAck w.Activity }
      | _ -> w
    | Op.RunStart ->
      let w = { w with RunsStarted = w.RunsStarted + 1 }
      let gate, entry = policy.Enter w.Gate
      match entry with
      | GateEntry.Admitted -> { w with Gate = gate; RunsOnHost = w.Generation :: w.RunsOnHost; RunsServed = w.RunsServed + 1 }
      | GateEntry.Held -> { w with Gate = gate; Held = w.Held + 1 }
    | Op.RunEnd ->
      match w.RunsOnHost with
      | [] -> w
      | _ :: rest ->
        let w = { w with RunsOnHost = rest }
        let gate, exit = TestRunGate.leave w.Gate
        let w = { w with Gate = gate }
        match exit, w.Stage with
        | GateExit.LevelMayStart, Stage.WaitingForRuns pid -> { w with Stage = Stage.ReadyToAct (pid, None) }
        | GateExit.Drained, _
        | GateExit.Left, _
        | GateExit.NotRunning, _
        | GateExit.LevelMayStart, _ -> w
    | Op.DefineDynamicTests -> { w with DynamicTestsDefined = true }
    | Op.RunInitScript -> { w with InitScriptDefined = true }
    | Op.Save ->
      let saves = w.Saves + 1
      { w with Saves = saves; Freshness = ReplFreshness.observe w.Freshness (deltaPending saves) }
    | Op.DaemonAsks ->
      let standing = LevelStanding.Servable w.WorkerPid
      let freshness, start = ReplFreshness.beginLeveling w.Freshness standing w.LevelInFlight
      match start with
      | LevelStart.StartLevel pid -> { w with Freshness = freshness; LevelInFlight = LevelFlight.LevelInFlight; Asks = w.Asks @ [ pid ] }
      | LevelStart.NoStart -> { w with Freshness = freshness }
    | Op.LevelBegin ->
      match w.Asks with
      | [] -> w
      | pid :: rest ->
        let w = { w with Asks = rest }
        match pid = w.WorkerPid, w.Stage with
        // An ask sent to a worker that has been replaced is gone with it.
        | false, _ -> w
        // The worker is already leveling for an earlier ask.
        | true, (Stage.WaitingForRuns _ | Stage.ReadyToAct _) -> answer pid (LevelAnswer.LevelDeferred [ LevelBlocker.LevelAlreadyRunning ]) w
        | true, Stage.NoLevel ->
          match policy.GuardsWithGate with
          | true ->
            let gate, request = TestRunGate.requestLevel w.Gate
            let w = { w with Gate = gate }
            match request with
            | GateLevelRequest.Start -> { w with Stage = Stage.ReadyToAct (pid, None) }
            | GateLevelRequest.WaitFor _ -> { w with Stage = Stage.WaitingForRuns pid }
            | GateLevelRequest.AlreadyRequested -> answer pid (LevelAnswer.LevelDeferred [ LevelBlocker.LevelAlreadyRunning ]) w
          | false ->
            // The check-then-act twin: read the facts now, act on them later, hold nothing in between.
            let verdict =
              match policy.DecideLevel (phaseOf w) (ownedOf w) with
              | EvalDecision.RunLevel -> LevelVerdict.Level
              | EvalDecision.DeferLevel blockers -> LevelVerdict.Deferred blockers
              | _ -> LevelVerdict.Deferred []
            { w with Stage = Stage.ReadyToAct (pid, Some verdict) }
    | Op.LevelAct ->
      match w.Stage with
      | Stage.ReadyToAct (pid, read) -> act policy pid read w
      | Stage.NoLevel
      | Stage.WaitingForRuns _ -> w
    | Op.AnswerArrives ->
      match w.Answers with
      | [] -> w
      | (askedPid, reply) :: rest ->
        let w = { w with Answers = rest }
        let fate = SageFs.WorkerPostReady.RefetchGate.judgeLevelAnswer askedPid (Some (servingStatus w.WorkerPid))
        let current =
          match policy.JudgesByPid, fate with
          | false, _ -> true
          | true, SageFs.WorkerPostReady.ReplyFate.Current -> true
          | true, SageFs.WorkerPostReady.ReplyFate.Stale -> false
        // The in-flight mark is the pid it was asked of's; an answer from a replaced worker never clears the current one's.
        let flight =
          match askedPid = w.WorkerPid with
          | true -> LevelFlight.NoLevelInFlight
          | false -> w.LevelInFlight
        match current with
        | false -> { w with LevelInFlight = flight }
        | true ->
          { w with
              Freshness = policy.ApplyAnswer w.Freshness reply
              LevelInFlight = flight
              Applied = { AskedPid = askedPid; ServingPid = w.WorkerPid; Answer = reply } :: w.Applied }
    | Op.WorkerSwap ->
      // A replaced worker is built from the current build, REPL included; what it was doing, and what was waiting on it, goes.
      { w with
          WorkerPid = w.WorkerPid + 1
          Generation = SessionGeneration.next w.Generation
          Activity = SessionActivity.Idle
          EvalsSinceHost = 0
          InitScriptDefined = false
          DynamicTestsDefined = false
          Gate = GateStanding.Idle
          RunsOnHost = []
          RunsDropped = w.RunsDropped + w.Held
          Held = 0
          Stage = Stage.NoLevel
          Asks = []
          Freshness = ReplFreshness.afterWorkerSwap w.Freshness
          LevelInFlight = LevelFlight.NoLevelInFlight
          HostBuiltAt = w.Saves }
    | Op.HostRebuilt ->
      // The caller's hard reset: the host is replaced whatever it held, and the manager fetches again and then says so.
      let rebuilt = resetHost w
      { rebuilt with
          Freshness = ReplFreshness.afterHostRebuilt rebuilt.Freshness
          LevelInFlight = LevelFlight.LevelInFlight
          Answers = rebuilt.Answers @ [ rebuilt.WorkerPid, LevelAnswer.Leveled ] }

  /// The protocol as the product has it.
  let real : Policy =
    { Name = "real"
      Enter = TestRunGate.enter
      DecideLevel = fun phase owned -> decide SessionGeneration.initial phase (EvalInput.Level owned)
      GuardsWithGate = true
      ReReadsAtAct = true
      JudgesByPid = true
      ApplyAnswer = ReplFreshness.afterLevelAnswer
      ServesHeld = true }

  /// TWIN 1: the router cancels the running eval and resets, as `HardResetSession` does today. A level that cancels first
  /// kills what it was meant to wait for.
  let runCancelsFirstPolicy : Policy =
    { real with
        Name = "twin-cancels-first"
        DecideLevel = fun _ _ -> EvalDecision.RunLevel }

  /// TWIN 2: the handler reads the facts, the actor acts on them later, and nothing holds the gate in between.
  let runCheckThenActPolicy : Policy =
    { real with Name = "twin-check-then-act"; GuardsWithGate = false; ReReadsAtAct = false }

  /// TWIN 3: the gate admits a run while a level is under way.
  let runAdmitsDuringLevelPolicy : Policy =
    { real with
        Name = "twin-admits-during-level"
        Enter =
          fun standing ->
            match standing with
            | GateStanding.Leveling | GateStanding.LevelPending _ -> standing, GateEntry.Admitted
            | other -> TestRunGate.enter other }

  /// TWIN 4: the actor does not count the evals the host has run.
  let runIgnoresEvalsPolicy : Policy =
    { real with
        Name = "twin-ignores-evals"
        DecideLevel = fun phase owned -> decide SessionGeneration.initial phase (EvalInput.Level { owned with Holds = { owned.Holds with EvalsSinceHost = 0 } }) }

  /// TWIN 5: an answer is applied whichever worker gave it.
  let runNoPidJudgePolicy : Policy = { real with Name = "twin-no-pid-judge"; JudgesByPid = false }

  /// TWIN 6: a `Leveled` answer clears the state although a later save reopened the gap.
  let runLevelsBlindlyPolicy : Policy =
    { real with
        Name = "twin-levels-blindly"
        ApplyAnswer =
          fun freshness reply ->
            match reply, freshness with
            | LevelAnswer.Leveled, ReplFreshness.BehindApp _ -> ReplFreshness.InSync
            | _ -> ReplFreshness.afterLevelAnswer freshness reply }

  /// TWIN 7: the runs a level held are never served when it ends.
  let runDropsHeldPolicy : Policy = { real with Name = "twin-drops-held"; ServesHeld = false }

  /// `States` is every world the fold passed through, the initial one first, so an invariant that must hold at every step
  /// is checked at every step and not only at the end.
  type Trace = { Scenario: Scenario; States: World list; Final: World; Reducer: string }

  let runWith (policy: Policy) (scenario: Scenario) : Trace =
    let states = scenario.Ops |> List.scan (step policy) initial
    { Scenario = scenario; States = states; Final = List.last states; Reducer = policy.Name }

  let run (scenario: Scenario) : Trace = runWith real scenario
