namespace SageFs.Simulation

open System
open SageFs
open SageFs.WorkerProtocol

/// Deterministic Simulation Testing (DST) for file-reload claim-routing —
/// folds the REAL `SageFs.LiveTestWatcherCore.apply` /
/// `isUnderWatchedDir` / `sessionsForPath` (never a reimplemented
/// candidate) to replace the two slow real-`FileSystemWatcher` Integration
/// tests in `SageFs.Tests/DaemonStateChangeContractTests.fs`
/// ("file save in a shared dir fires FileReloaded for every owning
/// session" and "removing one session's claim stops only its FileReloaded
/// events"), each of which spun up a real watcher and polled up to 10s.
///
/// UNLIKE `EvalActorDecision.fs` (which E1 extracted a brand-new pure core
/// out of `AppState.fs`), `LiveTestWatcherCore` was ALREADY a pure,
/// dependency-free decision core (roast-9 #10) — no Core extraction was
/// needed here. This sim folds the three real pure functions directly,
/// composed exactly as the impure shell composes them in its own
/// `drainPath` (`SageFs/DaemonMode.fs`):
///   * `LiveTestWatcherCore.apply` — claim/unclaim/save/drain state machine
///   * `LiveTestWatcherCore.isUnderWatchedDir` — gates whether a drained
///     path is live at all (a save whose dir was unclaimed before the
///     drain is dropped, not misattributed — see LiveTestWatcherCoreTests
///     T1, "stale claim removed during debounce")
///   * `LiveTestWatcherCore.sessionsForPath` — resolves the owning
///     session(s) of a drained path from the CURRENT (post-drain) claim
///     state, longest-matching-dir-prefix wins
/// The file-existence/read-content half of `drainPath` is impure IO and
/// deliberately out of scope — DrainPending -> FileReloaded ROUTING is the
/// subject under test, not disk access.
///
/// Same design principles as `CohortLandingSim` / `EvalActorSim`:
///   * Chaos is DATA: a `Scenario` is a seed plus an ordered list of
///     `RoutingOp`s over small fixed dir/session/file pools. Same seed +
///     same ops => the identical trace, forever. No clock, no IO, no
///     waits, no actor.
///   * The REAL routing is the subject, folded directly — never
///     reimplemented as a parallel "correct" candidate.
///   * Each drained path also carries an `ExpectedOwners` set tracked by an
///     INDEPENDENT claim bookkeeping fold (`Claim`/`Unclaim` only — it
///     never reads `LiveTestWatcherCore.State`), so the invariants have a
///     ground truth that does not depend on the reducer under test.
///   * Two TWIN routers reintroduce plausible/historical attribution bugs
///     so the invariants can prove they have teeth: `runBroadcastToAll`
///     (ignores per-dir claims — every session that has EVER claimed
///     anything gets every reload, the pre-isolation-fix "global active
///     session" shape `LiveTestWatcherManager`'s own header comment
///     describes) and `runDoubleFire` (the real owners, but each notified
///     twice — a duplicate-watcher-registration bug, e.g. both `Changed`
///     and `Created` firing the handler for one save).
module FileReloadRoutingSim =

  /// One scripted message driving the claim/save/drain state machine.
  /// `Claim`/`Unclaim` index into the CLAIMABLE dir pool (dirs 1..3 — index
  /// 0 is the reserved, session-less fallback dir, matching production:
  /// "the fallback dir claims no session," `LiveTestWatcherManager`'s own
  /// doc comment). `Save` may target ANY dir in the pool, including the
  /// fallback (dir 0), so a scenario can exercise "a save under the
  /// fallback never fires FileReloaded for anyone."
  [<RequireQualifiedAccess>]
  type RoutingOp =
    | Claim of dirIdx: int * sessionIdx: int
    | Unclaim of dirIdx: int * sessionIdx: int
    | Save of dirIdx: int * fileIdx: int
    | Drain

  /// A fully-specified, replayable scenario.
  type Scenario = { Seed: int; Ops: RoutingOp list }

  // ── Fixed, deterministic pools — no environment/CWD dependency ──────────
  // Absolute-looking paths so `Path.GetFullPath` (used inside
  // LiveTestWatcherCore) normalizes them the same way on every run/OS
  // without depending on the process's actual working directory.

  let private dirs = [| "/sim/watch/fallback"; "/sim/watch/dirA"; "/sim/watch/dirB"; "/sim/watch/dirC" |]
  let private files = [| "Alpha.fs"; "Beta.fs" |]

  let private sid (raw: string) =
    match SessionId.validate raw with
    | Ok s -> s
    | Error e -> failwithf "sim session id %s invalid: %s" raw e

  let private sessions = [| sid "00000001"; sid "00000002"; sid "00000003" |]

  /// dirs.[0] is always the fallback dir — every scenario models a daemon
  /// that has one configured, so a save under it exercises the
  /// "watched but session-less" contract instead of just being unwatched.
  let private fallbackDir = Some dirs.[0]

  let private pathOf (dirIdx: int) (fileIdx: int) : string =
    IO.Path.Combine(dirs.[dirIdx], files.[fileIdx])

  let private msgOf (op: RoutingOp) : LiveTestWatcherCore.Msg =
    match op with
    | RoutingOp.Claim(d, s) -> LiveTestWatcherCore.AddDirectory(dirs.[d], sessions.[s])
    | RoutingOp.Unclaim(d, s) -> LiveTestWatcherCore.RemoveDirectory(dirs.[d], sessions.[s])
    | RoutingOp.Save(d, f) -> LiveTestWatcherCore.FileSaved(pathOf d f)
    | RoutingOp.Drain -> LiveTestWatcherCore.DebounceElapsed

  let private sessionIdx (s: SessionId) : int =
    sessions |> Array.findIndex (fun x -> SessionId.value x = SessionId.value s)

  /// A router: given the post-apply `LiveTestWatcherCore.State` and a
  /// drained path, who gets FileReloaded?
  type Router = LiveTestWatcherCore.State -> string -> SessionId list

  /// The subject under test: `isUnderWatchedDir` gates liveness,
  /// `sessionsForPath` resolves owners — composed exactly like the shell's
  /// `drainPath`.
  let private real : Router =
    fun state path ->
      match LiveTestWatcherCore.isUnderWatchedDir state.Watched path with
      | false -> []
      | true -> LiveTestWatcherCore.sessionsForPath state.DirSessions path

  /// TWIN 1: broadcast-to-all.
  let private broadcastToAll : Router =
    fun state path ->
      match LiveTestWatcherCore.isUnderWatchedDir state.Watched path with
      | false -> []
      | true -> state.DirSessions |> Map.toList |> List.collect snd |> List.distinct

  /// TWIN 2: double-fire — the real owners, each notified twice.
  let private doubleFire : Router =
    fun state path -> real state path |> List.collect (fun s -> [ s; s ])

  /// One drained path's routing outcome. `ExpectedOwners` is ground truth
  /// from the independent `Claims` bookkeeping (below), NOT from
  /// `LiveTestWatcherCore.State` — a genuine external oracle, not a
  /// tautology against the reducer under test. `SessionIdxs` is what the
  /// router under test actually decided (session-pool indices, in the
  /// order the router returned them — duplicates are preserved so the
  /// double-fire twin's bug is visible).
  type RoutingDecision =
    { DirIdx: int
      FileIdx: int
      Path: string
      ExpectedOwners: Set<int>
      SessionIdxs: int list }

  /// The pure fold state: the REAL `LiveTestWatcherCore.State` (folded
  /// through `apply`, exactly production's shape) plus the independent
  /// claim-bookkeeping ground truth and the accumulated drain log.
  type SimState =
    { Core: LiveTestWatcherCore.State
      /// dirIdx -> claiming sessionIdxs, tracked ONLY from Claim/Unclaim
      /// ops — deliberately never read from `Core`.
      Claims: Map<int, Set<int>>
      /// Most-recent-first while folding; reversed to op order in `Trace`.
      Drains: RoutingDecision list }

  let private initial : SimState =
    { Core = LiveTestWatcherCore.empty; Claims = Map.empty; Drains = [] }

  let private applyClaimTracking (claims: Map<int, Set<int>>) (op: RoutingOp) : Map<int, Set<int>> =
    match op with
    | RoutingOp.Claim(d, s) ->
      let existing = Map.tryFind d claims |> Option.defaultValue Set.empty
      Map.add d (Set.add s existing) claims
    | RoutingOp.Unclaim(d, s) ->
      let existing = Map.tryFind d claims |> Option.defaultValue Set.empty
      Map.add d (Set.remove s existing) claims
    | RoutingOp.Save _
    | RoutingOp.Drain -> claims

  /// Resolve a drained path back to its (dirIdx, fileIdx) — exact, not
  /// heuristic: the pool is small and `pathOf` is deterministic, so this is
  /// a genuine reverse lookup.
  let private resolvePath (path: string) : int * int =
    let dirIdx = dirs |> Array.findIndex (fun d -> path.StartsWith(d, StringComparison.OrdinalIgnoreCase))
    let fileIdx = files |> Array.findIndex (fun f -> path.EndsWith(f, StringComparison.OrdinalIgnoreCase))
    dirIdx, fileIdx

  let rec private go (router: Router) (state: SimState) (ops: RoutingOp list) : SimState =
    match ops with
    | [] -> state
    | op :: rest ->
      let core', effects = LiveTestWatcherCore.apply fallbackDir state.Core (msgOf op)
      let claims' = applyClaimTracking state.Claims op
      let newDrains =
        effects
        |> List.collect (fun effect ->
          match effect with
          | LiveTestWatcherCore.DrainPending paths ->
            [ for path in paths do
                let dirIdx, fileIdx = resolvePath path
                let expected = claims' |> Map.tryFind dirIdx |> Option.defaultValue Set.empty
                yield
                  { DirIdx = dirIdx
                    FileIdx = fileIdx
                    Path = path
                    ExpectedOwners = expected
                    SessionIdxs = router core' path |> List.map sessionIdx } ]
          | _ -> [])
      go router { Core = core'; Claims = claims'; Drains = newDrains @ state.Drains } rest

  /// The result of folding a scenario to completion.
  type Trace = { Scenario: Scenario; Final: SimState; Reducer: string }

  let private runWith (name: string) (router: Router) (scenario: Scenario) : Trace =
    let final = go router initial scenario.Ops
    { Scenario = scenario; Final = { final with Drains = List.rev final.Drains }; Reducer = name }

  /// Run through the REAL routing (isUnderWatchedDir + sessionsForPath) —
  /// the subject under test, in production's own shape.
  let run (scenario: Scenario) : Trace =
    runWith "real (isUnderWatchedDir + sessionsForPath)" real scenario

  /// Run through the broadcast-to-all twin — used to prove
  /// `unclaimed-sessions-get-none` (and, incidentally,
  /// `each-owning-session-gets-exactly-one-FileReloaded-per-save`) has teeth.
  let runBroadcastToAll (scenario: Scenario) : Trace =
    runWith "twin-broadcast-to-all" broadcastToAll scenario

  /// Run through the double-fire twin — used to prove
  /// `each-owning-session-gets-exactly-one-FileReloaded-per-save` has teeth.
  let runDoubleFire (scenario: Scenario) : Trace =
    runWith "twin-double-fire" doubleFire scenario
