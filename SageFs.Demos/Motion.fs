/// Pure cursor-motion planning (demo-gif-plan.md §4.3, §5). Property tests to
/// write in Wave 2: monotone time, starts/ends exactly on the endpoints,
/// never leaves the screen, deterministic by seed.
module SageFs.Demos.Motion

open SageFs.Demos.Domain

/// §9: "Motion 250–650 ms minimum-jerk".
[<Literal>]
let private MinDurationMs = 250

[<Literal>]
let private MaxDurationMs = 650

/// Distance (px) beyond which the duration clamp is fully saturated at
/// `MaxDurationMs`; chosen so a full-screen-width move (§4.5: 1280x720)
/// reads as "long".
[<Literal>]
let private LongestPlannedDistance = 800.0

/// Distance (px) beyond which a move gets the small overshoot-then-settle
/// (§4.3).
[<Literal>]
let private LongMoveDistance = 300.0

/// Peak lateral wobble amplitude, in pixels (§4.3: "seeded ±2 px wobble").
[<Literal>]
let private WobbleAmplitudePx = 2.0

/// Peak overshoot distance beyond the eased path, in pixels, for a long move.
[<Literal>]
let private OvershootPx = 6.0

/// How densely the path is sampled. Independent of capture fps (§4.5's 15
/// fps is a recording concern) — this just needs to be fine enough for a
/// smooth-looking overlay and short enough that `duration / this` never
/// collapses to too few frames at the 250ms floor.
[<Literal>]
let private FrameIntervalMs = 20

/// The classic minimum-jerk scalar profile: s(0)=0, s(1)=1, with zero
/// velocity and acceleration at both ends.
let private minimumJerk (t: float) : float =
  10.0 * (t ** 3.0) - 15.0 * (t ** 4.0) + 6.0 * (t ** 5.0)

/// Public so `Ffmpeg.fs`'s compositing can recompute the SAME real,
/// distance-based motion duration `Input.fs` actually used to deliver a
/// click's motion — the fix for the cursor "lagging"/looking inhuman: the
/// compositor used to spread a click's cursor animation across the WHOLE
/// recorded step (which can be 90s of warmup-waiting), instead of the ~250–
/// 650ms a real mouse move actually takes, so the synthetic cursor crawled
/// for the entire wait and the ripple fired near the very end of it instead
/// of the instant the click actually happened.
let durationForDistance (distance: float) : int =
  let fraction = min 1.0 (distance / LongestPlannedDistance)
  let raw = float MinDurationMs + fraction * float (MaxDurationMs - MinDurationMs)
  raw |> max (float MinDurationMs) |> min (float MaxDurationMs) |> int

/// A minimum-jerk cursor path from `start` to `finish`, with a seeded ±2 px
/// lateral wobble and a small overshoot-then-settle on long moves;
/// `duration = clamp 250..650 ms` by distance (§4.3).
let path (start: Point) (finish: Point) (seed: Seed) : PointerFrame list =
  // Compute in float space throughout so extreme int32 endpoints never wrap
  // around before being scaled down by easing.
  let dx = float finish.X - float start.X
  let dy = float finish.Y - float start.Y
  let distance = sqrt (dx * dx + dy * dy)
  let durationMs = durationForDistance distance

  // The wobble/overshoot source is derived only from the seed, so the same
  // seed always produces the same jitter regardless of start/finish.
  let (Seed seedValue) = seed
  let rng = System.Random(int (uint32 seedValue))

  let frameCount = max 8 (durationMs / FrameIntervalMs)

  // Unit vector perpendicular to the direction of travel, for lateral
  // wobble; undefined (and unused) for a zero-distance move.
  let perpX, perpY = if distance < 1e-6 then 0.0, 0.0 else -dy / distance, dx / distance
  let dirX, dirY = if distance < 1e-6 then 0.0, 0.0 else dx / distance, dy / distance

  [ for i in 0..frameCount ->
      let t = float i / float frameCount
      let eased = minimumJerk t
      let baseX = float start.X + dx * eased
      let baseY = float start.Y + dy * eased

      // Zero at t=0 and t=1 so the endpoints stay exact no matter the
      // wobble/overshoot magnitude.
      let envelope = sin (System.Math.PI * t)

      let wobble = (rng.NextDouble() * 2.0 - 1.0) * WobbleAmplitudePx * envelope

      let overshoot = if distance > LongMoveDistance then OvershootPx * envelope * envelope * t else 0.0

      let x = baseX + perpX * wobble + dirX * overshoot
      let y = baseY + perpY * wobble + dirY * overshoot

      let point =
        if i = 0 then start
        elif i = frameCount then finish
        else { X = int (round x); Y = int (round y) }

      { At = point; TMs = int (round (t * float durationMs)) } ]
