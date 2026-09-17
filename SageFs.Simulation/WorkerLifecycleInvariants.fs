namespace SageFs.Simulation

open SageFs
open SageFs.Simulation.WorkerLifecycleSim

/// Named invariants over a worker-lifecycle `MgrTrace`. Same stable-id / message
/// shape as Phase 1's `Invariants` module. The oracle here is INDEPENDENT of the
/// reducer under test: it inspects each step's status-before, status-after and
/// the raw pid on the event — it never calls `WorkerEventGuard` — so it is a
/// genuine external check, not a tautology. It HOLDS for the real reducer (by
/// construction of the guard) and is VIOLATED for the pid-blind reducer.
module WorkerLifecycleInvariants =

  [<RequireQualifiedAccess>]
  type Outcome =
    | Holds
    | Violated of message: string

  type Invariant =
    { Id: string
      Description: string
      Check: MgrTrace -> Outcome }

  /// The pid an event carries, if any. Commands with no pid (Create, HardReset,
  /// Stop) never participate in the stale-pid check — their mutations are driven
  /// by the command itself, not by a worker's identity.
  let private eventPid (cmd: MgrCommand) : int option =
    match cmd with
    | MgrCommand.Ready pid
    | MgrCommand.Exited (pid, _)
    | MgrCommand.SpawnFailed pid -> Some pid
    | MgrCommand.Create _
    | MgrCommand.HardReset _
    | MgrCommand.Stop -> None

  /// The pids that are ALLOWED to mutate the session at a given before-state:
  ///   * Ready cur     -> only the current worker (`cur`) — its own exit/failure.
  ///   * Swapping old  -> only the awaited replacement (a real pid, distinct from
  ///                      the still-registered old pid); the OLD worker's own
  ///                      events must be inert (committing the old pid mid-swap
  ///                      is the T8 bug).
  ///   * Empty/terminal-> not pid-gated (Create / absorbing); pid events here do
  ///                      not legitimately mutate but are handled elsewhere.
  let private isLegitimateMutator (before: SimStatus) (pid: int) : bool =
    match before with
    | SimStatus.Ready cur -> pid = cur
    | SimStatus.Swapping oldPid -> WorkerEventGuard.isRealPid pid && pid <> oldPid
    | SimStatus.Empty
    | SimStatus.Stopped
    | SimStatus.Faulted _ -> false

  /// no-stale-pid-applied: a worker-lifecycle event carrying a real pid that is
  /// NOT a legitimate mutator of the session's current state must leave the
  /// session unchanged. Equivalently: only the current worker (or, mid-swap, the
  /// awaited replacement) may ever change the registered worker or the session's
  /// liveness. A dead/retired/foreign worker's late event must be inert.
  ///
  /// This is the roast's pid-blind race (§4), expressed as a property. It HOLDS
  /// for the real `WorkerEventGuard`; the pid-blind reducer violates it on the
  /// first straggler.
  let noStalePidApplied : Invariant =
    { Id = "no-stale-pid-applied"
      Description = "A worker event whose pid is not the current worker (nor, mid-swap, the awaited replacement) never mutates the session."
      Check = fun t ->
        t.Steps
        |> List.tryPick (fun s ->
          match eventPid s.Command with
          | Some pid when WorkerEventGuard.isRealPid pid
                          && s.StatusAfter <> s.StatusBefore
                          && not (isLegitimateMutator s.StatusBefore pid) ->
            Some(
              sprintf
                "step %d: %A (pid %d) mutated the session %A -> %A although pid %d is not the current worker%s"
                s.Index s.Command pid s.StatusBefore s.StatusAfter pid
                (if Set.contains pid s.RetiredBefore then " (it is a RETIRED worker's straggler)" else ""))
          | _ -> None)
        |> function
           | Some msg -> Outcome.Violated msg
           | None -> Outcome.Holds }

  let all : Invariant list = [ noStalePidApplied ]

  /// The invariants that failed for a trace, if any (id, message).
  let violations (t: MgrTrace) : (string * string) list =
    all
    |> List.choose (fun inv ->
      match inv.Check t with
      | Outcome.Holds -> None
      | Outcome.Violated msg -> Some(inv.Id, msg))
