namespace SageFs.Simulation

open System
open SageFs

/// Gap F (outcome-gate-sweep.md §3): Deterministic Simulation Testing for
/// workflow switching (`switch_workflow` — `SessionCommand.SwitchWorkflow`,
/// `SageFs.Core/SessionManager.fs:1620-1642`, dispatching into `spawnFirst`,
/// `SessionManager.fs:703-756`).
///
/// HONESTY (per the DST authoring spec): this is a HYBRID, not a pure
/// extraction. The pid-classification decision — "does this Ready/
/// SpawnFailed/Exited event belong to the worker the session is waiting on,
/// the one being retired, or an already-replaced straggler?" — folds the
/// REAL, already-extracted `SageFs.WorkerEventGuard.classify*` functions
/// directly (the same seam `WorkerLifecycleSim.fs` folds for the pid-blind
/// restart race). The SURROUNDING control flow — the `SwitchWorkflow`
/// handler's own gating ("same workflow => no-op") and `spawnFirst`'s
/// spawn-first-then-park-then-retire shape, including its single-slot
/// `PendingSwap` — is a FAITHFUL MINIATURE MODEL, exactly like
/// `SupervisorSim.fs`: the real mailbox loop is a giant closure over
/// `MailboxProcessor`/`inbox`/`runtime` and is not extractable without a Core
/// change. A modeled DST is a weaker gate than a folded one for the modeled
/// part — it cannot catch production drifting from the model — so the
/// `PendingSwap` single-slot-overwrite shape below is deliberately copied
/// from `SessionManager.fs:750` (`ManagerState.setPendingSwap id session
/// state`, which is `Map.add` — an unconditional overwrite, never a merge or
/// a rejection) rather than "fixed" in the model, because reproducing that
/// exact shape is what let this sim surface a real transition defect — see
/// `WorkflowSwitchGenerators.overlappingSwitchThenRevertLosesOriginalWorkflow`
/// and the "known transition defect" section of `WorkflowSwitchSimTests.fs`.
///
/// `SessionWorkflow` (`SageFs.Core/WorkflowTypes.fs:117-128`) has THREE cases
/// — `Interactive | LiveTesting | HotReload of BrowserRefreshConfig` — so the
/// transition space this sim exercises is the full 3x3 (nine ordered pairs,
/// including same-workflow no-ops), not just Interactive<->HotReload.
///
/// Same design principles as `FileReloadRoutingSim` / `SupervisorSim`:
///   * Chaos is DATA: a `Scenario` is a seed plus an ordered `SwitchOp` list
///     over a small fixed workflow-index pool. Same seed => the identical
///     trace, forever. No clock, no IO, no waits, no actor.
///   * Two reducers share the SAME pid-guarded event handling and differ only
///     in how a NEWLY REQUESTED switch is accepted: `spawnFirst` (the real
///     shape — park the old worker, keep it addressable, warm the
///     replacement) vs. the `stopBeforeSpawn` TWIN (stop the old worker
///     synchronously before the replacement exists — the "dead window"
///     AGENTS.md's spawn-first doctrine exists to prevent).
module WorkflowSwitchSim =

  /// One scripted message driving the switch/warmup/commit state machine.
  /// `newPid` on `RequestSwitch` is the replacement worker's pid IF the
  /// request is accepted (target <> current workflow) — mirrors
  /// `runtime.StartWorkerProcess` always allocating a fresh OS pid the
  /// instant `spawnFirst` runs, independent of whether that worker ever
  /// reports Ready.
  [<RequireQualifiedAccess>]
  type SwitchOp =
    | Create of pid: int * workflowIdx: int
    | RequestSwitch of targetIdx: int * newPid: int
    | Ready of pid: int
    | SpawnFailed of pid: int
    | Exited of pid: int * exitCode: int
    | Stop

  /// A fully-specified, replayable scenario.
  type Scenario = { Seed: int; Ops: SwitchOp list }

  // ── Fixed, deterministic workflow pool — the real three-case DU ─────────
  let workflows : WorkflowTypes.SessionWorkflow[] =
    [| WorkflowTypes.SessionWorkflow.Interactive
       WorkflowTypes.SessionWorkflow.LiveTesting
       WorkflowTypes.SessionWorkflow.HotReload WorkflowTypes.BrowserRefreshConfig.defaults |]

  let workflowLabel (idx: int) : string =
    if idx < 0 then "<none>" else WorkflowTypes.SessionWorkflow.label workflows.[idx]

  /// The session's status, modeled as a DU so illegal combinations are
  /// unrepresentable (no bool "isSwapping" flag) — mirrors
  /// `WorkerLifecycleSim.SimStatus` exactly, since it is the same underlying
  /// worker-process lifecycle `SwitchWorkflow` rides on top of.
  [<RequireQualifiedAccess>]
  type Status =
    | Empty
    | Ready of pid: int
    | Swapping of registeredPid: int
    | Stopped
    | Faulted of reason: string

  /// The single `PendingSwap` slot — mirrors `ManagerState.PendingSwap :
  /// Map<SessionId, ManagedSession>`, keyed by this one session, so a SECOND
  /// accepted switch overwrites it exactly as `Map.add` does in production.
  type Pending = { OldPid: int; OldWorkflowIdx: int }

  type SwitchState =
    { Status: Status
      /// Mirrors `session.Workflow` — set the INSTANT a switch is accepted
      /// (spawnFirst/its twin runs), never gated on the replacement's Ready.
      /// Meaningless while Status = Empty (no session created yet).
      WorkflowIdx: int
      PendingSwap: Pending option }

  let initial : SwitchState = { Status = Status.Empty; WorkflowIdx = -1; PendingSwap = None }

  let private currentPid (s: SwitchState) : int option =
    match s.Status with
    | Status.Ready p | Status.Swapping p -> Some p
    | Status.Empty | Status.Stopped | Status.Faulted _ -> None

  let private pendingPid (s: SwitchState) : int option =
    s.PendingSwap |> Option.map (fun p -> p.OldPid)

  /// Liveness classification for the no-dead-window invariant — a DU, not a
  /// bool, so "live but mid-swap" and "gone" can never be confused.
  [<RequireQualifiedAccess>]
  type Liveness =
    | Live
    | NotLive

  let liveness (s: Status) : Liveness =
    match s with
    | Status.Ready _ | Status.Swapping _ -> Liveness.Live
    | Status.Empty | Status.Stopped | Status.Faulted _ -> Liveness.NotLive

  /// A switch-acceptance strategy: given the state and an accepted (target
  /// <> current-workflow) switch request, what happens to the OLD worker
  /// right now? This is the ONLY seam the twin differs on.
  type Strategy = SwitchState -> int -> int -> SwitchState

  /// THE REAL SHAPE: spawn-first. Park the old (still-registered, still-live)
  /// worker; the session stays addressable (`Swapping`, still Live) for the
  /// whole warmup. Mirrors `spawnFirst` (`SessionManager.fs:703-756`).
  let spawnFirst : Strategy =
    fun before target _newPid ->
      match before.Status with
      | Status.Ready oldPid | Status.Swapping oldPid ->
        { before with
            Status = Status.Swapping oldPid
            WorkflowIdx = target
            PendingSwap = Some { OldPid = oldPid; OldWorkflowIdx = before.WorkflowIdx } }
      | Status.Empty | Status.Stopped | Status.Faulted _ -> before

  /// THE TWIN: stop-before-spawn. Stops the old worker SYNCHRONOUSLY before
  /// the replacement exists — the working directory has zero live sessions
  /// for the whole warmup window. This is the historical/plausible shape
  /// AGENTS.md's "Spawn-first restart — no dead window" doctrine was written
  /// to rule out.
  let stopBeforeSpawn : Strategy =
    fun before target _newPid ->
      match before.Status with
      | Status.Ready oldPid | Status.Swapping oldPid ->
        { before with
            Status = Status.Stopped
            WorkflowIdx = target
            PendingSwap = Some { OldPid = oldPid; OldWorkflowIdx = before.WorkflowIdx } }
      | Status.Empty | Status.Stopped | Status.Faulted _ -> before

  /// One folded step: the op and the state before/after it.
  type Step =
    { Index: int
      Op: SwitchOp
      Before: SwitchState
      After: SwitchState }

  type Trace =
    { Scenario: Scenario
      Steps: Step list
      Final: SwitchState
      /// Which reducer produced this trace (real spawn-first vs the
      /// stop-before-spawn twin).
      Reducer: string }

  /// The pure fold step, parameterized by acceptance strategy. Event
  /// handling (Ready/SpawnFailed/Exited) is IDENTICAL for both reducers —
  /// routed through the REAL `WorkerEventGuard.classify*` — the twin differs
  /// ONLY in how an accepted RequestSwitch treats the outgoing worker.
  let private step (strategy: Strategy) (before: SwitchState) (op: SwitchOp) : SwitchState =
    let cur = currentPid before
    let pend = pendingPid before
    match before.Status, op with
    | Status.Empty, SwitchOp.Create(pid, wf) ->
      { before with Status = Status.Ready pid; WorkflowIdx = wf }
    | Status.Empty, _ -> before
    | Status.Stopped, _ -> before
    | Status.Faulted _, _ -> before
    | (Status.Ready _ | Status.Swapping _), SwitchOp.Create _ ->
      before // a session already exists — Create is a no-op, mirrors WorkerLifecycleSim
    | (Status.Ready _ | Status.Swapping _), SwitchOp.RequestSwitch(target, _) when target = before.WorkflowIdx ->
      // AlreadyActive — "Session is already in %A mode" (SessionManager.fs
      // :1621) — zero side effects, no worker touched.
      before
    | (Status.Ready _ | Status.Swapping _), SwitchOp.RequestSwitch(target, newPid) ->
      strategy before target newPid
    | (Status.Ready _ | Status.Swapping _), SwitchOp.Ready ev ->
      match WorkerEventGuard.classifyReady cur pend ev with
      | WorkerEventGuard.ReadyDecision.IgnoreStale -> before
      | WorkerEventGuard.ReadyDecision.Commit ->
        { before with Status = Status.Ready ev; PendingSwap = None }
    | (Status.Ready _ | Status.Swapping _), SwitchOp.SpawnFailed ev ->
      match WorkerEventGuard.classifySpawnFailed cur pend ev with
      | WorkerEventGuard.SpawnFailedDecision.IgnoreStale -> before
      | WorkerEventGuard.SpawnFailedDecision.RevertSwap ->
        match before.PendingSwap with
        | Some p -> { before with Status = Status.Ready p.OldPid; WorkflowIdx = p.OldWorkflowIdx; PendingSwap = None }
        | None -> before // unreachable: RevertSwap only returned when a swap is pending
      | WorkerEventGuard.SpawnFailedDecision.Fault ->
        { before with Status = Status.Faulted "worker spawn failed"; PendingSwap = None }
    | (Status.Ready _ | Status.Swapping _), SwitchOp.Exited(ev, exitCode) ->
      match WorkerEventGuard.classifyExited cur pend ev with
      | WorkerEventGuard.ExitDecision.IgnoreRetired
      | WorkerEventGuard.ExitDecision.IgnoreStale -> before
      | WorkerEventGuard.ExitDecision.Apply ->
        if exitCode = 0 then { before with Status = Status.Stopped; PendingSwap = None }
        else { before with Status = Status.Faulted (sprintf "worker crashed with exit code %d" exitCode); PendingSwap = None }
    | (Status.Ready _ | Status.Swapping _), SwitchOp.Stop ->
      { before with Status = Status.Stopped; PendingSwap = None }

  let private runWith (name: string) (strategy: Strategy) (scenario: Scenario) : Trace =
    let folder (state, steps) (idx, op) =
      let after = step strategy state op
      let st = { Index = idx; Op = op; Before = state; After = after }
      (after, st :: steps)
    let (final, revSteps) =
      scenario.Ops
      |> List.mapi (fun i o -> (i, o))
      |> List.fold folder (initial, [])
    { Scenario = scenario; Steps = List.rev revSteps; Final = final; Reducer = name }

  /// Run through the real spawn-first acceptance strategy (folding the REAL
  /// `WorkerEventGuard` for every event) — the subject under test.
  let run (scenario: Scenario) : Trace = runWith "real-spawn-first" spawnFirst scenario

  /// Run through the stop-before-spawn twin — used to prove the no-dead-window
  /// invariant has teeth (it must FAIL here).
  let runStopBeforeSpawn (scenario: Scenario) : Trace = runWith "twin-stop-before-spawn" stopBeforeSpawn scenario
