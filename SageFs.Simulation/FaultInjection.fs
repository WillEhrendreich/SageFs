namespace SageFs.Simulation

open System
open SageFs.Simulation.Scenario

/// Phase 3C DST (Brief B8): reusable, PURE fault-injection combinators that
/// transform an existing scenario's event/command stream to inject
/// transport/timing chaos — clock skew, message reorder, drop, and
/// duplicate — composing with the shipped generators (Generators/
/// MgrGenerators/ManifestGenerators), the shrinker (Shrink, Brief B2), and
/// the manifest sim (ManifestSim, Brief B5).
///
/// Same design principles as the rest of the harness:
///   * Chaos is DATA, and now so is the FAULT: every transform is a pure,
///     deterministic function of (seed, input) — same inputs, same output,
///     forever. No transform calls `DateTime.Now` or ambient randomness.
///   * The transforms are GENERIC over the element type so they compose with
///     every scenario family's own command/event list
///     (`Scenario.SimEvent list`, `WorkerLifecycleSim.MgrCommand list`,
///     `ManifestSim.OwnerCmd list`) without this module needing to know
///     about any of them individually — e.g.
///     `Generators.fromSeed >> FaultInjection.reorderWithin 3 seed`.
///   * The one scenario-specific combinator, `withClockSkew`, targets
///     `Scenario.ClockAdvance` spans directly (including zero and negative
///     spans — a non-monotonic simulated clock) because "skew" is meaningful
///     only for the one event carrying a time delta.
///   * A robustness property that FAILS under a fault combinator is a
///     genuine finding, not a harness bug — see `DstFaultInjectionTests.fs`.
module FaultInjection =

  /// Bounded, deterministic reordering: the list is chunked into consecutive
  /// windows of at most `window` elements, and each chunk is independently
  /// Fisher-Yates shuffled using a single seeded `Random` stream consumed in
  /// a fixed left-to-right order. Models bounded out-of-order delivery (a
  /// message can arrive early/late relative to its neighbours, but never
  /// migrate arbitrarily far in the stream): no element can move outside the
  /// `window`-sized chunk it started in. `window <= 1` (or an empty/singleton
  /// list) is a no-op — there is nothing to reorder within a window of one.
  let reorderWithin (window: int) (seed: int) (xs: 'a list) : 'a list =
    if window <= 1 || List.isEmpty xs then
      xs
    else
      let rnd = Random(seed)
      let arr = List.toArray xs
      let n = arr.Length
      let mutable chunkStart = 0
      while chunkStart < n do
        let chunkEnd = min n (chunkStart + window) // exclusive
        // In-place Fisher-Yates over arr[chunkStart .. chunkEnd-1].
        for j in (chunkEnd - 1) .. -1 .. (chunkStart + 1) do
          let k = chunkStart + rnd.Next(j - chunkStart + 1)
          let tmp = arr.[j]
          arr.[j] <- arr.[k]
          arr.[k] <- tmp
        chunkStart <- chunkEnd
      List.ofArray arr

  /// Drop each element independently with probability `rate` (clamped to
  /// [0.0, 1.0]), using a single seeded `Random` stream consumed in a fixed
  /// left-to-right order — models lossy at-most-once delivery.
  let drop (rate: float) (seed: int) (xs: 'a list) : 'a list =
    let clamped = rate |> max 0.0 |> min 1.0
    let rnd = Random(seed)
    xs |> List.filter (fun _ -> rnd.NextDouble() >= clamped)

  /// Duplicate each element independently with probability `rate` (clamped
  /// to [0.0, 1.0]) — the duplicate is inserted immediately after the
  /// original, using a single seeded `Random` stream consumed in a fixed
  /// left-to-right order. Models at-least-once delivery / straggler
  /// amplification: a retried/re-delivered message arriving twice in a row.
  let duplicate (rate: float) (seed: int) (xs: 'a list) : 'a list =
    let clamped = rate |> max 0.0 |> min 1.0
    let rnd = Random(seed)
    xs
    |> List.collect (fun x ->
      match rnd.NextDouble() < clamped with
      | true -> [ x; x ]
      | false -> [ x ])

  /// Perturb every `ClockAdvance` span in a `Scenario`, deterministically
  /// from `seed`, into one of four skew regimes chosen per event: zeroed (no
  /// time passes at all), NEGATIVE (the simulated clock runs backward — the
  /// real robustness question the brief asks: how does
  /// `RestartPolicy.decide` behave when `now < WindowStart`/`LastRestartAt`?
  /// Answer, read from `RestartPolicy.decide`, SageFs.Core/RestartPolicy.fs:
  /// both window checks are `(now - X) <= threshold`/`> threshold` — a
  /// negative delta is simply "very much within" every window, so a
  /// non-monotonic clock never throws and never desyncs the decision, it
  /// just always looks like a rapid/startup crash), unchanged, or doubled
  /// (an exaggerated forward skew). Non-`ClockAdvance` events pass through
  /// untouched. `withClockSkew` never mutates `Policy`/`StartTime`/`Seed` —
  /// only the event stream's time deltas.
  let withClockSkew (seed: int) (scn: Scenario) : Scenario =
    let rnd = Random(seed)
    let perturb (span: TimeSpan) : TimeSpan =
      match rnd.Next(0, 4) with
      | 0 -> TimeSpan.Zero
      | 1 -> TimeSpan.FromMilliseconds(-(abs span.TotalMilliseconds)) // clock skews backward
      | 2 -> span // unchanged
      | _ -> TimeSpan.FromMilliseconds(span.TotalMilliseconds * 2.0) // exaggerated forward skew
    let events =
      scn.Events
      |> List.map (function
        | SimEvent.ClockAdvance span -> SimEvent.ClockAdvance(perturb span)
        | other -> other)
    { scn with Events = events }
