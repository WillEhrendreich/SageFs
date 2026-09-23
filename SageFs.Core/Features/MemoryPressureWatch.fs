namespace SageFs.Features

open SageFs
open SageFs.MemorySupervisor

/// The one place that remembers the daemon's current `MemoryPressure` across
/// calls — `MemorySupervisor.step` itself is pure and takes the level as an
/// explicit parameter; this is the stateful wrapper around it, the same
/// split `HealthWatch` is over `HealthAnomaly` and `GcDumpWatch` is over
/// `GcDumpCapture`. Without persisting the level between calls, the
/// hysteresis in `nextLevel` (the whole point: no flapping at a boundary)
/// would never actually apply — each call would start back at `Normal`
/// instead of remembering it was already `SheddingIdle`.
module MemoryPressureWatch =

  let mutable private level = MemoryPressure.Normal
  let private gate = obj ()

  /// Evaluate the policy against the given machine/session snapshot,
  /// persist the level it produces, and return the full `Decision`. Called
  /// from the admission check (before creating a session) and from the
  /// periodic sweep (to shed idle sessions even when nothing is trying to
  /// create a new one).
  let evaluate (t: Thresholds) (m: MachineStats) (sessions: SessionSnapshot list) : Decision =
    lock gate (fun () ->
      let decision = step t level m sessions
      level <- decision.Level
      decision)

  let currentLevel () : MemoryPressure = lock gate (fun () -> level)

  /// For tests, and for a daemon that wants to start clean.
  let reset () : unit = lock gate (fun () -> level <- MemoryPressure.Normal)
