namespace SageFs.Simulation

open System
open SageFs

/// Phase 2 DST: simulate the SessionManager mailbox's worker-lifecycle command
/// handling deterministically, to REPRODUCE the pid-blind restart race the roast
/// named (§4) — a dead worker's late WorkerReady/WorkerSpawnFailed applied to its
/// replacement — as a replayable regression test.
///
/// Same design principles as Phase 1 (see Scenario.fs):
///   * Chaos is DATA: a `MgrScenario` is an ordered, seeded list of pid-carrying
///     commands, including stragglers (events reusing an already-retired pid).
///   * The REAL decision is the subject: the faithful reducer folds through the
///     REAL `WorkerEventGuard.classify*` functions extracted from the mailbox
///     loop — not a reimplemented candidate.
///   * The oracle is a named invariant (`no-stale-pid-applied`): a stale/retired
///     worker's event must never mutate the session's current worker.
///
/// A second, `pidBlind`, reducer models the PRE-FIX behavior (WorkerReady and
/// WorkerSpawnFailed ignoring the pid, as before the guard existed). The
/// invariant HOLDS against the real reducer and is VIOLATED against the pidBlind
/// one — which is exactly what proves the invariant has teeth and pins the fix.
module WorkerLifecycleSim =

  /// One simulated mailbox command. The pid is the crux: a spawn-first restart
  /// runs two workers at once, so an event's pid is the only thing that says
  /// which worker it belongs to.
  [<RequireQualifiedAccess>]
  type MgrCommand =
    /// Create the session and bring its first worker to Ready with this pid.
    | Create of pid: int
    /// A worker reports Ready. During a swap only the NEW worker's pid commits.
    | Ready of pid: int
    /// A worker process exited. exitCode 0 = graceful stop, non-zero = crash.
    | Exited of pid: int * exitCode: int
    /// A worker failed to come up.
    | SpawnFailed of pid: int
    /// Begin a spawn-first restart: park the old worker, warm a new one (newPid).
    | HardReset of newPid: int
    /// Stop the session.
    | Stop

  /// The pid an event carries, if any — for the invariant to reason about
  /// stragglers. `HardReset`'s pid is the NEW worker's (never a straggler) and
  /// `Stop` carries none, so neither participates in the stale-pid check.
  let private eventPidOf (cmd: MgrCommand) : int option =
    match cmd with
    | MgrCommand.Ready pid
    | MgrCommand.Exited (pid, _)
    | MgrCommand.SpawnFailed pid -> Some pid
    | MgrCommand.Create _
    | MgrCommand.HardReset _
    | MgrCommand.Stop -> None

  /// The session's status, modeled as a DU so illegal combinations are
  /// unrepresentable (no bool "isSwapping"/"isTerminal" flags). During a
  /// spawn-first swap BOTH the registered session and the parked old session
  /// still carry the OLD pid — the new pid is not in state; it arrives on the
  /// Ready/SpawnFailed event — so `Swapping` carries exactly the one registered
  /// old pid, mirroring the real mailbox.
  [<RequireQualifiedAccess>]
  type SimStatus =
    /// No session exists yet.
    | Empty
    /// A live worker with this pid is serving.
    | Ready of pid: int
    /// A spawn-first restart is in flight; this old pid is still registered and
    /// serving while the replacement warms up.
    | Swapping of registeredPid: int
    /// The session stopped gracefully.
    | Stopped
    /// The session faulted.
    | Faulted of reason: string

  /// The registered worker pid a status exposes — exactly
  /// `SessionLifecycleStatus.workerPid` in the real code.
  let currentPid (s: SimStatus) : int option =
    match s with
    | SimStatus.Ready pid -> Some pid
    | SimStatus.Swapping pid -> Some pid
    | SimStatus.Empty
    | SimStatus.Stopped
    | SimStatus.Faulted _ -> None

  /// The pid parked in a pending swap (the old worker being retired), if any.
  let pendingSwapPid (s: SimStatus) : int option =
    match s with
    | SimStatus.Swapping pid -> Some pid
    | SimStatus.Empty
    | SimStatus.Ready _
    | SimStatus.Stopped
    | SimStatus.Faulted _ -> None

  /// A coarse terminal classification for the invariant — a DU, not a bool, so
  /// "live-but-swapping" and "faulted" can never be confused.
  [<RequireQualifiedAccess>]
  type Liveness =
    | Absent
    | Live
    | Stopped
    | Faulted

  let liveness (s: SimStatus) : Liveness =
    match s with
    | SimStatus.Empty -> Liveness.Absent
    | SimStatus.Ready _
    | SimStatus.Swapping _ -> Liveness.Live
    | SimStatus.Stopped -> Liveness.Stopped
    | SimStatus.Faulted _ -> Liveness.Faulted

  /// A fully-specified, replayable scenario. Same seed + same commands => the
  /// identical trace.
  type MgrScenario =
    { Seed: int
      Commands: MgrCommand list }

  /// One folded step: the command, the status before/after, and the set of
  /// already-retired pids at the moment the command was processed (for the
  /// invariant). `Retired` is harness bookkeeping, not domain state.
  type MgrStep =
    { Index: int
      Command: MgrCommand
      StatusBefore: SimStatus
      StatusAfter: SimStatus
      RetiredBefore: Set<int> }

  type MgrTrace =
    { Scenario: MgrScenario
      Steps: MgrStep list
      /// Which reducer produced this trace, for reporting (real guard vs the
      /// pre-fix pid-blind model).
      Reducer: string }

  // ── Reducers ──────────────────────────────────────────────────────────────

  /// Apply a graceful/crash exit of the CURRENT worker. The restart-backoff
  /// decision is Phase 1's domain (RestartPolicy); here we only need that a real
  /// exit of the current worker changes state (so the invariant can tell a
  /// legitimate mutation from a stale one).
  let private applyCurrentExit (exitCode: int) : SimStatus =
    if exitCode = 0 then SimStatus.Stopped
    else SimStatus.Faulted (sprintf "worker crashed with exit code %d" exitCode)

  /// A pid genuinely retired/superseded by this transition, if any — added to
  /// the running Retired set so a later event reusing it is a known straggler.
  /// Threaded by both reducers identically (it is bookkeeping about which pids
  /// are dead, independent of whether the reducer is buggy).
  let private retiredBy (before: SimStatus) (cmd: MgrCommand) : int option =
    match before, cmd with
    // A committed swap retires the old (registered) worker.
    | SimStatus.Swapping oldPid, MgrCommand.Ready ev when WorkerEventGuard.isRealPid ev && ev <> oldPid -> Some oldPid
    // A reverted swap kills the failed replacement (its pid never became current).
    | SimStatus.Swapping oldPid, MgrCommand.SpawnFailed ev when WorkerEventGuard.isRealPid ev && ev <> oldPid -> Some ev
    // A real exit / stop of the current worker retires it.
    | SimStatus.Ready cur, MgrCommand.Exited (ev, _) when ev = cur -> Some cur
    | SimStatus.Ready cur, MgrCommand.Stop -> Some cur
    | _ -> None

  /// The FAITHFUL reducer: routes every worker-lifecycle command through the
  /// REAL `WorkerEventGuard` decision extracted from the mailbox loop.
  let private stepReal (before: SimStatus) (cmd: MgrCommand) : SimStatus =
    let cur = currentPid before
    let pend = pendingSwapPid before
    match before with
    | SimStatus.Stopped
    | SimStatus.Faulted _ ->
      before // terminal: absorbing
    | SimStatus.Empty ->
      match cmd with
      | MgrCommand.Create pid -> SimStatus.Ready pid
      | _ -> before
    | SimStatus.Ready _
    | SimStatus.Swapping _ ->
      match cmd with
      | MgrCommand.Create _ -> before // already exists
      | MgrCommand.Stop -> SimStatus.Stopped
      | MgrCommand.HardReset _ ->
        match before with
        | SimStatus.Ready p -> SimStatus.Swapping p // park old, warm the new worker
        | _ -> before // a reset mid-swap is ignored here
      | MgrCommand.Ready ev ->
        match WorkerEventGuard.classifyReady cur pend ev with
        | WorkerEventGuard.ReadyDecision.IgnoreStale -> before
        | WorkerEventGuard.ReadyDecision.Commit -> SimStatus.Ready ev
      | MgrCommand.SpawnFailed ev ->
        match WorkerEventGuard.classifySpawnFailed cur pend ev with
        | WorkerEventGuard.SpawnFailedDecision.IgnoreStale -> before
        | WorkerEventGuard.SpawnFailedDecision.RevertSwap ->
          match before with
          | SimStatus.Swapping oldPid -> SimStatus.Ready oldPid // revert to the still-serving old worker
          | _ -> before
        | WorkerEventGuard.SpawnFailedDecision.Fault -> SimStatus.Faulted "worker spawn failed"
      | MgrCommand.Exited (ev, exitCode) ->
        match WorkerEventGuard.classifyExited cur pend ev with
        | WorkerEventGuard.ExitDecision.IgnoreRetired
        | WorkerEventGuard.ExitDecision.IgnoreStale -> before
        | WorkerEventGuard.ExitDecision.Apply -> applyCurrentExit exitCode

  /// The PRE-FIX reducer: models the historical pid-blind bug — WorkerReady and
  /// WorkerSpawnFailed ignore the pid entirely (WorkerExited always had its
  /// guard, so it is kept faithful). This is the state of the code the roast
  /// described; the `no-stale-pid-applied` invariant is VIOLATED against it,
  /// which is the deterministic reproduction of the race.
  let private stepPidBlind (before: SimStatus) (cmd: MgrCommand) : SimStatus =
    let cur = currentPid before
    let pend = pendingSwapPid before
    match before with
    | SimStatus.Stopped
    | SimStatus.Faulted _ -> before
    | SimStatus.Empty ->
      match cmd with
      | MgrCommand.Create pid -> SimStatus.Ready pid
      | _ -> before
    | SimStatus.Ready _
    | SimStatus.Swapping _ ->
      match cmd with
      | MgrCommand.Create _ -> before
      | MgrCommand.Stop -> SimStatus.Stopped
      | MgrCommand.HardReset _ ->
        match before with
        | SimStatus.Ready p -> SimStatus.Swapping p
        | _ -> before
      // PID-BLIND: any Ready commits, whatever pid it carries — a retired
      // worker's late Ready re-points the registry at the dead process and (mid
      // swap) clears the pending swap.
      | MgrCommand.Ready ev -> SimStatus.Ready ev
      // PID-BLIND: any spawn failure tombstones the session, even a straggler
      // from an already-replaced worker — killing a healthy session.
      | MgrCommand.SpawnFailed _ -> SimStatus.Faulted "worker spawn failed"
      // WorkerExited kept its pid guard even before the fix — model it faithfully.
      | MgrCommand.Exited (ev, exitCode) ->
        match WorkerEventGuard.classifyExited cur pend ev with
        | WorkerEventGuard.ExitDecision.IgnoreRetired
        | WorkerEventGuard.ExitDecision.IgnoreStale -> before
        | WorkerEventGuard.ExitDecision.Apply -> applyCurrentExit exitCode

  /// Fold a scenario through a chosen step function, producing its trace.
  let private runWith (name: string) (step: SimStatus -> MgrCommand -> SimStatus) (scenario: MgrScenario) : MgrTrace =
    let folder (status, retired, steps) (idx, cmd) =
      let after = step status cmd
      let retired' =
        match retiredBy status cmd with
        | Some pid -> Set.add pid retired
        | None -> retired
      let st =
        { Index = idx; Command = cmd; StatusBefore = status; StatusAfter = after; RetiredBefore = retired }
      (after, retired', st :: steps)
    let (_, _, revSteps) =
      scenario.Commands
      |> List.mapi (fun i c -> (i, c))
      |> List.fold folder (SimStatus.Empty, Set.empty, [])
    { Scenario = scenario; Steps = List.rev revSteps; Reducer = name }

  /// Run through the REAL extracted guard — the subject under test.
  let run (scenario: MgrScenario) : MgrTrace = runWith "real-WorkerEventGuard" stepReal scenario

  /// Run through the pre-fix pid-blind model — used to prove the invariant has
  /// teeth (it must FAIL here).
  let runPidBlind (scenario: MgrScenario) : MgrTrace = runWith "pid-blind-prefix" stepPidBlind scenario
