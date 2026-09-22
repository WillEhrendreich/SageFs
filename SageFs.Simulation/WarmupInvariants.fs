namespace SageFs.Simulation

open System
open SageFs
open SageFs.WarmupSupervision
open SageFs.Simulation.WarmupSim

/// Named invariants over a drained `WarmupSim.Trace`. Same stable-id /
/// `Holds`-vs-`Violated` shape as `StreamingProxyInvariants` /
/// `FileReloadRoutingInvariants`.
module WarmupInvariants =

  [<RequireQualifiedAccess>]
  type Outcome =
    | Holds
    | Violated of message: string

  type Invariant =
    { Id: string
      Description: string
      Check: Trace -> Outcome }

  let private totalClockAdvance (events: LifecycleEvent<'a> list) : TimeSpan =
    events
    |> List.sumBy (function LifecycleEvent.ClockAdvance s -> s.TotalSeconds | _ -> 0.0)
    |> TimeSpan.FromSeconds

  /// Elapsed time since the last `Progressed` observation (or since the
  /// start, if none occurred) — the "trailing silence" a healthy warmup
  /// must never accumulate past `Bounds.Inactivity` while still Starting.
  let private trailingSilence (events: LifecycleEvent<'a> list) : TimeSpan =
    events
    |> List.fold
      (fun acc event ->
        match event with
        | LifecycleEvent.ClockAdvance span -> acc + span
        | LifecycleEvent.PollTick PollObservation.Progressed -> TimeSpan.Zero
        | LifecycleEvent.PollTick _ -> acc
        | LifecycleEvent.StopRequested -> acc)
      TimeSpan.Zero

  let private hasStop (t: Trace) : bool =
    t.Scenario.Events |> List.contains LifecycleEvent.StopRequested

  let private hasTerminalObservation (t: Trace) : bool =
    t.Scenario.Events
    |> List.exists (function
      | LifecycleEvent.PollTick (PollObservation.Ready _)
      | LifecycleEvent.PollTick (PollObservation.Faulted _) -> true
      | _ -> false)

  /// bounded-reach (defect #1, absolute bound): once the scenario's total
  /// simulated elapsed time exceeds `Bounds.Absolute`, the final state must
  /// not still be `Starting` — the absolute ceiling is a real ceiling, not
  /// a suggestion.
  let boundedReachAbsolute : Invariant =
    { Id = "bounded-reach-absolute"
      Description =
        "Once total elapsed time exceeds Bounds.Absolute, the session is never left Starting — it always reaches Ready or a stated Faulted."
      Check = fun t ->
        let elapsed = totalClockAdvance t.Scenario.Events
        match elapsed > t.Scenario.Bounds.Absolute, t.Final.State with
        | true, LifecycleState.Starting ->
          Outcome.Violated(
            sprintf
              "reducer=%s seed=%d — %.0fs of simulated time elapsed (absolute bound %.0fs) and the session is STILL Starting"
              t.Reducer t.Scenario.Seed elapsed.TotalSeconds t.Scenario.Bounds.Absolute.TotalSeconds)
        | _ -> Outcome.Holds }

  /// silence-is-caught-by-inactivity (defect #1, the trial's actual shape):
  /// if the scenario has no Stop and no Ready/Faulted observation was ever
  /// reported, and trailing silence exceeds `Bounds.Inactivity`, the final
  /// state must not still be Starting. This is what distinguishes the real
  /// fix from the historical flat-bound-only shape — see the twin test in
  /// WarmupSimTests.fs, where THIS invariant (not boundedReachAbsolute)
  /// is what the twin violates.
  let silenceIsCaughtByInactivity : Invariant =
    { Id = "silence-is-caught-by-inactivity"
      Description =
        "A session that has gone silent (no Progressed observation) for longer than Bounds.Inactivity, with no Stop and no Ready/Faulted reported, is never left Starting — silence has its own, tighter bound than the absolute ceiling."
      Check = fun t ->
        match hasStop t, hasTerminalObservation t with
        | true, _ | _, true -> Outcome.Holds // covered by other invariants; not this one's shape
        | false, false ->
          let silence = trailingSilence t.Scenario.Events
          match silence > t.Scenario.Bounds.Inactivity, t.Final.State with
          | true, LifecycleState.Starting ->
            Outcome.Violated(
              sprintf
                "reducer=%s seed=%d — %.0fs of trailing silence (inactivity bound %.0fs) and the session is STILL Starting"
                t.Reducer t.Scenario.Seed silence.TotalSeconds t.Scenario.Bounds.Inactivity.TotalSeconds)
          | _ -> Outcome.Holds }

  /// stop-always-completes (defect #2): any scenario containing
  /// StopRequested ends in Stopped — never anything else, regardless of
  /// what else happened or in what order, and regardless of which reducer
  /// (real or twin) processed it — StopRequested's handling is identical in
  /// both (see WarmupSim.flatBoundStep), so this invariant holds for both.
  let stopAlwaysCompletes : Invariant =
    { Id = "stop-always-completes"
      Description =
        "Any scenario containing StopRequested ends in Stopped, regardless of what else happened or in what order."
      Check = fun t ->
        match hasStop t, t.Final.State with
        | true, LifecycleState.Stopped -> Outcome.Holds
        | true, other ->
          Outcome.Violated(
            sprintf
              "reducer=%s seed=%d — StopRequested was in the event list but the final state is %A, not Stopped"
              t.Reducer t.Scenario.Seed other)
        | false, _ -> Outcome.Holds }

  /// no-resurrection (defect #3): once ANY prefix of the real
  /// `WarmupSupervision.step` fold reaches a terminal state (Faulted or
  /// Stopped), no later PollTick — a stale Ready, a late Faulted, an extra
  /// StillWarming tick, all things a worker or a stray poll might still
  /// deliver after the daemon already considers the session decided — ever
  /// changes that decided outcome. This is what "one source of truth" means
  /// for defect #3: whichever caller reads the state, and whenever they
  /// read it, a terminal state reads the same way from then on.
  ///
  /// A `StopRequested` is DELIBERATELY exempt from this check: it is an
  /// intentional command, not a stray observation, and per
  /// `stopAlwaysCompletes` it is REQUIRED to move even an already-Faulted
  /// session to Stopped — that is a feature (defect #2), not a
  /// resurrection. Checked against the REAL reducer specifically (this is
  /// what production must guarantee); the twin isn't exercised against this
  /// invariant, mirroring how StreamingProxyInvariants' twin is only
  /// checked against the ONE invariant its regression targets.
  let noResurrection : Invariant =
    { Id = "no-resurrection"
      Description =
        "Once a prefix of the event list reaches a terminal state, no LATER PollTick resurrects or changes it — an explicit StopRequested may still move Faulted to Stopped, by design."
      Check = fun t ->
        let rec walk (model: LifecycleModel<Loaded>) (remaining: LifecycleEvent<Loaded> list) (terminalSoFar: LifecycleState<Loaded> option) : Outcome =
          match remaining with
          | [] -> Outcome.Holds
          | (LifecycleEvent.StopRequested as event) :: rest ->
            // Exempt by design — see the doc comment above.
            let next = WarmupSupervision.step model event
            let terminalSoFar' = if isTerminal next.State then Some next.State else terminalSoFar
            walk next rest terminalSoFar'
          | event :: rest ->
            let next = WarmupSupervision.step model event
            match terminalSoFar with
            | Some prior when isTerminal next.State && next.State <> prior ->
              Outcome.Violated(
                sprintf "reducer=%s seed=%d — terminal state changed from %A to %A after event %A" t.Reducer t.Scenario.Seed prior next.State event)
            | Some prior when not (isTerminal next.State) ->
              Outcome.Violated(
                sprintf "reducer=%s seed=%d — an already-terminal state %A became non-terminal (%A) after event %A" t.Reducer t.Scenario.Seed prior next.State event)
            | _ ->
              let terminalSoFar' = if isTerminal next.State then Some next.State else terminalSoFar
              walk next rest terminalSoFar'
        walk (LifecycleModel.starting t.Scenario.Bounds) t.Scenario.Events None }

  let all : Invariant list =
    [ boundedReachAbsolute; silenceIsCaughtByInactivity; stopAlwaysCompletes; noResurrection ]

  /// The invariants that failed for a trace, if any (id, message).
  let violations (t: Trace) : (string * string) list =
    all
    |> List.choose (fun inv ->
      match inv.Check t with
      | Outcome.Holds -> None
      | Outcome.Violated msg -> Some(inv.Id, msg))
