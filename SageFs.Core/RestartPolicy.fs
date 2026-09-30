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
    /// Circuit breaker: a crash within this window of the previous restart is
    /// a STARTUP crash (host failing to come up). Startup crashes back off 4x
    /// and give up at a lower ceiling — a host that fails to start is almost
    /// always a permanent config error, so fail fast rather than burn cycles.
    StartupCrashWindow: TimeSpan
    /// Ceiling for startup-crash loops (lower than MaxRestarts).
    StartupCrashMaxRestarts: int
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
    StartupCrashWindow = TimeSpan.FromSeconds 10.0
    StartupCrashMaxRestarts = 3
  }

  /// Calculate the backoff delay for a given restart count.
  /// Exponential: base * 2^(count-1), capped at max.
  let nextBackoff (policy: Policy) (restartCount: int) : TimeSpan =
    match restartCount <= 0 with
    | true -> policy.BackoffBase
    | false ->
      let exponent = min restartCount 20
      let multiplier = Math.Pow(2.0, float (exponent - 1))
      let delay = policy.BackoffBase.TotalMilliseconds * multiplier
      let capped = min delay policy.BackoffMax.TotalMilliseconds
      TimeSpan.FromMilliseconds(capped)

  /// The seed a jittered delay is drawn from. A single-case union, so a seed is never mistaken for a
  /// count, a pid or a delay. The caller supplies it (a hash of the session id and the crash time is
  /// enough), which keeps the policy pure: the same seed always gives the same delay.
  [<Struct>]
  type JitterSeed = JitterSeed of int64

  /// How far jitter may move a delay, as a fraction of it, in either direction: at most +/-20%.
  [<Literal>]
  let MaxJitterFraction = 0.2

  /// How many bits the sample keeps. A double holds exactly 53 bits of mantissa, so a 53 bit integer
  /// over 2^53 is uniform in [0, 1) and never reaches 1.
  [<Literal>]
  let private SampleBits = 53

  /// A number in [0, 1) that is a pure function of the seed. SplitMix64's finalizer, so seeds that
  /// differ by one (session ids in a row) land far apart rather than next to each other.
  let jitterSample (JitterSeed seed) : float =
    let step1 = uint64 seed + 0x9E3779B97F4A7C15UL
    let step2 = (step1 ^^^ (step1 >>> 30)) * 0xBF58476D1CE4E5B9UL
    let step3 = (step2 ^^^ (step2 >>> 27)) * 0x94D049BB133111EBUL
    let mixed = step3 ^^^ (step3 >>> 31)
    float (mixed >>> (64 - SampleBits)) / float (1UL <<< SampleBits)

  /// Spread a delay so sessions whose workers died together do not all retry at the same instant.
  /// The result is within +/-MaxJitterFraction of the delay, never below zero and never above the
  /// policy cap. Pure: the same seed and delay always give the same result.
  let withJitter (policy: Policy) (seed: JitterSeed) (delay: TimeSpan) : TimeSpan =
    let swing = (2.0 * jitterSample seed - 1.0) * MaxJitterFraction
    let jitteredMs = delay.TotalMilliseconds * (1.0 + swing)
    let boundedMs = max 0.0 (min jitteredMs policy.BackoffMax.TotalMilliseconds)
    TimeSpan.FromMilliseconds boundedMs

  /// Should we restart? Pure function: policy + state + current time → decision + new state.
  ///
  /// Rules:
  /// 1. If the reset window has expired since the first restart, counts reset to zero.
  /// 2. Circuit breaker: if the previous crash was a STARTUP crash (within
  ///    StartupCrashWindow of the prior restart), the ceiling drops to
  ///    StartupCrashMaxRestarts and the backoff multiplies by 4x.
  /// 3. If restart count has reached the (effective) max, give up.
  /// 4. Otherwise, restart with exponential backoff delay.
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

    // Circuit breaker: is this a startup-crash loop? The previous restart was
    // recent (within StartupCrashWindow of now) — the host keeps dying right
    // after starting.
    let isStartupCrash =
      match effectiveState.LastRestartAt with
      | Some last when (now - last) <= policy.StartupCrashWindow -> true
      | _ -> false

    let maxRestarts =
      match isStartupCrash with
      | true -> min policy.StartupCrashMaxRestarts policy.MaxRestarts
      | false -> policy.MaxRestarts

    match effectiveState.RestartCount >= maxRestarts with
    | true ->
      let error =
        SageFsError.RestartLimitExceeded(
          effectiveState.RestartCount,
          policy.ResetWindow.TotalMinutes)
      Decision.GiveUp error, effectiveState
    | false ->
      let newCount = effectiveState.RestartCount + 1
      // Startup crashes back off at a fixed 4x base (circuit breaker): the
      // second startup-crash backoff is 4x the first (1s -> 4s), not 2x.
      let delay =
        match isStartupCrash with
        | true ->
          let multiplied = policy.BackoffBase.TotalMilliseconds * 4.0
          TimeSpan.FromMilliseconds(min multiplied policy.BackoffMax.TotalMilliseconds)
        | false -> nextBackoff policy newCount
      let newState = {
        RestartCount = newCount
        LastRestartAt = Some now
        WindowStart =
          match effectiveState.WindowStart with
          | None -> Some now
          | Some _ as ws -> ws
      }
      Decision.Restart delay, newState

  /// `decide`, with the restart delay spread by `withJitter`. The next state and the give-up decision
  /// are exactly what `decide` returns: jitter only moves the delay, never the count or the window.
  let decideWithJitter
    (policy: Policy)
    (seed: JitterSeed)
    (state: State)
    (now: DateTime)
    : Decision * State =
    let decision, newState = decide policy state now
    match decision with
    | Decision.Restart delay -> Decision.Restart(withJitter policy seed delay), newState
    | Decision.GiveUp _ -> decision, newState
