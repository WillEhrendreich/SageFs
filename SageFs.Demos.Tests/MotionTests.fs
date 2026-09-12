/// Property tests for `Motion.path` (demo-gif-plan.md §4.3, §9): monotone
/// time, exact endpoints, staying on the (720p) virtual screen with a small
/// margin for the seeded wobble/overshoot, deterministic by seed, and the
/// duration clamp.
module SageFs.Demos.Tests.MotionTests

open Expecto
open Expecto.Flip
open SageFs.Demos.Domain

/// The virtual screen the plan records onto (§4.5).
let private screenW = 1280
let private screenH = 720

/// Covers the seeded ±2px wobble and the small overshoot on long moves
/// (§4.3), plus rounding — generous on purpose so this is a real "did the
/// path go somewhere absurd" check, not a hair-trigger.
let private margin = 20

/// Folds an arbitrary int into the virtual screen's coordinate range so
/// generated points always describe a plausible on-screen move.
let private boundTo (maxExclusive: int) (v: int) : int =
  ((abs v) % maxExclusive)

let private mkPoint (rawX: int) (rawY: int) : Point =
  { X = boundTo screenW rawX; Y = boundTo screenH rawY }

let private mkSeed (raw: int) : Seed = Seed(uint64 (abs (int64 raw)))

[<Tests>]
let tests =
  testList "Motion" [

    testProperty "frames are monotone non-decreasing in time"
    <| fun (sx: int) (sy: int) (fx: int) (fy: int) (seedRaw: int) ->
      let start = mkPoint sx sy
      let finish = mkPoint fx fy
      let frames = SageFs.Demos.Motion.path start finish (mkSeed seedRaw)
      frames |> List.pairwise |> List.forall (fun (a, b) -> b.TMs >= a.TMs)

    testProperty "starts and ends exactly on the endpoints"
    <| fun (sx: int) (sy: int) (fx: int) (fy: int) (seedRaw: int) ->
      let start = mkPoint sx sy
      let finish = mkPoint fx fy
      let frames = SageFs.Demos.Motion.path start finish (mkSeed seedRaw)
      (List.head frames).At = start
      && (List.last frames).At = finish
      && (List.head frames).TMs = 0

    testProperty "never leaves the (margined) virtual screen"
    <| fun (sx: int) (sy: int) (fx: int) (fy: int) (seedRaw: int) ->
      let start = mkPoint sx sy
      let finish = mkPoint fx fy
      let frames = SageFs.Demos.Motion.path start finish (mkSeed seedRaw)
      frames
      |> List.forall (fun f ->
        f.At.X >= -margin
        && f.At.X <= screenW + margin
        && f.At.Y >= -margin
        && f.At.Y <= screenH + margin)

    testProperty "deterministic by seed"
    <| fun (sx: int) (sy: int) (fx: int) (fy: int) (seedRaw: int) ->
      let start = mkPoint sx sy
      let finish = mkPoint fx fy
      let seed = mkSeed seedRaw
      let a = SageFs.Demos.Motion.path start finish seed
      let b = SageFs.Demos.Motion.path start finish seed
      a = b

    testCase "duration is clamped to 250..650ms by distance" <| fun _ ->
      let short = SageFs.Demos.Motion.path { X = 0; Y = 0 } { X = 1; Y = 0 } (Seed 1UL)
      let long = SageFs.Demos.Motion.path { X = 0; Y = 0 } { X = 1280; Y = 720 } (Seed 1UL)
      let shortDuration = (List.last short).TMs
      let longDuration = (List.last long).TMs
      (shortDuration, 250) |> Expect.isGreaterThanOrEqual "short move duration >= 250ms"
      (shortDuration, 650) |> Expect.isLessThanOrEqual "short move duration <= 650ms"
      (longDuration, 250) |> Expect.isGreaterThanOrEqual "long move duration >= 250ms"
      (longDuration, 650) |> Expect.isLessThanOrEqual "long move duration <= 650ms"
      (longDuration, shortDuration) |> Expect.isGreaterThan "a long move takes longer than a tiny move"

    testCase "a zero-distance move still produces a valid path at the point" <| fun _ ->
      let point = { X = 400; Y = 300 }
      let frames = SageFs.Demos.Motion.path point point (Seed 42UL)
      (List.head frames).At |> Expect.equal "starts at the point" point
      (List.last frames).At |> Expect.equal "ends at the point" point
  ]
