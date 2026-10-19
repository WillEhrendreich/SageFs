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

// ---------------------------------------------------------------------------------------------
// The curve: does a save get slower as the session gets older?
//
// `summarize` and `gate` judge a series as one pile, so a series that starts at 0.5 s and ends at 5 s
// and one that sits at 2.7 s throughout look the same to them. The roast of 2026-10-17 (PERF-1) says
// the first is what happens: every eval walks the whole growing FSI assembly and re-points every older
// copy of a function, so the cost of a save follows the number of saves already made. The figure the
// Readme quotes is from saves 3 to 22 of a fresh session, so it cannot show that. These are the pieces
// that can: the windows to compare, the bound, and the arithmetic, all pure.
// ---------------------------------------------------------------------------------------------

/// How many saves the growth series measures after the `warmupEdits`. Long enough that the last window
/// is a session several times older than the one the Readme figure came from (saves 3 to 22), and
/// short enough to be a tier of its own: at the Readme's pace of 0.5 s a save it is a minute and a half,
/// and at the pace the roast reports for its last saves (4.8 s) it is several minutes.
[<Literal>]
let growthSeriesSaves = 150

/// Saves in each of the three windows the curve is read from: the first, the middle and the last
/// `growthWindowSaves` of the series. The early window (saves 3 to 17 once the warm-up is dropped) lines
/// up with the Readme's own and with the secondhand curve's first window ("saves 1-15"); 15 is odd, so
/// its median is a real middle value, and three of them fit in 150 saves with 120 saves between
/// the first and the last.
[<Literal>]
let growthWindowSaves = 15

/// The most the median of the last window may be over the median of the first. 2.0.
///
/// The Readme (Readme.md:486) sells "a save is back to all green in about 0.5 s" (p50 542 ms over saves
/// 3 to 22). Twice that, about 1.1 s, is still a loop a user would call "about a second" at save 150;
/// three times or more is a different product. The absolute gate that exists (a p95 of 3 s,
/// TestTimeouts.liveTestingSaveToGreenP95Bound) is 4.7 times the Readme's p95 of 631 ms, an allowance for
/// a cold shared runner, and it cannot see growth until the curve is already large. This bound is a
/// ratio of two windows of one daemon, one session and one run, so the load on the box moves both
/// together, and a median of 15 is far steadier than a p95 of 20: 2.0 leaves room for that and still
/// catches the curve the roast was handed (655 ms to 4.8 s, 7.3 times) at about a quarter of its size.
/// It is a judgment from those numbers, not a fit to a measured curve: tighten it with the first real one.
[<Literal>]
let growthBoundRatio = 2.0

/// Why a curve cannot be read from a series.
[<RequireQualifiedAccess>]
type CurveRefusal =
  /// A window of no saves has no median.
  | WindowIsEmpty
  /// Three windows need three windows' worth of saves.
  | TooFewSamples of have: int * need: int
  /// The first window's median is zero, so a ratio against it says nothing.
  | NoBaseline

/// The medians of the first, the middle and the last window of a series, in the order the saves were made,
/// and how much the last grew from the first.
type Curve =
  { Count: int
    Window: int
    Early: TimeSpan
    Middle: TimeSpan
    Late: TimeSpan
    /// `Late` over `Early`: 1.0 is a flat curve.
    Growth: float }

/// The median (nearest rank, so the lower middle of an even window) of `window` samples from index `from`.
let windowMedian (window: int) (from: int) (samples: TimeSpan list) : Result<TimeSpan, CurveRefusal> =
  samples
  |> List.skip from
  |> List.truncate window
  |> summarize
  |> Result.map (fun summary -> summary.P50)
  |> Result.mapError (fun SummaryRefusal.NoSamples -> CurveRefusal.WindowIsEmpty)

