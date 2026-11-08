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

  /// One look at the daemon and the machine: the footprint and the machine's memory, read together. The footprint
  /// reads `/proc/self/smaps_rollup`, which walks every mapping of the process: on a 25 GB daemon that is a stall
  /// worth paying once for a sample, so everything a sample says is judged from one reading.
  type Reading =
    { Footprint: MachineMemory.Footprint
      Machine: MachineMemory.Stats }

  /// Where a reading comes from. The daemon passes `live`; a test passes a fixed reading and counts the calls.
  type FootprintSource = unit -> Reading

  let live : FootprintSource =
    fun () -> { Footprint = MachineMemory.ownFootprint (); Machine = MachineMemory.current () }

  /// What one reading says: the reading itself (a caller that also needs the footprint takes it from here, not
  /// from a second read), the level, and why the level is what it is.
  type Observation =
    { Reading: Reading
      Level: MemoryPressure
      /// Empty when the footprint says nothing.
      Note: string }

  /// The machine facts the shedding policy judges, from one reading: what the daemon holds, and what the machine has.
  let machineStatsOf (reading: Reading) : MachineStats =
    { DaemonRssBytes = MachineMemory.heldBytes reading.Footprint
      MachineAvailableBytes = reading.Machine.AvailableBytes
      MachineTotalBytes = reading.Machine.TotalBytes }

  /// What the footprint alone says about a reading, and why.
  let footprintFloor (reading: Reading) : MemoryPressure * string =
    MemoryPressure.ofFootprint MemoryPressure.defaultFootprintThresholds reading.Footprint reading.Machine.TotalBytes,
    MemoryPressure.describeFootprint reading.Footprint reading.Machine.TotalBytes

  /// Evaluate the policy against the given reading and session snapshot,
  /// persist the level it produces, and return the full `Decision`. Called
  /// from the admission check (before creating a session) and from the
  /// periodic sweep (to shed idle sessions even when nothing is trying to
  /// create a new one). The caller read the footprint once and takes the daemon's
  /// own size from the same reading.
  let evaluate (reading: Reading) (t: Thresholds) (m: MachineStats) (sessions: SessionSnapshot list) : Decision =
    lock gate (fun () ->
      let floor, why = footprintFloor reading
      let decision = stepWithFloor t floor why level m sessions
      level <- decision.Level
      decision)

  /// One sample: the footprint is read once from `source`, and the level and the note both come from that read.
  /// The level is the last judged level, and never better than the daemon's footprint says right now, so a daemon
  /// that holds 20 GB in swap is not "normal" between two sweeps.
  let observe (source: FootprintSource) : Observation =
    let reading = source ()
    let floor, note = footprintFloor reading
    { Reading = reading
      Level = lock gate (fun () -> MemoryPressure.worseOf level floor)
      Note = note }

  /// The note for a level a caller already holds (a health snapshot's). A level is never better than the footprint's
  /// own reading, so at Normal the footprint says nothing and there is nothing to read; at any other level the
  /// footprint is read once for the note.
  let noteFor (level: MemoryPressure) : string =
    match level with
    | MemoryPressure.Normal -> ""
    | MemoryPressure.Tight
    | MemoryPressure.Critical -> (observe live).Note

  /// For tests, and for a daemon that wants to start clean.
  let reset () : unit = lock gate (fun () -> level <- MemoryPressure.Normal)
