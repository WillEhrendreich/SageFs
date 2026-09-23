namespace SageFs.Simulation

open SageFs.WorkerProtocol

/// Deterministic Simulation Testing (DST) for the registry-vs-worker
/// reconciliation loop `SageFs.Core/WorkerProtocol.fs`'s
/// `SessionLifecycleStatus.ofWorkerReport` performs on every status poll
/// (`get_fsi_status`, Mcp.fs — which writes the reconciled result straight
/// back into the registry).
///
/// THE HISTORICAL BUG (the twin below reproduces its SHAPE): found 2026-09-22
/// in a real run against a Hot Reload session — the daemon had already
/// recorded the session as Faulted (through a channel OTHER than a status
/// poll: WorkerExited, a spawn failure), but a `GetStatus` poll still got
/// back a live-sounding reply ("Starting") from a worker process that had
/// not yet caught up to its own death, or whose reply simply raced the fault
/// signal. `/health` then reported `status: "Starting"` for a session its
/// own registry entry called `Faulted`. The pre-fix `ofWorkerReport` took
/// every worker reply at face value and rebuilt a live status from it — no
/// check that the registry had already decided the session was done — so
/// the ONE function that writes the registry back could resurrect a session
/// the daemon had already, correctly, pronounced dead.
///
/// WHY THE REAL CODE IS THE SUBJECT: `run` folds `SessionLifecycleStatus
/// .ofWorkerReport` directly — no reimplemented candidate stands in for the
/// fix itself. `runNonStickyTwin` is a SEPARATE, deliberately-regressed
/// reducer standing in for the pre-fix shape, so the invariant in
/// `SessionStatusReconciliationInvariants.fs` can be proven to actually
/// discriminate between the fix and the bug it replaced, not just hold
/// vacuously.
module SessionStatusReconciliationSim =

  /// One thing that can happen to a session's registry entry between two
  /// reads of it. `Poll` is the ONLY event `ofWorkerReport` itself ever
  /// sees — `DaemonFault`/`DaemonStop`/`Restarted` model the OTHER, real
  /// code paths (SessionManager.fs) that write the registry directly,
  /// bypassing this function entirely, so the sim's event space matches
  /// what can actually interleave with a poll in production.
  [<RequireQualifiedAccess>]
  type Event =
    /// A `GetStatus` reply came back from the worker. This is the only
    /// event the function under test consumes.
    | Poll of SessionStatus
    /// The daemon learned of a fault through another channel entirely
    /// (WorkerExited, a spawn failure) and recorded it directly.
    | DaemonFault of reason: string option
    /// An explicit stop, recorded directly.
    | DaemonStop
    /// A genuine restart: the registry entry is replaced outright with a
    /// fresh, non-terminal status. Never goes through `ofWorkerReport`.
    | Restarted of WorkerHandle

  type Scenario =
    { Seed: int
      Initial: SessionLifecycleStatus
      Events: Event list }

  type Trace =
    { Scenario: Scenario
      Final: SessionLifecycleStatus
      /// Every intermediate status, in order, INCLUDING the initial one —
      /// what the invariants walk to check "no later event undoes an
      /// earlier terminal decision except an explicit restart."
      History: SessionLifecycleStatus list
      Reducer: string }

  let private applyReal (current: SessionLifecycleStatus) (event: Event) : SessionLifecycleStatus =
    match event with
    | Event.Poll reported -> SessionLifecycleStatus.ofWorkerReport current reported
    | Event.DaemonFault reason -> SessionLifecycleStatus.Faulted reason
    | Event.DaemonStop -> SessionLifecycleStatus.Stopped
    | Event.Restarted handle -> SessionLifecycleStatus.Starting handle

  let private foldHistory (apply: SessionLifecycleStatus -> Event -> SessionLifecycleStatus) (scenario: Scenario) =
    scenario.Events
    |> List.scan apply scenario.Initial

  /// Run through the REAL, fixed reconciliation function.
  let run (scenario: Scenario) : Trace =
    let history = foldHistory applyReal scenario
    { Scenario = scenario
      Final = history |> List.last
      History = history
      Reducer = "real (SessionLifecycleStatus.ofWorkerReport — Faulted/Stopped sticky)" }

  // ---------------------------------------------------------------------
  // TWIN — the pre-fix shape: every worker reply is taken at face value,
  // with no check that the registry had already decided the session was
  // done.
  // ---------------------------------------------------------------------

  let private nonStickyOfWorkerReport (current: SessionLifecycleStatus) (reported: SessionStatus) : SessionLifecycleStatus =
    let handle () : WorkerHandle =
      { Pid = SessionLifecycleStatus.workerPid current |> Option.defaultValue 0
        Port = SessionLifecycleStatus.workerPort current }
    match reported with
    | SessionStatus.Starting -> SessionLifecycleStatus.Starting (handle ())
    | SessionStatus.Ready -> SessionLifecycleStatus.Ready (handle ())
    | SessionStatus.Evaluating -> SessionLifecycleStatus.Evaluating (handle ())
    | SessionStatus.Building reason -> SessionLifecycleStatus.Building (reason, handle ())
    | SessionStatus.Faulted -> SessionLifecycleStatus.Faulted (SessionLifecycleStatus.faultReason current)
    | SessionStatus.Restarting -> SessionLifecycleStatus.Restarting (SessionLifecycleStatus.workerPid current)
    | SessionStatus.Stopped -> SessionLifecycleStatus.Stopped

  let private applyTwin (current: SessionLifecycleStatus) (event: Event) : SessionLifecycleStatus =
    match event with
    | Event.Poll reported -> nonStickyOfWorkerReport current reported
    | Event.DaemonFault reason -> SessionLifecycleStatus.Faulted reason
    | Event.DaemonStop -> SessionLifecycleStatus.Stopped
    | Event.Restarted handle -> SessionLifecycleStatus.Starting handle

  /// Run through the twin: every worker poll reply is authoritative, with
  /// no stickiness check against an already-terminal registry entry.
  let runNonStickyTwin (scenario: Scenario) : Trace =
    let history = foldHistory applyTwin scenario
    { Scenario = scenario
      Final = history |> List.last
      History = history
      Reducer = "twin-non-sticky (pre-fix: every worker reply is authoritative)" }
