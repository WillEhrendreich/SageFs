module SageFs.Tests.RespawnAfterReapSimTests

open Expecto
open Expecto.Flip
open SageFs.Simulation
open SageFs.Simulation.RespawnAfterReapSim

// DST for #179. `SageFs.Simulation/RespawnAfterReapSim.fs` folds the real `RespawnAfterReap.verdict` and
// `WorkerReaper` over seeded orders in which the old workers leave, with a Stop landing anywhere, against a modeled
// port. The twins are the defects the invariants exist to catch: each must break an invariant on the seeds that can
// reach it, and the counts below keep "no violations" from being a green run over an empty space.

let private simConfig = { FsCheckConfig.defaultConfig with maxTest = 500 }

let private atLeast (what: string) (minimum: int) (actual: int) : unit =
  (actual >= minimum) |> Expect.isTrue (sprintf "%s: %d seeds, wanted at least %d" what actual minimum)

let private seeds = [ 0 .. 499 ]

let private hasSurvivor (scenario: Scenario) : bool =
  scenario.Events |> List.exists (function Event.WorkerLeft (_, true) -> true | Event.WorkerLeft _ | Event.Stop -> false)

/// Scenarios whose first event is an old worker leaving, so no Stop can drop the parked replacement before it starts.
let private leavesFirst (scenario: Scenario) : bool =
  match scenario.Events with
  | Event.WorkerLeft _ :: _ -> true
  | Event.Stop :: _ | [] -> false

let private stopsBeforeLastLeave (scenario: Scenario) : bool =
  let lastLeave = scenario.Events |> List.findIndexBack (function Event.WorkerLeft _ -> true | Event.Stop -> false)
  match scenario.Events |> List.tryFindIndex (function Event.Stop -> true | Event.WorkerLeft _ -> false) with
  | Some stopAt -> stopAt < lastLeave
  | None -> false

let private violations (trace: Trace) = RespawnAfterReapInvariants.violations trace

let private idsOf (trace: Trace) : string list = violations trace |> List.map fst

let private mutantRuns (strategy: Strategy) (keep: Scenario -> bool) : (Scenario * Trace) list =
  seeds
  |> List.map RespawnAfterReapGenerators.fromSeed
  |> List.filter keep
  |> List.map (fun scenario -> scenario, runWith strategy scenario)

[<Tests>]
let tests =
  testList "DST respawn after reap (#179)" [

    testList "the real order holds the invariants" [

      testPropertyWithConfig simConfig "seeded schedules: bind-succeeds, no-spawn-while-an-old-worker-lives, one-replacement-per-reset and no-profile-keeps-spawn-first all hold"
        <| fun (seed: int) ->
          let trace = run (RespawnAfterReapGenerators.fromSeed seed)
          match violations trace with
          | [] -> ()
          | broken -> failtestf "INVARIANT VIOLATION — replay this scenario:\n  Seed=%d\n  Scenario=%A\n  Violations=%A" seed trace.Scenario broken

      testPropertyWithConfig simConfig "same seed, identical trace" <| fun (seed: int) ->
        run (RespawnAfterReapGenerators.fromSeed seed) = run (RespawnAfterReapGenerators.fromSeed seed)

      testCase "the seed space reaches every case the invariants speak about" <| fun _ ->
        let scenarios = seeds |> List.map RespawnAfterReapGenerators.fromSeed
        let count keep = scenarios |> List.filter keep |> List.length
        count (fun s -> s.HasProfile && not (hasSurvivor s)) |> atLeast "profile sessions with clean exits" 150
        count (fun s -> s.HasProfile && s.OldWorkers.Length = 2 && leavesFirst s && not (hasSurvivor s)) |> atLeast "two old workers, no Stop first" 40
        count (fun s -> s.HasProfile && stopsBeforeLastLeave s) |> atLeast "a Stop before the last worker leaves" 20
        count hasSurvivor |> atLeast "a worker that outlives its kill" 20
        count (fun s -> not s.HasProfile) |> atLeast "sessions with no profile" 60

      testCase "a worked schedule: two old workers, the second reported last, then the replacement starts and binds" <| fun _ ->
        let scenario : Scenario =
          { Seed = -1
            HasProfile = true
            OldWorkers = [ 100; 101 ]
            Events = [ Event.WorkerLeft (101, false); Event.WorkerLeft (100, false) ] }
        let trace = run scenario
        trace.Spawns |> Expect.equal "one spawn, after both reports, with the port free" [ { AfterEvents = 2; Holders = []; Bound = true } ]
        violations trace |> Expect.isEmpty "nothing is violated"
    ]

    testList "the twins break them, so the invariants have teeth" [

      testCase "twin-spawn-first (the order before the fix) loses the bind on every profile session whose workers exit cleanly" <| fun _ ->
        let runs = mutantRuns twinSpawnFirst (fun s -> s.HasProfile && not (hasSurvivor s))
        runs.Length |> atLeast "enough seeds to mean something" 150
        for scenario, trace in runs do
          idsOf trace |> Expect.contains (sprintf "seed %d loses the bind" scenario.Seed) "bind-succeeds"

      testCase "twin-spawn-first still agrees with the real order for a session with no profile" <| fun _ ->
        let runs = mutantRuns twinSpawnFirst (fun s -> not s.HasProfile)
        for scenario, trace in runs do
          trace |> Expect.equal (sprintf "seed %d: same spawns as the real order" scenario.Seed) { run scenario with Reducer = trace.Reducer }

      testCase "twin-first-leave starts the replacement while the second old worker still holds the port" <| fun _ ->
        let runs = mutantRuns twinFirstLeave (fun s -> s.HasProfile && s.OldWorkers.Length = 2 && leavesFirst s && not (hasSurvivor s))
        runs.Length |> atLeast "enough seeds to mean something" 40
        for scenario, trace in runs do
          idsOf trace |> Expect.contains (sprintf "seed %d starts early" scenario.Seed) "no-spawn-while-an-old-worker-lives"
          idsOf trace |> Expect.contains (sprintf "seed %d loses the bind" scenario.Seed) "bind-succeeds"

      testCase "twin-first-leave is indistinguishable from the real order when there is one old worker, which is the mutant's whole reach" <| fun _ ->
        let runs = mutantRuns twinFirstLeave (fun s -> s.HasProfile && s.OldWorkers.Length = 1 && not (hasSurvivor s))
        runs.Length |> atLeast "enough seeds to mean something" 40
        for scenario, trace in runs do
          violations trace |> Expect.isEmpty (sprintf "seed %d is clean" scenario.Seed)

      testCase "twin-ignores-stop starts a replacement for a session that was stopped" <| fun _ ->
        let runs = mutantRuns twinIgnoresStop (fun s -> s.HasProfile && stopsBeforeLastLeave s)
        runs.Length |> atLeast "enough seeds to mean something" 20
        for scenario, trace in runs do
          idsOf trace |> Expect.contains (sprintf "seed %d starts after the stop" scenario.Seed) "one-replacement-per-reset"
    ]
  ]
