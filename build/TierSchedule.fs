/// What the pipeline learns from one run and spends on the next: the order units start in (what ends the run first),
/// the weights the host partition balances on (per-case seconds, smoothed), the check that every host case was handed
/// to some shard, and the table that shows each tier's start, wall and cpu so the critical path is visible at a glance.
///
/// Pure, so the pipeline script and the tests read the same rules. Loaded by ci-pipeline.fsx (`#load`) after
/// TierPlan.fs and TierCost.fs.
module SageFs.Build.TierSchedule

open SageFs.Build.TierPlan

/// Recorded seconds per tier (wall) and what each cost (cpu, peak memory), by tier name. Both come from files the
/// pipeline keeps beside its results; a missing file is an empty map, and an empty history is the declared order.
type History =
  { Wall: Map<string, float>
    Costs: Map<string, TierCost.Cost> }

let emptyHistory = { Wall = Map.empty; Costs = Map.empty }

/// A unit that keeps less than this many threads busy, on average, spends its time waiting (a journey that sleeps
/// on a build or a restart), not computing.
let waitBoundThreads = 2.0

/// Wait-bound units start this much "earlier" than their wall time alone puts them: they leave the CPU to the units
/// that need it, so starting them first costs the rest nothing. Smaller than any real gap between two units, so it only
/// settles near-ties.
let waitBoundHeadStartSeconds = 60.0

/// How long this tier is expected to hold up the end of the run: its recorded wall time, plus the head start of a
/// wait-bound unit. A tier with no recorded wall time is `infinity`: it may be the longest, so it starts first.
let priorityOf (history: History) (tier: Tier) : float =
  match history.Wall.TryFind tier.Name with
  | None -> infinity
  | Some wall ->
    match history.Costs.TryFind tier.Name with
    | Some cost when wall > 0.0 && TierCost.totalSeconds cost / wall < waitBoundThreads -> wall + waitBoundHeadStartSeconds
    | _ -> wall

/// Longest critical path first. Stable for equal priorities, so with no history the order is the declared one.
let orderByCriticalPath (history: History) (tiers: Tier list) : Tier list =
  tiers |> List.sortByDescending (priorityOf history)

/// The memory a unit is expected to need: what it peaked at last time, or `Admission.unknownPeakBytes`.
let peakBytesOf (history: History) (name: string) : int64 =
  match history.Costs.TryFind name with
  | Some cost when cost.PeakBytes > 0L -> cost.PeakBytes
  | _ -> Admission.unknownPeakBytes

let candidateOf (history: History) (tier: Tier) : Candidate =
  { Label = tier.Name; PeakBytes = peakBytesOf history tier.Name }

// ---- per-case seconds -------------------------------------------------------------------------------

/// One line of the case timings file a test process writes (SAGEFS_CASE_TIMINGS_OUT): seconds, a tab, the full name
/// with its levels joined by " / ".
type CaseTiming = { Case: string; Seconds: float }

let parseCaseTimings (tsv: string) : CaseTiming list =
  tsv.Split('\n', System.StringSplitOptions.RemoveEmptyEntries)
  |> Array.choose (fun line ->
    match line.TrimEnd('\r').Split('\t', 2) with
    | [| seconds; name |] ->
      match System.Double.TryParse(seconds, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture) with
      | true, s -> Some { Case = name; Seconds = s }
      | false, _ -> None
    | _ -> None)
  |> List.ofArray

let caseNameSeparator = " / "

/// The root list of the host tier (SageFs.Tests Program.fs: `testList "Integration (host)"`): the suites of the host tier
/// are the lists right under it.
let hostRootName = "Integration (host)"

/// Seconds per suite: the sum of its cases. A case's suite is the level right under the root list (`root / suite / case`),
/// the unit the host partition moves between shards.
let suiteSecondsOfCases (root: string) (cases: CaseTiming list) : Map<string, float> =
  cases
  |> List.choose (fun c ->
    match c.Case.Split(caseNameSeparator, System.StringSplitOptions.None) with
    | parts when parts.Length >= 3 && parts[0] = root -> Some (parts[1], c.Seconds)
    | _ -> None)
  |> List.groupBy fst
  |> List.map (fun (suite, xs) -> suite, xs |> List.sumBy snd)
  |> Map.ofList

