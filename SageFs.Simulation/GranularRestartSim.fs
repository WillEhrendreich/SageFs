module SageFs.Simulation.GranularRestartSim

/// WHY — the granular-restart decision is a state machine over two subjects
/// and a shared clock, and its whole value is that one subject's exhaustion
/// never spends another's. That is exactly the kind of claim a hand-written
/// example test cannot establish: it needs every interleaving, and it needs a
/// TWIN that reintroduces the shared-counter bug so the invariants are proven
/// to have teeth rather than passing vacuously.
///
/// This sim folds the REAL `GranularRestart.decideFor`, which delegates to the
/// real `RestartPolicy.decide`. It restates no rule. The wiring is injected
/// (like `ValueReadSim`'s `Wiring`) so a twin can substitute the pre-fix
/// "one shared counter for everything" model and must break an invariant.
///
/// Pure: no IO, no ambient clock (the scenario supplies every timestamp), and
/// the seed drives everything, so a failure replays exactly.

open System

open SageFs
open SageFs.GranularRestart

/// A fixed reference instant so scenarios read as small comparable offsets
/// rather than `DateTime.UtcNow` noise.
let baseTimeUtc = DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)

/// One thing that went wrong, and when.
type Event =
  /// `subject` failed and the caller wants a decision for it at `atOffset`.
  | Crash of subject: RestartSubject * atOffset: float

/// The ordered operations a scenario performs.
type Scenario =
  { Seed: int
    Subject: RestartSubject
    Policy: RestartPolicy.Policy
    /// Seconds between successive crashes of the scenario's subject.
    CrashEvery: float
    /// How many crashes to perform.
    Crashes: int
    /// When present, also crash this subject, interleaved, to prove the
    /// budgets are independent. A `None` is the coarse-only case.
    OtherSubject: RestartSubject option
    /// Seconds between the other subject's crashes.
    OtherEvery: float
    OtherCrashes: int }

/// The injected decision function, so a twin can substitute a wrong model.
type Wiring = {
  Decide: RestartSubject -> RestartPolicy.Policy -> Budget -> DateTime -> Verdict * Budget
  /// Where a decision's resulting budget is STORED. The real wiring stores it
  /// under the subject that actually failed; the pre-fix wiring stored every
  /// budget under the worker, so all subjects shared one counter. Making the
  /// key the ONLY difference is what proves cross-subject isolation is what
  /// the invariants are really testing.
  KeyOf: RestartSubject -> string
}

/// The real wiring: the production decision, with the budget keyed by the
/// subject that failed.
let real : Wiring =
  { Decide = GranularRestart.decide
    KeyOf = describe }

/// The pre-fix model: the same decision, but every budget filed under the
/// worker, so a unit's exhaustion is charged to — and shared with — the worker.
let sharedCounter : Wiring =
  { Decide = GranularRestart.decide
    KeyOf = fun _ -> describe RestartSubject.Worker }

/// A second wrong model: a give-up RESETS the subject's budget, so an
/// exhausted subject is handed a fresh allowance on the next call and can
/// never actually stay down.
let giveUpResets : Wiring =
  { Decide = fun subject policy budget now ->
      let verdict, next = GranularRestart.decide subject policy budget now

      match verdict with
      | Verdict.GiveUp _ -> verdict, RestartPolicy.emptyState
      | Verdict.Restart _ -> verdict, next
    KeyOf = describe }

/// What actually happened, per step. The trace is the replay evidence.
type Step =
  { At: DateTime
    Subject: RestartSubject
    Verdict: Verdict }

type Trace =
  { Scenario: Scenario
    Steps: Step list
    FinalBudgets: Budgets }

/// Run a scenario under a wiring, deterministically. The ONLY difference
/// between the real model and the pre-fix twin is which key a decision's
/// budget is filed under, so any invariant difference is attributable to
/// cross-subject isolation and nothing else.
let runWith (wiring: Wiring) (scenario: Scenario) : Trace =
  let at offset = baseTimeUtc.AddSeconds offset
  let mutable budgets : Budgets = GranularRestart.emptyBudgets
  let steps = ResizeArray<Step>()

  for i in 1 .. scenario.Crashes do
    let now = at (float i * scenario.CrashEvery)

    // Which subjects fail at this tick. Derived only from the scenario, so
    // the real model and the twin see an identical schedule.
    let other =
      match scenario.OtherSubject with
      | Some other when scenario.OtherCrashes > 0 && i % int scenario.OtherEvery = 0 -> Some other
      | _ -> None

    let subjects =
      match other with
      | Some other -> [ scenario.Subject; other ]
      | None -> [ scenario.Subject ]

    for subject in subjects do
      // The budget is read under the SAME key it will be written under, which
      // is exactly what makes the shared-counter wiring share one counter.
      let key = wiring.KeyOf subject
      let budget = budgets |> Map.tryFind key |> Option.defaultValue RestartPolicy.emptyState
      let verdict, next = wiring.Decide subject scenario.Policy budget now

      steps.Add { At = now; Subject = subject; Verdict = verdict }
      budgets <- budgets |> Map.add key next

  { Scenario = scenario
    Steps = List.ofSeq steps
    FinalBudgets = budgets }

let run (scenario: Scenario) : Trace = runWith real scenario

/// Run the scenario under the pre-fix shared-counter model, for the twin.
let runSharedCounter (scenario: Scenario) : Trace = runWith sharedCounter scenario

/// Run the scenario under the give-up-resets model, for the twin.
let runGiveUpResets (scenario: Scenario) : Trace = runWith giveUpResets scenario

/// The seeded scenarios the invariants fold over.
///
/// The scenario's own subject is ALWAYS a unit, and the interleaved other
/// subject is the worker on some seeds. That is the only shape in which
/// "a unit's crash must not spend the worker's budget" is a statement that can
/// be true or false, so it is the shape every scenario uses.
let scenarioOf (seed: int) : Scenario =
  let other =
    if seed % 2 = 0 then
      Some RestartSubject.Worker
    else
      Some(RestartSubject.UnitScope "session-cache")

  { Seed = seed
    Subject = RestartSubject.UnitScope "order-store"
    Policy = RestartPolicy.defaultPolicy
    CrashEvery = 1.0 + float (seed % 4)
    Crashes = 1 + (seed % 12)
    OtherSubject = other
    OtherEvery = 2.0 + float (seed % 3)
    OtherCrashes = 1 + (seed % 5) }
