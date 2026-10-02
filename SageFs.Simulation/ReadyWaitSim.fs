namespace SageFs.Simulation

open System
open SageFs
open SageFs.WorkerProtocol

/// Deterministic Simulation Testing for the claim a `wait_seconds` caller relies on: "when you
/// asked me to wait, I answered when the thing you waited for was done, and I said which way it ended".
///
/// Callers arrive and ask to wait, a rebuild starts, its build succeeds or fails, a worker reports
/// Ready, the session faults, and each caller's deadline passes, in any order. The fold is the REAL
/// decision (`ReadyWait.plan`, `ReadyWait.ofSession`, `ReadyWait.ofRebuildEnd`), the same functions
/// the session manager settles its parked callers with and the status tool decides to park with.
/// Everything else here is the world around them: what state the session is really in, which
/// callers are really parked, and what each was really told.
///
/// Same design rules as `BuildConfirmationSim`:
///   * Chaos is DATA: a `Scenario` is an ordered op list. Same ops, same trace. The op order IS the
///     scheduler: a `Deadline` before a `WorkerReady` means the caller gave up first.
///   * Ground truth is tracked by the world, never read back from the decision under test.
///   * Twins reintroduce the bugs the invariants exist to catch.
module ReadyWaitSim =

  /// What a caller is told. The set is closed, and mirrors the wire.
  [<RequireQualifiedAccess>]
  type Answer =
    | NotNeeded
    | BecameReady
    | Faulted
    | TimedOut

  /// How the session starts.
  [<RequireQualifiedAccess>]
  type Start =
    | Serving
    | Warming

  [<RequireQualifiedAccess>]
  type Op =
    /// A caller asks to wait.
    | Arrive
    /// A rebuild is requested and its build starts (refused while one runs, as the manager does).
    | StartRebuild
    /// The build finishes well: the replacement worker is spawning.
    | BuildSucceeds
    /// The build fails: the old worker keeps serving.
    | BuildFails
    /// A worker reports Ready.
    | WorkerReady
    /// The session faults.
    | Fault
    /// The deadline of the n-th parked caller (modulo how many are parked) passes.
    | Deadline of which: int
    /// A poll tick. Only the polling twin does anything with it.
    | Poll

  type Scenario = { Seed: int; Start: Start; Ops: Op list }

  type Waiter = { Id: int }

  /// What the world looked like and what happened in one step.
  type Step =
    { Op: Op
      Status: SessionLifecycleStatus
      Rebuild: LastRebuild
      /// The session just before the op.
      StatusBefore: SessionLifecycleStatus
      RebuildBefore: LastRebuild
      /// Callers parked after the op.
      Parked: int list
      /// Callers parked before the op.
      ParkedBefore: int list
      /// Who was told what in this op.
      Answered: (int * Answer) list }

  type Trace =
    { Scenario: Scenario
      Steps: Step list
      Reducer: string }

  [<RequireQualifiedAccess>]
  type private Policy =
    /// The real decisions.
    | Real
    /// TWIN: the decision of the day the bug was reported. A rebuild running is not a state anyone waits on.
    | ReturnsEarly
    /// TWIN: no event-driven settling. A parked caller is only looked at when a poll tick arrives.
    | Polls
    /// TWIN: a deadline never answers anyone.
    | NeverTimesOut
    /// TWIN: a caller parked through a failed rebuild is told the session is Ready.
    | ReadyAfterFailedRebuild

  type private World =
    { Status: SessionLifecycleStatus
      Rebuild: LastRebuild
      Parked: int list
      NextId: int
      Steps: Step list }

  let private worker : WorkerHandle = { Pid = 7; Port = Some 6000 }
  let private serving = SessionLifecycleStatus.Ready worker
  let private warming = SessionLifecycleStatus.Starting { Pid = 7; Port = None }
  let private replacementSpawning = SessionLifecycleStatus.Restarting (PreviousWorker.ColdStart)
  let private buildError = SageFsError.HardResetFailed "the build failed"

  let private at = DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)

  let private initial (start: Start) : World =
    { Status = (match start with | Start.Serving -> serving | Start.Warming -> warming)
      Rebuild = LastRebuild.NeverRebuilt
      Parked = []
      NextId = 0
      Steps = [] }

  let private isRebuilding (rebuild: LastRebuild) : bool =
    match rebuild with
    | LastRebuild.Latest (RebuildOutcome.InProgress _) -> true
    | LastRebuild.Latest _
    | LastRebuild.NeverRebuilt -> false

  let private isServing (status: SessionLifecycleStatus) : bool =
    match status with
    | SessionLifecycleStatus.Ready _
    | SessionLifecycleStatus.Evaluating _ -> true
    | SessionLifecycleStatus.Starting _
    | SessionLifecycleStatus.Building _
    | SessionLifecycleStatus.Faulted _
    | SessionLifecycleStatus.HostCrashed _
    | SessionLifecycleStatus.Restarting _
    | SessionLifecycleStatus.Stopped -> false

  /// The plan for a caller who arrives, by policy.
  let private planOf (policy: Policy) (status: SessionLifecycleStatus) (rebuild: LastRebuild) : ReadyWait.Plan =
    match policy with
    | Policy.ReturnsEarly ->
      // The old rule: only the lifecycle decides.
      match ReadyWait.ofSession status LastRebuild.NeverRebuilt with
      | ReadyWait.Verdict.KeepParked -> ReadyWait.Plan.Park
      | ReadyWait.Verdict.Ready
      | ReadyWait.Verdict.Failed _ -> ReadyWait.Plan.DoNotPark
    | _ -> ReadyWait.plan status rebuild

  /// The verdict a parked caller gets from the session's state, by policy.
  let private verdictOf (policy: Policy) (status: SessionLifecycleStatus) (rebuild: LastRebuild) : ReadyWait.Verdict =
    match policy with
    | Policy.ReturnsEarly -> ReadyWait.ofSession status LastRebuild.NeverRebuilt
    | _ -> ReadyWait.ofSession status rebuild

  let private answerOf (verdict: ReadyWait.Verdict) : Answer voption =
    match verdict with
    | ReadyWait.Verdict.KeepParked -> ValueNone
    | ReadyWait.Verdict.Ready -> ValueSome Answer.BecameReady
    | ReadyWait.Verdict.Failed _ -> ValueSome Answer.Faulted

  /// Answer every parked caller the verdict settles. Returns who is still parked and who was told what.
  let private settle (policy: Policy) (world: World) : World * (int * Answer) list =
    let told =
      world.Parked
      |> List.choose (fun id ->
        match answerOf (verdictOf policy world.Status world.Rebuild) with
        | ValueSome answer -> Some (id, answer)
        | ValueNone -> None)
    let stillParked = world.Parked |> List.filter (fun id -> told |> List.exists (fun (t, _) -> t = id) |> not)
    { world with Parked = stillParked }, told

  let private nth (index: int) (ids: int list) : int voption =
    match ids with
    | [] -> ValueNone
    | _ -> ValueSome (List.item (((index % List.length ids) + List.length ids) % List.length ids) ids)

  /// One op in this world: the new world and who was told what.
  let private apply (policy: Policy) (world: World) (op: Op) : World * (int * Answer) list =
    // Every op but a poll settles callers right away, unless the policy polls.
    let settleAfter (next: World) =
      match policy with
      | Policy.Polls -> next, []
      | _ -> settle policy next
    match op with
    | Op.Arrive ->
      let id = world.NextId
      let world' = { world with NextId = id + 1 }
      match planOf policy world.Status world.Rebuild with
      | ReadyWait.Plan.DoNotPark -> world', [ id, Answer.NotNeeded ]
      | ReadyWait.Plan.Park -> { world' with Parked = world'.Parked @ [ id ] }, []
    | Op.StartRebuild ->
      match isServing world.Status, isRebuilding world.Rebuild with
      | true, false -> settleAfter { world with Rebuild = LastRebuild.Latest (RebuildOutcome.InProgress at) }
      | _ -> world, []
    | Op.BuildSucceeds ->
      match isRebuilding world.Rebuild with
      | false -> world, []
      | true ->
        let outcome = RebuildOutcome.Succeeded at
        let next = { world with Status = replacementSpawning; Rebuild = LastRebuild.Latest outcome }
        settleAfter next
    | Op.BuildFails ->
      match isRebuilding world.Rebuild with
      | false -> world, []
      | true ->
        let outcome = RebuildOutcome.FailedStillServing (buildError, at)
        let next = { world with Rebuild = LastRebuild.Latest outcome }
        // The manager answers the callers parked through the rebuild with the build's own error first.
        let failedFirst, failedTold =
          match policy, ReadyWait.ofRebuildEnd outcome with
          | Policy.ReadyAfterFailedRebuild, _ -> next, []
          | _, ReadyWait.Verdict.Failed _ ->
            { next with Parked = [] }, next.Parked |> List.map (fun id -> id, Answer.Faulted)
          | _, ReadyWait.Verdict.KeepParked
          | _, ReadyWait.Verdict.Ready -> next, []
        let settled, told = settleAfter failedFirst
        settled, failedTold @ told
    | Op.WorkerReady ->
      match world.Status with
      | SessionLifecycleStatus.Starting _
      | SessionLifecycleStatus.Restarting _ -> settleAfter { world with Status = serving }
      | _ -> world, []
    | Op.Fault ->
      match world.Status with
      | SessionLifecycleStatus.Stopped -> world, []
      | _ -> settleAfter { world with Status = SessionLifecycleStatus.Faulted (FaultReason.report "the worker faulted") }
    | Op.Deadline which ->
      match policy, nth which world.Parked with
      | Policy.NeverTimesOut, _ -> world, []
      | _, ValueNone -> world, []
      | _, ValueSome id -> { world with Parked = world.Parked |> List.filter ((<>) id) }, [ id, Answer.TimedOut ]
    | Op.Poll ->
      match policy with
      | Policy.Polls -> settle policy world
      | _ -> world, []

  let private runOp (policy: Policy) (world: World) (op: Op) : World =
    let world', told = apply policy world op
    let step : Step =
      { Op = op
        Status = world'.Status
        Rebuild = world'.Rebuild
        StatusBefore = world.Status
        RebuildBefore = world.Rebuild
        Parked = world'.Parked
        ParkedBefore = world.Parked
        Answered = told }
    { world' with Steps = step :: world'.Steps }

  /// Fairness: a poll tick, and then every still-parked caller's deadline passes, so nothing is left pending.
  let private drainOps (world: World) : Op list =
    Op.Poll :: (world.Parked |> List.mapi (fun _ _ -> Op.Deadline 0))

  let private runWith (name: string) (policy: Policy) (scenario: Scenario) : Trace =
    let folded = scenario.Ops |> List.fold (runOp policy) (initial scenario.Start)
    let rec drain (world: World) (rounds: int) =
      match rounds, world.Parked with
      | 0, _ -> world
      | _, [] -> world
      | _, _ -> drain (drainOps world |> List.fold (runOp policy) world) (rounds - 1)
    let final = drain folded (List.length scenario.Ops + 4)
    { Scenario = scenario; Steps = List.rev final.Steps; Reducer = name }

  /// The real decisions.
  let run (scenario: Scenario) : Trace = runWith "real (ReadyWait)" Policy.Real scenario

  /// TWIN: a rebuild in progress is not a state anyone waits on.
  let runReturnsEarly (scenario: Scenario) : Trace = runWith "twin-returns-early" Policy.ReturnsEarly scenario

  /// TWIN: callers are only looked at on a poll tick.
  let runPolls (scenario: Scenario) : Trace = runWith "twin-polls" Policy.Polls scenario

  /// TWIN: a deadline answers nobody.
  let runNeverTimesOut (scenario: Scenario) : Trace = runWith "twin-never-times-out" Policy.NeverTimesOut scenario

  /// TWIN: a caller parked through a failed rebuild is told Ready.
  let runReadyAfterFailedRebuild (scenario: Scenario) : Trace =
    runWith "twin-ready-after-failed-rebuild" Policy.ReadyAfterFailedRebuild scenario
