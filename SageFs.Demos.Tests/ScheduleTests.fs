/// RED-then-GREEN tests for the pure wave scheduler (demo-gif-plan.md §4.2):
/// packs scenarios greedily by longest duration first, never exceeding
/// either `cores - 1` or `memory - 2 GB` — whichever binds first. Generated
/// scenarios draw their ids from the pool `ScenarioId.derive` can actually
/// produce, never a made-up string, so a scheduler bug involving id
/// collisions (the same combo minted twice) is reachable by the generator.
module SageFs.Demos.Tests.ScheduleTests

open Expecto
open Expecto.Flip
open FsCheck
open FsCheck.FSharp
open SageFs.Demos.Domain
open SageFs.Demos.Schedule

let private propConfig = { FsCheckConfig.defaultConfig with maxTest = 100 }

/// Every id `ScenarioId.derive` can mint: 5 capabilities × 3 clients × 4 app
/// kinds collapses to 24 distinct values, because every non-`HotReload`
/// capability's id ignores `AppKind` entirely (Domain.fs `ScenarioId.derive`).
let private allScenarioIds : ScenarioId list =
  [ for capability in [ Capability.HotReload; Capability.LiveTesting; Capability.Repl; Capability.Sessions; Capability.Agent ] do
      for client in Client.all do
        for appKind in [ AppKind.Web; AppKind.Raylib; AppKind.Console; AppKind.NoApp ] ->
          ScenarioId.derive capability client appKind ]
  |> List.distinct

/// A minimal, valid `Scenario` carrying only the two fields the scheduler
/// actually reads (`Id`, `Cost`) — the rest are fixed placeholders so this
/// stays a pure fixture, not a second copy of the §6.1 worked example.
let private mkScenario (id: ScenarioId) (cost: CostClass) : Scenario =
  { Id = id
    Capability = Capability.Repl
    Client = Client.Dashboard
    App = AppKind.NoApp
    Sample = Sample.ConsoleTicker
    Layout = LayoutTemplate.DashboardOnly
    Steps = []
    Cost = cost
    Masks = [] }

let private genCostClass : Gen<CostClass> =
  gen {
    let! cpu = Gen.choose (1, 4)
    let! memTenths = Gen.choose (1, 80)
    let! durTenths = Gen.choose (1, 600)
    return
      { Cpu = cpu
        MemoryGb = float memTenths / 10.0
        DurationSeconds = float durTenths / 10.0 }
  }

/// A scenario list with distinct ids (a random-size subset of
/// `allScenarioIds`, never inventing more than exist) and independently
/// random costs.
let private genScenarios : Gen<Scenario list> =
  gen {
    let! n = Gen.choose (1, List.length allScenarioIds)
    let! shuffled = Gen.shuffle (List.toArray allScenarioIds)
    let ids = shuffled |> Array.toList |> List.truncate n
    let! costs = Gen.listOfLength n genCostClass
    return List.map2 mkScenario ids costs
  }

let private genResources : Gen<Resources> =
  gen {
    let! cores = Gen.choose (2, 17)
    let! memTenths = Gen.choose (21, 650)
    return { Cores = cores; MemoryGb = float memTenths / 10.0; Gl = Gl.SoftwareGl }
  }

type private ScheduleGenerators =
  static member Scenarios() = Arb.fromGen genScenarios
  static member Resources() = Arb.fromGen genResources

let private scheduleConfig = { propConfig with arbitrary = [ typeof<ScheduleGenerators> ] }

let private costById (scenarios: Scenario list) : Map<ScenarioId, CostClass> =
  scenarios |> List.map (fun s -> s.Id, s.Cost) |> Map.ofList

[<Tests>]
let tests =
  testList "Schedule" [

    testPropertyWithConfig
      scheduleConfig
      "no wave exceeds either the core or the memory budget, unless it holds a single unavoidably-oversized scenario" <|
      fun (resources: Resources) (scenarios: Scenario list) ->
        let costs = costById scenarios
        let coreBudget = resources.Cores - 1
        let memoryBudget = resources.MemoryGb - 2.0
        let waves = plan resources scenarios
        let allWavesOk =
          waves
          |> List.forall (fun wave ->
            let waveCosts = wave |> List.map (fun cell -> costs.[cell.Id])
            let totalCpu = waveCosts |> List.sumBy (fun c -> c.Cpu)
            let totalMem = waveCosts |> List.sumBy (fun c -> c.MemoryGb)
            (totalCpu <= coreBudget && totalMem <= memoryBudget) || List.length wave = 1)
        allWavesOk |> Expect.isTrue "every wave stays within budget, or is a single scenario that cannot be split further"

    testPropertyWithConfig scheduleConfig "every scenario is scheduled exactly once" <|
      fun (resources: Resources) (scenarios: Scenario list) ->
        let scheduledIds =
          plan resources scenarios
          |> List.collect (List.map (fun cell -> cell.Id))
          |> List.sortBy ScenarioId.value
        let expectedIds = scenarios |> List.map (fun s -> s.Id) |> List.sortBy ScenarioId.value
        scheduledIds |> Expect.equal "the scheduled ids are exactly the input ids, once each" expectedIds

    testCase "waves never exceed ceil(total/capacity) for uniform-cost scenarios" <| fun _ ->
      let cost = { Cpu = 1; MemoryGb = 1.0; DurationSeconds = 10.0 }
      let scenarios = allScenarioIds |> List.truncate 10 |> List.map (fun id -> mkScenario id cost)
      let resources = { Cores = 5; MemoryGb = 10.0; Gl = Gl.SoftwareGl }
      // coreBudget = 4, memoryBudget = 8.0 => capacity = min(4, 8) = 4 per wave.
      let capacity = 4
      let expectedMaxWaves = int (ceil (float scenarios.Length / float capacity))
      let waves = plan resources scenarios
      (waves.Length, expectedMaxWaves)
      |> Expect.isLessThanOrEqual "waves stay within the theoretical minimum for uniform-cost scenarios"

    testCase "longest duration is scheduled first when the budget forces one scenario per wave" <| fun _ ->
      let short = mkScenario allScenarioIds.[0] { Cpu = 1; MemoryGb = 1.0; DurationSeconds = 10.0 }
      let medium = mkScenario allScenarioIds.[1] { Cpu = 1; MemoryGb = 1.0; DurationSeconds = 20.0 }
      let long = mkScenario allScenarioIds.[2] { Cpu = 1; MemoryGb = 1.0; DurationSeconds = 30.0 }
      // coreBudget = 1 (Cores=2 - 1) forces exactly one scenario per wave, so
      // wave order reveals the greedy longest-duration-first pack order.
      let resources = { Cores = 2; MemoryGb = 10.0; Gl = Gl.SoftwareGl }
      let waves = plan resources [ short; medium; long ]
      let waveIds = waves |> List.map (List.map (fun cell -> cell.Id))
      waveIds
      |> Expect.equal
        "greedy longest-duration-first packs long, then medium, then short, one per wave"
        [ [ long.Id ]; [ medium.Id ]; [ short.Id ] ]

    testCase "an empty scenario list schedules zero waves" <| fun _ ->
      let resources = { Cores = 8; MemoryGb = 16.0; Gl = Gl.SoftwareGl }
      plan resources [] |> Expect.equal "no scenarios, no waves" []
  ]
