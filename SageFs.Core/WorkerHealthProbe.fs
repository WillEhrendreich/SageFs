module SageFs.WorkerHealthProbe

open SageFs.WorkerProtocol

/// Detects a worker whose OS PROCESS stays alive but stops answering the
/// daemon's own health checks — the exact gap `proc.Exited` and the warmup
/// poll are structurally blind to (roast-6 #3: `WorkerLivenessMonitor.fs` was
/// deleted and nothing replaced it). Pure decision lives here; the injected
/// `probe` (a real HTTP/RPC round-trip) and the loop's restart side effect
/// live at the caller — the same split `SageFs.OwnerMonitor` uses for
/// parent-death detection (`isAlive` is pure, `getProcessById`/`run`'s
/// `cts.Cancel()` are the injected/side-effecting edges).

/// The outcome of a single health probe. Fail-closed: a probe that could not
/// be completed — timeout, transport error, any exception — MUST be reported
/// as `Missed`. It must never be reported `Healthy` just because nothing
/// happened to throw where the caller looked; a monitor bug can never mask a
/// hung worker as healthy.
[<RequireQualifiedAccess>]
type ProbeOutcome =
  /// The worker answered, with nothing usable to say about its status.
  | Healthy
  /// The worker answered, and this is the status it reported about itself.
  | Reported of SessionStatus
  | Missed

/// Decision after folding one probe outcome into the current consecutive-miss
/// streak.
[<RequireQualifiedAccess>]
type Decision =
  /// Keep monitoring; carries the new consecutive-miss count (always 0 right
  /// after a Healthy probe).
  | Continue of missCount: int
  /// The miss streak reached the threshold — restart the worker.
  | Restart

/// Consecutive missed health checks before a worker is considered hung and
/// restarted. Mirrors `RestartPolicy.defaultPolicy`'s spirit of "a few
/// transient blips are normal, three in a row is not."
let defaultThreshold = 3

/// Interval between health probes of an established (Ready) worker.
let defaultProbeIntervalMs = int Timeouts.workerHealthProbeInterval.TotalMilliseconds

/// Default per-probe timeout: how long a single health round-trip is given
/// to answer before it counts as `Missed`.
let defaultProbeTimeoutMs = int Timeouts.workerHealthProbeTimeout.TotalMilliseconds

/// Pure decision: threshold + current miss streak + this probe's outcome ->
/// what to do next.
///   - `Healthy` always resets the streak to zero, however high it was.
///   - `Missed` increments the streak; reaching (or, defensively, exceeding)
///     the threshold restarts.
let decide (threshold: int) (missCount: int) (outcome: ProbeOutcome) : Decision =
  match outcome with
  | ProbeOutcome.Healthy
  | ProbeOutcome.Reported _ -> Decision.Continue 0
  | ProbeOutcome.Missed ->
    let next = missCount + 1
    match next >= threshold with
    | true -> Decision.Restart
    | false -> Decision.Continue next

/// What the daemon's registry should do with a status a worker just reported on a probe.
[<RequireQualifiedAccess>]
type RegistryUpdate =
  | NoChange
  | Replace of SessionLifecycleStatus

/// The probe is not a second status writer: it only carries one fact the registry has no other way to learn on
/// its own, that the worker's FSI host died while the worker stayed up (the worker process never exits, so no
/// `WorkerExited` comes), and the host coming back after a reset. Everything else a worker reports is left to the
/// paths that already own it. A Faulted or Stopped session is terminal and is never touched.
let registryUpdate (current: SessionLifecycleStatus) (reported: SessionStatus) : RegistryUpdate =
  match current, reported with
  | SessionLifecycleStatus.HostCrashed(_, known), SessionStatus.HostCrashed seen when known = seen -> RegistryUpdate.NoChange
  | (SessionLifecycleStatus.Ready _ | SessionLifecycleStatus.Evaluating _ | SessionLifecycleStatus.HostCrashed _), SessionStatus.HostCrashed _
  | SessionLifecycleStatus.HostCrashed _, (SessionStatus.Ready | SessionStatus.Evaluating) ->
    RegistryUpdate.Replace(SessionLifecycleStatus.ofWorkerReport current reported)
  | _ -> RegistryUpdate.NoChange

/// The probe's `onReported`: look the registry's status up when a report arrives (not before, it moves) and post
/// the replacement when `registryUpdate` says there is one. A session that is gone has nothing to update.
let syncRegistry
  (currentStatus: unit -> SessionLifecycleStatus option)
  (replace: SessionLifecycleStatus -> unit)
  (reported: SessionStatus)
  : unit =
  match currentStatus () with
  | None -> ()
  | Some current ->
    match registryUpdate current reported with
    | RegistryUpdate.Replace status -> replace status
    | RegistryUpdate.NoChange -> ()

/// Run the probe loop until either the miss threshold is reached
/// (`onRestart` fires exactly once and the loop exits) or `shouldContinue`
/// says to stop (the worker this loop watches was replaced, restarted, or
/// stopped by some other path — e.g. a hard reset — so this loop's job is
/// already done). `probe` is the injected side effect and MUST fail closed:
/// any exception it raises is caught here and folded in as `Missed`, so a
/// probe implementation bug can never mask a hung worker as healthy.
let run
  (probe: unit -> Async<ProbeOutcome>)
  (threshold: int)
  (intervalMs: int)
  (shouldContinue: unit -> bool)
  (onReported: SessionStatus -> unit)
  (onRestart: unit -> unit)
  : Async<unit> =
  async {
    let mutable missCount = 0
    let mutable stop = false
    while not stop && shouldContinue () do
      do! Async.Sleep intervalMs
      match shouldContinue () with
      | false -> stop <- true
      | true ->
        let! outcome =
          async {
            try
              return! probe ()
            with _ ->
              return ProbeOutcome.Missed
          }
        match outcome with
        | ProbeOutcome.Reported status -> onReported status
        | ProbeOutcome.Healthy
        | ProbeOutcome.Missed -> ()
        match decide threshold missCount outcome with
        | Decision.Continue n -> missCount <- n
        | Decision.Restart ->
          stop <- true
          onRestart ()
  }
