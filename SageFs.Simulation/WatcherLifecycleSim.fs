namespace SageFs.Simulation

open System
open SageFs
open SageFs.WorkerProtocol

/// Deterministic Simulation Testing for the file-watcher lifecycle — the
/// leak and the swallowed-verdict bugs the daemon was actually caught in:
///
///   1. A stopped session's directory claim must be fully released — every
///      `StartWatch` the claim caused must be balanced by a `StopWatch` once
///      nothing claims that directory anymore. Measured live: one daemon
///      held 148,077 inotify watches with `sessionCount: 0`.
///   2. A watch-buffer overflow must never be folded into an ordinary
///      change: `FileWatcher.fileChangeAction` must route it to
///      `RecoverFromOverflow`, distinct from `SoftReset`/`Reload`, so a
///      consumer can never report "patched" or "project changed" for a
///      save it never actually saw.
///
/// Folds two REAL pure decision functions directly, never reimplemented:
///   * `LiveTestWatcherCore.apply` — the same claim/reconcile core
///     `FileReloadRoutingSim` folds for routing; this sim folds it for
///     WATCH-COUNT bookkeeping instead (do StartWatch/StopWatch balance?).
///   * `FileWatcher.fileChangeAction` — the save-outcome router bug #2
///     lives in.
///
/// Chaos is DATA, same shape as every other sim here: a `Scenario` is a
/// seed plus an ordered op list over small fixed pools. Same seed, same
/// ops, identical trace, forever.
///
/// Two twins reintroduce the actual historical bugs so the invariants can
/// prove they have teeth:
///   * `runLeakyStop` — a session stopping never releases its directory
///     claim (the shape of "stop_session doesn't dispose all of it").
///   * `runStaleOverflow` — an overflow is folded into an ordinary
///     `.fsproj`-shaped `SoftReset` instead of getting its own outcome (the
///     shape of "the overflow recovery synthesises a fake project-file
///     change," this repo's actual prior behaviour).
module WatcherLifecycleSim =

  [<RequireQualifiedAccess>]
  type LifecycleOp =
    /// A session starts and claims a directory (mirrors a daemon tick's
    /// `SyncToSessions` gaining an entry when `create_session` succeeds).
    | SessionStarts of sessionIdx: int * dirIdx: int
    /// A session stops (mirrors `stop_session` completing — the claim this
    /// session holds on every directory it claimed must be dropped).
    | SessionStops of sessionIdx: int
    /// An ordinary file change under a directory.
    | Save of dirIdx: int * fileIdx: int
    /// The watch on a directory overflows: events were lost, and SageFs
    /// cannot know which files actually changed.
    | Overflow of dirIdx: int

  /// A fully-specified, replayable scenario.
  type Scenario = { Seed: int; Ops: LifecycleOp list }

  // ── Fixed, deterministic pools ───────────────────────────────────────
  let private dirs = [| "/sim/lifecycle/fallback"; "/sim/lifecycle/dirA"; "/sim/lifecycle/dirB"; "/sim/lifecycle/dirC" |]
  let private files = [| "Alpha.fs"; "Beta.fs" |]
  let private fallbackDir = Some dirs.[0]

  let private sid (raw: string) =
    match SessionId.validate raw with
    | Ok s -> s
    | Error e -> failwithf "sim session id %s invalid: %s" raw e

  let private sessions = [| sid "00000001"; sid "00000002"; sid "00000003" |]

  /// One entry per Save/Overflow op, in op order: what `fileChangeAction`
  /// (or the stale-overflow twin) decided for it.
  type Outcome =
    { DirIdx: int
      WasOverflow: bool
      Action: FileWatcher.FileChangeAction }

  /// The pure fold state. `OpenWatches` is ground truth for "which
  /// directories currently hold a live OS-level watch," rebuilt purely from
  /// the `StartWatch`/`StopWatch` effects `LiveTestWatcherCore.apply`
  /// itself returns — not re-derived from `Core.Watched` (which is the
  /// reducer's OWN belief, not an independent check).
  ///
  /// `Claims` is a SECOND, independent ground truth: dir -> the sessions
  /// that SHOULD currently claim it, tracked purely from `SessionStarts`/
  /// `SessionStops` ops. A `SessionStops` op ALWAYS clears that session's
  /// claims here, regardless of whether the reducer under test actually
  /// called `RemoveDirectory` — if `Claims` were instead read back from
  /// `Core.DirSessions` (the reducer's own belief), the leaky-stop twin
  /// could never be caught: a stop that never calls `RemoveDirectory`
  /// leaves `Core.DirSessions` un-shrunk too, so "does OpenWatches match
  /// Core's own claims" would trivially hold even while every stopped
  /// session's watch sits open forever.
  type SimState =
    { Live: Set<int>
      Core: LiveTestWatcherCore.State
      OpenWatches: Set<string>
      Claims: Map<string, Set<int>>
      Outcomes: Outcome list }

  let private initial : SimState =
    { Live = Set.empty
      Core = LiveTestWatcherCore.empty
      OpenWatches = Set.empty
      Claims = Map.empty
      Outcomes = [] }

  let private applyEffects (opens: Set<string>) (effects: LiveTestWatcherCore.Effect list) : Set<string> =
    effects
    |> List.fold
      (fun o effect ->
        match effect with
        | LiveTestWatcherCore.StartWatch dir -> Set.add dir o
        | LiveTestWatcherCore.StopWatch dir -> Set.remove dir o
        | LiveTestWatcherCore.ArmDebounce
        | LiveTestWatcherCore.DrainPending _ -> o)
      opens

  /// Directories `sessionIdx` currently claims in `core` — read straight
  /// from `LiveTestWatcherCore.State.DirSessions`, which is the same field
  /// production's own `RemoveDirectory` effect derives from.
  let private claimsOf (core: LiveTestWatcherCore.State) (sessionIdx: int) : string list =
    core.DirSessions
    |> Map.toList
    |> List.filter (fun (_, claims) -> List.contains sessions.[sessionIdx] claims)
    |> List.map fst

  /// releaseOnStop=false is the leak twin: a stop drops the session from
  /// `Live` but never calls `RemoveDirectory`, so its claims (and their
  /// watches) live on forever — exactly "stop_session doesn't dispose all
  /// of it."
  let rec private go
    (overflowAction: FileWatcher.FileChange -> FileWatcher.FileChangeAction)
    (releaseOnStop: bool)
    (state: SimState)
    (ops: LifecycleOp list)
    : SimState =
    match ops with
    | [] -> state
    | op :: rest ->
      match op with
      | LifecycleOp.SessionStarts(s, d) ->
        let core', effects = LiveTestWatcherCore.apply fallbackDir state.Core (LiveTestWatcherCore.AddDirectory(dirs.[d], sessions.[s]))
        let opens' = applyEffects state.OpenWatches effects
        let claims' =
          let existing = Map.tryFind dirs.[d] state.Claims |> Option.defaultValue Set.empty
          Map.add dirs.[d] (Set.add s existing) state.Claims
        go overflowAction releaseOnStop { state with Live = Set.add s state.Live; Core = core'; OpenWatches = opens'; Claims = claims' } rest
      | LifecycleOp.SessionStops s ->
        // Ground truth ALWAYS clears this session's claims, independent of
        // `releaseOnStop` — see the `Claims` field doc.
        let claims' = state.Claims |> Map.map (fun _ claimants -> Set.remove s claimants)
        match releaseOnStop with
        | false ->
          go overflowAction releaseOnStop { state with Live = Set.remove s state.Live; Claims = claims' } rest
        | true ->
          let core', opens' =
            claimsOf state.Core s
            |> List.fold
              (fun (c, o) dir ->
                let c', effects = LiveTestWatcherCore.apply fallbackDir c (LiveTestWatcherCore.RemoveDirectory(dir, sessions.[s]))
                c', applyEffects o effects)
              (state.Core, state.OpenWatches)
          go overflowAction releaseOnStop { state with Live = Set.remove s state.Live; Core = core'; OpenWatches = opens'; Claims = claims' } rest
      | LifecycleOp.Save(d, f) ->
        let change : FileWatcher.FileChange =
          { FilePath = IO.Path.Combine(dirs.[d], files.[f])
            Kind = FileWatcher.FileChangeKind.Changed
            Timestamp = DateTimeOffset.UtcNow }
        let outcome = { DirIdx = d; WasOverflow = false; Action = FileWatcher.fileChangeAction change }
        go overflowAction releaseOnStop { state with Outcomes = outcome :: state.Outcomes } rest
      | LifecycleOp.Overflow d ->
        let change : FileWatcher.FileChange =
          { FilePath = dirs.[d]
            Kind = FileWatcher.FileChangeKind.Overflow
            Timestamp = DateTimeOffset.UtcNow }
        let outcome = { DirIdx = d; WasOverflow = true; Action = overflowAction change }
        go overflowAction releaseOnStop { state with Outcomes = outcome :: state.Outcomes } rest

  /// The result of folding a scenario to completion.
  type Trace = { Scenario: Scenario; Final: SimState; Reducer: string }

  let private runWith
    (name: string)
    (overflowAction: FileWatcher.FileChange -> FileWatcher.FileChangeAction)
    (releaseOnStop: bool)
    (scenario: Scenario)
    : Trace =
    let final = go overflowAction releaseOnStop initial scenario.Ops
    { Scenario = scenario; Final = { final with Outcomes = List.rev final.Outcomes }; Reducer = name }

  /// The real behaviour: `fileChangeAction` unmodified, stop actually
  /// releases every claim it holds.
  let run (scenario: Scenario) : Trace =
    runWith "real (fileChangeAction + release-on-stop)" FileWatcher.fileChangeAction true scenario

  /// TWIN: a session stopping never releases its directory claims — the
  /// leak shape. Proves "a stop releases everything it took" has teeth.
  let runLeakyStop (scenario: Scenario) : Trace =
    runWith "twin-leaky-stop" FileWatcher.fileChangeAction false scenario

  /// TWIN: an overflow is folded into an ordinary `.fsproj`-shaped
  /// `SoftReset`, the actual prior behaviour this sim exists to keep from
  /// coming back. Proves "an overflow never claims to have seen a specific
  /// change" has teeth.
  let runStaleOverflow (scenario: Scenario) : Trace =
    let staleAction (change: FileWatcher.FileChange) : FileWatcher.FileChangeAction =
      match change.Kind with
      | FileWatcher.FileChangeKind.Overflow -> FileWatcher.FileChangeAction.SoftReset
      | _ -> FileWatcher.fileChangeAction change
    runWith "twin-stale-overflow" staleAction true scenario
