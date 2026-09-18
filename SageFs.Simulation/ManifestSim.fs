namespace SageFs.Simulation

open SageFs.Features.DaemonManifest

/// Phase 3 DST (Brief B5): simulate the daemon manifest owner's concurrency
/// semantics deterministically, to prove the single-owner mailbox design (see
/// SageFs.Core/Features/ManifestOwner.fs) is free of the lost-update race the
/// roast named (§4) as a HIGH-risk durable-state gap — and, symmetrically, to
/// reproduce that race under a twin reducer modeling the pre-fix
/// "read-merge-write" shape (periodicManifestSave, the shutdown save, and
/// PurgeSession each independently reading a possibly-stale snapshot of
/// daemon.sagefm and writing their own merge back over it, with no lock).
///
/// Pure/in-memory only: no filesystem IO anywhere in this module. "Durable"
/// below models the persisted file in memory; it is never the real
/// ~/.SageFs/daemon.sagefm, and nothing here starts a daemon or touches disk.
///
/// Same design shape as Phase 2 (WorkerLifecycleSim.fs):
///   * Chaos is DATA: a ManifestScenario is an ordered, seeded command list.
///   * The REAL decision is the subject: `stepOwner` gates every Apply
///     exactly as `ManifestOwner.commit` gates it (AfterShutdown /
///     BaseUnreadable / WriteFailed), then applies it through the REAL, pure
///     `ManifestMutation.apply` (SageFs.Core) — never a reimplemented
///     candidate.
///   * `stepReadMergeWrite` is the TWIN: it also calls the REAL
///     `ManifestMutation.apply`, but merges each Apply onto a snapshot that
///     may be stale — reproducing the pre-single-owner lost-update bug.
module ManifestSim =

  /// A command sent to the modeled manifest owner. `CorruptBase` / `WriteFault`
  /// / `Restore` are fault injection: they flip the modeled durable store
  /// between readable/unreadable and writes between succeeding/failing —
  /// they mutate only the in-memory `SimState`, never real IO. `Shutdown` is
  /// a sim-level convenience for flipping `Lifecycle` directly (production
  /// only ever reaches `ShutDown` via committing a `SyncLive ShuttingDown`
  /// mutation, which this sim also supports as an ordinary `Apply` — both
  /// paths are exercised by the generators).
  [<RequireQualifiedAccess>]
  type OwnerCmd =
    | Apply of ManifestMutation
    | Read
    | Shutdown
    | CorruptBase
    | WriteFault
    | Restore

  /// The daemon's coarse serving lifecycle — a DU, not a bool, mirroring
  /// `ManifestOwner`'s internal `Lifecycle` (Serving | ShutDown).
  [<RequireQualifiedAccess>]
  type Lifecycle =
    | Live
    | ShutDown

  /// The in-memory model of the owner's held state plus the fault switches.
  /// `Current` is what the owner holds and would report to a `Read` right
  /// now; `Durable` is what the modeled persisted store actually contains
  /// (they diverge only while a write is failing — `NextWriteFails`).
  /// `BaseReadable` / `NextWriteFails` are genuine two-valued fault
  /// switches — they don't duplicate a DU case the way a domain "isActive"
  /// bool would; they ARE the fault's entire state (readable-or-not,
  /// next-write-fails-or-not), toggled only by the fault-injection commands
  /// above. Kept as bools per the brief's explicit carve-out for this case.
  type SimState =
    { Current: DaemonManifestState
      Durable: DaemonManifestState
      Lifecycle: Lifecycle
      BaseReadable: bool
      NextWriteFails: bool }

  module SimState =
    let empty : SimState =
      { Current = DaemonManifestState.empty
        Durable = DaemonManifestState.empty
        Lifecycle = Lifecycle.Live
        BaseReadable = true
        NextWriteFails = false }

  /// What committing one Apply produced — mirrors `ManifestOwner`'s
  /// `CommitResult` cases, collapsed to what the sim needs to reason about.
  [<RequireQualifiedAccess>]
  type ApplyOutcome =
    | Committed
    | RejectedAfterShutdown
    | RejectedBaseUnreadable
    | WriteFailed

  /// A fully-specified, replayable scenario. Same seed + same commands =>
  /// the identical trace.
  type ManifestScenario =
    { Seed: int
      Commands: OwnerCmd list }

  /// One folded step: the command, the state before/after, the Apply
  /// outcome (if this was an Apply), and what a Read observed (if this was
  /// a Read). This is everything `ManifestInvariants` needs to check
  /// properties without ever calling the reducer functions below.
  type ManifestStep =
    { Index: int
      Command: OwnerCmd
      StateBefore: SimState
      StateAfter: SimState
      Outcome: ApplyOutcome option
      ReadResult: DaemonManifestState option }

  type ManifestTrace =
    { Scenario: ManifestScenario
      Steps: ManifestStep list
      /// Which reducer produced this trace, for reporting (real single-owner
      /// vs the pre-fix read-merge-write twin).
      Reducer: string }

  // ── shared (non-Apply) command handling — identical for both reducers ──────

  let private applyFaultOrLifecycleCmd (state: SimState) (cmd: OwnerCmd) : SimState =
    match cmd with
    | OwnerCmd.Shutdown -> { state with Lifecycle = Lifecycle.ShutDown }
    | OwnerCmd.CorruptBase -> { state with BaseReadable = false }
    | OwnerCmd.WriteFault -> { state with NextWriteFails = true }
    | OwnerCmd.Restore -> { state with BaseReadable = true; NextWriteFails = false }
    | OwnerCmd.Apply _
    | OwnerCmd.Read -> state

  // ── the FAITHFUL single-owner reducer ───────────────────────────────────────

  /// Commit one mutation exactly as `ManifestOwner.commit` gates it
  /// (ManifestOwner.fs:117-134): an AfterShutdown `SyncLive Running` is
  /// refused; an unreadable base blocks the write (fail-closed — Current and
  /// Durable are left exactly as they were, never reset toward empty); a
  /// failing write still advances the in-memory `Current` (the change is
  /// queued, not lost — `CommitError.WriteFailed`'s own documented contract,
  /// ManifestOwner.fs:42) but leaves `Durable` behind until a later
  /// successful write.
  let private commitOwner (state: SimState) (mutation: ManifestMutation) : SimState * ApplyOutcome =
    match mutation, state.Lifecycle with
    | ManifestMutation.SyncLive (_, _, _, LiveSync.Running), Lifecycle.ShutDown ->
      state, ApplyOutcome.RejectedAfterShutdown
    | _ ->
      match state.BaseReadable with
      | false -> state, ApplyOutcome.RejectedBaseUnreadable
      | true ->
        let next = ManifestMutation.apply mutation state.Current
        let lifecycle =
          match mutation with
          | ManifestMutation.SyncLive (_, _, _, LiveSync.ShuttingDown) -> Lifecycle.ShutDown
          | _ -> state.Lifecycle
        match state.NextWriteFails with
        | true -> { state with Current = next; Lifecycle = lifecycle }, ApplyOutcome.WriteFailed
        | false -> { state with Current = next; Durable = next; Lifecycle = lifecycle }, ApplyOutcome.Committed

  let private stepOwner
    (state: SimState)
    (cmd: OwnerCmd)
    : SimState * ApplyOutcome option * DaemonManifestState option =
    match cmd with
    | OwnerCmd.Apply mutation ->
      let state', outcome = commitOwner state mutation
      state', Some outcome, None
    | OwnerCmd.Read -> state, None, Some state.Current
    | OwnerCmd.Shutdown
    | OwnerCmd.CorruptBase
    | OwnerCmd.WriteFault
    | OwnerCmd.Restore -> applyFaultOrLifecycleCmd state cmd, None, None

  // ── the READ-MERGE-WRITE twin (pre-fix, 3-writer race) ─────────────────────

  /// A run of consecutive `Apply` commands (no Read/Shutdown/fault command
  /// between them) models independent, unsynchronized writers scheduled back
  /// to back: EVERY writer in the run reads the SAME pre-run `Durable`
  /// snapshot — the file as it stood before any of them started, exactly the
  /// historical bug (periodicManifestSave / the shutdown save / PurgeSession
  /// each did their own read-merge-write against the shared file with no
  /// lock, DaemonMode.fs:796-828,654-668, DaemonPersistence.fs:88-104) — and
  /// each writer's write clobbers whatever the previous writer in the run
  /// just wrote. Only the LAST writer's single mutation survives; the others
  /// are silently lost — the `no-lost-update` violation this twin exists to
  /// reproduce. Any non-Apply command ends the run (its own effect is still
  /// applied faithfully), so the NEXT Apply run re-snapshots fresh.
  let private stepReadMergeWrite
    (batchBase: DaemonManifestState option)
    (state: SimState)
    (cmd: OwnerCmd)
    : SimState * ApplyOutcome option * DaemonManifestState option * DaemonManifestState option =
    match cmd with
    | OwnerCmd.Apply mutation ->
      match mutation, state.Lifecycle with
      | ManifestMutation.SyncLive (_, _, _, LiveSync.Running), Lifecycle.ShutDown ->
        state, Some ApplyOutcome.RejectedAfterShutdown, None, batchBase
      | _ ->
        match state.BaseReadable with
        | false -> state, Some ApplyOutcome.RejectedBaseUnreadable, None, batchBase
        | true ->
          // The writer's read: the run's shared stale base if one is already
          // open, else a fresh read of Durable — opening a new run.
          let readBase = defaultArg batchBase state.Durable
          let merged = ManifestMutation.apply mutation readBase
          let lifecycle =
            match mutation with
            | ManifestMutation.SyncLive (_, _, _, LiveSync.ShuttingDown) -> Lifecycle.ShutDown
            | _ -> state.Lifecycle
          match state.NextWriteFails with
          | true ->
            { state with Current = merged; Lifecycle = lifecycle }, Some ApplyOutcome.WriteFailed, None, Some readBase
          | false ->
            { state with Current = merged; Durable = merged; Lifecycle = lifecycle }, Some ApplyOutcome.Committed, None, Some readBase
    | OwnerCmd.Read -> state, None, Some state.Current, None
    | OwnerCmd.Shutdown
    | OwnerCmd.CorruptBase
    | OwnerCmd.WriteFault
    | OwnerCmd.Restore -> applyFaultOrLifecycleCmd state cmd, None, None, None

  // ── folding ─────────────────────────────────────────────────────────────

  /// Fold a scenario through the faithful single-owner reducer.
  let run (scenario: ManifestScenario) : ManifestTrace =
    let folder (state, steps) (idx, cmd) =
      let after, outcome, readResult = stepOwner state cmd
      let st =
        { Index = idx; Command = cmd; StateBefore = state; StateAfter = after
          Outcome = outcome; ReadResult = readResult }
      (after, st :: steps)
    let (_, revSteps) =
      scenario.Commands
      |> List.mapi (fun i c -> (i, c))
      |> List.fold folder (SimState.empty, [])
    { Scenario = scenario; Steps = List.rev revSteps; Reducer = "real-ManifestOwner" }

  /// Fold a scenario through the pre-fix read-merge-write twin — used to
  /// prove `no-lost-update` has teeth (it must FAIL here).
  let runReadMergeWrite (scenario: ManifestScenario) : ManifestTrace =
    let folder (state, batchBase, steps) (idx, cmd) =
      let after, outcome, readResult, batchBase' = stepReadMergeWrite batchBase state cmd
      let st =
        { Index = idx; Command = cmd; StateBefore = state; StateAfter = after
          Outcome = outcome; ReadResult = readResult }
      (after, batchBase', st :: steps)
    let (_, _, revSteps) =
      scenario.Commands
      |> List.mapi (fun i c -> (i, c))
      |> List.fold folder (SimState.empty, None, [])
    { Scenario = scenario; Steps = List.rev revSteps; Reducer = "read-merge-write-twin" }
