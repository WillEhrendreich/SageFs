namespace SageFs.Features

open System
open System.IO

/// The machine's own memory, not just this process's. Two real incidents
/// (`HealthWatch.fs`'s header) had the daemon's RSS climb to 51.7GB and then
/// 55GB of a 62GB box while every session read Ready and `/health` read
/// Healthy — the daemon had no notion of "how much room is actually left on
/// this box," only its own working set. This module is that missing number.
module MachineMemory =

  type Stats = {
    /// The box's total physical memory (or container/cgroup limit, wherever
    /// the runtime is confined to less than the physical machine).
    TotalBytes: int64
    /// Memory available for new allocations without swapping — the same
    /// number `free -h`'s "available" column shows on Linux. This is what
    /// the shedding policy (`MemorySupervisor`) and the gcdump guard
    /// (`GcDumpCapture.hasHeadroomToCapture`) actually care about: not "how
    /// much RAM exists" but "how much is left before the machine starts
    /// swapping or the OOM killer starts choosing victims."
    AvailableBytes: int64
  }

  /// Pure parse of `/proc/meminfo`'s lines into (MemTotal, MemAvailable), in
  /// bytes. `MemAvailable` (not `MemFree`) is the kernel's own estimate that
  /// already accounts for reclaimable caches — the number an operator means
  /// by "how much memory do I have left." Returns `None` if either field is
  /// missing or malformed, so a caller can fall back rather than trust a
  /// zero.
  let parseMeminfo (lines: string[]) : Stats option =
    let find (key: string) =
      lines
      |> Array.tryPick (fun (line: string) ->
        match line.StartsWith(key, StringComparison.Ordinal) with
        | false -> None
        | true ->
          let parts = line.Split([| ' ' |], StringSplitOptions.RemoveEmptyEntries)
          match parts.Length >= 2 with
          | false -> None
          | true ->
            match UInt64.TryParse parts.[1] with
            | true, kb -> Some(int64 kb * 1024L)
            | false, _ -> None)
    match find "MemTotal:", find "MemAvailable:" with
    | Some total, Some available -> Some { TotalBytes = total; AvailableBytes = available }
    | _ -> None

  let private procMeminfoPath = "/proc/meminfo"

  /// Best-effort snapshot of the machine's memory. On Linux, reads
  /// `/proc/meminfo` directly — no new dependency, just a file the kernel
  /// already publishes. Anywhere else (no `/proc/meminfo`: Windows, macOS),
  /// falls back to the .NET GC's own view of the machine/container memory
  /// ceiling as `TotalBytes`, and treats this PROCESS's own working set as
  /// the only claim on it, so `AvailableBytes` is an honest-but-conservative
  /// approximation (other processes on the box are invisible to it) rather
  /// than a number this module pretends is exact. Never throws: any failure
  /// reading or parsing falls through to the GC-based estimate.
  let current () : Stats =
    let fromProc =
      try
        match File.Exists procMeminfoPath with
        | true -> parseMeminfo (File.ReadAllLines procMeminfoPath)
        | false -> None
      with _ -> None
    match fromProc with
    | Some stats -> stats
    | None ->
      let gcInfo = GC.GetGCMemoryInfo()
      let total = gcInfo.TotalAvailableMemoryBytes
      let ownRss = Diagnostics.Process.GetCurrentProcess().WorkingSet64
      { TotalBytes = total; AvailableBytes = max 0L (total - ownRss) }
