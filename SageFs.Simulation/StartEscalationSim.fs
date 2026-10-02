namespace SageFs.Simulation

open System
open SageFs

/// Deterministic Simulation Testing (DST) for starting a worker on a machine whose speed is not
/// known in advance (`SageFs.Core/StartEscalation.fs`).
///
/// THE HISTORICAL BUG (the twins below reproduce its shape). On a 2009 four core with a spinning
/// disk, a session could not start. The daemon gave a starting worker 30 seconds of silence, the
/// worker needed about 40, and the worker was killed and started again. Five times, each attempt
/// with the same 30 seconds, each doomed the same way, a doomed loop that also kept the CPU busy.
/// The user then saw "Worker process exited with code 137 (abandoned after max retries)", which
/// names neither what was being waited for nor how long.
///
/// WHY THE REAL CODE IS THE SUBJECT: `run` calls `StartEscalation.firstBudget` and
/// `StartEscalation.next` directly. `runSameBudget` and `runUnbounded` are SEPARATE, deliberately
/// regressed reducers standing in for the old loop and for a loop with no end, so the invariants
/// in `StartEscalationInvariants` can be shown to discriminate, not to hold vacuously.
module StartEscalationSim =

  /// What the machine does to each attempt to start.
  [<RequireQualifiedAccess>]
  type Machine =
    /// Attempt k needs the k-th duration, silently (no progress is reported); the last repeats.
    | NeedsPerAttempt of needs: TimeSpan list
    /// No attempt ever finishes.
    | Hangs

  type Scenario =
    { Seed: int
      Tier: MachineTier
      History: StageHistory
      /// The tier-scaled silence allowance a first attempt gets with no history.
      StaticInactivity: TimeSpan
      Absolute: TimeSpan
      Machine: Machine }

  /// How a failure was reported: with the whole story, or as the old loop said it.
  [<RequireQualifiedAccess>]
  type FailureReport =
    | Named of StartFailure
    | Vague of message: string

  [<RequireQualifiedAccess>]
  type StartOutcome =
    | Succeeded of attempt: int * took: TimeSpan
    | GaveUp of FailureReport

  type Trace =
    { Scenario: Scenario
      Reducer: string
      /// The silence each attempt made was allowed.
      Budgets: TimeSpan list
      /// How long each attempt made actually waited.
      Waited: TimeSpan list
      Outcome: StartOutcome }

  /// How long attempt `attempt` (1-based) needs. A hung machine needs forever.
  let needOf (machine: Machine) (attempt: int) : TimeSpan =
    match machine with
    | Machine.Hangs -> TimeSpan.MaxValue
    | Machine.NeedsPerAttempt [] -> TimeSpan.Zero
    | Machine.NeedsPerAttempt needs -> needs |> List.item (min (attempt - 1) (List.length needs - 1))

  let private silentTimeout (budget: StartBudget) : StartTimeout =
    { Stage = StartStage.WorkerPort
      Budget = budget
      Waited = budget.Inactivity
      Progress = ProgressSeen.NoneYet }

  /// Run through the REAL escalation.
  let run (scenario: Scenario) : Trace =
    let rec attempt (budget: StartBudget) (budgets: TimeSpan list) (waited: TimeSpan list) : Trace =
      let need = needOf scenario.Machine budget.Attempt
      match need <= budget.Inactivity with
      | true ->
        { Scenario = scenario
          Reducer = "real (StartEscalation.firstBudget and next)"
          Budgets = budgets @ [ budget.Inactivity ]
          Waited = waited @ [ need ]
          Outcome = StartOutcome.Succeeded (budget.Attempt, need) }
      | false ->
        match StartEscalation.next scenario.Tier scenario.History (silentTimeout budget) with
        | EscalationStep.RetryWith bigger ->
          attempt bigger (budgets @ [ budget.Inactivity ]) (waited @ [ budget.Inactivity ])
        | EscalationStep.GiveUp failure ->
          { Scenario = scenario
            Reducer = "real (StartEscalation.firstBudget and next)"
            Budgets = budgets @ [ budget.Inactivity ]
            Waited = waited @ [ budget.Inactivity ]
            Outcome = StartOutcome.GaveUp (FailureReport.Named failure) }
    attempt (StartEscalation.firstBudget scenario.History scenario.StaticInactivity scenario.Absolute) [] []

  // ---------------------------------------------------------------------
  // TWIN 1: the doomed loop. Every attempt gets the same silence allowance,
  // five restarts, then "abandoned after max retries".
  // ---------------------------------------------------------------------

  /// What the restart policy allowed: the first start and five restarts.
  [<Literal>]
  let private OldLoopStarts = 6

  /// The old loop's whole explanation of its failure.
  [<Literal>]
  let private OldLoopMessage = "Worker process exited with code 137 (abandoned after max retries)"

  let runSameBudget (scenario: Scenario) : Trace =
    let budget = scenario.StaticInactivity
    let rec attempt (n: int) (budgets: TimeSpan list) (waited: TimeSpan list) : Trace =
      let need = needOf scenario.Machine n
      match need <= budget with
      | true ->
        { Scenario = scenario
          Reducer = "twin-same-budget (the old restart loop)"
          Budgets = budgets @ [ budget ]
          Waited = waited @ [ need ]
          Outcome = StartOutcome.Succeeded (n, need) }
      | false when n >= OldLoopStarts ->
        { Scenario = scenario
          Reducer = "twin-same-budget (the old restart loop)"
          Budgets = budgets @ [ budget ]
          Waited = waited @ [ budget ]
          Outcome = StartOutcome.GaveUp (FailureReport.Vague OldLoopMessage) }
      | false -> attempt (n + 1) (budgets @ [ budget ]) (waited @ [ budget ])
    attempt 1 [] []

  // ---------------------------------------------------------------------
  // TWIN 2: escalation with no end. Doubles forever, never gives up.
  // ---------------------------------------------------------------------

  /// A fuse so the twin itself terminates when the machine hangs. It is far past any real bound.
  [<Literal>]
  let private UnboundedFuse = 40

  let runUnbounded (scenario: Scenario) : Trace =
    let rec attempt (n: int) (inactivity: TimeSpan) (budgets: TimeSpan list) (waited: TimeSpan list) : Trace =
      let need = needOf scenario.Machine n
      match need <= inactivity with
      | true ->
        { Scenario = scenario
          Reducer = "twin-unbounded (doubles for ever)"
          Budgets = budgets @ [ inactivity ]
          Waited = waited @ [ need ]
          Outcome = StartOutcome.Succeeded (n, need) }
      | false when n >= UnboundedFuse ->
        { Scenario = scenario
          Reducer = "twin-unbounded (doubles for ever)"
          Budgets = budgets @ [ inactivity ]
          Waited = waited @ [ inactivity ]
          Outcome = StartOutcome.GaveUp (FailureReport.Vague "the fuse blew: this loop never ends by itself") }
      | false ->
        attempt (n + 1) (TimeSpan.FromTicks(inactivity.Ticks * 2L)) (budgets @ [ inactivity ]) (waited @ [ inactivity ])
    attempt 1 scenario.StaticInactivity [] []
