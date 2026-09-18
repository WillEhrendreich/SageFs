namespace SageFs.Simulation

open System
open SageFs
open SageFs.Simulation.Scenario
open SageFs.Simulation.Invariants

/// Coverage matrix over a DST battery: which effect kinds actually occurred
/// across a set of traces, and — more importantly — which invariants'
/// ANTECEDENTS were actually hit by at least one trace, as opposed to merely
/// "held" because the antecedent never occurred.
///
/// This directly answers the roast's §9 "is this vacuously green?" concern: a
/// battery that reports every invariant as Holds proves nothing about
/// give-up-terminal if no trace in the battery ever produced a GiveUp. The
/// coverage matrix turns "the suite is green" into "the suite is green AND it
/// actually exercised every branch it claims to."
module Coverage =

  /// The distinct shapes a folded step can take, as observed across a trace.
  /// CircuitBreakerDip and WindowReset are refinements of Restarted: every
  /// dip/reset is also counted as a plain Restarted, but the finer kind lets
  /// the battery prove it hit the harder-to-reach sub-cases too.
  [<RequireQualifiedAccess>]
  type EffectKind =
    | Restarted
    | GaveUp
    | Stopped
    | NoEffectTerminal
    | ClockOnly
    | CircuitBreakerDip
    | WindowReset

  /// Whether one named invariant's ANTECEDENT was exercised by the battery —
  /// never merely whether `Check` returned Holds (an invariant whose
  /// antecedent never occurs trivially Holds, and that is exactly the
  /// vacuous-green case this type exists to catch).
  type InvariantHit =
    { Id: string
      Exercised: bool }

  type CoverageReport =
    { ScenarioCount: int
      Effects: Set<EffectKind>
      Invariants: InvariantHit list
      UncoveredEffects: Set<EffectKind> }

  let private allEffectKinds : Set<EffectKind> =
    Set.ofList
      [ EffectKind.Restarted; EffectKind.GaveUp; EffectKind.Stopped
        EffectKind.NoEffectTerminal; EffectKind.ClockOnly
        EffectKind.CircuitBreakerDip; EffectKind.WindowReset ]

  /// The startup-crash circuit breaker's fixed delay: a flat 4x-base, capped
  /// at BackoffMax. Deliberately duplicated from Invariants.fs's private
  /// helper of the same formula — Invariants only needs this value to EXCUSE
  /// a dip; Coverage needs it to POSITIVELY identify when a dip occurred, so
  /// it is not sharable through Invariants' public surface without exposing
  /// an implementation detail there. Keeping the (tiny, stable) formula
  /// local avoids coupling the two modules' internals.
  let private circuitBreakerDelay (p: RestartPolicy.Policy) : TimeSpan =
    let fourX = p.BackoffBase.TotalMilliseconds * 4.0
    TimeSpan.FromMilliseconds(min fourX p.BackoffMax.TotalMilliseconds)

  /// Only Restarted steps carry window/delay information relevant to dip and
  /// window-reset detection; every other step is irrelevant to this trail.
  let private restartTrail (t: Trace) : (DateTime option * TimeSpan) list =
    t.Steps
    |> List.choose (fun s ->
      match s.Effect with
      | StepEffect.Restarted delay -> Some(s.RestartState.WindowStart, delay)
      | StepEffect.NoEffect
      | StepEffect.Stopped
      | StepEffect.GaveUp _ -> None)

  /// The size of each window's restart group (how many restarts shared one
  /// WindowStart) — the same grouping `backoff-monotonic-in-window` and
  /// `crash-storm-terminates` reason about, used here to detect whether their
  /// antecedents were exercised.
  let private restartGroupSizes (t: Trace) : int list =
    restartTrail t
    |> List.groupBy fst
    |> List.map (fun (_, g) -> List.length g)

  /// Walk the restart trail in order, detecting circuit-breaker dips (a
  /// same-window delay that drops below the running max, landing exactly on
  /// the circuit-breaker constant — the one legitimate dip
  /// `backoff-monotonic-in-window` allows) and window resets (a restart whose
  /// WindowStart differs from the immediately preceding restart's, i.e. the
  /// ResetWindow expired and the count started over).
  let private dipAndResetKinds (t: Trace) : Set<EffectKind> =
    let cb = circuitBreakerDelay t.Scenario.Policy

    let rec walk
      (prev: (DateTime option * TimeSpan) option)
      (acc: Set<EffectKind>)
      (items: (DateTime option * TimeSpan) list)
      : Set<EffectKind> =
      match items with
      | [] -> acc
      | (window, delay) :: rest ->
        match prev with
        | Some(prevWindow, runningMax) when prevWindow = window ->
          let acc' =
            match delay < runningMax && delay = cb with
            | true -> Set.add EffectKind.CircuitBreakerDip acc
            | false -> acc
          walk (Some(window, max runningMax delay)) acc' rest
        | Some(prevWindow, _) when prevWindow <> window ->
          walk (Some(window, delay)) (Set.add EffectKind.WindowReset acc) rest
        | _ -> walk (Some(window, delay)) acc rest

    walk None Set.empty (restartTrail t)

  /// Which effect kinds actually occurred in one trace.
  let effectsCovered (t: Trace) : Set<EffectKind> =
    let simple =
      t.Steps
      |> List.fold
        (fun acc s ->
          match s.Effect with
          | StepEffect.Restarted _ -> Set.add EffectKind.Restarted acc
          | StepEffect.GaveUp _ -> Set.add EffectKind.GaveUp acc
          | StepEffect.Stopped -> Set.add EffectKind.Stopped acc
          | StepEffect.NoEffect ->
            match s.Event with
            | SimEvent.ClockAdvance _ -> Set.add EffectKind.ClockOnly acc
            | SimEvent.WorkerCrashed
            | SimEvent.WorkerExitedGracefully -> Set.add EffectKind.NoEffectTerminal acc)
        Set.empty
    Set.union simple (dipAndResetKinds t)

  /// give-up-terminal's antecedent: a GiveUp actually occurred.
  let private exercisesGiveUpTerminal (t: Trace) : bool =
    t.Steps
    |> List.exists (fun s ->
      match s.Effect with
      | StepEffect.GaveUp _ -> true
      | StepEffect.NoEffect
      | StepEffect.Stopped
      | StepEffect.Restarted _ -> false)

  /// backoff-monotonic-in-window's antecedent: at least one window saw two or
  /// more restarts — a single restart in a window has nothing to compare
  /// monotonicity against.
  let private exercisesBackoffMonotonic (t: Trace) : bool =
    restartGroupSizes t |> List.exists (fun n -> n >= 2)

  /// crash-storm-terminates' antecedent: some window's restart count actually
  /// reached the effective ceiling (max of MaxRestarts and
  /// StartupCrashMaxRestarts) — the point at which the liveness bound is
  /// genuinely tested, not just trivially satisfied by a short trace.
  let private exercisesCrashStormTerminates (t: Trace) : bool =
    let ceiling = max t.Scenario.Policy.MaxRestarts t.Scenario.Policy.StartupCrashMaxRestarts
    restartGroupSizes t |> List.exists (fun n -> n = ceiling)

  /// Phase 1's three named invariants paired with the predicate that decides
  /// whether a single trace exercises that invariant's antecedent.
  let private invariantAntecedents : (Invariant * (Trace -> bool)) list =
    [ giveUpTerminal, exercisesGiveUpTerminal
      backoffMonotonicInWindow, exercisesBackoffMonotonic
      crashStormTerminates, exercisesCrashStormTerminates ]

  /// Compute the coverage report for a battery of traces. Deterministic over
  /// a fixed battery — no randomness here, only aggregation.
  let over (traces: Trace seq) : CoverageReport =
    let traces = List.ofSeq traces
    let effects =
      traces
      |> List.map effectsCovered
      |> List.fold Set.union Set.empty
    let invariants =
      invariantAntecedents
      |> List.map (fun (inv, exercisedFn) ->
        { Id = inv.Id
          Exercised = traces |> List.exists exercisedFn })
    { ScenarioCount = List.length traces
      Effects = effects
      Invariants = invariants
      UncoveredEffects = Set.difference allEffectKinds effects }

  /// A printable coverage matrix.
  let render (report: CoverageReport) : string =
    let effectLines =
      allEffectKinds
      |> Set.toList
      |> List.map (fun k ->
        let status = if report.Effects.Contains k then "COVERED" else "UNCOVERED"
        sprintf "  %-20s %s" (sprintf "%A" k) status)
      |> String.concat "\n"
    let invariantLines =
      report.Invariants
      |> List.map (fun h ->
        let status = if h.Exercised then "EXERCISED" else "NOT EXERCISED (vacuous)"
        sprintf "  %-32s %s" h.Id status)
      |> String.concat "\n"
    sprintf
      "DST Coverage Report — %d scenarios\nEffects:\n%s\nInvariants:\n%s"
      report.ScenarioCount
      effectLines
      invariantLines
