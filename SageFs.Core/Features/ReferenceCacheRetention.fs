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

[<RequireQualifiedAccess>]
module ReleaseReason =
  let describe (reason: ReleaseReason) : string =
    match reason with
    | ReleaseReason.OverResidentCeiling(resident, ceiling) ->
      sprintf "the worker held %d MB, over the %d MB ceiling" (resident / 1048576L) (ceiling / 1048576L)
    | ReleaseReason.IdlePastRetention(idle, retention) ->
      sprintf "no ask for %.0fs, past the %.0fs retention" idle.TotalSeconds retention.TotalSeconds
    | ReleaseReason.MemoryNotNormal pressure -> sprintf "memory pressure is %s" (MemoryPressure.describe pressure)

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
