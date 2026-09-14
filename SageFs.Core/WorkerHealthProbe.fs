module SageFs.WorkerHealthProbe

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
  | Healthy
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
let defaultProbeIntervalMs = 5_000

/// Default per-probe timeout: how long a single health round-trip is given
/// to answer before it counts as `Missed`.
let defaultProbeTimeoutMs = 3_000

/// Pure decision: threshold + current miss streak + this probe's outcome ->
/// what to do next.
///   - `Healthy` always resets the streak to zero, however high it was.
///   - `Missed` increments the streak; reaching (or, defensively, exceeding)
///     the threshold restarts.
let decide (threshold: int) (missCount: int) (outcome: ProbeOutcome) : Decision =
  match outcome with
  | ProbeOutcome.Healthy -> Decision.Continue 0
  | ProbeOutcome.Missed ->
    let next = missCount + 1
    match next >= threshold with
    | true -> Decision.Restart
    | false -> Decision.Continue next

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
        match decide threshold missCount outcome with
        | Decision.Continue n -> missCount <- n
        | Decision.Restart ->
          stop <- true
          onRestart ()
  }
