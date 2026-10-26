namespace SageFs.Features

open System
open SageFs

/// Why the compiler cache behind `list_references` is dropped. A closed set, so the log line and a test name the same
/// three causes and a fourth cannot be added without every match saying what it does.
[<RequireQualifiedAccess>]
type ReleaseReason =
  /// The worker's resident bytes passed the ceiling. A project count bounds how many projects are held and says nothing
  /// about how big they are: eight projects of the SageFs.Tests session held 4.5 GB.
  | OverResidentCeiling of resident: int64 * ceiling: int64
  /// Nobody asked for longer than the retention, so the cache is memory held for a question that may never come back.
  | IdlePastRetention of idle: TimeSpan * retention: TimeSpan
  /// The machine is short on memory (the same three-level reading the daemon admits work by).
  | MemoryNotNormal of pressure: MemoryPressure

[<RequireQualifiedAccess>]
type CacheDecision =
  | Keep
  | Release of ReleaseReason

/// What the decision is made from.
type CacheReading =
  { /// The worker's resident set, in bytes.
    ResidentBytes: int64
    /// How long since the cache last answered an ask.
    IdleFor: TimeSpan
    Pressure: MemoryPressure }

/// The two numbers the decision compares against, both from `SageFsConfig` and `Timeouts`.
type CachePolicy =
  { CeilingBytes: int64
    Retention: TimeSpan }

/// Where a ceiling came from. A closed set, so the worker's log says whether the number was the operator's, a share of
/// the machine, a floor or a cap, and an unreadable machine is a case of its own and never a zero.
[<RequireQualifiedAccess>]
type CeilingBasis =
  /// `SAGEFS_REFERENCES_RESIDENT_CEILING_MB` was set to a positive integer.
  | SetByOperator
  | ShareOfMemory
  /// The share would not hold one project, so the machine gets the floor.
  | FloorOfSmallMachine
  /// The share is more than one worker's cache has a use for, so the machine gets the cap.
  | CapOfLargeMachine
  /// The machine's total memory could not be read.
  | MemoryUnreadable

/// The bytes the worker may hold before the cache is dropped, and why that many.
type ResidentCeiling = { Bytes: int64; Basis: CeilingBasis }

[<RequireQualifiedAccess>]
module ReleaseReason =
  let describe (reason: ReleaseReason) : string =
    match reason with
    | ReleaseReason.OverResidentCeiling(resident, ceiling) ->
      sprintf "the worker held %d MB, over the %d MB ceiling" (resident / 1048576L) (ceiling / 1048576L)
    | ReleaseReason.IdlePastRetention(idle, retention) ->
      sprintf "no ask for %.0fs, past the %.0fs retention" idle.TotalSeconds retention.TotalSeconds
    | ReleaseReason.MemoryNotNormal pressure -> sprintf "memory pressure is %s" (MemoryPressure.describe pressure)

