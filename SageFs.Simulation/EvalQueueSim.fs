namespace SageFs.Simulation

open System
open SageFs.FsiHost.FsiProtocol

/// Deterministic Simulation Testing for the claim several callers of one session rely on: "a cancel only ever interrupts the
/// eval of the request that asked for it, and the host is killed only when that request's own eval is the one running and
/// ignores the interrupt".
///
/// `EvalBoundSim` has one caller and one eval, so it cannot see the case that matters: agent A runs a long, legitimate eval,
/// agent B's eval waits in the host's queue behind it, and B's `timeout_seconds` runs out. Here several callers submit evals to
/// one host that runs them one at a time, first come first served, and each caller may cancel its own request at a time of its
/// choosing. The host's rule for an interrupt is the REAL `EvalInterrupts.planInterrupt`, and for an interrupted request whose
/// grace has run out the REAL `EvalInterrupts.graceExpired`. Time is data: the same scenario gives the same trace.
///
/// Two TWINS reintroduce the bugs the invariants exist to catch: a host whose interrupt carries no request id (it interrupts
/// whatever is running), and a grace that is not disarmed by the reply (it kills the host although the request has answered).
module EvalQueueSim =

  /// One eval a caller submits. Times and durations are milliseconds of virtual time.
  type Submission =
    { /// Names the eval; unique within a scenario.
      Id: int
      SubmittedAt: int
      /// Host time the eval needs when nobody interrupts it.
      Needs: int
      /// When the caller's own timeout fires and it cancels this request, if ever.
      CancelledAt: int option
      /// How long after an interrupt the eval answers. None: it never does (a native call, a tight loop).
      AnswersAfterInterrupt: int option }

  type Scenario =
    { Seed: int
      /// The grace an interrupted request gets before the host is killed.
      Grace: int
      Submissions: Submission list }

  [<RequireQualifiedAccess>]
  type Answer =
    /// The eval ran to its end.
    | Finished
    /// The eval was interrupted while running and answered.
    | Interrupted
    /// The eval was still queued, was cancelled, and never ran.
    | NeverRan
    /// The host was killed with this eval unanswered.
    | Lost

  type Interrupted = { At: int; Requester: int; Victim: int }

  type Killed = { At: int; ForEval: int; WasAnswered: bool }

  type Trace =
    { Scenario: Scenario
      Reducer: string
      Answers: Map<int, int * Answer>
      Interrupts: Interrupted list
      Kills: Killed list }

  [<RequireQualifiedAccess>]
  type private Host =
    | Real
    /// TWIN: the interrupt names no request, so the host interrupts whichever eval is running.
    | NoRequestId
    /// TWIN: the grace is not disarmed when the reply arrives, so it kills the host whether or not the request answered.
    | StaleGrace

  type private Running = { Id: int; StartedAt: int; InterruptedAt: int option }

  type private World =
    { Now: int
      Running: Running option
      Queue: int list
      /// Interrupted requests and when their grace runs out.
      Graces: (int * int) list
      Answers: Map<int, int * Answer>
      Interrupts: Interrupted list
      Kills: Killed list }

  let private tick = 10

  let private find (s: Scenario) (id: int) : Submission = s.Submissions |> List.find (fun x -> x.Id = id)

  let private answer (world: World) (id: int) (how: Answer) : World =
    match Map.containsKey id world.Answers with
    | true -> world
    | false -> { world with Answers = Map.add id (world.Now, how) world.Answers }

  /// Start the next queued eval if the session thread is free.
  let private startNext (world: World) : World =
    match world.Running, world.Queue with
    | None, next :: rest -> { world with Running = Some { Id = next; StartedAt = world.Now; InterruptedAt = None }; Queue = rest }
    | _ -> world

  /// When the running eval answers by itself (its work ends, or it replies to the interrupt), or never.
  let private answersAt (s: Scenario) (running: Running) : int option =
    let eval = find s running.Id
    let byItself = running.StartedAt + eval.Needs
    match running.InterruptedAt, eval.AnswersAfterInterrupt with
    | Some at, Some after -> Some(min byItself (at + after))
    | Some _, None -> Some byItself
    | None, _ -> Some byItself

  let private horizon (s: Scenario) : int =
    (s.Submissions |> List.sumBy (fun x -> x.Needs)) + (s.Submissions |> List.map (fun x -> x.SubmittedAt) |> List.max) + 2 * s.Grace + 10 * tick

  let private step (host: Host) (s: Scenario) (world: World) : World =
    let now = world.Now
    // 1. The host's running eval answers, if its time has come. Replies land before anything else of the tick, so a reply
    //    that arrives as the grace ends has answered.
    let afterAnswer =
      match world.Running with
      | Some running when answersAt s running = Some now ->
        let how = match now < running.StartedAt + (find s running.Id).Needs with true -> Answer.Interrupted | false -> Answer.Finished
        startNext { answer world running.Id how with Running = None }
      | _ -> world
    // 2. Evals are submitted: they queue behind the running one.
    let afterSubmit =
      s.Submissions
      |> List.filter (fun x -> x.SubmittedAt = now)
      |> List.fold (fun w x -> startNext { w with Queue = w.Queue @ [ x.Id ] }) afterAnswer
    // 3. Callers whose timeout fires cancel their own request.
    let afterCancel =
      s.Submissions
      |> List.filter (fun x -> x.CancelledAt = Some now && not (Map.containsKey x.Id afterSubmit.Answers))
      |> List.fold
        (fun (w: World) x ->
          let target = match host with Host.NoRequestId -> (match w.Running with Some r -> r.Id | None -> x.Id) | Host.Real | Host.StaleGrace -> x.Id
          match EvalInterrupts.planInterrupt (w.Running |> Option.map (fun r -> int64 r.Id) |> Option.toValueOption) (w.Queue |> List.map int64) (int64 target) with
          | EvalInterrupts.Plan.InterruptRunning ->
            let victim = w.Running.Value
            { w with
                Running = Some { victim with InterruptedAt = Some now }
                Graces = (victim.Id, now + s.Grace) :: w.Graces
                Interrupts = { At = now; Requester = x.Id; Victim = victim.Id } :: w.Interrupts }
          | EvalInterrupts.Plan.DropQueued -> answer { w with Queue = w.Queue |> List.filter ((<>) target) } target Answer.NeverRan
          | EvalInterrupts.Plan.NothingToInterrupt -> w)
        afterSubmit
    // 4. Graces that run out: keep the host or kill it.
    let due, rest = afterCancel.Graces |> List.partition (fun (_, expiry) -> expiry = now)
    due
    |> List.fold
      (fun (w: World) (victim, _) ->
        let answered = Map.containsKey victim w.Answers
        let stillWaiting = match host with Host.StaleGrace -> true | Host.Real | Host.NoRequestId -> not answered
        match EvalInterrupts.graceExpired stillWaiting with
        | EvalInterrupts.GraceVerdict.KeepHost -> w
        | EvalInterrupts.GraceVerdict.KillHost ->
          let lost = (Option.toList (w.Running |> Option.map (fun r -> r.Id)) @ w.Queue) |> List.fold (fun x id -> answer x id Answer.Lost) w
          { lost with Running = None; Queue = []; Kills = { At = now; ForEval = victim; WasAnswered = answered } :: lost.Kills })
      { afterCancel with Graces = rest }

  let private runWith (name: string) (host: Host) (s: Scenario) : Trace =
    let limit = horizon s
    let rec loop (world: World) =
      match world.Now > limit with
      | true -> world
      | false -> loop (step host s { world with Now = world.Now + tick })
    let final = loop { Now = -tick; Running = None; Queue = []; Graces = []; Answers = Map.empty; Interrupts = []; Kills = [] }
    { Scenario = s; Reducer = name; Answers = final.Answers; Interrupts = List.rev final.Interrupts; Kills = List.rev final.Kills }

  /// The real decisions.
  let run (s: Scenario) : Trace = runWith "real (EvalInterrupts)" Host.Real s

  /// TWIN: an interrupt that names no request.
  let runNoRequestId (s: Scenario) : Trace = runWith "twin-no-request-id" Host.NoRequestId s

  /// TWIN: a grace the reply does not disarm.
  let runStaleGrace (s: Scenario) : Trace = runWith "twin-stale-grace" Host.StaleGrace s

  // -- Scenarios --

  /// Agent A runs a long, legitimate eval. Agent B's eval waits behind it, and B's timeout fires. A must finish; B never ran.
  let queuedBehindLong : Scenario =
    { Seed = 800
      Grace = 1000
      Submissions =
        [ { Id = 1; SubmittedAt = 0; Needs = 3000; CancelledAt = None; AnswersAfterInterrupt = Some 20 }
          { Id = 2; SubmittedAt = 100; Needs = 200; CancelledAt = Some 600; AnswersAfterInterrupt = Some 20 } ] }

  /// A cancels its own eval, which answers exactly as the grace runs out; C waits behind it. The reply has landed, so the host lives.
  let replyAsGraceEnds : Scenario =
    { Seed = 801
      Grace = 1000
      Submissions =
        [ { Id = 1; SubmittedAt = 0; Needs = 5000; CancelledAt = Some 1000; AnswersAfterInterrupt = Some 1000 }
          { Id = 2; SubmittedAt = 500; Needs = 100; CancelledAt = None; AnswersAfterInterrupt = Some 20 } ] }

  /// A's eval ignores the interrupt and A cancelled it: the host may be killed for it, and C's loss is that eval's doing.
  let ignorerKilled : Scenario =
    { Seed = 802
      Grace = 1000
      Submissions =
        [ { Id = 1; SubmittedAt = 0; Needs = 600000; CancelledAt = Some 500; AnswersAfterInterrupt = None }
          { Id = 2; SubmittedAt = 300; Needs = 100; CancelledAt = None; AnswersAfterInterrupt = Some 20 } ] }

  let fromSeed (seed: int) : Scenario =
    let rnd = Random(seed)
    let count = rnd.Next(2, 5)
    let grace = rnd.Next(2, 12) * 100
    let submissions =
      [ for id in 1 .. count ->
          let submittedAt = rnd.Next(0, 10) * 100
          let needs = rnd.Next(1, 40) * 100
          let cancelledAt = match rnd.Next 3 with 0 -> Some(submittedAt + rnd.Next(1, 30) * 100) | _ -> None
          let answers = match rnd.Next 3 with 0 -> None | _ -> Some(rnd.Next(0, 3) * (grace / 2))
          { Id = id; SubmittedAt = submittedAt; Needs = needs; CancelledAt = cancelledAt; AnswersAfterInterrupt = answers } ]
    { Seed = seed; Grace = grace; Submissions = submissions }

  // -- Invariants --

  type Violation = { Id: string; Message: string }

  let violations (t: Trace) : Violation list =
    let s = t.Scenario
    let replay = sprintf "seed=%d, grace=%dms, submissions=%A" s.Seed s.Grace s.Submissions
    [ // CANCEL-ONLY-OWN: an interrupt only ever reaches the eval of the request that asked for it.
      for i in t.Interrupts do
        if i.Requester <> i.Victim then
          { Id = "CANCEL-ONLY-OWN"; Message = sprintf "request %d's cancel interrupted request %d's eval at %dms (%s)" i.Requester i.Victim i.At replay }
      // KILL-ONLY-FOR-OWN-IGNORER: the host is killed only for a request that cancelled its own eval, that was running,
      // and that had not answered when the grace ran out.
      for k in t.Kills do
        let eval = find s k.ForEval
        if k.WasAnswered then
          { Id = "KILL-ONLY-FOR-OWN-IGNORER"; Message = sprintf "the host was killed at %dms for request %d, which had already answered (%s)" k.At k.ForEval replay }
        elif eval.CancelledAt.IsNone then
          { Id = "KILL-ONLY-FOR-OWN-IGNORER"; Message = sprintf "the host was killed at %dms for request %d, whose caller never cancelled it (%s)" k.At k.ForEval replay }
      // UNCANCELLED-EVAL-FINISHES: an eval whose caller never cancelled it ends with its own result, unless the host was
      // killed for another request that cancelled its own eval and ignored the interrupt.
      for eval in s.Submissions do
        if eval.CancelledAt.IsNone then
          match Map.tryFind eval.Id t.Answers with
          | Some(_, Answer.Finished) -> ()
          | Some(_, Answer.Lost) when not (List.isEmpty t.Kills) -> ()
          | Some(_, other) -> { Id = "UNCANCELLED-EVAL-FINISHES"; Message = sprintf "request %d, never cancelled, ended %A (%s)" eval.Id other replay }
          | None -> { Id = "UNCANCELLED-EVAL-FINISHES"; Message = sprintf "request %d, never cancelled, was never answered (%s)" eval.Id replay } ]
