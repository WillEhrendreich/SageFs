module SageFs.Tests.OperatorStoreSimTests

/// DST over how operator.token comes to exist: three daemons on one data directory, seeded interleavings and
/// crashes, folded through the REAL OperatorFile.next. Each twin breaks one guarantee in the WORLD, and the
/// invariant that guards it must catch it, or the invariant would pass for nothing.

open Expecto
open Expecto.Flip
open SageFs
open SageFs.OperatorFile
open SageFs.Simulation.OperatorStoreSim
open SageFs.Simulation.OperatorStoreSimInvariants

let seeds : int list = [ 1 .. 400 ]

let violatedBy (behavior: Behavior) (invariant: Invariant) (seed: int) : bool =
  match invariant.Check(trace behavior (scenarioOf seed)) with
  | Outcome.Violated _ -> true
  | Outcome.Holds -> false

let someSeedBreaks (behavior: Behavior) (invariant: Invariant) : unit =
  seeds
  |> List.exists (violatedBy behavior invariant)
  |> Expect.isTrue (sprintf "some seeded scenario must make the %A twin violate %s" behavior invariant.Id)

[<Tests>]
let tests =
  testList "OperatorStoreSim" [

    test "WHY - the real machine keeps every invariant on every seed" {
      for seed in seeds do
        let states = trace Behavior.Real (scenarioOf seed)
        for invariant in all do
          match invariant.Check states with
          | Outcome.Holds -> ()
          | Outcome.Violated message -> failtestf "%s must hold for the real machine, seed %d: %s" invariant.Id seed message
    }

    test "WHY - the same seed gives the same trace" {
      for seed in [ 1; 7; 99; 400 ] do
        trace Behavior.Real (scenarioOf seed) |> Expect.equal (sprintf "seed %d replays" seed) (trace Behavior.Real (scenarioOf seed))
    }

    test "WHY - the seeds are not vacuous: daemons really lose races, and a crash really leaves a temporary file" {
      let losses =
        seeds |> List.filter (fun seed ->
          trace Behavior.Real (scenarioOf seed)
          |> List.exists (fun s -> s.Daemons |> Map.exists (fun _ d -> match fst d.Machine with State.LostRace _ -> true | _ -> false)))
      losses |> List.isEmpty |> Expect.isFalse "some seed has a daemon at LostRace"
      let litter =
        seeds |> List.filter (fun seed ->
          let last = trace Behavior.Real (scenarioOf seed) |> List.last
          last.Directory.Temps |> Map.exists (fun d _ -> (Map.find d last.Daemons).Crashed))
      litter |> List.isEmpty |> Expect.isFalse "some seed has a crashed daemon's temporary file left behind"
      let allThree =
        seeds |> List.filter (fun seed ->
          let last = trace Behavior.Real (scenarioOf seed) |> List.last
          last.Daemons |> Map.forall (fun _ d -> d.Started && not d.Crashed))
      allThree |> List.isEmpty |> Expect.isFalse "some seed has all three daemons running to the end"
    }

    test "WHY - a publish that replaces the target leaves a daemon holding a credential the file no longer has" {
      someSeedBreaks Behavior.OverwriteRaceTwin everyHolderAgreesWithTheFile
    }

    test "WHY - a write into the target itself lets a daemon see half a credential" {
      someSeedBreaks Behavior.TornWriteTwin targetNeverTorn
      someSeedBreaks Behavior.TornWriteTwin noDaemonReadsATornFile
    }

    test "WHY - each twin breaks only its own guarantee: the overwrite twin never tears the file" {
      for seed in seeds do
        violatedBy Behavior.OverwriteRaceTwin targetNeverTorn seed |> Expect.isFalse (sprintf "seed %d: overwriting is atomic, so the file is never torn" seed)
    }
  ]
