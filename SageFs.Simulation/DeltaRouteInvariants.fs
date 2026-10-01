namespace SageFs.Simulation

open System
open SageFs.Features.MetadataDelta
open SageFs.Simulation.DeltaRouteSim

/// Named invariants over a drained `DeltaRouteSim.Trace`. Same stable-id / `Holds`-vs-`Violated` shape as the other
/// simulations.
module DeltaRouteInvariants =

  [<RequireQualifiedAccess>]
  type Outcome =
    | Holds
    | Violated of message: string

  type Invariant =
    { Id: string
      Description: string
      Check: Trace -> Outcome }

  let private replay (t: Trace) : string = sprintf "seed=%d, rude=%A, ops=%A" t.Scenario.Seed t.Scenario.Rude t.Scenario.Ops

  /// The first step where `bad` says the step is wrong, as a violation naming the step, the op and what was seen.
  let private firstBadStep (t: Trace) (bad: StepRecord -> string option) : Outcome =
    t.Steps
    |> List.tryPick (fun s ->
      bad s |> Option.map (fun why -> sprintf "step %d (%A): %s (%s)" s.Index s.Op why (replay t)))
    |> function
       | Some message -> Outcome.Violated message
       | None -> Outcome.Holds

  /// never-patched-with-a-stale-baseline: a delta is applied only to the module it was computed for, from the build
  /// that module runs. Applied to anything else, the runtime reads it against metadata that is not the delta's own, and
  /// the process runs a mix of two builds nobody wrote.
  let neverPatchedWithAStaleBaseline : Invariant =
    { Id = "never-patched-with-a-stale-baseline"
      Description = "A delta is applied only to the module it was prepared for, and only when it was computed against the build that module runs."
      Check = fun t ->
        match t.Applications |> List.tryFind (fun a -> a.Basis <> a.ProcessRan || a.PreparedFor <> a.RanIn) with
        | None -> Outcome.Holds
        | Some a ->
          Outcome.Violated(
            sprintf "save %d was computed against build %d for module %d and applied to a process running build %d in module %d (%s)"
              a.Save a.Basis a.PreparedFor a.ProcessRan a.RanIn (replay t)) }

  /// a-failed-apply-restarts-not-lies: when the runtime refuses a delta the save says it restarts, and nothing is applied
  /// to that process afterwards. The process may hold part of the delta, so a later one would be built on a state nobody knows.
  let aFailedApplyRestartsNotLies : Invariant =
    { Id = "a-failed-apply-restarts-not-lies"
      Description = "A delta the runtime refused is reported as a restart, and no delta is applied to that process after it."
      Check = fun t ->
        match t.Applications |> List.tryFind (fun a -> a.ProcessWasBroken) with
        | Some a ->
          Outcome.Violated(sprintf "save %d was applied to a process a delta had already failed in (%s)" a.Save (replay t))
        | None ->
          firstBadStep t (fun s ->
            match s.RuntimeRefused, s.Said with
            | true, Said.Restarted _ -> None
            | true, said -> Some(sprintf "the runtime refused the delta and the save said %A" said)
            | false, _ -> None) }

  /// chain-never-forks: where the route still tracks the module that runs, its chain is at exactly the number of deltas
  /// that process has taken. Behind, a delta is applied twice; ahead, one is skipped; either way the next delta is
  /// computed against a build the process does not run.
  let chainNeverForks : Invariant =
    { Id = "chain-never-forks"
      Description = "A chain that tracks the module that runs is at exactly the number of deltas the process has taken."
      Check = fun t ->
        firstBadStep t (fun s ->
          match s.Observed.Tracks = s.Truth.Module, s.Observed.ChainGeneration = s.Truth.Applied with
          | true, false ->
            Some(sprintf "the chain is at generation %d and the process has taken %d deltas" s.Observed.ChainGeneration s.Truth.Applied)
          | true, true
          | false, _ -> None) }

  /// patched-means-the-process-runs-it: a save that says patched left the process running the build it was made from.
  let patchedMeansTheProcessRunsIt : Invariant =
    { Id = "patched-means-the-process-runs-it"
      Description = "A save that says it was patched left the process running the version it said."
      Check = fun t ->
        firstBadStep t (fun s ->
          match s.Said with
          | Said.Patched(_, target) when s.Truth.Running <> target -> Some(sprintf "said patched to %d and the process runs %d" target s.Truth.Running)
          | _ -> None) }

  /// confirmed-only-after-the-new-body-ran: a patch is called live only once a call has entered the NEW body. A call that
  /// was already running the old one and finished after the delta landed proves nothing.
  let confirmedOnlyAfterTheNewBodyRan : Invariant =
    { Id = "confirmed-only-after-the-new-body-ran"
      Description = "A patch is confirmed only when a call entered its new body after it landed."
      Check = fun t ->
        firstBadStep t (fun s ->
          match s.Said with
          | Said.Confirmed(save, target) when not (Set.contains target s.Truth.Ran) ->
            Some(sprintf "save %d was confirmed for version %d and no call has entered that body" save target)
          | _ -> None) }

  /// a-refused-edit-is-never-applied: a version the emitter cannot carry never reaches the runtime.
  let aRefusedEditIsNeverApplied : Invariant =
    { Id = "a-refused-edit-is-never-applied"
      Description = "A version the emitter refused is never handed to the runtime."
      Check = fun t ->
        match t.Applications |> List.tryFind (fun a -> Set.contains a.Target t.Scenario.Rude) with
        | None -> Outcome.Holds
        | Some a -> Outcome.Violated(sprintf "save %d applied version %d, which the emitter refuses (%s)" a.Save a.Target (replay t)) }

  let all : Invariant list =
    [ neverPatchedWithAStaleBaseline
      aFailedApplyRestartsNotLies
      chainNeverForks
      patchedMeansTheProcessRunsIt
      confirmedOnlyAfterTheNewBodyRan
      aRefusedEditIsNeverApplied ]

  /// The invariants that failed for a trace, if any (id, message).
  let violations (t: Trace) : (string * string) list =
    all
    |> List.choose (fun inv ->
      match inv.Check t with
      | Outcome.Holds -> None
      | Outcome.Violated msg -> Some(inv.Id, msg))
