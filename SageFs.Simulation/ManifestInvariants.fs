namespace SageFs.Simulation

open SageFs.Features.DaemonManifest
open SageFs.Simulation.ManifestSim

/// Named invariants over a manifest owner `ManifestTrace`. Same stable-id /
/// message shape as Phase 1's `Invariants` and Phase 2's
/// `WorkerLifecycleInvariants`. The oracle here is INDEPENDENT of the reducer
/// under test: every check recomputes its expectation purely from each
/// step's recorded Command/Outcome/StateBefore/StateAfter/ReadResult fields
/// plus the REAL, pure `ManifestMutation.apply` (the production reducer's
/// own domain function, SageFs.Core) — it never calls `ManifestSim.stepOwner`
/// or `stepReadMergeWrite` (both `private`, unreachable from here), so a
/// check passing is not a tautology against either sim reducer.
module ManifestInvariants =

  [<RequireQualifiedAccess>]
  type Outcome =
    | Holds
    | Violated of message: string

  type Invariant =
    { Id: string
      Description: string
      Check: ManifestTrace -> Outcome }

  /// Whether an Apply's outcome actually advanced `Current` — both
  /// `Committed` and `WriteFailed` do (a failed disk write still keeps the
  /// change in memory, per `ManifestOwner`'s own `CommitError.WriteFailed`
  /// contract); only the two `Rejected*` outcomes leave `Current` untouched.
  let private advancesCurrent (outcome: ApplyOutcome) : bool =
    match outcome with
    | ApplyOutcome.Committed
    | ApplyOutcome.WriteFailed -> true
    | ApplyOutcome.RejectedAfterShutdown
    | ApplyOutcome.RejectedBaseUnreadable -> false

  /// The mutation stream that should have advanced `Current`, in order, up
  /// to (and including) step `uptoIndex`.
  let private mutationsUpTo (steps: ManifestStep list) (uptoIndex: int) : ManifestMutation list =
    steps
    |> List.filter (fun s -> s.Index <= uptoIndex)
    |> List.choose (fun s ->
      match s.Command, s.Outcome with
      | OwnerCmd.Apply mutation, Some outcome when advancesCurrent outcome -> Some mutation
      | _ -> None)

  /// The independently-recomputed `Current` after folding every mutation
  /// that should have advanced it, from the empty manifest, via the REAL
  /// pure `ManifestMutation.apply` — the one spec this module trusts.
  let private expectedCurrent (steps: ManifestStep list) (uptoIndex: int) : DaemonManifestState =
    mutationsUpTo steps uptoIndex
    |> List.fold (fun state mutation -> ManifestMutation.apply mutation state) DaemonManifestState.empty

  /// no-lost-update: `Current` after step n is exactly the fold, via the
  /// REAL `ManifestMutation.apply`, of every mutation that advanced it up to
  /// n — no committed change is ever silently overwritten by a racing
  /// writer's stale merge. This is the property the single-owner design
  /// exists to guarantee, and the read-merge-write twin (independent writers
  /// racing a shared, possibly-stale snapshot) violates.
  let noLostUpdate : Invariant =
    { Id = "no-lost-update"
      Description =
        "Current after step n equals the fold, via the real ManifestMutation.apply, of every mutation that advanced it up to n — no committed change is ever silently overwritten by a racing writer's stale merge."
      Check = fun t ->
        t.Steps
        |> List.tryPick (fun s ->
          match s.Command, s.Outcome with
          | OwnerCmd.Apply mutation, Some outcome when advancesCurrent outcome ->
            let expected = expectedCurrent t.Steps s.Index
            match s.StateAfter.Current = expected with
            | true -> None
            | false ->
              Some(
                sprintf
                  "step %d: Apply(%A) advanced Current to %A but the independent fold of every mutation that should have advanced Current up to this step expects %A — a prior committed mutation was lost"
                  s.Index mutation s.StateAfter.Current expected)
          | _ -> None)
        |> function
           | Some msg -> Outcome.Violated msg
           | None -> Outcome.Holds }

  /// after-shutdown-sealed: once `Lifecycle` has become `ShutDown`, no
  /// further `SyncLive Running` mutation may mutate `Current` — a late
  /// periodic-save racing the shutdown save must never mark the sessions it
  /// just stopped alive again.
  let afterShutdownSealed : Invariant =
    { Id = "after-shutdown-sealed"
      Description = "Once shutdown is recorded, a SyncLive-Running mutation never mutates Current again."
      Check = fun t ->
        t.Steps
        |> List.tryPick (fun s ->
          match s.Command, s.StateBefore.Lifecycle with
          | OwnerCmd.Apply (ManifestMutation.SyncLive (_, _, _, LiveSync.Running)), Lifecycle.ShutDown
            when s.StateAfter.Current <> s.StateBefore.Current ->
            Some(
              sprintf
                "step %d: a SyncLive-Running mutation mutated Current (%A -> %A) after shutdown was already recorded"
                s.Index s.StateBefore.Current s.StateAfter.Current)
          | _ -> None)
        |> function
           | Some msg -> Outcome.Violated msg
           | None -> Outcome.Holds }

  /// read-sees-whole-prefix: a Read never observes a torn/partial merge — it
  /// is exactly the fold of the mutation prefix that had committed by then.
  let readSeesWholePrefix : Invariant =
    { Id = "read-sees-whole-prefix"
      Description = "A Read observes exactly the fold of the committed mutation prefix up to that point — never a torn or partial merge."
      Check = fun t ->
        t.Steps
        |> List.tryPick (fun s ->
          match s.Command, s.ReadResult with
          | OwnerCmd.Read, Some observed ->
            let expected = expectedCurrent t.Steps s.Index
            match observed = expected with
            | true -> None
            | false ->
              Some(
                sprintf
                  "step %d: Read observed %A but the fold of the committed prefix expects %A"
                  s.Index observed expected)
          | _ -> None)
        |> function
           | Some msg -> Outcome.Violated msg
           | None -> Outcome.Holds }

  /// corrupt-base-preserves-history: while the base is unreadable, no commit
  /// silently drops previously-committed records — a rejected Apply must
  /// leave `Current` byte-for-byte unchanged (fail-closed), never reset
  /// toward empty or toward some partially-reconstructed state.
  let corruptBasePreservesHistory : Invariant =
    { Id = "corrupt-base-preserves-history"
      Description = "While BaseReadable=false, a commit never mutates Current — history is preserved, not silently dropped."
      Check = fun t ->
        t.Steps
        |> List.tryPick (fun s ->
          match s.Command, s.StateBefore.BaseReadable with
          | OwnerCmd.Apply _, false when s.StateAfter.Current <> s.StateBefore.Current ->
            Some(
              sprintf
                "step %d: Current mutated (%A -> %A) while the base was unreadable — history was not preserved"
                s.Index s.StateBefore.Current s.StateAfter.Current)
          | _ -> None)
        |> function
           | Some msg -> Outcome.Violated msg
           | None -> Outcome.Holds }

  /// remove-idempotent: `Remove id` applied twice in a row (no other Apply
  /// between them) leaves `Current` unchanged the second time.
  let removeIdempotent : Invariant =
    { Id = "remove-idempotent"
      Description = "Removing the same session id twice in a row (no other Apply between them) leaves Current unchanged the second time."
      Check = fun t ->
        let applySteps =
          t.Steps
          |> List.filter (fun s -> match s.Command with OwnerCmd.Apply _ -> true | _ -> false)
        applySteps
        |> List.pairwise
        |> List.tryPick (fun (s1, s2) ->
          match s1.Command, s2.Command with
          | OwnerCmd.Apply (ManifestMutation.Remove sid1), OwnerCmd.Apply (ManifestMutation.Remove sid2) when sid1 = sid2 ->
            match s2.StateAfter.Current = s2.StateBefore.Current with
            | true -> None
            | false ->
              Some(
                sprintf
                  "step %d: a second consecutive Remove %s mutated Current (%A -> %A) — removal is not idempotent"
                  s2.Index sid1 s2.StateBefore.Current s2.StateAfter.Current)
          | _ -> None)
        |> function
           | Some msg -> Outcome.Violated msg
           | None -> Outcome.Holds }

  let all : Invariant list =
    [ noLostUpdate; afterShutdownSealed; readSeesWholePrefix; corruptBasePreservesHistory; removeIdempotent ]

  /// The invariants that failed for a trace, if any (id, message).
  let violations (t: ManifestTrace) : (string * string) list =
    all
    |> List.choose (fun inv ->
      match inv.Check t with
      | Outcome.Holds -> None
      | Outcome.Violated msg -> Some(inv.Id, msg))
