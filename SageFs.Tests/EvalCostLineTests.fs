/// The cost line of docs/plans/eval-cost-line.md: a slow or allocation-heavy eval ends its reply with one line
/// carrying the time, the bytes allocated and the GC counts. A quick, light eval carries nothing.
module SageFs.Tests.EvalCostLineTests

open System
open Expecto
open Expecto.Flip
open FsCheck
open SageFs

let private thresholds = EvalCost.productThresholds

let private costOf (elapsedMs: int64) (allocated: int64) (gen: PositiveInt) : EvalCost =
  { Elapsed = TimeSpan.FromMilliseconds(float elapsedMs)
    Allocated = allocated
    Gen0 = gen.Get % 1000
    Gen1 = gen.Get % 100
    Gen2 = gen.Get % 10 }

/// A cost under both thresholds, from a seed.
let private lightCost (seed: PositiveInt) : EvalCost =
  let slowMs = int64 thresholds.Slow.TotalMilliseconds
  costOf (int64 seed.Get % slowMs) (int64 seed.Get * 1_013L % thresholds.HeavyAllocation) seed

/// A cost at or over one threshold, from a seed. Even seeds are over the time threshold only, odd seeds over the allocation threshold only.
let private notableCost (seed: PositiveInt) (extra: PositiveInt) : EvalCost =
  let slowMs = int64 thresholds.Slow.TotalMilliseconds
  match seed.Get % 2 with
  | 0 -> costOf (slowMs + int64 extra.Get % 120_000L) (int64 seed.Get * 1_013L % thresholds.HeavyAllocation) seed
  | _ -> costOf (int64 seed.Get % slowMs) (thresholds.HeavyAllocation + int64 extra.Get * 4_099L) seed

[<Tests>]
let evalCostLineTests =
  testList "Eval cost line" [

    testProperty "WHY - an eval under both thresholds carries no cost line" <| fun (seed: PositiveInt) ->
      EvalCost.line thresholds (lightCost seed) = None

    testCase "WHY - an eval exactly at a threshold is annotated, because the threshold is inclusive" <| fun _ ->
      let atTime = { EvalCost.unmeasured with Elapsed = thresholds.Slow }
      let atBytes = { EvalCost.unmeasured with Allocated = thresholds.HeavyAllocation }
      EvalCost.line thresholds atTime |> Expect.isSome "a time at the threshold gets a line"
      EvalCost.line thresholds atBytes |> Expect.isSome "an allocation at the threshold gets a line"

    testProperty "WHY - an eval at or over either threshold carries one line with all five figures" <| fun (seed: PositiveInt) (extra: PositiveInt) ->
      let cost = notableCost seed extra
      match EvalCost.line thresholds cost |> Option.bind EvalCost.tryParseLine with
      | None -> false
      | Some back ->
        let elapsedOff = abs (back.Elapsed - cost.Elapsed).TotalMilliseconds
        let sameCounts = back.Gen0 = cost.Gen0 && back.Gen1 = cost.Gen1 && back.Gen2 = cost.Gen2
        // The line prints the time to the hundredth of a second and the bytes to a tenth of their unit, so the figures come back to that precision.
        sameCounts && elapsedOff <= 5.0 + 1e-6

    testProperty "WHY - the line read back and written again is the same line, so the reader and the writer cannot drift" <| fun (seed: PositiveInt) (extra: PositiveInt) ->
      let cost = notableCost seed extra
      let first = EvalCost.line thresholds cost
      let second = first |> Option.bind EvalCost.tryParseLine |> Option.bind (EvalCost.line thresholds)
      first.IsSome && first = second

    testCase "WHY - the line has the shape FsiMcp 0.3.0 prints, so an agent that knows one knows the other" <| fun _ ->
      let cost = { Elapsed = TimeSpan.FromSeconds 0.84; Allocated = 312_400_000L; Gen0 = 14; Gen1 = 3; Gen2 = 1 }
      EvalCost.line thresholds cost
      |> Expect.equal "the shape" (Some "[0.84 s · 312.4 MB allocated · GCs gen0 14 / gen1 3 / gen2 1]")

    testCase "WHY - a reading that went backwards is a zero, never a negative figure" <| fun _ ->
      let cost = EvalCost.measured (TimeSpan.FromSeconds -1.0) (500L, 100L) (5, 3) (2, 2) (9, 1)
      cost |> Expect.equal "all zero" EvalCost.unmeasured
  ]
