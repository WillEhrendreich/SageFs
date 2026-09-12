/// Pure cost-classed, memory-bound scheduling (demo-gif-plan.md §4.2, §5).
/// Packs cells greedily by longest duration first, never exceeding either
/// `cores - 1` or `memory - 2 GB`, whichever binds first. Wave 2 property
/// tests: never over either budget; every scenario scheduled exactly once;
/// waves ≤ ceil(total/budget).
module SageFs.Demos.Schedule

open SageFs.Demos.Domain

/// Packs `scenarios` into waves that fit within `resources`' core/memory
/// budget, one wave recorded at a time, cells within a wave in parallel.
let plan (resources: Resources) (scenarios: Scenario list) : Wave list =
  failwith "TODO: Schedule — Wave 2"
