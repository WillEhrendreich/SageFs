namespace SageFs.Simulation

open SageFs
open SageFs.Simulation.WatcherLifecycleSim

/// Named invariants over a drained `WatcherLifecycleSim.Trace`. Same
/// stable-id / `Holds`-vs-`Violated` shape as `FileReloadRoutingInvariants`
/// / `EvalActorInvariants`.
module WatcherLifecycleInvariants =

  [<RequireQualifiedAccess>]
  type Outcome =
    | Holds
    | Violated of message: string

  type Invariant =
    { Id: string
      Description: string
      Check: Trace -> Outcome }

  /// no-orphaned-watch: a `StartWatch` the fold ever issued for a directory
  /// must have a matching `StopWatch` once no live session claims that
  /// directory anymore — checked at the END of the scenario, where every
  /// session that is still going to stop has already stopped. This is the
  /// 148,077-inotify-watches-at-sessionCount-0 bug: a directory left open
  /// with zero claimants is exactly what that measurement found. The
  /// fallback directory is exempt — it is watched for the daemon's own
  /// lifetime by design, claimed by no session.
  let noOrphanedWatch : Invariant =
    { Id = "no-orphaned-watch"
      Description =
        "Every directory the fold is still watching at the end of a scenario is claimed by at least one still-live session (the fallback directory excepted)."
      Check = fun t ->
        // Ground truth comes from `Final.Claims` (tracked purely from
        // SessionStarts/SessionStops ops), NEVER from `Final.Core.DirSessions`
        // — the reducer's own belief. A leaky-stop twin that never calls
        // RemoveDirectory leaves Core.DirSessions un-shrunk too, so checking
        // OpenWatches against Core's own claims would never catch it.
        let claimedDirs =
          t.Final.Claims
          |> Map.filter (fun _ claimants -> not (Set.isEmpty claimants))
          |> Map.toList
          |> List.map fst
          |> Set.ofList
        let fallback = Set.singleton "/sim/lifecycle/fallback"
        let orphaned = Set.difference t.Final.OpenWatches (Set.union claimedDirs fallback)
        match Set.isEmpty orphaned with
        | true -> Outcome.Holds
        | false ->
          Outcome.Violated(
            sprintf
              "directories still watched with no live claimant: %A (live sessions=%A, seed=%d, ops=%A)"
              (Set.toList orphaned) (Set.toList t.Final.Live) t.Scenario.Seed t.Scenario.Ops) }

  /// overflow-never-silent: an `Overflow` op's outcome is ALWAYS
  /// `RecoverFromOverflow`, distinct from `SoftReset`, `Reload` or
  /// `Ignore`. A consumer must always be able to tell "we don't know what
  /// changed" apart from every other case — the historical bug was an
  /// overflow folding into the SAME case a real `.fsproj` edit produces,
  /// so a consumer had no way to say why it reset, and often said nothing
  /// at all.
  let overflowNeverSilent : Invariant =
    { Id = "overflow-never-silent"
      Description =
        "An overflow's outcome is always RecoverFromOverflow — never SoftReset, Reload or Ignore, and never indistinguishable from an ordinary change."
      Check = fun t ->
        t.Final.Outcomes
        |> List.filter (fun o -> o.WasOverflow)
        |> List.tryPick (fun o ->
          match o.Action with
          | FileWatcher.FileChangeAction.RecoverFromOverflow _ -> None
          | other ->
            Some(
              sprintf
                "an overflow under dir #%d produced %A instead of RecoverFromOverflow (seed=%d, ops=%A)"
                o.DirIdx other t.Scenario.Seed t.Scenario.Ops))
        |> function
           | Some msg -> Outcome.Violated msg
           | None -> Outcome.Holds }

  /// no-outcome-claims-an-unseen-file: `RecoverFromOverflow`'s own payload
  /// is the WATCHED DIRECTORY that overflowed, never a specific file path —
  /// the type-level guarantee that an overflow can never be reported as
  /// "file X was patched," because SageFs genuinely does not know that.
  let overflowNamesOnlyTheDirectory : Invariant =
    { Id = "no-outcome-claims-an-unseen-file"
      Description =
        "RecoverFromOverflow always carries the directory that overflowed, never a file path — an overflow can never be misreported as a specific file's outcome."
      Check = fun t ->
        t.Final.Outcomes
        |> List.filter (fun o -> o.WasOverflow)
        |> List.tryPick (fun o ->
          match o.Action with
          | FileWatcher.FileChangeAction.RecoverFromOverflow dir when dir.EndsWith(".fs") || dir.EndsWith(".fsx") || dir.EndsWith(".fsproj") ->
            Some(sprintf "overflow for dir #%d carried a FILE-shaped path %s, not a directory (seed=%d)" o.DirIdx dir t.Scenario.Seed)
          | _ -> None)
        |> function
           | Some msg -> Outcome.Violated msg
           | None -> Outcome.Holds }

  /// every-save-exactly-one-outcome: the fold produces exactly one
  /// `Outcome` per `Save`/`Overflow` op, in the order they occurred — no op
  /// is dropped, and none is ever double-counted.
  let everySaveExactlyOneOutcome : Invariant =
    { Id = "every-save-exactly-one-outcome"
      Description =
        "Every Save/Overflow op in the scenario produces exactly one recorded outcome, in order — none dropped, none duplicated."
      Check = fun t ->
        let expected =
          t.Scenario.Ops
          |> List.choose (function
            | LifecycleOp.Save(d, _) -> Some(d, false)
            | LifecycleOp.Overflow d -> Some(d, true)
            | LifecycleOp.SessionStarts _
            | LifecycleOp.SessionStops _ -> None)
        let actual = t.Final.Outcomes |> List.map (fun o -> o.DirIdx, o.WasOverflow)
        match expected = actual with
        | true -> Outcome.Holds
        | false ->
          Outcome.Violated(
            sprintf
              "expected one outcome per Save/Overflow op in order %A, got %A (seed=%d)"
              expected actual t.Scenario.Seed) }

  let all : Invariant list =
    [ noOrphanedWatch; overflowNeverSilent; overflowNamesOnlyTheDirectory; everySaveExactlyOneOutcome ]

  /// The invariants that failed for a trace, if any (id, message).
  let violations (t: Trace) : (string * string) list =
    all
    |> List.choose (fun inv ->
      match inv.Check t with
      | Outcome.Holds -> None
      | Outcome.Violated msg -> Some(inv.Id, msg))
