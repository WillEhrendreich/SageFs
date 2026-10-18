namespace SageFs

/// Whether a worker that failed to shut down gracefully still needs killing. Split out of
/// SessionManager.fs, which is at its line budget, and kept pure: the process handle comes
/// in as a probe, so a test needs no real worker.
module WorkerStop =

  type ForceKillDecision =
    /// The worker may still be running, so kill it.
    | ForceKill
    /// The worker is confirmed gone, so there is nothing to kill.
    | AlreadyExited

  /// `hasExited` asks the process handle and may throw (a disposed or never-started handle).
  /// An exception means we cannot tell, and a leaked worker costs more than a kill on a dead
  /// one, so it fails closed to `ForceKill`.
  let needsForceKill (hasExited: unit -> bool) : ForceKillDecision =
    try (if hasExited () then AlreadyExited else ForceKill) with _ -> ForceKill
