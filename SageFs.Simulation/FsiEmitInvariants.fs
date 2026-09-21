namespace SageFs.Simulation

open SageFs.Features.ReloadOutcome
open SageFs.Simulation.FsiEmitSim

/// Invariants over `FsiEmitSim`. There is really only one, and every hot-reload
/// surface in the product depends on it:
///
///   A save that REPORTS the running process changed must have changed it.
///
/// `ReloadOutcome.processChanged` is the single place that answers "did the
/// running process change?" — `shouldRefreshBrowser` is literally defined as
/// that function — so a violation is not cosmetic. It refreshes a browser into
/// byte-identical code, tells an agent its edit landed when it did not, and
/// makes "Hot reloaded 1 of 1" mean nothing.
module FsiEmitInvariants =

  type Violation =
    { Index: int
      Reported: ReloadOutcome
      ClaimedChange: bool
      ActualChange: bool
      Why: string }

  /// THE honesty invariant, in both directions.
  ///
  /// Over-claiming is the failure that shipped: the report says Patched and the
  /// app still runs the old body. Under-claiming matters too — a save that DID
  /// move the running code while reporting no effect leaves the user staring at
  /// a page that is silently already new, and it is the shape the shape-matrix
  /// fixture calls out for the `mutable` cell ("serves B although the wire said
  /// nothing was patched").
  let honestClaim (observations: Observation list) : Violation list =
    observations
    |> List.indexed
    |> List.choose (fun (i, o) ->
      match o.ClaimedChange = o.ActualChange with
      | true -> None
      | false ->
        let why =
          match o.ClaimedChange with
          | true ->
            "reported that the running process changed, but the copy the app holds was never re-pointed — the name in reloadedMethods belonged to a previous eval's copy, which nothing outside that eval calls"
          | false ->
            "reported no effect while the running process DID change, so a client that trusts the verdict shows stale output for code that already moved"
        Some
          { Index = i
            Reported = o.Reported
            ClaimedChange = o.ClaimedChange
            ActualChange = o.ActualChange
            Why = why })

  /// A `Patched` count must never be zero — `ofPatchCounts` routes zero to
  /// `NoEffect`, so `Patched(0, _)` is unrepresentable by construction and this
  /// asserts the constructor is the only way the type is built.
  let patchedCountIsPositive (observations: Observation list) : Violation list =
    observations
    |> List.indexed
    |> List.choose (fun (i, o) ->
      match o.Reported with
      | ReloadOutcome.Patched(patched, _) when patched <= 0 ->
        Some
          { Index = i
            Reported = o.Reported
            ClaimedChange = o.ClaimedChange
            ActualChange = o.ActualChange
            Why = "Patched carried a count of zero — 'succeeded, changed nothing', the exact lie ofPatchCounts exists to make unrepresentable" }
      | _ -> None)

  let all (observations: Observation list) : Violation list =
    honestClaim observations @ patchedCountIsPositive observations

  /// TWIN WITH TEETH — the pre-fix decision shape, so the invariant above can
  /// be PROVEN to catch something rather than merely passing.
  ///
  /// This is what `confirmPatchAsOutcome` does today: ask only whether a method
  /// with that NAME appears in `reloadedMethods`. Because FSI's `FSI_NNNN`
  /// wrapper is stripped on both sides, that name is shared by the compiled
  /// copy and every prior eval's copy, so an FSI-copy-to-FSI-copy redirect
  /// answers yes for an app that calls neither.
  ///
  /// Deliberately NOT wired into any product path. It exists only so a test can
  /// show `honestClaim` failing under it and passing under a decision that
  /// distinguishes the copies.
  let nameOnlyConfirmTwin
    (before: SageFs.Features.ReloadPlanning.FileDecls)
    (patched: SageFs.Features.ReloadPlanning.SourceDecl list)
    (reloadedMethods: string list)
    (_reachedRunningProcess: string list)
    : ReloadOutcome =
    // The historical shape: ignore the evidence entirely and go by NAME alone,
    // which is what shipped. Passing `reloadedMethods` as its own
    // reached-set reproduces "any redirect counts as reaching the process".
    SageFs.Features.ReloadPlanning.confirmPatchAsOutcome before patched reloadedMethods reloadedMethods