/// The default ceiling is a function of the machine. Measured on 2026-10-25 on the SageFs.Tests session (eight projects),
/// a worker goes from 0.77 GB at Ready to 4.0 to 4.3 GB after the first ask (4.5 GB at the highest reading), and one
/// project is about 0.4 to 0.5 GB on top of the baseline. At a flat 2,560 MB the cache was dropped in the middle of the ask
/// and every ask answered `partial, 7 of 8`; at 6,144 MB it answered `project-wide, 8 of 8` and the second ask was warm.
/// A flat number is a guess about a machine it cannot see, so:
///   * the share is 15 percent of total memory: 4.5 GB over 15 percent is 30 GB, so a 32 GB machine and up answer that
///     session project-wide, and a 16 GB machine gives one worker's cache 2.4 GB;
///   * the floor is 1,280 MB: the worker's own 0.77 GB and about one project, so a small machine still keeps one project
///     warm between asks (the machine-pressure rule still drops it when the machine is short);
///   * the cap is 8,192 MB: the 4.5 GB peak with room to spare, and past it a bigger machine would only let a runaway
///     cache take more.
[<RequireQualifiedAccess>]
module ResidentCeiling =

  let bytesPerMegabyte : int64 = 1048576L

  /// Percent of the machine's total memory one worker's cache may take, before the floor and the cap.
  let sharePercent : int64 = 15L

  let floorBytes : int64 = 1280L * bytesPerMegabyte

  let capBytes : int64 = 8192L * bytesPerMegabyte

  /// What the ceiling was before it depended on the machine, and what a machine whose memory cannot be read gets.
  let unreadableBytes : int64 = 2560L * bytesPerMegabyte

  /// The ceiling for a machine with this much physical memory. Pure and monotone in the total. A total that is zero or
  /// negative is `MemoryUnreadable` with the old flat number.
  let ceilingOf (totalBytes: int64) : ResidentCeiling =
    match totalBytes > 0L with
    | false -> { Bytes = unreadableBytes; Basis = CeilingBasis.MemoryUnreadable }
    | true ->
      match totalBytes * sharePercent / 100L with
      | share when share < floorBytes -> { Bytes = floorBytes; Basis = CeilingBasis.FloorOfSmallMachine }
      | share when share > capBytes -> { Bytes = capBytes; Basis = CeilingBasis.CapOfLargeMachine }
      | share -> { Bytes = share; Basis = CeilingBasis.ShareOfMemory }

  /// The operator's megabytes when one was set, otherwise the machine's own number.
  let resolve (setting: SageFsConfig.ResidentCeilingSetting) (totalBytes: int64) : ResidentCeiling =
    match setting with
    | SageFsConfig.ResidentCeilingSetting.Megabytes megabytes ->
      { Bytes = int64 megabytes * bytesPerMegabyte; Basis = CeilingBasis.SetByOperator }
    | SageFsConfig.ResidentCeilingSetting.Unset
    | SageFsConfig.ResidentCeilingSetting.Rejected _ -> ceilingOf totalBytes

  /// One log line: the number and where it came from.
  let describe (ceiling: ResidentCeiling) : string =
    let megabytes = ceiling.Bytes / bytesPerMegabyte
    match ceiling.Basis with
    | CeilingBasis.SetByOperator -> sprintf "%d MB, set by SAGEFS_REFERENCES_RESIDENT_CEILING_MB" megabytes
    | CeilingBasis.ShareOfMemory -> sprintf "%d MB, %d percent of the machine's memory" megabytes sharePercent
    | CeilingBasis.FloorOfSmallMachine -> sprintf "%d MB, the floor: %d percent of this machine's memory would hold less" megabytes sharePercent
    | CeilingBasis.CapOfLargeMachine -> sprintf "%d MB, the cap: %d percent of this machine's memory is more than one worker's cache needs" megabytes sharePercent
    | CeilingBasis.MemoryUnreadable -> sprintf "%d MB, the machine's memory could not be read" megabytes

[<RequireQualifiedAccess>]
module ReferenceCacheRetention =

  /// Whether the cache is kept. The causes are tried in order of how much harm waiting does: bytes over the ceiling
  /// first (the hazard this exists for), then a machine short on memory, then idleness. Pure: the same reading and
  /// policy always give the same answer.
  let decide (policy: CachePolicy) (reading: CacheReading) : CacheDecision =
    match reading.ResidentBytes > policy.CeilingBytes, reading.Pressure, reading.IdleFor > policy.Retention with
    | true, _, _ -> CacheDecision.Release(ReleaseReason.OverResidentCeiling(reading.ResidentBytes, policy.CeilingBytes))
    | false, (MemoryPressure.Tight | MemoryPressure.Critical), _ ->
      CacheDecision.Release(ReleaseReason.MemoryNotNormal reading.Pressure)
    | false, MemoryPressure.Normal, true ->
      CacheDecision.Release(ReleaseReason.IdlePastRetention(reading.IdleFor, policy.Retention))
    | false, MemoryPressure.Normal, false -> CacheDecision.Keep

  /// The pressure a worker can read for itself. The daemon's level is hysteresis-folded across health ticks and lives in
  /// another process, so the worker reads the machine and applies the same thresholds from `Normal`, which is the level
  /// the daemon itself starts every fold from.
  let pressureOf (stats: MachineMemory.Stats) : MemoryPressure =
    match stats.TotalBytes with
    | 0L -> MemoryPressure.Normal
    | total ->
      MemoryPressure.nextFromAvailableFrac
        MemoryPressure.defaultThresholds
        MemoryPressure.Normal
        (float stats.AvailableBytes / float total)