/// Read the curve of `samples`, which must be in the order the saves were made: the windows are positions in
/// the series, never ranks, so a series that gets faster reads as a growth below 1.0.
let curveOf (window: int) (samples: TimeSpan list) : Result<Curve, CurveRefusal> =
  let count = List.length samples
  match window < 1, count < 3 * window with
  | true, _ -> Result.Error CurveRefusal.WindowIsEmpty
  | false, true -> Result.Error (CurveRefusal.TooFewSamples (count, 3 * window))
  | false, false ->
    windowMedian window 0 samples
    |> Result.bind (fun early ->
      windowMedian window ((count - window) / 2) samples
      |> Result.bind (fun middle ->
        windowMedian window (count - window) samples
        |> Result.bind (fun late ->
          match early = TimeSpan.Zero with
          | true -> Result.Error CurveRefusal.NoBaseline
          | false ->
            Ok
              { Count = count
                Window = window
                Early = early
                Middle = middle
                Late = late
                Growth = float late.Ticks / float early.Ticks })))

[<RequireQualifiedAccess>]
type GrowthVerdict =
  | Flat of measured: float * bound: float
  | Growing of measured: float * bound: float

/// Judged on the ratio of the last window to the first, and on that alone: a bound of exactly `bound` passes.
let growthGate (bound: float) (curve: Curve) : GrowthVerdict =
  match curve.Growth <= bound with
  | true -> GrowthVerdict.Flat (curve.Growth, bound)
  | false -> GrowthVerdict.Growing (curve.Growth, bound)

let describeGrowth (verdict: GrowthVerdict) : string =
  match verdict with
  | GrowthVerdict.Flat (measured, bound) ->
    sprintf "the late saves took %.2f times the early ones, within the bound of %.2f" measured bound
  | GrowthVerdict.Growing (measured, bound) ->
    sprintf "the late saves took %.2f times the early ones, over the bound of %.2f" measured bound

/// One line per curve: the three medians and the growth, so the raw shape is in the log beside the verdict.
let curveLine (name: string) (curve: Curve) : string =
  sprintf
    "CURVE %s n=%d window=%d early-p50=%.1fms middle-p50=%.1fms late-p50=%.1fms growth=%.2fx"
    name curve.Count curve.Window
    curve.Early.TotalMilliseconds curve.Middle.TotalMilliseconds curve.Late.TotalMilliseconds curve.Growth

/// Every save's time, in the order the saves were made, in whole milliseconds, so the shape between the
/// windows can be read and not only the three medians.
let timesLine (name: string) (times: TimeSpan list) : string =
  times
  |> List.map (fun t -> sprintf "%.0f" t.TotalMilliseconds)
  |> String.concat ","
  |> sprintf "SAMPLES %s ms=%s" name

/// The smallest window a shortened series may use: three saves have a middle one.
[<Literal>]
let shortestWindowSaves = 3

/// The variable that shortens the growth series, for a smoke run of the same code path. Unset, the series
/// is `growthSeriesSaves` long.
[<Literal>]
let growthSavesVariable = "SAGEFS_HR_GROWTH_SAVES"

/// How long a growth series is and how wide its windows are.
type GrowthPlan = { Saves: int; Window: int }

[<RequireQualifiedAccess>]
type GrowthPlanRefusal =
  | NotANumber of text: string
  | TooFewSaves of asked: int * need: int

module GrowthPlan =
  let full : GrowthPlan = { Saves = growthSeriesSaves; Window = growthWindowSaves }

  /// A plan of `saves` measured saves: windows of `growthWindowSaves`, or a third of the series when that is
  /// shorter. Three windows of at least `shortestWindowSaves` have to fit.
  let ofSaves (saves: int) : Result<GrowthPlan, GrowthPlanRefusal> =
    let need = 3 * shortestWindowSaves
    match saves < need with
    | true -> Result.Error (GrowthPlanRefusal.TooFewSaves (saves, need))
    | false -> Ok { Saves = saves; Window = min growthWindowSaves (saves / 3) }

  let ofText (text: string) : Result<GrowthPlan, GrowthPlanRefusal> =
    match Int32.TryParse text with
    | true, saves -> ofSaves saves
    | false, _ -> Result.Error (GrowthPlanRefusal.NotANumber text)
