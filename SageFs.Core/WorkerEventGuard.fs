namespace SageFs

/// Pure pid-guard decision for the three worker-lifecycle events the
/// SessionManager mailbox routes: WorkerReady, WorkerSpawnFailed, WorkerExited.
///
/// Each such event carries the pid of the worker that produced it. During a
/// spawn-first restart a session has TWO workers alive at once (the outgoing
/// old worker still serving, and the new one warming up), so an event's pid is
/// the ONLY fact that says whether it belongs to the session's current worker,
/// to the worker being retired, or to an already-replaced worker whose event
/// arrived late. Getting this wrong is the "pid-blind" race the roast named
/// (§4): a dead worker's late WorkerReady/WorkerSpawnFailed applied to its
/// replacement — a straggler Ready re-points the registry at the dying process,
/// a straggler SpawnFailed tombstones a healthy session.
///
/// This module is the single source of truth for that decision. Every one of
/// the three handlers routes on the matching `classify*` function below instead
/// of re-deriving the pid comparison inline, so the guard cannot drift between
/// them (the exact regression: before the fix WorkerExited had the guard but
/// WorkerReady/WorkerSpawnFailed did not). Pure data in, decision DU out — no
/// IO, no session objects — which is also what lets the DST harness fold the
/// REAL decision (SageFs.Simulation) rather than a reimplemented candidate.
///
/// Inputs shared by all three:
///   currentPid     = SessionLifecycleStatus.workerPid session.Info.Status
///                    (the registered worker; None once the session has no live
///                    worker — a Faulted tombstone or removed session).
///   pendingSwapPid = SessionLifecycleStatus.workerPid oldSession.Info.Status
///                    for the old session parked in PendingSwap during a
///                    spawn-first restart, else None. During a swap BOTH the
///                    registered session and the parked old session still carry
///                    the OLD pid — the new pid is not in state; it arrives on
///                    the event.
///   eventPid       = the pid carried on the incoming event.
module WorkerEventGuard =

  /// A pid marks an event stale only when it is real. Synthetic events post a
  /// non-positive pid on purpose — NotifyWorkerDied posts -1 to force a real
  /// exit lookup, and an unspawned session reads pid 0 — and neither may ever
  /// be judged "stale" (which would drop a genuine event). This single predicate
  /// replaces the `workerPid > 0` test copied across all three handlers.
  let isRealPid (pid: int) : bool = pid > 0

  // ── WorkerReady ─────────────────────────────────────────────────────────

  /// What to do with an incoming WorkerReady(eventPid).
  [<RequireQualifiedAccess>]
  type ReadyDecision =
    /// The ready belongs to the worker the session is waiting on: install its
    /// transport (and, if a swap is pending, commit it — retire the old worker).
    | Commit
    /// A straggler ready: either the retiring old worker's late ready during a
    /// swap, or a replaced worker's ready with no swap in flight. Ignoring it is
    /// what stops the registry being pointed back at a dead process.
    | IgnoreStale

  /// Classify a WorkerReady. Mirrors SessionManager's `isStaleReady`:
  ///   * swap pending  -> stale iff the ready is from the OLD (parked) worker.
  ///   * no swap        -> stale iff a real pid differs from the registered one.
  let classifyReady (currentPid: int option) (pendingSwapPid: int option) (eventPid: int) : ReadyDecision =
    let isStale =
      match pendingSwapPid with
      | Some _ ->
        isRealPid eventPid && pendingSwapPid = Some eventPid
      | None ->
        match currentPid with
        | Some cur -> isRealPid eventPid && cur <> eventPid
        | None -> false
    if isStale then ReadyDecision.IgnoreStale else ReadyDecision.Commit

  // ── WorkerSpawnFailed ───────────────────────────────────────────────────

  /// What to do with an incoming WorkerSpawnFailed(eventPid).
  [<RequireQualifiedAccess>]
  type SpawnFailedDecision =
    /// A swap is pending and the failure is the NEW (replacement) worker's:
    /// revert — restore the still-serving old worker, clear the pending swap.
    | RevertSwap
    /// The current worker failed to come up: fault the session with the message.
    | Fault
    /// A straggler spawn-failure from an already-replaced worker (e.g. a hard
    /// reset killed the old process while its port-await task was still reading
    /// stdout; on EOF it posts WorkerSpawnFailed). Ignoring it stops a fresh
    /// worker being tombstoned.
    | IgnoreStale

  /// Classify a WorkerSpawnFailed:
  ///   * swap pending, a real pid differs from the registered old pid -> revert
  ///     (the NEW replacement worker failed; restore the still-serving old one).
  ///   * swap pending, a real pid EQUALS the registered old pid       -> stale
  ///     (a straggler spawn-failure from the retiring old worker — e.g. its
  ///     awaitWorkerPort task posting late on EOF after it was killed; it must
  ///     NOT fault the healthy swap, exactly as WorkerReady/WorkerExited already
  ///     ignore the old worker's stragglers).
  ///   * no swap, a real event pid differs from the registered pid    -> stale.
  ///   * otherwise                                                    -> fault.
  ///
  /// The parked-old-pid stale case was surfaced by the Phase-2 DST harness
  /// (SageFs.Simulation): the previous `| _ -> Fault` fallback faulted the
  /// session on a straggler carrying the parked old pid mid-swap — a stale-pid
  /// application inconsistent with the other two events. Making all three ignore
  /// the retiring worker's stragglers uniformly closes it.
  let classifySpawnFailed (currentPid: int option) (pendingSwapPid: int option) (eventPid: int) : SpawnFailedDecision =
    match pendingSwapPid with
    | Some _ ->
      match currentPid with
      | Some oldPid when isRealPid eventPid && oldPid <> eventPid -> SpawnFailedDecision.RevertSwap
      | Some oldPid when isRealPid eventPid && oldPid = eventPid -> SpawnFailedDecision.IgnoreStale
      | _ -> SpawnFailedDecision.Fault
    | None ->
      match currentPid with
      | Some cur when isRealPid eventPid && cur <> eventPid -> SpawnFailedDecision.IgnoreStale
      | _ -> SpawnFailedDecision.Fault

  // ── WorkerExited ────────────────────────────────────────────────────────

  /// What to do with an incoming WorkerExited(eventPid).
  [<RequireQualifiedAccess>]
  type ExitDecision =
    /// A real exit of the session's current worker: run the restart policy
    /// (RestartAfter / Abandoned / Graceful).
    | Apply
    /// The expected retirement exit of the old worker during a spawn-first swap.
    | IgnoreRetired
    /// A straggler exit from an already-replaced worker, or a synthetic
    /// NotifyWorkerDied (pid -1) against a session that has no registered pid.
    | IgnoreStale

  /// Classify a WorkerExited. Mirrors SessionManager's WorkerExited handler:
  ///   * swap pending and the exit is the parked old worker's -> retired.
  ///   * no registered pid but a real exit pid                 -> stale.
  ///   * a real exit pid differs from the registered pid       -> stale.
  ///   * otherwise                                             -> apply.
  let classifyExited (currentPid: int option) (pendingSwapPid: int option) (eventPid: int) : ExitDecision =
    match pendingSwapPid with
    | Some _ when isRealPid eventPid && pendingSwapPid = Some eventPid ->
      ExitDecision.IgnoreRetired
    | _ ->
      match currentPid with
      | None when isRealPid eventPid -> ExitDecision.IgnoreStale
      | Some cur when cur <> eventPid && isRealPid eventPid -> ExitDecision.IgnoreStale
      | _ -> ExitDecision.Apply
