/// Pure cursor-motion planning (demo-gif-plan.md §4.3, §5). Property tests to
/// write in Wave 2: monotone time, starts/ends exactly on the endpoints,
/// never leaves the screen, deterministic by seed.
module SageFs.Demos.Motion

open SageFs.Demos.Domain

/// A minimum-jerk cursor path from `start` to `finish`, with a seeded ±2 px
/// lateral wobble and a small overshoot-then-settle on long moves;
/// `duration = clamp 250..650 ms` by distance (§4.3).
let path (start: Point) (finish: Point) (seed: Seed) : PointerFrame list =
  failwith "TODO: Motion — Wave 2"
