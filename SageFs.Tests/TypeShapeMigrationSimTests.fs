module SageFs.Tests.TypeShapeMigrationSimTests

/// WHY — the migration's claim is "migrate only what is decidable, refuse
/// everything else". These tests fold the real decision over seeded shape
/// combinations and check BOTH directions, because a rule that always refuses
/// would satisfy the safety invariant while shipping nothing.

open Expecto
open Expecto.Flip
open SageFs.Simulation.TypeShapeMigrationSim
open SageFs.Simulation.TypeShapeMigrationInvariants

let private seeds = [ 1 .. 2000 ]

let private battery = lazy (seeds |> List.map (fun seed -> run (scenarioOf seed)))

let private report label (violations: Violation list) =
  if not (List.isEmpty violations) then
    let first = List.head violations
    failtestf "%s: seed %d — %s (replay: TypeShapeMigrationSim.run (scenarioOf %d))" label first.Seed first.Why first.Seed

[<Tests>]
let tests =
  testList "Type-shape migration DST" [

    testCase "SAFETY — no migration ever claims a carry over something undecidable" <| fun _ ->
      battery.Value
      |> List.iter (fun trace -> trace |> neverClaimsAnUndecidableCarry |> report "claimed an undecidable carry")

    testCase "SAFETY — a fully decidable shape does migrate, so the rule is not just 'always refuse'" <| fun _ ->
      battery.Value
      |> List.iter (fun trace -> trace |> decidableShapesMigrate |> report "refused a decidable shape")

    testCase "REPLAY — the same seed always produces the same decision" <| fun _ ->
      (run (scenarioOf 4242)).Observation
      |> Expect.equal "a seeded scenario must be deterministic" (run (scenarioOf 4242)).Observation

    testCase "NON-VACUOUS — the battery reaches both migrating and refusing decisions" <| fun _ ->
      let migrated = battery.Value |> List.filter (fun t -> t.Observation.Migrated)
      let refused = battery.Value |> List.filter (fun t -> not t.Observation.Migrated)

      (migrated.Length, 0)
      |> Expect.isGreaterThan "the battery must actually migrate, or the rule is vacuous"

      (refused.Length, 0)
      |> Expect.isGreaterThan "the battery must actually refuse, or the safety rule is vacuous"

    testCase "TWIN — assuming every new field is defaulted must break the safety rule" <| fun _ ->
      let breaking = seeds |> List.filter (fun seed -> assumeDefaultsBreaksTheRule (scenarioOf seed))

      (breaking.Length, 0)
      |> Expect.isGreaterThan "the assume-defaults twin must break the invariant on some seed"
  ]
