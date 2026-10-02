namespace SageFs.Simulation

open System
open SageFs
open SageFs.Simulation.StartLearningSim

/// Named invariants over a `StartLearningSim.Trace`.
module StartLearningInvariants =

  [<RequireQualifiedAccess>]
  type Outcome =
    | Holds
    | Violated of message: string

  type Invariant =
    { Id: string
      Description: string
      Check: Trace -> Outcome }

  /// After a start succeeded taking D, the next first attempt is allowed at least D (up to the absolute
  /// bound): the timeout, mean plus four deviations, never sits below the latest observation.
  let nextFirstAttemptCoversTheLastStart : Invariant =
    { Id = "next-first-attempt-covers-the-last-start"
      Description = "A start that succeeded is never followed by a first attempt allowed less than it took (up to the absolute bound)."
      Check = fun t ->
        let pairs = t.Sessions |> List.pairwise
        match pairs |> List.tryFind (fun (previous, next) -> previous.Started && next.FirstAllowance < min previous.Need t.Scenario.Absolute) with
        | Some (previous, next) ->
          Outcome.Violated(
            sprintf "reducer=%s seed=%d: start %d took %.0fs but start %d was only allowed %.0fs of silence"
              t.Reducer t.Scenario.Seed previous.Index previous.Need.TotalSeconds next.Index next.FirstAllowance.TotalSeconds)
        | None -> Outcome.Holds }

  /// On a machine whose starts are stable (every start within a quarter of the same duration, and short of
  /// the absolute bound) the waste ends: from the second start on, no start needs a second attempt.
  let stableMachinesStopWasting : Invariant =
    { Id = "stable-machines-stop-wasting"
      Description = "When every start needs about the same time, no start after the first needs more than one attempt."
      Check = fun t ->
        let needs = t.Scenario.Starts |> List.map (fun s -> s.TotalSeconds)
        match needs with
        | [] -> Outcome.Holds
        | _ ->
          let low, high = List.min needs, List.max needs
          let stable = high <= low * 1.25 && TimeSpan.FromSeconds high < t.Scenario.Absolute
          match stable, t.Sessions |> List.skip 1 |> List.tryFind (fun s -> s.Attempts > 1) with
          | true, Some wasted ->
            Outcome.Violated(
              sprintf "reducer=%s seed=%d: start %d needed %d attempts on a machine whose starts are all within a quarter of each other (%.0fs to %.0fs)"
                t.Reducer t.Scenario.Seed wasted.Index wasted.Attempts low high)
          | _ -> Outcome.Holds }

  let all : Invariant list = [ nextFirstAttemptCoversTheLastStart; stableMachinesStopWasting ]

  let violations (t: Trace) : (string * string) list =
    all
    |> List.choose (fun inv ->
      match inv.Check t with
      | Outcome.Holds -> None
      | Outcome.Violated msg -> Some (inv.Id, msg))
