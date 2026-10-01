namespace SageFs.Simulation

open SageFs.WorkspaceHygiene
open SageFs.Simulation.HygieneSim

/// Invariants over `HygieneSim`'s trace. Each one names what it protects. Every violation says which step
/// of the scenario broke it, so a failing seed replays straight to the cause.
module HygieneSimInvariants =

  type Violation = { Index: int; Why: string }

  /// NEVER-REMOVE-IN-USE: nothing a session was using, and no process whose owner is alive, was removed
  /// on a decision made while it was in use.
  let neverRemoveInUse (trace: Trace) : Violation list =
    trace.Steps
    |> List.indexed
    |> List.collect (fun (i, step) ->
      step.Removals
      |> List.choose (fun r ->
        match r.DecidedOn, r.DecidedProcess with
        | Some w, _ when w.Use = Use.Busy -> Some { Index = i; Why = sprintf "%s while worktree %d had a session in it" r.Operation w.Id }
        | _, Some p when p.OwnerAlive -> Some { Index = i; Why = sprintf "%s while its owner was alive" r.Operation }
        | _ -> None))

  /// A process is only ever stopped if it is the very process that was looked at: same start time.
  let neverKillAnotherProcess (trace: Trace) : Violation list =
    trace.Steps
    |> List.indexed
    |> List.collect (fun (i, step) ->
      step.Removals
      |> List.choose (fun r ->
        match r.DecidedProcess, r.KilledStartTicks with
        | Some decided, Some killed when decided.StartTicks <> killed ->
          Some { Index = i; Why = sprintf "%s killed a different process than the one it looked at (start %d, then %d)" r.Operation decided.StartTicks killed }
        | _ -> None))

  /// NEVER-LOSE-UNMERGED-COMMITS: after every step, every commit that is not in the base is still on a branch.
  let neverLoseUnmergedCommits (trace: Trace) : Violation list =
    trace.Steps
    |> List.indexed
    |> List.choose (fun (i, step) ->
      match lostCommits step.After with
      | [] -> None
      | lost -> Some { Index = i; Why = sprintf "commits %A are on no branch and not in the base" lost })

  /// NEVER-LOSE-REAL-DIRTY-WORK: a worktree with uncommitted work was never removed. Tidy saves nothing,
  /// so work it would lose is work it must not touch.
  let neverLoseRealDirtyWork (trace: Trace) : Violation list =
    trace.Steps
    |> List.indexed
    |> List.collect (fun (i, step) ->
      step.Removals
      |> List.choose (fun r ->
        match r.DoneOn with
        | Some w when w.Dirt = Dirt.RealDirt -> Some { Index = i; Why = sprintf "%s removed uncommitted work" r.Operation }
        | _ -> None))

  /// PLAN-IS-DRY-UNLESS-CONFIRMED: making a plan changes nothing, and a plan that is not the one that exists
  /// now cannot be run.
  let planIsDryUnlessConfirmed (trace: Trace) : Violation list =
    trace.Steps
    |> List.indexed
    |> List.choose (fun (i, step) ->
      match step.Op with
      | Op.PlanNow ->
        match step.Before = step.After, step.PerformedDuringPlanning with
        | true, 0 -> None
        | _ -> Some { Index = i; Why = sprintf "planning changed the world (%d operations performed)" step.PerformedDuringPlanning }
      | Op.ExecuteStale ->
        match step.PlanId with
        | None -> None
        | Some shown ->
          let current = (Planner.plan (gather step.Before)).Id
          match shown = current || step.Before = step.After with
          | true -> None
          | false -> Some { Index = i; Why = "a stale plan was run" }
      | _ -> None)

  /// EXECUTE-IS-IDEMPOTENT: running the same plan again straight after it ran reclaims nothing and changes nothing.
  let executeIsIdempotent (trace: Trace) : Violation list =
    trace.Steps
    |> List.indexed
    |> List.pairwise
    |> List.choose (fun ((_, first), (i, second)) ->
      match first.Op, second.Op with
      | Op.Execute [], Op.ExecuteAgain ->
        match second.PlanId = first.PlanId, second.Reclaimed, second.Before = second.After with
        | true, Some 0L, true -> None
        | true, Some bytes, _ -> Some { Index = i; Why = sprintf "the second run reclaimed %d bytes or changed the world" bytes }
        | _ -> None
      | _ -> None)

  /// The scenarios actually run steps and remove things, so a green run is not vacuous.
  let removalsHappened (traces: Trace list) : int =
    traces |> List.sumBy (fun t -> t.Steps |> List.sumBy (fun s -> List.length s.Removals))
