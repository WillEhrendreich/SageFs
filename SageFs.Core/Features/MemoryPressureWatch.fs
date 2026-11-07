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

  /// The daemon's own footprint and the machine's total memory, read where the level is judged. A test swaps it
  /// for a fixed reading. The default reads `/proc/self/smaps_rollup` and the GC, a few microseconds.
  let mutable footprintProbe : unit -> MachineMemory.Footprint * int64 =
    fun () -> MachineMemory.ownFootprint (), (MachineMemory.current ()).TotalBytes

  /// What the footprint alone says right now, and why.
  let footprintFloor () : MemoryPressure * string =
    let footprint, total = footprintProbe ()
    MemoryPressure.ofFootprint MemoryPressure.defaultFootprintThresholds footprint total,
    MemoryPressure.describeFootprint footprint total

  /// Evaluate the policy against the given machine/session snapshot,
  /// persist the level it produces, and return the full `Decision`. Called
  /// from the admission check (before creating a session) and from the
  /// periodic sweep (to shed idle sessions even when nothing is trying to
  /// create a new one).
  let evaluate (t: Thresholds) (m: MachineStats) (sessions: SessionSnapshot list) : Decision =
    lock gate (fun () ->
      let floor, why = footprintFloor ()
      let decision = stepWithFloor t floor why level m sessions
      level <- decision.Level
      decision)

  /// The level every status surface reads: the last judged level, and never better than the daemon's footprint
  /// says right now, so a daemon that holds 20 GB in swap is not "normal" between two sweeps.
  let currentLevel () : MemoryPressure =
    let floor, _ = footprintFloor ()
    lock gate (fun () -> MemoryPressure.worseOf level floor)

  /// Why the level is what it is, from the footprint: empty when the footprint says nothing.
  let currentNote () : string = footprintFloor () |> snd

  /// For tests, and for a daemon that wants to start clean.
  let reset () : unit = lock gate (fun () -> level <- MemoryPressure.Normal)
