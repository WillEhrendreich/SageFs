module SageFs.Tests.FootprintPressureTests

open Expecto
open Expecto.Flip
open FsCheck
open SageFs
open SageFs.Features
open SageFs.MemorySupervisor

/// On 2026-11-07 `/health` said `memoryMB: 639` and `memoryPressure: "normal"` for a daemon that held 25 GB of managed heap,
/// 20 GB of it swapped out, and failed every session creation with OutOfMemoryException. The numbers below are that
/// daemon's, from its `/proc/<pid>/smaps_rollup` and the dump's `eeheap -gc`.

let private gib (n: int64) = n * 1_073_741_824L

/// The incident, as `/proc/self/smaps_rollup` printed it.
let private incidentSmaps =
  [| "7ee01bb14000-7ffd5d8ef000 ---p 00000000 00:00 0                      [rollup]"
     "Rss:              655668 kB"
     "Pss:              587716 kB"
     "Swap:           20323840 kB"
     "SwapPss:        20289812 kB" |]

let private incidentFootprint : MachineMemory.Footprint =
  { ResidentBytes = 655_668L * 1024L
    SwappedBytes = 20_323_840L * 1024L
    HeapCommittedBytes = 25_454_010_368L
    // 75% of the user slice's 32 GiB memory.max, which is the limit the GC derived.
    HeapLimitBytes = 25_769_803_776L }

let private machineTotal = 65_751_896L * 1024L

[<Tests>]
let tests =
  testList "the daemon's own footprint is what its health says" [

    testCase "WHY — smaps_rollup parses to resident and swapped bytes" <| fun _ ->
      MachineMemory.parseSmapsRollup incidentSmaps
      |> Expect.equal "Rss and Swap in bytes" (Some(655_668L * 1024L, 20_323_840L * 1024L))

    testCase "a kernel that prints no Swap line reads as nothing swapped" <| fun _ ->
      MachineMemory.parseSmapsRollup [| "Rss:              1024 kB" |]
      |> Expect.equal "Rss only" (Some(1024L * 1024L, 0L))

    testCase "no Rss line is no reading" <| fun _ ->
      MachineMemory.parseSmapsRollup [| "Swap: 1 kB" |] |> Expect.isNone "refuses to guess a resident size"

    testCase "WHY — the incident's footprint is Critical, though 18 GB of the machine was free" <| fun _ ->
      MemoryPressure.ofFootprint MemoryPressure.defaultFootprintThresholds incidentFootprint machineTotal
      |> Expect.equal "heap at 98.8% of its limit and 31% of the machine in swap" MemoryPressure.Critical

    testCase "the held size is resident plus swapped" <| fun _ ->
      MachineMemory.heldBytes incidentFootprint / 1_048_576L
      |> Expect.equal "20,487 MB (resident 640 plus swapped 19,847), not the 640 MB the resident size alone read" 20_487L

    testCase "a small footprint is Normal" <| fun _ ->
      let small : MachineMemory.Footprint =
        { ResidentBytes = gib 1L; SwappedBytes = 0L; HeapCommittedBytes = gib 1L; HeapLimitBytes = gib 24L }
      MemoryPressure.ofFootprint MemoryPressure.defaultFootprintThresholds small machineTotal
      |> Expect.equal "nothing to report" MemoryPressure.Normal

    testCase "a heap near its limit alone is Tight, then Critical" <| fun _ ->
      let at (committed: int64) : MachineMemory.Footprint =
        { ResidentBytes = committed; SwappedBytes = 0L; HeapCommittedBytes = committed; HeapLimitBytes = gib 24L }
      [ MemoryPressure.ofFootprint MemoryPressure.defaultFootprintThresholds (at (gib 19L)) machineTotal
        MemoryPressure.ofFootprint MemoryPressure.defaultFootprintThresholds (at (gib 22L)) machineTotal ]
      |> Expect.equal "79% is Tight, 92% is Critical" [ MemoryPressure.Tight; MemoryPressure.Critical ]

    testCase "an unknown limit or machine size reads as no information" <| fun _ ->
      let unknown : MachineMemory.Footprint =
        { ResidentBytes = gib 8L; SwappedBytes = 0L; HeapCommittedBytes = gib 8L; HeapLimitBytes = 0L }
      MemoryPressure.ofFootprint MemoryPressure.defaultFootprintThresholds unknown 0L
      |> Expect.equal "no division by zero, no false alarm" MemoryPressure.Normal

    testCase "the supervisor refuses new sessions on the footprint's floor, naming the daemon's own numbers" <| fun _ ->
      let machine : MachineStats =
        { DaemonRssBytes = MachineMemory.heldBytes incidentFootprint
          MachineAvailableBytes = gib 18L
          MachineTotalBytes = machineTotal }
      let why = MemoryPressure.describeFootprint incidentFootprint machineTotal
      let decision = stepWithFloor defaultThresholds MemoryPressure.Critical why MemoryPressure.Normal machine []
      decision.Level |> Expect.equal "critical although the machine has room" MemoryPressure.Critical
      decision.Actions
      |> List.tryPick (function ShedAction.RefuseNewSessions reason -> Some reason | _ -> None)
      |> Option.exists (fun reason -> reason.Contains "swapped")
      |> Expect.isTrue "a session create is refused with the swapped size in the reason"

    testCase "step without a floor is unchanged: the machine alone decides" <| fun _ ->
      let machine : MachineStats =
        { DaemonRssBytes = gib 1L; MachineAvailableBytes = gib 18L; MachineTotalBytes = machineTotal }
      (step defaultThresholds MemoryPressure.Normal machine []).Level
      |> Expect.equal "18 of 62 GB free is Normal" MemoryPressure.Normal

    testCase "WHY — the level every status surface reads is never better than the footprint says" <| fun _ ->
      let previous = MemoryPressureWatch.footprintProbe
      try
        MemoryPressureWatch.reset ()
        MemoryPressureWatch.footprintProbe <- fun () -> incidentFootprint, machineTotal
        MemoryPressureWatch.currentLevel () |> Expect.equal "Critical with no sweep having run" MemoryPressure.Critical
        MemoryPressure.explainWith (MemoryPressureWatch.currentLevel ()) (MemoryPressureWatch.currentNote ())
        |> fun note -> note.Contains "MB swapped" |> Expect.isTrue "the note says how much is swapped"
      finally
        MemoryPressureWatch.footprintProbe <- previous
        MemoryPressureWatch.reset ()

    testProperty "the floor is monotone: more swap or more heap never lowers the level" <| fun (heap: uint16) (swap: uint16) (extra: uint16) ->
      let footprint (h: int) (s: int) : MachineMemory.Footprint =
        { ResidentBytes = 0L; SwappedBytes = int64 s * gib 30L / 65_535L
          HeapCommittedBytes = int64 h * gib 24L / 65_535L; HeapLimitBytes = gib 24L }
      let level f = MemoryPressure.ofFootprint MemoryPressure.defaultFootprintThresholds f machineTotal |> MemoryPressure.rank
      level (footprint (int heap + int extra) (int swap + int extra)) >= level (footprint (int heap) (int swap))
  ]

