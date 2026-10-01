namespace SageFs.Simulation

open System
open SageFs

/// Deterministic Simulation Testing (DST) for what a machine teaches SageFs over many starts: the first
/// start on a slow machine may need a second attempt, and then it should not again. The real code is the
/// subject: `run` calls `StartEscalation.firstBudget` and `StartEscalation.next`, and folds each start
/// that succeeds into the history with `StageEstimate.observe`, as the ledger does. `runWithoutMemory` is
/// the twin that never learns: every start begins as the first ever did.
module StartLearningSim =

  type Scenario =
    { Seed: int
      Tier: MachineTier
      /// The tier-scaled silence allowance a first attempt gets with no history.
      StaticInactivity: TimeSpan
      Absolute: TimeSpan
      /// How long each successive start of a session needs, in order, with the worker silent throughout.
      Starts: TimeSpan list }

  type Session =
    { Index: int
      Need: TimeSpan
      FirstAllowance: TimeSpan
      Attempts: int
      Started: bool }

  type Trace =
    { Scenario: Scenario
      Reducer: string
      Sessions: Session list
      /// What was known of the stage after the last start.
      Final: StageHistory }

  /// One start: the real escalation until it succeeds or gives up. Returns the session and the duration to
  /// learn from (the need of the attempt that succeeded), when one did.
  let private startOnce (scenario: Scenario) (history: StageHistory) (index: int) (need: TimeSpan) : Session * TimeSpan option =
    let first = StartEscalation.firstBudget history scenario.StaticInactivity scenario.Absolute
    let rec attempt (budget: StartBudget) : Session * TimeSpan option =
      match need <= budget.Inactivity with
      | true ->
        { Index = index; Need = need; FirstAllowance = first.Inactivity; Attempts = budget.Attempt; Started = true }, Some need
      | false ->
        let timeout : StartTimeout =
          { Stage = StartStage.WorkerPort; Budget = budget; Waited = budget.Inactivity; Progress = ProgressSeen.NoneYet }
        match StartEscalation.next scenario.Tier history timeout with
        | EscalationStep.RetryWith bigger -> attempt bigger
        | EscalationStep.GiveUp failure ->
          { Index = index; Need = need; FirstAllowance = first.Inactivity; Attempts = failure.Attempts; Started = false }, None
    attempt first

  let private foldSessions (reducer: string) (learn: bool) (scenario: Scenario) : Trace =
    let sessions, final =
      scenario.Starts
      |> List.indexed
      |> List.fold
        (fun (done', history) (index, need) ->
          let session, learned = startOnce scenario history index need
          let next =
            match learn, learned with
            | true, Some took ->
              (match history with
               | StageHistory.NeverSeen -> StageHistory.Seen (StageEstimate.first StartStage.WorkerPort took.TotalMilliseconds)
               | StageHistory.Seen estimate -> StageHistory.Seen (StageEstimate.observe estimate took.TotalMilliseconds))
            | _ -> history
          done' @ [ session ], next)
        ([], StageHistory.NeverSeen)
    { Scenario = scenario; Reducer = reducer; Sessions = sessions; Final = final }

  /// Run through the REAL escalation, learning from every start that succeeds.
  let run (scenario: Scenario) : Trace =
    foldSessions "real (firstBudget, next, and StageEstimate.observe)" true scenario

  /// TWIN: a machine that learns nothing. Every start is the first start ever.
  let runWithoutMemory (scenario: Scenario) : Trace =
    foldSessions "twin-no-memory (history never updated)" false scenario
