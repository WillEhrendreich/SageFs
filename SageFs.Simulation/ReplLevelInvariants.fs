namespace SageFs.Simulation

open SageFs
open SageFs.EvalActorDecision
open SageFs.Simulation.ReplLevelSim

/// The named invariants over a `ReplLevelSim.Trace`. Each reads the log the fold kept and the ground truth it carried, and
/// none calls the code under test, so they are an outside check and not a restatement of it.
module ReplLevelInvariants =

  [<RequireQualifiedAccess>]
  type Outcome =
    | Holds
    | Violated of message: string

  type Invariant =
    { Id: string
      Description: string
      Check: Trace -> Outcome }

  let private levelResets (t: Trace) : ResetRecord list =
    t.Final.Resets |> List.filter (fun r -> r.Decision = EvalDecision.RunLevel)

  let private violated (t: Trace) (what: string) : Outcome =
    Outcome.Violated (sprintf "%s (seed=%d, ops=%A)" what t.Scenario.Seed t.Scenario.Ops)

  /// L1: the moment the actor resets the host for a level, no eval is running or cancelling on it, and no test run is on it.
  let noResetUnderWork : Invariant =
    { Id = "L1-no-reset-under-work"
      Description = "A level never resets the host while an eval is running or cancelling, or while a test run is on it."
      Check = fun t ->
        match levelResets t |> List.tryFind (fun r ->
                (match r.ActivityAtAct with
                 | SessionActivity.Evaluating | SessionActivity.Cancelling -> true
                 | SessionActivity.Idle | SessionActivity.HostCrashed _ -> false)
                || r.RunsOnHost > 0) with
        | Some r -> violated t (sprintf "a level reset the host with activity %A and %d run(s) on it" r.ActivityAtAct r.RunsOnHost)
        | None ->
          match t.Final.RunsKilledByReset with
          | 0 -> Outcome.Holds
          | killed -> violated t (sprintf "%d test run(s) were on the host a level replaced" killed) }

  /// L2: a run that arrives while a level is pending or under way is held, and held only for that: a held run with no level
  /// pending or under way has been dropped.
  let heldRunsAreServed : Invariant =
    { Id = "L2-held-runs-are-served"
      Description = "A test run that arrives during a level is held and then served, never dropped and never run on the host about to be reset."
      Check = fun t ->
        let strandedAt =
          t.States
          |> List.tryFind (fun w ->
            w.Held > 0
            && (match w.Gate with
                | GateStanding.LevelPending _ | GateStanding.Leveling -> false
                | GateStanding.Idle | GateStanding.Running _ -> true))
        match strandedAt with
        | Some w -> violated t (sprintf "%d run(s) were held with the gate at %A, so no level is left to serve them" w.Held w.Gate)
        | None -> Outcome.Holds }

  /// L3: a level never resets a host that holds something of the user's.
  let noResetOverDefinitions : Invariant =
    { Id = "L3-no-reset-over-definitions"
      Description = "A level never resets a host that has run an eval since it was built, or in which an init script or a buffer's tests defined something."
      Check = fun t ->
        match levelResets t |> List.tryFind (fun r -> r.EvalsSinceHost > 0 || r.InitScriptDefined || r.DynamicTestsDefined) with
        | Some r ->
          violated t (sprintf "a level reset a host with %d eval(s), init script %b, dynamic tests %b" r.EvalsSinceHost r.InitScriptDefined r.DynamicTestsDefined)
        | None -> Outcome.Holds }

  /// L4: every answer the manager applied came from the worker that serves the session.
  let answersAreJudgedByPid : Invariant =
    { Id = "L4-answers-judged-by-pid"
      Description = "An answer to a level ask is applied only if the worker it was asked of still serves the session."
      Check = fun t ->
        match t.Final.Applied |> List.tryFind (fun a -> a.AskedPid <> a.ServingPid) with
        | Some a -> violated t (sprintf "an answer from pid %d was applied to a session served by pid %d" a.AskedPid a.ServingPid)
        | None -> Outcome.Holds }

  /// L5: at every step, the state says level only if the host was built at or after the latest save.
  let levelMeansLevel : Invariant =
    { Id = "L5-level-means-level"
      Description = "The state is InSync only when the host was built at or after the latest save."
      Check = fun t ->
        match t.States |> List.tryFind (fun w -> w.Freshness = ReplFreshness.InSync && w.HostBuiltAt < w.Saves) with
        | Some w -> violated t (sprintf "InSync with the host built at save %d and the app at save %d" w.HostBuiltAt w.Saves)
        | None -> Outcome.Holds }

  let all : Invariant list =
    [ noResetUnderWork; heldRunsAreServed; noResetOverDefinitions; answersAreJudgedByPid; levelMeansLevel ]

  /// Every violation, as `id: message`.
  let violations (t: Trace) : string list =
    all
    |> List.choose (fun inv ->
      match inv.Check t with
      | Outcome.Holds -> None
      | Outcome.Violated message -> Some (sprintf "%s: %s" inv.Id message))
