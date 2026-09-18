namespace SageFs.Simulation

open SageFs.Simulation.SupervisorSim

/// Named invariants over a `SupTrace`. Same stable-id / message shape as
/// Phase 1's `Invariants` and Phase 2's `WorkerLifecycleInvariants`. The
/// oracle here is INDEPENDENT of the reducer under test: it only inspects
/// each step's before/after state and the raw command — it never calls
/// `stepReal` or `stepNoSupervisor` — so it is a genuine external check, not
/// a tautology. It HOLDS for the real (supervised) reducer and is VIOLATED
/// for the no-supervisor twin.
module SupervisorInvariants =

  [<RequireQualifiedAccess>]
  type Outcome =
    | Holds
    | Violated of message: string

  type Invariant =
    { Id: string
      Description: string
      Check: SupTrace -> Outcome }

  /// sessions-preserved-across-fault: for every Poison step, the sessions set
  /// AFTER the fault must contain everything that was already checkpointed as
  /// last-good BEFORE it — a supervisor restart must never orphan a session
  /// that was already committed. This is the "supervise the supervisor"
  /// property (`SessionManager.fs:1820-1830`) expressed over the trace. It
  /// HOLDS for the real reducer (Poison falls back to exactly the last
  /// checkpoint); the no-supervisor twin violates it on the first fault that
  /// follows any successful Register.
  let sessionsPreservedAcrossFault : Invariant =
    { Id = "sessions-preserved-across-fault"
      Description = "A Poison step's post-fault Sessions must retain every session that was last-good checkpointed before the fault."
      Check = fun t ->
        t.Steps
        |> List.tryPick (fun s ->
          match s.Command with
          | SupCmd.Poison when not (Set.isSubset s.StateBefore.LastGood s.StateAfter.Sessions) ->
            Some(
              sprintf
                "step %d: Poison lost checkpointed sessions — LastGood before=%A, Sessions after=%A (missing %A)"
                s.Index s.StateBefore.LastGood s.StateAfter.Sessions
                (Set.difference s.StateBefore.LastGood s.StateAfter.Sessions))
          | _ -> None)
        |> function
           | Some msg -> Outcome.Violated msg
           | None -> Outcome.Holds }

  /// cancel-is-terminal: once the phase is Cancelled, no later command may
  /// mutate Sessions — cancellation is an absorbing exit, not just another
  /// fault to restart from (mirrors the real loop's `OperationCanceledException`
  /// case, which stops the supervisor rather than looping again).
  let cancelIsTerminal : Invariant =
    { Id = "cancel-is-terminal"
      Description = "After the phase becomes Cancelled, Sessions never change again."
      Check = fun t ->
        t.Steps
        |> List.tryPick (fun s ->
          match s.StateBefore.Phase with
          | SupPhase.Cancelled when s.StateAfter.Sessions <> s.StateBefore.Sessions ->
            Some(
              sprintf
                "step %d: %A mutated Sessions after Cancel (%A -> %A)"
                s.Index s.Command s.StateBefore.Sessions s.StateAfter.Sessions)
          | _ -> None)
        |> function
           | Some msg -> Outcome.Violated msg
           | None -> Outcome.Holds }

  /// fault-count-matches: the trace's accumulated `FaultCount` equals the
  /// number of Poison commands processed while still Running (i.e. before any
  /// Cancel), INDEPENDENTLY recomputed here from the raw step data rather
  /// than trusted from the trace — so this is a genuine check of the
  /// harness's own bookkeeping, not a rubber stamp.
  let faultCountMatches : Invariant =
    { Id = "fault-count-matches"
      Description = "FaultCount equals the number of Poison commands processed before Cancel, recomputed from the steps."
      Check = fun t ->
        let expected =
          t.Steps
          |> List.filter (fun s -> s.Command = SupCmd.Poison && s.StateBefore.Phase = SupPhase.Running)
          |> List.length
        if expected = t.FaultCount then Outcome.Holds
        else Outcome.Violated(sprintf "expected fault count %d (recomputed from steps), trace reports %d" expected t.FaultCount) }

  let all : Invariant list = [ sessionsPreservedAcrossFault; cancelIsTerminal; faultCountMatches ]

  /// The invariants that failed for a trace, if any (id, message).
  let violations (t: SupTrace) : (string * string) list =
    all
    |> List.choose (fun inv ->
      match inv.Check t with
      | Outcome.Holds -> None
      | Outcome.Violated msg -> Some(inv.Id, msg))
