/// Pure cost-classed, memory-bound scheduling (demo-gif-plan.md §4.2, §5).
/// Packs cells greedily by longest duration first, never exceeding either
/// `cores - 1` or `memory - 2 GB`, whichever binds first.
module SageFs.Demos.Schedule

open SageFs.Demos.Domain

/// Every cell shares this same private Xvfb display name — safe because
/// each cell gets its own private `/tmp` (§4.1), so no two cells' `:99`
/// collide.
let private sharedDisplay = Display ":99"

/// TODO(shape): the doctor resolves the real `Isolation` once for the whole
/// run (§4.1) — a placeholder here until Wave 2 threads that resolved value
/// through cell construction. The scheduler's job is packing, not choosing
/// isolation.
let private placeholderIsolation = Isolation.ScratchEnv { EnvVars = [] }

let private toCell (scenario: Scenario) : Cell =
  { Id = scenario.Id
    Isolation = placeholderIsolation
    Display = sharedDisplay }

/// Whether `candidate` fits alongside `waveScenarios` without pushing the
/// wave's total cores or memory over budget.
let private fits (coreBudget: int) (memoryBudget: float) (waveScenarios: Scenario list) (candidate: Scenario) =
  let totalCpu = candidate.Cost.Cpu + (waveScenarios |> List.sumBy (fun s -> s.Cost.Cpu))
  let totalMem = candidate.Cost.MemoryGb + (waveScenarios |> List.sumBy (fun s -> s.Cost.MemoryGb))
  totalCpu <= coreBudget && totalMem <= memoryBudget

/// Packs `scenarios` into waves that fit within `resources`' core/memory
/// budget: greedy first-fit-decreasing — longest `Duration` first, each
/// scenario placed into the first existing wave it fits in, else a new
/// wave. A scenario whose own cost alone exceeds a budget is still
/// scheduled, alone, in its own over-budget wave: every scenario must be
/// scheduled exactly once, and a lone oversized scenario has nowhere else
/// to go.
let plan (resources: Resources) (scenarios: Scenario list) : Wave list =
  let coreBudget = resources.Cores - 1
  let memoryBudget = resources.MemoryGb - 2.0
  let sorted = scenarios |> List.sortByDescending (fun s -> s.Cost.DurationSeconds)
  let waves =
    (([]: Scenario list list), sorted)
    ||> List.fold (fun waves scenario ->
      match waves |> List.tryFindIndex (fun wave -> fits coreBudget memoryBudget wave scenario) with
      | Some i -> waves |> List.mapi (fun j wave -> if j = i then wave @ [ scenario ] else wave)
      | None -> waves @ [ [ scenario ] ])
  waves |> List.map (List.map toCell)
