namespace SageFs

open System

/// Configuration for worker liveness monitoring.
type LivenessConfig = {
  /// How often to check worker health (default 10s).
  CheckInterval: TimeSpan
  /// Consecutive missed checks before declaring hung (default 3).
  MissThreshold: int
  /// HTTP timeout for each health check (default 2s from Timeouts.healthCheck).
  HealthCheckTimeout: TimeSpan
}

/// Result of a single health check against a worker.
[<RequireQualifiedAccess>]
type HealthCheckResult =
  | Healthy of responseTimeMs: float
  | Unhealthy of reason: string
  | Timeout

/// Accumulated liveness state for one worker.
type LivenessState = {
  /// Number of consecutive non-Healthy results.
  ConsecutiveMisses: int
  /// When the worker last responded healthy.
  LastHealthy: DateTimeOffset option
  /// When we last performed a check.
  LastCheckAt: DateTimeOffset option
}

/// Verdict after evaluating a health check result against liveness state.
[<RequireQualifiedAccess>]
type LivenessVerdict =
  | Alive
  | Hung of missCount: int * lastHealthy: DateTimeOffset option

/// Pure liveness monitor logic — no IO, no timers, no HTTP.
/// All side effects (HTTP calls, process kill, scheduling) happen
/// at the integration boundary in SessionManager.
module WorkerLivenessMonitor =

  let defaultConfig : LivenessConfig = {
    CheckInterval = TimeSpan.FromSeconds 10.0
    MissThreshold = 3
    HealthCheckTimeout = Timeouts.healthCheck
  }

  let initialState : LivenessState = {
    ConsecutiveMisses = 0
    LastHealthy = None
    LastCheckAt = None
  }

  /// Pure function: given a health check result, update state and return verdict.
  let processCheckResult
    (config: LivenessConfig)
    (state: LivenessState)
    (result: HealthCheckResult)
    (now: DateTimeOffset)
    : LivenessState * LivenessVerdict =
    match result with
    | HealthCheckResult.Healthy _ ->
      let newState = {
        ConsecutiveMisses = 0
        LastHealthy = Some now
        LastCheckAt = Some now
      }
      newState, LivenessVerdict.Alive
    | HealthCheckResult.Unhealthy _
    | HealthCheckResult.Timeout ->
      let misses = state.ConsecutiveMisses + 1
      let newState = {
        state with
          ConsecutiveMisses = misses
          LastCheckAt = Some now
      }
      match misses >= config.MissThreshold with
      | true -> newState, LivenessVerdict.Hung(misses, state.LastHealthy)
      | false -> newState, LivenessVerdict.Alive

  /// Pure function: should we perform a health check now based on the interval?
  let shouldCheck
    (config: LivenessConfig)
    (state: LivenessState)
    (now: DateTimeOffset)
    : bool =
    match state.LastCheckAt with
    | None -> true
    | Some last -> (now - last) >= config.CheckInterval
