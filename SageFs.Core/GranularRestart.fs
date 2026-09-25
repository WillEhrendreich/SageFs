namespace SageFs

open System

// ── Granular restart: the decision names its subject ─────────────────
//
// `type-migration-direction.md` step 1, and the first prerequisite for
// making a redefined type survivable. A type change today restarts the whole
// app, even when one unit held the old value. That is expensive because
// `RestartPolicy.Decision` is `Restart of delay | GiveUp of SageFsError`: it
// has no subject, so nothing downstream can tell "restart the worker" from
// "restart the unit holding the old instance" — and `RestartPolicy.State` is a
// single shared counter, so two subjects cannot be tracked independently.
//
// This module adds the subject as a VALUE and threads it through the existing
// policy unchanged. It is a strict extension: `RestartPolicy.decide` is still
// the only thing that decides, and every rule it already enforces
// (exponential backoff, the reset window, the startup-crash circuit breaker)
// still applies per subject.
//
// Pure by construction: no IO, and the clock is a parameter rather than a
// read, so the DST harness folds it deterministically. Illegal states are
// unrepresentable rather than validated: a verdict cannot exist without its
// subject, and the subject is a closed DU, so "a restart of something" is not
// constructible.
module GranularRestart =

  /// What a restart decision is about.
  ///
  /// A `Worker` subject is today's behaviour and the coarse tier. A
  /// `UnitScope` subject is the granular tier: only the unit named here is
  /// rebuilt, which is what makes "a type change restarts the part that held
  /// the old value" expressible at all.
  [<RequireQualifiedAccess>]
  type RestartSubject =
    /// The whole worker process — today's only option.
    | Worker
    /// A single named unit, e.g. a DI singleton or an agent's mailbox.
    | UnitScope of name: string

  /// A decision that carries its subject, so no consumer has to know it out of
  /// band. `attempt` is the 1-based restart number the policy counted, which
  /// the old `Restart of delay` threw away.
  [<RequireQualifiedAccess>]
  type Verdict =
    | Restart of subject: RestartSubject * delay: TimeSpan * attempt: int
    | GiveUp of subject: RestartSubject * because: SageFsError

  /// Per-subject restart tracking. A plain alias so this module can name what
  /// it is, and so a subject's budget reads as a budget rather than as a bare
  /// `State`.
  type Budget = RestartPolicy.State

  /// Restarts recorded per subject, keyed by its stable label. A map is what
  /// makes "one subject's exhaustion never spends another's budget" true by
  /// construction rather than by discipline.
  type Budgets = Map<string, Budget>

  /// The neutral starting point: no restarts yet, for any subject.
  let emptyBudget : Budget = RestartPolicy.emptyState

  /// `RestartPolicy.Policy`, aliased so callers of this module need not open
  /// both.
  let defaultPolicy : RestartPolicy.Policy = RestartPolicy.defaultPolicy

  /// No subject has spent anything.
  let emptyBudgets : Budgets = Map.empty

  /// The stable, user-facing name for a subject. This string reaches a restart
  /// reason and the dashboard, so it lives beside the DU rather than being
  /// formatted at each call site.
  let describe (subject: RestartSubject) : string =
    match subject with
    | RestartSubject.Worker -> "worker"
    | RestartSubject.UnitScope name -> sprintf "unit:%s" name

  /// Every subject the DST harness folds through. Adding a subject kind means
  /// adding it here, so a new kind cannot ship without a label.
  let allSubjects : RestartSubject list =
    [ RestartSubject.Worker
      RestartSubject.UnitScope "order-store" ]

  /// The budget a subject has spent, or the neutral one if it never has.
  let budgetOf (budgets: Budgets) (subject: RestartSubject) : Budget =
    budgets |> Map.tryFind (describe subject) |> Option.defaultValue emptyBudget

  /// Decide, and name the subject. `RestartPolicy.decide` remains the only
  /// decision function: this only attaches the subject and the attempt
  /// number, so no rule is restated here and none can drift.
  let decide
    (subject: RestartSubject)
    (policy: RestartPolicy.Policy)
    (budget: Budget)
    (now: DateTime)
    : Verdict * Budget =
    let decision, next = RestartPolicy.decide policy budget now

    match decision with
    | RestartPolicy.Decision.Restart delay ->
      Verdict.Restart(subject, delay, budget.RestartCount + 1), next
    | RestartPolicy.Decision.GiveUp because ->
      Verdict.GiveUp(subject, because), next

  /// Decide for one subject against its own budget, and record the new budget
  /// only for that subject.
  ///
  /// A give-up neither spends more budget nor clears what was already spent:
  /// the subject is still exhausted, and forgetting that would let a later
  /// call hand it a fresh budget.
  let decideFor
    (budgets: Budgets)
    (subject: RestartSubject)
    (policy: RestartPolicy.Policy)
    (now: DateTime)
    : Verdict * Budgets =
    let budget = budgetOf budgets subject
    let verdict, next = decide subject policy budget now

    match verdict with
    | Verdict.Restart _ -> verdict, budgets |> Map.add (describe subject) next
    | Verdict.GiveUp _ -> verdict, budgets

  /// The labels that currently hold a budget. The DST invariants fold over
  /// this, so a subject the sim never exercises is a non-vacuousness failure
  /// rather than a silent pass.
  let subjectsWithBudget (budgets: Budgets) : string list =
    budgets |> Map.toList |> List.map fst
