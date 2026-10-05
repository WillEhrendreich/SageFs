namespace SageFs.Simulation

open SageFs
open SageFs.Cohort
open SageFs.Features
open SageFs.Features.TrunkFollow

/// Deterministic Simulation Testing for the claim the trunk makes: "what the cohort lands reaches the trunk session's running app,
/// in the order it landed, once, and the record says what really happened to the app".
///
/// Landings land, are refused, or are still being verified. The trunk checkout moves, or does not. A worker answers late, or not
/// at all, and says it patched, or that it restarted. The patched code runs, in a request nobody scheduled. All of these arrive in
/// any order; the fold under test is the real `TrunkFollow.step`, and everything else here is the world around it: which
/// landings really landed, which effects are really in flight, and what really happened to the app process.
///
/// Same design rules as `BuildConfirmationSim`:
///   * Chaos is DATA: a `Scenario` is an ordered op list, and the op order IS the scheduler. Same ops, same trace.
///   * Ground truth is tracked by an independent fold that never reads the machine under test.
///   * Twins put a bug back, so each invariant can be shown to have teeth.
module TrunkFollowSim =

  /// What a session in the trunk checkout is when the checkout is moved.
  [<RequireQualifiedAccess>]
  type Sessions =
    | Serving
    | NoApp
    | NoSession

  /// How moving the checkout ends.
  [<RequireQualifiedAccess>]
  type MoveEnds =
    | Moves of Sessions
    | Refuses

  /// What the worker's save pipeline does with the saves a landing hands it.
  [<RequireQualifiedAccess>]
  type WorkerDoes =
    /// Patches the running process: state kept, new body not seen running yet.
    | Patches
    /// Cannot patch, and restarts the app, naming why. The process goes, and its in-memory state with it.
    | Restarts
    /// Never answers, so the shell's bound is what ends the wait.
    | GoesSilent

  [<RequireQualifiedAccess>]
  type Op =
    /// The cohort records a landing as landed.
    | Land
    /// A landing is being verified: the cohort says so, and it has not landed.
    | Verify
    /// A verified landing is blocked: it never lands.
    | Refuse
    /// An in-flight move of the trunk checkout ends (`which` picks among those in flight, modulo how many).
    | MoveDone of which: int * MoveEnds
    /// A worker runs the save pipeline for a delivery in flight, and puts its answer on the wire.
    | WorkerRuns of which: int * WorkerDoes
    /// An answer on the wire reaches the daemon.
    | AnswerArrives of which: int
    /// A request runs a patched body (`which` picks among the patches waiting), and the worker reports the save settled.
    | BodyRuns of which: int

  type Scenario = { Seed: int; Ops: Op list }

  /// The one trunk session.
  let session = "trunk-session"

  /// The file every landing changes in the trunk.
  let landedFile = { Path = "/trunk/Handlers.fs"; Kind = SaveKind.Changed }

  let landingId (n: int) = LandingId (sprintf "l-%d" n)

  /// A landing that is only ever verified, then refused: its own namespace, so it can never be mistaken for one that landed.
  let refusedId (n: int) = LandingId (sprintf "x-%d" n)

  let commitOf (n: int) = sprintf "c-%d" n

  let private patchFacts : ReloadFacts =
    { Case = ReloadCase.PatchPending
      Patched = 0
      Considered = 1
      Message = "applied; the new body has not run yet"
      SuggestedAction = ""
      Mechanism = SageFs.Features.ReloadOutcome.PatchMechanism.MetadataDelta
      Declarations = []
      Callers = SageFs.Features.CallerState.CallersState.CallersCurrent }

  let private settledFacts : ReloadFacts = { patchFacts with Case = ReloadCase.Patched; Patched = 1; Message = "the new body ran" }

  let private restartFacts : ReloadFacts =
    { Case = ReloadCase.Restarted
      Patched = 0
      Considered = 1
      Message = "restarted: a virtual member changed its signature"
      SuggestedAction = ""
      Mechanism = SageFs.Features.ReloadOutcome.PatchMechanism.NoPatch
      Declarations = []
      Callers = SageFs.Features.CallerState.CallersState.CallersCurrent }

  let private restartCause : RestartCause = { Case = "VirtualSignatureChanged"; Message = "Shape.Name changed its signature" }

  /// What really happened to the app process in one delivery. Not what the machine was told: what the world did.
  [<RequireQualifiedAccess>]
  type Truth =
    /// The process and its in-memory state carried on.
    | StateKept
    /// The process was replaced.
    | Restarted

  /// An answer on the wire, with the world's own account of what it did.
  type private Answer = { Landing: LandedLanding; Files: SavedFile list; Outcome: SessionOutcome; Truth: Truth option }

  /// One step of the fold, with the world as it was.
  type Step =
    { Op: Op
      /// The trunk event the op raised, if it raised one.
      Event: TrunkEvent option
      Before: TrunkMachine
      After: TrunkMachine
      Effects: TrunkEffect list
      /// Landings the cohort had recorded as landed once this op had run, oldest first.
      LandedSoFar: LandingId list }

  type Trace =
    { Scenario: Scenario
      Steps: Step list
      Final: TrunkMachine
      /// Landings that landed, oldest first.
      Landed: LandingId list
      /// What really happened to the app in each delivery.
      Truths: Map<LandingId, Truth>
      /// How many reports of a settled patch the world sent.
      Settled: int
      /// Patches that never ran their new body, at the end.
      StillPending: int
      Reducer: string }

  [<RequireQualifiedAccess>]
  type private Policy =
    /// The real decision.
    | Real
    /// TWIN: a landing is acted on as soon as it is being verified, before it has landed.
    | AppliesUnlanded
    /// TWIN: queued landings are followed newest first.
    | ReordersLandings
    /// TWIN: a landing that lands while another is being followed is dropped.
    | DropsWhileBusy
    /// TWIN: a restart is recorded as a patch.
    | HidesRestarts
    /// TWIN: a patch is recorded as settled the moment it is applied.
    | PromotesPending
    /// TWIN: a report that a patch settled, arriving before the answer that carries the patch, is dropped.
    | DropsEarlyReports

  type private World =
    { Machine: TrunkMachine
      Landed: LandingId list
      Verifying: int
      Refused: int
      Moves: LandedLanding list
      Delivers: (LandedLanding * SavedFile list) list
      Wire: Answer list
      Waiting: int
      Truths: Map<LandingId, Truth>
      Settled: int
      Steps: Step list }

  let private initial : World =
    { Machine = TrunkFollow.initial
      Landed = []
      Verifying = 0
      Refused = 0
      Moves = []
      Delivers = []
      Wire = []
      Waiting = 0
      Truths = Map.empty
      Settled = 0
      Steps = [] }

  /// The cohort event a trunk event comes from, under the policy.
  let private eventOfCohort (policy: Policy) (event: CohortEvent<string>) : TrunkEvent option =
    match policy, event with
    | Policy.AppliesUnlanded, CohortEvent.LandingStateChanged (id, LandingState.Verifying (_, rebased, _, _)) ->
      Some (TrunkEvent.Landed { Landing = id; Commit = rebased })
    | _ -> TrunkFollow.ofCohortEvent event

  let private hideRestart (outcome: SessionOutcome) : SessionOutcome =
    match outcome with
    | SessionOutcome.Delivered verdicts ->
      SessionOutcome.Delivered
        (verdicts
         |> List.map (fun v ->
           match v.Outcome with
           | FileOutcome.Reloaded (facts, _) when facts.Case = ReloadCase.Restarted -> { v with Outcome = FileOutcome.Reloaded (patchFacts, []) }
           | _ -> v))
    | other -> other

  let private promote (outcome: SessionOutcome) : SessionOutcome =
    match outcome with
    | SessionOutcome.Delivered verdicts ->
      SessionOutcome.Delivered
        (verdicts
         |> List.map (fun v ->
           match v.Outcome with
           | FileOutcome.Reloaded (facts, causes) when facts.Case = ReloadCase.PatchPending ->
             { v with Outcome = FileOutcome.Reloaded ({ facts with Case = ReloadCase.Patched; Patched = facts.Considered }, causes) }
           | _ -> v))
    | other -> other

  /// The policy's version of the fold: the real step, or the real step with a bug put back.
  let private stepWith (policy: Policy) (machine: TrunkMachine) (event: TrunkEvent) : TrunkMachine * TrunkEffect list =
    match policy, event with
    | Policy.DropsWhileBusy, TrunkEvent.Landed _ when machine.Phase <> Phase.Idle -> machine, []
    | Policy.HidesRestarts, TrunkEvent.Answered (id, s, outcome) -> TrunkFollow.step machine (TrunkEvent.Answered (id, s, hideRestart outcome))
    | Policy.PromotesPending, TrunkEvent.Answered (id, s, outcome) -> TrunkFollow.step machine (TrunkEvent.Answered (id, s, promote outcome))
    | Policy.DropsEarlyReports, TrunkEvent.ReloadReported _ ->
      match machine.Phase with
      | Phase.Delivering _ -> machine, []
      | Phase.Idle
      | Phase.Moving _ -> TrunkFollow.step machine event
    | Policy.ReordersLandings, TrunkEvent.Landed _ ->
      // The queue is kept newest first: the real step appends at the end, so reverse around it, and it takes from the head.
      let next, effects = TrunkFollow.step { machine with Queued = List.rev machine.Queued } event
      { next with Queued = List.rev next.Queued }, effects
    | _ -> TrunkFollow.step machine event

  let private nth (index: int) (items: 'a list) : 'a option =
    match items with
    | [] -> None
    | _ -> Some (List.item (((index % List.length items) + List.length items) % List.length items) items)

  let private removeAt (index: int) (items: 'a list) : 'a list =
    match items with
    | [] -> []
    | _ ->
      let i = ((index % List.length items) + List.length items) % List.length items
      List.take i items @ List.skip (i + 1) items

  /// Fold the effects the machine asked for into what is really in flight.
  let private applyEffects (world: World) (effects: TrunkEffect list) : World =
    effects
    |> List.fold
      (fun w effect ->
        match effect with
        | TrunkEffect.MoveTrunk landing -> { w with Moves = w.Moves @ [ landing ] }
        | TrunkEffect.Deliver (landing, _, files) -> { w with Delivers = w.Delivers @ [ landing, files ] })
      world

  let private feed (policy: Policy) (world: World) (op: Op) (event: TrunkEvent option) (landed: LandingId list) : World =
    match event with
    | None -> { world with Landed = landed; Steps = { Op = op; Event = None; Before = world.Machine; After = world.Machine; Effects = []; LandedSoFar = landed } :: world.Steps }
    | Some e ->
      let after, effects = stepWith policy world.Machine e
      let step = { Op = op; Event = Some e; Before = world.Machine; After = after; Effects = effects; LandedSoFar = landed }
      let world' = applyEffects { world with Machine = after; Landed = landed } effects
      { world' with Steps = step :: world'.Steps }

  let private runOp (policy: Policy) (world: World) (op: Op) : World =
    match op with
    | Op.Land ->
      let n = List.length world.Landed + 1
      let id = landingId n
      let event = CohortEvent<string>.LandingLanded (id, commitOf n)
      feed policy world op (eventOfCohort policy event) (world.Landed @ [ id ])
    | Op.Verify ->
      let n = world.Refused + 1
      let id = refusedId n
      let event = CohortEvent<string>.LandingStateChanged (id, LandingState.Verifying ("head", commitOf (100 + n), 1, 1))
      feed policy { world with Verifying = world.Verifying + 1 } op (eventOfCohort policy event) world.Landed
    | Op.Refuse ->
      let n = world.Refused + 1
      let id = refusedId n
      let event = CohortEvent<string>.LandingStateChanged (id, LandingState.Blocked (LandingBlocker.FailingTests [], NextAction.Withdraw))
      feed policy { world with Refused = n } op (eventOfCohort policy event) world.Landed
    | Op.MoveDone (which, ends) ->
      match nth which world.Moves with
      | None -> feed policy world op None world.Landed
      | Some landing ->
        let move =
          match ends with
          | MoveEnds.Refuses -> TrunkMove.NotMoved "the checkout refused"
          | MoveEnds.Moves Sessions.Serving -> TrunkMove.Moved ([ landedFile ], [ { Session = session; State = TrunkSessionState.Serving } ])
          | MoveEnds.Moves Sessions.NoApp -> TrunkMove.Moved ([ landedFile ], [ { Session = session; State = TrunkSessionState.NoRunningApp } ])
          | MoveEnds.Moves Sessions.NoSession -> TrunkMove.Moved ([ landedFile ], [])
        let removed = { world with Moves = removeAt which world.Moves }
        feed policy removed op (Some (TrunkEvent.Moved (landing.Landing, move))) world.Landed
    | Op.WorkerRuns (which, does) ->
      match nth which world.Delivers with
      | None -> feed policy world op None world.Landed
      | Some (landing, files) ->
        let removed = { world with Delivers = removeAt which world.Delivers }
        let verdict (facts: ReloadFacts) (causes: RestartCause list) : SessionOutcome =
          SessionOutcome.Delivered [ { File = landedFile.Path; Outcome = FileOutcome.Reloaded (facts, causes) } ]
        match does with
        | WorkerDoes.Patches ->
          let answer = { Landing = landing; Files = files; Outcome = verdict patchFacts []; Truth = Some Truth.StateKept }
          feed policy { removed with Wire = removed.Wire @ [ answer ]; Waiting = removed.Waiting + 1; Truths = Map.add landing.Landing Truth.StateKept removed.Truths } op None world.Landed
        | WorkerDoes.Restarts ->
          let answer = { Landing = landing; Files = files; Outcome = verdict restartFacts [ restartCause ]; Truth = Some Truth.Restarted }
          feed policy { removed with Wire = removed.Wire @ [ answer ]; Truths = Map.add landing.Landing Truth.Restarted removed.Truths } op None world.Landed
        | WorkerDoes.GoesSilent ->
          // The shell bounds every wait for a worker, so silence reaches the machine as an answer that says so.
          let answer = { Landing = landing; Files = files; Outcome = SessionOutcome.Unreachable "no answer within the budget"; Truth = None }
          feed policy { removed with Wire = removed.Wire @ [ answer ] } op None world.Landed
    | Op.AnswerArrives which ->
      match nth which world.Wire with
      | None -> feed policy world op None world.Landed
      | Some answer ->
        let removed = { world with Wire = removeAt which world.Wire }
        feed policy removed op (Some (TrunkEvent.Answered (answer.Landing.Landing, session, answer.Outcome))) world.Landed
    | Op.BodyRuns _ ->
      match world.Waiting with
      | 0 -> feed policy world op None world.Landed
      | n ->
        feed policy { world with Waiting = n - 1; Settled = world.Settled + 1 } op (Some (TrunkEvent.ReloadReported (session, settledFacts))) world.Landed

  /// The default way each thing in flight ends, so a drained world has nothing waiting: the checkout moves to a serving session,
  /// the worker patches, the answer arrives, and the patched code runs.
  let private drainOps (world: World) : Op list =
    [ yield! world.Moves |> List.map (fun _ -> Op.MoveDone (0, MoveEnds.Moves Sessions.Serving))
      yield! world.Delivers |> List.map (fun _ -> Op.WorkerRuns (0, WorkerDoes.Patches))
      yield! world.Wire |> List.map (fun _ -> Op.AnswerArrives 0)
      yield! List.replicate world.Waiting (Op.BodyRuns 0) ]

  let private runWith (name: string) (policy: Policy) (scenario: Scenario) : Trace =
    let folded = scenario.Ops |> List.fold (runOp policy) initial
    // Completing one thing can put another in flight (a move starts a delivery, an answer closes a landing and starts the next
    // move), so drain until nothing is left, bounded by what the scenario could have started.
    let rec drain (world: World) (rounds: int) =
      match drainOps world, rounds with
      | [], _
      | _, 0 -> world
      | ops, _ -> drain (ops |> List.fold (runOp policy) world) (rounds - 1)
    let final = drain folded (4 * (List.length scenario.Ops + 4))
    { Scenario = scenario
      Steps = List.rev final.Steps
      Final = final.Machine
      Landed = final.Landed
      Truths = final.Truths
      Settled = final.Settled
      StillPending = final.Waiting
      Reducer = name }

  /// The real decision.
  let run (scenario: Scenario) : Trace = runWith "real (TrunkFollow.step)" Policy.Real scenario

  /// TWIN: a landing is followed as soon as it is being verified.
  let runAppliesUnlanded (scenario: Scenario) : Trace = runWith "twin-applies-unlanded" Policy.AppliesUnlanded scenario

  /// TWIN: queued landings are followed newest first.
  let runReordersLandings (scenario: Scenario) : Trace = runWith "twin-reorders-landings" Policy.ReordersLandings scenario

  /// TWIN: a landing that lands while another is being followed is lost.
  let runDropsWhileBusy (scenario: Scenario) : Trace = runWith "twin-drops-while-busy" Policy.DropsWhileBusy scenario

  /// TWIN: a restart is recorded as a patch.
  let runHidesRestarts (scenario: Scenario) : Trace = runWith "twin-hides-restarts" Policy.HidesRestarts scenario

  /// TWIN: a patch is recorded as settled before its new body has run.
  let runPromotesPending (scenario: Scenario) : Trace = runWith "twin-promotes-pending" Policy.PromotesPending scenario

  /// TWIN: a report of a settled patch that arrives before its answer is dropped.
  let runDropsEarlyReports (scenario: Scenario) : Trace = runWith "twin-drops-early-reports" Policy.DropsEarlyReports scenario
