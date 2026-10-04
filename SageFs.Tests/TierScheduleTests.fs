module SageFs.Tests.TierScheduleTests

open Expecto
open Expecto.Flip
open FsCheck
open SageFs.Build
open SageFs.Build.TierPlan
open SageFs.Build.TierSchedule

/// What the pipeline learns from a run and spends on the next one: the order units start in, the partition of the
/// host suites, the check that every host case was handed to some shard, and the table that shows the critical path.

let private tierNamed (name: string) = { Name = name; Args = name; Framework = Framework.primary }

let private cost (cpu: float) (peakGiB: int) : TierCost.Cost =
  let noSystemTime = 0.0
  { UserSeconds = cpu; SystemSeconds = noSystemTime; PeakBytes = int64 peakGiB * Admission.bytesPerGiB }

let private history (wall: (string * float) list) (costs: (string * TierCost.Cost) list) =
  { Wall = Map.ofList wall; Costs = Map.ofList costs }

[<Tests>]
let tests =
  testList "TierSchedule" [

    // ── order ──
    testCase "WHY — the longest expected unit starts first: it is what ends the run" <| fun _ ->
      let h = history [ "short", 20.0; "long", 500.0; "mid", 200.0 ] []
      orderByCriticalPath h [ tierNamed "short"; tierNamed "mid"; tierNamed "long" ]
      |> List.map (fun t -> t.Name)
      |> Expect.equal "longest first" [ "long"; "mid"; "short" ]

    testCase "WHY — a unit nobody has timed starts before every timed one, since it may be the longest" <| fun _ ->
      let h = history [ "long", 500.0 ] []
      orderByCriticalPath h [ tierNamed "long"; tierNamed "new" ]
      |> List.map (fun t -> t.Name)
      |> Expect.equal "unknown first" [ "new"; "long" ]

    testCase "WHY — with no history at all the order is the declared order: the static fallback" <| fun _ ->
      orderByCriticalPath emptyHistory [ tierNamed "b"; tierNamed "a"; tierNamed "c" ]
      |> List.map (fun t -> t.Name)
      |> Expect.equal "unchanged" [ "b"; "a"; "c" ]

    testCase "WHY — of two units that end at about the same time, the one that waits starts first and leaves the CPU to the rest" <| fun _ ->
      // `waiting` runs 290 s on one thread's worth of CPU; `crunching` runs 300 s on three threads' worth.
      let h = history [ "waiting", 290.0; "crunching", 300.0 ] [ "waiting", cost 290.0 1; "crunching", cost 900.0 2 ]
      orderByCriticalPath h [ tierNamed "crunching"; tierNamed "waiting" ]
      |> List.map (fun t -> t.Name)
      |> Expect.equal "the wait-bound journey first" [ "waiting"; "crunching" ]

    testCase "WHY — the head start never reorders units that differ by more of it than the wall time says" <| fun _ ->
      let h = history [ "waiting", 100.0; "crunching", 500.0 ] [ "waiting", cost 100.0 1; "crunching", cost 1500.0 2 ]
      orderByCriticalPath h [ tierNamed "waiting"; tierNamed "crunching" ]
      |> List.map (fun t -> t.Name)
      |> Expect.equal "a 400 s gap is not a tie" [ "crunching"; "waiting" ]

    testProperty "WHY — ordering is a permutation: every unit starts exactly once" <|
      fun (names: NonEmptyString list) (known: (NonEmptyString * PositiveInt) list) ->
        let tiers = names |> List.map (fun n -> n.Get) |> List.distinct |> List.map tierNamed
        let h = history (known |> List.map (fun (n, s) -> n.Get, float s.Get)) []
        List.sort (orderByCriticalPath h tiers) = List.sort tiers

    testCase "WHY — a unit's memory claim is its recorded peak, or the assumed one when it has never run" <| fun _ ->
      let h = history [] [ "known", cost 10.0 3 ]
      peakBytesOf h "known" |> Expect.equal "recorded" (3L * Admission.bytesPerGiB)
      peakBytesOf h "never run" |> Expect.equal "assumed" Admission.unknownPeakBytes

    // ── per-case seconds ──
    testCase "WHY — the case file is one `seconds<TAB>full name` line per case, slowest first" <| fun _ ->
      let tsv = "225.300\tIntegration (host) / hot reload keeps live state across a save / rule 1\n0.012\tIntegration (host) / Falco / renders\n"
      parseCaseTimings tsv
      |> Expect.equal "both lines"
           [ { Case = "Integration (host) / hot reload keeps live state across a save / rule 1"; Seconds = 225.3 }
             { Case = "Integration (host) / Falco / renders"; Seconds = 0.012 } ]

    testCase "WHY — a line that is not a timing is skipped, never turned into a zero that would make a suite look free" <| fun _ ->
      parseCaseTimings "garbage\n\n12.5\tA / b\nx\tA / c\n"
      |> Expect.equal "only the good line" [ { Case = "A / b"; Seconds = 12.5 } ]

    testCase "WHY — a suite's weight is the sum of its cases, found by the second segment of the name" <| fun _ ->
      let cases =
        [ { Case = "Integration (host) / S1 / a"; Seconds = 10.0 }
          { Case = "Integration (host) / S1 / b"; Seconds = 5.0 }
          { Case = "Integration (host) / S2 / a"; Seconds = 7.0 }
          { Case = "Elsewhere / S1 / a"; Seconds = 99.0 } ]
      suiteSecondsOfCases "Integration (host)" cases
      |> Expect.equal "other roots do not count" (Map.ofList [ "S1", 15.0; "S2", 7.0 ])

    testProperty "WHY — blending keeps a suite's weight between what it was and what it just took, so one noisy run cannot whiplash the partition" <|
      fun (before: PositiveInt) (latest: PositiveInt) ->
        let b, l = float before.Get, float latest.Get
        let blended = blend (Map.ofList [ "s", b ]) (Map.ofList [ "s", l ])
        let v = blended["s"]
        v >= min b l - 1e-9 && v <= max b l + 1e-9

    testCase "WHY — blending keeps suites a shard did not run and takes new ones as they are" <| fun _ ->
      blend (Map.ofList [ "old", 3.0 ]) (Map.ofList [ "new", 4.0 ])
      |> Expect.equal "union" (Map.ofList [ "old", 3.0; "new", 4.0 ])

    // ── the partition ──
    testProperty "WHY — the longest shard is within one suite of a perfect split, so no shard is left to end the run alone" <|
      fun (PositiveInt countArg) (weights: PositiveInt list) ->
        let count = 1 + countArg % 8
        let suites = weights |> List.mapi (fun i w -> sprintf "suite-%d" i, float w.Get)
        let durations = Map.ofList suites
        let loads = shardLoads count durations (suites |> List.map fst)
        let total = suites |> List.sumBy snd
        let heaviest = suites |> List.fold (fun m (_, w) -> max m w) 0.0
        (List.max loads) <= total / float count + heaviest + 1e-6

    testCase "WHY — the partition predicts its own longest shard, so a run says what it expects before it starts" <| fun _ ->
      let durations = Map.ofList [ "a", 362.0; "b", 339.0; "c", 254.0; "d", 100.0; "e", 100.0 ]
      shardLoads 5 durations [ "a"; "b"; "c"; "d"; "e" ]
      |> List.max
      |> Expect.equal "five suites on five shards: the largest suite is the longest shard" 362.0

    // ── how many shards ──
    testCase "WHY — with no history the host tier gets today's shard count" <| fun _ ->
      hostShardCount Map.empty |> Expect.equal "the floor" minHostShards

    testCase "WHY — more shards are asked for only while they shorten the longest one: past the heaviest suite they buy nothing" <| fun _ ->
      // 75 suites with the real shape: one 360 s suite, one 350 s, one 264 s, and a long tail.
      let heavy = [ "a", 360.0; "b", 350.0; "c", 264.0; "d", 143.0; "e", 132.0 ]
      let tail = [ for i in 1 .. 70 -> sprintf "t%d" i, 20.0 ]
      let weights = Map.ofList (heavy @ tail)
      let n = hostShardCount weights
      (n > minHostShards) |> Expect.isTrue "the tail can be spread, so more shards than the floor"
      (n <= maxHostShards) |> Expect.isTrue "never past the cap"
      let longest = shardLoads n weights (weights |> Map.toList |> List.map fst) |> List.max
      (longest <= 360.0 * (1.0 + hostShardTolerance) + 1e-6) |> Expect.isTrue "the longest shard is within the tolerance of the heaviest suite"

    testCase "WHY — one suite that dwarfs the rest gains nothing from more shards, so the count stays at the floor" <| fun _ ->
      hostShardCount (Map.ofList [ "huge", 900.0; "a", 10.0; "b", 10.0 ]) |> Expect.equal "the floor" minHostShards

    testProperty "WHY — the shard count stays between the floor and the cap, whatever the weights" <|
      fun (weights: PositiveInt list) ->
        let n = hostShardCount (weights |> List.mapi (fun i w -> sprintf "s%d" i, float w.Get) |> Map.ofList)
        n >= minHostShards && n <= maxHostShards

    // ── coverage ──
    testCase "WHY — shards that between them registered every host case are Covered" <| fun _ ->
      checkHostCoverage 330 [ 61; 36; 76; 71; 86 ] |> Expect.equal "61+36+76+71+86" Covered

    testCase "WHY — a missing case is named with both numbers, so a shard that dropped a suite is red" <| fun _ ->
      checkHostCoverage 330 [ 61; 36; 76; 71; 85 ]
      |> Expect.equal "one short" (CoverageGap (330, 329))

    testCase "WHY — a case counted twice is a gap too: the partition is a partition or it is wrong" <| fun _ ->
      checkHostCoverage 330 [ 61; 36; 76; 71; 87 ] |> Expect.equal "one extra" (CoverageGap (330, 331))

    testCase "WHY — no shard reporting is not coverage" <| fun _ ->
      checkHostCoverage 330 [] |> Expect.equal "nothing ran" (CoverageGap (330, 0))

    // ── the table ──
    testCase "WHY — the timing table lists every tier by when it started, with its wall and cpu, and names what ended the run" <| fun _ ->
      // A row is built from named arguments, so no field takes a bare number the timeout-literal ratchet would count.
      let row (tier: string) (start: float) (wall: float) (cpu: CpuReading) =
        { Tier = tier; StartOffsetSeconds = start; WallSeconds = wall; Cpu = cpu }
      let rows =
        [ row "hr" 0.0 360.0 (CpuSeconds 650.0)
          row "host-1" 3.0 300.0 (CpuSeconds 700.0)
          row "lt" 303.0 40.0 CpuNotMeasured ]
      let text = renderTimingTable rows
      text |> Expect.stringContains "the first row" "hr"
      text |> Expect.stringContains "a start offset" "303"
      text |> Expect.stringContains "an unmeasured cpu says so" "n/a"
      text |> Expect.stringContains "the critical path is the tier that ended last" "critical path: hr"

    testCase "WHY — a run with no timed tier has no critical path to name" <| fun _ ->
      renderTimingTable [] |> Expect.stringContains "says so" "no tier ran"
  ]
