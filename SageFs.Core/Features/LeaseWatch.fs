namespace SageFs.Features

open System
open SageFs

/// The one live `ExpensiveWorkLease.PoolState` for this daemon run — same
/// split as `HealthWatch`/`GcDumpWatch`/`MemoryPressureWatch`: the pure
/// pool logic lives in `ExpensiveWorkLease`, this is the stateful shell a
/// caller (the HTTP lease endpoints, the MCP lease tools, and the daemon's
/// own internal expensive-work call sites) reads and writes through.
///
/// `MemoryPressureWatch.currentLevel()` — the SAME persisted pressure
/// `MemorySupervisor`'s session shedding reads — is what every `request`
/// call here is admitted against, so the pool and the shedding policy can
/// never disagree about how bad things are right now.
module LeaseWatch =

  let mutable private pool = ExpensiveWorkLease.empty
  let private gate = obj ()

  /// Ask for a lease. `holder` identifies the AGENT (connection, agent name,
  /// working directory), not a fresh id per call — see
  /// `ExpensiveWorkLease.request`'s own doc comment for why.
  let request (holder: ExpensiveWorkLease.Holder) (kind: ExpensiveWorkLease.Kind) : ExpensiveWorkLease.Decision =
    lock gate (fun () ->
      let pressure = MemoryPressureWatch.currentLevel ()
      let pool', decision = ExpensiveWorkLease.request DateTimeOffset.UtcNow pressure pool holder kind
      pool <- pool'
      decision)

  /// Give up a queued ask the caller will not repeat, so it does not sit at the head of the line as a ghost. See
  /// `ExpensiveWorkLease.withdraw`.
  let withdraw (holder: ExpensiveWorkLease.Holder) (kind: ExpensiveWorkLease.Kind) : unit =
    lock gate (fun () -> pool <- ExpensiveWorkLease.withdraw holder kind pool)

  /// Release a held lease. Returns whether it was actually still live —
  /// see `ExpensiveWorkLease.ReleaseOutcome`'s own doc comment for why a
  /// caller should pay attention to `AlreadyGone`.
  let release (leaseId: ExpensiveWorkLease.LeaseId) : ExpensiveWorkLease.ReleaseOutcome =
    lock gate (fun () ->
      let pool', outcome = ExpensiveWorkLease.release leaseId pool
      pool <- pool'
      outcome)

  /// Release a lease only if it belongs to `connection`.
  let releaseOwned (connection: string) (leaseId: ExpensiveWorkLease.LeaseId) : ExpensiveWorkLease.ReleaseOutcome =
    lock gate (fun () ->
      let pool', outcome = ExpensiveWorkLease.releaseOwned connection leaseId pool
      pool <- pool'
      outcome)

  /// For observability — `get_fsi_status`/a health payload's own view.
  let snapshot () = lock gate (fun () -> ExpensiveWorkLease.snapshot DateTimeOffset.UtcNow pool)

  /// For tests, and for a daemon that wants to start clean.
  let reset () : unit = lock gate (fun () -> pool <- ExpensiveWorkLease.empty)
