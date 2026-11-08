// What an eval cost, and the one line that says so (docs/plans/eval-cost-line.md).
//
// BCL and `Timeouts` only. This file is compiled into SageFs.Core and into the isolated FSI host: the host measures
// the figures around its eval, the wire carries them, and the daemon writes the line, all from this one definition.
//
// An agent trying two versions of a function reads the line and sees which one did less. Allocation volume is a far
// steadier signal than time on a busy machine, so the line carries both. A fast, light eval carries no line, so the
// common reply is no longer.
namespace SageFs

open System
open System.Globalization

/// What one eval cost, measured in the process that ran it.
type EvalCost =
  { /// Wall time around the eval itself, without the queue wait or the wire.
    Elapsed: TimeSpan
    /// Bytes the process allocated while the eval ran.
    Allocated: int64
    /// Collections that ran in each generation while the eval ran.
    Gen0: int
    Gen1: int
    Gen2: int }

/// The two sizes past which an eval earns a line.
type CostThresholds =
  { Slow: TimeSpan
    HeavyAllocation: int64 }

[<RequireQualifiedAccess>]
module EvalCost =

  /// How many bytes make one kilobyte, megabyte and gigabyte in the line (decimal, so "100 MB" is 100,000,000 bytes).
  [<Literal>]
  let BytesPerKilobyte = 1000L

  /// The key an eval's cost travels under in its reply metadata, from the eval actor through the worker to the daemon.
  [<Literal>]
  let MetadataKey = "evalCost"

  /// An eval that allocated this much is annotated even when it was quick: allocation volume is what an agent
  /// comparing two versions of a function wants to see. FsiMcp 0.3.0 uses the same 100 MB.
  [<Literal>]
  let HeavyAllocationBytes = 100L * BytesPerKilobyte * BytesPerKilobyte

  /// The thresholds the product uses.
  let productThresholds : CostThresholds =
    { Slow = Timeouts.evalCostSlow
      HeavyAllocation = HeavyAllocationBytes }

  /// A cost of nothing: what an eval that was not measured carries.
  let unmeasured : EvalCost =
    { Elapsed = TimeSpan.Zero
      Allocated = 0L
      Gen0 = 0
      Gen1 = 0
      Gen2 = 0 }

  /// Two costs added: what a send of several statements cost altogether.
  let sum (a: EvalCost) (b: EvalCost) : EvalCost =
    { Elapsed = a.Elapsed + b.Elapsed
      Allocated = a.Allocated + b.Allocated
      Gen0 = a.Gen0 + b.Gen0
      Gen1 = a.Gen1 + b.Gen1
      Gen2 = a.Gen2 + b.Gen2 }

  /// The cost of an eval from the readings taken before and after it. A reading that went backwards (a counter that
  /// restarted, a clock that was stepped) is a zero, never a negative figure.
  let measured (elapsed: TimeSpan) (allocatedBefore: int64, allocatedAfter: int64) (gen0: int * int) (gen1: int * int) (gen2: int * int) : EvalCost =
    let rise (before: int64) (after: int64) = max 0L (after - before)
    { Elapsed = max TimeSpan.Zero elapsed
      Allocated = rise allocatedBefore allocatedAfter
      Gen0 = int (rise (int64 (fst gen0)) (int64 (snd gen0)))
      Gen1 = int (rise (int64 (fst gen1)) (int64 (snd gen1)))
      Gen2 = int (rise (int64 (fst gen2)) (int64 (snd gen2))) }

  /// The counters of this process, read at one moment. The eval host and the in-process session read them before an eval and
  /// after it, and `between` turns the two readings into a cost.
  ///
  /// Bytes are the whole process's (`GC.GetTotalAllocatedBytes`), so an eval that starts its own tasks or threads is charged
  /// for them. The thread-local counter misses those: 100 MB allocated by a `Parallel.For` read 2 KB there. The price is that a
  /// background thread's allocation during the eval is counted too (12 MB of 72 MB in a measured string-builder loop).
  type Readings =
    { Timestamp: int64
      Allocated: int64
      Gen0: int
      Gen1: int
      Gen2: int }

  /// Read the counters now.
  let take () : Readings =
    { Timestamp = System.Diagnostics.Stopwatch.GetTimestamp()
      Allocated = GC.GetTotalAllocatedBytes false
      Gen0 = GC.CollectionCount 0
      Gen1 = GC.CollectionCount 1
      Gen2 = GC.CollectionCount 2 }

  /// What happened between two readings.
  let between (before: Readings) (after: Readings) : EvalCost =
    measured
      (System.Diagnostics.Stopwatch.GetElapsedTime(before.Timestamp, after.Timestamp))
      (before.Allocated, after.Allocated)
      (before.Gen0, after.Gen0)
      (before.Gen1, after.Gen1)
      (before.Gen2, after.Gen2)

  /// Whether this cost is past either threshold. At the threshold counts as past it.
  let isNotable (thresholds: CostThresholds) (cost: EvalCost) : bool =
    cost.Elapsed >= thresholds.Slow || cost.Allocated >= thresholds.HeavyAllocation

  let inv = CultureInfo.InvariantCulture

  /// The units a byte count is written in.
  type ByteUnit =
    | Bytes
    | Kilobytes
    | Megabytes
    | Gigabytes

  /// The unit's label in the line. The one place that spells it.
  let unitLabel (unit: ByteUnit) : string =
    match unit with
    | Bytes -> "B"
    | Kilobytes -> "KB"
    | Megabytes -> "MB"
    | Gigabytes -> "GB"

  let allUnits =[ Bytes; Kilobytes; Megabytes; Gigabytes ]

  /// How many bytes one of the unit is.
  let bytesIn (unit: ByteUnit) : int64 =
    match unit with
    | Bytes -> 1L
    | Kilobytes -> BytesPerKilobyte
    | Megabytes -> BytesPerKilobyte * BytesPerKilobyte
    | Gigabytes -> BytesPerKilobyte * BytesPerKilobyte * BytesPerKilobyte

  /// The label read back: `None` for a word that is not a unit.
  let tryUnitOfLabel (label: string) : ByteUnit option = allUnits |> List.tryFind (fun u -> unitLabel u = label)

  /// The largest unit the count reaches one of, so the figure has one digit or more before the point.
  let unitFor (bytes: int64) : ByteUnit =
    allUnits |> List.rev |> List.tryFind (fun u -> bytes >= bytesIn u) |> Option.defaultValue Bytes

  /// Bytes as a short figure: `812 B`, `4.2 KB`, `312.4 MB`, `1.5 GB`.
  let describeBytes (bytes: int64) : string =
    let bytes = max 0L bytes
    match unitFor bytes with
    | Bytes -> String.Format(inv, "{0} {1}", bytes, unitLabel Bytes)
    | unit -> String.Format(inv, "{0:F1} {1}", float bytes / float (bytesIn unit), unitLabel unit)

  /// The one line: `[0.84 s · 312.4 MB allocated · GCs gen0 14 / gen1 3 / gen2 1]`. Nothing when the eval was under both thresholds.
  let line (thresholds: CostThresholds) (cost: EvalCost) : string option =
    match isNotable thresholds cost with
    | false -> None
    | true ->
      String.Format(
        inv,
        "[{0:F2} s · {1} allocated · GCs gen0 {2} / gen1 {3} / gen2 {4}]",
        cost.Elapsed.TotalSeconds,
        describeBytes cost.Allocated,
        cost.Gen0,
        cost.Gen1,
        cost.Gen2
      )
      |> Some

  let linePattern =
    System.Text.RegularExpressions.Regex(
      @"^\[(?<s>\d+\.\d{2}) s · (?<n>\d+(?:\.\d+)?) (?<u>[A-Z]+) allocated · GCs gen0 (?<g0>\d+) / gen1 (?<g1>\d+) / gen2 (?<g2>\d+)\]$",
      System.Text.RegularExpressions.RegexOptions.Compiled
    )

  /// The figures a line carries, read back. Time is to the hundredth of a second and bytes to the precision the line
  /// printed, so a line read and written again is the same line. The reader exists for the tests and for a client that wants the numbers.
  let tryParseLine (text: string) : EvalCost option =
    let m = linePattern.Match(text.Trim())
    match m.Success, tryUnitOfLabel m.Groups["u"].Value with
    | true, Some unit ->
      let number = Double.Parse(m.Groups["n"].Value, inv)
      Some
        // Through ticks and a rounding: converting 0.57 with FromSeconds gives 5,699,999 ticks, a tick short of the 0.57 s the line printed.
        { Elapsed = TimeSpan.FromTicks(int64 (Math.Round(Double.Parse(m.Groups["s"].Value, inv) * float TimeSpan.TicksPerSecond)))
          Allocated = int64 (Math.Round(number * float (bytesIn unit)))
          Gen0 = int m.Groups["g0"].Value
          Gen1 = int m.Groups["g1"].Value
          Gen2 = int m.Groups["g2"].Value }
    | _ -> None
