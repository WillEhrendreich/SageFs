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

/// The machine the gates were measured on (16 threads, 62 GiB), and one that carries 8 shards (24 threads).
let private thisMachine = { UsableThreads = 16; TotalMemoryBytes = 62L * Admission.bytesPerGiB }
let private roomyMachine = { UsableThreads = 24; TotalMemoryBytes = 64L * Admission.bytesPerGiB }

/// The 102 suites of ~/.local/share/sagefs-gate/suite-durations.json at 2026-10-04 (4,888 s, heaviest 422 s), whole seconds,
/// the one-second suites and below dropped. The uncapped picker said 8 shards for these; the gates that passed ran 5.
let private realShapedSuites =
  [ 422; 398; 371; 349; 276; 265; 264; 222; 215; 202; 142; 140; 102; 96; 90; 75; 71; 68; 67; 63; 63; 54; 49; 48; 44; 44; 40; 35; 33
    32; 32; 28; 28; 25; 23; 22; 22; 20; 19; 17; 17; 15; 14; 13; 11; 11; 10; 9; 9; 9; 9; 8; 7; 7; 7; 7; 6; 6; 6; 6; 5; 5; 5; 5; 5; 5
    5; 5; 3; 3; 3; 3; 3; 3; 3; 2; 2; 2; 2; 2; 2; 1; 1; 1; 1; 1; 1; 1; 1; 1 ]
  |> List.mapi (fun i w -> sprintf "suite-%03d" i, float w)
  |> Map.ofList

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
      hostShardCount thisMachine Map.empty |> Expect.equal "the floor" minHostShards

    testCase "WHY — more shards are asked for only while they shorten the longest one: past the heaviest suite they buy nothing" <| fun _ ->
      // 75 suites with the real shape: one 360 s suite, one 350 s, one 264 s, and a long tail.
      let heavy = [ "a", 360.0; "b", 350.0; "c", 264.0; "d", 143.0; "e", 132.0 ]
      let tail = [ for i in 1 .. 70 -> sprintf "t%d" i, 20.0 ]
      let weights = Map.ofList (heavy @ tail)
      let n = hostShardCount roomyMachine weights
      (n > minHostShards) |> Expect.isTrue "the tail can be spread, so more shards than the floor"
      (n <= hostShardCap roomyMachine) |> Expect.isTrue "never past the cap"
      let longest = shardLoads n weights (weights |> Map.toList |> List.map fst) |> List.max
      (longest <= 360.0 * (1.0 + hostShardTolerance) + 1e-6) |> Expect.isTrue "the longest shard is within the tolerance of the heaviest suite"

    testCase "WHY — one suite that dwarfs the rest gains nothing from more shards, so the count stays at the floor" <| fun _ ->
      hostShardCount roomyMachine (Map.ofList [ "huge", 900.0; "a", 10.0; "b", 10.0 ]) |> Expect.equal "the floor" minHostShards

    testProperty "WHY — the shard count stays between the floor and the cap, whatever the weights" <|
      fun (weights: PositiveInt list) ->
        let n = hostShardCount roomyMachine (weights |> List.mapi (fun i w -> sprintf "s%d" i, float w.Get) |> Map.ofList)
        n >= minHostShards && n <= hostShardCap roomyMachine

    // ── how many shards THIS machine carries ──
    // The shard count is not a free choice: every shard runs `hostsPerShard` hosts at once, so shards x hosts is the number
    // of live hosts. 8 shards x 3 = 24 live hosts on 16 threads failed twice with load-induced flakes (a connection
    // refused, a pid mismatch); 5 x 3 = 15 passed in 458 to 517 s.
    testCase "WHY — the hosts per shard here are the hosts the test process runs at once, or the cap counts the wrong thing" <| fun _ ->
      hostsPerShard |> Expect.equal "TestMagnitudes.concurrentHosts" TestMagnitudes.concurrentHosts

    testCase "WHY — this machine class (16 threads, 62 GiB) carries 5 shards: 15 live hosts, the count that passed, not the 24 that flaked" <| fun _ ->
      hostShardCap thisMachine |> Expect.equal "five" 5
      (hostShardCap thisMachine * hostsPerShard <= liveHostCap thisMachine) |> Expect.isTrue "the live hosts fit the cap"
      ((hostShardCap thisMachine + 1) * hostsPerShard > liveHostCap thisMachine) |> Expect.isTrue "and one more shard would not"

    testCase "WHY — the picker, given the real suite weights, picks 5 without an override" <| fun _ ->
      hostShardCount thisMachine realShapedSuites |> Expect.equal "the count the passing gates ran" 5

    testCase "WHY — on a machine that carries 8 the same weights call for 8: the cap is what held it at 5, not the weights" <| fun _ ->
      hostShardCount roomyMachine realShapedSuites |> Expect.equal "what the picker said before the cap" 8

    testCase "WHY — memory binds when the machine has threads to spare: each live host has a peak to fit in" <| fun _ ->
      let threadRich = { UsableThreads = 64; TotalMemoryBytes = 24L * Admission.bytesPerGiB }
      (int64 (liveHostCap threadRich) * peakBytesPerHost <= threadRich.TotalMemoryBytes - Admission.memoryReserveBytes)
      |> Expect.isTrue "hosts x peak fit what is left after the reserve"
      (liveHostCap threadRich < threadRich.UsableThreads) |> Expect.isTrue "memory, not threads, is what set it"

    testProperty "WHY — a bigger machine never lowers the cap: a cap that fell as the machine grew would be a modelling error" <|
      fun (threads: PositiveInt) (gib: PositiveInt) (moreThreads: PositiveInt) (moreGib: PositiveInt) ->
        let small = { UsableThreads = threads.Get; TotalMemoryBytes = int64 gib.Get * Admission.bytesPerGiB }
        let big = { UsableThreads = threads.Get + moreThreads.Get; TotalMemoryBytes = small.TotalMemoryBytes + int64 moreGib.Get * Admission.bytesPerGiB }
        liveHostCap big >= liveHostCap small && hostShardCap big >= hostShardCap small

    testProperty "WHY — the picked count never puts more live hosts on a machine than its cap, unless the floor says so" <|
      fun (threads: PositiveInt) (gib: PositiveInt) (weights: PositiveInt list) ->
        let machine = { UsableThreads = threads.Get; TotalMemoryBytes = int64 gib.Get * Admission.bytesPerGiB }
        let n = hostShardCount machine (weights |> List.mapi (fun i w -> sprintf "s%d" i, float w.Get) |> Map.ofList)
        n = minHostShards || n * hostsPerShard <= liveHostCap machine

    testProperty "WHY — scaling every weight by the same power of two changes no count: the picker reads the shape, not the unit" <|
      fun (weights: PositiveInt list) (exponent: PositiveInt) ->
        let suites k = weights |> List.mapi (fun i w -> sprintf "s%d" i, float w.Get * k) |> Map.ofList
        hostShardCount roomyMachine (suites 1.0) = hostShardCount roomyMachine (suites (2.0 ** float (exponent.Get % 5)))

    testProperty "WHY — a higher cap never lowers the count: the target only gets tighter, so the first count that meets it only moves up" <|
      fun (weights: PositiveInt list) (threads: PositiveInt) (moreThreads: PositiveInt) ->
        let suites = weights |> List.mapi (fun i w -> sprintf "s%d" i, float w.Get) |> Map.ofList
        let small = { thisMachine with UsableThreads = threads.Get }
        let big = { small with UsableThreads = threads.Get + moreThreads.Get }
        hostShardCount big suites >= hostShardCount small suites

    // ── the FSI host prebuild ──
    // A case builds the host for the SDK its project pins. Only the repo's pinned SDK was prebuilt, so a case on net10 paid
    // a cold host build (about 11 s, 24 CPU s) inside the case.
    testCase "WHY — every installed SDK gets a host prebuilt, the repo's pinned one first so the tiers' own SDK is ready earliest" <| fun _ ->
      hostPrebuildSdks "11.0.100-rc.1" [ "10.0.401"; "11.0.100-rc.1" ]
      |> Expect.equal "pinned first, then the rest" [ "11.0.100-rc.1"; "10.0.401" ]

    testCase "WHY — a pinned SDK that the listing did not show is still prebuilt: the pin is what the tiers ask for" <| fun _ ->
      hostPrebuildSdks "11.0.100" [ "10.0.401" ]
      |> Expect.equal "the pin, then the installed" [ "11.0.100"; "10.0.401" ]

    testProperty "WHY — the prebuild set is the pin and every installed SDK, each once: a missing one is a cold build in a case, a repeat is a wasted build" <|
      fun (pinned: NonEmptyString) (installed: NonEmptyString list) ->
        let installedVersions = installed |> List.map (fun v -> v.Get)
        let result = hostPrebuildSdks pinned.Get installedVersions
        List.head result = pinned.Get
        && result = List.distinct result
        && Set.ofList result = Set.ofList (pinned.Get :: installedVersions)

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
