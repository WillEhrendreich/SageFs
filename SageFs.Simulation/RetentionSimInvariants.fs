namespace SageFs.Simulation

open SageFs.Cohort
open SageFs.Features
open SageFs.Simulation.RetentionSim

/// Invariants over `RetentionSim`'s trace. Each one is checked at the exact
/// step a prune ran (`LastPrune` moved to that step).
module RetentionSimInvariants =

  type Violation = { Index: int; Why: string }

  /// The states right after a prune, with what that prune saw going in.
  let private prunes (states: State list) =
    states
    |> List.indexed
    |> List.choose (fun (i, s) ->
      match s.LastPrune with
      | PruneObservation.Pruned(step, rowsBefore, ledgerBefore, at, version) when step = s.Step ->
        Some(i, s, rowsBefore, ledgerBefore, at, version)
      | _ -> None)

  /// After any prune the store is inside every cap: no more than `MaxRows`
  /// raw rows, every raw row written by the running version and inside
  /// `MaxAge`, and aggregates for no more than `MaxAggregateVersions` versions.
  let storeNeverExceedsItsCaps (states: State list) : Violation list =
    prunes states
    |> List.collect (fun (i, s, _, _, at, version) ->
      let aggregateVersions = s.Aggregate |> Map.toList |> List.map (fun (k, _) -> k.Version) |> List.distinct
      [ if s.Rows.Length > policy.MaxRows then
          yield { Index = i; Why = sprintf "%d raw rows kept, cap is %d" s.Rows.Length policy.MaxRows }
        match s.Rows |> List.tryFind (fun r -> r.Version <> version) with
        | Some r -> yield { Index = i; Why = sprintf "row %d from version %s survived a prune on %s" r.Id r.Version version }
        | None -> ()
        match s.Rows |> List.tryFind (fun r -> at - r.OccurredAt > policy.MaxAge) with
        | Some r -> yield { Index = i; Why = sprintf "row %d is %A old, cap is %A" r.Id (at - r.OccurredAt) policy.MaxAge }
        | None -> ()
        if aggregateVersions.Length > policy.MaxAggregateVersions then
          yield { Index = i; Why = sprintf "aggregates for %d versions, cap is %d" aggregateVersions.Length policy.MaxAggregateVersions } ])

  /// A prune never loses a running-version row that's inside the age window,
  /// unless there are more of those than the row cap, and then only the
  /// oldest go: the newest `MaxRows` of them always survive.
  let currentVersionRowsInsideTheWindowAreNeverLost (states: State list) : Violation list =
    prunes states
    |> List.choose (fun (i, s, rowsBefore, _, at, version) ->
      let mustSurvive =
        rowsBefore
        |> List.filter (fun r -> r.Version = version && at - r.OccurredAt <= policy.MaxAge)
        |> List.sortByDescending (fun r -> r.OccurredAt, r.Id)
        |> List.truncate policy.MaxRows
        |> List.map (fun r -> r.Id)
        |> Set.ofList
      let kept = s.Rows |> List.map (fun r -> r.Id) |> Set.ofList
      match Set.difference mustSurvive kept |> Set.toList with
      | [] -> None
      | lost -> Some { Index = i; Why = sprintf "current-version rows inside the window were lost: %A" lost })

  /// Every row ever written is either still stored or counted in its
  /// version's aggregate. The only counts allowed to go are whole versions
  /// dropped by the aggregate version cap.
  let noCountIsSilentlyLost (states: State list) : Violation list =
    states
    |> List.indexed
    |> List.choose (fun (i, s) ->
      let mismatches =
        s.Written
        |> Map.toList
        |> List.filter (fun (v, _) -> not (Set.contains v s.AggregateDropped))
        |> List.choose (fun (v, written) ->
          let stored = s.Rows |> List.filter (fun r -> r.Version = v) |> List.length
          let counted = s.Aggregate |> Map.toList |> List.sumBy (fun (k, n) -> if k.Version = v then n else 0)
          match stored + counted = written with
          | true -> None
          | false -> Some(sprintf "%s: wrote %d, %d stored + %d counted" v written stored counted))
      match mismatches with
      | [] -> None
      | ms -> Some { Index = i; Why = String.concat "; " ms })

  /// A daemon start never clears the ledger of a cohort that's still running.
  let activeCohortLedgerIsNeverCleared (states: State list) : Violation list =
    prunes states
    |> List.choose (fun (i, s, _, ledgerBefore, _, _) ->
      match LocalDataRetention.cohortActivity (replay ledgerBefore) with
      | LocalDataRetention.CohortActivity.Active _ when s.Ledger.Length < ledgerBefore.Length ->
        Some { Index = i; Why = sprintf "cleared %d ledger rows of a running cohort" (ledgerBefore.Length - s.Ledger.Length) }
      | _ -> None)
