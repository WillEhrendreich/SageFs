module SageFs.Tests.ReferenceCacheRetentionTests

/// `list_references` keeps the compiler's cache between asks so a second ask goes further. A project count does not bound
/// bytes (eight projects of the SageFs.Tests session held 4.5 GB), so whether the cache is kept is decided from the
/// worker's resident bytes, the time since the last ask and the machine's memory pressure. The decision is pure
/// (`ReferenceCacheRetention.decide`); the holder that applies it takes a clock, a residency reader and a pressure reader,
/// so these tests run with no compiler and no worker.
open System
open Expecto
open Expecto.Flip
open FsCheck
open SageFs
open SageFs.Features

let megabyte = 1048576L

let decide = ReferenceCacheRetention.decide

let pressureOf = ReferenceCacheRetention.pressureOf

let machine (totalMb: int64) (availableMb: int64) : MachineMemory.Stats =
  { TotalBytes = totalMb * megabyte; AvailableBytes = availableMb * megabyte }

/// A made-up duration for a made-up clock, named by its number of seconds.
let seconds (s: float) : TimeSpan = TimeSpan.FromSeconds s

/// The product's own retention, so a test takes a production duration from `Timeouts` and reads every other instant
/// relative to it.
let retentionSeconds = Timeouts.referencesCacheRetention.TotalSeconds

let policy : CachePolicy = { CeilingBytes = 3000L * megabyte; Retention = seconds retentionSeconds }

let reading (residentMb: int64) (idleSeconds: float) (pressure: MemoryPressure) : CacheReading =
  { ResidentBytes = residentMb * megabyte; IdleFor = seconds idleSeconds; Pressure = pressure }

let pressureOfInt (n: int) : MemoryPressure =
  match abs (n % 3) with
  | 0 -> MemoryPressure.Normal
  | 1 -> MemoryPressure.Tight
  | _ -> MemoryPressure.Critical

let isRelease (decision: CacheDecision) : bool =
  match decision with
  | CacheDecision.Release _ -> true
  | CacheDecision.Keep -> false

/// A holder over a fake heap: `cached` is what the fake compiler holds, `resident` is a baseline plus that.
type Fake() =
  member val Now = TimeSpan.Zero with get, set
  member val Pressure = MemoryPressure.Normal with get, set
  member val CachedMb = 0L with get, set
  member val Created = 0 with get, set
  member val Dropped = 0 with get, set
  member val Reasons : ReleaseReason list = [] with get, set

let holderOver (fake: Fake) (baselineMb: int64) : CacheHolder<int> =
  CacheHolder<int>(
    { Create =
        fun () ->
          fake.Created <- fake.Created + 1
          fake.Created
      Drop =
        fun _ ->
          fake.Dropped <- fake.Dropped + 1
          fake.CachedMb <- 0L
      ResidentBytes = fun () -> (baselineMb + fake.CachedMb) * megabyte
      Pressure = fun () -> fake.Pressure
      Now = fun () -> fake.Now
      Policy = policy
      Released = fun reason -> fake.Reasons <- fake.Reasons @ [ reason ] })

