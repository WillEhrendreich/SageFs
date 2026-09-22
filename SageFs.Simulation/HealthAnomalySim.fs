namespace SageFs.Simulation

open System
open SageFs.Features
open SageFs.Features.HealthAnomaly

/// Deterministic Simulation Testing for `HealthAnomaly`: a seeded sequence of
/// named series shapes (flat, step, drift, spike, sawtooth, recovery, and a
/// legitimate scale change) folded through the REAL `HealthAnomaly.step`,
/// same rules as the other DST harnesses here — chaos is data (a seeded
/// shape list), the real decision function is the subject, and a twin (a
/// detector that always says "fine") shows the invariants actually have
/// teeth: they FAIL against the twin, which is the whole point of running
/// them against it.
module HealthAnomalySim =

  [<RequireQualifiedAccess>]
  type ShapeEvent =
    /// `length` samples at the current baseline, with noise.
    | Flat of length: int
    /// A sustained jump: the baseline is multiplied by `factor` for
    /// `length` samples, and stays there (it's the new baseline) until a
    /// later `Recovery` reverts it.
    | Step of length: int * factor: float
    /// A slow, sustained drift: the baseline moves by `ratePerSample`
    /// (a fraction of itself) every sample, for `length` samples, and the
    /// drifted value becomes the new baseline afterwards.
    | Drift of length: int * ratePerSample: float
    /// One sample at `baseline * factor`, surrounded by whatever comes
    /// before/after. The baseline itself is untouched.
    | Spike of factor: float
    /// A periodic oscillation around the current baseline, `length`
    /// samples, repeating every `period` samples, swinging by
    /// `amplitudeFrac` of the baseline. The baseline itself is untouched.
    | Sawtooth of length: int * period: int * amplitudeFrac: float
    /// `length` samples reverting to the baseline that was in effect
    /// before the most recent `Step`/`Drift` (a real recovery, not a new
    /// segment) — models "the incident ended and things went back to how
    /// they were".

    | Recovery of length: int

  type Scenario = { Seed: int; NoiseFrac: float; InitialBaseline: float; Events: ShapeEvent list }

  /// Which decision function produces the verdict.
  [<RequireQualifiedAccess>]
  type DetectorBehavior =
    | Real
    /// TWIN: never fires, no matter what it sees. Exists so an invariant
    /// that claims "the real detector always does X" can be shown to
    /// actually depend on the real detector — it must fail against this.
    | AlwaysFineTwin

  type State = {
    Rng: Random
    Step: int
    Baseline: float
    PriorBaseline: float
    NoiseFrac: float
    Detector: HealthAnomaly.State
    /// Every (observation, verdict) pair produced so far, oldest first.
    History: (Observation * Verdict) list
  }

  let private epoch = DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)
  let private atSec (n: int) = epoch.AddSeconds(float n)

  let initial (seed: int) (noiseFrac: float) (initialBaseline: float) : State = {
    Rng = Random seed
    Step = 0
    Baseline = initialBaseline
    PriorBaseline = initialBaseline
    NoiseFrac = noiseFrac
    Detector = HealthAnomaly.initial
    History = []
  }

  let private signal = SignalId.Custom "dst-signal"

  let private noisyValue (s: State) (baseline: float) =
    let amp = baseline * s.NoiseFrac
    baseline + (s.Rng.NextDouble() - 0.5) * 2.0 * amp

  let private decide (behavior: DetectorBehavior) (detector: HealthAnomaly.State) (obs: Observation) =
    match behavior with
    | DetectorBehavior.Real -> HealthAnomaly.step HealthAnomaly.defaultParams signal obs detector
    | DetectorBehavior.AlwaysFineTwin -> detector, Verdict.Normal

  /// Fold `n` observations at `baselineAt step` (a function of the sample's
  /// index within this segment, so `Step`/`Drift` can move the value while
  /// `Flat`/`Sawtooth` keep it constant), appending each to `History`.
  let private foldSamples (behavior: DetectorBehavior) (n: int) (baselineAt: int -> float) (s: State) : State =
    [ 0 .. n - 1 ]
    |> List.fold
      (fun (st: State) i ->
        let value = noisyValue st (baselineAt i)
        let obs = { At = atSec st.Step; Value = value }
        let detector', verdict = decide behavior st.Detector obs
        { st with
            Step = st.Step + 1
            Detector = detector'
            History = st.History @ [ obs, verdict ] })
      s

  let step (behavior: DetectorBehavior) (s: State) (ev: ShapeEvent) : State =
    match ev with
    | ShapeEvent.Flat length -> foldSamples behavior length (fun _ -> s.Baseline) s
    | ShapeEvent.Step(length, factor) ->
      let newBaseline = s.Baseline * factor
      let s' = foldSamples behavior length (fun _ -> newBaseline) s
      { s' with Baseline = newBaseline; PriorBaseline = s.Baseline }
    | ShapeEvent.Drift(length, ratePerSample) ->
      let baselineAt i = s.Baseline * (1.0 + ratePerSample * float i)
      let s' = foldSamples behavior length baselineAt s
      { s' with Baseline = baselineAt (length - 1); PriorBaseline = s.Baseline }
    | ShapeEvent.Spike factor -> foldSamples behavior 1 (fun _ -> s.Baseline * factor) s
    | ShapeEvent.Sawtooth(length, period, amplitudeFrac) ->
      let amp = s.Baseline * amplitudeFrac
      let baselineAt i = s.Baseline + amp * float (i % (max 1 period))
      foldSamples behavior length baselineAt s
    | ShapeEvent.Recovery length ->
      let s' = foldSamples behavior length (fun _ -> s.PriorBaseline) s
      { s' with Baseline = s.PriorBaseline }

  /// Every state, oldest first, including the initial one — mirrors the
  /// other DST harnesses' `trace` shape so invariants can look at any point
  /// in the run.
  let trace (behavior: DetectorBehavior) (scenario: Scenario) : State list =
    scenario.Events
    |> List.scan (step behavior) (initial scenario.Seed scenario.NoiseFrac scenario.InitialBaseline)

  /// A pure function of `seed`: replaying a seed gives the identical
  /// scenario. Sweeps every shape at least once, plus a legitimate scale
  /// change (a `Step` that is never `Recovery`-ed) so a run of seeds
  /// reliably exercises every shape the module is meant to handle.
  let scenarioOf (seed: int) : Scenario =
    let rng = Random seed
    let noiseFrac = 0.02 + rng.NextDouble() * 0.08 // 2%..10% noise
    let baseline = 1.0 + rng.NextDouble() * 9999.0 // scale-free: works at any magnitude
    let stepFactor = 3.0 + rng.NextDouble() * 12.0 // 3x..15x — a real sustained incident
    // Scaled to the noise band itself, not a fixed fraction of baseline: a
    // drift is "slow" relative to THIS signal's own noise, not in absolute
    // terms — see HealthAnomaly.fs's module doc comment on why an
    // absolute-rate drift can be indistinguishable from a step once it's
    // faster than what the baseline's own noise already contains.
    let driftRate = noiseFrac * (0.25 + rng.NextDouble() * 0.3)
    let events = [
      ShapeEvent.Flat 40
      ShapeEvent.Sawtooth(60, 10 + rng.Next 10, 0.15)
      // Generous settle time after the sawtooth: HealthAnomaly.fs's module
      // doc comment documents that a periodic swing wider than what warmup
      // saw can trip transient false positives per cycle until the learned
      // variance widens to the whole cycle — this is where that settles.
      ShapeEvent.Flat 60
      ShapeEvent.Step(30, stepFactor)
      ShapeEvent.Recovery 300
      ShapeEvent.Flat 30
      ShapeEvent.Drift(150, driftRate)
      // A drift that is still actively breaching when its segment ends
      // needs real time to settle at the level it drifted to — same reason
      // as the sawtooth settle buffer above.
      ShapeEvent.Flat 150
      ShapeEvent.Spike(50.0 + rng.NextDouble() * 200.0)
      ShapeEvent.Flat 40
      // A legitimate scale change: a step that is never recovered from —
      // the new level IS the new normal from here on.
      ShapeEvent.Step(200, 2.0 + rng.NextDouble() * 3.0)
    ]
    { Seed = seed; NoiseFrac = noiseFrac; InitialBaseline = baseline; Events = events }
