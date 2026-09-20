namespace SageFs.Simulation

open SageFs.Simulation.WorkflowSwitchSim

/// Named invariants over a `WorkflowSwitchSim.Trace`. Same stable-id /
/// `Holds`-vs-`Violated` shape as `SupervisorInvariants` /
/// `FileReloadRoutingInvariants`. Both oracles below are INDEPENDENT of the
/// reducer under test:
///   * `noDeadWindow` only inspects each step's raw `Before`/`After`
///     liveness — it never calls `spawnFirst`/`stopBeforeSpawn` itself.
///   * `settledWorkflowMatchesLastRequested` recomputes its expectation by
///     scanning the scenario's OWN op list — it never reads `Trace.Steps` or
///     `Trace.Final` while computing what SHOULD be true.
/// Scope note on the second invariant: its ground truth intentionally does
/// NOT model spawn-failure reverts or overlapping (mid-swap) switch requests
/// — recomputing "what the settled workflow should be" through a revert or
/// an overlap would mean re-deriving the same pid-guard/single-slot-overwrite
/// logic the sim exists to check, which would make the check a tautology.
/// The seeded property sweep therefore only exercises CLEAN scenarios (every
/// switch resolves via its own matching Ready before the next op —
/// `WorkflowSwitchGenerators.fromSeed`); reverts, stragglers, and the
/// overlapping-switch case are each covered by their own named,
/// hand-verified scenario instead (see `WorkflowSwitchSimTests.fs`).
module WorkflowSwitchInvariants =

  [<RequireQualifiedAccess>]
  type Outcome =
    | Holds
    | Violated of message: string

  type Invariant =
    { Id: string
      Description: string
      Check: Trace -> Outcome }

  /// no-dead-window: a workflow switch requested against a currently-live
  /// session must never itself take that session to a non-live state — the
  /// working directory is never left with zero live sessions for the switch
  /// to land, even momentarily. This is "Spawn-first restart — no dead
  /// window" (AGENTS.md) expressed as a property over the trace. It HOLDS
  /// for `run` (spawn-first parks the old worker and stays `Swapping`,
  /// still Live) and is VIOLATED by `runStopBeforeSpawn` (which stops the
  /// old worker synchronously, before any replacement exists).
  let noDeadWindow : Invariant =
    { Id = "no-dead-window"
      Description =
        "A RequestSwitch against a live session never itself takes the session to a non-live state — no interval leaves the working directory with zero live sessions."
      Check = fun t ->
        t.Steps
        |> List.tryPick (fun s ->
          match s.Op with
          | SwitchOp.RequestSwitch _ when liveness s.Before.Status = Liveness.Live && liveness s.After.Status = Liveness.NotLive ->
            Some(
              sprintf
                "step %d: %A took a LIVE session (%A) to a NON-LIVE state (%A) (seed=%d, ops=%A)"
                s.Index s.Op s.Before.Status s.After.Status t.Scenario.Seed t.Scenario.Ops)
          | _ -> None)
        |> function
           | Some msg -> Outcome.Violated msg
           | None -> Outcome.Holds }

  /// settled-workflow-matches-last-requested: for a CLEAN scenario (see the
  /// module header's scope note), the final recorded workflow equals the
  /// target of the last accepted switch request — "its workflow is the last
  /// one requested" (outcome-gate-sweep.md, Gap F). Ground truth is a pure
  /// scan of `Scenario.Ops`, independent of the fold under test: a same-
  /// workflow request is harmless to include unfiltered because it always
  /// targets the value already carried forward.
  let settledWorkflowMatchesLastRequested : Invariant =
    { Id = "settled-workflow-matches-last-requested"
      Description =
        "For a scenario where every switch resolves via its own Ready before the next op, the final recorded workflow is the target of the last requested switch."
      Check = fun t ->
        let rec lastTarget (workflowIdx: int) (ops: SwitchOp list) =
          match ops with
          | [] -> workflowIdx
          | SwitchOp.Create(_, wf) :: rest -> lastTarget wf rest
          | SwitchOp.RequestSwitch(target, _) :: rest -> lastTarget target rest
          | (SwitchOp.Ready _ | SwitchOp.SpawnFailed _ | SwitchOp.Exited _ | SwitchOp.Stop) :: rest -> lastTarget workflowIdx rest
        let expected = lastTarget (-1) t.Scenario.Ops
        if t.Final.WorkflowIdx = expected then
          Outcome.Holds
        else
          Outcome.Violated(
            sprintf
              "expected settled workflow %s (index %d, last requested), reducer recorded %s (index %d) (seed=%d, ops=%A)"
              (workflowLabel expected) expected (workflowLabel t.Final.WorkflowIdx) t.Final.WorkflowIdx
              t.Scenario.Seed t.Scenario.Ops) }

  let all : Invariant list = [ noDeadWindow; settledWorkflowMatchesLastRequested ]

  /// The invariants that failed for a trace, if any (id, message).
  let violations (t: Trace) : (string * string) list =
    all
    |> List.choose (fun inv ->
      match inv.Check t with
      | Outcome.Holds -> None
      | Outcome.Violated msg -> Some(inv.Id, msg))