/// Whether an ask is using the checker now. A sweep never drops a checker in the middle of a check.
[<RequireQualifiedAccess>]
type CacheUse =
  | Checking
  | Resting

/// Whether the holder has something to drop.
[<RequireQualifiedAccess>]
type CacheState<'checker> =
  | Empty
  | Held of checker: 'checker * lastUsed: TimeSpan * usage: CacheUse

/// Everything the holder touches outside itself, so a simulation can run the real holder on a simulated clock and a
/// simulated heap.
type CacheEnv<'checker> =
  { Create: unit -> 'checker
    /// Frees what the checker cached. The holder forgets its reference right after.
    Drop: 'checker -> unit
    ResidentBytes: unit -> int64
    Pressure: unit -> MemoryPressure
    Now: unit -> TimeSpan
    Policy: CachePolicy
    /// Told every release, with the reason, for the log.
    Released: ReleaseReason -> unit }

/// Holds the compiler's checker between asks and gives it up when `ReferenceCacheRetention.decide` says so. Asked at
/// three moments: `Acquire` before each project check, `Settle` after each answer, and `Sweep` by a timer while nobody
/// asks. The next `Acquire` after a release builds a fresh checker, so a release costs the next ask its warm cache and
/// nothing else.
type CacheHolder<'checker>(env: CacheEnv<'checker>) =
  let gate = obj ()
  let mutable state = CacheState<'checker>.Empty

  let reading (idle: TimeSpan) : CacheReading =
    { ResidentBytes = env.ResidentBytes(); IdleFor = idle; Pressure = env.Pressure() }

  /// Called under the gate. Drops a held checker when the decision says release; an empty holder has nothing to drop.
  let consult (idle: TimeSpan) : unit =
    match state with
    | CacheState.Empty -> ()
    | CacheState.Held(checker, _, _) ->
      match ReferenceCacheRetention.decide env.Policy (reading idle) with
      | CacheDecision.Keep -> ()
      | CacheDecision.Release reason ->
        state <- CacheState.Empty
        env.Drop checker
        env.Released reason

  let idleOf (lastUsed: TimeSpan) : TimeSpan = env.Now() - lastUsed

  /// The checker for the next project check, after asking whether the one held should go.
  member _.Acquire() : 'checker =
    lock gate (fun () ->
      (match state with
       | CacheState.Empty -> ()
       | CacheState.Held(_, lastUsed, _) -> consult (idleOf lastUsed))
      match state with
      | CacheState.Held(checker, _, _) ->
        state <- CacheState.Held(checker, env.Now(), CacheUse.Checking)
        checker
      | CacheState.Empty ->
        let checker = env.Create()
        state <- CacheState.Held(checker, env.Now(), CacheUse.Checking)
        checker)

  /// After an answer: the cache was just used, so only the ceiling and the pressure can release it.
  member _.Settle() : unit =
    lock gate (fun () ->
      match state with
      | CacheState.Empty -> ()
      | CacheState.Held(checker, _, _) ->
        state <- CacheState.Held(checker, env.Now(), CacheUse.Resting)
        consult TimeSpan.Zero)

  /// While nobody asks: the same decision, with the real idle time. A checker in the middle of a check is left alone.
  member _.Sweep() : unit =
    lock gate (fun () ->
      match state with
      | CacheState.Empty -> ()
      | CacheState.Held(_, _, CacheUse.Checking) -> ()
      | CacheState.Held(_, lastUsed, CacheUse.Resting) -> consult (idleOf lastUsed))

  /// What the holder holds now, as the state and not a flag.
  member _.State : CacheState<'checker> = lock gate (fun () -> state)
