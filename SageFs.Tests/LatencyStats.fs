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

/// The p-th percentile (whole percents) of an ascending array, by nearest rank: the ceil(p * n / 100)-th smallest.
let private nearestRank (percent: int) (ascending: TimeSpan array) : TimeSpan =
  let n = ascending.Length
  let rank = max 1 ((percent * n + 99) / 100)
  ascending.[min n rank - 1]

/// Nearest-rank summary: the p-th percentile of n samples is the ceil(p * n / 100)-th smallest.
let summarize (samples: TimeSpan list) : Result<Summary, SummaryRefusal> =
  match samples with
  | [] -> Result.Error SummaryRefusal.NoSamples
  | _ ->
    let ascending = samples |> List.sort |> List.toArray
    Ok
      { Count = ascending.Length
        Min = ascending.[0]
        P50 = nearestRank 50 ascending
        P95 = nearestRank 95 ascending
        Max = ascending.[ascending.Length - 1] }

/// Judged on the 95th percentile: a bound on the median would let one slow run in five hide.
/// `Timeout.InfiniteTimeSpan` is "no bound set yet": the measurement still prints, nothing is gated.
let gate (bound: TimeSpan) (summary: Summary) : GateVerdict =
  match bound = Threading.Timeout.InfiniteTimeSpan || summary.P95 <= bound with
  | true -> GateVerdict.WithinBound (summary.P95, bound)
  | false -> GateVerdict.Regressed (summary.P95, bound)

let describeVerdict (verdict: GateVerdict) : string =
  match verdict with
  | GateVerdict.WithinBound (measured, bound) when bound = Threading.Timeout.InfiniteTimeSpan ->
    sprintf "p95 is %g ms, and no bound is set yet" measured.TotalMilliseconds
  | GateVerdict.WithinBound (measured, bound) ->
    sprintf "p95 is %g ms, within the bound of %g ms" measured.TotalMilliseconds bound.TotalMilliseconds
  | GateVerdict.Regressed (measured, bound) ->
    sprintf "p95 is %g ms, over the bound of %g ms" measured.TotalMilliseconds bound.TotalMilliseconds

let private bytesPerGiB = 1024L * 1024L * 1024L

/// The CPU's marketing name where the OS gives one, and the architecture where it does not.
let private cpuName () : string =
  let fromProcCpuInfo () =
    try
      IO.File.ReadLines "/proc/cpuinfo"
      |> Seq.tryFind (fun l -> l.StartsWith("model name", StringComparison.Ordinal))
      |> Option.map (fun l -> l.Substring(l.IndexOf ':' + 1).Trim())
    with _ -> None
  let fromEnvironment () =
    match Environment.GetEnvironmentVariable "PROCESSOR_IDENTIFIER" with
    | null | "" -> None
    | id -> Some id
  fromProcCpuInfo ()
  |> Option.orElseWith fromEnvironment
  |> Option.defaultValue (sprintf "%A" Runtime.InteropServices.RuntimeInformation.ProcessArchitecture)

/// The machine this process is running on.
let machine () : Machine =
  { Os = Runtime.InteropServices.RuntimeInformation.OSDescription
    Cpu = cpuName ()
    LogicalCores = Environment.ProcessorCount
    MemoryGiB = int (max 1L ((GC.GetGCMemoryInfo().TotalAvailableMemoryBytes + bytesPerGiB / 2L) / bytesPerGiB))
    Runtime = Runtime.InteropServices.RuntimeInformation.FrameworkDescription }

/// One line per measurement: name, sample count, the percentiles and the machine.
let reportLine (name: string) (machine: Machine) (summary: Summary) : string =
  sprintf
    "LATENCY %s n=%d p50=%.1fms p95=%.1fms min=%.1fms max=%.1fms machine=\"%s; %s; %d logical cores; %d GiB; %s\""
    name summary.Count
    summary.P50.TotalMilliseconds summary.P95.TotalMilliseconds summary.Min.TotalMilliseconds summary.Max.TotalMilliseconds
    machine.Os machine.Cpu machine.LogicalCores machine.MemoryGiB machine.Runtime
