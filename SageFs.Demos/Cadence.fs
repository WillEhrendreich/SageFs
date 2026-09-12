/// Pure per-character typing cadence (demo-gif-plan.md §4.3, §5). Seeded
/// log-normal delays (median 55 ms, longer after `.`/`,`/newline, a 120 ms
/// "think" before an identifier), deterministic by seed.
module SageFs.Demos.Cadence

open SageFs.Demos.Domain

/// The `(Key, Delay)` sequence for typing `text`, seeded so the cadence is
/// identical every run and different per scenario.
let keys (text: Text) (seed: Seed) : (Key * Delay) list =
  failwith "TODO: Cadence — Wave 2"
