namespace SageFs

open System

/// Pure, deterministic restart policy for Erlang-style supervision.
/// Used by SessionManager for worker auto-restart and
/// by Supervisor for daemon-level watchdog.
/// No IO, no side effects — just decisions based on state.
module RestartPolicy =

  /// Configuration for restart behavior.
  type Policy = {
    /// Maximum number of restarts before giving up.
    MaxRestarts: int
    /// Base delay for exponential backoff (e.g. 1 second).
    BackoffBase: TimeSpan
    /// Maximum delay cap (e.g. 30 seconds).
    BackoffMax: TimeSpan
    /// Window for counting restarts. Restarts older than this are forgotten.
    /// Prevents permanent give-up after spaced-out transient failures.
    ResetWindow: TimeSpan
  }

  /// Per-session restart tracking state.
  type State = {
    /// How many restarts have occurred within the reset window.
    RestartCount: int
    /// When the last restart happened.
    LastRestartAt: DateTime option
    /// When the window started (first restart in current window).
    WindowStart: DateTime option
  }

  /// Decision from the restart policy.
  [<RequireQualifiedAccess>]
  type Decision =
    /// Restart the worker after waiting the specified delay.
    | Restart of delay: TimeSpan
    /// Give up — too many restarts in the window.
    | GiveUp of SageFsError

  let emptyState : State = {
    RestartCount = 0
    LastRestartAt = None
    WindowStart = None
  }

  let defaultPolicy : Policy = {
    MaxRestarts = 5
    BackoffBase = TimeSpan.FromSeconds 1.0
    BackoffMax = TimeSpan.FromSeconds 30.0
    ResetWindow = TimeSpan.FromMinutes 5.0
  }

  /// Calculate the backoff delay for a given restart count.
  /// Exponential: base * 2^(count-1), capped at max.
  let nextBackoff (policy: Policy) (restartCount: int) : TimeSpan =
    if restartCount <= 0 then policy.BackoffBase
    else
      let exponent = min restartCount 20
      let multiplier = Math.Pow(2.0, float (exponent - 1))
      let delay = policy.BackoffBase.TotalMilliseconds * multiplier
      let capped = min delay policy.BackoffMax.TotalMilliseconds
      TimeSpan.FromMilliseconds(capped)

  /// Should we restart? Pure function: policy + state + current time → decision + new state.
  ///
  /// Rules:
  /// 1. If the reset window has expired since the first restart, counts reset to zero.
  /// 2. If restart count has reached max, give up.
  /// 3. Otherwise, restart with exponential backoff delay.
  let decide
    (policy: Policy)
    (state: State)
    (now: DateTime)
    : Decision * State =
    // Check if the restart window has expired — reset count if so
    let effectiveState =
      match state.WindowStart with
      | Some start when (now - start) > policy.ResetWindow ->
        emptyState
      | _ -> state

    if effectiveState.RestartCount >= policy.MaxRestarts then
      let error =
        SageFsError.RestartLimitExceeded(
          effectiveState.RestartCount,
          policy.ResetWindow.TotalMinutes)
      Decision.GiveUp error, effectiveState
    else
      let newCount = effectiveState.RestartCount + 1
      let delay = nextBackoff policy newCount
      let newState = {
        RestartCount = newCount
        LastRestartAt = Some now
        WindowStart =
          match effectiveState.WindowStart with
          | None -> Some now
          | Some _ as ws -> ws
      }
      Decision.Restart delay, newState
