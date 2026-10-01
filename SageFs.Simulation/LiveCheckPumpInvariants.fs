namespace SageFs.Simulation

open SageFs.Features.LiveTesting
open SageFs.Simulation.LiveCheckPumpSim

/// Named invariants over a settled `LiveCheckPumpSim.Trace`. Same stable-id / `Holds`-vs-`Violated`
/// shape as the other simulations.
module LiveCheckPumpInvariants =

  [<RequireQualifiedAccess>]
  type Outcome =
    | Holds
    | Violated of message: string

  type Invariant =
    { Id: string
      Description: string
      Check: Trace -> Outcome }

  let private replay (t: Trace) = sprintf "seed=%d, supersession=%A, ops=%A" t.Scenario.Seed t.Scenario.Supersession t.Scenario.Ops

  let private deliveries (step: Step) : (Req * PumpOutcome<Verdict>) list =
    step.Effects
    |> List.choose (function
      | PumpEffect.Deliver (request, outcome) -> Some (request, outcome)
      | _ -> None)

  let private allDeliveries (t: Trace) : (Req * PumpOutcome<Verdict>) list =
    t.Steps |> List.collect deliveries

  let private firstViolation (t: Trace) (check: Step -> string voption) : Outcome =
    t.Steps
    |> List.tryPick (fun s -> match check s with | ValueSome message -> Some message | ValueNone -> None)
    |> function
       | Some message -> Outcome.Violated (sprintf "%s (%s)" message (replay t))
       | None -> Outcome.Holds

  /// newest-edit-always-judged: once the worker has come and every question has come back, the newest text typed has
  /// a verdict. A worker that was never going to come (the session faulted) and a wait that ran out of time are the
  /// two excuses, and each says so.
  let newestEditAlwaysJudged : Invariant =
    { Id = "newest-edit-always-judged"
      Description = "After the worker is Ready and every call has come back, the newest request was answered; only a faulted session may leave it unanswered, and then it says why."
      Check = fun t ->
        match List.tryLast t.Requests with
        | None -> Outcome.Holds
        | Some newest ->
          let mine = allDeliveries t |> List.filter (fun (r, _) -> r.Id = newest.Id) |> List.map snd
          let answered = mine |> List.exists (function PumpOutcome.Answered _ -> true | PumpOutcome.Unanswered _ -> false)
          // Two excuses, both said out loud: the session faulted so no worker is coming, or the wait for one
          // reached its deadline (the confirmation's own deadline is the same bound).
          let excused =
            mine
            |> List.exists (function
              | PumpOutcome.Unanswered (UnansweredWhy.WorkerGone _) -> t.FinalWorker = WorkerNow.Faulted
              | PumpOutcome.Unanswered UnansweredWhy.NoWorkerInTime -> true
              | PumpOutcome.Unanswered (UnansweredWhy.WorkerSilent _)
              | PumpOutcome.Answered _ -> false)
          match answered || excused with
          | true -> Outcome.Holds
          | false -> Outcome.Violated (sprintf "the newest request %A never got a verdict; it ended as %A with the worker %A (%s)" newest mine t.FinalWorker (replay t)) }

  /// no-false-blocked: a check is never reported as having errors when the text has none. The worker that said so
  /// was not Ready, or was not there.
  let noFalseBlocked : Invariant =
    { Id = "no-false-blocked"
      Description = "An answer of HasErrors is only ever delivered for a text that really has errors."
      Check = fun t ->
        firstViolation t (fun s ->
          deliveries s
          |> List.tryPick (fun (request, outcome) ->
            match outcome with
            | PumpOutcome.Answered Verdict.HasErrors when truth request.Text = Verdict.Clean ->
              Some (sprintf "step %A reported errors for %A, whose text is clean, with the worker %A" s.Op request s.Worker)
            | _ -> None)
          |> function Some m -> ValueSome m | None -> ValueNone) }

  /// no-false-clear: the mirror of no-false-blocked: a text with errors is never reported clean.
  let noFalseClear : Invariant =
    { Id = "no-false-clear"
      Description = "An answer of Clean is only ever delivered for a text that really is clean."
      Check = fun t ->
        firstViolation t (fun s ->
          deliveries s
          |> List.tryPick (fun (request, outcome) ->
            match outcome with
            | PumpOutcome.Answered Verdict.Clean when truth request.Text = Verdict.HasErrors ->
              Some (sprintf "step %A reported %A clean, and its text has errors, with the worker %A" s.Op request s.Worker)
            | _ -> None)
          |> function Some m -> ValueSome m | None -> ValueNone) }

  /// no-stale-apply: when a newer request replaces an older one, the older one's answer is applied to nothing.
  /// Only for `NewestWins`: under `EveryOneRuns` every request is answered in its turn.
  let noStaleApply : Invariant =
    { Id = "no-stale-apply"
      Description = "Under NewestWins an answer is only ever delivered for the newest request made so far."
      Check = fun t ->
        match t.Scenario.Supersession with
        | Supersession.EveryOneRuns -> Outcome.Holds
        | Supersession.NewestWins ->
          firstViolation t (fun s ->
            deliveries s
            |> List.tryPick (fun (request, outcome) ->
              match outcome with
              | PumpOutcome.Answered _ when request.Id <> s.Latest ->
                Some (sprintf "step %A applied the answer to %A while the newest request was #%d" s.Op request s.Latest)
              | _ -> None)
            |> function Some m -> ValueSome m | None -> ValueNone) }

  /// asks-only-a-ready-worker: a call is only ever put to the worker the manager says is Ready, at that moment.
  let asksOnlyAReadyWorker : Invariant =
    { Id = "asks-only-a-ready-worker"
      Description = "Every Ask names the worker that is Serving when the step runs."
      Check = fun t ->
        firstViolation t (fun s ->
          s.Effects
          |> List.tryPick (function
            | PumpEffect.Ask (request, pid) when s.Worker <> WorkerNow.Serving pid ->
              Some (sprintf "step %A asked worker %d for %A while the world had %A" s.Op pid request s.Worker)
            | _ -> None)
          |> function Some m -> ValueSome m | None -> ValueNone) }

  /// single-flight: at most one call is in flight, so no step asks while another has not come back. A step that is
  /// the other's answer coming back may ask the next.
  let singleFlight : Invariant =
    { Id = "single-flight"
      Description = "A step asks at most once, and only when nothing is in flight or the step is the answer that ends the flight."
      Check = fun t ->
        firstViolation t (fun s ->
          let asks = s.Effects |> List.filter (function PumpEffect.Ask _ -> true | _ -> false) |> List.length
          let isAnswer = (match s.Event with PumpEvent.WorkerAnswered _ -> true | _ -> false)
          match asks, s.Before.Flight with
          | 0, _ -> ValueNone
          | 1, Flight.Idle -> ValueNone
          | 1, Flight.InFlight _ when isAnswer -> ValueNone
          | n, flight -> ValueSome (sprintf "step %A asked %d time(s) with %A in flight" s.Op n flight)) }

  /// every-request-ends-exactly-once: a request is reported once or is replaced by a newer one, never both and never
  /// twice (reporting it twice puts the same text's verdict on the rows twice), and none is left without an end once
  /// the worker is Ready and every call has come back (a request that is lost is an edit nobody judged).
  let everyRequestEndsExactlyOnce : Invariant =
    { Id = "every-request-ends-exactly-once"
      Description = "After the settle, each request has been delivered or superseded exactly once."
      Check = fun t ->
        let ends =
          t.Steps
          |> List.collect (fun s ->
            s.Effects
            |> List.choose (function
              | PumpEffect.Deliver (request, _) -> Some request.Id
              | PumpEffect.Superseded request -> Some request.Id
              | PumpEffect.Ask _
              | PumpEffect.AwaitWorker _ -> None))
        let counts = ends |> List.countBy id |> Map.ofList
        let miscounted =
          t.Requests
          |> List.tryFind (fun r -> Map.tryFind r.Id counts |> Option.defaultValue 0 <> 1)
        match miscounted with
        | Some request -> Outcome.Violated (sprintf "request %A ended %d times (%s)" request (Map.tryFind request.Id counts |> Option.defaultValue 0) (replay t))
        | None -> Outcome.Holds }

  /// every-request-runs-in-order: under `EveryOneRuns` each request is delivered, and in the order it was made, so a
  /// worker that came back does not reorder the evals of one file.
  let everyRequestRunsInOrder : Invariant =
    { Id = "every-request-runs-in-order"
      Description = "Under EveryOneRuns every request is delivered, in the order made."
      Check = fun t ->
        match t.Scenario.Supersession with
        | Supersession.NewestWins -> Outcome.Holds
        | Supersession.EveryOneRuns ->
          let delivered = allDeliveries t |> List.map (fun (r, _) -> r.Id)
          let made = t.Requests |> List.map (fun r -> r.Id)
          match delivered = made with
          | true -> Outcome.Holds
          | false -> Outcome.Violated (sprintf "requests were made %A and ended %A (%s)" made delivered (replay t)) }

  /// stale-waits-do-nothing: a wake-up or a deadline for a wait that is no longer the one parked changes nothing.
  let staleWaitsDoNothing : Invariant =
    { Id = "stale-waits-do-nothing"
      Description = "A WorkerSeen or WaitDeadlineReached for any wait but the current one has no effect and no change of state."
      Check = fun t ->
        firstViolation t (fun s ->
          let waitId =
            match s.Event with
            | PumpEvent.WorkerSeen (id, _) -> ValueSome id
            | PumpEvent.WaitDeadlineReached id -> ValueSome id
            | _ -> ValueNone
          match waitId with
          | ValueNone -> ValueNone
          | ValueSome id ->
            match s.Before.Awaiting = Awaiting.AwaitingWorker id, s.Effects, s.Before = s.After with
            | true, _, _ -> ValueNone
            | false, [], true -> ValueNone
            | false, _, _ -> ValueSome (sprintf "step %A woke wait %d, which was not the one parked (%A), and changed %A" s.Op id s.Before.Awaiting s.Effects)) }

  /// everything-resolves: once the worker is Ready and every call has come back, nothing is left queued or in flight.
  let everythingResolves : Invariant =
    { Id = "everything-resolves"
      Description = "After the settle, no request is queued and none is in flight."
      Check = fun t ->
        match t.Final.Queue, t.Final.Flight with
        | [], Flight.Idle -> Outcome.Holds
        | queue, flight -> Outcome.Violated (sprintf "left %A queued and %A in flight after the settle (%s)" queue flight (replay t)) }

  let all : Invariant list =
    [ newestEditAlwaysJudged
      noFalseBlocked
      noFalseClear
      noStaleApply
      asksOnlyAReadyWorker
      singleFlight
      everyRequestEndsExactlyOnce
      everyRequestRunsInOrder
      staleWaitsDoNothing
      everythingResolves ]

  /// The invariants that failed for a trace, if any (id, message).
  let violations (t: Trace) : (string * string) list =
    all
    |> List.choose (fun inv ->
      match inv.Check t with
      | Outcome.Holds -> None
      | Outcome.Violated msg -> Some (inv.Id, msg))
