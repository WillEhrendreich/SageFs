namespace SageFs.Simulation

open SageFs.Features.CallerState
open SageFs.Simulation.CallerPendingSim

/// Named invariants over a `CallerPendingSim.Trace`. Same stable-id / `Holds`-vs-`Violated` shape as the other
/// simulations.
module CallerPendingInvariants =

  [<RequireQualifiedAccess>]
  type Outcome =
    | Holds
    | Violated of message: string

  type Invariant =
    { Id: string
      Description: string
      Check: Trace -> Outcome }

  let private isClean (state: CallersState) : bool =
    match state with
    | CallersState.CallersCurrent
    | CallersState.CallersNotReported -> true
    | CallersState.CallersPending _
    | CallersState.CallersNotChecked _ -> false

  let private firstViolation (t: Trace) (violates: int -> Snapshot -> string option) : Outcome =
    t.Snapshots
    |> List.mapi violates
    |> List.tryPick id
    |> function
       | Some message -> Outcome.Violated(sprintf "%s (seed=%d, ops=%A)" message t.Scenario.Seed t.Scenario.Ops)
       | None -> Outcome.Holds

  /// resigned-with-unsaved-caller-is-never-clean: whenever a caller still runs a method a save replaced or removed, the
  /// state a report would carry is not clean. This is the bug: the save said Patched, and the caller in the other file
  /// went on running the old behavior with nothing to say so.
  let resignedWithUnsavedCallerIsNeverClean : Invariant =
    { Id = "resigned-with-unsaved-caller-is-never-clean"
      Description = "While any caller's landed code is on a replaced or removed method, the reported state is Pending or NotChecked, never Current."
      Check = fun t ->
        firstViolation t (fun i s ->
          match Set.isEmpty s.Stranded || not (isClean s.Reported) with
          | true -> None
          | false -> Some(sprintf "after op #%d (%A) callers %A are on an old method and the state is %A" i s.Op (Set.toList s.Stranded) s.Reported)) }

  /// pending-clears-exactly-when-the-caller-lands: what the ledger lists is exactly what the truth says is stranded, so a
  /// caller leaves the list when its code lands and not before, and nothing is listed that is not stranded.
  let pendingClearsExactlyWhenTheCallerLands : Invariant =
    { Id = "pending-clears-exactly-when-the-caller-lands"
      Description = "The (caller file, declaration) pairs the ledger lists equal the pairs the truth says are stranded, after every op."
      Check = fun t ->
        firstViolation t (fun i s ->
          match s.Listed = s.Stranded with
          | true -> None
          | false ->
            Some(
              sprintf
                "after op #%d (%A) the ledger lists %A but the truth is %A"
                i s.Op (Set.toList s.Listed) (Set.toList s.Stranded))) }

  /// a-restart-leaves-nothing-pending: the process after a restart has no old methods.
  let aRestartLeavesNothingPending : Invariant =
    { Id = "a-restart-leaves-nothing-pending"
      Description = "After a Restart op the reported state is Current."
      Check = fun t ->
        firstViolation t (fun i s ->
          match s.Op, s.Reported with
          | Op.Restart, CallersState.CallersCurrent -> None
          | Op.Restart, other -> Some(sprintf "after the restart at op #%d the state is %A" i other)
          | _ -> None) }

  let all : Invariant list =
    [ resignedWithUnsavedCallerIsNeverClean; pendingClearsExactlyWhenTheCallerLands; aRestartLeavesNothingPending ]

  /// The invariants that failed for a trace, if any (id, message).
  let violations (t: Trace) : (string * string) list =
    all
    |> List.choose (fun inv ->
      match inv.Check t with
      | Outcome.Holds -> None
      | Outcome.Violated msg -> Some(inv.Id, msg))
