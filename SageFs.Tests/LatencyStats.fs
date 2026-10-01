/// Honest latency numbers for the live-testing path: the summary a repeated
/// measurement reduces to, the gate it is judged against, and the one line it
/// prints with the machine it ran on. A number without its machine and its
/// sample count is not a number worth writing down, so `reportLine` refuses to
/// print one without them.
module SageFs.Tests.LatencyStats

open System

/// How many measured edits each latency path takes. The 95th percentile of 20 is the 19th value, so
/// one slow edit in twenty shows without being the whole answer. Each costs a second or two, so more
/// samples is a longer tier, not a better number.
[<Literal>]
let samplesPerPath = 20

/// Edits made and thrown away before measuring: the first ones pay for the JIT and a cold type-check
/// cache, and a latency that includes them is a startup time, not a keystroke time.
[<Literal>]
let warmupEdits = 2

/// Why a set of samples cannot be summarised.
[<RequireQualifiedAccess>]
type SummaryRefusal =
  /// Nothing was measured. A summary of nothing would read as "zero latency".
  | NoSamples

type Summary =
  { Count: int
    Min: TimeSpan
    P50: TimeSpan
    P95: TimeSpan
    Max: TimeSpan }

[<RequireQualifiedAccess>]
type GateVerdict =
  | WithinBound of measured: TimeSpan * bound: TimeSpan
  | Regressed of measured: TimeSpan * bound: TimeSpan

/// The machine a measurement ran on.
type Machine =
  { Os: string
    Cpu: string
    LogicalCores: int
    MemoryGiB: int
    Runtime: string }

/// Nearest-rank summary: the p-th percentile of n samples is the ceil(p * n / 100)-th smallest.
let summarize (samples: TimeSpan list) : Result<Summary, SummaryRefusal> =
  failwith "not implemented: LatencyStats.summarize"

/// Judged on the 95th percentile: a bound on the median would let one slow run in five hide.
let gate (bound: TimeSpan) (summary: Summary) : GateVerdict =
  failwith "not implemented: LatencyStats.gate"

let describeVerdict (verdict: GateVerdict) : string =
  failwith "not implemented: LatencyStats.describeVerdict"

/// The machine this process is running on.
let machine () : Machine =
  failwith "not implemented: LatencyStats.machine"

/// One line per measurement: name, sample count, the percentiles and the machine.
let reportLine (name: string) (machine: Machine) (summary: Summary) : string =
  failwith "not implemented: LatencyStats.reportLine"
