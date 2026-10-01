namespace SageFs.Simulation

open SageFs.Features.LiveTesting

/// Deterministic Simulation Testing for the claim a person typing makes without saying so: "whatever
/// the worker is doing, the newest text I typed gets a verdict, and a worker that was not there to answer
/// never makes my code look broken".
///
/// A build confirmation replaces the worker, and an edit can land at any point of that: while the replacement
/// spawns (the old worker is parked and the registry has no proxy at all), while it warms (a proxy that answers with
/// errors that are not there), while it is Ready, or on top of a request that the retired worker never answered. The fold under test is the REAL decision
/// (`LiveCheckPump.step`); everything else here is the world around it: which worker is really there, which
/// questions are really in flight, and which waits are really parked.
///
/// Same design rules as `BuildConfirmationSim`:
///   * Chaos is DATA: a `Scenario` is an ordered op list. Same ops, same trace. The op order IS the
///     scheduler: an `Answer` before a `WorkerStops` means the worker answered before it went away.
///   * Ground truth is tracked by an independent fold that never reads the decision under test: what a
///     text really type-checks to, and what a worker that is not Ready really says.
///   * Twins reintroduce the bugs the invariants exist to catch.
module LiveCheckPumpSim =

  /// A request to check one text. `Id` is unique per request; `Text` decides what a truthful check says.
  type Req = { Id: int; Text: int }

  /// What a type-check of a text says.
  [<RequireQualifiedAccess>]
  type Verdict =
    | Clean
    | HasErrors

  /// What a truthful worker says about a text. Some texts really do have errors, so a verdict is never vacuous.
  let truth (text: int) : Verdict =
    match text % 3 with
    | 0 -> Verdict.HasErrors
    | _ -> Verdict.Clean

  /// What a worker that is still warming says: the opposite, because it has not loaded what the text refers to.
  let warmingSays (text: int) : Verdict =
    match truth text with
    | Verdict.Clean -> Verdict.HasErrors
    | Verdict.HasErrors -> Verdict.Clean

  /// Which worker the world really has.
  [<RequireQualifiedAccess>]
  type WorkerNow =
    /// No proxy: the old worker is parked and the replacement has not reported its port.
    | Absent
    /// A proxy exists and answers, but the worker has not finished warming.
    | Warming of pid: int
    | Serving of pid: int
    | Faulted

  [<RequireQualifiedAccess>]
  type Op =
    /// A new text is typed: a check of it is requested.
    | Edit of text: int
    /// The old worker is parked and the replacement is spawning: there is no proxy.
    | WorkerStops
    /// A replacement worker starts: it has a proxy, and is warming.
    | WorkerSpawns
    /// The warming worker is Ready: every parked wait is answered.
    | WorkerWarmed
    /// The session faults: every parked wait is answered with the reason.
    | WorkerFaults
    /// The nth call in flight (by order, modulo how many) comes back, from whichever worker is there now.
    | Answer of which: int
    /// The nth parked wait reaches its deadline.
    | Deadline of which: int

  type Scenario =
    { Seed: int
      Supersession: Supersession
      Ops: Op list }

  /// One step of the fold, with the world as it was.
  type Step =
    { Op: Op
      Event: PumpEvent<Req, Verdict>
      Before: PumpState<Req>
      After: PumpState<Req>
      Effects: PumpEffect<Req, Verdict> list
      /// The worker the world had when the step ran.
      Worker: WorkerNow
      /// The Id of the newest request any op has produced so far (-1 before any), by the simulation's own record.
      Latest: int }

  type Trace =
    { Scenario: Scenario
      Steps: Step list
      Requests: Req list
      Final: PumpState<Req>
      FinalWorker: WorkerNow
      Reducer: string }

  [<RequireQualifiedAccess>]
  type private Policy =
    /// The real decision.
    | Real
    /// TWIN: what the daemon did before the pump. A request that finds no Ready worker is dropped, with no wait.
    | DropsWhatItCannotAsk
    /// TWIN: a proxy that exists is a worker that can be asked, so a worker still warming is asked.
    | AsksAnyProxy
    /// TWIN: an answer is believed without looking at the worker again, so a worker that was retired under the
    /// request, or was still warming, is trusted.
    | TrustsWithoutRecheck
    /// TWIN: a newer request does not replace an older one, so an older answer is applied to rows that moved on.
    | AppliesOlderAnswers
    /// TWIN: a request is put to the worker at once, whether or not another is still in flight.
    | AsksWhileInFlight

  type private World =
    { Scenario: Scenario
      State: PumpState<Req>
      Worker: WorkerNow
      NextPid: int
      NextReq: int
      /// Calls put to a worker and not yet come back, in the order they were made.
      Asked: (Req * int) list
      /// Waits parked in the manager, in the order they were parked.
      Waits: int64 list
      /// Waits that already ended, which a late deadline or a second wake-up can still name.
      Spent: int64 list
      Requests: Req list
      Latest: int
      Steps: Step list }

  let private initial (scenario: Scenario) : World =
    { Scenario = scenario
      State = LiveCheckPump.initial
      Worker = WorkerNow.Serving 1
      NextPid = 2
      NextReq = 0
      Asked = []
      Waits = []
      Spent = []
      Requests = []
      Latest = -1
      Steps = [] }

  /// What the session manager says about the world's worker. A warming worker is Arriving whatever its proxy does.
  let private honestView (worker: WorkerNow) : WorkerView =
    match worker with
    | WorkerNow.Serving pid -> WorkerView.Serving pid
    | WorkerNow.Warming _
    | WorkerNow.Absent -> WorkerView.Arriving
    | WorkerNow.Faulted -> WorkerView.Gone "the session faulted"

  /// The twin's view: a proxy that exists is a worker that is there.
  let private proxyView (worker: WorkerNow) : WorkerView =
    match worker with
    | WorkerNow.Warming pid -> WorkerView.Serving pid
    | other -> honestView other

  let private supersedesFor (supersession: Supersession) : Req -> Req -> bool =
    match supersession with
    | Supersession.NewestWins -> fun _ _ -> true
    | Supersession.EveryOneRuns -> fun _ _ -> false

  let private viewFor (policy: Policy) (worker: WorkerNow) : WorkerView =
    match policy with
    | Policy.AsksAnyProxy -> proxyView worker
    | _ -> honestView worker

  /// The policy's version of the fold: the real step, or the real step with a bug put back.
  let private stepWith
    (policy: Policy)
    (supersession: Supersession)
    (state: PumpState<Req>)
    (event: PumpEvent<Req, Verdict>)
    : PumpState<Req> * PumpEffect<Req, Verdict> list =
    let real = LiveCheckPump.step (supersedesFor supersession)
    match policy, event with
    | Policy.DropsWhatItCannotAsk, PumpEvent.Requested (_, (WorkerView.Arriving | WorkerView.Gone _)) -> state, []
    | Policy.TrustsWithoutRecheck, PumpEvent.WorkerAnswered (reply, _) ->
      match state.Flight with
      | Flight.InFlight (_, pid) -> real state (PumpEvent.WorkerAnswered (reply, WorkerView.Serving pid))
      | Flight.Idle -> real state event
    | Policy.AppliesOlderAnswers, _ -> LiveCheckPump.step (fun _ _ -> false) state event
    | Policy.AsksWhileInFlight, PumpEvent.Requested (request, WorkerView.Serving pid) ->
      state, [ PumpEffect.Ask (request, pid) ]
    | _ -> real state event

  let private nth (index: int) (items: 'a list) : 'a voption =
    match items with
    | [] -> ValueNone
    | _ -> ValueSome (List.item (((index % List.length items) + List.length items) % List.length items) items)

  /// Fold the effects into the world: what is now really in flight or parked.
  let private applyEffects (world: World) (effects: PumpEffect<Req, Verdict> list) : World =
    effects
    |> List.fold
      (fun w effect ->
        match effect with
        | PumpEffect.Ask (request, pid) -> { w with Asked = w.Asked @ [ request, pid ] }
        | PumpEffect.AwaitWorker waitId -> { w with Waits = w.Waits @ [ waitId ] }
        | PumpEffect.Deliver _ -> w)
      world

  let private foldEvent (policy: Policy) (world: World) (op: Op) (event: PumpEvent<Req, Verdict>) : World =
    let after, effects = stepWith policy world.Scenario.Supersession world.State event
    let step =
      { Op = op
        Event = event
        Before = world.State
        After = after
        Effects = effects
        Worker = world.Worker
        Latest = world.Latest }
    applyEffects { world with State = after; Steps = step :: world.Steps } effects

  /// Where the op that brings a parked wait to an end gets its event.
  let private answerWaits (policy: Policy) (world: World) (op: Op) : World =
    let view = viewFor policy world.Worker
    world.Waits
    |> List.fold (fun w waitId -> foldEvent policy w op (PumpEvent.WorkerSeen (waitId, view))) { world with Waits = []; Spent = world.Spent @ world.Waits }

  let private runOp (policy: Policy) (world: World) (op: Op) : World =
    match op with
    | Op.Edit text ->
      let request = { Id = world.NextReq; Text = text }
      let world' = { world with NextReq = world.NextReq + 1; Latest = request.Id; Requests = world.Requests @ [ request ] }
      foldEvent policy world' op (PumpEvent.Requested (request, viewFor policy world'.Worker))
    | Op.WorkerStops ->
      match world.Worker with
      | WorkerNow.Faulted -> world
      | _ -> { world with Worker = WorkerNow.Absent }
    | Op.WorkerSpawns ->
      match world.Worker with
      | WorkerNow.Absent -> { world with Worker = WorkerNow.Warming world.NextPid; NextPid = world.NextPid + 1 }
      | _ -> world
    | Op.WorkerWarmed ->
      match world.Worker with
      | WorkerNow.Warming pid -> answerWaits policy { world with Worker = WorkerNow.Serving pid } op
      | _ -> world
    | Op.WorkerFaults ->
      match world.Worker with
      | WorkerNow.Faulted -> world
      | _ -> answerWaits policy { world with Worker = WorkerNow.Faulted } op
    | Op.Answer which ->
      match nth which (List.indexed world.Asked) with
      | ValueNone -> world
      | ValueSome (index, (request, pid)) ->
        let reply =
          match world.Worker with
          | WorkerNow.Serving p when p = pid -> WorkerReply.Replied (truth request.Text)
          | WorkerNow.Warming p when p = pid -> WorkerReply.Replied (warmingSays request.Text)
          | _ -> WorkerReply.Silent "the worker was retired under the request"
        let remaining = world.Asked |> List.indexed |> List.filter (fun (i, _) -> i <> index) |> List.map snd
        foldEvent policy { world with Asked = remaining } op (PumpEvent.WorkerAnswered (reply, viewFor policy world.Worker))
    | Op.Deadline which ->
      match nth which (world.Waits @ world.Spent) with
      | ValueNone -> world
      | ValueSome waitId ->
        let ended = { world with Waits = world.Waits |> List.filter ((<>) waitId); Spent = waitId :: (world.Spent |> List.filter ((<>) waitId)) }
        foldEvent policy ended op (PumpEvent.WaitDeadlineReached waitId)

  let private isSettled (world: World) : bool =
    List.isEmpty world.Asked
    && List.isEmpty world.State.Queue
    && world.State.Flight = Flight.Idle
    && (match world.Worker with
        | WorkerNow.Serving _
        | WorkerNow.Faulted -> true
        | WorkerNow.Absent
        | WorkerNow.Warming _ -> false)

  /// Fairness: a worker comes and is Ready, and every question in flight comes back, so nothing is left pending.
  let private settle (policy: Policy) (world: World) (rounds: int) : World =
    let rec go (w: World) (n: int) =
      match n, isSettled w with
      | 0, _ -> w
      | _, true -> w
      | _, false ->
        let toWorker =
          match w.Worker with
          | WorkerNow.Absent -> runOp policy w Op.WorkerSpawns
          | WorkerNow.Warming _ -> runOp policy w Op.WorkerWarmed
          | WorkerNow.Serving _
          | WorkerNow.Faulted -> w
        let answered =
          match toWorker.Asked with
          | [] -> toWorker
          | _ -> runOp policy toWorker (Op.Answer 0)
        go answered (n - 1)
    go world rounds

  let private runWith (name: string) (policy: Policy) (scenario: Scenario) : Trace =
    let start = initial scenario
    let folded = scenario.Ops |> List.fold (runOp policy) start
    let final = settle policy folded (4 * (List.length scenario.Ops + 4))
    { Scenario = scenario
      Steps = List.rev final.Steps
      Requests = final.Requests
      Final = final.State
      FinalWorker = final.Worker
      Reducer = name }

  /// The real decision.
  let run (scenario: Scenario) : Trace = runWith "real (LiveCheckPump.step)" Policy.Real scenario

  /// TWIN: a request that finds no Ready worker is dropped.
  let runDropsWhatItCannotAsk (scenario: Scenario) : Trace = runWith "twin-drops-what-it-cannot-ask" Policy.DropsWhatItCannotAsk scenario

  /// TWIN: a worker that has a proxy is asked, whether or not it is Ready.
  let runAsksAnyProxy (scenario: Scenario) : Trace = runWith "twin-asks-any-proxy" Policy.AsksAnyProxy scenario

  /// TWIN: an answer is believed without looking at the worker again.
  let runTrustsWithoutRecheck (scenario: Scenario) : Trace = runWith "twin-trusts-without-recheck" Policy.TrustsWithoutRecheck scenario

  /// TWIN: an older request's answer is applied after a newer request was made.
  let runAppliesOlderAnswers (scenario: Scenario) : Trace = runWith "twin-applies-older-answers" Policy.AppliesOlderAnswers scenario

  /// TWIN: a request is put to the worker while another is in flight.
  let runAsksWhileInFlight (scenario: Scenario) : Trace = runWith "twin-asks-while-in-flight" Policy.AsksWhileInFlight scenario
