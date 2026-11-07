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
  let BytesPerKilobyte = 1_000L

  /// An eval that allocated this much is annotated even when it was quick: allocation volume is what an agent
  /// comparing two versions of a function wants to see. FsiMcp 0.3.0 uses the same 100 MB.
  [<Literal>]
  let HeavyAllocationBytes = 100_000_000L

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

  /// The cost of an eval from the readings taken before and after it. A reading that went backwards (a counter that
  /// restarted, a clock that was stepped) is a zero, never a negative figure.
  let measured (elapsed: TimeSpan) (allocatedBefore: int64, allocatedAfter: int64) (gen0: int * int) (gen1: int * int) (gen2: int * int) : EvalCost =
    let rise (before: int64) (after: int64) = max 0L (after - before)
    { Elapsed = max TimeSpan.Zero elapsed
      Allocated = rise allocatedBefore allocatedAfter
      Gen0 = int (rise (int64 (fst gen0)) (int64 (snd gen0)))
      Gen1 = int (rise (int64 (fst gen1)) (int64 (snd gen1)))
      Gen2 = int (rise (int64 (fst gen2)) (int64 (snd gen2))) }

  /// Whether this cost is past either threshold. At the threshold counts as past it.
  let isNotable (thresholds: CostThresholds) (cost: EvalCost) : bool =
    cost.Elapsed >= thresholds.Slow || cost.Allocated >= thresholds.HeavyAllocation

  let private inv = CultureInfo.InvariantCulture

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

  let private allUnits = [ Bytes; Kilobytes; Megabytes; Gigabytes ]

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
  let private unitFor (bytes: int64) : ByteUnit =
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
    | _ -> None // RED: the line is not written yet

  let private linePattern =
    System.Text.RegularExpressions.Regex(
      @"^\[(?<s>\d+\.\d{2}) s · (?<n>\d+(?:\.\d+)?) (?<u>[A-Z]+) allocated · GCs gen0 (?<g0>\d+) / gen1 (?<g1>\d+) / gen2 (?<g2>\d+)\]$",
      System.Text.RegularExpressions.RegexOptions.Compiled
    )

  /// The figures a line carries, read back. Time is to the hundredth of a second and bytes to the precision the line
  /// printed, so a line read and written again is the same line. The reader exists for the tests and for a client that wants the numbers.
  let tryParseLine (text: string) : EvalCost option =
    match linePattern.Match(text.Trim()), text with
    | _ -> None // RED: the reader is not written yet
