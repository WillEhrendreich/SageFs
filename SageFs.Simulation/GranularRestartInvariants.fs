module SageFs.Simulation.GranularRestartInvariants

/// WHY — the granular-restart sim exists to prove one thing that matters more
/// than the individual decisions: a subject's budget is ITS OWN. If one
/// subject's exhaustion can spend another's, the feature is worse than
/// useless — a busy unit would take the whole worker down with it.
///
/// Each invariant below is paired with a TWIN that reintroduces a specific
/// wrong model. A twin that does not break its invariant means the invariant
/// is vacuous, which the test suite treats as a failure.

open SageFs
open SageFs.GranularRestart
open SageFs.Simulation.GranularRestartSim

/// A violation names the seed so the scenario replays exactly.
type Violation =
  { Seed: int
    Trace: Trace
    Why: string }

let private fail seed trace why : Violation list = [ { Seed = seed; Trace = trace; Why = why } ]

/// SAFETY — the worker must hold a budget only if the WORKER actually crashed.
/// A unit crashing many times must never charge the worker anything, because
/// the worker was never asked to restart. This is the isolation property: each
/// subject spends only its own budget.
let neverPatchedOverAnotherSubjectsExhaustion (trace: Trace) : Violation list =
  let workerCrashed =
    trace.Steps
    |> List.exists (fun s ->
      match s.Subject with
      | RestartSubject.Worker -> true
      | RestartSubject.UnitScope _ -> false)

  let unitCrashed =
    trace.Steps
    |> List.exists (fun s ->
      match s.Subject with
      | RestartSubject.UnitScope _ -> true
      | RestartSubject.Worker -> false)

  let workerSpent =
    trace.FinalBudgets
    |> Map.tryFind "worker"
    |> Option.map (fun budget -> budget.RestartCount)
    |> Option.defaultValue 0

  if unitCrashed && not workerCrashed && workerSpent > 0 then
    fail trace.Scenario.Seed trace (sprintf "only units crashed, yet the worker spent %d" workerSpent)
  else
    []

/// A unit's recorded budget must be backed by decisions actually made for it:
/// a subject holding a non-zero budget that never restarted is a ledger that
/// has drifted from reality.
let budgetsTrackTheirOwnDecisions (trace: Trace) : Violation list =
  let restartsFor label =
    trace.Steps
    |> List.filter (fun s -> describe s.Subject = label)
    |> List.filter (fun s ->
      match s.Verdict with
      | Verdict.Restart _ -> true
      | Verdict.GiveUp _ -> false)

  trace.FinalBudgets
  |> Map.toList
  |> List.collect (fun (label, budget) ->
    let actual = restartsFor label |> List.length

    if budget.RestartCount > 0 && actual = 0 then
      fail trace.Scenario.Seed trace (sprintf "%s holds a budget of %d but never restarted" label budget.RestartCount)
    else
      [])

/// A give-up must never hand its subject a fresh budget. Forgetting the spend
/// would let a later call resurrect an exhausted subject.
let giveUpNeverClearsASpentBudget (trace: Trace) : Violation list =
  trace.Steps
  |> List.filter (fun s ->
    match s.Verdict with
    | Verdict.GiveUp _ -> true
    | _ -> false)
  |> List.collect (fun s ->
    let label = describe s.Subject

    match trace.FinalBudgets |> Map.tryFind label with
    | None -> fail trace.Scenario.Seed trace (sprintf "%s gave up but has no recorded budget at all" label)
    | Some budget when budget.RestartCount = 0 -> fail trace.Scenario.Seed trace (sprintf "%s gave up yet its budget was reset to zero" label)
    | Some _ -> [])

/// Every invariant, in one list, for a trace.
let all (trace: Trace) : Violation list =
  neverPatchedOverAnotherSubjectsExhaustion trace
  @ budgetsTrackTheirOwnDecisions trace
  @ giveUpNeverClearsASpentBudget trace

// ── Twins ────────────────────────────────────────────────────────────
// Each reintroduces one wrong model. A twin that does NOT break its
// invariant proves the invariant is vacuous.

/// TWIN — the pre-fix shared counter: every subject's budget is filed under
/// the worker, so a unit's crashes spend — and can exhaust — the worker's
/// budget. Run the SAME scenario under `sharedCounter` and the isolation
/// invariant must fire, which is what proves the real model earns it.
let sharedCounterBreaksIsolation (scenario: Scenario) : bool =
  let trace = runSharedCounter scenario
  not (List.isEmpty (neverPatchedOverAnotherSubjectsExhaustion trace))

/// TWIN — a give-up that clears the budget, so an exhausted subject is
/// resurrected on the very next call. Run the same scenario under the
/// `giveUpResets` wiring: a subject that gave up must end up holding a budget
/// of zero, which the real invariant forbids.
let giveUpResetsBreaks (scenario: Scenario) : bool =
  let trace = runGiveUpResets scenario
  not (List.isEmpty (giveUpNeverClearsASpentBudget trace))