[<Tests>]
let tests =
  testList "reference cache retention" [

    testList "the decision" [
      testCase "WHY — a small worker, asked a moment ago, on a machine with room keeps its cache" <| fun _ ->
        decide policy (reading 1200L 5.0 MemoryPressure.Normal)
        |> Expect.equal "kept" CacheDecision.Keep

      testCase "WHY — a worker over the byte ceiling gives the cache up, because the project count bounds nothing about size" <| fun _ ->
        match decide policy (reading 4500L 5.0 MemoryPressure.Normal) with
        | CacheDecision.Release(ReleaseReason.OverResidentCeiling(resident, ceiling)) ->
          resident |> Expect.equal "the reading is named" (4500L * megabyte)
          ceiling |> Expect.equal "the ceiling is named" (3000L * megabyte)
        | other -> failtestf "expected the ceiling reason, got %A" other

      testCase "WHY — a cache nobody asked for within the retention is memory held for a question that may not return" <| fun _ ->
        match decide policy (reading 1200L (retentionSeconds + 1.0) MemoryPressure.Normal) with
        | CacheDecision.Release(ReleaseReason.IdlePastRetention(idle, retention)) ->
          idle |> Expect.equal "the idle time is named" (seconds (retentionSeconds + 1.0))
          retention |> Expect.equal "the retention is named" (seconds retentionSeconds)
        | other -> failtestf "expected the idle reason, got %A" other

      testCase "WHY — a machine short on memory takes the cache back even for a small, recent one" <| fun _ ->
        decide policy (reading 800L 1.0 MemoryPressure.Tight)
        |> Expect.equal "tight releases" (CacheDecision.Release(ReleaseReason.MemoryNotNormal MemoryPressure.Tight))
        decide policy (reading 800L 1.0 MemoryPressure.Critical)
        |> Expect.equal "critical releases" (CacheDecision.Release(ReleaseReason.MemoryNotNormal MemoryPressure.Critical))

      testCase "WHY — the retention itself is inside the window: only longer than it releases" <| fun _ ->
        decide policy (reading 1200L retentionSeconds MemoryPressure.Normal) |> Expect.equal "exactly the retention is kept" CacheDecision.Keep

      testCase "WHY — the ceiling itself is inside the window: only more than it releases" <| fun _ ->
        decide policy (reading 3000L 1.0 MemoryPressure.Normal) |> Expect.equal "exactly the ceiling is kept" CacheDecision.Keep

      testCase "WHY — when two causes hold at once the ceiling is named first, so the log says the bytes" <| fun _ ->
        match decide policy (reading 4500L (retentionSeconds * 10.0) MemoryPressure.Critical) with
        | CacheDecision.Release(ReleaseReason.OverResidentCeiling _) -> ()
        | other -> failtestf "expected the ceiling reason first, got %A" other

      testProperty "WHY — below the ceiling, within the retention and with the machine at ease, the cache is never released" <| fun (residentMb: uint16) (idleSeconds: uint16) ->
        let resident = min (int64 residentMb) 3000L
        let idle = min (float idleSeconds) retentionSeconds
        decide policy (reading resident idle MemoryPressure.Normal) = CacheDecision.Keep

      testProperty "WHY — above the ceiling the cache is released whatever the idle time and the pressure are" <| fun (extraMb: uint16) (idleSeconds: uint16) (p: int) ->
        let resident = 3001L + int64 extraMb
        isRelease (decide policy (reading resident (float idleSeconds) (pressureOfInt p)))

      testProperty "WHY — pressure that is not normal releases whatever the bytes and the idle time are" <| fun (residentMb: uint16) (idleSeconds: uint16) (p: int) ->
        match pressureOfInt p with
        | MemoryPressure.Normal -> true
        | MemoryPressure.Tight
        | MemoryPressure.Critical -> isRelease (decide policy (reading (int64 residentMb) (float idleSeconds) (pressureOfInt p)))

      testProperty "WHY — the same reading always gives the same answer, so a log line can be replayed" <| fun (residentMb: uint16) (idleSeconds: uint16) (p: int) ->
        let r = reading (int64 residentMb) (float idleSeconds) (pressureOfInt p)
        decide policy r = decide policy r
    ]

    testList "the pressure a worker reads for itself" [
      testCase "WHY — a machine with most of its memory free reads normal" <| fun _ ->
        pressureOf (machine 65536L 40960L)
        |> Expect.equal "normal" MemoryPressure.Normal

      testCase "WHY — a machine down to 15 percent reads tight, with the thresholds the daemon admits work by" <| fun _ ->
        pressureOf (machine 100000L 15000L)
        |> Expect.equal "tight" MemoryPressure.Tight

      testCase "WHY — a machine down to 5 percent reads critical" <| fun _ ->
        pressureOf (machine 100000L 5000L)
        |> Expect.equal "critical" MemoryPressure.Critical

      testCase "WHY — a reading of no memory at all is a failed read, and it does not make the cache vanish on every ask" <| fun _ ->
        pressureOf (machine 0L 0L) |> Expect.equal "normal" MemoryPressure.Normal
    ]

    testList "the holder that applies it" [
      testCase "WHY — the first ask builds a checker and the second, inside the retention, reuses it" <| fun _ ->
        let fake = Fake()
        let holder = holderOver fake 500L
        let first = holder.Acquire()
        fake.CachedMb <- 800L
        holder.Settle()
        fake.Now <- seconds 30.0
        let second = holder.Acquire()
        second |> Expect.equal "the same checker" first
        fake.Created |> Expect.equal "one checker was built" 1
        fake.Dropped |> Expect.equal "nothing was dropped" 0

      testCase "WHY — an answer that leaves the worker over the ceiling drops the cache at once, and the next ask builds a fresh checker" <| fun _ ->
        let fake = Fake()
        let holder = holderOver fake 500L
        holder.Acquire() |> ignore
        fake.CachedMb <- 3500L
        holder.Settle()
        fake.Dropped |> Expect.equal "dropped" 1
        fake.CachedMb |> Expect.equal "the heap is empty" 0L
        match fake.Reasons with
        | [ ReleaseReason.OverResidentCeiling _ ] -> ()
        | other -> failtestf "expected the ceiling reason, got %A" other
        holder.State |> Expect.equal "nothing is held" CacheState<int>.Empty
        let next = holder.Acquire()
        next |> Expect.equal "a second checker" 2

      testCase "WHY — a sweep after the retention drops a cache nobody asked for" <| fun _ ->
        let fake = Fake()
        let holder = holderOver fake 500L
        holder.Acquire() |> ignore
        fake.CachedMb <- 800L
        holder.Settle()
        fake.Now <- seconds (retentionSeconds + 1.0)
        holder.Sweep()
        fake.Dropped |> Expect.equal "dropped" 1
        match fake.Reasons with
        | [ ReleaseReason.IdlePastRetention _ ] -> ()
        | other -> failtestf "expected the idle reason, got %A" other

      testCase "WHY — a sweep inside the retention leaves the cache alone" <| fun _ ->
        let fake = Fake()
        let holder = holderOver fake 500L
        holder.Acquire() |> ignore
        fake.CachedMb <- 800L
        holder.Settle()
        fake.Now <- seconds (retentionSeconds / 6.0)
        holder.Sweep()
        fake.Dropped |> Expect.equal "kept" 0

      testCase "WHY — an ask that arrives after the retention gets a fresh checker, not a stale one" <| fun _ ->
        let fake = Fake()
        let holder = holderOver fake 500L
        holder.Acquire() |> ignore
        fake.CachedMb <- 800L
        holder.Settle()
        fake.Now <- seconds (retentionSeconds + 100.0)
        let next = holder.Acquire()
        next |> Expect.equal "a second checker" 2
        fake.Dropped |> Expect.equal "the stale one was dropped" 1

      testCase "WHY — a machine that tightens between two projects of one ask drops the cache before the next project" <| fun _ ->
        let fake = Fake()
        let holder = holderOver fake 500L
        holder.Acquire() |> ignore
        fake.CachedMb <- 600L
        fake.Pressure <- MemoryPressure.Tight
        let next = holder.Acquire()
        next |> Expect.equal "a fresh checker" 2
        fake.Reasons |> Expect.equal "named" [ ReleaseReason.MemoryNotNormal MemoryPressure.Tight ]

      testCase "WHY — sweeping or settling an empty holder drops nothing and builds nothing" <| fun _ ->
        let fake = Fake()
        let holder = holderOver fake 500L
        holder.Sweep()
        holder.Settle()
        fake.Created |> Expect.equal "built nothing" 0
        fake.Dropped |> Expect.equal "dropped nothing" 0
    ]
  ]