/// How much of a suite's new weight replaces the old: half. One run under load takes twice as long as a quiet one, and
/// taking it whole would move the suite to another shard for the next run (and back after it).
let latestWeight = 0.5

/// The suite weights after a run: each suite the run timed moves halfway to its new value, the rest keep what they had.
let blend (previous: Map<string, float>) (latest: Map<string, float>) : Map<string, float> =
  latest
  |> Map.fold
    (fun (acc: Map<string, float>) suite seconds ->
      match acc.TryFind suite with
      | None -> acc.Add(suite, seconds)
      | Some before -> acc.Add(suite, before * (1.0 - latestWeight) + seconds * latestWeight))
    previous

// ---- the host partition -----------------------------------------------------------------------------

/// The seconds each of `count` shards is expected to take with the partition `TierPlan.assign` makes from `durations`.
let shardLoads (count: int) (durations: Map<string, float>) (suites: string list) : float list =
  let plan = assign count durations suites
  let known = suites |> List.distinct |> List.choose durations.TryFind
  let fallback = match known with [] -> 1.0 | xs -> List.average xs
  [ for shard in 1 .. max 1 count ->
      suites
      |> List.distinct
      |> List.filter (fun s -> plan[s] = shard)
      |> List.sumBy (fun s -> durations.TryFind s |> Option.defaultValue fallback) ]

/// The fewest host shards there will be: past the heaviest suite more shards shorten nothing, and with fewer than this the
/// longest shard is a suite plus a queue. It is a floor, not a promise: a machine too small to carry this many live hosts
/// still gets this many shards (the scheduler admits them by what the machine has, so they queue, and a longer run is
/// better than a wrong partition).
let minHostShards = 5

/// What a machine has, as far as the host tier is concerned. A snapshot taken once per run (threads this process may use,
/// physical memory the runtime may use), never a live reading: the picker must give the same answer for the same history.
type Machine = { UsableThreads: int; TotalMemoryBytes: int64 }

/// How many host cases one test process runs at once (`HostSlots`, `TestMagnitudes.concurrentHosts`: a test pins the two
/// equal, because the build cannot reference the tests).
let hostsPerShard = 3

/// Live hosts per usable thread. Measured on the 16-thread development machine, with the other tiers running beside the host
/// shards:
///   5 shards x 3 = 15 live hosts (0.94 per thread): five gates, all green, shards 458 to 517 s;
///   8 shards x 3 = 24 live hosts (1.50 per thread): two gates, both red, with load-induced flakes
///   (RunAppDeltaTests.fs:321 connection refused, HostCoreAdoptionOrphanSweepTests.fs:84 marker pid mismatch).
/// One per thread is the highest figure that has a green gate under it, and rounds to the 15 that passed. Do not raise it
/// without a green gate at the higher figure: the cases that flaked are timing cases, and a shard that is slower because the
/// machine is loaded is the cost of the host count, not a bug in the cases.
let liveHostsPerThread = 1

/// The most memory one live host peaks at. A host shard (3 hosts, the FSI sessions and the apps they run) peaked at 2.9 to 5.0
/// GiB over those gates (tier-durations.costs.json); 5.0 / 3 = 1.67 GiB, rounded up to a whole 2 GiB.
let peakBytesPerHost = 2L * Admission.bytesPerGiB

/// How many hosts the machine can have live at once for the host tier: the lower of what its threads carry
/// (`liveHostsPerThread`) and what its memory holds once the reserve every unit leaves for the OS and its neighbours
/// (`Admission.memoryReserveBytes`) is taken off, each host at its peak. Never negative, and never lower on a bigger machine.
let liveHostCap (machine: Machine) : int =
  let byThreads = max 0 machine.UsableThreads * liveHostsPerThread
  let byMemory = max 0L (machine.TotalMemoryBytes - Admission.memoryReserveBytes) / peakBytesPerHost
  int (min (int64 byThreads) byMemory)

