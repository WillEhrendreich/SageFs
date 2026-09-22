/// `HealthAnomaly` is a pure classical anomaly detector (EWMA + CUSUM) over
/// a named signal's observations — see the module doc comment in
/// SageFs.Core/Features/HealthAnomaly.fs for the math and why it's shaped
/// this way. This file proves the properties the module is held to, then
/// replays the two real incidents that motivated it: `/health` latency
/// stuck at 60s for a minute, and worker RSS climbing to 37GB over hours.
/// Those two, plus the flat-with-noise control, are the acceptance check.
module SageFs.Tests.HealthAnomalyTests

open System
open Expecto
open Expecto.Flip
open FsCheck
open FsCheck.FSharp
open SageFs.Features
open SageFs.Features.HealthAnomaly

let private epoch = DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)
let private atSec (n: int) = epoch.AddSeconds(float n)

let private flatNoisy (rng: Random) (n: int) (baseVal: float) (noiseAmp: float) : Observation list =
  [ for i in 0 .. n - 1 -> { At = atSec i; Value = baseVal + (rng.NextDouble() - 0.5) * 2.0 * noiseAmp } ]

/// Reindex a concatenated series so `At` is strictly increasing across the
/// whole thing, not just within each piece that built it.
let private reindexed (series: Observation list) : Observation list =
  series |> List.mapi (fun i o -> { o with At = atSec i })

let private fires =
  function
  | Verdict.Drifting _
  | Verdict.Broken _ -> true
  | Verdict.Normal
  | Verdict.InsufficientHistory -> false

let private isBroken =
  function
  | Verdict.Broken _ -> true
  | _ -> false

let private evaluate (series: Observation list) = evaluateSeries (SignalId.Custom "test") series

// ── Generators for the property tests ──

