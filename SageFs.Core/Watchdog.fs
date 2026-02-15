namespace SageFs

open System

/// Pure, deterministic watchdog logic for daemon supervision.
/// Uses RestartPolicy for backoff decisions.
/// No IO, no side effects — just decisions based on state.
module Watchdog =

  /// Configuration for the watchdog supervisor.
  type Config = {
    /// How often to check if the daemon is alive.
    CheckInterval: TimeSpan
    /// Restart policy for backoff/give-up decisions.
    RestartPolicy: RestartPolicy.Policy
    /// Grace period after startup before first health check.
    GracePeriod: TimeSpan
  }

  /// Observed status of the daemon process.
  [<RequireQualifiedAccess>]
  type DaemonStatus =
    | Running
    | NotRunning
    | Unknown

  /// Watchdog tracking state.
  type State = {
    /// Current daemon PID being monitored (None if not started yet).
    DaemonPid: int option
    /// Restart policy state for backoff tracking.
    RestartState: RestartPolicy.State
    /// When the daemon was last started.
    LastStartedAt: DateTime option
    /// When the watchdog itself started.
    WatchdogStartedAt: DateTime
  }

  /// Actions the watchdog can take — pure decisions, no side effects.
  [<RequireQualifiedAccess>]
  type Action =
    | StartDaemon
    | RestartDaemon of delay: TimeSpan
    | Wait
    | GiveUp of reason: string

  let defaultConfig : Config = {
    CheckInterval = TimeSpan.FromSeconds 5.0
    RestartPolicy = RestartPolicy.defaultPolicy
    GracePeriod = TimeSpan.FromSeconds 30.0
  }

  let emptyState (now: DateTime) : State = {
    DaemonPid = None
    RestartState = RestartPolicy.emptyState
    LastStartedAt = None
    WatchdogStartedAt = now
  }

  /// Pure decision: given the daemon status and current time, what should we do?
  let decide
    (config: Config)
    (state: State)
    (daemonStatus: DaemonStatus)
    (now: DateTime)
    : Action * State =
    match daemonStatus with
    | DaemonStatus.Running -> Action.Wait, state
    | DaemonStatus.Unknown -> Action.Wait, state
    | DaemonStatus.NotRunning ->
      match state.DaemonPid with
      | None ->
        Action.StartDaemon, state
      | Some _pid ->
        match state.LastStartedAt with
        | Some startedAt when (now - startedAt) < config.GracePeriod ->
          Action.Wait, state
        | _ ->
          let decision, newRestartState =
            RestartPolicy.decide config.RestartPolicy state.RestartState now
          match decision with
          | RestartPolicy.Decision.Restart delay ->
            Action.RestartDaemon delay,
              { state with RestartState = newRestartState }
          | RestartPolicy.Decision.GiveUp error ->
            Action.GiveUp (SageFsError.describe error),
              { state with RestartState = newRestartState }

  /// Record that a daemon was started.
  let recordStart (pid: int) (now: DateTime) (state: State) : State =
    { state with
        DaemonPid = Some pid
        LastStartedAt = Some now }
