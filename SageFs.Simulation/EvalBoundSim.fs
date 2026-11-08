namespace SageFs.Simulation

open System
open SageFs

/// Deterministic Simulation Testing for the claim an eval caller relies on: "you get an answer by your timeout plus the
/// cancel's own bound, whatever the eval does".
///
/// The eval is scripted by fault injection: it finishes soon, finishes late, stops when it is cancelled (managed code),
/// or never stops at all (a native call). Time is data: the caller looks at the clock every tick of virtual time and
/// decides with the REAL `EvalWait.next`, the function `EvalWait.run` loops on in production. Same scenario, same trace.
///
/// Three TWIN callers reintroduce the bugs the invariants exist to catch: one that waits for the eval and never cancels
/// (what `send_fsharp_code` did: a wedged eval was never answered), one that cancels before the limit, and one that
/// cancels and then waits for the eval forever.
module EvalBoundSim =

  /// What the eval does, in milliseconds of virtual time.
  [<RequireQualifiedAccess>]
  type Behavior =
    /// Answers this long after it started, cancelled or not.
    | FinishesAfter of ms: int
    /// Runs until cancelled, then answers this long after the cancel (managed code reaches a check point).
    | StopsWhenCancelled of afterCancelMs: int
    /// Never answers: a native call, or a loop nothing can interrupt.
    | NeverAnswers

  /// `Limit` and `AnswerWait` are milliseconds of virtual time.
  type Scenario =
    { Seed: int
      Limit: int
      AnswerWait: int
      Behavior: Behavior }

  [<RequireQualifiedAccess>]
  type Answer =
    | Finished
    | FinishedAfterCancel
    | GaveUp

  type Trace =
    { Scenario: Scenario
      Reducer: string
      /// Virtual times at which the caller asked the eval to stop.
      CancelAsks: int list
      /// When and how the caller was answered, if it was before the horizon.
      Answered: (int * Answer) option
      /// The virtual time the eval itself answered, if it did.
      EvalAnsweredAt: int option }

  [<RequireQualifiedAccess>]
  type private Caller =
    | Real
    /// TWIN: waits for the eval and never asks it to stop.
    | NeverCancels
    /// TWIN: asks the eval to stop at half its limit.
    | CancelsEarly
    /// TWIN: asks at the limit, then waits for the eval as long as it takes.
    | NeverGivesUp

  let private tick = 10

  let private span (ms: int) = TimeSpan.FromMilliseconds(float ms)

  /// Ticks past the point where the real caller must have answered: a caller that is still silent here never will.
  let private horizonMs (s: Scenario) = s.Limit + s.AnswerWait + 20 * tick

  let private stepOf (caller: Caller) (s: Scenario) (nowMs: int) (stage: EvalWaitStage) : EvalWaitStep =
    match caller, stage with
    | Caller.Real, _ -> EvalWait.next (span s.Limit) (span s.AnswerWait) (span nowMs) stage
    | Caller.NeverCancels, _ -> EvalWaitStep.KeepWaiting(span tick)
    | Caller.CancelsEarly, EvalWaitStage.Running ->
      match nowMs >= s.Limit / 2 with
      | true -> EvalWaitStep.AskCancel
      | false -> EvalWaitStep.KeepWaiting(span tick)
    | Caller.CancelsEarly, EvalWaitStage.CancelAsked _ -> EvalWait.next (span s.Limit) (span s.AnswerWait) (span nowMs) stage
    | Caller.NeverGivesUp, EvalWaitStage.Running -> EvalWait.next (span s.Limit) (span s.AnswerWait) (span nowMs) stage
    | Caller.NeverGivesUp, EvalWaitStage.CancelAsked _ -> EvalWaitStep.KeepWaiting(span tick)

  /// When the eval answers, given when it was cancelled (if it was).
  let private evalAnswersAt (s: Scenario) (cancelledAt: int option) : int option =
    match s.Behavior, cancelledAt with
    | Behavior.FinishesAfter ms, _ -> Some ms
    | Behavior.StopsWhenCancelled after, Some at -> Some(at + after)
    | Behavior.StopsWhenCancelled _, None -> None
    | Behavior.NeverAnswers, _ -> None

  let private runWith (name: string) (caller: Caller) (s: Scenario) : Trace =
    let horizon = horizonMs s
    let rec loop (nowMs: int) (stage: EvalWaitStage) (cancelledAt: int option) (asks: int list) =
      let evalAt = evalAnswersAt s cancelledAt
      let evalDone = evalAt |> Option.exists (fun at -> at <= nowMs)
      match evalDone, nowMs > horizon with
      | true, _ ->
        let answer =
          match stage with
          | EvalWaitStage.Running -> Answer.Finished
          | EvalWaitStage.CancelAsked _ -> Answer.FinishedAfterCancel
        { Scenario = s; Reducer = name; CancelAsks = List.rev asks; Answered = Some(nowMs, answer); EvalAnsweredAt = evalAt }
      | false, true ->
        { Scenario = s; Reducer = name; CancelAsks = List.rev asks; Answered = None; EvalAnsweredAt = evalAt }
      | false, false ->
        match stepOf caller s nowMs stage with
        | EvalWaitStep.KeepWaiting _ -> loop (nowMs + tick) stage cancelledAt asks
        | EvalWaitStep.AskCancel -> loop nowMs (EvalWaitStage.CancelAsked(span nowMs)) (Some nowMs) (nowMs :: asks)
        | EvalWaitStep.GiveUp ->
          { Scenario = s; Reducer = name; CancelAsks = List.rev asks; Answered = Some(nowMs, Answer.GaveUp); EvalAnsweredAt = evalAt }
    loop 0 EvalWaitStage.Running None []

  /// The real decision.
  let run (s: Scenario) : Trace = runWith "real (EvalWait)" Caller.Real s

  /// TWIN: never asks the eval to stop.
  let runNeverCancels (s: Scenario) : Trace = runWith "twin-never-cancels" Caller.NeverCancels s

  /// TWIN: asks the eval to stop before its limit.
  let runCancelsEarly (s: Scenario) : Trace = runWith "twin-cancels-early" Caller.CancelsEarly s

  /// TWIN: asks, then waits for the eval as long as it takes.
  let runNeverGivesUp (s: Scenario) : Trace = runWith "twin-never-gives-up" Caller.NeverGivesUp s

  // -- Generators: chaos is data --

  let fromSeed (seed: int) : Scenario =
    let rnd = Random(seed)
    let limitMs = rnd.Next(2, 40) * 100
    let answerWaitMs = rnd.Next(1, 20) * 100
    let behavior =
      match rnd.Next 4 with
      | 0 -> Behavior.FinishesAfter(rnd.Next(1, limitMs))
      | 1 -> Behavior.FinishesAfter(limitMs + rnd.Next(1, 3000))
      | 2 -> Behavior.StopsWhenCancelled(rnd.Next(0, 2 * answerWaitMs))
      | _ -> Behavior.NeverAnswers
    { Seed = seed; Limit = limitMs; AnswerWait = answerWaitMs; Behavior = behavior }

  /// A native call: the eval never answers, whatever is done to it.
  let wedged : Scenario = { Seed = 700; Limit = 1000; AnswerWait = 500; Behavior = Behavior.NeverAnswers }

  /// A loop with a check point: it stops soon after the cancel.
  let loops : Scenario = { Seed = 701; Limit = 1000; AnswerWait = 500; Behavior = Behavior.StopsWhenCancelled 30 }

  /// Work that finishes inside its limit.
  let quick : Scenario = { Seed = 702; Limit = 1000; AnswerWait = 500; Behavior = Behavior.FinishesAfter 200 }

  // -- Invariants --

  type Violation = { Id: string; Message: string }

  let private replay (t: Trace) = sprintf "seed=%d, limit=%dms, answerWait=%dms, behavior=%A" t.Scenario.Seed t.Scenario.Limit t.Scenario.AnswerWait t.Scenario.Behavior

  let violations (t: Trace) : Violation list =
    let s = t.Scenario
    [ // ANSWERED-BY-BOUND: whatever the eval does, the caller is answered by the limit plus the cancel's bound.
      match t.Answered with
      | None -> { Id = "ANSWERED-BY-BOUND"; Message = sprintf "the caller was never answered (%s)" (replay t) }
      | Some(at, _) when at > s.Limit + s.AnswerWait + tick ->
        { Id = "ANSWERED-BY-BOUND"; Message = sprintf "the caller was answered at %dms, past %dms (%s)" at (s.Limit + s.AnswerWait) (replay t) }
      | Some _ -> ()
      // NO-EARLY-CANCEL: an eval is never asked to stop before its limit.
      for at in t.CancelAsks do
        if at < s.Limit then
          { Id = "NO-EARLY-CANCEL"; Message = sprintf "the eval was asked to stop at %dms, before its limit of %dms (%s)" at s.Limit (replay t) }
      // CANCEL-ONCE: one ask is enough, and a second would only repeat it.
      if List.length t.CancelAsks > 1 then
        { Id = "CANCEL-ONCE"; Message = sprintf "the eval was asked to stop %d times (%s)" (List.length t.CancelAsks) (replay t) }
      // FINISHED-IS-TRUTHFUL: an answer reported as finished within the limit came from an eval that was never asked to stop.
      match t.Answered with
      | Some(_, Answer.Finished) when not (List.isEmpty t.CancelAsks) ->
        { Id = "FINISHED-IS-TRUTHFUL"; Message = sprintf "answered Finished after the eval was asked to stop (%s)" (replay t) }
      | _ -> () ]
