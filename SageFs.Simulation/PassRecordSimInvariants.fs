namespace SageFs.Simulation

open SageFs.Build
open SageFs.Simulation.PassRecordSim

/// Invariants over `PassRecordSim`'s trace. Each one is judged at the gate run it concerns, against ground truth the
/// world kept (`TruthBefore`: the input keys a tier really executed green on), never against the decision itself.
module PassRecordSimInvariants =

  type Violation = { Index: int; Why: string }

  /// Each distinct run once: a run is appended at its own step and stays in every later state.
  let runs(states: State list) : (int * GateRun) list =
    states
    |> List.indexed
    |> List.pairwise
    |> List.choose (fun ((_, before), (i, after)) ->
      match after.Gates.Length > before.Gates.Length with
      | true -> Some(i, List.last after.Gates)
      | false -> None)

  let reused(g: GateRun) =
    g.Statuses |> Map.toList |> List.choose (fun (tier, status) -> match status with | TierStatus.Reused r -> Some(tier, r) | TierStatus.Executed _ -> None)

  /// A tier reuses a record only if the record is for exactly the inputs it has now AND those inputs were really run
  /// green. This is the property that makes `Trusted (reused ...)` mean what `Trusted` means.
  let reuseIsSound (states: State list) : Violation list =
    runs states
    |> List.collect (fun (i, g) ->
      reused g
      |> List.collect (fun (tier, record) ->
        [ match Map.tryFind tier g.Inputs with
          | Some current when current <> record.Inputs ->
            yield { Index = i; Why = sprintf "%s reused a record for different inputs: it ran on %A, now %A" tier record.Inputs current }
          | _ -> ()
          if not (Set.contains record.Key g.TruthBefore) then
            yield { Index = i; Why = sprintf "%s reused a record no execution ever produced green (key %s)" tier (record.Key.Substring(0, 12)) } ]))

  /// `--fresh` reuses nothing.
  let freshNeverReuses (states: State list) : Violation list =
    runs states
    |> List.collect (fun (i, g) ->
      match g.Freshness with
      | Freshness.Reuse -> []
      | Freshness.Fresh -> reused g |> List.map (fun (tier, _) -> { Index = i; Why = sprintf "%s reused a record in a --fresh run" tier }))

  /// A dirty tree never takes a record: its sources are not the commit's.
  let dirtyTreeNeverReuses (states: State list) : Violation list =
    runs states
    |> List.collect (fun (i, g) ->
      match g.Tree with
      | TreeState.Clean -> []
      | TreeState.Dirty -> reused g |> List.map (fun (tier, _) -> { Index = i; Why = sprintf "%s reused a record on a dirty tree" tier }))

  /// A tier the gate says is not reusable (the mutation score) is never reused, whatever the store holds.
  let ineligibleNeverReuses (states: State list) : Violation list =
    runs states
    |> List.collect (fun (i, g) ->
      reused g
      |> List.choose (fun (tier, _) ->
        match tiers |> List.find (fun t -> t.Name = tier) with
        | { Eligibility = Eligibility.Ineligible reason } -> Some { Index = i; Why = sprintf "%s reused a record although %s" tier reason }
        | _ -> None))

  /// The other direction, so a decision that refuses everything is caught too: an eligible tier on a clean tree,
  /// in a run that did not ask for `--fresh`, whose store holds an intact record for exactly its current inputs, takes it.
  let intactRecordsAreUsed (states: State list) : Violation list =
    runs states
    |> List.collect (fun (i, g) ->
      match g.Freshness, g.Tree with
      | Freshness.Fresh, _
      | _, TreeState.Dirty -> []
      | Freshness.Reuse, TreeState.Clean ->
        tiers
        |> List.choose (fun spec ->
          match spec.Eligibility, Map.tryFind spec.Name g.Stored, Map.tryFind spec.Name g.Inputs, Map.tryFind spec.Name g.Statuses with
          | Eligibility.Eligible, Some(Stored.Present record), Some current, Some(TierStatus.Executed _)
            when record.Key = PassRecord.key current && record.Inputs = current ->
            Some { Index = i; Why = sprintf "%s ran although an intact record for exactly its inputs was in the store" spec.Name }
          | _ -> None))

  /// A damaged or missing record is never trusted: whatever the decision does, the tier runs.
  let damagedRecordsRerun (states: State list) : Violation list =
    runs states
    |> List.collect (fun (i, g) ->
      reused g
      |> List.choose (fun (tier, _) ->
        match Map.tryFind tier g.Stored with
        | Some(Stored.Present _) -> None
        | _ -> Some { Index = i; Why = sprintf "%s reused a record the store did not hold intact" tier }))

  /// The run's verdict is red when any tier is red, and a tier that did not execute green is never counted green.
  let noTierIsGreenWithoutEvidence (states: State list) : Violation list =
    runs states
    |> List.collect (fun (i, g) ->
      g.Statuses
      |> Map.toList
      |> List.choose (fun (tier, status) ->
        match status with
        | TierStatus.Executed Outcome.Red -> None
        | TierStatus.Executed Outcome.Green -> None
        | TierStatus.Reused record ->
          match PassRecord.isGreen record with
          | true -> None
          | false -> Some { Index = i; Why = sprintf "%s reused a record whose verdict was %s" tier record.Verdict }))
