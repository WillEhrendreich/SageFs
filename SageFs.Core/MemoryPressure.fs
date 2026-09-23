namespace SageFs

open System
open SageFs.Features

/// The daemon's own graded read of how much room is left before it starves
/// the machine — ONE shared vocabulary for every subsystem that reacts to
/// memory pressure: `MemorySupervisor`'s session shedding (job 4) and
/// `ExpensiveWorkLease`'s admission throttling for session
/// create/warmup/rebuild (job 5). Neither subsystem derives its own
/// competing notion of "how bad is it" — they both read this.
///
/// Two incidents motivate the THREE-LEVEL shape specifically, not a bool:
/// a daemon eating the machine's memory needs a level between "fine" and
/// "refuse everything" so it can degrade gracefully (serialize expensive
/// work, shed idle sessions) before it ever needs to refuse outright.
[<RequireQualifiedAccess>]
type MemoryPressure =
  | Normal
  | Tight
  | Critical

module MemoryPressure =

  /// Exhaustive name for a pressure level — the one place this becomes text
  /// (wire payloads, log lines, MCP notifications, `get_fsi_status`).
  let describe =
    function
    | MemoryPressure.Normal -> "normal"
    | MemoryPressure.Tight -> "tight"
    | MemoryPressure.Critical -> "critical"

  /// A short, human sentence — what a client should actually DO. Used in
  /// refusal/queued messages so an agent is told to wait, not left to guess.
  let explain =
    function
    | MemoryPressure.Normal -> "the machine has room — no waiting needed"
    | MemoryPressure.Tight -> "the machine is short on memory — expensive work is serialized, one at a time"
    | MemoryPressure.Critical -> "the machine is almost out of memory — expensive work is queued until pressure eases"

  /// Total ordering, worst-last, so two readings can be combined with
  /// `worseOf` without a caller having to hand-write the comparison.
  let rank =
    function
    | MemoryPressure.Normal -> 0
    | MemoryPressure.Tight -> 1
    | MemoryPressure.Critical -> 2

  /// The more severe of two readings — used to combine "what the machine's
  /// raw available memory says" with "what the RSS anomaly detector says",
  /// since either one alone can under-report: the machine can be short on
  /// memory from OTHER processes while this daemon's own RSS still looks
  /// normal, and this daemon's RSS can be drifting/broken while the machine
  /// still has plenty of raw headroom (an early warning worth acting on
  /// before it becomes a machine-wide problem).
  let worseOf (a: MemoryPressure) (b: MemoryPressure) : MemoryPressure = if rank a >= rank b then a else b

  /// Fractions of machine memory available, with a THREE-WAY HYSTERESIS —
  /// same shape as `HealthAnomaly.step`'s breach/clear-with-a-dead-zone, and
  /// for the identical reason: a single threshold that available memory
  /// happens to hover around would flap the level every sample.
  type Thresholds = {
    /// At or below this fraction, pressure rises to (at least) Tight.
    TightEnterFrac: float
    /// At or above this fraction, pressure can fall back to Normal — must
    /// exceed `TightEnterFrac`.
    TightExitFrac: float
    /// At or below this fraction, pressure rises to Critical.
    CriticalEnterFrac: float
    /// At or above this fraction, pressure can fall back to Tight — must
    /// exceed `CriticalEnterFrac`.
    CriticalExitFrac: float
  }

  let defaultThresholds : Thresholds = {
    TightEnterFrac = 0.20
    TightExitFrac = 0.30
    CriticalEnterFrac = 0.08
    CriticalExitFrac = 0.15
  }

  /// The next level given the current one and a fresh `availableFrac`
  /// reading — pure, and the SAME function `MemorySupervisor.step` folds on
  /// every tick.
  let nextFromAvailableFrac (t: Thresholds) (current: MemoryPressure) (availableFrac: float) : MemoryPressure =
    match current with
    | MemoryPressure.Critical ->
      if availableFrac < t.CriticalExitFrac then MemoryPressure.Critical
      elif availableFrac < t.TightExitFrac then MemoryPressure.Tight
      else MemoryPressure.Normal
    | MemoryPressure.Tight ->
      if availableFrac <= t.CriticalEnterFrac then MemoryPressure.Critical
      elif availableFrac >= t.TightExitFrac then MemoryPressure.Normal
      else MemoryPressure.Tight
    | MemoryPressure.Normal ->
      if availableFrac <= t.CriticalEnterFrac then MemoryPressure.Critical
      elif availableFrac <= t.TightEnterFrac then MemoryPressure.Tight
      else MemoryPressure.Normal

  /// What the RSS anomaly detector alone would say: `Broken` is the "say so
  /// in your own status" case (Critical), `Drifting` is worth reacting to
  /// early (Tight), and anything else has nothing to add.
  let ofAnomalyVerdict (verdict: HealthAnomaly.Verdict) : MemoryPressure =
    match verdict with
    | HealthAnomaly.Verdict.Broken _ -> MemoryPressure.Critical
    | HealthAnomaly.Verdict.Drifting _ -> MemoryPressure.Tight
    | HealthAnomaly.Verdict.Normal
    | HealthAnomaly.Verdict.InsufficientHistory -> MemoryPressure.Normal

  /// The authoritative reading: the worse of (a) the machine's raw
  /// available-memory fraction, hysteresis-folded against the PREVIOUS
  /// level, and (b) what this daemon's own RSS anomaly verdict says right
  /// now. Called once per health tick; the result is what every consumer
  /// (`MemorySupervisor`, the expensive-work lease, `get_fsi_status`, MCP
  /// pressure notifications) reads.
  let derive (t: Thresholds) (current: MemoryPressure) (availableFrac: float) (rssVerdict: HealthAnomaly.Verdict) : MemoryPressure =
    worseOf (nextFromAvailableFrac t current availableFrac) (ofAnomalyVerdict rssVerdict)