let gigabyte = 1024L * megabyte

let ceilingMb (totalGb: int64) : int64 =
  (ResidentCeiling.ceilingOf (totalGb * gigabyte)).Bytes / megabyte

let totalOfMb (mb: uint32) : int64 = (int64 mb + 1L) * megabyte

/// What `list_references` needs of the worker to answer the largest session measured (eight projects, SageFs.Tests): 4.0
/// to 4.3 GB resident after the first ask, 4.5 GB at the highest reading.
let eightProjectPeakBytes = 4608L * megabyte

[<Tests>]
let ceilingTests =
  testList "the resident ceiling a machine gets" [

    testList "examples at the machine sizes people run" [
      testCase "WHY — a 4 GB machine gets the floor, enough for one project and no more" <| fun _ ->
        ceilingMb 4L |> Expect.equal "floor" 1280L
        (ResidentCeiling.ceilingOf (4L * gigabyte)).Basis |> Expect.equal "named" CeilingBasis.FloorOfSmallMachine

      testCase "WHY — an 8 GB machine is still on the floor, because a share of it would not hold one project" <| fun _ ->
        ceilingMb 8L |> Expect.equal "floor" 1280L

      testCase "WHY — a 16 GB machine gives one worker's cache 15 percent, about 2.4 GB" <| fun _ ->
        ceilingMb 16L |> Expect.equal "15 percent of 16 GB" 2457L
        (ResidentCeiling.ceilingOf (16L * gigabyte)).Basis |> Expect.equal "named" CeilingBasis.ShareOfMemory

      testCase "WHY — a 32 GB machine holds the eight-project session whole, which is why the share is 15 percent and not 12" <| fun _ ->
        ceilingMb 32L |> Expect.equal "15 percent of 32 GB" 4915L
        ((ResidentCeiling.ceilingOf (32L * gigabyte)).Bytes >= eightProjectPeakBytes) |> Expect.isTrue "above the 4.5 GB peak"

      testCase "WHY — a 64 GB machine reaches the cap, 8 GB, and the eight-project session fits with room" <| fun _ ->
        ceilingMb 64L |> Expect.equal "cap" 8192L
        (ResidentCeiling.ceilingOf (64L * gigabyte)).Basis |> Expect.equal "named" CeilingBasis.CapOfLargeMachine
        ((ResidentCeiling.ceilingOf (64L * gigabyte)).Bytes >= eightProjectPeakBytes) |> Expect.isTrue "above the 4.5 GB peak"

      testCase "WHY — a 128 GB machine is held to the same cap, because one worker's cache has no use for a share of that much" <| fun _ ->
        ceilingMb 128L |> Expect.equal "cap" 8192L

      testCase "WHY — the machine this was measured on (62.7 GiB, as /proc/meminfo reports it) is not left on the flat 2,560 MB that answered 7 of 8" <| fun _ ->
        let measured = 67329937408L
        ((ResidentCeiling.ceilingOf measured).Bytes >= eightProjectPeakBytes) |> Expect.isTrue "holds the session"
    ]

    testList "the rule" [
      testProperty "WHY — more memory never gives a smaller ceiling" <| fun (a: uint32) (b: uint32) ->
        let low, high = (min a b), (max a b)
        (ResidentCeiling.ceilingOf (totalOfMb low)).Bytes <= (ResidentCeiling.ceilingOf (totalOfMb high)).Bytes

      testProperty "WHY — a readable machine never gets less than the floor, so a small one still caches a project" <| fun (mb: uint32) ->
        (ResidentCeiling.ceilingOf (totalOfMb mb)).Bytes >= ResidentCeiling.floorBytes

      testProperty "WHY — no machine gets more than the cap" <| fun (mb: uint32) ->
        (ResidentCeiling.ceilingOf (totalOfMb mb)).Bytes <= ResidentCeiling.capBytes

      testProperty "WHY — between the floor and the cap the ceiling is the share, exactly, and says so" <| fun (mb: uint32) ->
        let total = totalOfMb mb
        let share = total * ResidentCeiling.sharePercent / 100L
        let ceiling = ResidentCeiling.ceilingOf total
        match share >= ResidentCeiling.floorBytes && share <= ResidentCeiling.capBytes with
        | false -> true
        | true -> ceiling.Bytes = share && ceiling.Basis = CeilingBasis.ShareOfMemory

      testProperty "WHY — the ceiling is never more than the share unless the floor lifts it" <| fun (mb: uint32) ->
        let total = totalOfMb mb
        let ceiling = ResidentCeiling.ceilingOf total
        ceiling.Bytes <= total * ResidentCeiling.sharePercent / 100L || ceiling.Basis = CeilingBasis.FloorOfSmallMachine
    ]

    testList "memory that cannot be read" [
      testCase "WHY — zero total memory is a named case with the old 2,560 MB, never a silent zero ceiling that would drop every cache" <| fun _ ->
        let ceiling = ResidentCeiling.ceilingOf 0L
        ceiling.Bytes |> Expect.equal "the fallback" (2560L * megabyte)
        ceiling.Basis |> Expect.equal "named" CeilingBasis.MemoryUnreadable

      testProperty "WHY — a negative total is unreadable too" <| fun (n: NegativeInt) ->
        (ResidentCeiling.ceilingOf (int64 n.Get)).Basis = CeilingBasis.MemoryUnreadable
    ]

    testList "the operator's value" [
      testProperty "WHY — a positive megabyte count from SAGEFS_REFERENCES_RESIDENT_CEILING_MB wins on every machine" <| fun (mb: PositiveInt) (machineMb: uint32) ->
        let ceiling = ResidentCeiling.resolve (SageFsConfig.ResidentCeilingSetting.Megabytes mb.Get) (totalOfMb machineMb)
        ceiling.Bytes = int64 mb.Get * megabyte && ceiling.Basis = CeilingBasis.SetByOperator

      testCase "WHY — unset leaves the machine's own number" <| fun _ ->
        let total = 16L * gigabyte
        ResidentCeiling.resolve SageFsConfig.ResidentCeilingSetting.Unset total
        |> Expect.equal "same as the rule" (ResidentCeiling.ceilingOf total)

      testCase "WHY — a value that is not a positive integer falls back to the machine's number, and is kept to be warned about" <| fun _ ->
        let total = 16L * gigabyte
        ResidentCeiling.resolve (SageFsConfig.ResidentCeilingSetting.Rejected "lots") total
        |> Expect.equal "same as the rule" (ResidentCeiling.ceilingOf total)

      testCase "WHY — the setting reads a positive integer, and names anything else instead of guessing" <| fun _ ->
        SageFsConfig.ResidentCeilingSetting.parse "6144" |> Expect.equal "positive" (SageFsConfig.ResidentCeilingSetting.Megabytes 6144)
        SageFsConfig.ResidentCeilingSetting.parse null |> Expect.equal "null" SageFsConfig.ResidentCeilingSetting.Unset
        SageFsConfig.ResidentCeilingSetting.parse "" |> Expect.equal "empty" SageFsConfig.ResidentCeilingSetting.Unset
        for text in [ "0"; "-5"; "1.5"; "6 GB"; "lots" ] do
          SageFsConfig.ResidentCeilingSetting.parse text
          |> Expect.equal (sprintf "'%s' is rejected with its text" text) (SageFsConfig.ResidentCeilingSetting.Rejected text)

      testCase "WHY — every basis has its own words in the log line, so the log says where the number came from" <| fun _ ->
        let described =
          [ CeilingBasis.SetByOperator
            CeilingBasis.ShareOfMemory
            CeilingBasis.FloorOfSmallMachine
            CeilingBasis.CapOfLargeMachine
            CeilingBasis.MemoryUnreadable ]
          |> List.map (fun basis -> ResidentCeiling.describe { Bytes = 1L * gigabyte; Basis = basis })
        (List.distinct described).Length |> Expect.equal "five different lines" 5
    ]
  ]
