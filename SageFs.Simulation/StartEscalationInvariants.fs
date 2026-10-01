namespace SageFs.Simulation

open System
open SageFs
open SageFs.Simulation.StartEscalationSim

/// Named invariants over a `StartEscalationSim.Trace`. Same stable-id / `Holds`-vs-`Violated` shape
/// as `WarmupInvariants`.
module StartEscalationInvariants =

  [<RequireQualifiedAccess>]
  type Outcome =
    | Holds
    | Violated of message: string

  type Invariant =
    { Id: string
      Description: string
      Check: Trace -> Outcome }

  /// never-retry-with-a-smaller-budget: each attempt after the first is allowed MORE silence than the
  /// one before it. Retrying with the same allowance repeats the failure; with less, it guarantees it.
  let neverRetryWithASmallerBudget : Invariant =
    { Id = "never-retry-with-a-smaller-budget"
      Description = "Every attempt after the first is given strictly more silence than the attempt before it."
      Check = fun t ->
        let pairs = t.Budgets |> List.pairwise
        match pairs |> List.tryFind (fun (a, b) -> b <= a) with
        | Some (a, b) ->
          Outcome.Violated(
            sprintf "reducer=%s seed=%d: an attempt was given %.0fs after one that was given %.0fs (all: %A)"
              t.Reducer t.Scenario.Seed b.TotalSeconds a.TotalSeconds (t.Budgets |> List.map (fun x -> x.TotalSeconds)))
        | None -> Outcome.Holds }

  /// How much silence the real schedule would allow across all its attempts, last one included. The
  /// machine "can" start if some attempt in that schedule is allowed as long as the machine needs.
  let private canStartWithinSchedule (s: Scenario) : bool =
    let rec walk (budget: StartBudget) : bool =
      let need = needOf s.Machine budget.Attempt
      match need <= budget.Inactivity with
      | true -> true
      | false ->
        let timeout : StartTimeout =
          { Stage = StartStage.WorkerPort; Budget = budget; Waited = budget.Inactivity; Progress = ProgressSeen.NoneYet }
        match StartEscalation.next s.Tier s.History timeout with
        | EscalationStep.RetryWith bigger -> walk bigger
        | EscalationStep.GiveUp _ -> false
    walk (StartEscalation.firstBudget s.History s.StaticInactivity s.Absolute)

  /// eventually-succeeds-if-the-machine-can: a machine that finishes a start inside the patience the
  /// schedule reaches is never reported as failed.
  let eventuallySucceedsIfTheMachineCan : Invariant =
    { Id = "eventually-succeeds-if-the-machine-can"
      Description = "If some attempt in the escalation schedule is patient enough for the machine, the start succeeds."
      Check = fun t ->
        match canStartWithinSchedule t.Scenario, t.Outcome with
        | true, StartOutcome.GaveUp _ ->
          Outcome.Violated(
            sprintf "reducer=%s seed=%d: the machine needs %A per attempt, the schedule reaches it, and the start was given up on (budgets %A)"
              t.Reducer t.Scenario.Seed t.Scenario.Machine (t.Budgets |> List.map (fun x -> x.TotalSeconds)))
        | _ -> Outcome.Holds }

  /// no-unbounded-loop: attempts are bounded, and so is the time they can add up to.
  let noUnboundedLoop : Invariant =
    { Id = "no-unbounded-loop"
      Description = "At most StartEscalation.MaxAttempts attempts, and none waits longer than the absolute bound."
      Check = fun t ->
        match List.length t.Budgets > StartEscalation.MaxAttempts with
        | true ->
          Outcome.Violated(
            sprintf "reducer=%s seed=%d: %d attempts made (limit %d)" t.Reducer t.Scenario.Seed (List.length t.Budgets) StartEscalation.MaxAttempts)
        | false ->
          match t.Budgets |> List.tryFind (fun b -> b > t.Scenario.Absolute) with
          | Some b ->
            Outcome.Violated(
              sprintf "reducer=%s seed=%d: an attempt was given %.0fs, past the absolute bound of %.0fs" t.Reducer t.Scenario.Seed b.TotalSeconds t.Scenario.Absolute.TotalSeconds)
          | None -> Outcome.Holds }

  /// failure-names-the-wait: a failure says what it was waiting for, how long, how many times and on
  /// what tier, and says it in the text a person reads, not only in fields.
  let failureNamesTheWait : Invariant =
    { Id = "failure-names-the-wait"
      Description = "A given-up start names what it waited for, each attempt's allowance, the total, the attempts and the tier."
      Check = fun t ->
        match t.Outcome with
        | StartOutcome.Succeeded _ -> Outcome.Holds
        | StartOutcome.GaveUp (FailureReport.Vague message) ->
          Outcome.Violated(sprintf "reducer=%s seed=%d: the failure is only '%s'" t.Reducer t.Scenario.Seed message)
        | StartOutcome.GaveUp (FailureReport.Named failure) ->
          let text = StartEscalation.describe failure
          let missing =
            [ StartStage.describe failure.Stage
              MachineTier.toString failure.Tier
              sprintf "%d attempt" failure.Attempts ]
            @ (failure.Budgets |> List.map (fun b -> sprintf "%.0f s" b.TotalSeconds))
            |> List.filter (fun needle -> not (text.Contains(needle, StringComparison.Ordinal)))
          let countsAgree =
            failure.Attempts = List.length failure.Budgets && failure.Attempts = List.length t.Budgets
          match missing, countsAgree with
          | [], true -> Outcome.Holds
          | _ ->
            Outcome.Violated(
              sprintf "reducer=%s seed=%d: the failure text lacks %A, or its counts disagree with the attempts made. Text: %s" t.Reducer t.Scenario.Seed missing text) }

  /// the-first-attempt-is-never-less-patient-than-the-machine-has-shown: the first allowance is at
  /// least what the tier gives, and at least what the learned estimate asks for (up to the cap).
  let firstAttemptHonoursWhatWasLearned : Invariant =
    { Id = "first-attempt-honours-what-was-learned"
      Description = "The first attempt's silence allowance is at least the tier's and at least the learned timeout, capped at the absolute bound."
      Check = fun t ->
        match t.Budgets with
        | [] -> Outcome.Holds
        | first :: _ ->
          let learned =
            match t.Scenario.History with
            | StageHistory.NeverSeen -> TimeSpan.Zero
            | StageHistory.Seen e -> StageEstimate.timeout e
          let floor' = min (max t.Scenario.StaticInactivity learned) t.Scenario.Absolute
          match first >= floor' with
          | true -> Outcome.Holds
          | false ->
            Outcome.Violated(
              sprintf "reducer=%s seed=%d: the first attempt was given %.0fs but the tier and the history ask for %.0fs"
                t.Reducer t.Scenario.Seed first.TotalSeconds floor'.TotalSeconds) }

  let all : Invariant list =
    [ neverRetryWithASmallerBudget
      eventuallySucceedsIfTheMachineCan
      noUnboundedLoop
      failureNamesTheWait
      firstAttemptHonoursWhatWasLearned ]

  /// The invariants that failed for a trace, if any (id, message).
  let violations (t: Trace) : (string * string) list =
    all
    |> List.choose (fun inv ->
      match inv.Check t with
      | Outcome.Holds -> None
      | Outcome.Violated msg -> Some (inv.Id, msg))