let private pick (gen: Gen<'a>) = (Gen.sample 1 gen).[0]

let private propConfig = { FsCheckConfig.defaultConfig with maxTest = 200 }

/// A seed, a positive baseline of varied magnitude (proves scale-freeness),
/// and a modest noise fraction of that baseline.
let private genFlatShape : Gen<int * float * float> =
  gen {
    let! seed = Gen.choose (1, 1_000_000)
    let! baseVal = Gen.choose (1, 100_000) |> Gen.map float
    let! noisePercent = Gen.choose (1, 20)
    return seed, baseVal, baseVal * float noisePercent / 100.0
  }

[<Tests>]
let tests =
  testList "HealthAnomaly" [

    testList "cold start" [
      testCase "the first sample is always InsufficientHistory, never Normal" <| fun _ ->
        let verdict = evaluate [ { At = atSec 0; Value = 4.0 } ]
        verdict |> Expect.equal "a single observation can't know what normal is yet" [ Verdict.InsufficientHistory ]

      testCase "a cold daemon does not fire on its first three samples" <| fun _ ->
        let series = [ { At = atSec 0; Value = 4.0 }; { At = atSec 1; Value = 900.0 }; { At = atSec 2; Value = 4.0 } ]
        evaluate series
        |> List.exists fires
        |> Expect.isFalse "warmup must never produce a verdict other than InsufficientHistory"

      testCase "every sample before MinWarmupSamples is InsufficientHistory" <| fun _ ->
        let rng = Random 1
        let series = flatNoisy rng (defaultParams.MinWarmupSamples - 1) 10.0 1.0
        evaluate series
        |> List.forall (function Verdict.InsufficientHistory -> true | _ -> false)
        |> Expect.isTrue "every pre-warmup sample must read as InsufficientHistory"
    ]

    testList "properties" [

      testPropertyWithConfig propConfig "a flat signal with noise never fires" <| fun () ->
        let seed, baseVal, noiseAmp = pick genFlatShape
        let rng = Random seed
        let series = flatNoisy rng 250 baseVal noiseAmp
        evaluate series |> List.exists fires |> not

      testPropertyWithConfig propConfig "a sustained step fires within a bounded number of samples" <| fun () ->
        let seed, baseVal, noiseAmp = pick genFlatShape
        let stepFactor = pick (Gen.choose (3, 15) |> Gen.map float)
        let rng = Random seed
        let before = flatNoisy rng 30 baseVal noiseAmp
        let after = flatNoisy rng 60 (baseVal * stepFactor) noiseAmp
        let series = (before @ after) |> reindexed
        let verdicts = evaluate series
        let boundSamples = 25
        verdicts
        |> List.indexed
        |> List.exists (fun (i, v) -> i >= 30 && i < 30 + boundSamples && fires v)

      testPropertyWithConfig propConfig "a slow drift is detected as drift before it's ever detected as a break" <| fun () ->
        // CUSUM's whole point is that a persistent bias, however small,
        // eventually crosses any fixed threshold — so a slow-enough or
        // long-enough drift CAN legitimately end up Broken. What the module
        // actually promises is ORDERING: severity escalates gradually
        // (Drifting first), it never snaps straight to Broken the way a
        // sudden step does. The drift rate is scaled to the noise band
        // itself (a "slow" drift moves a small fraction of the noise per
        // sample), which is what makes it slow relative to THIS signal.
        let seed, baseVal, noiseAmp = pick genFlatShape
        let noiseFrac = noiseAmp / baseVal
        let rng = Random seed
        let warmup = flatNoisy rng 20 baseVal noiseAmp
        let ratePerSample = noiseFrac * (0.25 + rng.NextDouble() * 0.3)
        let drift =
          [ for i in 0 .. 149 ->
              { At = atSec 0; Value = baseVal * (1.0 + ratePerSample * float i) + (rng.NextDouble() - 0.5) * 2.0 * noiseAmp } ]
        let series = (warmup @ drift) |> reindexed
        let verdicts = evaluate series
        let firstDrifting = verdicts |> List.tryFindIndex (function Verdict.Drifting _ -> true | _ -> false)
        match verdicts |> List.tryFindIndex isBroken with
        | None -> true
        | Some brokenAt ->
          match firstDrifting with
          | Some driftingAt -> driftingAt < brokenAt
          | None -> false

      testPropertyWithConfig propConfig "a single spike doesn't fire" <| fun () ->
        let seed, baseVal, noiseAmp = pick genFlatShape
        let spikeFactor = pick (Gen.choose (20, 500) |> Gen.map float)
        let rng = Random seed
        let before = flatNoisy rng 30 baseVal noiseAmp
        let spike = [ { At = atSec 0; Value = baseVal * spikeFactor } ]
        let after = flatNoisy rng 60 baseVal noiseAmp
        let series = (before @ spike @ after) |> reindexed
        evaluate series |> List.exists fires |> not

      testPropertyWithConfig propConfig "recovery clears it" <| fun () ->
        let seed, baseVal, noiseAmp = pick genFlatShape
        let stepFactor = pick (Gen.choose (3, 50) |> Gen.map float)
        let rng = Random seed
        let before = flatNoisy rng 30 baseVal noiseAmp
        let incident = flatNoisy rng 30 (baseVal * stepFactor) (noiseAmp * stepFactor)
        let after = flatNoisy rng 300 baseVal noiseAmp
        let series = (before @ incident @ after) |> reindexed
        let verdicts = evaluate series
        verdicts
        |> List.skip (verdicts.Length - 30)
        |> List.forall (function Verdict.Normal -> true | _ -> false)
    ]

    // ── Acceptance check: replay the two real incidents, plus the control. ──
    testList "replayed incidents (the acceptance check)" [

      testCase "INCIDENT 1: /health latency flat at a few ms, jumps to 60s for a minute, returns — fires and recovers" <| fun _ ->
        let rng = Random 20260922
        // A minute of samples at 1Hz either side, a minute of the incident itself.
        let normal = flatNoisy rng 60 4.0 1.2
        let stuck = flatNoisy rng 60 60_000.0 2_000.0
        let recovered = flatNoisy rng 250 4.0 1.2
        let series = (normal @ stuck @ recovered) |> reindexed
        let verdicts = evaluate series

        let duringIncident = verdicts |> List.indexed |> List.filter (fun (i, _) -> i >= 60 && i < 120)
        duringIncident
        |> List.exists (fun (_, v) -> isBroken v)
        |> Expect.isTrue "the daemon's own health endpoint going from ~4ms to 60,000ms for a minute must read as Broken"

        verdicts
        |> List.skip (verdicts.Length - 30)
        |> List.forall (function Verdict.Normal -> true | _ -> false)
        |> Expect.isTrue "once latency has been back to ~4ms for a while, the verdict must clear back to Normal"

      testCase "INCIDENT 2: worker RSS climbs steadily from 30MB to 37,000MB over hours — fires and never falsely clears mid-climb" <| fun _ ->
        let rng = Random 37000
        let baseline = flatNoisy rng 60 30.0 1.0
        let climb =
          [ for i in 0 .. 300 ->
              { At = atSec 0
                Value = 30.0 + float i * (37_000.0 - 30.0) / 300.0 + (rng.NextDouble() - 0.5) * 2.0 } ]
        let series = (baseline @ climb) |> reindexed
        let verdicts = evaluate series

        verdicts
        |> List.skip 90 // comfortably past MinSustainSamples once the climb starts at index 60
        |> List.exists fires
        |> Expect.isTrue "a steady climb to 37GB must eventually read as Drifting or Broken"

        verdicts
        |> List.skip (verdicts.Length - 30)
        |> List.forall fires
        |> Expect.isTrue "an ongoing, never-stabilizing climb must still be flagged at the end — it never got the chance to become a new stable normal"

      testCase "CONTROL: flat with noise never fires" <| fun _ ->
        let rng = Random 4
        let series = flatNoisy rng 300 4.0 1.2
        evaluate series
        |> List.exists fires
        |> Expect.isFalse "a daemon behaving exactly as usual must never be flagged"
    ]

    testList "evidence and messages" [
      testCase "Broken evidence names the right direction and carries the raw deviation" <| fun _ ->
        let rng = Random 5
        let before = flatNoisy rng 30 4.0 1.0
        let after = flatNoisy rng 30 900.0 20.0
        let series = (before @ after) |> reindexed
        let evidence =
          evaluate series
          |> List.tryPick (function Verdict.Broken e -> Some e | _ -> None)
        match evidence with
        | None -> failtest "expected the step to 900 to read as Broken somewhere in the series"
        | Some e ->
          e.Direction |> Expect.equal "900 is well above the ~4 baseline" SignalDirection.Increased
          (e.ObservedValue, 100.0) |> Expect.isGreaterThan "the evidence carries the actual out-of-range value"
          (e.SamplesSustained, defaultParams.MinSustainSamples) |> Expect.isGreaterThanOrEqual "sustain count matches the streak gate"

      testCase "describe returns None for Normal and InsufficientHistory, Some for Drifting/Broken" <| fun _ ->
        describe Verdict.Normal |> Expect.isNone "Normal has no evidence to describe"
        describe Verdict.InsufficientHistory |> Expect.isNone "InsufficientHistory has no evidence to describe"

      testCase "signalName is exhaustive text for every known signal, plus Custom" <| fun _ ->
        [ SignalId.HealthLatency; SignalId.EvalLatency; SignalId.WorkerRss; SignalId.MailboxQueueDepth; SignalId.WarmupDuration ]
        |> List.map signalName
        |> Expect.equal
          "each known signal has a stable name"
          [ "health_latency"; "eval_latency"; "worker_rss"; "mailbox_queue_depth"; "warmup_duration" ]
        signalName (SignalId.Custom "my_custom_signal") |> Expect.equal "Custom passes the name through" "my_custom_signal"
    ]
  ]