/// The most shards the machine carries: each is `hostsPerShard` live hosts. At least one, so the cap is a number a count can
/// be compared with; `hostShardCount` applies the floor.
let hostShardCap (machine: Machine) : int =
  max 1 (liveHostCap machine / hostsPerShard)

/// A shard may be this much longer than the best a count can do, before another shard is asked for.
let hostShardTolerance = 0.05

/// How many host shards the recorded suite weights call for: the smallest count whose longest shard (by
/// `TierPlan.assign`) is within `hostShardTolerance` of the best any count can do, which is the heaviest suite alone or a
/// perfect split at the machine's cap (`hostShardCap`, never below `minHostShards`). The weights are sequential-equivalent
/// seconds, so a shard's real length is shorter than the weights say (the 5-shard partition of the recorded weights
/// predicts 969 s and ran in 458 to 517 s): the count is bounded by the machine, not chased down to the weights.
/// With no history it is `minHostShards`.
let hostShardCount (machine: Machine) (suiteSeconds: Map<string, float>) : int =
  match suiteSeconds.IsEmpty with
  | true -> minHostShards
  | false ->
    let cap = max minHostShards (hostShardCap machine)
    let suites = suiteSeconds |> Map.toList |> List.map fst
    let heaviest = suiteSeconds |> Map.fold (fun m _ v -> max m v) 0.0
    let total = suiteSeconds |> Map.fold (fun s _ v -> s + v) 0.0
    let target = max heaviest (total / float cap) * (1.0 + hostShardTolerance)
    [ minHostShards .. cap ]
    |> List.tryFind (fun n -> List.max (shardLoads n suiteSeconds suites) <= target)
    |> Option.defaultValue cap

/// Whether the shards between them were handed every host case exactly once.
type Coverage =
  | Covered
  | CoverageGap of expected: int * registered: int

/// `expected` is the number of cases every host suite registers when none is left out (a `--list-tests` of the whole host
/// tier); `registered` is each shard's own count from its trust row. A suite no shard took, or one two shards took, is a gap.
let checkHostCoverage (expected: int) (registered: int list) : Coverage =
  match List.sum registered = expected with
  | true -> Covered
  | false -> CoverageGap (expected, List.sum registered)

// ---- the timing table ---------------------------------------------------------------------------------

type CpuReading =
  | CpuSeconds of float
  | CpuNotMeasured

/// When a tier started (seconds after the first tier did), how long it ran and what its process tree cost.
type TierTiming =
  { Tier: string
    StartOffsetSeconds: float
    WallSeconds: float
    Cpu: CpuReading }

let renderTimingTable (timings: TierTiming list) : string =
  let header =
    [ "| Tier | Start | Wall | End | CPU |"
      "|---|---:|---:|---:|---:|" ]
  let cpuText (cpu: CpuReading) =
    match cpu with
    | CpuSeconds s -> sprintf "%.0fs" s
    | CpuNotMeasured -> "n/a"
  let ordered = timings |> List.sortBy (fun t -> t.StartOffsetSeconds, t.Tier)
  let body =
    ordered
    |> List.map (fun t ->
      sprintf "| %s | %.0fs | %.0fs | %.0fs | %s |" t.Tier t.StartOffsetSeconds t.WallSeconds (t.StartOffsetSeconds + t.WallSeconds) (cpuText t.Cpu))
  let last =
    match ordered with
    | [] -> "no tier ran, so there is no critical path"
    | _ ->
      let t = ordered |> List.maxBy (fun t -> t.StartOffsetSeconds + t.WallSeconds)
      sprintf "critical path: %s ended last, at %.0fs" t.Tier (t.StartOffsetSeconds + t.WallSeconds)
  String.concat "\n" ("## Tier timing" :: "" :: header @ body @ [ ""; last ])
